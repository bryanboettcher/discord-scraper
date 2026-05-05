namespace DiscordScraper.Enrichment.Analysis;

/// <summary>
/// Fast pre-enrichment gate. Returns true when the message content is obviously
/// not worth indexing, saving downstream Ollama/vector calls.
///
/// Ported from DiscordScraper.Ingestion.Enrichment.SubstantivenessFilter;
/// the Ingestion copy is retained until the Ingestion project is fully retired.
/// </summary>
internal static class SubstantivenessFilter
{
    // Single-token acknowledgements and social tokens that appear constantly and carry
    // no semantic weight worth indexing. Kept intentionally short; edge cases go to LLM.
    private static readonly HashSet<string> TrivialTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "okk", "kk", "k", "lol", "lmao", "rofl", "haha", "hahaha",
        "yep", "yup", "nope", "nah", "yeah", "yes", "no", "sure", "same", "true", "false",
        "nice", "cool", "oof", "rip", "gg", "wp", "ez", "good", "bad", "fine",
        "thanks", "thx", "ty", "tysm", "yw", "np",
        "+1", "-1", ":)", ":(", ":/", ":D", "xD", "^_^", "o/", "\\o",
    };

    /// <summary>
    /// Returns true when the content is obviously non-substantive and should be excluded
    /// from enrichment. False means "not obviously trivial — pass it through."
    /// </summary>
    public static bool IsObviouslyTrivial(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;

        var trimmed = content.Trim();
        if (trimmed.Length < 3) return true;

        if (TrivialTokens.Contains(trimmed)) return true;

        // Require at least one run of 2+ ASCII letters. This gates out pure emoji,
        // pure punctuation, raw numbers, and sticker-only messages.
        var runLength = 0;
        foreach (var c in trimmed)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                runLength++;
                if (runLength >= 2) return false;
            }
            else
            {
                runLength = 0;
            }
        }

        return true;
    }
}
