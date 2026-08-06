using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Realtime;

namespace SpeechToSpeech.Stt;

/// <summary>
/// Sits between STT and the LLM.
/// </summary>
/// <remarks>
/// In realtime mode (no <see cref="RuntimeConfig"/>) it only publishes transcription events on the
/// text output queue and yields nothing — the realtime service builds the response request itself.
/// In the other pipeline modes it appends the user message to the chat and yields a
/// <see cref="GenerateResponseRequest"/> so the LLM handler always sees the same input type.
/// </remarks>
public sealed class TranscriptionNotifier(
    CancellationTokenSource stopSource,
    PipelineQueue<IPipelineItem> queueIn,
    PipelineQueue<IPipelineItem> queueOut,
    PipelineQueue<IPipelineItem>? textOutputQueue = null,
    RuntimeConfig? runtimeConfig = null,
    ManualResetEventSlim? shouldListen = null,
    ILogger<TranscriptionNotifier>? logger = null)
    : BaseHandler<PipelineMessage, PipelineMessage>(stopSource, queueIn, queueOut, logger)
{
    public override IEnumerable<PipelineMessage> Process(PipelineMessage input)
    {
        if (input is PartialTranscription partial)
        {
            if (textOutputQueue is not null && !string.IsNullOrEmpty(partial.Text))
            {
                textOutputQueue.Put(new PartialTranscriptionEvent
                {
                    Delta = partial.Text,
                    TurnId = partial.TurnId,
                    TurnRevision = partial.TurnRevision,
                });
                Logger.LogDebug("Partial transcription: {Text}", Truncate(partial.Text, 80));
            }

            yield break;
        }

        if (input is not Transcription transcription)
        {
            yield break;
        }

        var transcript = transcription.Text ?? string.Empty;

        // Always close the client-visible transcription item: an empty final result must not trigger
        // the LLM, but clients may already have received partial deltas and still need the completion.
        textOutputQueue?.Put(new TranscriptionCompletedEvent
        {
            Transcript = transcript,
            LanguageCode = transcription.LanguageCode,
            TurnId = transcription.TurnId,
            TurnRevision = transcription.TurnRevision,
            SpeechStoppedAtSeconds = transcription.SpeechStoppedAtSeconds,
        });

        if (transcript.Length == 0)
        {
            Logger.LogDebug("Transcription completed with empty transcript");
            if (shouldListen is not null)
            {
                shouldListen.Set();
                Logger.LogDebug("Empty transcription completed; listening re-enabled");
            }

            yield break;
        }

        if (transcription.LanguageCode is { } languageCode)
        {
            Logger.LogInformation(
                "Transcription completed (language={Language}): {Transcript}",
                languageCode,
                transcript);
        }
        else
        {
            Logger.LogInformation("Transcription completed: {Transcript}", transcript);
        }

        if (runtimeConfig is null)
        {
            yield break;
        }

        runtimeConfig.Chat.AddItem(ChatMessages.MakeUserMessage(transcript));
        yield return new GenerateResponseRequest
        {
            RuntimeConfig = runtimeConfig,
            LanguageCode = transcription.LanguageCode,
            TurnId = transcription.TurnId,
            TurnRevision = transcription.TurnRevision,
            SpeechStoppedAtSeconds = transcription.SpeechStoppedAtSeconds,
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
