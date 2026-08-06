using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SpeechToSpeech.Core.Configuration;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Llm;
using SpeechToSpeech.Tts;
using Whisper.net.Ggml;

namespace SpeechToSpeech.Pipeline;

/// <summary>
/// Everything <see cref="VoicePipeline"/> needs to build a speech-to-speech loop: model paths,
/// voice, language, persona and the tuning knobs each stage exposes.
/// </summary>
/// <remarks>
/// Every property has a working default, so a caller can construct the pipeline with
/// <c>new VoicePipelineConfig()</c> and override only what matters. The same shape loads from JSON
/// via <see cref="Load"/>, which is what makes the pipeline fully config-driven.
/// </remarks>
public sealed class VoicePipelineConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public VadConfig Vad { get; set; } = new();

    public SttConfig Stt { get; set; } = new();

    public LlmConfig Llm { get; set; } = new();

    public TtsConfig Tts { get; set; } = new();

    public AudioIoConfig Audio { get; set; } = new();

    public QueueConfig Queues { get; set; } = new();

    /// <summary>Records per-stage turn latency and logs a timeline for each response.</summary>
    public bool EnableMetrics { get; set; }

    /// <summary>Reads a JSON configuration file.</summary>
    public static VoicePipelineConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads a JSON configuration document. Comments and trailing commas are tolerated.</summary>
    public static VoicePipelineConfig Parse(string json) =>
        JsonSerializer.Deserialize<VoicePipelineConfig>(json, JsonOptions) ?? new VoicePipelineConfig();

    /// <summary>Reads a JSON configuration file when it exists, otherwise returns defaults.</summary>
    public static VoicePipelineConfig LoadOrDefault(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Load(path) : new VoicePipelineConfig();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Fails fast on combinations that would otherwise surface as silence or garbled audio.</summary>
    /// <exception cref="InvalidOperationException">A setting is missing or internally inconsistent.</exception>
    public void Validate()
    {
        if (Audio.InputSampleRate != Vad.SampleRate)
        {
            throw new InvalidOperationException(
                $"audio.inputSampleRate ({Audio.InputSampleRate}) must match vad.sampleRate ({Vad.SampleRate}); " +
                "the VAD consumes capture frames unresampled.");
        }

        if (string.IsNullOrWhiteSpace(Vad.ModelPath))
        {
            throw new InvalidOperationException("vad.modelPath is required.");
        }

        if (string.IsNullOrWhiteSpace(Tts.ModelPath))
        {
            throw new InvalidOperationException("tts.modelPath is required.");
        }

        if (string.IsNullOrWhiteSpace(Llm.Model))
        {
            throw new InvalidOperationException("llm.model is required.");
        }

        if (Llm.UserTemplate is { Length: > 0 } template
            && !template.Contains(LlmConfig.TranscriptPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"llm.userTemplate must contain the {LlmConfig.TranscriptPlaceholder} placeholder.");
        }
    }
}

/// <summary>Voice-activity detection settings. Defaults match <see cref="VadOptions"/>.</summary>
public sealed class VadConfig
{
    public string ModelPath { get; set; } = "models/silero_vad.onnx";

    /// <summary>Speech probability above which a frame counts as speech. Higher rejects more noise.</summary>
    public double Threshold { get; set; } = 0.6;

    public int SampleRate { get; set; } = 16000;

    public int MinSilenceMs { get; set; } = 300;

    public int MinSpeechMs { get; set; } = 384;

    public int MinSpeechContinuationMs { get; set; } = 192;

    /// <summary>Hard cap on a single utterance. Null means unbounded.</summary>
    public double? MaxSpeechMs { get; set; }

    public int SpeechPadMs { get; set; } = 30;

    public bool EnableRealtimeTranscription { get; set; } = true;

    public double RealtimeProcessingPause { get; set; } = 0.5;

    public int SpeculativeReopenMs { get; set; } = 1000;

    public int UnansweredReopenMs { get; set; } = 7000;

    public int ShortSegmentMergeMs { get; set; }

    /// <summary>Logs peak Silero probability once per second, for threshold tuning.</summary>
    public bool LogProbabilities { get; set; }

    public VadOptions ToOptions() => new()
    {
        ModelPath = ModelPath,
        Threshold = Threshold,
        SampleRate = SampleRate,
        MinSilenceMs = MinSilenceMs,
        MinSpeechMs = MinSpeechMs,
        MinSpeechContinuationMs = MinSpeechContinuationMs,
        MaxSpeechMs = MaxSpeechMs ?? double.PositiveInfinity,
        SpeechPadMs = SpeechPadMs,
        EnableRealtimeTranscription = EnableRealtimeTranscription,
        RealtimeProcessingPause = RealtimeProcessingPause,
        SpeculativeReopenMs = SpeculativeReopenMs,
        UnansweredReopenMs = UnansweredReopenMs,
        ShortSegmentMergeMs = ShortSegmentMergeMs,
    };
}

