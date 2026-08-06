using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;
using SpeechToSpeech.Llm.ChatClientBackend;
using SpeechToSpeech.Llm.Prompts;
using SpeechToSpeech.Llm.ToolCall;

namespace SpeechToSpeech.Llm;

/// <summary>
/// LLM handler that talks to an OpenAI-compatible server through
/// <c>Microsoft.Extensions.AI</c>'s <see cref="IChatClient"/> over the official OpenAI SDK.
/// </summary>
/// <remarks>
/// <para>
/// SSE parsing, tool-call delta accumulation and message serialisation come from the maintained
/// library implementations, while every behaviour the base class owns stays put: speculative turn
/// gating, sentence batching, history write-back, prompted-tool fallback and the capability probe.
/// </para>
/// <para>
/// Tools are declared, not invoked. The realtime host executes them and feeds results back as
/// conversation items, so <c>UseFunctionInvocation()</c> and <c>AIFunctionFactory.Create</c> are
/// deliberately not used — a <see cref="FunctionCallContent"/> is translated straight into a
/// <see cref="ProviderEvent.ToolCall"/> and leaves the pipeline.
/// </para>
/// </remarks>
public sealed class ChatClientLanguageModel : BaseOpenAiCompatibleLanguageModel
{
    /// <summary>About 18–24 seconds of backoff before warmup gives up.</summary>
    private const int WarmupMaxRetries = 6;

    private readonly IChatClient _client;
    private readonly string _endpoint;

    public ChatClientLanguageModel(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        LanguageModelHandlerOptions options,
        string? baseUrl = null,
        string? apiKey = null,
        bool disableThinking = true,
        string? reasoningEffort = null,
        CancelScope? cancelScope = null,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger<ChatClientLanguageModel>? logger = null)
        : base(stopSource, queueIn, queueOut, options, cancelScope, speculativeTurns, logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = baseUrl ?? "https://api.openai.com/v1";
        _endpoint = root.TrimEnd('/') + "/chat/completions";

        var clientOptions = new OpenAI.OpenAIClientOptions { Endpoint = new Uri(root) };

        if (ExtraBodyPolicy.Build(baseUrl, disableThinking, reasoningEffort) is { } extraBody)
        {
            clientOptions.AddPolicy(new ExtraBodyPolicy(extraBody), PipelinePosition.PerCall);
        }

        // Local servers ignore the key but the SDK still requires a non-empty credential.
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-required" : apiKey);

        _client = new OpenAI.OpenAIClient(credential, clientOptions)
            .GetChatClient(options.ModelName)
            .AsIChatClient();
    }

    // ── request construction ──────────────────────────────────────────────────

    /// <summary>The messages and options for one attempt at a turn.</summary>
    private sealed record Request(List<ChatMessage> Messages, ChatOptions Options);

    protected override object? BuildRequestPayload(Chat activeChat, ToolRequest toolRequest)
    {
        ArgumentNullException.ThrowIfNull(toolRequest);

        var messages = ChatMessageMapper.ToChatMessages(activeChat);
        if (messages.Count == 0)
        {
            return null;
        }

        var chatOptions = new ChatOptions { ModelId = Options.ModelName };

        if (toolRequest.HasTools)
        {
            if (toolRequest.Prompted)
            {
                // The whole point of this mode is that the server does not understand the tools key,
                // so the declarations go into the system prompt and calls come back as plain text.
                AppendToolSection(messages, toolRequest);
            }
            else
            {
                chatOptions.Tools = [.. toolRequest.Tools!.Select(tool => (AITool)new DeclaredTool(tool))];
                chatOptions.ToolMode = ToChatToolMode(toolRequest.ToolChoice);
            }
        }

        return new Request(messages, chatOptions);
    }

