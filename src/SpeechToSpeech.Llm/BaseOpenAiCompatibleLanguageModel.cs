using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;
using SpeechToSpeech.Llm.Prompts;
using SpeechToSpeech.Llm.ToolCall;

namespace SpeechToSpeech.Llm;

/// <summary>
/// Normalised provider events. Each backend's stream/response is mapped to this small vocabulary so
/// the shared speech-pipeline logic (sentence batching, cancellation, history, token usage) lives in
/// one place; subclasses differ only in how they produce these events.
/// </summary>
public abstract record ProviderEvent
{
    /// <summary>Incremental assistant text. Always raw; the base applies unspeechable filtering.</summary>
    public sealed record TextDelta(string Text) : ProviderEvent;

    /// <summary>A complete assistant turn to write back to history.</summary>
    public sealed record AssistantMessage(IReadOnlyList<ContentPart> Content) : ProviderEvent;

    /// <summary>A complete function tool call, with call id and id already generated.</summary>
    public sealed record ToolCall(FunctionToolCall Item) : ProviderEvent;

    /// <summary>Token accounting for the turn.</summary>
    public sealed record Usage(int InputTokens, int OutputTokens) : ProviderEvent;
}

/// <summary>Per-request context threaded through generation; immutable for the turn.</summary>
public sealed record LanguageModelTurn(
    string? LanguageCode,
    uint? Generation,
    RuntimeConfig RuntimeConfig,
    ResponseCreateParams? Response,
    string? TurnId,
    int? TurnRevision,
    double? SpeechStoppedAtSeconds,
    bool WantsAudio);

/// <summary>Per-request tool settings, resolved for the attempt that is about to be made.</summary>
/// <param name="Prompted">
/// Describe the tools in the system prompt and read calls back out of the model's own output,
/// instead of sending native tool definitions.
/// </param>
public sealed record ToolRequest(
    IReadOnlyList<FunctionToolDefinition>? Tools,
    string? ToolChoice,
    bool Prompted,
    bool WantsAudio)
{
    public bool HasTools => Tools is { Count: > 0 };
}

/// <summary>Options shared by the OpenAI-compatible language model handlers.</summary>
public sealed class LanguageModelHandlerOptions
{
    public string ModelName { get; set; } = "gpt-4o-mini";

    public bool Stream { get; set; } = true;

    public int StreamBatchSentences { get; set; } = 3;

    /// <summary>Sentences to accumulate before the <em>first</em> chunk of a response is emitted.</summary>
    /// <remarks>
    /// The first chunk sets time-to-first-audio, and it pays twice: the handler waits for the
    /// sentences, then TTS synthesizes all of them before the first block comes out. Later chunks can
    /// afford <see cref="StreamBatchSentences"/> because audio is already playing over them.
    /// </remarks>
    public int StreamFirstBatchSentences { get; set; } = 1;

    public bool EnableLanguagePrompt { get; set; }

