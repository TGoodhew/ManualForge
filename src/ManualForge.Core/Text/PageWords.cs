using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;

namespace ManualForge.Core.Text;

/// <summary>
/// The words on a page, grouped the way a reader would group them.
///
/// <para>
/// PdfPig's default grouping starts a new word wherever a letter begins even fractionally after the
/// one before it ends. A typesetter that places every glyph on its own, with a hair of tracking
/// between them - FrameMaker through Distiller, which is most of Agilent's and Tektronix's manuals
/// - gets its words cut at random: <c>A g ile nt Te chn o log i e s m a k e s no wa rr a nty</c>, on
/// 9,935 of the library's pages (#47). Search could not find the words on those pages.
/// </para>
///
/// <para>
/// So a word ends at a space character, or at a gap wider than a share of the type size, as
/// <c>pdftotext</c> reads it. The gap is measured along the letter's own baseline, so a table set
/// sideways is read the same way as one set upright. Against <c>pdftotext</c> on 150 affected and 150
/// ordinary pages: 99.9% and 99.7% of its words, against 70.1% and 99.4% for the default grouping and
/// 99.1% and 98.8% for PdfPig's nearest-neighbour grouping, at a twentieth of the default's cost.
/// </para>
/// </summary>
public static class PageWords
{
    /// <summary>The page's words, in the order they were set.</summary>
    public static IEnumerable<Word> Of(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.GetWords(GapWordExtractor.Instance);
    }
}

/// <summary>Splits letters into words on space characters and on gaps wider than kerning.</summary>
public sealed class GapWordExtractor : IWordExtractor
{
    public static GapWordExtractor Instance { get; } = new();

    /// <summary>
    /// A gap wider than this share of the type size is a space. 0.10 to 0.20 all read within half a
    /// per cent of <c>pdftotext</c>; 0.15 was best on both the affected and the ordinary pages.
    /// </summary>
    public double GapShare { get; init; } = 0.15;

    public IEnumerable<Word> GetWords(IReadOnlyList<Letter> letters)
    {
        ArgumentNullException.ThrowIfNull(letters);

        var current = new List<Letter>();
        Letter? last = null;

        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                if (current.Count > 0)
                    yield return new Word(current);
                current = [];
                last = null;
                continue;
            }

            if (last is not null && Breaks(last, letter))
            {
                yield return new Word(current);
                current = [];
            }

            current.Add(letter);
            last = letter;
        }

        if (current.Count > 0)
            yield return new Word(current);
    }

    /// <summary>
    /// Whether <paramref name="next"/> starts a new word: a gap wider than kerning along the
    /// previous letter's baseline, a step back (a new line, a column jump), a move off the
    /// baseline, or a change of direction.
    /// </summary>
    private bool Breaks(Letter previous, Letter next)
    {
        var (dx, dy) = Direction(previous);
        var (nx, ny) = Direction(next);
        if (dx * nx + dy * ny < 0.9)
            return true;

        var size = Math.Max(1.0, Math.Max(previous.PointSize, next.PointSize));

        var gap = (next.StartBaseLine.X - previous.EndBaseLine.X) * dx
                  + (next.StartBaseLine.Y - previous.EndBaseLine.Y) * dy;
        var off = Math.Abs((next.StartBaseLine.X - previous.StartBaseLine.X) * -dy
                           + (next.StartBaseLine.Y - previous.StartBaseLine.Y) * dx);

        return gap > GapShare * size || gap < -0.5 * size || off > 0.3 * size;
    }

    /// <summary>The unit vector a letter's baseline runs along; rightwards for a letter with no width.</summary>
    private static (double X, double Y) Direction(Letter letter)
    {
        var x = letter.EndBaseLine.X - letter.StartBaseLine.X;
        var y = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-6 ? (1, 0) : (x / length, y / length);
    }
}
