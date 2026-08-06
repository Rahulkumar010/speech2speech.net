using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Configuration;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Vad;

/// <summary>
/// Detects voice activity. Audio is accumulated while speech is present and released downstream
/// when the utterance ends, plus progressive slices while the user is still speaking.
/// </summary>
public sealed class VadHandler : BaseHandler<AudioChunk, VadAudio>
{
    /// <summary>
    /// Fragments with less active speech than this are treated as noise and never held for
    /// stitching, so sub-threshold bursts cannot sum past <see cref="VadOptions.MinSpeechMs"/> and
    /// fire a false barge-in.
    /// </summary>
    private const double ShortSegmentMinFragmentMs = 100;

    private readonly VadOptions _options;
    private readonly ManualResetEventSlim _shouldListen;
    private readonly PipelineQueue<IPipelineItem>? _textOutputQueue;
    private readonly SpeculativeTurnTracker? _speculativeTurns;
    private readonly IVadModel _model;
    private readonly VadIterator _iterator;
    private readonly int _minSpeechContinuationMs;
    private readonly int _unansweredReopenMs;

    private long _totalSamples;
    private double _lastProcessTime;
    private double _lastLogTime;
    private int _logChunks;
    private int _logSpeechStarts;
    private int _logSpeechEnds;
    private int _logProgressiveYields;
    private bool _speechStartedEmitted;
    private int _turnCounter;
    private string? _currentTurnId;
    private int? _currentTurnRevision;
    private float[]? _speculativeAudioPrefix;
    private int? _lastFinalAudioMs;
    private TurnDetectionConfig? _lastTurnDetection;
    private PendingReopen? _pendingReopenCandidate;
    private PendingShortSegment? _pendingShortSegment;

    public VadHandler(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        VadOptions options,
        ManualResetEventSlim shouldListen,
        IVadModel model,
        PipelineQueue<IPipelineItem>? textOutputQueue = null,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger<VadHandler>? logger = null)
        : base(stopSource, queueIn, queueOut, logger)
    {
        _options = options;
        _shouldListen = shouldListen;
        _textOutputQueue = textOutputQueue;
        _speculativeTurns = speculativeTurns;
        _model = model;
        _minSpeechContinuationMs = ResolveMinSpeechContinuationMs(options.MinSpeechMs, options.MinSpeechContinuationMs);
        _unansweredReopenMs = Math.Max(options.SpeculativeReopenMs, options.UnansweredReopenMs);
        _iterator = new VadIterator(
            model,
            options.Threshold,
            options.SampleRate,
            options.MinSilenceMs,
            options.SpeechPadMs);
    }

    private readonly record struct PendingReopen(string TurnId, int BaseRevision, int CandidateRevision);

    private sealed record PendingShortSegment(float[] Audio, double ActiveMs, int StartMs, int EndMs);

    /// <summary>Cumulative audio received so far, in milliseconds.</summary>
    private int AudioMs => (int)(_totalSamples / (double)_options.SampleRate * 1000);

    public override IEnumerable<VadAudio> Process(AudioChunk input)
    {
        ApplyRuntimeTurnDetection(input.RuntimeConfig);

        if (!_shouldListen.IsSet)
        {
            yield break;
        }

        _logChunks++;
        var samples = AudioConvert.Int16BytesToFloat(input.Data);
        _totalSamples += samples.Length;

        var vadOutput = _iterator.Process(samples);

        // Deferred speech_started: emit only once active VAD speech reaches the valid threshold.
        var isTriggeredNow = _iterator.Triggered;
        if (isTriggeredNow && !_speechStartedEmitted)
        {
            EmitDeferredSpeechStarted();
        }
        else if (!isTriggeredNow && vadOutput is null)
        {
            DiscardExpiredPendingShortSegment();
        }

        LogSummaryOncePerSecond(isTriggeredNow);

        var results = UsesRealtimeTurnHandling() ? ProcessRealtime(vadOutput) : ProcessNormal(vadOutput);
        foreach (var result in results)
        {
            yield return result;
        }
    }

