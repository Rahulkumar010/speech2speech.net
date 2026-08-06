using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Core.Conversation;

/// <summary>Raised when a conversation item fails validation in <see cref="Chat.AddItem"/>.</summary>
public sealed class ChatItemException(string message) : Exception(message);

/// <summary>Output of a <see cref="Chat.CompactFn"/> summarization run.</summary>
public sealed record CompactionResult(string UserSummary, string AssistantSummary);

/// <summary>
/// Manages conversation history with bounded size to avoid unbounded growth.
/// </summary>
/// <remarks>
/// The buffer stores conversation items (user messages, assistant messages, function calls,
/// function call outputs). System messages live in <see cref="InitChatMessage"/> and never enter
/// the buffer.
///
/// History bounding is decided per <see cref="TrimIfNeeded"/> call:
/// with no compactor, the oldest complete turn is evicted in place once the user-turn count
/// exceeds <see cref="Size"/>; with a compactor, older turns are summarized on a background thread
/// into a single user/assistant pair. Compaction is single-flight — additional triggers are
/// silently bypassed while one is running.
/// </remarks>
public sealed class Chat : IDisposable
{
    /// <summary>Summarizes a serialized history slice into a replacement user/assistant pair.</summary>
    public delegate Task<CompactionResult> CompactFn(IReadOnlyList<ConversationItem> history);

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Dictionary<string, ConversationItem> _pendingToolCalls = [];
    private readonly ManualResetEventSlim _shutdown = new(false);

    private List<ConversationItem> _buffer = [];
    private int _userTurnCount;
    private bool _compactInFlight;
    private int _generation;

    public Chat(int size, ILogger<Chat>? logger = null)
    {
        Size = size;
        _logger = logger ?? NullLogger<Chat>.Instance;
    }

    /// <summary>Number of user turns to keep before eviction or compaction kicks in.</summary>
    public int Size { get; }

    public ConversationItem? InitChatMessage { get; private set; }

    public IReadOnlyList<ConversationItem> Buffer
    {
        get
        {
            lock (_gate)
            {
                return [.. _buffer];
            }
        }
    }

    public int UserTurnCount
    {
        get
        {
            lock (_gate)
            {
                return _userTurnCount;
            }
        }
    }

    public void InitChat(ConversationItem message)
    {
        lock (_gate)
        {
            InitChatMessage = message;
        }
    }

    /// <summary>
    /// Validates and routes a conversation item into the chat buffer.
    /// </summary>
    /// <remarks>
    /// Does not enforce the soft size limit — call <see cref="TrimIfNeeded"/> after each successful
    /// generation. A hard upper bound at <c>2 * Size</c> is enforced inline as a runaway-client
    /// safety net.
    /// </remarks>
    public ConversationItem AddItem(ConversationItem item)
    {
        lock (_gate)
        {
            switch (item.ItemType)
            {
                case ConversationItemType.Message when item.Role == ConversationRole.System:
                    item.Id = EnsureId(item.Id, "sys");
                    InitChatMessage = item;
                    _logger.LogDebug("Set system message via conversation item");
                    break;

                case ConversationItemType.Message when item.Role == ConversationRole.User:
                    item.Id = EnsureId(item.Id, "msg");
                    item.Content = item.Content
                        .Where(p => (p.Type == "input_text" && !string.IsNullOrEmpty(p.Text))
                                    || (p.Type == "input_image" && !string.IsNullOrEmpty(p.ImageUrl)))
                        .ToList();
                    if (item.Content.Count == 0)
                    {
                        throw new ChatItemException(
                            "Message has no supported content. Supported modalities: input_text, input_image.");
                    }

                    _buffer.Add(item);
                    _userTurnCount++;
                    _logger.LogDebug("Added user message to chat ({Parts} parts)", item.Content.Count);
                    break;

                case ConversationItemType.Message when item.Role == ConversationRole.Assistant:
                    item.Id = EnsureId(item.Id, "msg");
                    item.Content = item.Content
                        .Where(p => p.Type == "output_text" && !string.IsNullOrEmpty(p.Text))
                        .ToList();
                    if (item.Content.Count == 0)
                    {
                        return item;
                    }

                    _buffer.Add(item);
                    _logger.LogDebug("Added assistant message to chat ({Parts} parts)", item.Content.Count);
                    break;

                case ConversationItemType.FunctionCall:
                    item.Id = EnsureId(item.Id, "fc");
                    item.CallId = EnsureId(item.CallId, "call");
                    _pendingToolCalls[item.CallId] = item;
                    _logger.LogDebug("Added function_call to chat (call_id={CallId})", item.CallId);
                    break;

                case ConversationItemType.FunctionCallOutput:
                    item.Id = EnsureId(item.Id, "fco");
                    AppendToolOutputLocked(
                        item.CallId ?? throw new ChatItemException("function_call_output requires a call_id."),
                        item);
                    _logger.LogDebug("Added function_call_output to chat (call_id={CallId})", item.CallId);
                    break;

                default:
                    throw new ChatItemException($"Unsupported item type: {item.ItemType}");
            }

            if (Size > 0 && _userTurnCount > 2 * Size)
            {
                _logger.LogWarning(
                    "Chat buffer exceeded hard cap ({Count} > 2 * size={Size}); evicting oldest turn",
                    _userTurnCount,
                    Size);
                while (_userTurnCount > 2 * Size)
                {
                    EvictOldestTurnLocked();
                }
            }

            return item;
        }
    }