    public bool CompactHistory { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Shared lifecycle for OpenAI-compatible LLM backends (Responses and Chat Completions).
/// </summary>
/// <remarks>
/// Subclasses implement <see cref="WarmupAsync"/>, <see cref="BuildCompactionGenerateFn"/>,
/// <see cref="RequestEventsAsync"/> and <see cref="BuildRequestPayload"/>, and inherit the
/// request/response orchestration: speculative-turn gating, cancellation, sentence batching,
/// text-only versus audio handling, history write-back, token usage, out-of-band handling and error
/// termination.
/// </remarks>
public abstract class BaseOpenAiCompatibleLanguageModel : BaseHandler<GenerateResponseRequest, PipelineMessage>
{
    /// <summary>
    /// Error text sent to the client when generation fails.
    /// </summary>
    /// <remarks>
    /// Deliberately opaque. <c>EndOfResponse.Error</c> is surfaced over the realtime connection, and
    /// the provider exception message can carry the base URL, host names and occasionally an API key
    /// fragment. The exception itself is logged server-side with a stack trace, which is where an
    /// operator should be looking anyway.
    /// </remarks>
    private const string GenerationFailedMessage =
        "Language model generation failed. See the server log for details.";

    /// <summary>Mutable accumulators collected while consuming a turn's events.</summary>
    private sealed class GenerationState
    {
        public List<FunctionToolCall> Tools { get; } = [];

        public List<ConversationItem> Pending { get; } = [];

        /// <summary>Filtered text, kept only for the debug log.</summary>
        public string CleanText { get; set; } = string.Empty;

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }
    }

    private readonly Chat.CompactFn? _compactor;

    /// <summary>The probe's verdict for this endpoint and model.</summary>
    /// <remarks>
    /// Sticky for the life of the handler: whether a server implements <c>tools</c> is a property of
    /// the server, so re-deciding it every turn would be pure latency.
    /// </remarks>
    private ToolSupport _toolSupport;

    private readonly ToolSupportCache _toolSupportCache = ToolSupportCache.Shared;

    protected BaseOpenAiCompatibleLanguageModel(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        LanguageModelHandlerOptions options,
        CancelScope? cancelScope = null,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger? logger = null)
        : base(stopSource, queueIn, queueOut, logger)
    {
        Options = options;
        CancelScope = cancelScope;
        SpeculativeTurns = speculativeTurns;
        _compactor = options.CompactHistory ? CompactionPrompt.BuildCompactor(BuildCompactionGenerateFn(), Logger) : null;
    }

    protected LanguageModelHandlerOptions Options { get; }

    protected SpeculativeTurnTracker? SpeculativeTurns { get; }

    /// <summary>Issues a cheap request so the model and connection are ready before serving.</summary>
    public abstract Task WarmupAsync();

    /// <summary>Returns a (system, user) to text function used to compact long histories.</summary>
    protected abstract CompactGenerateFn BuildCompactionGenerateFn();

    /// <summary>Serialises the chat plus per-request tool settings into the backend's payload.</summary>
    protected abstract object? BuildRequestPayload(Chat activeChat, ToolRequest toolRequest);

    /// <summary>
    /// Issues the request and maps the response to normalised provider events.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="Task{TResult}"/> of a stream rather than a bare stream so the request and
    /// its status check happen eagerly, at the await. An async iterator would defer both to the first
    /// <c>MoveNextAsync</c>, by which point the turn is already committed and a failure can only be
    /// reported as a spoken error.
    /// </remarks>
    protected abstract Task<IAsyncEnumerable<ProviderEvent>> RequestEventsAsync(
        object payload,
        CancellationToken cancellationToken);

    public override async IAsyncEnumerable<PipelineMessage> ProcessAsync(
        GenerateResponseRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runtimeConfig = request.RuntimeConfig;
        var response = request.Response;

        if (!TurnIsLatest(request.TurnId, request.TurnRevision))
        {
            Logger.LogInformation(
                "Skipping stale LLM request for turn={TurnId} rev={Revision}",
                request.TurnId,
                request.TurnRevision);
            yield return new EndOfResponse { TurnId = request.TurnId, TurnRevision = request.TurnRevision };
            yield break;
        }

        var originalChat = runtimeConfig.Chat;
        Chat activeChat;
        string? rejection = null;

        if (ResponseSemantics.IsOutOfBand(response))
        {
            try
            {
                activeChat = ChatFactory.BuildActiveChat(originalChat, response);
            }
            catch (ChatItemException exception)
            {
                Logger.LogInformation("Out-of-band response rejected: {Message}", exception.Message);
                rejection = exception.Message;
                activeChat = originalChat.Copy();
            }
        }
        else
        {
            activeChat = originalChat.Copy();
        }

        if (rejection is not null)
        {
            yield return new EndOfResponse
            {
                TurnId = request.TurnId,
                TurnRevision = request.TurnRevision,
                Error = rejection,
            };
            yield break;
        }

        var session = runtimeConfig.Session;
        var instructions = (!string.IsNullOrEmpty(response?.Instructions)
            ? response!.Instructions
            : session.Instructions) ?? string.Empty;
        var tools = response?.Tools is { Count: > 0 } ? response.Tools : session.Tools;
        var toolChoice = !string.IsNullOrEmpty(response?.ToolChoice) ? response!.ToolChoice : session.ToolChoice;
        var wantsAudio = ResponseSemantics.WantsAudio(response);

        var (languageCode, languageName) = LlmUtils.ResolveAutoLanguage(request.LanguageCode);

        ApplyConfig(activeChat, instructions, wantsAudio, Options.EnableLanguagePrompt ? languageName : null);

        // CancelScope.IsStale(generation) is only observed when the stream iterator advances; a
        // blocked network read cannot be aborted by the websocket router, so the request timeout is
        // the backstop.
        var generation = CancelScope?.Generation;

        var turn = new LanguageModelTurn(
            languageCode,
            generation,
            runtimeConfig,
            response,
            request.TurnId,
            request.TurnRevision,
            request.SpeechStoppedAtSeconds,
            wantsAudio);

        await foreach (var output in Generate(activeChat, originalChat, turn, tools, toolChoice)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return output;
        }
    }

    protected override bool ShouldLogTiming(PipelineMessage output) =>
        output is LlmResponseChunk && LastTime > MinTimeToDebugSeconds;

    protected override LogLevel TimingLogLevel => LogLevel.Information;

    // ── speculative-turn / cancellation gating ────────────────────────────────

    private bool TurnIsLatest(string? turnId, int? turnRevision) =>
        SpeculativeTurns is null || SpeculativeTurns.IsLatest(turnId, turnRevision);

    private bool GenerationIsStale(uint? generation) =>
        generation is { } value && CancelScope is not null && CancelScope.IsStale(value);

    private bool TurnOutputAllowed(string? turnId, int? turnRevision) =>
        SpeculativeTurns is null || SpeculativeTurns.IsLatestAfterReopenGrace(turnId, turnRevision);

    // ── tool-calling mode ─────────────────────────────────────────────────────

    /// <summary>
    /// Settles native-versus-prompted tool calling once per endpoint and model, with a throwaway
    /// request instead of by failing a real turn.
    /// </summary>
    /// <remarks>
    /// An inconclusive probe (server not up yet, connection reset) resolves to prompted for this turn
    /// and is not cached. Prompted works against every server, so the turn still completes and the
    /// next one probes again; guessing native instead would spend a user-visible turn discovering the
    /// answer the hard way.
    /// </remarks>
    private async Task<bool> UsePromptedToolsAsync(ToolRequest toolRequest)
    {
        if (!toolRequest.HasTools)
        {
            return false;
        }

        if (_toolSupport != ToolSupport.Unknown)
        {
            return _toolSupport == ToolSupport.Prompted;
        }

        var key = ToolSupportCache.KeyFor(ProbeEndpointForCache, Options.ModelName);

        var cached = _toolSupportCache.Get(key);
        if (cached != ToolSupport.Unknown)
        {
            _toolSupport = cached;
            Logger.LogDebug("{Handler}: tool support for {Key} read from cache: {Support}", Name, key, cached);
            return cached == ToolSupport.Prompted;
        }

        ToolSupport probed;
        try
        {
            probed = await ProbeToolSupportAsync(StopToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A probe must never be able to take down a turn.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Logger.LogDebug(exception, "{Handler}: tool-support probe was inconclusive", Name);
            probed = ToolSupport.Unknown;
        }

        if (probed == ToolSupport.Unknown)
        {
            return true;
        }

        Logger.LogInformation("{Handler}: tool support for {Key} probed as {Support}", Name, key, probed);
        _toolSupport = probed;
        _toolSupportCache.Set(key, probed);
        return probed == ToolSupport.Prompted;
    }

    /// <summary>Endpoint identity used as the cache key. Defaults to the model name alone.</summary>
    protected virtual string? ProbeEndpointForCache => null;

    /// <summary>
    /// Issues the smallest request that can decide whether the provider honours a <c>tools</c> array.
    /// Return <see cref="ToolSupport.Unknown"/> when the answer cannot be established.
    /// </summary>
    protected virtual Task<ToolSupport> ProbeToolSupportAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ToolSupport.Unknown);

    /// <summary>
    /// In prompted mode, rewrites the provider's events so the rest of the pipeline cannot tell the
    /// difference from native tool calling: call expressions are lifted out of the assistant text
    /// and re-emitted as <see cref="ProviderEvent.ToolCall"/>.
    /// </summary>
    private IAsyncEnumerable<ProviderEvent> ApplyPromptedTools(
        IAsyncEnumerable<ProviderEvent> events,
        ToolRequest toolRequest) =>
        toolRequest.Prompted && toolRequest.HasTools ? LiftPromptedToolCalls(events, toolRequest) : events;

    private async IAsyncEnumerable<ProviderEvent> LiftPromptedToolCalls(
        IAsyncEnumerable<ProviderEvent> events,
        ToolRequest toolRequest)
    {
        var gate = new ToolBlockGate(ToolPrompt.EnterCode, ToolPrompt.EndCode);
        var tools = toolRequest.Tools!.Select(FunctionTool.FromDefinition).ToList();
        var blockRegex = ToolPrompt.BuildBlockRegex();
        var parsed = new List<ParsedFunctionCall>();
        var blocks = new List<string>();

        await foreach (var providerEvent in events.ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case ProviderEvent.TextDelta delta:
                    blocks.Clear();
                    var speakable = gate.Feed(delta.Text, blocks);
                    foreach (var block in blocks)
                    {
                        parsed.AddRange(FunctionCallParser.ParseMultipleFunctions([block]));
                    }

                    if (speakable.Length > 0)
                    {
                        yield return new ProviderEvent.TextDelta(speakable);
                    }

                    break;

                case ProviderEvent.AssistantMessage message:
                    // History must not carry the tags back to the model: it already gets the call as
                    // a function_call item, and replaying the raw block teaches it to repeat itself.
                    yield return new ProviderEvent.AssistantMessage(
                        [.. message.Content.Select(part => StripToolBlocks(part, blockRegex))]);
                    break;

                default:
                    yield return providerEvent;
                    break;
            }
        }

        if (gate.HasUnclosedBlock)
        {
            Logger.LogWarning("{Handler}: discarding an unterminated tool-call block", Name);
        }

        var tail = gate.Flush();
        if (tail.Length > 0)
        {
            yield return new ProviderEvent.TextDelta(tail);
        }

        // Deferred to the end so the assistant text is already in history when the call is recorded,
        // which is the ordering the native path produces.
        foreach (var call in parsed)
        {
            FunctionToolCall item;
            try
            {
                item = call.ToRealtimeToolCall(tools, Logger);
            }
            catch (InvalidOperationException exception)
            {
                Logger.LogWarning(exception, "{Handler}: ignoring an invalid tool call from the model", Name);
                continue;
            }

            yield return new ProviderEvent.ToolCall(item);
        }
    }

