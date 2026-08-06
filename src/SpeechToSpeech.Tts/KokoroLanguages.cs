namespace SpeechToSpeech.Tts;

/// <summary>
/// Language and voice tables for Kokoro. Kokoro identifies a language by a single letter, so
/// transcription language codes have to be mapped before a voice can be picked.
/// </summary>
public static class KokoroLanguages
{
    /// <summary>Whisper/langdetect codes to Kokoro language letters.</summary>
    public static readonly IReadOnlyDictionary<string, string> WhisperToKokoro =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "b", // British English
            ["ja"] = "j",
            ["zh"] = "z",
            ["fr"] = "f",
            ["es"] = "e",
            ["it"] = "i",
            ["pt"] = "p",
            ["hi"] = "h",
            // Kokoro has no voices for these, so they fall back to British English.
            ["de"] = "b",
            ["nl"] = "b",
            ["pl"] = "b",
            ["ru"] = "b",
            ["uk"] = "b",
        };

    /// <summary>Default (native-sounding) voice per Kokoro language letter.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultVoices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "af_heart",  // American English female
            ["b"] = "bm_fable",  // British English male
            ["e"] = "ef_dora",   // Spanish female
            ["f"] = "ff_siwis",  // French female
            ["h"] = "hf_alpha",  // Hindi female
            ["i"] = "if_sara",   // Italian female
            ["j"] = "jf_alpha",  // Japanese female
            ["p"] = "pf_dora",   // Portuguese female
            ["z"] = "zf_xiaobei", // Chinese female
        };

    /// <summary>espeak-ng voice used to phonemize text for a Kokoro language letter.</summary>
    public static readonly IReadOnlyDictionary<string, string> EspeakVoices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "en-us",
            ["b"] = "en-gb",
            ["e"] = "es",
            ["f"] = "fr-fr",
            ["h"] = "hi",
            ["i"] = "it",
            ["j"] = "ja",
            ["p"] = "pt-br",
            ["z"] = "cmn",
        };

    public static string EspeakVoiceFor(string langCode) =>
        EspeakVoices.TryGetValue(langCode, out var voice) ? voice : "en-gb";
}
