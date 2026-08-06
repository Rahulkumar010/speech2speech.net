using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Pipeline;
using Xunit;

namespace SpeechToSpeech.Tests;

public class TurnMetricsTests
{
    [Fact]
    public void CompleteLogsOnceAndForgetsTheTurn()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);

        metrics.Anchor("turn-1", Clock.NowSeconds);
        metrics.Mark("turn-1", "SttHandler/transcription");
        metrics.Mark("turn-1", "TtsHandler/audio");
        metrics.Complete("turn-1");

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("turn-1", entry, StringComparison.Ordinal);
        Assert.Contains("SttHandler/transcription", entry, StringComparison.Ordinal);
        Assert.Contains("TtsHandler/audio", entry, StringComparison.Ordinal);

        // A second completion has nothing left to report.
        metrics.Complete("turn-1");
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void OnlyTheFirstMarkPerStageIsKept()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);
        var anchor = Clock.NowSeconds;

        metrics.Anchor("turn-1", anchor);
        metrics.Mark("turn-1", "TtsHandler/audio");
        var afterFirst = Clock.NowSeconds - anchor;

        while (Clock.NowSeconds - anchor < afterFirst + 0.05)
        {
            metrics.Mark("turn-1", "TtsHandler/audio");
        }

        metrics.Complete("turn-1");

        // The total is measured to the first block, not to the last one 50 ms later.
        var total = double.Parse(
            Assert.Single(logger.Entries).Split(' ')[2],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(total < 0.05, $"expected the first mark to win, got {total:F3} s");
    }

    [Fact]
    public void MarksBeforeSpeechStopAreExcluded()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);

        metrics.Mark("turn-1", "VadHandler/vad_audio:Progressive");
        metrics.Anchor("turn-1", Clock.NowSeconds + 60);
        metrics.Mark("turn-1", "SttHandler/transcription");
        metrics.Complete("turn-1");

        // Every mark predates the anchor, so there is nothing to attribute.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TurnsWithoutASpeechStopAreNotReported()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);

        metrics.Mark("turn-1", "SttHandler/transcription");
        metrics.Complete("turn-1");

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void AbandonedTurnsDoNotAccumulate()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);

        // Barged-in turns never reach Complete; the oldest must be evicted rather than retained.
        for (var i = 0; i < 100; i++)
        {
            metrics.Anchor($"turn-{i}", Clock.NowSeconds);
            metrics.Mark($"turn-{i}", "SttHandler/transcription");
        }

        metrics.Complete("turn-0");
        Assert.Empty(logger.Entries);

        metrics.Complete("turn-99");
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void MessagesWithoutATurnAreIgnored()
    {
        var logger = new CapturingLogger();
        var metrics = new TurnMetrics(logger);

        metrics.Anchor(null, Clock.NowSeconds);
        metrics.Mark(null, "SttHandler/transcription");
        metrics.Complete(null);

        Assert.Empty(logger.Entries);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }
}
