namespace ManualForge.Core.Classification;

/// <summary>Token-level statistics for one page of an existing text layer.</summary>
public sealed record PageTextMetrics(
    int PageNumber,
    int AlphanumericCount,
    int TokenCount,
    int PlausibleTokenCount,
    int WordLikeTokenCount,
    int CommonWordCount)
{
    /// <summary>
    /// Share of tokens that look like a word or a part number rather than OCR confetti. This is
    /// the bad-OCR detector: garbled text produces tokens full of stray punctuation and single
    /// letters, and this ratio collapses.
    /// </summary>
    public double PlausibleTokenRatio => TokenCount == 0 ? 0 : PlausibleTokenCount / (double)TokenCount;

    /// <summary>
    /// Share of purely alphabetic tokens that are common English words. This is an
    /// English-prose detector, *not* a quality detector. A parts cross-reference or a multilingual
    /// manual scores near zero with flawless text, so it must never condemn a file on its own.
    /// </summary>
    public double CommonWordShare => WordLikeTokenCount == 0 ? 0 : CommonWordCount / (double)WordLikeTokenCount;

    public static PageTextMetrics Empty(int pageNumber) => new(pageNumber, 0, 0, 0, 0, 0);
}

/// <summary>
/// Turns a page's extracted words into <see cref="PageTextMetrics"/>.
/// </summary>
/// <remarks>
/// Words must come from a real word-segmenting extractor, never from a raw concatenation of
/// glyphs. Many PDFs position words instead of emitting space characters, so their raw text comes
/// back as one unbroken run — measured that way, a perfectly good manual scores zero on every
/// word statistic. That mistake reclassified 22 files and nearly 4,000 pages of this corpus as
/// needing re-OCR when nothing was wrong with them.
/// </remarks>
public static class TextMetricsCalculator
{
    private static readonly char[] TrimCharacters =
        ['-', '_', '"', '\'', '*', '.', ',', ';', ':', '(', ')', '[', ']', '|', '/', '\\'];

    public static PageTextMetrics Measure(int pageNumber, IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var alphanumeric = 0;
        var tokens = 0;
        var plausible = 0;
        var wordLike = 0;
        var common = 0;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
                continue;

            foreach (var c in word)
            {
                if (char.IsLetterOrDigit(c))
                    alphanumeric++;
            }

            var token = word.Trim(TrimCharacters);
            if (token.Length < 2)
                continue;

            tokens++;

            var letters = 0;
            var digits = 0;
            var separators = 0;
            foreach (var c in token)
            {
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
                else if (IsStructuralSeparator(c)) separators++;
            }
            var junk = token.Length - letters - digits - separators;

            // A plausible token is mostly letters or digits, joined by the separators technical
            // writing legitimately uses, with at most one genuinely stray character. So
            // "frequency", "59401A", "08340-60019" and "SENSe:FREQuency:STARt" all qualify, while
            // "l;'~4" does not.
            //
            // Treating those separators as structure rather than noise is essential for this
            // corpus. Counting the colons in a SCPI mnemonic as junk marks every programming
            // manual as garbled, and sends thousands of pages of perfectly good text back through
            // OCR for nothing.
            if (junk <= 1 && (letters >= 2 || digits >= 2))
                plausible++;

            if (letters == token.Length)
            {
                wordLike++;
                if (CommonWords.Contains(token))
                    common++;
            }
        }

        return new PageTextMetrics(pageNumber, alphanumeric, tokens, plausible, wordLike, common);
    }

    /// <summary>
    /// Characters that join parts of a technical token rather than corrupting it: SCPI command
    /// separators, part-number and model-number hyphens, decimal points, unit slashes.
    /// </summary>
    private static bool IsStructuralSeparator(char c)
        => c is ':' or '-' or '_' or '.' or '/' or '+';
}

/// <summary>
/// Common English words, plus the vocabulary that dominates test-equipment manuals. The domain
/// terms are here deliberately: a service manual whose prose is "Set the FREQUENCY control fully
/// clockwise and observe the output on the analyzer" is perfectly good English for this corpus,
/// and a general word list would score it as gibberish.
/// </summary>
public static class CommonWords
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // General high-frequency English.
        "the", "of", "and", "to", "a", "in", "is", "it", "that", "was", "for", "on", "are", "with",
        "as", "his", "they", "be", "at", "one", "have", "this", "from", "or", "had", "by", "not",
        "but", "what", "some", "we", "can", "out", "other", "were", "all", "there", "when", "up",
        "use", "your", "how", "an", "each", "she", "which", "do", "their", "time", "if", "will",
        "way", "about", "many", "then", "them", "would", "write", "like", "so", "these", "her",
        "long", "make", "thing", "see", "him", "two", "has", "look", "more", "day", "could", "go",
        "come", "did", "my", "no", "most", "number", "who", "over", "know", "than", "call",
        "first", "people", "may", "down", "side", "been", "now", "find", "any", "new", "work",
        "part", "take", "get", "place", "made", "live", "where", "after", "back", "little", "only",
        "round", "man", "year", "came", "show", "every", "good", "me", "give", "our", "under",
        "name", "very", "through", "just", "form", "much", "before", "must", "also", "such",
        "same", "here", "should", "because", "between", "both", "during", "into", "while", "being",
        "used", "using", "shown", "should", "within", "above", "below", "each", "either", "its",
        "note", "see", "when", "with", "without", "than", "then", "thus", "type", "values",

        // Vocabulary specific to this corpus.
        "figure", "table", "section", "page", "chapter", "appendix", "manual", "instrument",
        "voltage", "current", "signal", "output", "input", "frequency", "switch", "circuit",
        "service", "operation", "operating", "meter", "range", "test", "adjust", "adjustment",
        "control", "power", "supply", "amplifier", "board", "assembly", "connector", "cable",
        "panel", "front", "rear", "display", "resistor", "capacitor", "transistor", "diode",
        "check", "replace", "remove", "install", "connect", "set", "press", "turn", "select",
        "reading", "measurement", "calibration", "specification", "performance", "procedure",
        "reference", "level", "gain", "phase", "noise", "filter", "mode", "data", "error",
        "channel", "terminal", "ground", "cover", "screw", "refer", "step", "following",
    };

    public static bool Contains(string token) => Words.Contains(token);

    public static int Count => Words.Count;
}
