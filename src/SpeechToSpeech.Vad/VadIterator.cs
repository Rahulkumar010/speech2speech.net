namespace SpeechToSpeech.Vad;

/// <summary>
/// Streaming wrapper around a Silero VAD model. Mainly taken from
/// https://github.com/snakers4/silero-vad.
/// </summary>
public sealed class VadIterator
{
    /// <summary>
    /// Hysteresis below <see cref="Threshold"/>: once triggered, a window still counts as speech
    /// while its probability stays within this margin of the trigger threshold.
    /// </summary>
    private const double ReleaseMargin = 0.15;

    private readonly IVadModel _model;
    private readonly List<float[]> _buffer = [];
    private readonly List<float[]> _prefixBuffer = [];
    private readonly LinkedList<float[]> _preSpeechBuffer = [];

    private int _preSpeechSamples;
    private long _currentSample;
    private long _tempEnd;

    /// <param name="model">Preloaded Silero VAD model.</param>
    /// <param name="threshold">
    /// Speech threshold. Silero outputs a speech probability per chunk; probabilities above this
    /// value are considered speech. 0.5 is a reasonable default for most datasets.
    /// </param>
    /// <param name="samplingRate">Silero VAD supports 8000 and 16000 Hz.</param>
    /// <param name="minSilenceDurationMs">
    /// Silence to wait for at the end of a speech chunk before separating it.
    /// </param>
    /// <param name="speechPadMs">
    /// Audio retained before VAD triggers and prepended to the detected speech chunk.
    /// </param>
    public VadIterator(
        IVadModel model,
        double threshold = 0.5,
        int samplingRate = 16000,
        int minSilenceDurationMs = 300,
        int speechPadMs = 30)
    {
        if (samplingRate is not (8000 or 16000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingRate),
                "VadIterator does not support sampling rates other than 8000 and 16000.");
        }

        _model = model;
        Threshold = threshold;
        SamplingRate = samplingRate;
        MinSilenceSamples = (int)(samplingRate * minSilenceDurationMs / 1000.0);
        SpeechPadSamples = (int)(samplingRate * speechPadMs / 1000.0);
        ResetStates();
    }

    public double Threshold { get; set; }

    public int SamplingRate { get; }

    public int MinSilenceSamples { get; set; }

    public int SpeechPadSamples { get; }

    public bool Triggered { get; private set; }

    public int ActiveSpeechSamples { get; private set; }

    public int LastUtteranceActiveSpeechSamples { get; private set; }

    public int BufferedChunkCount => _buffer.Count;

    public void ResetStates()
    {
        _model.ResetStates();
        Triggered = false;
        _tempEnd = 0;
        _currentSample = 0;
        _buffer.Clear();
        _prefixBuffer.Clear();
        ActiveSpeechSamples = 0;
        LastUtteranceActiveSpeechSamples = 0;
        _preSpeechBuffer.Clear();
        _preSpeechSamples = 0;
    }

    /// <summary>Audio accumulated for the current utterance, including the pre-speech pad.</summary>
    public IReadOnlyList<float[]> SpeechBuffer() =>
        _prefixBuffer.Count == 0 ? _buffer : [.. _prefixBuffer, .. _buffer];

    /// <summary>
    /// Feeds one audio window. Returns the complete utterance when speech ends, otherwise null.
    /// </summary>
    public IReadOnlyList<float[]>? Process(float[] chunk)
    {
        var windowSizeSamples = chunk.Length;
        _currentSample += windowSizeSamples;

        var speechProbability = _model.Predict(chunk, SamplingRate);

        if (speechProbability >= Threshold && !Triggered)
        {
            Triggered = true;
            _prefixBuffer.Clear();
            _prefixBuffer.AddRange(_preSpeechBuffer);
            _preSpeechBuffer.Clear();
            _preSpeechSamples = 0;
            _buffer.Add(chunk);
            ActiveSpeechSamples = windowSizeSamples;
            LastUtteranceActiveSpeechSamples = 0;
            return null;
        }

        if (!Triggered)
        {
            RememberPreSpeech(chunk);
            return null;
        }

        _buffer.Add(chunk);

        if (speechProbability >= Threshold - ReleaseMargin)
        {
            ActiveSpeechSamples += windowSizeSamples;
            if (_tempEnd != 0)
            {
                _tempEnd = 0;
            }

            return null;
        }

        if (_tempEnd == 0)
        {
            _tempEnd = _currentSample;
        }

        if (_currentSample - _tempEnd < MinSilenceSamples)
        {
            return null;
        }

        // End of speech: keep the final low-confidence chunks observed before VAD decided the
        // utterance was done.
        _tempEnd = 0;
        Triggered = false;
        var spokenUtterance = SpeechBuffer().ToList();
        LastUtteranceActiveSpeechSamples = ActiveSpeechSamples;
        ActiveSpeechSamples = 0;
        _buffer.Clear();
        _prefixBuffer.Clear();
        return spokenUtterance;
    }

    private void RememberPreSpeech(float[] chunk)
    {
        if (SpeechPadSamples <= 0)
        {
            _preSpeechBuffer.Clear();
            _preSpeechSamples = 0;
            return;
        }

        _preSpeechBuffer.AddLast(chunk);
        _preSpeechSamples += chunk.Length;
        TrimPreSpeechBuffer();
    }

    private void TrimPreSpeechBuffer()
    {
        while (SpeechPadSamples > 0 && _preSpeechBuffer.First is { } first && _preSpeechSamples > SpeechPadSamples)
        {
            var firstSamples = first.Value.Length;
            var excess = _preSpeechSamples - SpeechPadSamples;

            if (excess >= firstSamples)
            {
                _preSpeechBuffer.RemoveFirst();
                _preSpeechSamples -= firstSamples;
                continue;
            }

            first.Value = first.Value[excess..];
            _preSpeechSamples -= excess;
        }
    }
}
