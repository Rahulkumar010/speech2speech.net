namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Unified cancellation signal for the speech2speech pipeline.
///
/// Uses a generation counter so pipeline threads (LLM, TTS) can detect cancellation without
/// brief-pulse timing games, and an internal discarding flag so the async send loop can drop
/// stale output.
/// </summary>
/// <remarks>
/// One writer (the router loop) and many readers (handler threads), the fields are guarded by a lock.
/// </remarks>
public sealed class CancelScope
{
    private readonly Lock _gate = new();
    private uint _generation;
    private bool _discarding;
    private uint? _discardedGeneration;

    /// <summary>
    /// Current generation number. Pipeline threads capture this at the start of each response and
    /// compare with <see cref="IsStale"/>.
    /// </summary>
    public uint Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>Whether the send loop should silently drop stale output.</summary>
    public bool Discarding
    {
        get
        {
            lock (_gate)
            {
                return _discarding;
            }
        }
    }

    /// <summary>
    /// Cancels the current response: bumps the generation so pipeline threads see their captured
    /// generation as stale, and enables the send-loop discard guard.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _discardedGeneration = _generation;
            _generation = unchecked(_generation + 1);
            _discarding = true;
        }
    }

    /// <summary>Pipeline acknowledged completion. Clears the discard guard.</summary>
    public void ResponseDone(uint? generation = null)
    {
        lock (_gate)
        {
            if (generation is { } gen
                && _discardedGeneration is { } discarded
                && gen != discarded
                && gen != _generation)
            {
                return;
            }

            _discarding = false;
            _discardedGeneration = null;
        }
    }

    /// <summary>An explicit <c>response.create</c> starts a new response. Clears the discard guard.</summary>
    public void NewResponse() => Reset();

    /// <summary>Returns whether <paramref name="generation"/> has been superseded by a cancel.</summary>
    public bool IsStale(uint generation)
    {
        lock (_gate)
        {
            return generation != _generation;
        }
    }

    /// <summary>Clears discard state (e.g. on new session connect).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _discarding = false;
            _discardedGeneration = null;
        }
    }
}