    private static ContentPart StripToolBlocks(ContentPart part, string blockRegex)
    {
        if (part.Text is null)
        {
            return part;
        }

        var (outside, _) = FunctionCallParser.ExtractFunctionCallsFromText(part.Text, blockRegex);
        var stripped = part.Clone();
        stripped.Text = outside.Trim();
        return stripped;
    }

    /// <summary>Installs the system prompt for the turn, optionally pinning the reply language.</summary>
    /// <remarks>
    /// The language instruction belongs here rather than in a trailing user message: appended after
    /// the transcript it becomes the newest thing addressed to the model, and small models answer it
    /// ("Understood, I will reply in English.") instead of answering the user.
    /// </remarks>
    private static void ApplyConfig(Chat chat, string? instructions, bool wantsAudio, string? replyLanguage)
    {
        if (string.IsNullOrEmpty(instructions) && replyLanguage is null)
        {
            return;
        }

        var sessionPrompt = instructions ?? string.Empty;
        var full = wantsAudio ? VoicePrompt.Build(sessionPrompt) : TextPrompt.Build(sessionPrompt);

        if (replyLanguage is not null)
        {
            full = $"{full.TrimEnd()}\n- Always reply in {replyLanguage}.\n";
        }

        chat.AddItem(ChatMessages.MakeSystemMessage(full));
    }

