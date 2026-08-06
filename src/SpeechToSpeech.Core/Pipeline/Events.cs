using System.Text.Json.Serialization;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Base for the internal events produced by VAD, the transcription notifier and the LM output
/// processor, and consumed by the realtime send loop.
/// </summary>
public abstract class PipelineEvent : IPipelineItem
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("turn_revision")]
    public int? TurnRevision { get; init; }
}

// ── VAD events ────────────────────────────────────────────────────────

public sealed class SpeechStartedEvent : PipelineEvent
{
    public override string Type => "speech_started";

    [JsonPropertyName("audio_start_ms")]
    public int AudioStartMs { get; init; }

    [JsonPropertyName("reopened")]
    public bool Reopened { get; init; }

    /// <summary>Not serialized: tells the service whether barge-in should cancel the response.</summary>
    [JsonIgnore]
    public bool InterruptResponse { get; init; } = true;
}

public sealed class SpeechStoppedEvent : PipelineEvent
{
    public override string Type => "speech_stopped";

    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; init; }

    [JsonPropertyName("audio_end_ms")]
    public int AudioEndMs { get; init; }
}

// ── Transcription events ─────────────────────────────────────────────

public sealed class PartialTranscriptionEvent : PipelineEvent
{
    public override string Type => "partial_transcription";

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed class TranscriptionCompletedEvent : PipelineEvent
{
    public override string Type => "transcription_completed";

    [JsonPropertyName("transcript")]
    public required string Transcript { get; init; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; init; }

    [JsonIgnore]
    public double? SpeechStoppedAtSeconds { get; init; }
}

// ── LLM output events ────────────────────────────────────────────────

public sealed class AssistantTextEvent : PipelineEvent
{
    public override string Type => "assistant_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<FunctionToolCall> Tools { get; init; } = [];

    /// <summary>
    /// Response generation that produced this text, mirroring <see cref="AudioOutput"/>, so the
    /// send loop can discard stale assistant text by the same generation-aware rule as audio.
    /// </summary>
    [JsonIgnore]
    public uint? CancelGeneration { get; init; }
}

public sealed class TokenUsageEvent : PipelineEvent
{
    public override string Type => "token_usage";

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

/// <summary>
/// Signals that a response could not be generated (e.g. invalid out-of-band input), so the service
/// closes it with <c>status="failed"</c> instead of the usual <c>completed</c>.
/// </summary>
public sealed class ResponseFailedEvent : PipelineEvent
{
    public override string Type => "response_failed";

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
