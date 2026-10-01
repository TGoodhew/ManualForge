using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManualForge.Core.Text;

/// <summary>
/// Where a document's pages already carry text, so that a new layer can be written around it.
///
/// A scan is not always only a scan. Sellers stamp their name on every page, Acrobat adds headers,
/// and some manuals arrive with a contents page typeset over the image. That text is ink and has to
/// stay - but the recogniser reads it off the page too, and a recognised word written on top of
/// text already there is the "BBrrooaaddbbaanndd" an extractor makes of two layers. So a recognised
/// word that lands on existing text is left out: the page already says it.
///
/// Positions are compared in display space, the space PdfPig reports letters in and the verifier
/// already measures against.
/// </summary>
public sealed class ExistingText : IDisposable
{
    /// <summary>A word's box is grown by this share of its height before letters are tested against it.</summary>
    private const double Margin = 0.1;

    private readonly PdfDocument? _document;

    private ExistingText(PdfDocument? document) => _document = document;

    /// <summary>
    /// Reads the document's existing text. One that will not open has none as far as this is
    /// concerned; the check before recognition is the one that should say so.
    /// </summary>
    public static ExistingText Open(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        try
        {
            return new ExistingText(PdfDocument.Open(pdfBytes, new ParsingOptions { UseLenientParsing = true }));
        }
        catch (Exception)
        {
            return new ExistingText(null);
        }
    }

    /// <summary>The middle of every letter already on the page, 1-based.</summary>
    public IReadOnlyList<PointD> LettersOn(int pageNumber)
    {
        if (_document is null)
            return [];

        try
        {
            return _document.GetPage(pageNumber).Letters
                .Where(l => !string.IsNullOrWhiteSpace(l.Value))
                .Select(MiddleOf)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The words that do not land on any of <paramref name="letters"/>.</summary>
    public static RecognisedWord[] WordsClearOf(
        IReadOnlyList<RecognisedWord> words, IReadOnlyList<PointD> letters, PageGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(letters);
        ArgumentNullException.ThrowIfNull(geometry);

        if (letters.Count == 0)
            return words as RecognisedWord[] ?? words.ToArray();

        return words.Where(word => !letters.Any(DisplayBox(word.BoxPx, geometry).Contains)).ToArray();
    }

    private static Box DisplayBox(RectD boxPx, PageGeometry geometry)
    {
        var margin = boxPx.Height * Margin;
        var a = geometry.ToDisplaySpace(boxPx.Left - margin, boxPx.Top - margin);
        var b = geometry.ToDisplaySpace(boxPx.Right + margin, boxPx.Bottom + margin);
        return new Box(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// The middle of a letter's glyph. A font with no outlines - another program's invisible OCR
    /// font, say - can report a box with no height, and then the middle is taken a third of the
    /// point size above the baseline instead.
    /// </summary>
    private static PointD MiddleOf(Letter letter)
    {
        var glyph = letter.BoundingBox;
        if (Math.Abs(glyph.Height) > 0.5 && Math.Abs(glyph.Width) > 0.5)
            return new PointD(glyph.Centroid.X, glyph.Centroid.Y);

        var lift = letter.PointSize / 3;
        return new PointD(
            (letter.StartBaseLine.X + letter.EndBaseLine.X) / 2,
            (letter.StartBaseLine.Y + letter.EndBaseLine.Y) / 2 + lift);
    }

    private readonly record struct Box(double Left, double Bottom, double Right, double Top)
    {
        public bool Contains(PointD point)
            => point.X >= Left && point.X <= Right && point.Y >= Bottom && point.Y <= Top;
    }

    public void Dispose() => _document?.Dispose();
}
