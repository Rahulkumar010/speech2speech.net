using Microsoft.Extensions.Logging;
using SpeechToSpeech.Core;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Llm;

/// <summary>
/// Intercepts LLM output to publish assistant text and tool calls on the text side-channel and
/// forward clean text to TTS.
/// </summary>
/// <remarks>
/// Input is <see cref="LlmResponseChunk"/>, <see cref="TokenUsage"/> or <see cref="EndOfResponse"/>;
/// output is <see cref="TtsInput"/> or <see cref="EndOfResponse"/>.
/// </remarks>
public sealed class LmOutputProcessor(
    CancellationTokenSource stopSource,
    PipelineQueue<IPipelineItem> queueIn,
    PipelineQueue<IPipelineItem> queueOut,
    PipelineQueue<IPipelineItem>? textOutputQueue = null,
    SpeculativeTurnTracker? speculativeTurns = null,
    ILogger<LmOutputProcessor>? logger = null)
    : BaseHandler<PipelineMessage, PipelineMessage>(stopSource, queueIn, queueOut, logger)
{
    public override IEnumerable<PipelineMessage> Process(PipelineMessage input)
    {
        switch (input)
        {
            case TokenUsage usage:
                if (!TurnOutputAllowed(usage))
                {
                    Logger.LogDebug(
                        "Dropping stale token usage for turn={TurnId} rev={Revision}",
                        usage.TurnId,
                        usage.TurnRevision);
                    yield break;
                }

                textOutputQueue?.Put(new TokenUsageEvent
                {
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    TurnId = usage.TurnId,
                    TurnRevision = usage.TurnRevision,
                });
                yield break;

            case EndOfResponse end:
                if (!TurnOutputAllowed(end))
                {
                    Logger.LogDebug(
                        "Dropping stale end-of-response for turn={TurnId} rev={Revision}",
                        end.TurnId,
                        end.TurnRevision);
                    yield break;
                }

                // A failed generation (e.g. invalid out-of-band input) closes the response as
                // "failed" via the text side-channel, then still emits the normal EndOfResponse so
                // the audio path re-enables listening and releases the slot.
                if (end.Error is { Length: > 0 } && textOutputQueue is not null)
                {
                    textOutputQueue.Put(new ResponseFailedEvent
                    {
                        Message = end.Error,
                        TurnId = end.TurnId,
                        TurnRevision = end.TurnRevision,
                    });
                }

                yield return new EndOfResponse
                {
                    TurnId = end.TurnId,
                    TurnRevision = end.TurnRevision,
                    CancelGeneration = end.CancelGeneration,
                };
                yield break;

            case LlmResponseChunk chunk:
                foreach (var output in ProcessChunk(chunk))
                {
                    yield return output;
                }

                yield break;

            default:
                Logger.LogWarning("LmOutputProcessor received unexpected type: {Type}", input.GetType());
                yield break;
        }
    }

    private IEnumerable<PipelineMessage> ProcessChunk(LlmResponseChunk chunk)
    {
        if (!TurnOutputAllowed(chunk))
        {
            Logger.LogDebug(
                "Dropping stale LLM chunk for turn={TurnId} rev={Revision}",
                chunk.TurnId,
                chunk.TurnRevision);
            yield break;
        }

        Logger.LogDebug("LM processor: text='{Text}', tools={ToolCount}", chunk.Text, chunk.Tools.Count);

        if (textOutputQueue is not null)
        {
            var textEvent = new AssistantTextEvent
            {
                Text = chunk.Text,
                Tools = chunk.Tools,
                TurnId = chunk.TurnId,
                TurnRevision = chunk.TurnRevision,
                CancelGeneration = chunk.CancelGeneration,
            };

            if (chunk.Tools.Count > 0)
            {
                Logger.LogInformation(
                    "Sending to clients: text='{Text}', tools={Tools}",
                    chunk.Text,
                    string.Join(", ", chunk.Tools.Select(tool => tool.Name)));
            }
            else
            {
                Logger.LogDebug("Sending to clients: text='{Text}' (no tools)", chunk.Text);
            }

            textOutputQueue.Put(textEvent);
        }

        if (!string.IsNullOrEmpty(chunk.Text) && ResponseSemantics.WantsAudio(chunk.Response))
        {
            Logger.LogDebug("Forwarding to TTS: '{Text}'", chunk.Text);
            yield return new TtsInput
            {
                Text = chunk.Text,
                LanguageCode = chunk.LanguageCode,
                RuntimeConfig = chunk.RuntimeConfig,
                Response = chunk.Response,
                TurnId = chunk.TurnId,
                TurnRevision = chunk.TurnRevision,
                SpeechStoppedAtSeconds = chunk.SpeechStoppedAtSeconds,
                CancelGeneration = chunk.CancelGeneration,
            };
        }
    }

    private bool TurnOutputAllowed(PipelineMessage message) =>
        speculativeTurns is null
        || speculativeTurns.IsLatestAfterReopenGrace(message.TurnId, message.TurnRevision);
}