/// <summary>Speech-to-text settings.</summary>
public sealed class SttConfig
{
    /// <summary>A ggml <c>.bin</c> file, or a directory to search and download weights into.</summary>
    public string ModelPath { get; set; } = "models/whisper";

    /// <summary>Whisper ggml size to download when <see cref="ModelPath"/> holds no weights.</summary>
    public string ModelSize { get; set; } = "base";

    /// <summary>ISO code such as <c>en</c>, or <c>auto</c> to detect per utterance.</summary>
    public string Language { get; set; } = "auto";

    public double FinalRevisionSettleSeconds { get; set; } = 0.15;

    public SttOptions ToOptions() => new()
    {
        ModelPath = ModelPath,
        Language = Language,
        FinalRevisionSettleSeconds = FinalRevisionSettleSeconds,
    };

    /// <summary>Resolves <see cref="ModelSize"/>, falling back to <see cref="GgmlType.Base"/>.</summary>
    public GgmlType ResolveGgmlType() =>
        Enum.TryParse<GgmlType>(ModelSize, ignoreCase: true, out var type) ? type : GgmlType.Base;
}

/// <summary>Language model, persona and prompt settings.</summary>
public sealed class LlmConfig
{
    /// <summary>Token replaced by the final transcript when <see cref="UserTemplate"/> is set.</summary>
    public const string TranscriptPlaceholder = "{transcript}";

    /// <summary>OpenAI-compatible base URL, including the <c>/v1</c> suffix.</summary>
    public string BaseUrl { get; set; } = "http://localhost:65466/v1";

    /// <summary>Literal key. Prefer <see cref="ApiKeyEnvironmentVariable"/> so secrets stay out of config files.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";

    public string Model { get; set; } = "qwen2.5-1.5b-instruct-openvino-npu:5";

    /// <summary>Who the assistant is. Prepended to <see cref="SystemInstructions"/>.</summary>
    public string? Persona { get; set; }

    /// <summary>How the assistant should behave. Appended after <see cref="Persona"/>.</summary>
    public string? SystemInstructions { get; set; } =
        "You are a concise, friendly voice assistant. Answer in under 20 words.";

    /// <summary>
    /// Optional wrapper applied to each final transcript before it reaches the model, e.g.
    /// <c>"The user said: {transcript}. Reply in one sentence."</c>. Null passes the transcript through.
    /// </summary>
    public string? UserTemplate { get; set; }

    public bool Stream { get; set; } = true;

    public int StreamBatchSentences { get; set; } = 2;

    public int StreamFirstBatchSentences { get; set; } = 1;

    /// <summary>Tells the model to answer in the language the user spoke.</summary>
    public bool EnableLanguagePrompt { get; set; } = true;

    /// <summary>Summarizes older turns instead of evicting them once the history exceeds <see cref="HistorySize"/>.</summary>
    public bool CompactHistory { get; set; }

    public double RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>User turns retained before eviction or compaction.</summary>
    public int HistorySize { get; set; } = 10;

    /// <summary>Strips reasoning blocks from models that emit them. Disable for reasoning-first models.</summary>
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    public double? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    /// <summary><c>auto</c>, <c>none</c>, <c>required</c>, or a function name.</summary>
    public string? ToolChoice { get; set; }

    public List<ToolConfig> Tools { get; set; } = [];

    /// <summary>Issues a cheap request at startup so the first turn is not paying for model load.</summary>
    public bool Warmup { get; set; } = true;

    /// <summary>Joins persona and instructions into the single system prompt the session carries.</summary>
    public string? BuildInstructions()
    {
        var parts = new[] { Persona, SystemInstructions }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());

        var combined = string.Join("\n\n", parts);
        return combined.Length == 0 ? null : combined;
    }

    /// <summary>Applies <see cref="UserTemplate"/>, or returns <paramref name="transcript"/> unchanged.</summary>
    public string ApplyUserTemplate(string transcript) =>
        string.IsNullOrEmpty(UserTemplate)
            ? transcript
            : UserTemplate.Replace(TranscriptPlaceholder, transcript, StringComparison.Ordinal);

    public LanguageModelHandlerOptions ToOptions() => new()
    {
        ModelName = Model,
        Stream = Stream,
        StreamBatchSentences = StreamBatchSentences,
        StreamFirstBatchSentences = StreamFirstBatchSentences,
        EnableLanguagePrompt = EnableLanguagePrompt,
        CompactHistory = CompactHistory,
        RequestTimeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
    };

    /// <summary>Reads the literal key, else the named environment variable.</summary>
    public string? ResolveApiKey() =>
        !string.IsNullOrEmpty(ApiKey)
            ? ApiKey
            : string.IsNullOrEmpty(ApiKeyEnvironmentVariable)
                ? null
                : Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
}