    private static ChatToolMode? ToChatToolMode(string? toolChoice) => toolChoice switch
    {
        null or "" or "auto" => ChatToolMode.Auto,
        "none" => ChatToolMode.None,
        "required" or "any" => ChatToolMode.RequireAny,
        _ => ChatToolMode.RequireSpecific(toolChoice),
    };

    /// <summary>
    /// Folds the tool-calling instructions into the first system message, adding one if the chat has
    /// none. A separate trailing system message is not used because local servers commonly collapse
    /// or ignore all but the first.
    /// </summary>
    private static void AppendToolSection(List<ChatMessage> messages, ToolRequest toolRequest)
    {
        var section = ToolPrompt.BuildSystemPrompt(
            [.. toolRequest.Tools!.Select(FunctionTool.FromDefinition)],
            textOnly: !toolRequest.WantsAudio);

        if (section.Length == 0)
        {
            return;
        }

        var index = messages.FindIndex(message => message.Role == ChatRole.System);
        if (index >= 0)
        {
            messages[index] = new ChatMessage(ChatRole.System, $"{messages[index].Text}\n\n{section}");
            return;
        }

        messages.Insert(0, new ChatMessage(ChatRole.System, section));
    }

    // ── request execution ─────────────────────────────────────────────────────

    protected override async Task<IAsyncEnumerable<ProviderEvent>> RequestEventsAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        var request = (Request)payload;

        if (!Options.Stream)
        {
            var response = await _client
                .GetResponseAsync(request.Messages, request.Options, cancellationToken)
                .ConfigureAwait(false);

            return TranslateResponse(response).ToAsyncEnumerable();
        }

        var updates = _client
            .GetStreamingResponseAsync(request.Messages, request.Options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        // The first update is pulled here rather than inside the iterator so a provider rejection
        // surfaces at this await, where the prompted-tools retry can still rerun the turn before
        // anything has been spoken.
        bool hasFirst;
        try
        {
            hasFirst = await updates.MoveNextAsync().ConfigureAwait(false);
        }
        catch
        {
            await updates.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return TranslateStream(updates, hasFirst);
    }

    private static async IAsyncEnumerable<ProviderEvent> TranslateStream(
        IAsyncEnumerator<ChatResponseUpdate> updates,
        bool hasFirst)
    {
        var rawText = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();
        ProviderEvent.Usage? usage = null;

        try
        {
            for (var more = hasFirst; more; more = await updates.MoveNextAsync().ConfigureAwait(false))
            {
                foreach (var content in updates.Current.Contents)
                {
                    switch (content)
                    {
                        case Microsoft.Extensions.AI.TextContent { Text.Length: > 0 } text:
                            rawText.Append(text.Text);
                            yield return new ProviderEvent.TextDelta(text.Text);
                            break;

                        case FunctionCallContent call:
                            toolCalls.Add(call);
                            break;

                        case UsageContent usageContent:
                            usage = ToUsage(usageContent.Details);
                            break;

                        default:
                            break;
                    }
                }
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var completed in Complete(rawText.ToString(), toolCalls, usage))
        {
            yield return completed;
        }
    }

    private static List<ProviderEvent> TranslateResponse(ChatResponse response)
    {
        var rawText = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case Microsoft.Extensions.AI.TextContent { Text.Length: > 0 } text:
                        rawText.Append(text.Text);
                        break;

                    case FunctionCallContent call:
                        toolCalls.Add(call);
                        break;

                    default:
                        break;
                }
            }
        }

        var events = new List<ProviderEvent>();
        if (response.Usage is { } details)
        {
            events.Add(ToUsage(details));
        }

        // Non-streaming still emits a single delta so downstream sentence batching sees the text.
        if (rawText.Length > 0)
        {
            events.Add(new ProviderEvent.AssistantMessage([ContentPart.OutputText(rawText.ToString())]));
            events.Add(new ProviderEvent.TextDelta(rawText.ToString()));
        }

