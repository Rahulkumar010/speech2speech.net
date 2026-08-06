using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Stt;

/// <summary>Base STT handler with speculative-turn stale input filtering.</summary>
public abstract class BaseSttHandler(
    CancellationTokenSource stopSource,
    PipelineQueue<IPipelineItem> queueIn,
    PipelineQueue<IPipelineItem> queueOut,
    SpeculativeTurnTracker? speculativeTurns = null,
    double finalRevisionSettleSeconds = 0.0,
    ILogger? logger = null)
    : BaseHandler<VadAudio, PipelineMessage>(stopSource, queueIn, queueOut, logger)
{
    private const int MaxCompletedFinalRevisions = 2048;

    private readonly Dictionary<(string Stage, string TurnId, int Revision), int> _staleDropCounts = [];
    private readonly LinkedList<(string TurnId, int Revision)> _completedFinalOrder = [];
    private readonly HashSet<(string TurnId, int Revision)> _completedFinalRevisions = [];

    protected SpeculativeTurnTracker? SpeculativeTurns { get; } = speculativeTurns;

    protected double FinalRevisionSettleSeconds { get; } = finalRevisionSettleSeconds;

    protected override bool ShouldProcessInput(VadAudio item)
    {
        if (IsCompletedFinalRevision(item))
        {
            var drops = DropStaleQueuedInputs();
            LogStaleTurnItem(item, "input-after-final", drops);
            return false;
        }

        if (item.Mode == VadAudioMode.Progressive && HasQueuedFinalForRevision(item))
        {
            LogStaleTurnItem(item, "progressive-before-final");
            return false;
        }

        var waitForStability = item.Mode == VadAudioMode.Final;
        var gateStart = Clock.NowSeconds;
        var isLatest = IsLatestTurnItem(item, waitForPendingReopen: true, waitForStability);
        var gateWait = Clock.NowSeconds - gateStart;

        if (gateWait >= 0.05)
        {
            Logger.LogInformation(
                "{Handler}: STT input gate waited {Wait:F3}s for turn={TurnId} rev={Revision} mode={Mode} latest={Latest} age={Age:F3}s queue={Queue}",
                GetType().Name,
                gateWait,
                item.TurnId,
                item.TurnRevision,
                item.Mode,
                isLatest,
                ItemAgeSeconds(item),
                QueueIn.Count);
        }

        if (isLatest)
        {
            return true;
        }

        var queuedDrops = DropStaleQueuedInputs();
        LogStaleTurnItem(item, "input", queuedDrops);
        return false;
    }

    protected override bool ShouldEmitOutput(PipelineMessage output)
    {
        if (output is PartialTranscription && IsCompletedFinalRevision(output))
        {
            LogStaleTurnItem(output, "output-after-final");
            return false;
        }

        if (IsLatestTurnItem(output, waitForPendingReopen: true, waitForStability: false))
        {
            return true;
        }

        LogStaleTurnItem(output, "output");
        return false;
    }

    protected override void BeforeEmitOutput(PipelineMessage output)
    {
        if (output is Transcription)
        {
            MarkCompletedFinalRevision(output);
        }
    }

    private bool IsLatestTurnItem(PipelineMessage item, bool waitForPendingReopen, bool waitForStability)
    {
        if (SpeculativeTurns is null || item.TurnId is null || item.TurnRevision is null)
        {
            return true;
        }

        if (waitForStability)
        {
            return SpeculativeTurns.IsLatestAfterStabilityWindow(
                item.TurnId,
                item.TurnRevision,
                FinalRevisionSettleSeconds);
        }

        return waitForPendingReopen
            ? SpeculativeTurns.IsLatestAfterPendingReopen(item.TurnId, item.TurnRevision)
            : SpeculativeTurns.IsLatest(item.TurnId, item.TurnRevision);
    }

    private int DropStaleQueuedInputs()
    {
        if (SpeculativeTurns is null)
        {
            return 0;
        }

        return QueueIn.WithLock(queue => queue.RemoveWhere(queued =>
            queued is VadAudio audio
            && (IsCompletedFinalRevision(audio)
                || (audio.Mode == VadAudioMode.Progressive && HasQueuedFinalForRevisionLocked(audio))
                || !IsLatestTurnItem(audio, waitForPendingReopen: false, waitForStability: false))));
    }

    private bool HasQueuedFinalForRevision(PipelineMessage item) =>
        QueueIn.WithLock(_ => HasQueuedFinalForRevisionLocked(item));

    private bool HasQueuedFinalForRevisionLocked(PipelineMessage item)
    {
        if (RevisionKey(item) is not { } key)
        {
            return false;
        }

        return QueueIn.Any(queued =>
            queued is VadAudio audio && audio.Mode == VadAudioMode.Final && RevisionKey(audio) == key);
    }

    private static (string TurnId, int Revision)? RevisionKey(PipelineMessage item) =>
        item.TurnId is { } turnId && item.TurnRevision is { } revision ? (turnId, revision) : null;

    private bool IsCompletedFinalRevision(PipelineMessage item)
    {
        if (RevisionKey(item) is not { } key)
        {
            return false;
        }

        lock (_completedFinalRevisions)
        {
            return _completedFinalRevisions.Contains(key);
        }
    }

    private void MarkCompletedFinalRevision(PipelineMessage item)
    {
        if (RevisionKey(item) is not { } key)
        {
            return;
        }

        lock (_completedFinalRevisions)
        {
            if (!_completedFinalRevisions.Add(key))
            {
                return;
            }

            _completedFinalOrder.AddLast(key);
            while (_completedFinalOrder.Count > MaxCompletedFinalRevisions)
            {
                var oldest = _completedFinalOrder.First!.Value;
                _completedFinalOrder.RemoveFirst();
                _completedFinalRevisions.Remove(oldest);
            }
        }
    }

    private void LogStaleTurnItem(PipelineMessage item, string stage, int queuedDrops = 0)
    {
        if (RevisionKey(item) is not { } key)
        {
            return;
        }

        var counterKey = (stage, key.TurnId, key.Revision);
        _staleDropCounts.TryGetValue(counterKey, out var count);
        _staleDropCounts[counterKey] = ++count;

        const string Message =
            "{Handler}: dropping stale STT {Stage} for turn={TurnId} rev={Revision} age={Age:F3}s (+{Drops} queued)";
        var level = count == 1 ? LogLevel.Information : LogLevel.Debug;
        Logger.Log(
            level,
            Message,
            GetType().Name,
            stage,
            key.TurnId,
            key.Revision,
            ItemAgeSeconds(item),
            queuedDrops);
    }

    private static double ItemAgeSeconds(PipelineMessage item) =>
        item is VadAudio audio ? Math.Max(0.0, Clock.NowSeconds - audio.CreatedAtSeconds) : 0.0;
}