    /// <summary>
    /// Appends a <c>function_call_output</c>, re-injecting its <c>function_call</c> if it was
    /// evicted, and marking the paired call completed.
    /// </summary>
    public void AppendToolOutput(string callId, ConversationItem outputItem)
    {
        lock (_gate)
        {
            AppendToolOutputLocked(callId, outputItem);
        }
    }

    /// <summary>Enforces the size limit after a generation completes.</summary>
    public void TrimIfNeeded(CompactFn? compactor = null)
    {
        lock (_gate)
        {
            if (_userTurnCount <= Size)
            {
                return;
            }

            if (compactor is not null)
            {
                MaybeTriggerCompactionLocked(compactor);
                return;
            }

            while (_userTurnCount > Size)
            {
                EvictOldestTurnLocked();
            }
        }
    }

    /// <summary>
    /// Replaces the text content of an existing user message. Used by speculative turn revisions:
    /// the conversation turn stays the same but the transcript is superseded by a transcription of
    /// a longer raw-audio buffer.
    /// </summary>
    public bool ReplaceUserMessageText(string itemId, string text)
    {
        lock (_gate)
        {
            foreach (var item in _buffer)
            {
                if (!item.IsMessage(ConversationRole.User) || item.Id != itemId)
                {
                    continue;
                }

                item.Content = [ContentPart.InputText(text)];
                _logger.LogDebug("Replaced speculative user message {ItemId}", itemId);
                return true;
            }
        }

        return false;
    }

    public bool RemoveUserMessage(string itemId)
    {
        lock (_gate)
        {
            for (var index = 0; index < _buffer.Count; index++)
            {
                if (!_buffer[index].IsMessage(ConversationRole.User) || _buffer[index].Id != itemId)
                {
                    continue;
                }

                _buffer.RemoveAt(index);
                _userTurnCount--;
                _logger.LogDebug("Removed speculative user message {ItemId}", itemId);
                return true;
            }
        }

        return false;
    }

    /// <summary>Serializes the system prompt plus buffer for an OpenAI-compatible provider.</summary>
    public IReadOnlyList<ConversationItem> ToProviderHistory(IReadOnlyList<ConversationItem>? items = null)
    {
        lock (_gate)
        {
            return ToProviderHistoryLocked(items ?? _buffer);
        }
    }

    /// <summary>IDs of user messages currently carrying <c>input_image</c> content.</summary>
    public HashSet<string> ImageMessageIds()
    {
        lock (_gate)
        {
            return _buffer
                .Where(item => item.IsMessage(ConversationRole.User)
                               && item.Id is not null
                               && item.Content.Any(p => p.Type == "input_image"))
                .Select(item => item.Id!)
                .ToHashSet();
        }
    }