    protected override void BeforeEmitOutput(VadAudio output) => DropSupersededVadAudio(output);

    protected override void OnSessionEnd()
    {
        _iterator.ResetStates();
        _speechStartedEmitted = false;
        _currentTurnId = null;
        _currentTurnRevision = null;
        _speculativeAudioPrefix = null;
        _lastFinalAudioMs = null;
        _pendingReopenCandidate = null;
        _pendingShortSegment = null;
        _totalSamples = 0;
        _lastProcessTime = 0;
        _speculativeTurns?.Reset();
    }

    protected override void Cleanup() => _model.Dispose();

    // ── Turn detection updates ───────────────────────────────────────

    private void ApplyRuntimeTurnDetection(RuntimeConfig? runtimeConfig)
    {
        var turnDetection = runtimeConfig?.Session.Audio?.Input?.TurnDetection;
        if (turnDetection is null)
        {
            return;
        }

        if (_lastTurnDetection is { } previous
            && previous.Threshold == turnDetection.Threshold
            && previous.SilenceDurationMs == turnDetection.SilenceDurationMs)
        {
            return;
        }

        _lastTurnDetection = new TurnDetectionConfig
        {
            Type = turnDetection.Type,
            Threshold = turnDetection.Threshold,
            SilenceDurationMs = turnDetection.SilenceDurationMs,
        };

        if (turnDetection.Threshold is { } threshold)
        {
            _iterator.Threshold = threshold;
            Logger.LogInformation("VAD threshold updated to {Threshold}", threshold);
        }

        if (turnDetection.SilenceDurationMs is { } silenceMs)
        {
            _iterator.MinSilenceSamples = _options.SampleRate * silenceMs / 1000;
            Logger.LogInformation("VAD silence duration updated to {SilenceMs}ms", silenceMs);
        }
    }

    // ── Turn lifecycle ───────────────────────────────────────────────

    private static int ResolveMinSpeechContinuationMs(int minSpeechMs, int minSpeechContinuationMs) =>
        minSpeechContinuationMs <= 0
            ? minSpeechMs
            : Math.Min(minSpeechMs, Math.Max((int)ShortSegmentMinFragmentMs, minSpeechContinuationMs));

    private bool UsesRealtimeTurnHandling() =>
        _options.EnableRealtimeTranscription || _speculativeTurns is not null;

    private (string TurnId, int Revision) StartNewTurn()
    {
        CancelPendingReopen();
        _turnCounter++;
        _currentTurnId = $"turn_{_turnCounter}";
        _currentTurnRevision = 0;
        _speculativeAudioPrefix = null;
        _lastFinalAudioMs = null;
        _speculativeTurns?.Observe(_currentTurnId, _currentTurnRevision);
        return (_currentTurnId, _currentTurnRevision.Value);
    }

    private double SpeechBufferDurationMs() =>
        _iterator.SpeechBuffer().Sum(chunk => chunk.Length) / (double)_options.SampleRate * 1000;

    private double CurrentActiveSpeechDurationMs() =>
        _iterator.ActiveSpeechSamples / (double)_options.SampleRate * 1000;

    private double LastUtteranceActiveSpeechDurationMs() =>
        _iterator.LastUtteranceActiveSpeechSamples / (double)_options.SampleRate * 1000;

    /// <summary>Duration hysteresis for speech that continues a reopenable turn.</summary>
    private double ActiveSpeechMinMs(int startMs) =>
        _pendingReopenCandidate is not null || ShouldReopenCurrentTurn(startMs)
            ? _minSpeechContinuationMs
            : _options.MinSpeechMs;

