using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core.Configuration;
using SpeechToSpeech.Core.Pipeline;
using Whisper.net;

namespace SpeechToSpeech.Stt;

/// <summary>
/// Transcribes VAD segments with whisper.cpp through Whisper.net. Progressive segments become
/// <see cref="PartialTranscription"/>; final segments become <see cref="Transcription"/>.
/// </summary>
public sealed class WhisperNetSttHandler : BaseSttHandler
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperNetSttHandler(
        CancellationTokenSource stopSource,
        PipelineQueue<IPipelineItem> queueIn,
        PipelineQueue<IPipelineItem> queueOut,
        SttOptions options,
        string modelPath,
        SpeculativeTurnTracker? speculativeTurns = null,
        ILogger<WhisperNetSttHandler>? logger = null)
        : base(stopSource, queueIn, queueOut, speculativeTurns, options.FinalRevisionSettleSeconds, logger)
    {
        _factory = WhisperFactory.FromPath(modelPath);

        // Every VAD segment is an independent utterance, so carrying the previous transcript in as a
        // prompt only invites the model to repeat it.
        var builder = _factory.CreateBuilder().WithNoContext();

        _processor = (string.IsNullOrWhiteSpace(options.Language) || options.Language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(options.Language)).Build();
    }

    public override async IAsyncEnumerable<PipelineMessage> ProcessAsync(
        VadAudio input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = Clock.NowSeconds;
        var (text, language) = await TranscribeAsync(input, cancellationToken).ConfigureAwait(false);

        Logger.LogDebug(
            "Whisper transcribed {Duration:F2}s of audio in {Elapsed:F3}s ({Mode}): {Text}",
            input.Audio.Length / 16000.0,
            Clock.NowSeconds - start,
            input.Mode,
            text);

        if (input.Mode == VadAudioMode.Progressive)
        {
            if (text.Length > 0)
            {
                yield return new PartialTranscription
                {
                    Text = text,
                    TurnId = input.TurnId,
                    TurnRevision = input.TurnRevision,
                };
            }

            yield break;
        }

        yield return new Transcription
        {
            Text = text,
            LanguageCode = language,
            TurnId = input.TurnId,
            TurnRevision = input.TurnRevision,
            SpeechStoppedAtSeconds = input.CreatedAtSeconds,
        };
    }

    protected override void Cleanup()
    {
        _processor.Dispose();
        _factory.Dispose();
    }

    /// <summary>Joins the segments whisper.cpp splits a long utterance into back into one transcript.</summary>
    private async Task<(string Text, string? Language)> TranscribeAsync(
        VadAudio input,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        string? language = null;

#pragma warning disable CA1031 // A failed utterance must not take the pipeline thread down with it.
        try
        {
            await foreach (var segment in _processor
                .ProcessAsync(input.Audio, cancellationToken)
                .ConfigureAwait(false))
            {
                language ??= segment.Language;
                text.Append(segment.Text);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Whisper transcription failed for turn={TurnId}", input.TurnId);
            return (string.Empty, null);
        }
#pragma warning restore CA1031

        return (text.ToString().Trim(), language);
    }
}
