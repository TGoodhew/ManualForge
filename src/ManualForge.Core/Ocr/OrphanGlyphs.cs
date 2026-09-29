using ManualForge.Core.Geometry;

namespace ManualForge.Core.Ocr;

/// <summary>
/// Finds ink the text detector never boxed: lone characters standing in cells of their own.
///
/// <para>
/// The DB detector answers weakly to a single glyph with nothing beside it on its line. On the
/// parts lists that make up much of this library that is the check-digit and quantity columns: on
/// the 100 captioned tables it boxed 3,653 of Acrobat's 8,100 lone characters, and for 1,621 of the
/// rest it produced nothing at all. None of eleven detector settings moved that by more than 1%;
/// lowering its thresholds only adds fragments of glyphs. The characters themselves read cleanly
/// once cropped. Issue #22; docs/measurements/single-characters.md.
/// </para>
///
/// <para>
/// A candidate here is a connected run of ink, shaped like a character at the page's line height,
/// standing clear of every recognised word, between words on its own row, and in a column with at
/// least two others. The last two are what keep a drawing out: a table's orphans line up between a
/// part number and a description; a schematic's strokes and terminal circles do not.
/// </para>
/// </summary>
public static class OrphanGlyphs
{
    /// <summary>
    /// How far a glyph must stand from a word, as a fraction of the larger of the line height and
    /// the word's own height. Word boxes come from recogniser timesteps and run narrower than the
    /// ink, so anything closer is a word's own first or last letter.
    /// </summary>
    public const double ClearanceFraction = 0.4;

    /// <summary>Padding round a candidate before it is read, as a fraction of the line height.</summary>
    /// <remarks>
    /// Tight on purpose. Half a line of padding brings the cell's rules into the crop, and they
    /// come back as <c>|</c> and <c>:</c>; on page 49 of the table book mean confidence fell from
    /// 0.98 to 0.55.
    /// </remarks>
    public const double PaddingFraction = 0.1;

    /// <summary>
    /// The narrowest crop, as a fraction of the line height. A lone <c>1</c> is a few pixels wide,
    /// and a crop that narrow is read as <c>4</c> at 0.2 confidence. Widening it sideways only
    /// reads it as <c>1</c> at 1.00, where padding on every side shrinks the glyph once the crop
    /// is scaled to line height.
    /// </summary>
    public const double MinimumWidthFraction = 0.8;

    /// <summary>
    /// The recogniser's score a rescued reading must reach to be kept, unless the engine is told
    /// otherwise (<see cref="OcrEngineOptions.OrphanConfidence"/>).
    /// </summary>
    public const double MinimumConfidence = 0.9;

    /// <summary>
    /// The typical line height on the page: the median height of recognised words of three or more
    /// characters that are wider than they are tall. Null when there are too few to say, and then
    /// nothing is rescued.
    /// </summary>
    /// <remarks>
    /// Words, not lines. A line chains a table row together with any tall stack beside it, and on
    /// page 75 of the table book that swelled the median until a stack 2.7 lines tall no longer
    /// looked tall. A stack's own words are taller than wide and a single character's box can
    /// collapse to a sliver, so neither is counted.
    /// </remarks>
    public static double? LineHeight(IReadOnlyList<RecognisedLine> lines)
    {
        var heights = lines
            .SelectMany(l => l.Words)
            .Where(w => w.Text.Length >= 3 && w.BoxPx.Width > w.BoxPx.Height && w.BoxPx.Height > 0)
            .Select(w => w.BoxPx.Height)
            .Order()
            .ToList();
        return heights.Count < 5 ? null : heights[heights.Count / 2];
    }

    /// <summary>
    /// Candidate boxes, in image pixels, for ink on the page that no word covers.
    /// </summary>
    /// <param name="ink">Row-major, one entry per pixel, true where the page is dark.</param>
    /// <param name="dropped">
    /// Told of each candidate the row and column tests turn away, and why. Glyphs too close to a
    /// word are not reported: every letter of every word is one.
    /// </param>
    public static IReadOnlyList<RectD> Find(
        bool[] ink, int width, int height, IReadOnlyList<RecognisedLine> found, Action<RectD, string>? dropped = null)
    {
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentNullException.ThrowIfNull(found);
        if (ink.Length != width * height)
            throw new ArgumentException($"Expected {width * height} pixels, got {ink.Length}.", nameof(ink));

        if (LineHeight(found) is not { } row)
            return [];

        // Words, not lines: a line box spans the whole row of a table, orphan cells included.
        var words = found
            .SelectMany(l => l.Words.Count > 0 ? l.Words.Select(w => w.BoxPx) : [l.BoxPx])
            .ToList();
        // Big lettering gets a gap to match its size. A word taller than it is wide is not big
        // lettering but a stack left uncut, and sized by its height it would claim the next column.
        var clearances = words.Select(b =>
        {
            double size = b.Width > b.Height ? Math.Max(row, b.Height) : row;
            return Inflate(b, ClearanceFraction * size, 0.15 * size);
        }).ToList();
        // Some word boxes come back a fraction of a point tall; they still block, but cannot
        // vouch for a row of text.
        var rowWords = words.Where(b => b.Height > 0.3 * row).ToList();

        var glyphs = Components(ink, width, height)
            .Where(c => c.Height >= 0.4 * row && c.Height <= 1.2 * row && c.Width <= 1.5 * row)
            .Where(c => !clearances.Any(w => Intersects(w, c)))
            .ToList();

        var candidates = new List<RectD>();
        foreach (var c in JoinAlongBaseline(glyphs, row))
        {
            if (BetweenWords(c, rowWords, row))
                candidates.Add(c);
            else
                dropped?.Invoke(c, OnTextRow(c, rowWords, row) ? "words on one side only" : "off the text rows");
        }

        var kept = new List<RectD>(candidates.Count);
        foreach (var c in candidates)
        {
            int column = candidates.Count(o => o != c && Math.Abs(CentreX(o) - CentreX(c)) < 0.5 * row);
            if (column >= 2)
                kept.Add(c);
            else
                dropped?.Invoke(c, $"not in a column ({column} others)");
        }

        return kept;
    }