        events.AddRange(toolCalls.Select(ToToolCall));
        return events;
    }

    /// <summary>Emits the end-of-turn events in the order the base class expects.</summary>
    private static IEnumerable<ProviderEvent> Complete(
        string rawText,
        List<FunctionCallContent> toolCalls,
        ProviderEvent.Usage? usage)
    {
        if (rawText.Trim().Length > 0)
        {
            yield return new ProviderEvent.AssistantMessage([ContentPart.OutputText(rawText)]);
        }

        foreach (var call in toolCalls)
        {
            yield return ToToolCall(call);
        }

        if (usage is not null)
        {
            yield return usage;
        }
    }

    private static ProviderEvent.Usage ToUsage(UsageDetails details) => new(
        (int)(details.InputTokenCount ?? 0),
        (int)(details.OutputTokenCount ?? 0));

    private static ProviderEvent.ToolCall ToToolCall(FunctionCallContent call) =>
        new(new FunctionToolCall
        {
            Id = Ids.Generate("fc"),
            CallId = string.IsNullOrEmpty(call.CallId) ? Ids.Generate("call") : call.CallId,
            Name = call.Name,
            Arguments = SerializeArguments(call.Arguments),
        });

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is not { Count: > 0 })
        {
            return "{}";
        }

        var json = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            json[key] = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType());
        }

        return json.ToJsonString();
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────

    public override async Task WarmupAsync()
    {
        Logger.LogInformation("Warming up {Handler}", Name);
        var start = Clock.NowSeconds;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a helpful assistant"),
            new(ChatRole.User, "Hello"),
        ];

        for (var attempt = 0; attempt < WarmupMaxRetries; attempt++)
        {
            try
            {
                await _client.GetResponseAsync(messages, new ChatOptions { ModelId = Options.ModelName })
                    .ConfigureAwait(false);
                Logger.LogInformation("{Handler}: warmed up! time: {Elapsed:F3} s", Name, Clock.NowSeconds - start);
                return;
            }
            catch (Exception exception) when (attempt < WarmupMaxRetries - 1)
            {
                Logger.LogDebug(exception, "{Handler}: warmup attempt {Attempt} failed; retrying", Name, attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 0.5)).ConfigureAwait(false);
            }
        }

        Logger.LogWarning("{Handler}: warmup did not succeed; serving anyway", Name);
    }

    protected override CompactGenerateFn BuildCompactionGenerateFn() => async (system, user) =>
    {
        List<ChatMessage> messages = [new(ChatRole.System, system), new(ChatRole.User, user)];
        var response = await _client
            .GetResponseAsync(messages, new ChatOptions { ModelId = Options.ModelName })
            .ConfigureAwait(false);

        return response.Text;
    };

    protected override void OnSessionEnd() =>
        Logger.LogDebug("Chat client language model session state reset");

    // ── capability probing ────────────────────────────────────────────────────

    protected override string? ProbeEndpointForCache => _endpoint;

    /// <summary>
    /// Sends a one-token request carrying a single no-op tool. Success means the server parsed the
    /// tool declarations; a rejection in the "I do not understand this body" family means it did not.
    /// </summary>
    protected override async Task<ToolSupport> ProbeToolSupportAsync(CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            ModelId = Options.ModelName,
            MaxOutputTokens = 1,
            ToolMode = ChatToolMode.None,
            Tools = [new DeclaredTool(new FunctionToolDefinition
            {
                Name = "probe_noop",
                Description = "Capability probe. Never call this.",
            })],
        };

        try
        {
            await _client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], options, cancellationToken)
                .ConfigureAwait(false);
            return ToolSupport.Native;
        }
        catch (ClientResultException exception)
        {
            return exception.Status is (int)HttpStatusCode.BadRequest
                or (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.UnprocessableEntity
                or (int)HttpStatusCode.NotImplemented
                ? ToolSupport.Prompted
                // Connection refused, 500 or a timeout says nothing about tool support.
                : ToolSupport.Unknown;
        }
    }
}