    private bool ShouldReopenCurrentTurn(int audioStartMs)
    {
        if (!UsesRealtimeTurnHandling()
            || _currentTurnId is null
            || _currentTurnRevision is null
            || _lastFinalAudioMs is null)
        {
            return false;
        }

        if (_speculativeTurns?.IsCommitted(_currentTurnId, _currentTurnRevision) == true)
        {
            return false;
        }

        // Elapsed time is measured on the audio clock, so the window only advances while the client
        // streams audio: continuous capture behaves like wall time, push-to-talk gaps freeze it.
        var elapsedMs = Math.Max(0, audioStartMs - _lastFinalAudioMs.Value);

        // Within the short grace window any uncommitted turn may reopen. Beyond it, an unanswered
        // turn stays reopenable up to the sanity cap, so a user pause longer than the grace does
        // not orphan a turn the assistant has not replied to. The cap also bounds the
        // empty-transcript case, where no request is queued and the turn would never commit.
        var reopenLimitMs = _speculativeTurns is not null ? _unansweredReopenMs : _options.SpeculativeReopenMs;
        return elapsedMs <= reopenLimitMs;
    }

    private void BeginPendingReopenIfNeeded(int audioStartMs)
    {
        if (_pendingReopenCandidate is not null
            || !ShouldReopenCurrentTurn(audioStartMs)
            || _speculativeTurns is null)
        {
            return;
        }

        var candidateRevision = _speculativeTurns.BeginReopenCandidate(_currentTurnId, _currentTurnRevision);
        if (candidateRevision is null || _currentTurnId is null || _currentTurnRevision is null)
        {
            return;
        }

        _pendingReopenCandidate = new PendingReopen(_currentTurnId, _currentTurnRevision.Value, candidateRevision.Value);
        Logger.LogInformation(
            "VAD: pending reopen candidate for speculative turn {TurnId} revision {Revision}",
            _currentTurnId,
            candidateRevision);
    }

    private void CancelPendingReopen()
    {
        if (_pendingReopenCandidate is not { } pending)
        {
            return;
        }

        _speculativeTurns?.CancelReopenCandidate(pending.TurnId, pending.CandidateRevision);
        _pendingReopenCandidate = null;
    }

    private (string TurnId, int Revision, bool Reopened)? ConfirmPendingReopen()
    {
        if (_pendingReopenCandidate is not { } pending)
        {
            return null;
        }

        _pendingReopenCandidate = null;
        if (_speculativeTurns is not null
            && !_speculativeTurns.ConfirmReopenCandidate(pending.TurnId, pending.BaseRevision, pending.CandidateRevision))
        {
            return null;
        }

        _currentTurnId = pending.TurnId;
        _currentTurnRevision = pending.CandidateRevision;
        Logger.LogInformation(
            "VAD: reopened speculative turn {TurnId} revision {Revision}",
            pending.TurnId,
            pending.CandidateRevision);
        return (pending.TurnId, pending.CandidateRevision, true);
    }

    private (string TurnId, int Revision, bool Reopened)? ReopenCurrentTurn()
    {
        if (_currentTurnId is not { } turnId || _currentTurnRevision is not { } baseRevision)
        {
            return null;
        }

        int candidateRevision;
        if (_speculativeTurns is not null)
        {
            var candidate = _speculativeTurns.BeginReopenCandidate(turnId, baseRevision);
            if (candidate is null
                || !_speculativeTurns.ConfirmReopenCandidate(turnId, baseRevision, candidate.Value))
            {
                return null;
            }

            candidateRevision = candidate.Value;
        }
        else
        {
            candidateRevision = baseRevision + 1;
        }

        _currentTurnId = turnId;
        _currentTurnRevision = candidateRevision;
        Logger.LogInformation("VAD: reopened speculative turn {TurnId} revision {Revision}", turnId, candidateRevision);
        return (turnId, candidateRevision, true);
    }

    private (string TurnId, int Revision, bool Reopened) EnsureTurnForSpeechStart(int audioStartMs)
    {
        if (_speechStartedEmitted && _currentTurnId is { } current && _currentTurnRevision is { } revision)
        {
            return (current, revision, false);
        }

        if (ConfirmPendingReopen() is { } confirmed)
        {
            return confirmed;
        }

        if (ShouldReopenCurrentTurn(audioStartMs) && ReopenCurrentTurn() is { } reopened)
        {
            return reopened;
        }

        var (turnId, turnRevision) = StartNewTurn();
        _speculativeTurns?.Observe(turnId, turnRevision);
        return (turnId, turnRevision, false);
    }

