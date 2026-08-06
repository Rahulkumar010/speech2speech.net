using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>Thread-safe revision tracker for raw-audio speculative turns.</summary>
public sealed class SpeculativeTurnTracker
{
    public const double PendingReopenWaitTimeoutSeconds = 2.0;
    public const int DefaultMaxTrackedTurns = 2048;

    private readonly object _gate = new();
    private readonly int _maxTrackedTurns;
    private readonly ILogger _logger;

    // Insertion-ordered so pruning can evict the least recently observed turn.
    private readonly LinkedList<string> _order = [];
    private readonly Dictionary<string, LinkedListNode<string>> _orderNodes = [];
    private readonly Dictionary<string, int> _latestRevision = [];
    private readonly Dictionary<string, int> _committedRevision = [];
    private readonly Dictionary<string, PendingReopen> _pendingReopen = [];
    private readonly Dictionary<string, ReopenGrace> _reopenGrace = [];

    public SpeculativeTurnTracker(
        int maxTrackedTurns = DefaultMaxTrackedTurns,
        ILogger<SpeculativeTurnTracker>? logger = null)
    {
        _maxTrackedTurns = maxTrackedTurns;
        _logger = logger ?? NullLogger<SpeculativeTurnTracker>.Instance;
    }

    private readonly record struct PendingReopen(int BaseRevision, int CandidateRevision);

    private readonly record struct ReopenGrace(int Revision, double Deadline);

    private static double Now => Clock.NowSeconds;

    public void Observe(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return;
        }

