using ManualForge.Core.Geometry;

namespace ManualForge.Core.Ocr;

/// <summary>
/// Finds words the detector boxed across several rows of a table, and where to cut them.
///
/// <para>
/// Where a column of short, aligned entries - designator prefixes, check digits - sits between wider
/// columns, the detector can box the whole column as one tall region. The recogniser turns a tall
/// crop on its side and reads it as one word: <c>NNNNN</c>, <c>AAAAA</c>, <c>mmmmm</c>. On the 100
/// captioned tables 919 of Acrobat's lone characters sat under such a box. Cut at the blank rows of
/// its own ink and read row by row, the engine reads 857 more of Acrobat's tokens there.
/// </para>
///
/// <para>
/// A word set sideways on a drawing is tall and narrow too, and the recogniser reads it correctly;
/// cutting it makes loose letters. The cut is made only where the rows stand clearly apart, and the
/// engine also asks that they line up with rows of ordinary text - which is what actually tells the
/// two apart; tight tables space their rows no wider than a sideways word spaces its letters. Issue #22;
/// docs/measurements/single-characters.md.
/// </para>
/// </summary>
public static class TallStacks
{
    /// <summary>A word at least this many lines tall, and taller than it is wide, is a stack.</summary>
    public const double MinimumHeightInLines = 2.5;

    /// <summary>The median gap between a stack's rows must be at least this many lines.</summary>
    public const double MinimumGapInLines = 0.15;

    /// <summary>
    /// A stack is a column, never more than this many lines wide. Every stack cut on the table
    /// pages was under two; a block of a component layout, six lines wide, was not a stack at all.
    /// </summary>
    public const double MaximumWidthInLines = 2.5;

    /// <summary>A stack's rows are no taller than this many lines; a word set sideways can be.</summary>
    public const double MaximumRowHeightInLines = 1.15;

    /// <summary>
    /// A pixel column inked on at least this share of a box's rows is a table rule, not lettering,
    /// and is left out when the box is cut into rows.
    /// </summary>
    public const double RuleShare = 0.9;

    /// <summary>
    /// Whether rows cut from a region belong to a table: every row lines up with a row of ordinary
    /// text - one may miss in a column of four or more - and the rows are no taller than a line.
    /// </summary>
    /// <remarks>
    /// Measured on 416 table stacks and 41 typical-page regions. 398 table stacks had every row on
    /// a text row, and nearly every wrong cut on the typical pages - <c>CABLE</c>, <c>-C3-</c>, a
    /// German sentence set sideways - had two or more rows off, or rows taller than a line. The one
    /// miss allowed keeps garbage columns like <c>NNNOI</c> and <c>OCONN</c> that fell one row short.
    /// </remarks>
    public static bool BelongsToTable(int rowsOnTextRows, int rows, double medianRowHeight, double lineHeight) =>
        rows > 0 &&
        rowsOnTextRows >= rows - (rows >= 4 ? 1 : 0) &&
        medianRowHeight <= MaximumRowHeightInLines * lineHeight;

    /// <summary>Whether a detected region is a stack of rows read as one.</summary>
    public static bool IsStack(RectD region, double lineHeight) =>
        region.Height >= MinimumHeightInLines * lineHeight && region.Height >= region.Width &&
        region.Width <= MaximumWidthInLines * lineHeight;

    /// <summary>
    /// Where each word read from a row sits: the row divided at the widest blank gap between each
    /// pair of the recogniser's word centres, each share shrunk to the ink it holds.
    /// </summary>
    /// <remarks>
    /// The recogniser's own word boxes come from its timesteps, and across a crop a few characters
    /// wide they collapse - a check digit came back a point wide, beside its ink. They are good
    /// enough to say where one word ends and the next begins, and no better.
    /// </remarks>
    /// <param name="centres">The recogniser's word centres, left to right.</param>
    public static IReadOnlyList<RectD> WordBoxes(bool[] ink, int width, RectD row, IReadOnlyList<double> centres)
    {
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentNullException.ThrowIfNull(centres);

        int top = Math.Max(0, (int)row.Top), bottom = Math.Min(ink.Length / width, (int)Math.Ceiling(row.Bottom));
        bool Inked(int x)
        {
            for (int y = top; y < bottom; y++)
                if (ink[y * width + x])
                    return true;
            return false;
        }

        // Between two centres the words meet at the widest run of blank columns; the centres
        // themselves can sit off their words, so halfway between them can land inside one.
        var cuts = new List<double>(centres.Count - 1);
        for (int i = 1; i < centres.Count; i++)
        {
            int from = Math.Max(0, (int)Math.Ceiling(Math.Min(centres[i - 1], centres[i])));
            int to = Math.Min(width - 1, (int)Math.Floor(Math.Max(centres[i - 1], centres[i])));
            int bestStart = -1, bestLength = 0, runStart = -1;
            for (int x = from; x <= to + 1; x++)
            {
                if (x <= to && !Inked(x))
                {
                    if (runStart < 0) runStart = x;
                    continue;
                }
                if (runStart >= 0 && x - runStart > bestLength)
                    (bestStart, bestLength) = (runStart, x - runStart);
                runStart = -1;
            }
            cuts.Add(bestLength > 0 ? bestStart + bestLength / 2.0 : (centres[i - 1] + centres[i]) / 2);
        }

        var boxes = new List<RectD>(centres.Count);
        for (int i = 0; i < centres.Count; i++)
        {
            double from = i == 0 ? row.Left : cuts[i - 1];
            double to = i == centres.Count - 1 ? row.Right : cuts[i];

            int left = int.MaxValue, right = int.MinValue;
            for (int x = Math.Max(0, (int)Math.Floor(from)); x < Math.Min(width, (int)Math.Ceiling(to)); x++)
                for (int y = top; y < bottom; y++)
                    if (ink[y * width + x])
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        break;
                    }

            boxes.Add(left <= right
                ? RectD.FromEdges(left, row.Top, right + 1, row.Bottom)
                : RectD.FromEdges(from, row.Top, to, row.Bottom));
        }

