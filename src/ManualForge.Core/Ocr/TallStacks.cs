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

    /// <summary>Whether a detected region is a stack of rows read as one.</summary>
    public static bool IsStack(RectD region, double lineHeight) =>
        region.Height >= MinimumHeightInLines * lineHeight && region.Height >= region.Width &&
        region.Width <= MaximumWidthInLines * lineHeight;

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

        // Runs of inked pixel rows. A single blank row inside a glyph - the waist of an 8 at low
        // resolution - is bridged; anything wider is a gap between table rows.
        var runs = new List<(int Top, int Bottom)>();
        for (int y = y0; y <= y1; y++)
        {
            bool inked = false;
            for (int x = x0; x <= x1 && !inked; x++)
                inked = ink[y * width + x];
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
                    if (ink[y * width + x])
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
            rows.Add(RectD.FromEdges(left, top, right + 1, bottom + 1));
        }

        return rows;
    }
}
