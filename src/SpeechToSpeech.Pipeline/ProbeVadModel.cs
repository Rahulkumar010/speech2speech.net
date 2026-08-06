using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Vad;

namespace SpeechToSpeech.Pipeline;

/// <summary>Reports peak Silero probability once per second so threshold tuning is not guesswork.</summary>
internal sealed class ProbeVadModel(IVadModel inner, ILogger logger, double threshold) : IVadModel
{
    private double _lastLog;
    private float _peak;
    private int _frames;
    private int _overThreshold;

    public float Predict(ReadOnlySpan<float> chunk, int sampleRate)
    {
        var probability = inner.Predict(chunk, sampleRate);

        _frames++;
        _peak = Math.Max(_peak, probability);
        if (probability >= threshold)
        {
            _overThreshold++;
        }

        var now = Clock.NowSeconds;
        if (now - _lastLog >= 1.0)
        {
            _lastLog = now;
            logger.LogInformation(
                "[silero] frames={Frames} peak={Peak:F3} over{Threshold:F2}={Over}",
                _frames, _peak, threshold, _overThreshold);
            _frames = 0;
            _peak = 0;
            _overThreshold = 0;
        }

        return probability;
    }

    public void ResetStates() => inner.ResetStates();

    public void Dispose() => inner.Dispose();
}
