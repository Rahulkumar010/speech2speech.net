using System.Diagnostics;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// Base for all typed pipeline messages. <see cref="Tag"/> mirrors the Pydantic discriminator so
/// a message can be round-tripped through JSON when needed.
/// </summary>
public abstract class PipelineMessage : IPipelineItem
{
    public abstract string Tag { get; }

    /// <summary>Speculative turn this message belongs to, when known.</summary>
    public string? TurnId { get; init; }

    /// <summary>Revision of <see cref="TurnId"/> this message was produced for.</summary>
    public int? TurnRevision { get; init; }
}

/// <summary>Messages that carry the response generation that produced them.</summary>
public interface ICancellable
{
    uint? CancelGeneration { get; }
}

/// <summary>
/// Raw PCM travelling on the audio queues. <c>bytes</c> there; a wrapper keeps the
/// queue element type uniform and lets a chunk carry the session config that arrived with it.
/// </summary>
public sealed class AudioChunk(byte[] data, RuntimeConfig? runtimeConfig = null) : IPipelineItem
{
    public byte[] Data { get; } = data;

    public RuntimeConfig? RuntimeConfig { get; } = runtimeConfig;
}

// ── VAD → STT ─────────────────────────────────────────────────────────

public enum VadAudioMode
{
    Progressive,
    Final,
}

/// <summary>Audio segment from VAD, with optional mode for realtime transcription.</summary>
public sealed class VadAudio : PipelineMessage
{
    public override string Tag => "vad_audio";

    public required float[] Audio { get; init; }

    public VadAudioMode? Mode { get; init; }

    public double CreatedAtSeconds { get; init; } = Clock.NowSeconds;
}

// ── STT → TranscriptionNotifier → LLM ────────────────────────────────

/// <summary>Live partial transcription; consumed by the notifier, never forwarded to the LLM.</summary>
public sealed class PartialTranscription : PipelineMessage
{
    public override string Tag => "partial_transcription";

    public required string Text { get; init; }
}

/// <summary>Final transcription result.</summary>
public sealed class Transcription : PipelineMessage
{
    public override string Tag => "transcription";

    public required string Text { get; init; }

    public string? LanguageCode { get; init; }

    public double? SpeechStoppedAtSeconds { get; init; }
}

// ── Realtime service → LLM ────────────────────────────────────────────

/// <summary>
/// Triggers LLM generation for a realtime session. Carries everything the LM handler needs so it
/// never has to reach back into shared objects.
/// </summary>
public sealed class GenerateResponseRequest : PipelineMessage
{
    public override string Tag => "generate_response";

    public required RuntimeConfig RuntimeConfig { get; init; }

    public ResponseCreateParams? Response { get; init; }

    public string? LanguageCode { get; init; }

    public double? SpeechStoppedAtSeconds { get; init; }
}

// ── LLM → LMOutputProcessor ──────────────────────────────────────────

/// <summary>One sentence/chunk of the LLM response.</summary>
public sealed class LlmResponseChunk : PipelineMessage, ICancellable
{
    public override string Tag => "llm_response_chunk";

    public required string Text { get; init; }

    public string? LanguageCode { get; init; }

    public IReadOnlyList<FunctionToolCall> Tools { get; init; } = [];

    public RuntimeConfig? RuntimeConfig { get; init; }

    public ResponseCreateParams? Response { get; init; }

    public double? SpeechStoppedAtSeconds { get; init; }

    public uint? CancelGeneration { get; init; }
}

/// <summary>Token count report (side-channel, not forwarded to TTS).</summary>
public sealed class TokenUsage : PipelineMessage
{
    public override string Tag => "token_usage";

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }
}

/// <summary>
/// Sentinel marking the end of a response. <see cref="Error"/> is set when generation could not
/// start (e.g. an out-of-band response whose input failed validation); the output processor turns
/// it into a <c>response.done(status="failed")</c> while still closing the response normally so
/// the pipeline cleans up.
/// </summary>
public sealed class EndOfResponse : PipelineMessage, ICancellable
{
    public override string Tag => "end_of_response";

    public uint? CancelGeneration { get; init; }

    public string? Error { get; init; }
}

// ── LMOutputProcessor → TTS ──────────────────────────────────────────

/// <summary>Text to synthesize with per-response context.</summary>
public sealed class TtsInput : PipelineMessage, ICancellable
{
    public override string Tag => "tts_input";

    public required string Text { get; init; }

    public string? LanguageCode { get; init; }

    public RuntimeConfig? RuntimeConfig { get; init; }

    public ResponseCreateParams? Response { get; init; }

    public double? SpeechStoppedAtSeconds { get; init; }

    public uint? CancelGeneration { get; init; }
}

/// <summary>Audio queue item tagged with the response generation that produced it.</summary>
public sealed class AudioOutput : PipelineMessage, ICancellable
{
    public override string Tag => "audio_output";

    public required byte[] Audio { get; init; }

    public uint? CancelGeneration { get; init; }
}

/// <summary>Monotonic clock shared by every stage so message ages are comparable.</summary>
public static class Clock
{
    public static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