    /// <summary>The region to hand the recogniser for a candidate: padded, and never too narrow.</summary>
    public static RectD CropFor(RectD candidate, double lineHeight, int imageWidth, int imageHeight)
    {
        double pad = PaddingFraction * lineHeight;
        double side = Math.Max(pad, (MinimumWidthFraction * lineHeight - candidate.Width) / 2);
        return RectD.FromEdges(
            Math.Max(0, candidate.Left - side),
            Math.Max(0, candidate.Top - pad),
            Math.Min(imageWidth, candidate.Right + side),
            Math.Min(imageHeight, candidate.Bottom + pad));
    }

    /// <summary>Whether a rescued reading is worth keeping: confident, and letters or digits only.</summary>
    public static bool Keep(string text, double confidence, double minimum = MinimumConfidence)
    {
        var trimmed = text.Trim();
        return confidence >= minimum && trimmed.Length > 0 && trimmed.All(char.IsLetterOrDigit);
    }

    /// <summary>Bounding boxes of the 8-connected runs of ink.</summary>
    internal static List<RectD> Components(bool[] ink, int width, int height)
    {
        var seen = new bool[ink.Length];
        var boxes = new List<RectD>();
        var stack = new Stack<int>();

        for (int start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || seen[start])
                continue;

            int x0 = width, y0 = height, x1 = -1, y1 = -1;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % width, y = i / width;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);

                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                    {
                        int n = ny * width + nx;
                        if (ink[n] && !seen[n])
                        {
                            seen[n] = true;
                            stack.Push(n);
                        }
                    }
            }

            boxes.Add(RectD.FromEdges(x0, y0, x1 + 1, y1 + 1));
        }

        return boxes;
    }

    /// <summary>Glyphs sitting side by side on one baseline become one candidate: <c>10</c>, <c>A2</c>.</summary>
    private static List<RectD> JoinAlongBaseline(List<RectD> glyphs, double row)
    {
        var joined = new List<RectD>();
        foreach (var g in glyphs.OrderBy(g => g.Left))
        {
            int k = joined.FindIndex(m =>
                g.Left - m.Right <= 0.35 * row && g.Left >= m.Left &&
                Math.Min(m.Bottom, g.Bottom) - Math.Max(m.Top, g.Top) > 0.5 * Math.Min(m.Height, g.Height));

            if (k < 0)
                joined.Add(g);
            else
                joined[k] = RectD.FromEdges(
                    joined[k].Left, Math.Min(joined[k].Top, g.Top),
                    Math.Max(joined[k].Right, g.Right), Math.Max(joined[k].Bottom, g.Bottom));
        }

        return joined;
    }

    /// <summary>
    /// A real orphan shares a row with recognised words: its middle lies inside a word of about its
    /// size, somewhere along the same line of the page.
    /// </summary>
    internal static bool OnTextRow(RectD c, IReadOnlyList<RectD> words, double row) =>
        words.Any(w => RowMate(c, w, row));

    /// <summary>
    /// A table's orphan sits between words on its row: a check digit has its part number to the
    /// left and its description to the right, and so does a quantity. A terminal circle or a stroke
    /// of a drawing, beside one label at most, does not.
    /// </summary>
    internal static bool BetweenWords(RectD c, IReadOnlyList<RectD> words, double row) =>
        words.Any(w => RowMate(c, w, row) && w.Right <= c.Left) &&
        words.Any(w => RowMate(c, w, row) && w.Left >= c.Right);

    private static bool RowMate(RectD c, RectD w, double row)
    {
        double middle = (c.Top + c.Bottom) / 2;
        return middle > w.Top && middle < w.Bottom &&
            w.Height >= 0.8 * c.Height && w.Height <= 2.2 * c.Height &&
            Math.Min(Math.Abs(w.Left - c.Right), Math.Abs(c.Left - w.Right)) < 15 * row;
    }

    private static double CentreX(RectD r) => (r.Left + r.Right) / 2;

    private static RectD Inflate(RectD r, double dx, double dy) =>
        RectD.FromEdges(r.Left - dx, r.Top - dy, r.Right + dx, r.Bottom + dy);

    private static bool Intersects(RectD a, RectD b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;
}
