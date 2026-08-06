using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Pipeline;

/// <summary>
/// Wraps each final transcript in <see cref="LlmConfig.UserTemplate"/> before the notifier commits
/// it to the chat history.
/// </summary>
/// <remarks>
/// The template is applied here rather than in the system prompt because it belongs to the user
/// turn: it has to be re-applied per utterance, and it must land in the history exactly as the model
/// saw it. Partial transcripts pass through untouched — they are only echoed to the client and never
/// reach the model.
/// </remarks>
public sealed class UserTemplateHandler(
    CancellationTokenSource stopSource,
    PipelineQueue<IPipelineItem> queueIn,
    PipelineQueue<IPipelineItem> queueOut,
    string template,
    ILogger<UserTemplateHandler>? logger = null)
    : BaseHandler<PipelineMessage, PipelineMessage>(stopSource, queueIn, queueOut, logger)
{
    public override IEnumerable<PipelineMessage> Process(PipelineMessage input)
    {
        if (input is not Transcription transcription || string.IsNullOrWhiteSpace(transcription.Text))
        {
            yield return input;
            yield break;
        }

        var rendered = template.Replace(
            LlmConfig.TranscriptPlaceholder, transcription.Text, StringComparison.Ordinal);

        Logger.LogDebug("Applied user template: {Text}", rendered);

        yield return new Transcription
        {
            Text = rendered,
            LanguageCode = transcription.LanguageCode,
            SpeechStoppedAtSeconds = transcription.SpeechStoppedAtSeconds,
            TurnId = transcription.TurnId,
            TurnRevision = transcription.TurnRevision,
        };
    }
}
