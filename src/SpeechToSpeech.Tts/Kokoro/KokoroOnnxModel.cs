using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace SpeechToSpeech.Tts.Kokoro;

/// <summary>
/// Kokoro-82M running on ONNX Runtime. Produces 24 kHz mono float audio.
/// </summary>
/// <remarks>
/// <para>
/// Phonemization, the token vocabulary, text normalization and voice loading come from
/// <see href="https://github.com/Lyrcaxis/KokoroSharp">KokoroSharp</see> rather than from
/// hand-written equivalents. Beyond removing code, this buys two things the local versions did not
/// have: a native English G2P (the misaki port) that does not shell out at all, and eSpeak NG
/// binaries shipped in the package for every other language, so nothing has to be installed on the
/// machine or found on <c>PATH</c>.
/// </para>
/// <para>
/// The lower-level <see cref="KokoroModel"/> is used instead of the <c>KokoroTTS</c> facade on
/// purpose: the facade owns job scheduling and audio playback, both of which this pipeline already
/// provides and neither of which can be driven by our speculative-turn cancellation. What is wanted
/// here is the one thing the facade hides — raw samples, synchronously.
/// </para>
/// </remarks>
public sealed class KokoroOnnxModel : IDisposable
{
    public const int SampleRate = 24000;

    private readonly KokoroModel _model;
    private readonly Dictionary<string, KokoroVoice> _voices;
    private readonly DefaultSegmentationConfig _segmentation = new();

    public KokoroOnnxModel(string modelPath, string? voicesPath = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Kokoro ONNX model not found at '{modelPath}'. Download kokoro-v1.0.onnx from the "
                + "KokoroSharpBinaries releases or the onnx-community/Kokoro-82M-v1.0-ONNX repository.",
                modelPath);
        }

        _model = new KokoroModel(modelPath, new SessionOptions());

        // A directory is a voice pack in KokoroSharp's .npy layout. Anything else — notably a packed
        // voices-v1.0.bin — is not loadable here, so the voices the package copies next to the
        // executable are used instead.
        //
        // Loaded explicitly: KokoroVoiceManager.Voices is only populated as a side effect of its
        // GetVoice/GetVoices lookups, so reading the list directly would find it empty.
        KokoroVoiceManager.LoadVoicesFromPath(
            !string.IsNullOrEmpty(voicesPath) && Directory.Exists(voicesPath) ? voicesPath : "voices");

        _voices = KokoroVoiceManager.Voices
            .DistinctBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(voice => voice.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasVoice(string voice) => _voices.ContainsKey(voice);

    /// <summary>Synthesizes one utterance. Returns an empty array when the text has no phonemes.</summary>
    public float[] Synthesize(string text, string voice, string langCode, float speed)
    {
        if (!_voices.TryGetValue(voice, out var style))
        {
            throw new InvalidOperationException($"Voice '{voice}' is not present in the loaded voice pack.");
        }

        var tokens = Tokenizer.Tokenize(text, KokoroLanguages.EspeakVoiceFor(langCode));
        if (tokens.Length == 0)
        {
            return [];
        }

        if (tokens.Length <= KokoroModel.maxTokens)
        {
            return _model.Infer(tokens, style.Features, speed);
        }

        // Past the model's context the tail would be silently trimmed, so split on punctuation and
        // concatenate. The pipeline feeds sentences, so this is a guard rather than the normal path.
        var parts = SegmentationSystem.SplitToSegments(tokens, _segmentation)
            .Select(segment => _model.Infer(segment, style.Features, speed))
            .ToList();

        var audio = new float[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(audio, offset);
            offset += part.Length;
        }

        return audio;
    }

    public void Dispose() => _model.Dispose();
}
