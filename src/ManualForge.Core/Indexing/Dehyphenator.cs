using System.Text;

namespace ManualForge.Core.Indexing;

/// <summary>One page's text, prepared for the index.</summary>
/// <param name="Text">
/// What a person reads, and what a search result snippet is taken from. Words broken across a line
/// end are rejoined here.
/// </param>
/// <param name="Alternates">
/// The other reading of every word this had to guess about, indexed but never displayed. See
/// <see cref="Dehyphenator"/> for why guessing is unavoidable and why both forms are kept.
/// </param>
public sealed record IndexedPageText(string Text, string Alternates)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// Rejoins words broken across line ends, for the index only. The PDF's own text layer stays
/// positionally faithful — a word's box is where the ink is, and moving text to read better would
/// break selection.
///
/// <para>
/// This is the one thing Acrobat does that a naive pipeline does not, and it matters because a
/// hyphen at a line end is ambiguous. <c>fre-</c> / <c>quency</c> is one word split for typesetting
/// and has to become <c>frequency</c>, or a search for it finds nothing. <c>frequency-</c> /
/// <c>controlling</c> is a real compound and has to stay hyphenated, or a search for
/// <c>frequency-controlling</c> finds nothing. The two look identical on the page.
/// </para>
///
/// <para>
/// No heuristic gets that right every time, so this does not rely on one being right. It picks the
/// more likely reading for the text a person sees, and puts the other reading in a second column
/// that is indexed but never shown. Search matches either; the snippet is always the readable one.
/// A guess can therefore cost a slightly odd snippet, never a missed result.
/// </para>
/// </summary>
public static class Dehyphenator
{
    /// <summary>
    /// Prepares a page's extracted text for indexing.
    /// </summary>
    public static IndexedPageText Prepare(string? pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
            return new IndexedPageText(string.Empty, string.Empty);

        var lines = pageText.ReplaceLineEndings("\n").Split('\n');
        var text = new StringBuilder(pageText.Length);
        var alternates = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            if (i + 1 < lines.Length && TryJoin(line, lines[i + 1], out var joined, out var alternate, out var rest))
            {
                text.Append(joined);
                if (alternate.Length > 0)
                {
                    alternates.Append(alternate);
                    alternates.Append(' ');
                }

                // The remainder of the next line still has to be emitted, and may itself end in a
                // hyphen, so it goes back through the loop rather than being appended here.
                lines[i + 1] = rest.TrimStart();
                text.Append(' ');
                continue;
            }

            text.Append(line);
            text.Append('\n');
        }

        return new IndexedPageText(
            text.ToString().Trim(),
            alternates.ToString().Trim());
    }

    /// <summary>
    /// Decides whether a line ending in a hyphen continues into the next one, and how to read it.
    /// </summary>
    private static bool TryJoin(
        string line, string next, out string joined, out string alternate, out string rest)
    {
        joined = string.Empty;
        alternate = string.Empty;
        rest = next;

        if (!line.EndsWith('-') || line.Length < 2)
            return false;

        // An em-dash written as "--", or a line that is only a hyphen, is punctuation rather than a
        // broken word.
        if (line.EndsWith("--", StringComparison.Ordinal))
            return false;

        var head = line[..^1];
        var leftStart = head.Length;
        while (leftStart > 0 && char.IsLetter(head[leftStart - 1]))
            leftStart--;

        var left = head[leftStart..];
        if (left.Length == 0)
            return false;

        var trimmedNext = next.TrimStart();
        var rightEnd = 0;
        while (rightEnd < trimmedNext.Length && char.IsLetter(trimmedNext[rightEnd]))
            rightEnd++;

        var right = trimmedNext[..rightEnd];
        if (right.Length == 0)
            return false;

        // A capital on the far side is a proper noun or a new sentence, not the tail of a word.
        if (char.IsUpper(right[0]))
            return false;

        rest = trimmedNext[rightEnd..];

        var prefix = head[..leftStart];
        var compound = $"{left}-{right}";
        var fused = left + right;

        // The question is only ever about the left half. A compound cannot have a first half that
        // is not a word, so a fragment like "fre" settles it immediately; whereas asking the same
        // of the right half fails on any word a fixed vocabulary happens to miss - "controlling"
        // was the case that showed this up. Either way the other reading goes to the index too.
        if (IsWord(left))
        {
            joined = prefix + compound;
            alternate = fused;
        }
        else
        {
            joined = prefix + fused;
            alternate = compound;
        }

        return true;
    }

    /// <summary>
    /// Whether a fragment stands as a word on its own.
    ///
    /// The vocabulary is the classifier's: general English plus the terms that dominate
    /// test-equipment manuals. A fragment it does not recognise is treated as a fragment, which is
    /// the right way round - splitting mid-word is far more common at a line end than a compound,
    /// and the reading not chosen is indexed regardless.
    /// </summary>
    private static bool IsWord(string fragment) =>
        fragment.Length >= 2 && Classification.CommonWords.Contains(fragment);
}
