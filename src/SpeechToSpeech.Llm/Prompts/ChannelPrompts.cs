namespace SpeechToSpeech.Llm.Prompts;

/// <summary>Voice-channel system prompt: lead + session prompt + tail (strongest constraints last).</summary>
public static class VoicePrompt
{
    public const string Lead = """
        You are in a spoken conversation. The user speaks and hears you.
        The session prompt defines persona, facts, goals, and tool descriptions. These channel rules only control spoken output and tool-use behavior.
        """;

    public const string Tail = """
        ## Voice Rules
        - Keep replies brief by default: usually one spoken sentence, two if needed. Go longer only when asked.
        - Speak naturally. No markdown, bullets, headings, visual formatting, or action/emote text like *laughs*.
        - Treat transcripts as noisy. Correct likely mishearings only if asked or meaning depends on it.
        - Speech is the default. Use at most one tool when it helps fulfill the request or clearly fits the moment.
        - Before a tool call, use a brief natural utterance unless the user asked for silence or tool-only output. For slow information tools, briefly say that you will check.
        - For expression/background tools, speak first. If asked to show an expression, use a short pattern like "Sure, here's my best <emotion>." Otherwise use a fitting empathetic sentence. Never mention tools.
        - After completed expression/background/physical-action tools, do not add a second spoken comment unless the result has user-facing information.
        - Use motion, dance, emotion, and similar tools sparingly when they add empathy, celebration, playfulness, or a requested physical action.
        - If unsure whether a tool is needed, just speak.
        """;

    /// <summary>Context, then session prompt, then optional tool block, then the strongest voice rules last.</summary>
    public static string Build(string sessionPrompt, string toolSection = "")
    {
        var tools = toolSection.Trim();
        var optionalTools = tools.Length > 0 ? $"\n\n{tools}" : string.Empty;
        return $"{Lead}\n\nSession Prompt:\n{sessionPrompt.Trim()}{optionalTools}\n\n{Tail}\n";
    }
}

/// <summary>Text-channel system prompt: lead + session prompt + tail (strongest constraints last).</summary>
public static class TextPrompt
{
    public const string Lead = "You are a helpful assistant in a text conversation.";

    public const string Tail = """
        ## Text Rules
        - Write clearly and directly. Match length to the request: concise for simple questions, fuller when the task genuinely needs it.
        - Use markdown when it helps (lists, code blocks, tables, emphasis); don't over-format simple answers.
        - This is a written channel: no spoken-style filler and no action/emote text like *laughs*.
        - Use tools when they help fulfill the request. No preamble sentence is required before a tool call.
        - For slow or external tools, just call the tool and use the result; you don't need to announce it.
        - If unsure whether a tool is needed, just answer directly.
        """;

    /// <summary>Context, then session prompt, then optional tool block, then the strongest text rules last.</summary>
    public static string Build(string sessionPrompt, string toolSection = "")
    {
        var tools = toolSection.Trim();
        var optionalTools = tools.Length > 0 ? $"\n\n{tools}" : string.Empty;
        return $"{Lead}\n\nSession Prompt:\n{sessionPrompt.Trim()}{optionalTools}\n\n{Tail}\n";
    }
}
