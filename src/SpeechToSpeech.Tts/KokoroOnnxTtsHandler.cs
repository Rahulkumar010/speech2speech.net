using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Configuration;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Utils;
using SpeechToSpeech.Tts.Kokoro;

namespace SpeechToSpeech.Tts;

/// <summary>
/// Text-to-speech with Kokoro-82M on ONNX Runtime.
/// </summary>
/// <remarks>
/// ONNX Runtime covers every supported target, so there is a single code path. Kokoro renders at
/// 24 kHz and the pipeline runs at 16 kHz, so output is resampled unless the two rates match.
/// </remarks>
[SuppressMessage(
    "Microsoft.Design",
    "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Handler lifetime is the pipeline thread. BaseHandler.Run always calls Cleanup on "
        + "exit, which disposes the session; IDisposable would add a second, unowned release path.")]
public sealed class KokoroOnnxTtsHandler : BaseTtsHandler
{
    private readonly KokoroOnnxModel _model;
    private readonly float _speed;
    private readonly int _outputSampleRate;
    private readonly string _initialVoice;
    private readonly string _initialLangCode;

    public KokoroOnnxTtsHandler(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        TtsOptions options,
        ManualResetEventSlim? shouldListen = null,
        string langCode = "b",
        int blockSize = 512,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger? logger = null)
        : base(stopSource, queueIn, queueOut, shouldListen, blockSize, speculativeTurns, logger)
    {
        _model = new KokoroOnnxModel(options.ModelPath, options.VoicesPath);
        _speed = (float)options.Speed;
        _outputSampleRate = options.OutputSampleRate;

        Voice = options.Voice;
        LangCode = langCode;
        _initialVoice = Voice;
        _initialLangCode = LangCode;

        Warmup();
    }

    private void Warmup()
    {
        Logger.LogInformation("Warming up {Handler}", Name);
        try
        {
            _model.Synthesize("Hello", Voice, LangCode, _speed);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Handler}: warmup failed", Name);
        }

        Logger.LogInformation("{Handler} warmed up", Name);
    }

    protected override IEnumerable<AudioChunk> Synthesize(TtsInput input)
    {
        SwitchLanguageIfNeeded(input.LanguageCode);

        float[] audio;
        try
        {
            audio = _model.Synthesize(input.Text, Voice, LangCode, _speed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Handler}: synthesis failed for '{Text}'", Name, input.Text);
            yield break;
        }

        if (audio.Length == 0)
        {
            yield break;
        }

        audio = TrimSilence(audio);
        if (_outputSampleRate != KokoroOnnxModel.SampleRate)
        {
            audio = AudioConvert.Resample(audio, KokoroOnnxModel.SampleRate, _outputSampleRate);
        }

        foreach (var chunk in ToChunks(audio, input.CancelGeneration))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Follows the detected transcription language, falling back to that language's default voice
    /// because a voice only sounds right for the language it was trained on.
    /// </summary>
    private void SwitchLanguageIfNeeded(string? languageCode)
    {
        if (languageCode is null
            || !KokoroLanguages.WhisperToKokoro.TryGetValue(languageCode, out var newLangCode)
            || newLangCode == LangCode)
        {
            return;
        }

        var newVoice = KokoroLanguages.DefaultVoices.TryGetValue(newLangCode, out var candidate) ? candidate : Voice;
        if (!_model.HasVoice(newVoice))
        {
            Logger.LogWarning("Voice {Voice} is not in the loaded pack; keeping {Current}", newVoice, Voice);
            return;
        }

        Logger.LogInformation(
            "Language change detected: {OldLang} -> {NewLang}, voice: {OldVoice} -> {NewVoice}",
            LangCode,
            newLangCode,
            Voice,
            newVoice);

        LangCode = newLangCode;
        Voice = newVoice;
    }

    protected override void OnSessionEnd()
    {
        Voice = _initialVoice;
        LangCode = _initialLangCode;
        Logger.LogDebug("Kokoro TTS session state reset");
    }

    protected override void Cleanup() => _model.Dispose();
}