    private (string? TurnId, int? Revision) CurrentTurnMetadata() => (_currentTurnId, _currentTurnRevision);

    private float[] CombinedTurnAudio(float[] currentSegment)
    {
        if (_speculativeAudioPrefix is not { } prefix)
        {
            return currentSegment;
        }

        var combined = new float[prefix.Length + currentSegment.Length];
        prefix.CopyTo(combined, 0);
        currentSegment.CopyTo(combined, prefix.Length);
        return combined;
    }

    // ── Short-segment stitching ──────────────────────────────────────

    private double SegmentDurationMs(float[] segment) => segment.Length / (double)_options.SampleRate * 1000;

    private int SegmentStartMs(float[] segment, int endMs) => Math.Max(0, endMs - (int)SegmentDurationMs(segment));

    private double ShortSegmentGapMs(int startMs) =>
        _pendingShortSegment is null ? double.PositiveInfinity : Math.Max(0, startMs - _pendingShortSegment.EndMs);

    private bool CanMergePendingShortSegment(int startMs) =>
        _pendingShortSegment is not null
        && _options.ShortSegmentMergeMs > 0
        && ShortSegmentGapMs(startMs) <= _options.ShortSegmentMergeMs;

    private (int StartMs, double ActiveMs) EffectiveActiveSpeechForStart(int startMs, double activeMs)
    {
        // A live fragment below the noise floor never counts the held segment toward the
        // speech-start threshold, mirroring the finalization path.
        if (activeMs < ShortSegmentMinFragmentMs || !CanMergePendingShortSegment(startMs))
        {
            return (startMs, activeMs);
        }

        return (_pendingShortSegment!.StartMs, _pendingShortSegment.ActiveMs + activeMs);
    }

    private (float[] Segment, double ActiveMs, int StartMs, bool Stitched) MergePendingShortSegment(
        float[] segment,
        double activeMs,
        int endMs)
    {
        var startMs = SegmentStartMs(segment, endMs);
        if (!CanMergePendingShortSegment(startMs))
        {
            DiscardExpiredPendingShortSegment(startMs);
            return (segment, activeMs, startMs, false);
        }

        var pending = _pendingShortSegment!;

        // Reinsert the silence between the two segments so the stitched audio keeps its acoustic
        // gap and its length matches the audio-clock span.
        var gapSamples = (int)(ShortSegmentGapMs(startMs) * _options.SampleRate / 1000);
        _pendingShortSegment = null;

        var merged = new float[pending.Audio.Length + Math.Max(0, gapSamples) + segment.Length];
        pending.Audio.CopyTo(merged, 0);
        segment.CopyTo(merged, pending.Audio.Length + Math.Max(0, gapSamples));
        return (merged, pending.ActiveMs + activeMs, pending.StartMs, true);
    }

    private void HoldShortSegment(float[] segment, double activeMs, int startMs, int endMs)
    {
        _pendingShortSegment = new PendingShortSegment(segment, activeMs, startMs, endMs);
        Logger.LogInformation(
            "VAD: holding short segment={SegmentMs:F0}ms active={ActiveMs:F0}ms (active_min={MinMs}ms, merge_max={MergeMs}ms)",
            SegmentDurationMs(segment),
            activeMs,
            _options.MinSpeechMs,
            _options.ShortSegmentMergeMs);
    }

    private void DiscardPendingShortSegment(string reason = "expired")
    {
        if (_pendingShortSegment is not { } pending)
        {
            return;
        }

        _pendingShortSegment = null;
        Logger.LogInformation(
            "VAD: discarding held short segment={SegmentMs:F0}ms active={ActiveMs:F0}ms ({Reason}, active_min={MinMs}ms)",
            SegmentDurationMs(pending.Audio),
            pending.ActiveMs,
            reason,
            _options.MinSpeechMs);
    }