    private static LlmResponseChunk Chunk(
        LanguageModelTurn turn,
        string text = "",
        IReadOnlyList<FunctionToolCall>? tools = null,
        string? languageCode = null) => new()
        {
            Text = text,
            LanguageCode = languageCode ?? turn.LanguageCode,
            Tools = tools ?? [],
            RuntimeConfig = turn.RuntimeConfig,
            Response = turn.Response,
            TurnId = turn.TurnId,
            TurnRevision = turn.TurnRevision,
            SpeechStoppedAtSeconds = turn.SpeechStoppedAtSeconds,
            CancelGeneration = turn.Generation,
        };

    /// <summary>
    /// Emits a tool call, persisting it (and any assistant text seen so far) to history before it is
    /// forwarded to the client.
    /// </summary>
    /// <remarks>
    /// The function call must already exist in the conversation by the time the client returns its
    /// function_call_output; otherwise a fast client races ahead of the deferred end-of-turn
    /// write-back and the output is rejected, which makes the model re-issue the same tool call. The
    /// call lands in the chat's pending tool calls (not serialized until its output pairs it), so
    /// eager recording is safe. Out-of-band turns never touch the default conversation, and a stale
    /// turn records nothing because it is not forwarded to the client either.
    /// </remarks>
    private IEnumerable<PipelineMessage> RecordToolCall(
        GenerationState state,
        LanguageModelTurn turn,
        FunctionToolCall item)
    {
        state.Tools.Add(item);

        if (GenerationIsStale(turn.Generation) || !TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
        {
            Logger.LogInformation("LLM generation cancelled (stale speculative turn)");
            yield break;
        }

        if (!ResponseSemantics.IsOutOfBand(turn.Response))
        {
            // Flush assistant text accumulated before this call first (so history order matches what
            // the client received), then persist the call — all before the chunk leaves.
            var chat = turn.RuntimeConfig.Chat;
            foreach (var pending in state.Pending)
            {
                chat.AddItem(pending);
            }

            state.Pending.Clear();
            chat.AddItem(new ConversationItem
            {
                Id = item.Id,
                ItemType = ConversationItemType.FunctionCall,
                Name = item.Name,
                Arguments = item.Arguments,
                CallId = item.CallId,
            });
        }

        yield return Chunk(turn, tools: [item]);
    }

    private async IAsyncEnumerable<PipelineMessage> ConsumeStreaming(
        IAsyncEnumerable<ProviderEvent> events,
        GenerationState state,
        LanguageModelTurn turn)
    {
        var cancelled = false;
        var printableText = string.Empty;
        var sentenceBatch = new List<string>();
        var spoken = false;

        await foreach (var providerEvent in events.ConfigureAwait(false))
        {
            if (GenerationIsStale(turn.Generation) || !TurnIsLatest(turn.TurnId, turn.TurnRevision))
            {
                Logger.LogInformation("LLM generation cancelled (interruption)");
                cancelled = true;
                break;
            }

            switch (providerEvent)
            {
                case ProviderEvent.Usage usage:
                    state.InputTokens = usage.InputTokens;
                    state.OutputTokens = usage.OutputTokens;
                    break;

                case ProviderEvent.AssistantMessage message:
                    state.Pending.Add(new ConversationItem
                    {
                        ItemType = ConversationItemType.Message,
                        Role = ConversationRole.Assistant,
                        Content = [.. message.Content],
                    });
                    break;

                case ProviderEvent.ToolCall toolCall:
                    // Flush any pending spoken text before emitting the tool call.
                    if (printableText.Trim().Length > 0)
                    {
                        sentenceBatch.Add(printableText.Trim());
                        printableText = string.Empty;
                    }

                    if (sentenceBatch.Count > 0)
                    {
                        if (!TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
                        {
                            Logger.LogInformation("LLM generation cancelled (stale speculative turn)");
                            cancelled = true;
                            break;
                        }

                        yield return Chunk(turn, string.Join(" ", sentenceBatch));
                        sentenceBatch = [];
                    }

                    foreach (var output in RecordToolCall(state, turn, toolCall.Item))
                    {
                        yield return output;
                    }

                    break;

                case ProviderEvent.TextDelta delta:
                    Metrics?.Mark(turn.TurnId, $"{Name}/first-text");
                    if (!turn.WantsAudio)
                    {
                        // Text-only: forward verbatim. Keep every character (no unspeechable
                        // filtering, which strips TTS-unfriendly symbols) and don't sentence-split,
                        // which would collapse newlines and markdown.
                        state.CleanText += delta.Text;
                        if (delta.Text.Length > 0)
                        {
                            if (!TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
                            {
                                Logger.LogInformation("LLM generation cancelled (stale speculative turn)");
                                cancelled = true;
                                break;
                            }

                            yield return Chunk(turn, delta.Text);
                        }

                        break;
                    }

                    var newText = LlmUtils.RemoveUnspeechable(delta.Text);
                    state.CleanText += newText;
                    printableText += newText;

                    var sentences = SentenceTokenizer.Split(printableText);
                    if (sentences.Count > 1)
                    {
                        var batchSize = spoken ? Options.StreamBatchSentences : Options.StreamFirstBatchSentences;
                        foreach (var sentence in sentences[..^1])
                        {
                            sentenceBatch.Add(sentence);
                            if (sentenceBatch.Count < batchSize)
                            {
                                continue;
                            }

                            if (!TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
                            {
                                Logger.LogInformation("LLM generation cancelled (stale speculative turn)");
                                cancelled = true;
                                break;
                            }

                            yield return Chunk(turn, string.Join(" ", sentenceBatch));
                            sentenceBatch = [];
                            spoken = true;
                            batchSize = Options.StreamBatchSentences;
                        }

                        if (cancelled)
                        {
                            break;
                        }

                        printableText = sentences[^1];
                    }

                    break;
            }

            if (cancelled)
            {
                break;
            }
        }

        if (cancelled)
        {
            yield break;
        }

        if (printableText.Trim().Length > 0)
        {
            sentenceBatch.Add(printableText.Trim());
        }

        if (sentenceBatch.Count > 0)
        {
            if (GenerationIsStale(turn.Generation))
            {
                Logger.LogInformation("LLM generation cancelled (interruption)");
            }
            else if (TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
            {
                Logger.LogDebug("Clean text: {Text}", state.CleanText);
                yield return Chunk(turn, string.Join(" ", sentenceBatch));
            }
        }

        Logger.LogInformation("Tools: {Tools}", string.Join(", ", state.Tools.Select(tool => tool.Name)));
    }

    private async IAsyncEnumerable<PipelineMessage> ConsumeNonStreaming(
        IAsyncEnumerable<ProviderEvent> events,
        GenerationState state,
        LanguageModelTurn turn)
    {
        if (GenerationIsStale(turn.Generation) || !TurnIsLatest(turn.TurnId, turn.TurnRevision))
        {
            Logger.LogInformation("LLM generation cancelled (interruption)");
            yield break;
        }

        await foreach (var providerEvent in events.ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case ProviderEvent.Usage usage:
                    state.InputTokens = usage.InputTokens;
                    state.OutputTokens = usage.OutputTokens;
                    break;

                case ProviderEvent.AssistantMessage message:
                    state.Pending.Add(new ConversationItem
                    {
                        ItemType = ConversationItemType.Message,
                        Role = ConversationRole.Assistant,
                        Content = [.. message.Content],
                    });
                    break;

                case ProviderEvent.ToolCall toolCall:
                    foreach (var output in RecordToolCall(state, turn, toolCall.Item))
                    {
                        yield return output;
                    }

                    break;

                case ProviderEvent.TextDelta delta:
                    // Text-only keeps every character verbatim; audio strips TTS-unfriendly symbols.
                    var spoken = turn.WantsAudio ? LlmUtils.RemoveUnspeechable(delta.Text) : delta.Text;
                    state.CleanText += spoken;

                    var output_ = turn.WantsAudio ? spoken.Trim() : spoken;
                    if (output_.Length > 0
                        && !GenerationIsStale(turn.Generation)
                        && TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
                    {
                        yield return Chunk(turn, output_);
                    }

                    break;
            }
        }

        Logger.LogDebug("Clean text: {Text}", state.CleanText);
        Logger.LogInformation("Tools: {Tools}", string.Join(", ", state.Tools.Select(tool => tool.Name)));
    }

    private async IAsyncEnumerable<PipelineMessage> Generate(
        Chat activeChat,
        Chat originalChat,
        LanguageModelTurn turn,
        IReadOnlyList<FunctionToolDefinition>? tools,
        string? toolChoice)
    {
        var state = new GenerationState();
        string? errorMessage = null;

        // Settled before the payload is built, so a prompted verdict shapes this very turn.
        var toolRequest = new ToolRequest(
            tools,
            toolChoice,
            await UsePromptedToolsAsync(new ToolRequest(tools, toolChoice, false, turn.WantsAudio))
                .ConfigureAwait(false),
            turn.WantsAudio);

        var payload = BuildRequestPayload(activeChat, toolRequest);

        // Images the model actually sees this turn; only these are stripped on write-back, so an
        // image a fast client injects mid-generation for the next turn survives.
        var consumedImageIds = activeChat.ImageMessageIds();

        if (payload is null)
        {
            // Nothing to send: empty instructions and no input (in the response, the default
            // conversation, or the out-of-band context). The provider would reject this, so fail with
            // a clear message instead of an opaque error.
            errorMessage = "Cannot generate a response: no instructions and no input were provided.";
        }

        if (errorMessage is null)
        {
            IAsyncEnumerator<PipelineMessage>? enumerator = null;
            LlmResponseChunk? timeoutNotice = null;

            try
            {
                Metrics?.Mark(turn.TurnId, $"{Name}/request-sent");
                var events = ApplyPromptedTools(
                    await RequestEventsAsync(payload!, StopToken).ConfigureAwait(false),
                    toolRequest);
                var consumed = Options.Stream
                    ? ConsumeStreaming(events, state, turn)
                    : ConsumeNonStreaming(events, state, turn);
                enumerator = consumed.GetAsyncEnumerator(StopToken);
            }
            catch (OperationCanceledException) when (IsRequestTimeout())
            {
                LogRequestTimeout();
                timeoutNotice = BuildTimeoutNotice(turn);
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a timeout: end the response quietly rather than apologising.
                Logger.LogDebug("LLM request cancelled during shutdown");
            }
            catch (Exception exception) when (GenerationIsStale(turn.Generation))
            {
                // The user barged in and the next turn's request made the server drop this stream.
                // Nothing of this response is wanted any more, so a failure notice would only talk
                // over the answer the user actually asked for.
                Logger.LogDebug(exception, "LLM request failed after being superseded by barge-in");
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "LLM generation failed; ending the current response");
                errorMessage = GenerationFailedMessage;
            }

            if (enumerator is not null)
            {
                // Emitted as produced rather than buffered. The whole point of streaming is that the
                // first sentence reaches TTS while the model is still generating; materialising the
                // sequence first made time-to-first-audio equal to full generation time.
                try
                {
                    while (true)
                    {
                        PipelineMessage current;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            current = enumerator.Current;
                        }
                        catch (OperationCanceledException) when (IsRequestTimeout())
                        {
                            LogRequestTimeout();
                            timeoutNotice = BuildTimeoutNotice(turn);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.LogDebug("LLM streaming cancelled during shutdown");
                            break;
                        }
                        catch (Exception exception) when (GenerationIsStale(turn.Generation))
                        {
                            Logger.LogDebug(exception, "LLM stream failed after being superseded by barge-in");
                            break;
                        }
                        catch (Exception exception)
                        {
                            // Any other generation failure must still terminate the response: record
                            // the error and fall through to the EndOfResponse below. Otherwise the
                            // response never closes and every subsequent one is blocked.
                            Logger.LogError(exception, "LLM generation failed; ending the current response");
                            errorMessage = GenerationFailedMessage;
                            break;
                        }

                        yield return current;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (timeoutNotice is not null)
            {
                yield return timeoutNotice;
            }
        }

        if (errorMessage is null
            && !GenerationIsStale(turn.Generation)
            && TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
        {
            // Out-of-band responses emit output and usage but never write back to the default
            // conversation, since their context was a throwaway chat.
            if (!ResponseSemantics.IsOutOfBand(turn.Response))
            {
                // Tool calls (and any assistant text preceding them) were already written eagerly in
                // RecordToolCall; only trailing items remain.
                foreach (var item in state.Pending)
                {
                    originalChat.AddItem(item);
                }

                originalChat.StripImages(consumedImageIds);
                originalChat.TrimIfNeeded(_compactor);
            }

            if (state.InputTokens != 0 || state.OutputTokens != 0)
            {
                yield return new TokenUsage
                {
                    InputTokens = state.InputTokens,
                    OutputTokens = state.OutputTokens,
                    TurnId = turn.TurnId,
                    TurnRevision = turn.TurnRevision,
                };
            }
        }

        yield return new EndOfResponse
        {
            TurnId = turn.TurnId,
            TurnRevision = turn.TurnRevision,
            CancelGeneration = turn.Generation,
            Error = errorMessage,
        };
    }

    /// <summary>
    /// Whether an <see cref="OperationCanceledException"/> came from the per-request timeout rather
    /// than pipeline shutdown.
    /// </summary>
    /// <remarks>
    /// <c>HttpClient</c> surfaces its own timeout as <see cref="TaskCanceledException"/>, which is
    /// indistinguishable by type from a cooperative stop. The pipeline's stop token is the reliable
    /// discriminator: if it has not been signalled, nobody asked us to shut down and the cancellation
    /// must have come from the request deadline. Conflating the two made Ctrl+C emit a spoken
    /// "could you repeat that?" on the way out.
    /// </remarks>
    private bool IsRequestTimeout() => !StopToken.IsCancellationRequested;

    private void LogRequestTimeout() =>
        Logger.LogWarning(
            "LLM request timed out after {Timeout:F1}s; ending the current response",
            Options.RequestTimeout.TotalSeconds);

    /// <summary>The canned apology spoken on timeout. Carries no language code.</summary>
    private LlmResponseChunk? BuildTimeoutNotice(LanguageModelTurn turn)
    {
        if (GenerationIsStale(turn.Generation) || !TurnOutputAllowed(turn.TurnId, turn.TurnRevision))
        {
            return null;
        }

        return new LlmResponseChunk
        {
            Text = "Wow I'm a bit slow today, could you repeat that?",
            RuntimeConfig = turn.RuntimeConfig,
            Response = turn.Response,
            TurnId = turn.TurnId,
            TurnRevision = turn.TurnRevision,
            SpeechStoppedAtSeconds = turn.SpeechStoppedAtSeconds,
            CancelGeneration = turn.Generation,
        };
    }
}
