using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Tts;

/// <summary>
/// Shared plumbing for every TTS backend: speculative-turn gating, voice resolution, fixed-size
/// chunking and mid-utterance cancellation.
/// </summary>
/// <remarks>
/// Input is <see cref="PipelineMessage"/> rather than <see cref="TtsInput"/> because the stage also
/// receives <see cref="EndOfResponse"/>, which it converts into
/// <see cref="SentinelMessage.AudioResponseDone"/> so the sender knows the utterance is complete.
/// Output is <see cref="IPipelineItem"/> for the same reason: audio chunks and that sentinel share
/// one stream.
/// </remarks>
public abstract class BaseTtsHandler : BaseHandler<PipelineMessage, IPipelineItem>
{
    private readonly SpeculativeTurnTracker? _speculativeTurns;

    protected BaseTtsHandler(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        ManualResetEventSlim? shouldListen = null,
        int blockSize = 512,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger? logger = null)
        : base(stopSource, queueIn, queueOut, logger)
    {
        ShouldListen = shouldListen;
        BlockSize = blockSize;
        _speculativeTurns = speculativeTurns;
    }

    protected ManualResetEventSlim? ShouldListen { get; }

    /// <summary>Samples per emitted audio chunk; the last chunk of an utterance is zero-padded.</summary>
    protected int BlockSize { get; }

    /// <summary>Voice currently in use, updated per response from the session/response config.</summary>
    protected string Voice { get; set; } = "bm_fable";

    /// <summary>Kokoro-style single-letter language code currently in use.</summary>
    protected string LangCode { get; set; } = "b";

    public override IEnumerable<IPipelineItem> Process(PipelineMessage input)
    {
        if (input is EndOfResponse endOfResponse)
        {
            if (_speculativeTurns is not null
                && !_speculativeTurns.IsLatestAfterReopenGrace(endOfResponse.TurnId, endOfResponse.TurnRevision))
            {
                yield break;
            }

            yield return SentinelMessage.AudioResponseDone;
            Metrics?.Complete(endOfResponse.TurnId);
            yield break;
        }

        if (input is not TtsInput ttsInput)
        {
            Logger.LogWarning("{Handler}: unexpected input {Tag}", Name, input.Tag);
            yield break;
        }

        if (_speculativeTurns is not null
            && !_speculativeTurns.IsLatestAfterReopenGrace(ttsInput.TurnId, ttsInput.TurnRevision))
        {
            Logger.LogDebug(
                "Dropping stale TTS input for turn={TurnId} rev={Revision}",
                ttsInput.TurnId,
                ttsInput.TurnRevision);
            yield break;
        }

        _speculativeTurns?.Commit(ttsInput.TurnId, ttsInput.TurnRevision);

        var voice = ResolveVoice(ttsInput);
        if (!string.IsNullOrEmpty(voice))
        {
            Voice = voice;
        }

        foreach (var chunk in Synthesize(ttsInput))
        {
            // Audio chunks carry no turn id, so the mark has to be taken here rather than on emit.
            Metrics?.Mark(ttsInput.TurnId, $"{Name}/audio");
            yield return chunk;
        }
    }

    /// <summary>Produces the audio for one text chunk, already split into <see cref="BlockSize"/> frames.</summary>
    protected abstract IEnumerable<AudioChunk> Synthesize(TtsInput input);

    /// <summary>
    /// The per-response voice wins over the session voice, matching the realtime API where
    /// <c>response.create</c> may override <c>session.audio.output.voice</c>.
    /// </summary>
    protected static string? ResolveVoice(TtsInput input)
    {
        var responseVoice = input.Response?.Audio?.Output?.Voice;
        if (!string.IsNullOrEmpty(responseVoice))
        {
            return responseVoice;
        }

        return input.RuntimeConfig?.Session.Audio?.Output?.Voice;
    }

    /// <summary>
    /// Splits a float waveform into fixed-size int16 chunks, stopping early once the generation that
    /// produced this utterance has been superseded by a barge-in.
    /// </summary>
    protected IEnumerable<AudioChunk> ToChunks(float[] samples, uint? generation)
    {
        for (var offset = 0; offset < samples.Length; offset += BlockSize)
        {
            if (generation is { } gen && CancelScope is not null && CancelScope.IsStale(gen))
            {
                Logger.LogInformation("TTS generation cancelled (interruption)");
                yield break;
            }

            var take = Math.Min(BlockSize, samples.Length - offset);
            var block = new float[BlockSize];
            Array.Copy(samples, offset, block, 0, take);
            yield return new AudioChunk(AudioConvert.FloatToInt16Bytes(block));
        }
    }

    /// <summary>Trims leading/trailing silence, which Kokoro emits generously around every utterance.</summary>
    protected static float[] TrimSilence(float[] audio, float threshold = 0.01f, int paddingSamples = 120)
    {
        var start = -1;
        var end = -1;
        for (var i = 0; i < audio.Length; i++)
        {
            if (Math.Abs(audio[i]) > threshold)
            {
                if (start < 0)
                {
                    start = i;
                }

                end = i;
            }
        }

        if (start < 0)
        {
            return audio;
        }

        start = Math.Max(0, start - paddingSamples);
        end = Math.Min(audio.Length - 1, end + paddingSamples);
        return audio[start..(end + 1)];
    }
}
