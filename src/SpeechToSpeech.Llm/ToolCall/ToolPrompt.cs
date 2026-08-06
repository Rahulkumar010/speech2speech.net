using System.Text;
using System.Text.RegularExpressions;

namespace SpeechToSpeech.Llm.ToolCall;

/// <summary>
/// Builds the optional system-prompt section that instructs a local LLM to emit tool calls as JSON
/// inside delimited blocks (e.g. <c>&lt;code&gt;{"name": "func", "arguments": {}}&lt;/code&gt;</c>).
/// </summary>
public static class ToolPrompt
{
    public const string EnterCode = "<code>";
    public const string EndCode = "</code>";

    private const string VoiceRules = """
        Rules:
        - You may say one brief natural sentence before the tool call; for slow information tools, briefly say that you will check.
        - For expression/background tools, always speak first. For requested expressions, use a short pattern like "Sure, here's my best <emotion>."; otherwise use a fitting empathetic sentence.
        - Do not mention tags, functions, or tools. Keep prose outside tags brief, and do not claim tool results before a tool result is available.
        - The call must be valid JSON with an "arguments" object. Omit optional arguments instead of sending placeholder values like "random", "none", "", or null.
        - Only one tool call may appear in a response.
        """;

    private const string TextRules = """
        Rules:
        - Call a tool directly when it helps fulfill the request; no preamble sentence is required.
        - Do not mention tags, functions, or tools in your prose, and do not claim tool results before a tool result is available.
        - The call must be valid JSON with an "arguments" object. Omit optional arguments instead of sending placeholder values like "random", "none", "", or null.
        - Only one tool call may appear in a response.
        """;

    /// <summary>
    /// Renders the tool-calling system-prompt section, or an empty string when there are no tools,
    /// so it can be appended unconditionally to a base system prompt.
    /// </summary>
    /// <param name="textOnly">
    /// Use the written-channel variant, which omits the voice "speak first" prose.
    /// </param>
    public static string BuildSystemPrompt(
        IReadOnlyList<FunctionTool> tools,
        string enterCode = EnterCode,
        string endCode = EndCode,
        bool textOnly = false)
    {
        if (tools.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("Available tools, as JSON Schema declarations:\n\n");
        foreach (var tool in tools)
        {
            builder.Append(tool.ToPromptJson()).Append("\n\n");
        }

        builder
            .Append("To call a tool, put exactly one JSON object inside ")
            .Append(enterCode)
            .Append("...")
            .Append(endCode)
            .Append(":\n")
            .Append(enterCode)
            .Append("""{"name": "function_name", "arguments": {"required_arg": "value"}}""")
            .Append(endCode)
            .Append("\n\n")
            .Append(textOnly ? TextRules : VoiceRules);

        return builder.ToString();
    }

    /// <summary>Builds a non-greedy regex matching a single code block, e.g. <c>&lt;code&gt;.*?&lt;/code&gt;</c>.</summary>
    public static string BuildBlockRegex(string enterCode = EnterCode, string endCode = EndCode) =>
        $"{Regex.Escape(enterCode)}.*?{Regex.Escape(endCode)}";
}