        return boxes;
    }

    /// <summary>
    /// The rows of ink inside <paramref name="box"/>, each as the box of its own ink. Empty unless
    /// there are at least two, so a single large glyph - a big <c>8</c> on a drawing - is left whole.
    /// </summary>
    /// <param name="ink">Row-major, one entry per pixel, true where the page is dark.</param>
    public static IReadOnlyList<RectD> Rows(bool[] ink, int width, int height, RectD box, double lineHeight) =>
        Rows(ink, width, height, box, lineHeight, out _);

    /// <inheritdoc cref="Rows(bool[], int, int, RectD, double)"/>
    /// <param name="verdict">Why the box was or was not cut, for the log.</param>
    internal static IReadOnlyList<RectD> Rows(bool[] ink, int width, int height, RectD box, double lineHeight, out string verdict)
    {
        verdict = "cut";
        ArgumentNullException.ThrowIfNull(ink);
        if (ink.Length != width * height)
            throw new ArgumentException($"Expected {width * height} pixels, got {ink.Length}.", nameof(ink));

        int x0 = Math.Max(0, (int)Math.Floor(box.Left)), x1 = Math.Min(width - 1, (int)Math.Ceiling(box.Right));
        int y0 = Math.Max(0, (int)Math.Floor(box.Top)), y1 = Math.Min(height - 1, (int)Math.Ceiling(box.Bottom));
        if (x1 <= x0 || y1 <= y0)
        {
            verdict = "off the page";
            return [];
        }

        // A table's rule inside the box is not part of the column. The detector's box for a column
        // of A2s can take in the vertical rule beside it, and a rule is inked on every pixel row, so
        // it would join the rows into one run with no gap to cut at (#50). Lettering never runs
        // unbroken down a whole column of rows, so a pixel column inked nearly all the way is a rule.
        // A scanned rule's edge is ragged - on page 60 its last pixel column was inked 85% of the way
        // down and the next 29% - so the columns just beside it go too. A tenth of a line is less than
        // the padding a table leaves between its rules and its text.
        var rule = new bool[x1 - x0 + 1];
        int margin = Math.Max(2, (int)Math.Round(0.1 * lineHeight));
        for (int x = x0; x <= x1; x++)
        {
            int inkedRows = 0;
            for (int y = y0; y <= y1; y++)
                if (ink[y * width + x])
                    inkedRows++;
            if (inkedRows < RuleShare * (y1 - y0 + 1))
                continue;
            for (int m = Math.Max(x0, x - margin); m <= Math.Min(x1, x + margin); m++)
                rule[m - x0] = true;
        }

        // Runs of inked pixel rows. A single blank row inside a glyph - the waist of an 8 at low
        // resolution - is bridged; anything wider is a gap between table rows.
        var runs = new List<(int Top, int Bottom)>();
        for (int y = y0; y <= y1; y++)
        {
            bool inked = false;
            for (int x = x0; x <= x1 && !inked; x++)
                inked = !rule[x - x0] && ink[y * width + x];
            if (!inked)
                continue;

            if (runs.Count > 0 && y - runs[^1].Bottom <= 2)
                runs[^1] = (runs[^1].Top, y);
            else
                runs.Add((y, y));
        }

        // Specks and underline fragments are not rows of text.
        runs.RemoveAll(r => r.Bottom - r.Top + 1 < 0.3 * lineHeight);
        if (runs.Count < 2)
        {
            verdict = "no gap to cut at";
            return [];
        }

        // Rows of a table stand apart by the table's own line spacing. The letters of a word set
        // sideways on a drawing - FUER, R934 - stand a pixel or two apart, and cutting there turns
        // a word the recogniser had read correctly into loose letters.
        var gaps = runs.Zip(runs.Skip(1), (a, b) => b.Top - a.Bottom - 1).Order().ToList();
        if (gaps[gaps.Count / 2] < MinimumGapInLines * lineHeight)
        {
            verdict = $"rows too close: {runs.Count} runs, median gap {gaps[gaps.Count / 2]} px";
            return [];
        }

        var rows = new List<RectD>(runs.Count);
        foreach (var (top, bottom) in runs)
        {
            int left = x1, right = x0;
            for (int y = top; y <= bottom; y++)
                for (int x = x0; x <= x1; x++)
                    if (!rule[x - x0] && ink[y * width + x])
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
            rows.Add(RectD.FromEdges(left, top, right + 1, bottom + 1));
        }

        return rows;
    }
}
