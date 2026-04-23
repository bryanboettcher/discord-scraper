namespace DiscordScraper.Ingestion.Enrichment;

/// <summary>
/// Fast pre-LLM gate. Anything this returns true for is flagged
/// non-substantive without burning a tagging call — the worker still writes
/// an enrichment row so the message isn't re-scanned on every pass, it just
/// skips embedding + Qdrant. False means "let the LLM decide."
/// </summary>
internal static class SubstantivenessFilter
{
    // Single-token acknowledgements that show up everywhere and carry no
    // semantic weight worth indexing. Kept short; the LLM handles edge cases.
    private static readonly HashSet<string> TrivialTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "okk", "kk", "k", "lol", "lmao", "rofl", "haha", "hahaha",
        "yep", "yup", "nope", "nah", "yeah", "yes", "no", "sure", "same", "true", "false",
        "nice", "cool", "oof", "rip", "gg", "wp", "ez", "good", "bad", "fine",
        "thanks", "thx", "ty", "tysm", "yw", "np",
        "+1", "-1", ":)", ":(", ":/", ":D", "xD", "^_^", "o/", "\\o",
    };

    public static bool IsObviouslyTrivial(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;

        var trimmed = content.Trim();
        if (trimmed.Length < 3) return true;

        if (TrivialTokens.Contains(trimmed)) return true;

        // "Has at least one run of 2+ ASCII letters" — filters out pure emoji,
        // pure punctuation, and raw numbers. Anything with real word content
        // passes and goes to the LLM.
        var hasLetterRun = false;
        var runLength = 0;
        foreach (var c in trimmed)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                runLength++;
                if (runLength >= 2) { hasLetterRun = true; break; }
            }
            else
            {
                runLength = 0;
            }
        }

        return !hasLetterRun;
    }
}