        lock (_gate)
        {
            var current = _latestRevision.GetValueOrDefault(turnId, -1);
            if (revision.Value <= current)
            {
                return;
            }

            _latestRevision[turnId] = revision.Value;
            Touch(turnId);
            PruneTrackedTurns();
            _logger.LogDebug("Observed speculative turn {TurnId} revision {Revision}", turnId, revision);
            Monitor.PulseAll(_gate);
        }
    }

    public bool IsLatest(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            return LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    public bool IsLatestAfterPendingReopen(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            WaitForPendingReopen(turnId, revision.Value, PendingReopenWaitTimeoutSeconds);
            return LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    /// <summary>
    /// Non-blocking variant of <see cref="IsLatestAfterPendingReopen"/>. Returns <c>null</c> when a
    /// matching reopen candidate is still pending and the caller should retry after it resolves.
    /// </summary>
    public bool? TryIsLatestAfterPendingReopen(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            return HasPendingReopenLocked(turnId, revision.Value)
                ? null
                : LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    public bool IsLatestAfterReopenGrace(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            WaitForReopenGate(turnId, revision.Value);
            return LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    public bool? TryIsLatestAfterReopenGrace(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            if (HasPendingReopenLocked(turnId, revision.Value)
                || ReopenGraceRemainingLocked(turnId, revision.Value) > 0)
            {
                return null;
            }

            return LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    public bool CommitIfLatestAfterPendingReopen(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            WaitForPendingReopen(turnId, revision.Value, PendingReopenWaitTimeoutSeconds);
            return CommitLocked(turnId, revision.Value);
        }
    }

    public bool CommitIfLatestAfterReopenGrace(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            WaitForReopenGate(turnId, revision.Value);
            return CommitLocked(turnId, revision.Value);
        }
    }

    public bool? TryCommitIfLatestAfterPendingReopen(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            return HasPendingReopenLocked(turnId, revision.Value) ? null : CommitLocked(turnId, revision.Value);
        }
    }

    public bool? TryCommitIfLatestAfterReopenGrace(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        lock (_gate)
        {
            if (HasPendingReopenLocked(turnId, revision.Value)
                || ReopenGraceRemainingLocked(turnId, revision.Value) > 0)
            {
                return null;
            }

            return CommitLocked(turnId, revision.Value);
        }
    }

    public bool HasPendingReopen(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return false;
        }

        lock (_gate)
        {
            return HasPendingReopenLocked(turnId, revision.Value);
        }
    }

    public bool HasPendingReopenOrGrace(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return false;
        }

        lock (_gate)
        {
            return HasPendingReopenLocked(turnId, revision.Value)
                || ReopenGraceRemainingLocked(turnId, revision.Value) > 0;
        }
    }

    public void StartReopenGrace(string? turnId, int? revision, double graceSeconds)
    {
        if (turnId is null || revision is null || graceSeconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (LatestOrSelf(turnId, revision.Value) != revision.Value)
            {
                return;
            }

            if (_committedRevision.GetValueOrDefault(turnId, -1) >= revision.Value)
            {
                return;
            }

            var deadline = Now + graceSeconds;
            if (_reopenGrace.TryGetValue(turnId, out var existing)
                && existing.Revision == revision.Value
                && deadline <= existing.Deadline)
            {
                return;
            }

            _reopenGrace[turnId] = new ReopenGrace(revision.Value, deadline);
            _logger.LogDebug(
                "Started speculative reopen grace for turn {TurnId} revision {Revision}: {GraceMs:F0}ms",
                turnId,
                revision,
                graceSeconds * 1000);
            Monitor.PulseAll(_gate);
        }
    }

    public bool IsLatestAfterStabilityWindow(string? turnId, int? revision, double settleSeconds)
    {
        if (turnId is null || revision is null)
        {
            return true;
        }

        if (settleSeconds <= 0)
        {
            return IsLatestAfterPendingReopen(turnId, revision);
        }

        lock (_gate)
        {
            var deadline = Now + settleSeconds;
            while (LatestOrSelf(turnId, revision.Value) == revision.Value)
            {
                if (HasPendingReopenLocked(turnId, revision.Value))
                {
                    break;
                }

                var remaining = deadline - Now;
                if (remaining <= 0)
                {
                    break;
                }

                Wait(remaining);
            }

            WaitForPendingReopen(turnId, revision.Value, PendingReopenWaitTimeoutSeconds);
            return LatestOrSelf(turnId, revision.Value) == revision.Value;
        }
    }

    public void Commit(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_pendingReopen.TryGetValue(turnId, out var pending) && pending.BaseRevision == revision.Value)
            {
                _logger.LogDebug(
                    "Deferring speculative turn {TurnId} revision {Revision} commit while reopen is pending",
                    turnId,
                    revision);
                return;
            }

            CommitLocked(turnId, revision.Value);
        }
    }

    public bool IsCommitted(string? turnId, int? revision = null)
    {
        if (turnId is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_committedRevision.TryGetValue(turnId, out var committed))
            {
                return false;
            }

            return revision is null || committed >= revision.Value;
        }
    }

    public int? BeginReopenCandidate(string? turnId, int? revision)
    {
        if (turnId is null || revision is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_committedRevision.GetValueOrDefault(turnId, -1) >= revision.Value)
            {
                return null;
            }

            if (LatestOrSelf(turnId, revision.Value) != revision.Value)
            {
                return null;
            }

            if (_pendingReopen.TryGetValue(turnId, out var pending))
            {
                return pending.BaseRevision == revision.Value ? pending.CandidateRevision : null;
            }

            var candidateRevision = revision.Value + 1;
            _pendingReopen[turnId] = new PendingReopen(revision.Value, candidateRevision);
            _logger.LogDebug(
                "Started speculative reopen candidate for turn {TurnId} revision {Revision} -> {Candidate}",
                turnId,
                revision,
                candidateRevision);
            Monitor.PulseAll(_gate);
            return candidateRevision;
        }
    }

    public bool ConfirmReopenCandidate(string? turnId, int? baseRevision, int? candidateRevision)
    {
        if (turnId is null || baseRevision is null || candidateRevision is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_pendingReopen.TryGetValue(turnId, out var pending)
                || pending.BaseRevision != baseRevision.Value
                || pending.CandidateRevision != candidateRevision.Value)
            {
                return false;
            }

            if (_committedRevision.GetValueOrDefault(turnId, -1) >= baseRevision.Value
                || LatestOrSelf(turnId, baseRevision.Value) != baseRevision.Value)
            {
                _pendingReopen.Remove(turnId);
                PruneTrackedTurns();
                Monitor.PulseAll(_gate);
                return false;
            }

            _latestRevision[turnId] = candidateRevision.Value;
            Touch(turnId);
            _pendingReopen.Remove(turnId);
            PruneTrackedTurns();
            _logger.LogDebug(
                "Confirmed speculative reopen candidate for turn {TurnId} revision {Revision}",
                turnId,
                candidateRevision);
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public void CancelReopenCandidate(string? turnId, int? candidateRevision = null)
    {
        if (turnId is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_pendingReopen.TryGetValue(turnId, out var pending))
            {
                return;
            }

            if (candidateRevision is { } candidate && pending.CandidateRevision != candidate)
            {
                return;
            }

            _pendingReopen.Remove(turnId);
            PruneTrackedTurns();
            _logger.LogDebug("Cancelled speculative reopen candidate for turn {TurnId}", turnId);
            Monitor.PulseAll(_gate);
        }
    }

    public void WaitForPendingReopen(
        string? turnId,
        int? revision,
        double timeoutSeconds = PendingReopenWaitTimeoutSeconds)
    {
        if (turnId is null || revision is null)
        {
            return;
        }

        lock (_gate)
        {
            WaitForPendingReopen(turnId, revision.Value, timeoutSeconds);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _latestRevision.Clear();
            _committedRevision.Clear();
            _pendingReopen.Clear();
            _reopenGrace.Clear();
            _order.Clear();
            _orderNodes.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    // ── Internals (caller holds _gate) ────────────────────────────────

    private int LatestOrSelf(string turnId, int fallback) => _latestRevision.GetValueOrDefault(turnId, fallback);

    private void Touch(string turnId)
    {
        if (_orderNodes.TryGetValue(turnId, out var node))
        {
            _order.Remove(node);
        }

        _orderNodes[turnId] = _order.AddLast(turnId);
    }

    private void Forget(string turnId)
    {
        if (_orderNodes.Remove(turnId, out var node))
        {
            _order.Remove(node);
        }
    }

    /// <summary>
    /// Records <paramref name="revision"/> as committed when it is still the tracked latest, and
    /// reports whether the caller's output for that revision is still valid.
    /// </summary>
    /// <remarks>
    /// A turn that is no longer tracked is deliberately not written back: pruning only walks the
    /// tracked-turn order, so a committed entry without a tracked turn would never be reclaimed and
    /// a recycled turn id would then read as already committed. Such a commit still reports
    /// success, since dropping the output of a turn the tracker no longer knows about would be
    /// worse than emitting it.
    /// </remarks>
    private bool CommitLocked(string turnId, int revision)
    {
        if (!_latestRevision.TryGetValue(turnId, out var latest))
        {
            return true;
        }

        if (revision != latest)
        {
            return false;
        }

        _committedRevision[turnId] = revision;
        _logger.LogDebug("Committed speculative turn {TurnId} revision {Revision}", turnId, revision);
        Monitor.PulseAll(_gate);
        return true;
    }

    private bool HasPendingReopenLocked(string turnId, int revision) =>
        _pendingReopen.TryGetValue(turnId, out var pending) && pending.BaseRevision == revision;

    private double ReopenGraceRemainingLocked(string turnId, int revision)
    {
        if (!_reopenGrace.TryGetValue(turnId, out var grace) || grace.Revision != revision)
        {
            return 0.0;
        }

        if (LatestOrSelf(turnId, revision) != revision)
        {
            _reopenGrace.Remove(turnId);
            return 0.0;
        }

        var remaining = grace.Deadline - Now;
        if (remaining <= 0)
        {
            _reopenGrace.Remove(turnId);
            PruneTrackedTurns();
            return 0.0;
        }

        return remaining;
    }

    private void WaitForReopenGate(string turnId, int revision)
    {
        while (LatestOrSelf(turnId, revision) == revision)
        {
            WaitForPendingReopen(turnId, revision, PendingReopenWaitTimeoutSeconds);
            if (LatestOrSelf(turnId, revision) != revision)
            {
                return;
            }

            var remaining = ReopenGraceRemainingLocked(turnId, revision);
            if (remaining <= 0)
            {
                return;
            }

            _logger.LogDebug("Waiting for speculative reopen grace turn={TurnId} rev={Revision}", turnId, revision);
            Wait(remaining);
        }
    }

    private void WaitForPendingReopen(string turnId, int revision, double timeoutSeconds)
    {
        var deadline = Now + timeoutSeconds;
        if (!_pendingReopen.TryGetValue(turnId, out var pending) || pending.BaseRevision != revision)
        {
            return;
        }

        _logger.LogDebug("Waiting for pending speculative reopen turn={TurnId} rev={Revision}", turnId, revision);
        while (_pendingReopen.TryGetValue(turnId, out pending) && pending.BaseRevision == revision)
        {
            var remaining = deadline - Now;
            if (remaining <= 0)
            {
                _logger.LogWarning(
                    "Timed out waiting for pending speculative reopen turn={TurnId} rev={Revision}",
                    turnId,
                    revision);
                if (_pendingReopen.TryGetValue(turnId, out var current) && current == pending)
                {
                    _pendingReopen.Remove(turnId);
                    PruneTrackedTurns();
                    Monitor.PulseAll(_gate);
                }

                return;
            }

            Wait(remaining);
        }
    }

    private void PruneTrackedTurns()
    {
        if (_maxTrackedTurns <= 0)
        {
            return;
        }

        DropExpiredReopenGracesLocked();

        var prunable = _order
            .Where(turnId => !_pendingReopen.ContainsKey(turnId) && !_reopenGrace.ContainsKey(turnId))
            .ToList();

        var index = 0;
        while (prunable.Count - index > _maxTrackedTurns)
        {
            var turnId = prunable[index++];
            _latestRevision.Remove(turnId);
            _committedRevision.Remove(turnId);
            _reopenGrace.Remove(turnId);
            Forget(turnId);
        }
    }

    private void DropExpiredReopenGracesLocked()
    {
        var now = Now;
        foreach (var (turnId, grace) in _reopenGrace.ToList())
        {
            if (LatestOrSelf(turnId, grace.Revision) != grace.Revision || grace.Deadline <= now)
            {
                _reopenGrace.Remove(turnId);
            }
        }
    }

    private void Wait(double seconds)
    {
        var milliseconds = (int)Math.Clamp(Math.Ceiling(seconds * 1000), 1, int.MaxValue);
        Monitor.Wait(_gate, milliseconds);
    }
}