    /// <summary>
    /// Removes image content parts from user messages so images don't persist across turns. With
    /// <paramref name="onlyIds"/>, strips only the images the just-completed response consumed,
    /// leaving intact an image a fast client injected mid-generation for the next turn.
    /// </summary>
    public void StripImages(IReadOnlySet<string>? onlyIds = null)
    {
        lock (_gate)
        {
            foreach (var item in _buffer.Where(item => item.IsMessage(ConversationRole.User)))
            {
                if (onlyIds is not null && (item.Id is null || !onlyIds.Contains(item.Id)))
                {
                    continue;
                }

                item.Content = item.Content.Where(p => p.Type != "input_image").ToList();
            }
        }
    }

    /// <summary>Returns an independent snapshot, safe to mutate without affecting this chat.</summary>
    /// <remarks>
    /// The items are deep-copied. Copying only the list left both chats pointing at the same
    /// <see cref="ConversationItem"/> instances, so an out-of-band turn that appended to an assistant
    /// message, or the image stripper clearing a <c>Content</c> list, silently edited the live
    /// conversation it was supposed to be isolated from.
    /// </remarks>
    public Chat Copy()
    {
        lock (_gate)
        {
            // Cloned once and looked up by identity, because _pendingToolCalls holds the *same*
            // instances as _buffer: AppendToolOutput mutates the pending call in place and expects
            // the change to be visible in the history. Cloning the two collections independently
            // would quietly break that link. ConversationItem does not override Equals, so the
            // default comparer is reference equality.
            var cloneByOriginal = new Dictionary<ConversationItem, ConversationItem>(_buffer.Count);

            var buffer = new List<ConversationItem>(_buffer.Count);
            foreach (var item in _buffer)
            {
                var copy = item.Clone();
                cloneByOriginal[item] = copy;
                buffer.Add(copy);
            }

            var clone = new Chat(Size)
            {
                InitChatMessage = InitChatMessage?.Clone(),
                _buffer = buffer,
                _userTurnCount = _userTurnCount,
            };

            foreach (var (callId, call) in _pendingToolCalls)
            {
                clone._pendingToolCalls[callId] =
                    cloneByOriginal.TryGetValue(call, out var copy) ? copy : call.Clone();
            }

            return clone;
        }
    }

