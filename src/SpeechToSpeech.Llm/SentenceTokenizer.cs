using System.Text.RegularExpressions;
using SpeechToSpeech.Core.Utils;

namespace SpeechToSpeech.Llm;

/// <summary>
/// Splits accumulated text into sentences so the audio path can be flushed to TTS one sentence at a
/// time <c>nltk.sent_tokenize</c>.
/// </summary>
/// <remarks>
/// Deliberately conservative: for space-separated scripts a boundary is only taken when terminal
/// punctuation is followed by whitespace and the preceding token is not a known abbreviation.
/// Over-splitting would send half-sentences to TTS, which is far more audible than under-splitting.
/// Full-width CJK terminators are exempt from the whitespace requirement, because those scripts do
/// not separate sentences with spaces and the rule would otherwise never fire for them.
/// </remarks>
public static partial class SentenceTokenizer
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt",
        "vs", "etc", "e.g", "i.e", "approx", "no", "fig", "al",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    /// <summary>Splits <paramref name="text"/> into sentences, preserving the trailing partial one.</summary>
    public static List<string> Split(string text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return sentences;
        }

        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (!IsTerminator(text[i]))
            {
                continue;
            }

            // CJK scripts do not put whitespace between sentences, so a full-width terminator is a
            // boundary on its own. Requiring trailing whitespace made every CJK reply arrive at TTS
            // as one unsplit block, defeating sentence-at-a-time streaming for those languages.
            var wideTerminator = IsWideTerminator(text[i]);

            var end = i + 1;

            // Absorb repeated terminators ("!?", "...") and any closing quote or bracket.
            while (end < text.Length && (IsTerminator(text[end]) || IsCloser(text[end])))
            {
                end++;
            }

            // A boundary requires trailing whitespace; mid-token dots (URLs, decimals) are not ends.
            if (!wideTerminator && end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                i = end - 1;
                continue;
            }

            var candidate = text[start..end].Trim();
            if (candidate.Length > 0 && !EndsWithAbbreviation(candidate) && !EndsWithInitial(candidate))
            {
                sentences.Add(candidate);
                start = end;
            }

            i = end - 1;
        }

        var remainder = text[start..].Trim();
        if (remainder.Length > 0)
        {
            sentences.Add(remainder);
        }

        return sentences;
    }

    private static bool IsTerminator(char character) =>
        character is '.' or '!' or '?' or '\u3002' or '\uff01' or '\uff1f' or '\u2026';

    /// <summary>Full-width terminators, which stand alone without following whitespace.</summary>
    private static bool IsWideTerminator(char character) =>
        character is '\u3002' or '\uff01' or '\uff1f';

    private static bool IsCloser(char character) =>
        character is '"' or '\'' or ')' or ']' or '\u201d' or '\u2019'
            or '\u300d' or '\u300f' or '\uff09' or '\u3011';

    private static bool EndsWithAbbreviation(string sentence)
    {
        if (!sentence.EndsWith('.'))
        {
            return false;
        }

        var match = LastWordPattern().Match(sentence[..^1]);
        return match.Success && Abbreviations.Contains(match.Value);
    }

    /// <summary>True for a single-letter initial such as the "J." in "J. Smith".</summary>
    private static bool EndsWithInitial(string sentence) =>
        sentence.Length >= 2
        && sentence[^1] == '.'
        && char.IsUpper(sentence[^2])
        && (sentence.Length == 2 || !char.IsLetter(sentence[^3]));

    [GeneratedRegex(@"[\w.]+$", RegexOptions.None, RegexBudget.MatchTimeoutMs)]
    private static partial Regex LastWordPattern();
}
