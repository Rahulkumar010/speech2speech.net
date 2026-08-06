using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeechToSpeech.Core.Conversation;
using SpeechToSpeech.Core.Realtime;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Llm.Prompts;

/// <summary>Backend-agnostic generation interface: (systemPrompt, userPrompt) to response text.</summary>
public delegate Task<string> CompactGenerateFn(string systemPrompt, string userPrompt);

/// <summary>
/// Prompt template and factory for conversation compaction (history summarization).
/// </summary>
/// <remarks>
/// Compaction reduces an unbounded conversation history to a tight user/assistant summary pair,
/// letting the pipeline run indefinitely without exhausting the context window.
/// <see cref="BuildCompactor"/> returns a <see cref="Chat.CompactFn"/> for <c>Chat.TrimIfNeeded</c>.
/// </remarks>
public static partial class CompactionPrompt
{
    public const string SystemPrompt = """
        You are a conversation memory compressor for a real-time voice AI assistant.

        Your task: read a multi-turn conversation and produce a dense summary so the
        assistant can continue naturally, as if it remembers everything that was said.

        Output a single JSON object with exactly two string fields:
          "user_summary"      — 1–5 sentences capturing what the user has been asking
                                about, any preferences or constraints they have stated,
                                and where the conversation stands from their perspective.
          "assistant_summary" — 1–5 sentences capturing what the assistant has
                                explained, decided, or done (including tool calls and
                                their results), plus any open questions or commitments.

        Rules:
        - Be information-dense: preserve names, numbers, file paths, error messages, and
          other specifics that would be needed to continue the conversation correctly.
        - Omit small-talk and filler that carries no forward context.
        - Write in third person, past tense
          (e.g. "The user asked about…", "The assistant provided…").
        - Emit only the JSON object — no markdown, no code fences, no extra keys.
        """;

    public const string UserTemplate = """
        Summarize the following conversation.  Return only the JSON object.

        --- CONVERSATION START ---
        {0}
        --- CONVERSATION END ---
        """;

    /// <summary>Parsed once; <see cref="UserTemplate"/> stays public as the readable source of truth.</summary>
    private static readonly CompositeFormat UserFormat = CompositeFormat.Parse(UserTemplate);

    /// <summary>
    /// Returns a <see cref="Chat.CompactFn"/> that summarizes history using <paramref name="generate"/>,
    /// the only model-specific dependency. The returned callable is safe to call from a background thread.
    /// </summary>
    public static Chat.CompactFn BuildCompactor(CompactGenerateFn generate, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        return async snapshot =>
        {
            var transcript = RenderTranscript(snapshot);
            if (transcript.Trim().Length == 0)
            {
                logger.LogWarning("Compaction called with an empty transcript; returning empty summaries");
                return new CompactionResult(string.Empty, string.Empty);
            }

            var rawText = await generate(
                SystemPrompt,
                string.Format(CultureInfo.InvariantCulture, UserFormat, transcript)).ConfigureAwait(false);
            using var document = ExtractJson(rawText);

            var userSummary = ReadString(document.RootElement, "user_summary");
            var assistantSummary = ReadString(document.RootElement, "assistant_summary");

            if (userSummary.Length == 0 || assistantSummary.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Compaction response missing required fields. Got: {document.RootElement.GetRawText()}");
            }

            logger.LogDebug(
                "Compaction complete. user={UserChars} chars  assistant={AssistantChars} chars",
                userSummary.Length,
                assistantSummary.Length);

            return new CompactionResult(userSummary, assistantSummary);
        };
    }

    /// <summary>Renders a conversation snapshot as a readable plain-text transcript.</summary>
    private static string RenderTranscript(IReadOnlyList<ConversationItem> snapshot)
    {
        var lines = new List<string>();

        foreach (var item in snapshot)
        {
            switch (item.ItemType)
            {
                case ConversationItemType.FunctionCall:
                    lines.Add($"[Tool call: {item.Name}({item.Arguments})]");
                    continue;

                case ConversationItemType.FunctionCallOutput:
                    lines.Add($"[Tool result: {item.Output}]");
                    continue;
            }

            if (item.Role == ConversationRole.System)
            {
                continue;
            }

            var text = item.TextContent().Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var label = item.Role switch
            {
                ConversationRole.User => "User",
                ConversationRole.Assistant => "Assistant",
                _ => "Unknown",
            };

            lines.Add($"{label}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>Extracts the first JSON object from <paramref name="text"/>, stripping markdown fences.</summary>
    private static JsonDocument ExtractJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // Fall through to the fence and brace-scan fallbacks.
        }

        var match = JsonBlockPattern().Match(text);
        if (match.Success)
        {
            return JsonDocument.Parse(match.Groups[1].Value);
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start != -1 && end > start)
        {
            return JsonDocument.Parse(text[start..(end + 1)]);
        }

        throw new InvalidOperationException($"No JSON object found in compaction response: '{text}'");
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline, RegexBudget.MatchTimeoutMs)]
    private static partial Regex JsonBlockPattern();
}
