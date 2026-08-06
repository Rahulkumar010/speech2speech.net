using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Core.Utils;

public static class Ids
{
    public static string Generate(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

public static class ResponseSemantics
{
    /// <summary>
    /// Whether a response should produce audio (and audio events) rather than text only. Mirrors
    /// the OpenAI realtime semantics for <c>output_modalities</c>: an absent or empty value, or an
    /// explicit <c>"audio"</c> entry, means audio; a non-empty list without <c>"audio"</c> (e.g.
    /// <c>["text"]</c>) means text only.
    /// </summary>
    public static bool WantsAudio(ResponseCreateParams? response)
    {
        if (response is null)
        {
            return true;
        }

        var modalities = response.OutputModalities;
        return modalities is null || modalities.Count == 0 || modalities.Contains("audio");
    }

    /// <summary>
    /// Whether a response is out-of-band (<c>conversation="none"</c>). Out-of-band responses run
    /// against a temporary context and are never threaded into the default conversation.
    /// </summary>
    public static bool IsOutOfBand(ResponseCreateParams? response) => response?.Conversation == "none";
}

public static class AudioConvert
{
    /// <summary>Converts interleaved little-endian 16-bit PCM to normalized floats.</summary>
    public static float[] Int16BytesToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (short)(pcm[(i * 2) + 1] << 8 | pcm[i * 2]);
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    /// <summary>Converts normalized floats to interleaved little-endian 16-bit PCM.</summary>
    public static byte[] FloatToInt16Bytes(ReadOnlySpan<float> samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            var sample = (short)scaled;
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>
    /// Linear resampling between two rates. Adequate for the 16 kHz ↔ 24 kHz conversions the
    /// pipeline needs between the VAD/STT and TTS/output sample rates.
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || samples.Length == 0)
        {
            return samples.ToArray();
        }

        var ratio = (double)targetRate / sourceRate;
        var length = (int)(samples.Length * ratio);
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i / ratio;
            var left = (int)position;
            var right = Math.Min(left + 1, samples.Length - 1);
            var fraction = (float)(position - left);
            output[i] = (samples[left] * (1 - fraction)) + (samples[right] * fraction);
        }

        return output;
    }
}

/// <summary>
/// Linear resampler that carries its interpolation state across calls, for rate conversion of a
/// stream that arrives in blocks.
/// </summary>
/// <remarks>
/// Calling <see cref="AudioConvert.Resample"/> once per block is not the same operation: each block
/// restarts the interpolation at phase zero and clamps its right-hand neighbour to its own last
/// sample, so every block boundary gets a small discontinuity and the output length drifts by the
/// truncated fraction. At 24 kHz to 16 kHz with 512-sample blocks that is an audible tick roughly
/// every 21 ms. This type keeps the trailing sample and the fractional read position, so the result
/// is bit-identical to resampling the whole utterance at once.
/// </remarks>
public sealed class StreamingResampler(int sourceRate, int targetRate)
{
    private readonly double _step = (double)sourceRate / targetRate;

    /// <summary>Last input sample of the previous block, used when a read position lands before 0.</summary>
    private float _previous;

    /// <summary>Next read position, relative to the start of the block being processed.</summary>
    private double _position;

    public bool IsPassThrough => sourceRate == targetRate;

    /// <summary>Resamples one block, continuing from where the previous block left off.</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        if (IsPassThrough || input.Length == 0)
        {
            return input.ToArray();
        }

        var output = new List<float>((int)(input.Length / _step) + 2);

        while (_position <= input.Length - 1)
        {
            var left = (int)Math.Floor(_position);
            var fraction = (float)(_position - left);
            var a = left < 0 ? _previous : input[left];

            // Only reachable when the position lands exactly on the last sample, where the fraction
            // is zero and the right-hand neighbour carries no weight.
            var b = left + 1 < input.Length ? input[left + 1] : input[left];
            output.Add((a * (1 - fraction)) + (b * fraction));
            _position += _step;
        }

        _previous = input[^1];

        // Rebase into the next block. The loop exit guarantees this lands in (-1, step].
        _position -= input.Length;

        return [.. output];
    }

    /// <summary>Clears the carried state so the next block starts a fresh utterance.</summary>
    public void Reset()
    {
        _previous = 0f;
        _position = 0d;
    }
}
