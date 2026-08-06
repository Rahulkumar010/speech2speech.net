using System.Text;
using System.Text.RegularExpressions;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Llm;

public static partial class LlmUtils
{
    /// <summary>
    /// Maps an STT language code to the language name used in the "Always reply in {name}" system
    /// prompt rule. Every language a bundled STT backend can report needs an entry, otherwise the
    /// language prompt silently emits no instruction for it. Names are lowercase because they are
    /// interpolated mid-sentence.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WhisperLanguageToLlmLanguage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "english",
            ["fr"] = "french",
            ["es"] = "spanish",
            ["zh"] = "chinese",
            ["ja"] = "japanese",
            ["ko"] = "korean",
            ["hi"] = "hindi",
            ["de"] = "german",
            ["pt"] = "portuguese",
            ["pl"] = "polish",
            ["it"] = "italian",
            ["nl"] = "dutch",
            ["ru"] = "russian",
            ["uk"] = "ukrainian",
            ["cs"] = "czech",
            ["sk"] = "slovak",
            ["hu"] = "hungarian",
            ["ro"] = "romanian",
            ["bg"] = "bulgarian",
            ["hr"] = "croatian",
            ["sl"] = "slovenian",
            ["sr"] = "serbian",
            ["da"] = "danish",
            ["no"] = "norwegian",
            ["sv"] = "swedish",
            ["fi"] = "finnish",
            ["et"] = "estonian",
            ["lv"] = "latvian",
            ["lt"] = "lithuanian",
        };

    /// <summary>
    /// Keeps only speechable characters: letters, digits, punctuation and whitespace, across all
    /// scripts (english, arabic, chinese, japanese, korean, ...).
    /// </summary>
    public static string RemoveUnspeechable(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201c' or '\u201d' => '"',
                _ => character,
            });
        }

        return UnspeechablePattern().Replace(builder.ToString(), string.Empty);
    }

    /// <summary>
    /// Strips the <c>-auto</c> suffix and resolves the human-readable language name. The name is
    /// non-null when the code (with or without <c>-auto</c>) maps to a known language.
    /// </summary>
    public static (string? Code, string? Name) ResolveAutoLanguage(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return (languageCode, null);
        }

        if (languageCode.EndsWith("-auto", StringComparison.Ordinal))
        {
            languageCode = languageCode[..^5];
        }

        return WhisperLanguageToLlmLanguage.TryGetValue(languageCode, out var name)
            ? (languageCode, name)
            : (languageCode, null);
    }

    [GeneratedRegex(@"[^\w\s.,!?;:'""\-()\/\\@#%&*+=$€£¥₹₽¢\[\]{}<>~`^|…—–\n\r\t]", RegexOptions.None, RegexBudget.MatchTimeoutMs)]
    private static partial Regex UnspeechablePattern();
}