    private void DiscardExpiredPendingShortSegment(int? nextStartMs = null)
    {
        if (_pendingShortSegment is not { } pending || _options.ShortSegmentMergeMs <= 0)
        {
            return;
        }

        var referenceMs = nextStartMs ?? AudioMs;
        if (Math.Max(0, referenceMs - pending.EndMs) > _options.ShortSegmentMergeMs)
        {
            DiscardPendingShortSegment("merge window elapsed");
        }
    }

    // ── Output filtering ─────────────────────────────────────────────

    private void DropSupersededVadAudio(VadAudio latest)
    {
        var dropped = QueueOut.RemoveWhere(item => item is VadAudio queued && IsSuperseded(queued, latest));
        if (dropped > 0)
        {
            Logger.LogDebug(
                "VAD: dropped {Count} superseded audio chunk(s) before enqueueing turn={TurnId} rev={Revision} mode={Mode}",
                dropped,
                latest.TurnId,
                latest.TurnRevision,
                latest.Mode);
        }
    }

    private bool IsSuperseded(VadAudio queued, VadAudio latest)
    {
        if (queued.TurnId is null || queued.TurnRevision is null)
        {
            return false;
        }

        if (_speculativeTurns is not null && !_speculativeTurns.IsLatest(queued.TurnId, queued.TurnRevision))
        {
            return true;
        }

        return queued.Mode == VadAudioMode.Progressive
               && queued.TurnId == latest.TurnId
               && queued.TurnRevision == latest.TurnRevision;
    }

    // ── Processing modes ─────────────────────────────────────────────

    private void EmitDeferredSpeechStarted()
    {
        var segmentDurationMs = _iterator.SpeechBuffer().Sum(c => c.Length) / (double)_options.SampleRate * 1000;
        var activeSpeechDurationMs = CurrentActiveSpeechDurationMs();
        var startMs = Math.Max(0, AudioMs - (int)SpeechBufferDurationMs());
        var (effectiveStartMs, effectiveActiveMs) = EffectiveActiveSpeechForStart(startMs, activeSpeechDurationMs);

        BeginPendingReopenIfNeeded(effectiveStartMs);
        var activeSpeechMinMs = ActiveSpeechMinMs(effectiveStartMs);
        if (effectiveActiveMs < activeSpeechMinMs)
        {
            return;
        }

        var (turnId, turnRevision, reopened) = EnsureTurnForSpeechStart(effectiveStartMs);
        _speechStartedEmitted = true;
        _logSpeechStarts++;
        Logger.LogInformation(
            "Speech started (confirmed, active={ActiveMs:F0}ms, min={MinMs:F0}ms, segment={SegmentMs:F0}ms, turn={TurnId} rev={Revision})",
            effectiveActiveMs,
            activeSpeechMinMs,
            segmentDurationMs,
            turnId,
            turnRevision);

        _textOutputQueue?.Put(new SpeechStartedEvent
        {
            AudioStartMs = effectiveStartMs,
            TurnId = turnId,
            TurnRevision = turnRevision,
            Reopened = reopened,
        });
    }

    private void LogSummaryOncePerSecond(bool isTriggeredNow)
    {
        var now = Clock.NowSeconds;
        if (now - _lastLogTime < 1.0)
        {
            return;
        }

        Logger.LogDebug(
            "VAD: {Chunks} chunks/s | {State} | starts={Starts} ends={Ends} progressive={Progressive}",
            _logChunks,
            isTriggeredNow ? "SPEAKING" : "silent",
            _logSpeechStarts,
            _logSpeechEnds,
            _logProgressiveYields);

        _logChunks = 0;
        _logSpeechStarts = 0;
        _logSpeechEnds = 0;
        _logProgressiveYields = 0;
        _lastLogTime = now;
    }

