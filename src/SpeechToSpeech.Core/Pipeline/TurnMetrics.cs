using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Per-turn stage timeline, so response latency can be attributed to a stage instead of guessed at.
/// </summary>
/// <remarks>
/// <para>
/// Every stage records the moment it first produced output for a turn. When the turn ends the marks
/// are rendered as one line of cumulative and per-stage deltas. Only the first output per stage is
/// kept: what matters for perceived latency is when a stage started producing, not how long it kept
/// going, and audio blocks arrive far too often to record individually.
/// </para>
/// <para>
/// Timings are anchored on the moment speech stopped rather than on the first mark, because that is
/// where the user starts waiting. Marks that predate the anchor belong to the part of the turn where
/// the user was still talking, and are dropped.
/// </para>
/// </remarks>
public sealed class TurnMetrics(ILogger? logger = null)
{
    /// <summary>Turns kept before the oldest is evicted.</summary>
    /// <remarks>
    /// A turn that is superseded by a barge-in never reaches its end mark, so without a cap the
    /// abandoned timelines would accumulate for the life of the process.
    /// </remarks>
    private const int MaxTrackedTurns = 8;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Timeline> _turns = [];
    private readonly Queue<string> _order = new();

    /// <summary>Records the first output a stage produced for a turn.</summary>
    public void Mark(string? turnId, string stage)
    {
        if (turnId is null)
        {
            return;
        }

        lock (_gate)
        {
            Track(turnId).Marks.TryAdd(stage, Clock.NowSeconds);
        }
    }

    /// <summary>Sets the moment the user stopped speaking, which all deltas are measured from.</summary>
    public void Anchor(string? turnId, double? atSeconds)
    {
        if (turnId is null || atSeconds is not { } anchor)
        {
            return;
        }

        lock (_gate)
        {
            Track(turnId).Anchor ??= anchor;
        }
    }

    /// <summary>Renders and forgets a turn's timeline.</summary>
    public void Complete(string? turnId)
    {
        if (turnId is null)
        {
            return;
        }

        Timeline timeline;
        lock (_gate)
        {
            if (!_turns.Remove(turnId, out var found))
            {
                return;
            }

            timeline = found;
        }

        if (timeline.Anchor is not { } anchor)
        {
            return;
        }

        var stages = timeline.Marks
            .Where(mark => mark.Value >= anchor)
            .OrderBy(mark => mark.Value)
            .ToList();

        if (stages.Count == 0)
        {
            return;
        }

        var previous = anchor;
        var breakdown = string.Join(
            " | ",
            stages.Select(stage =>
            {
                var step = stage.Value - previous;
                previous = stage.Value;
                return $"{stage.Key} +{step:F3} (@{stage.Value - anchor:F3})";
            }));

        _logger.LogInformation(
            "turn {TurnId}: {Total:F3} s from speech stop | {Breakdown}",
            turnId,
            previous - anchor,
            breakdown);
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private Timeline Track(string turnId)
    {
        if (_turns.TryGetValue(turnId, out var existing))
        {
            return existing;
        }

        while (_order.Count >= MaxTrackedTurns)
        {
            _turns.Remove(_order.Dequeue());
        }

        var timeline = new Timeline();
        _turns[turnId] = timeline;
        _order.Enqueue(turnId);
        return timeline;
    }

    private sealed class Timeline
    {
        public double? Anchor { get; set; }

        public Dictionary<string, double> Marks { get; } = [];
    }
}
