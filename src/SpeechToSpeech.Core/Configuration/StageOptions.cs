namespace SpeechToSpeech.Core.Configuration;

/// <summary>Voice activity detection settings.</summary>
public sealed class VadOptions
{
    /// <summary>Path to the Silero VAD ONNX model. Required for the ONNX Runtime backend.</summary>
    public string ModelPath { get; set; } = "models/silero_vad.onnx";

    public double Threshold { get; set; } = 0.6;

    public int SampleRate { get; set; } = 16000;

    public int MinSilenceMs { get; set; } = 64;

    public int MinSpeechMs { get; set; } = 384;

    public int MinSpeechContinuationMs { get; set; } = 192;

    public double MaxSpeechMs { get; set; } = double.PositiveInfinity;

    public int SpeechPadMs { get; set; } = 30;

    public bool EnableRealtimeTranscription { get; set; } = true;

    public double RealtimeProcessingPause { get; set; } = 0.5;

    public int SpeculativeReopenMs { get; set; } = 1000;

    public int UnansweredReopenMs { get; set; } = 7000;

    public int ShortSegmentMergeMs { get; set; }
}

/// <summary>Speech-to-text settings.</summary>
public sealed class SttOptions
{
    /// <summary>
    /// Whisper weights: a ggml <c>.bin</c> file, or a directory to search and download them into.
    /// </summary>
    public string ModelPath { get; set; } = "models/whisper";

    public string? Language { get; set; }

    public double FinalRevisionSettleSeconds { get; set; }
}

/// <summary>Text-to-speech settings.</summary>
public sealed class TtsOptions
{
    /// <summary>Path to the Kokoro ONNX model.</summary>
    public string ModelPath { get; set; } = "models/kokoro-v1.0.onnx";

    /// <summary>Optional directory of KokoroSharp <c>.npy</c> voices. Empty uses the bundled pack.</summary>
    public string VoicesPath { get; set; } = string.Empty;

    public string Voice { get; set; } = "af_heart";

    public double Speed { get; set; } = 1.0;

    public int SampleRate { get; set; } = 24000;

    /// <summary>Output sample rate delivered to clients; audio is resampled when it differs.</summary>
    public int OutputSampleRate { get; set; } = 24000;
}
