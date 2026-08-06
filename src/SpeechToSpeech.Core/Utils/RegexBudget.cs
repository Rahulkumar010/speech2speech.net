namespace SpeechToSpeech.Core.Utils;

/// <summary>
/// Shared execution budget for every <see cref="System.Text.RegularExpressions.Regex"/> applied to
/// text this process did not author.
/// </summary>
/// <remarks>
/// Language-model output, provider payloads and phonemizer input are all attacker-influenceable in
/// the sense that matters here: a prompt can steer them. An unbounded match on the LLM thread stalls
/// the entire turn, so every pattern that touches such text carries this timeout. It is generous
/// enough that no legitimate input reaches it and short enough that a pathological one is a blip
/// rather than a hang.
/// </remarks>
public static class RegexBudget
{
    /// <summary>Match timeout in milliseconds, for use in <c>[GeneratedRegex]</c> attributes.</summary>
    public const int MatchTimeoutMs = 250;

    /// <summary>Match timeout for regexes constructed at runtime.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(MatchTimeoutMs);
}
