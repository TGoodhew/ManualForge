using System.Text;

namespace ManualForge.Core.Benchmarking;

/// <summary>How far a recognised page is from the truth, at character and word level.</summary>
/// <param name="CharacterErrors">Insertions, deletions and substitutions to turn one into the other.</param>
/// <param name="TruthCharacters">Characters in the ground truth, which is what the rate is a share of.</param>
public sealed record Accuracy(
    int CharacterErrors,
    int TruthCharacters,
    int WordErrors,
    int TruthWords,
    int UnorderedWordErrors = 0)
{
    /// <summary>
    /// Character error rate. Zero is perfect; it can exceed 1 when the engine invents more than it
    /// gets right, which is exactly what a badly deskewed page does.
    /// </summary>
    public double CharacterErrorRate => TruthCharacters == 0 ? 0 : CharacterErrors / (double)TruthCharacters;

    public double WordErrorRate => TruthWords == 0 ? 0 : WordErrors / (double)TruthWords;

    /// <summary>
    /// Word errors ignoring the order the words came out in.
    ///
    /// The difference between this and <see cref="WordErrorRate"/> is reading order, and it needs
    /// separating because the two have nothing to do with each other. A two-column parts table
    /// recognised perfectly but read across the columns instead of down them scores terribly on
    /// ordered edit distance while every character is right; the fix for that is layout analysis,
    /// not a better recogniser, and a single number would send you after the wrong one.
    /// </summary>
    public double UnorderedWordErrorRate => TruthWords == 0 ? 0 : UnorderedWordErrors / (double)TruthWords;

    /// <summary>
    /// How much of the ordered word error is explained by ordering alone. Near 1 means the words
    /// were right and the sequence was not.
    /// </summary>
    public double ShareFromOrdering =>
        WordErrors == 0 ? 0 : Math.Clamp((WordErrors - UnorderedWordErrors) / (double)WordErrors, 0, 1);

    public static Accuracy operator +(Accuracy a, Accuracy b) => new(
        a.CharacterErrors + b.CharacterErrors,
        a.TruthCharacters + b.TruthCharacters,
        a.WordErrors + b.WordErrors,
        a.TruthWords + b.TruthWords,
        a.UnorderedWordErrors + b.UnorderedWordErrors);

    public static readonly Accuracy Zero = new(0, 0, 0, 0);
}

/// <summary>
/// Character and word error rates against hand-corrected text.
///
/// <para>
/// The measurement has to be edit distance rather than anything cheaper. Counting matching
/// characters in order would call a page with one inserted character near-total garbage, because
/// everything after the insertion is offset; and the errors that matter here are exactly
/// insertions and deletions - a speck read as a comma, a broken serif dropping a letter.
/// </para>
///
/// <para>
/// Normalisation is deliberately light. Case and line breaks are collapsed because neither is what
/// OCR quality means, but punctuation stays: a manual that renders <c>1N21</c> as <c>1N2l</c> or
/// <c>±0.5</c> as <c>+0.5</c> has made the kind of mistake this is meant to catch, and normalising
/// it away would flatter the result.
/// </para>
/// </summary>
public static class ErrorRate
{
    /// <summary>Compares recognised text against the truth.</summary>
    public static Accuracy Measure(string? truth, string? recognised)
    {
        var truthChars = Normalise(truth);
        var actualChars = Normalise(recognised);

        var truthWords = Words(truthChars);
        var actualWords = Words(actualChars);

        return new Accuracy(
            Distance(truthChars.AsSpan(), actualChars.AsSpan()),
            truthChars.Length,
            Distance<string>(truthWords, actualWords, StringComparer.Ordinal),
            truthWords.Length,
            UnorderedErrors(truthWords, actualWords));
    }

    /// <summary>
    /// Collapses whitespace and case, and nothing else.
    ///
    /// Line breaks are not a property of the text, they are a property of the page, and an engine
    /// should not be marked down for wrapping differently. Everything else stays.
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Words in the truth that are missing from the output, plus words invented that are not in the
    /// truth: the symmetric difference of the two multisets. Order does not enter into it.
    /// </summary>
    private static int UnorderedErrors(string[] truth, string[] actual)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var word in truth)
            counts[word] = counts.GetValueOrDefault(word) + 1;

        var invented = 0;
        foreach (var word in actual)
        {
            if (counts.TryGetValue(word, out var remaining) && remaining > 0)
                counts[word] = remaining - 1;
            else
                invented++;
        }

        var missing = counts.Values.Where(v => v > 0).Sum();

        // A substitution is one missing word and one invented one; counting both would report it
        // twice, so pair them up and count the surplus once.
        return Math.Max(missing, invented);
    }

    private static string[] Words(string normalised) =>
        normalised.Length == 0 ? [] : normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Levenshtein distance over characters, in two rows rather than a full matrix.
    ///
    /// A page of a service manual runs to a few thousand characters, so the full matrix would be
    /// millions of cells per page and tens of millions across a benchmark run. Two rows is the same
    /// answer for a fraction of the memory.
    /// </summary>
    public static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ai = a[i - 1];

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (ai == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>The same, over any sequence. Used for words.</summary>
    public static int Distance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T> comparer)
    {
        if (a.Count == 0) return b.Count;
        if (b.Count == 0) return a.Count;

        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];

        for (var j = 0; j <= b.Count; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Count; j++)
            {
                var substitution = previous[j - 1] + (comparer.Equals(a[i - 1], b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }
}