    /// <summary>Realtime mode keeps turns reopenable and emits progressive slices while speaking.</summary>
    private IEnumerable<VadAudio> ProcessRealtime(IReadOnlyList<float[]>? vadOutput)
    {
        if (_options.EnableRealtimeTranscription && _iterator.BufferedChunkCount > 0)
        {
            var currentTime = Clock.NowSeconds;
            var progressivePause = ProgressiveProcessingPause(SpeechBufferDurationMs());

            if (currentTime - _lastProcessTime >= progressivePause)
            {
                var array = Flatten(_iterator.SpeechBuffer());
                var durationMs = SegmentDurationMs(array);
                var activeSpeechDurationMs = CurrentActiveSpeechDurationMs();
                var startMs = Math.Max(0, AudioMs - (int)durationMs);

                if (activeSpeechDurationMs >= ActiveSpeechMinMs(startMs))
                {
                    _logProgressiveYields++;
                    Logger.LogDebug(
                        "VAD: yielding progressive audio (segment={SegmentMs:F0}ms, active={ActiveMs:F0}ms, interval={Interval:F2}s)",
                        durationMs,
                        activeSpeechDurationMs,
                        progressivePause);

                    var (turnId, turnRevision) = CurrentTurnMetadata();
                    yield return new VadAudio
                    {
                        Audio = CombinedTurnAudio(array),
                        Mode = VadAudioMode.Progressive,
                        TurnId = turnId,
                        TurnRevision = turnRevision,
                    };
                    _lastProcessTime = currentTime;
                }
            }
        }

        if (vadOutput is null)
        {
            yield break;
        }

        if (vadOutput.Count == 0)
        {
            HandlePhantomTrigger(realtime: true);
            yield break;
        }

        foreach (var final in FinalizeSegment(vadOutput, realtime: true))
        {
            yield return final;
        }
    }

    /// <summary>Original processing: yield only when speech ends.</summary>
    private IEnumerable<VadAudio> ProcessNormal(IReadOnlyList<float[]>? vadOutput)
    {
        if (vadOutput is null)
        {
            yield break;
        }

        if (vadOutput.Count == 0)
        {
            HandlePhantomTrigger(realtime: false);
            yield break;
        }

        foreach (var final in FinalizeSegment(vadOutput, realtime: false))
        {
            yield return final;
        }
    }

    private void HandlePhantomTrigger(bool realtime)
    {
        Logger.LogInformation("VAD: phantom trigger (empty buffer), closing speech pair");
        if (_speechStartedEmitted && _textOutputQueue is not null)
        {
            var (turnId, turnRevision) = realtime ? CurrentTurnMetadata() : (null, null);
            _textOutputQueue.Put(new SpeechStoppedEvent
            {
                AudioEndMs = AudioMs,
                TurnId = turnId,
                TurnRevision = turnRevision,
            });
        }

        if (!_speechStartedEmitted && realtime)
        {
            CancelPendingReopen();
        }

        _speechStartedEmitted = false;
        DiscardExpiredPendingShortSegment();
    }

