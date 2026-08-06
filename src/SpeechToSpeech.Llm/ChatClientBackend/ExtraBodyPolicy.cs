using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace SpeechToSpeech.Llm.ChatClientBackend;

/// <summary>
/// Merges provider-specific keys into the outgoing request body that the OpenAI SDK's strongly
/// typed options cannot express.
/// </summary>
/// <remarks>
/// <c>chat_template_kwargs.enable_thinking</c> is understood by vLLM and Qwen builds but has no
/// property on <c>ChatCompletionOptions</c>, and the SDK exposes no public escape hatch for extra
/// body keys in this version. Rewriting the serialised body in a per-call policy is the narrowest
/// way to keep the existing <c>--disable-thinking</c> behaviour working.
/// </remarks>
internal sealed class ExtraBodyPolicy(JsonObject extraBody) : PipelinePolicy
{
    private readonly JsonObject _extraBody = extraBody;

    /// <summary>
    /// Builds the provider-specific extra body used to disable reasoning.
    /// </summary>
    /// <remarks>
    /// Providers differ: vLLM/Qwen honour <c>chat_template_kwargs.enable_thinking=false</c>, while
    /// others ignore it and require <c>reasoning_effort='none'</c>, so a non-empty
    /// <paramref name="reasoningEffort"/> takes precedence. None of this applies to the official
    /// OpenAI server, which rejects unknown body keys.
    /// </remarks>
    public static JsonObject? Build(string? baseUrl, bool disableThinking, string? reasoningEffort)
    {
        if (baseUrl is null || IsOfficialOpenAi(baseUrl))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(reasoningEffort))
        {
            return new JsonObject { ["reasoning_effort"] = reasoningEffort };
        }

        return disableThinking
            ? new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } }
            : null;
    }

    /// <summary>Whether the base URL points at the official OpenAI server, normalising a trailing slash.</summary>
    public static bool IsOfficialOpenAi(string? baseUrl) =>
        baseUrl?.TrimEnd('/') == "https://api.openai.com/v1";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Rewrite(PipelineMessage message)
    {
        if (message?.Request?.Content is not { } content)
        {
            return;
        }

        JsonObject? body;
        try
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            buffer.Position = 0;
            body = JsonNode.Parse(buffer) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or NotSupportedException)
        {
            // A body we cannot read is a body we must not corrupt; send it through untouched.
            return;
        }

        if (body is null)
        {
            return;
        }

        foreach (var (key, value) in _extraBody)
        {
            body[key] = value?.DeepClone();
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
    }
}