    /// <summary>Clears all conversation state, cancelling any in-flight compaction splice.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _compactInFlight = false;
            _buffer = [];
            InitChatMessage = null;
            _pendingToolCalls.Clear();
            _userTurnCount = 0;
        }
    }

    /// <summary>
    /// Permanently shuts down the chat, suppressing an in-flight compaction splice. The compaction
    /// task is not awaited: it may be blocked in an LLM call.
    /// </summary>
    public void Close()
    {
        _shutdown.Set();
        lock (_gate)
        {
            _generation++;
            _compactInFlight = false;
        }
    }

    /// <summary>
    /// Shuts the chat down and releases the shutdown event.
    /// </summary>
    /// <remarks>
    /// A compaction thread may still be blocked in an LLM call and will read <c>_shutdown</c> when it
    /// returns, so disposal is only safe once the owning pipeline has stopped. Callers that cannot
    /// guarantee that should call <see cref="Close"/> instead.
    /// </remarks>
    public void Dispose()
    {
        Close();
        _shutdown.Dispose();
    }

    // ── Internal mutators (caller holds _gate) ────────────────────────

    private static string EnsureId(string? value, string prefix)
    {
        if (value is null)
        {
            return Ids.Generate(prefix);
        }

        if (!value.StartsWith($"{prefix}_", StringComparison.Ordinal))
        {
            throw new ChatItemException($"ID must start with '{prefix}_', got '{value}'");
        }

        return value;
    }

    /// <summary>Removes items from the front until the next user message boundary.</summary>
    private void EvictOldestTurnLocked()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var first = _buffer[0];
        _buffer.RemoveAt(0);
        if (first.IsMessage(ConversationRole.User))
        {
            _userTurnCount--;
        }

        while (_buffer.Count > 0 && !_buffer[0].IsMessage(ConversationRole.User))
        {
            _buffer.RemoveAt(0);
        }
    }

    private bool HasCallIdInBufferLocked(string callId) =>
        _buffer.Any(entry => entry.ItemType == ConversationItemType.FunctionCall && entry.CallId == callId);

    private void MarkCallCompletedLocked(string callId, string? status)
    {
        foreach (var entry in _buffer)
        {
            if (entry.ItemType == ConversationItemType.FunctionCall && entry.CallId == callId)
            {
                entry.Status = status ?? "completed";
                return;
            }
        }
    }

    private void AppendToolOutputLocked(string callId, ConversationItem outputItem)
    {
        if (HasCallIdInBufferLocked(callId))
        {
            _pendingToolCalls.Remove(callId);
            MarkCallCompletedLocked(callId, outputItem.Status);
            _buffer.Add(outputItem);
            return;
        }

        if (_pendingToolCalls.Remove(callId, out var call))
        {
            _logger.LogInformation("Re-injecting evicted function_call for call_id={CallId}", callId);
            call.Status = outputItem.Status ?? "completed";
            _buffer.Add(call);
            _buffer.Add(outputItem);
            return;
        }

        throw new ChatItemException($"No function_call with call_id '{callId}' found in conversation history.");
    }

    private List<ConversationItem> ToProviderHistoryLocked(IReadOnlyList<ConversationItem> items)
    {
        var result = new List<ConversationItem>();
        if (InitChatMessage is not null)
        {
            result.Add(new ConversationItem
            {
                Id = InitChatMessage.Id,
                ItemType = ConversationItemType.Message,
                Role = ConversationRole.System,
                Content = InitChatMessage.Content
                    .Select(p => ContentPart.InputText(
                        string.IsNullOrEmpty(p.Text) ? "A helpful AI assistant." : p.Text))
                    .ToList(),
            });
        }

        foreach (var item in items)
        {
            switch (item.ItemType)
            {
                case ConversationItemType.Message when item.Role == ConversationRole.User:
                    {
                        var content = item.Content
                            .Where(p => (p.Type == "input_text" && p.Text is not null)
                                        || (p.Type == "input_image" && p.ImageUrl is not null))
                            .Select(p => p.Clone())
                            .ToList();
                        if (content.Count > 0)
                        {
                            result.Add(new ConversationItem
                            {
                                Id = item.Id,
                                ItemType = ConversationItemType.Message,
                                Role = ConversationRole.User,
                                Content = content,
                            });
                        }

                        break;
                    }

                case ConversationItemType.Message when item.Role == ConversationRole.Assistant:
                    {
                        var content = item.Content
                            .Where(p => p.Type == "output_text" && p.Text is not null)
                            .Select(p => p.Clone())
                            .ToList();
                        if (content.Count > 0)
                        {
                            result.Add(new ConversationItem
                            {
                                Id = item.Id,
                                ItemType = ConversationItemType.Message,
                                Role = ConversationRole.Assistant,
                                Content = content,
                                Status = item.Status ?? "completed",
                            });
                        }

                        break;
                    }

                // Cloned like the message cases above. Returning the live instance handed callers a
                // reference into the buffer while claiming to be a projection, so a consumer that
                // edited Arguments or Output mutated the conversation itself.
                case ConversationItemType.FunctionCall when !string.IsNullOrEmpty(item.CallId):
                    result.Add(item.Clone());
                    break;

                case ConversationItemType.FunctionCallOutput:
                    result.Add(item.Clone());
                    break;
            }
        }

        return result;
    }

    // ── Compaction internals ─────────────────────────────────────────

    /// <summary>
    /// Computes the snapshot of items eligible for compaction. Always leaves the most recent user
    /// turn untouched because it may still be in flight, and yields nothing when fewer than two
    /// turns are compactable.
    /// </summary>
    private (List<ConversationItem> Snapshot, HashSet<string> MarkerIds, int Turns) SnapshotForCompactionLocked()
    {
        var turns = Math.Max(0, _userTurnCount - 1);
        if (turns < 2)
        {
            return ([], [], turns);
        }

        var userSeen = 0;
        var endIndex = _buffer.Count;
        for (var i = 0; i < _buffer.Count; i++)
        {
            if (!_buffer[i].IsMessage(ConversationRole.User))
            {
                continue;
            }

            userSeen++;
            if (userSeen == turns + 1)
            {
                endIndex = i;
                break;
            }
        }

        var itemsToCompact = _buffer.Take(endIndex).ToList();
        var markerIds = itemsToCompact.Where(x => x.Id is not null).Select(x => x.Id!).ToHashSet();
        var snapshot = ToProviderHistoryLocked(itemsToCompact);

        // Strip image parts so the summarizer doesn't have to handle them.
        foreach (var message in snapshot.Where(m => m.Role == ConversationRole.User))
        {
            message.Content = message.Content.Where(c => c.Type != "input_image").ToList();
        }

        return (snapshot, markerIds, turns);
    }

    private void MaybeTriggerCompactionLocked(CompactFn compactor)
    {
        if (_shutdown.IsSet || _compactInFlight)
        {
            return;
        }

        var (snapshot, markerIds, turns) = SnapshotForCompactionLocked();
        if (turns < 2 || markerIds.Count == 0)
        {
            return;
        }

        var generation = _generation;
        _compactInFlight = true;
        _logger.LogInformation(
            "Chat compaction triggered: compacting {Turns} turn(s) ({Items} item(s)), buffer size={Size}",
            turns,
            markerIds.Count,
            _buffer.Count);

        _ = Task.Run(() => CompactWorker(compactor, snapshot, markerIds, generation));
    }

    private async Task CompactWorker(
        CompactFn compactor,
        List<ConversationItem> snapshot,
        HashSet<string> markerIds,
        int generation)
    {
        try
        {
            if (_shutdown.IsSet || Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            CompactionResult result;
            try
            {
                result = await compactor(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat compaction failed; chat unchanged");
                return;
            }

            if (_shutdown.IsSet || Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            ApplyCompaction(result, markerIds, generation);
        }
        finally
        {
            // Don't clobber the flag if reset/close has advanced the generation.
            lock (_gate)
            {
                if (_generation == generation)
                {
                    _compactInFlight = false;
                }
            }
        }
    }

    /// <summary>
    /// Splices the summary in front of the items compaction did not consume. Function-call pairing
    /// stays entirely with <see cref="AddItem"/> / <see cref="AppendToolOutput"/>: compaction only
    /// drops items, it never inserts a call into the buffer.
    /// </summary>
    private void ApplyCompaction(CompactionResult result, HashSet<string> markerIds, int generation)
    {
        lock (_gate)
        {
            if (_shutdown.IsSet || _generation != generation)
            {
                return;
            }

            // Keep a call if its output falls outside the compacted range, otherwise the surviving
            // output would be orphaned.
            var outputCallIdsInRange = _buffer
                .Where(x => x.ItemType == ConversationItemType.FunctionCallOutput
                            && x.Id is not null
                            && markerIds.Contains(x.Id))
                .Select(x => x.CallId)
                .ToHashSet();

            var callIdsToKeep = _buffer
                .Where(x => x.Id is not null
                            && markerIds.Contains(x.Id)
                            && x.ItemType == ConversationItemType.FunctionCall
                            && !outputCallIdsInRange.Contains(x.CallId))
                .Select(x => x.Id!)
                .ToHashSet();

            var dropIds = markerIds.Except(callIdsToKeep).ToHashSet();
            var remaining = _buffer.Where(x => x.Id is null || !dropIds.Contains(x.Id)).ToList();

            var userSummary = new ConversationItem
            {
                Id = Ids.Generate("msg"),
                Role = ConversationRole.User,
                Content = [ContentPart.InputText(result.UserSummary)],
            };
            var assistantSummary = new ConversationItem
            {
                Id = Ids.Generate("msg"),
                Role = ConversationRole.Assistant,
                Content = [ContentPart.OutputText(result.AssistantSummary)],
            };

            _buffer = [userSummary, assistantSummary, .. remaining];
            _userTurnCount = _buffer.Count(x => x.IsMessage(ConversationRole.User));
            _logger.LogInformation(
                "Chat compaction applied: buffer now {Items} item(s), {Turns} user turn(s)",
                _buffer.Count,
                _userTurnCount);
        }
    }
}