    private IEnumerable<VadAudio> FinalizeSegment(IReadOnlyList<float[]> vadOutput, bool realtime)
    {
        var array = Flatten(vadOutput);
        var endMs = AudioMs;
        var rawActiveMs = LastUtteranceActiveSpeechDurationMs();
        var activeSpeechDurationMs = rawActiveMs;
        var stitched = false;
        int startMs;

        // Fragments below the noise floor never merge with or replace a held segment; the pending
        // segment's own expiry handles it.
        if (rawActiveMs >= ShortSegmentMinFragmentMs)
        {
            (array, activeSpeechDurationMs, startMs, stitched) =
                MergePendingShortSegment(array, activeSpeechDurationMs, endMs);
        }
        else
        {
            startMs = SegmentStartMs(array, endMs);
        }

        var durationMs = SegmentDurationMs(array);
        var minActiveMs = _speechStartedEmitted ? 0.0 : ActiveSpeechMinMs(startMs);

        if (activeSpeechDurationMs < minActiveMs || durationMs > _options.MaxSpeechMs)
        {
            if (_options.ShortSegmentMergeMs > 0
                && rawActiveMs >= ShortSegmentMinFragmentMs
                && activeSpeechDurationMs < minActiveMs
                && durationMs <= _options.MaxSpeechMs)
            {
                HoldShortSegment(array, activeSpeechDurationMs, startMs, endMs);
            }
            else
            {
                Logger.LogInformation(
                    "VAD: discarding segment={SegmentMs:F0}ms active={ActiveMs:F0}ms (active_min={MinMs}ms, segment_max={MaxMs}ms)",
                    durationMs,
                    activeSpeechDurationMs,
                    minActiveMs,
                    _options.MaxSpeechMs);
            }

            if (_speechStartedEmitted && _textOutputQueue is not null)
            {
                var (staleTurnId, staleRevision) = realtime ? CurrentTurnMetadata() : (null, null);
                _textOutputQueue.Put(new SpeechStoppedEvent
                {
                    AudioEndMs = AudioMs,
                    TurnId = staleTurnId,
                    TurnRevision = staleRevision,
                });
            }

            if (!_speechStartedEmitted && realtime)
            {
                CancelPendingReopen();
            }

            _speechStartedEmitted = false;
            yield break;
        }

        if (stitched)
        {
            Logger.LogInformation(
                "VAD: stitched short segment(s) into segment={SegmentMs:F0}ms active={ActiveMs:F0}ms",
                durationMs,
                activeSpeechDurationMs);
        }

        string? turnId;
        int? turnRevision;
        if (!_speechStartedEmitted)
        {
            var (newTurnId, newRevision, reopened) = EnsureTurnForSpeechStart(startMs);
            turnId = newTurnId;
            turnRevision = newRevision;
            _textOutputQueue?.Put(new SpeechStartedEvent
            {
                AudioStartMs = startMs,
                TurnId = newTurnId,
                TurnRevision = newRevision,
                Reopened = reopened,
                InterruptResponse = false,
            });
        }
        else
        {
            (turnId, turnRevision) = CurrentTurnMetadata();
        }

        _logSpeechEnds++;
        Logger.LogInformation(
            "Speech soft-ended (segment={SegmentMs:F0}ms, active={ActiveMs:F0}ms, turn={TurnId} rev={Revision})",
            durationMs,
            activeSpeechDurationMs,
            turnId,
            turnRevision);

        var outputArray = CombinedTurnAudio(array);
        _textOutputQueue?.Put(new SpeechStoppedEvent
        {
            DurationSeconds = outputArray.Length / (double)_options.SampleRate,
            AudioEndMs = endMs,
            TurnId = turnId,
            TurnRevision = turnRevision,
        });

        _speculativeAudioPrefix = outputArray;
        _lastFinalAudioMs = endMs;

        if (_speculativeTurns is not null)
        {
            // The grace window only delays response commits; reopen eligibility for unanswered
            // turns is extended separately via UnansweredReopenMs in ShouldReopenCurrentTurn.
            _speculativeTurns.StartReopenGrace(turnId, turnRevision, _options.SpeculativeReopenMs / 1000.0);
        }
        else
        {
            _shouldListen.Reset();
        }

        yield return new VadAudio
        {
            Audio = outputArray,
            Mode = VadAudioMode.Final,
            TurnId = turnId,
            TurnRevision = turnRevision,
        };

        _lastProcessTime = 0.0;
        _speechStartedEmitted = false;
    }

    /// <summary>Back off the progressive cadence as the utterance grows, capped at two seconds.</summary>
    private double ProgressiveProcessingPause(double durationMs)
    {
        var basePause = Math.Max(0.0, _options.RealtimeProcessingPause);
        var durationSeconds = durationMs / 1000.0;
        var multiplier = durationSeconds switch
        {
            < 8.0 => 1.0,
            < 15.0 => 2.0,
            < 30.0 => 4.0,
            _ => 6.0,
        };

        return Math.Min(basePause * multiplier, 2.0);
    }

    private static float[] Flatten(IReadOnlyList<float[]> chunks)
    {
        var total = chunks.Sum(chunk => chunk.Length);
        var result = new float[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }

        return result;
    }
}
