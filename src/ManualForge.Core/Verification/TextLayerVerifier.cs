using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManualForge.Core.Verification;

/// <summary>
/// Where one emitted word ended up, against where it was meant to go. All coordinates are in
/// display space: the page as a reader shows it, rotation applied, origin at the visible
/// bottom-left corner.
/// </summary>
public sealed record WordAlignment(
    string ExpectedText,
    string ExtractedText,
    PointD ExpectedBaselineStart,
    PointD ExtractedBaselineStart,
    PointD ExpectedBaselineEnd,
    PointD ExtractedBaselineEnd)
{
    public bool TextMatches => string.Equals(ExpectedText, ExtractedText, StringComparison.Ordinal);

    /// <summary>How far the start of the word's baseline is from where it should be, in points.</summary>
    public double StartDeviationPt => Distance(ExpectedBaselineStart, ExtractedBaselineStart);

    /// <summary>How far the end of the word's baseline is from where it should be, in points.</summary>
    public double EndDeviationPt => Distance(ExpectedBaselineEnd, ExtractedBaselineEnd);

    /// <summary>The worse of the two ends: what a reader selecting the word would notice.</summary>
    public double MaxDeviationPt => Math.Max(StartDeviationPt, EndDeviationPt);

    public double ExpectedWidthPt => Distance(ExpectedBaselineStart, ExpectedBaselineEnd);

    public double ExtractedWidthPt => Distance(ExtractedBaselineStart, ExtractedBaselineEnd);

    public double WidthDeviationPt => Math.Abs(ExpectedWidthPt - ExtractedWidthPt);

    private static double Distance(PointD a, PointD b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

public sealed record PageVerification(
    int PageNumber,
    int ExpectedWords,
    int ExtractedLetters,
    int PreExistingLetters,
    int MatchedWords,
    IReadOnlyList<WordAlignment> Alignments)
{
    public bool HasText => ExtractedLetters > 0;

    /// <summary>
    /// Characters that were in the file before this run. A page with a pre-existing text layer now
    /// carries two, which is fine for search but is worth knowing about: deciding whether to keep,
    /// replace or skip it is what the classifier does in phase 2.
    /// </summary>
    public bool HadTextAlready => PreExistingLetters > 0;

    public double MeanDeviationPt =>
        Alignments.Count == 0 ? 0 : Alignments.Average(a => a.MaxDeviationPt);

    public double WorstDeviationPt =>
        Alignments.Count == 0 ? 0 : Alignments.Max(a => a.MaxDeviationPt);

    public double MeanWidthDeviationPt =>
        Alignments.Count == 0 ? 0 : Alignments.Average(a => a.WidthDeviationPt);

    public double WorstWidthDeviationPt =>
        Alignments.Count == 0 ? 0 : Alignments.Max(a => a.WidthDeviationPt);

    /// <summary>The deviation below which the given share of words fall.</summary>
    public double PercentileDeviationPt(double percentile)
    {
        if (Alignments.Count == 0) return 0;
        var sorted = Alignments.Select(a => a.MaxDeviationPt).Order().ToArray();
        var index = (int)Math.Clamp(Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

/// <summary>
/// Reads a written text layer back out with a different library than the one that wrote it, and
/// measures how far each word moved.
///
/// Verifying with PdfPig rather than PDFsharp is deliberate: a bug in the writer that also existed
/// in the reader would cancel out and the check would pass on a broken file. PdfPig parses the
/// content stream, resolves the embedded font and applies the text and transformation matrices
/// exactly as a reader would, so agreement here means the geometry really is right.
///
/// The comparison is on baselines rather than glyph boxes. A word's baseline start, end and
/// direction are what selection and search follow, and they are what both the writer and the
/// reader agree on exactly. Glyph box heights are not usable for this: the invisible font has no
/// contours, so extractors are entitled to report a zero-height box for it, and several do.
/// </summary>
public static class TextLayerVerifier
{
    public static PageVerification VerifyPage(
        string pdfPath,
        int pageNumber,
        IReadOnlyList<RecognisedWord> expected,
        PageGeometry geometry,
        double baselineOffsetFraction = 0.0)
    {
        using var document = PdfDocument.Open(pdfPath);
        var page = document.GetPage(pageNumber);
        return VerifyPage(page, expected, geometry, baselineOffsetFraction);
    }

    public static PageVerification VerifyPage(
        Page page,
        IReadOnlyList<RecognisedWord> expected,
        PageGeometry geometry,
        double baselineOffsetFraction = 0.0)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(geometry);

        // Only our own letters take part. Many of these manuals already carry a text layer from
        // 2000s-era OCR, and those letters are interleaved with ours in reading order, so matching
        // by position in the list would silently compare our words against someone else's. The
        // invisible font's name identifies ours unambiguously.
        var letters = page.Letters
            .Where(l => l.FontName is not null
                && l.FontName.Contains(Text.GlyphlessTrueTypeFont.PostScriptName, StringComparison.Ordinal))
            .ToArray();

        var alignments = new List<WordAlignment>();
        var cursor = 0;

        foreach (var word in expected)
        {
            if (!word.IsUsable)
                continue;

            // Letters come back in the order they were shown, so each emitted word is simply the
            // next run of them. Whitespace is never shown, only implied by position.
            var visible = word.Text.Where(c => !char.IsWhiteSpace(c)).ToArray();
            var consumed = new List<Letter>(visible.Length);
            for (var i = 0; i < visible.Length && cursor < letters.Length; i++, cursor++)
                consumed.Add(letters[cursor]);

            if (consumed.Count == 0)
                continue;

            var (expectedStart, expectedEnd) = ExpectedBaseline(word, geometry, baselineOffsetFraction);

            alignments.Add(new WordAlignment(
                new string(visible),
                string.Concat(consumed.Select(l => l.Value)),
                expectedStart,
                new PointD(consumed[0].StartBaseLine.X, consumed[0].StartBaseLine.Y),
                expectedEnd,
                new PointD(consumed[^1].EndBaseLine.X, consumed[^1].EndBaseLine.Y)));
        }

        return new PageVerification(
            page.Number,
            expected.Count(w => w.IsUsable),
            letters.Length,
            page.Letters.Count - letters.Length,
            alignments.Count(a => a.TextMatches),
            alignments);
    }

    /// <summary>
    /// Where a word's baseline should start and end, in the display space extractors report in.
    /// </summary>
    public static (PointD Start, PointD End) ExpectedBaseline(
        RecognisedWord word,
        PageGeometry geometry,
        double baselineOffsetFraction)
    {
        var box = word.BoxPx;
        var baselinePy = box.Bottom + box.Height * baselineOffsetFraction;
        return (
            geometry.ToDisplaySpace(box.Left, baselinePy),
            geometry.ToDisplaySpace(box.Right, baselinePy));
    }

    /// <summary>Total number of characters a reader can extract from a document.</summary>
    public static int CountExtractableCharacters(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        return document.GetPages().Sum(p => p.Letters.Count);
    }
}