/// <summary>A function tool advertised to the model. The host is responsible for executing calls.</summary>
public sealed class ToolConfig
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>JSON Schema object describing the arguments. Defaults to a no-argument schema.</summary>
    public JsonNode? Parameters { get; set; }

    public FunctionToolDefinition ToDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Parameters = Parameters ?? JsonNode.Parse("""{"type":"object","properties":{}}"""),
    };
}

/// <summary>Text-to-speech settings.</summary>
public sealed class TtsConfig
{
    public string ModelPath { get; set; } = "models/kokoro-v1.0.onnx";

    /// <summary>Directory of KokoroSharp <c>.npy</c> voices. Empty uses the bundled pack.</summary>
    public string VoicesPath { get; set; } = string.Empty;

    /// <summary>Kokoro voice id such as <c>bm_fable</c>. Null picks the default for the language.</summary>
    public string? Voice { get; set; }

    /// <summary>Speaking rate; 1.0 is natural, below 1 is slower.</summary>
    public double Speed { get; set; } = 1.0;

    public int SampleRate { get; set; } = 24000;

    /// <summary>Rate delivered to the caller; audio is resampled when it differs from <see cref="SampleRate"/>.</summary>
    public int OutputSampleRate { get; set; } = 24000;

    /// <summary>
    /// Kokoro language letter (<c>a</c>, <c>b</c>, <c>j</c>, <c>z</c>, …). Null derives it from the
    /// STT language, falling back to British English.
    /// </summary>
    public string? LanguageCode { get; set; }

    /// <summary>Playback block length. Smaller blocks lower barge-in latency at more overhead.</summary>
    public int BlockMilliseconds { get; set; } = 50;

    /// <summary>Explicit block size in samples, overriding <see cref="BlockMilliseconds"/>.</summary>
    public int? BlockSize { get; set; }

    /// <summary>Resolves the Kokoro language letter from config, then from the STT language.</summary>
    public string ResolveLanguageCode(string? sttLanguage)
    {
        if (!string.IsNullOrWhiteSpace(LanguageCode))
        {
            return LanguageCode;
        }

        if (!string.IsNullOrWhiteSpace(sttLanguage)
            && !sttLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && KokoroLanguages.WhisperToKokoro.TryGetValue(sttLanguage, out var mapped))
        {
            return mapped;
        }

        return "b";
    }

    public string ResolveVoice(string langCode) =>
        !string.IsNullOrWhiteSpace(Voice) ? Voice
        : KokoroLanguages.DefaultVoices.TryGetValue(langCode, out var voice) ? voice
        : "bm_fable";

    public int ResolveBlockSize() => BlockSize ?? Math.Max(1, OutputSampleRate * BlockMilliseconds / 1000);

    public TtsOptions ToOptions(string langCode) => new()
    {
        ModelPath = ModelPath,
        VoicesPath = VoicesPath,
        Voice = ResolveVoice(langCode),
        Speed = Speed,
        SampleRate = SampleRate,
        OutputSampleRate = OutputSampleRate,
    };
}

/// <summary>Capture and playback device settings, used by <see cref="VoiceLoopHost"/>.</summary>
public sealed class AudioIoConfig
{
    /// <summary>WinMM capture device index. 0 is the system default.</summary>
    public int InputDeviceNumber { get; set; }

    /// <summary>Must match <see cref="VadConfig.SampleRate"/>; capture audio is not resampled.</summary>
    public int InputSampleRate { get; set; } = 16000;

    /// <summary>Samples per VAD frame. 512 is the Silero v5 window at 16 kHz.</summary>
    public int FrameSamples { get; set; } = 512;

    public int CaptureBufferMilliseconds { get; set; } = 32;

    public int CaptureBufferCount { get; set; } = 3;

    public int PlaybackLatencyMilliseconds { get; set; } = 120;

    public int PlaybackBufferSeconds { get; set; } = 30;

    /// <summary>Prints a live input level meter, to prove capture is working.</summary>
    public bool EnableLevelMeter { get; set; }

    /// <summary>Silent frames pushed on shutdown so the VAD releases the last utterance.</summary>
    public int TrailingSilenceFrames { get; set; } = 40;

    /// <summary>How long shutdown waits for an in-flight response to finish speaking.</summary>
    public double DrainTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Queue bounds. Audio queues evict their oldest item when full: the capture callback must never
/// block, and dropping stale audio beats growing the heap until the process dies.
/// </summary>
public sealed class QueueConfig
{
    /// <summary>Capture and VAD queue depth. 512 is about 16 s of 32 ms frames.</summary>
    public int AudioCapacity { get; set; } = 512;

    /// <summary>Playback queue depth. 2048 is about 100 s of 50 ms blocks.</summary>
    public int OutputCapacity { get; set; } = 2048;
}
