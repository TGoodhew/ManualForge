using ManualForge.Core.Geometry;

namespace ManualForge.Core.Auditing;

/// <summary>
/// Puts recognised lines into reading order, columns and all.
///
/// <para>
/// A recogniser returns lines in whatever order it detected them, which on a single column is
/// reading order and on anything else is not. The page that made this necessary is 2-60 of the
/// 54845A Programmer's Guide, which carries two sibling notes side by side:
/// </para>
///
/// <code>
/// The AUX                                     The EXTernal
/// command is                                  command is only
/// only available                              available on the
/// on the 54815/25/35/45/46.                   54810/20.
/// </code>
///
/// <para>
/// Taken in detection order those two interleave line by line, and what comes out reads as though
/// the AUX command were the one restricted to the 54810/20. Every word is still searchable, so the
/// index does not care — but <c>read_manual_page</c> hands this text to something that will answer
/// from it, and the answer would name the wrong trigger source for the instrument. That is the
/// exact shape of failure this whole exercise exists to remove, arrived at by a different route.
/// </para>
///
/// <para>
/// The method is the classic recursive XY-cut: find the widest band of blank page that separates
/// the lines, split there, and recurse. A horizontal cut wins ties because reading runs down the
/// page before it runs across.
/// </para>
///
/// <para>
/// <b>This is not on its own enough, and the reason generalises.</b> Sorting lines was the first
/// attempt at 2-60 and it changed nothing, because the recogniser had already merged the two notes
/// into single detected lines — <c>The AUX CHANnel channel_number</c> arrives as one box. A pass
/// that reorders lines cannot repair a line that was wrongly detected in the first place. Every
/// layer above detection inherits detection's mistakes and cannot see them, so the line has to be
/// cut back into runs before any of this applies; see <c>PageRepairer.TextRuns</c>.
/// </para>
///
/// <para>
/// <b>Known limitation.</b> A run that belongs to a drawn figure can still be ordered between two
/// runs of a note, when it sits inside the note's own column band. On 2-60 one does: the label
/// <c>EXTernal</c> lands between "available on the" and "54810/20.". That instance is harmless
/// because the label repeats a word the sentence is already about — but that is luck about which
/// bubble sits where, not a property of this method. The neighbouring bubble says <c>AUX</c>, and
/// had it fallen there the result would have read as a relationship the page does not state.
/// </para>
///
/// <para>
/// Two fixes were tried and both cost more than they bought. Narrowing
/// <see cref="ColumnGutter"/> separates the label but starts reading close-set tables down their
/// columns, which mis-pairs every row. Grouping lines into paragraphs before ordering fixes the
/// label and does the same thing to tables, for the same reason: a table's columns are left-aligned
/// and consecutively led, exactly like a paragraph. The fix at the right level is to tell a figure
/// label from prose by whether a drawn path encloses it, which the page's vector paths would
/// support and which nothing here does yet.
/// </para>
/// </summary>
public static class ReadingOrder
{
    /// <summary>
    /// Least blank width, as a multiple of the median line height, that counts as a gutter between
    /// columns.
    ///
    /// <para>
    /// Generous on purpose. A real gutter is wide; the gaps between the cells of a table are not,
    /// and cutting a table into columns would order it down each column instead of along each row,
    /// which is worse than the problem being fixed. Three line heights clears the notes on 2-60 and
    /// leaves ordinary tabulation alone.
    /// </para>
    /// </summary>
    public const double ColumnGutter = 3.0;

    /// <summary>
    /// Least blank height, as a multiple of the median line height, that counts as a break between
    /// blocks. Just over one line: consecutive lines of a paragraph do not separate, and anything
    /// with a blank line between it does.
    /// </summary>
    public const double BlockGap = 1.2;

    /// <summary>
    /// Sorts items into reading order using their boxes, which must be in image space — origin at
    /// the top left, Y growing downwards.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(IReadOnlyList<T> items, Func<T, RectD> box)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(box);

        if (items.Count <= 2)
            return items;

        var heights = items.Select(i => box(i).Height).Where(h => h > 0).Order().ToArray();
        if (heights.Length == 0)
            return items;

        var lineHeight = heights[heights.Length / 2];
        if (lineHeight <= 0)
            return items;

        var ordered = new List<T>(items.Count);
        Cut([.. items], box, lineHeight * ColumnGutter, lineHeight * BlockGap, ordered, depth: 0);
        return ordered;
    }

    private static void Cut<T>(
        List<T> items,
        Func<T, RectD> box,
        double minGutter,
        double minBlockGap,
        List<T> into,
        int depth)
    {
        if (items.Count <= 1)
        {
            into.AddRange(items);
            return;
        }

        // A page of a few hundred lines cannot need this many cuts, and a recursion that will not
        // terminate on some pathological layout must not take the repair down with it.
        if (depth >= 24)
        {
            into.AddRange(items.OrderBy(i => box(i).Top).ThenBy(i => box(i).Left));
            return;
        }

        var (horizontalGap, horizontalAt) = WidestGap(items, box, i => box(i).Top, i => box(i).Bottom);
        var (verticalGap, verticalAt) = WidestGap(items, box, i => box(i).Left, i => box(i).Right);

        // Normalised against their own thresholds so the two axes can be compared at all, and
        // horizontal wins a tie because reading goes down the page before it goes across it.
        var horizontalScore = horizontalGap / minBlockGap;
        var verticalScore = verticalGap / minGutter;

        if (horizontalGap >= minBlockGap && horizontalScore >= verticalScore)
        {
            Split(items, box, i => box(i).Top, horizontalAt, minGutter, minBlockGap, into, depth);
            return;
        }

        if (verticalGap >= minGutter)
        {
            Split(items, box, i => box(i).Left, verticalAt, minGutter, minBlockGap, into, depth);
            return;
        }

        // Nothing separates them: one block, read top to bottom and left to right within a line.
        into.AddRange(items.OrderBy(i => box(i).Top).ThenBy(i => box(i).Left));
    }

    private static void Split<T>(
        List<T> items,
        Func<T, RectD> box,
        Func<T, double> start,
        int at,
        double minGutter,
        double minBlockGap,
        List<T> into,
        int depth)
    {
        var sorted = items.OrderBy(start).ToList();
        Cut(sorted.Take(at).ToList(), box, minGutter, minBlockGap, into, depth + 1);
        Cut(sorted.Skip(at).ToList(), box, minGutter, minBlockGap, into, depth + 1);
    }

    /// <summary>
    /// The widest stretch along one axis that no item occupies, and the index of the first item
    /// after it once the items are sorted by that axis.
    /// </summary>
    private static (double Gap, int At) WidestGap<T>(
        List<T> items, Func<T, RectD> box, Func<T, double> start, Func<T, double> end)
    {
        _ = box;

        var sorted = items.OrderBy(start).ToList();
        var reach = end(sorted[0]);
        var widest = 0.0;
        var at = -1;

        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = start(sorted[i]) - reach;
            if (gap > widest)
            {
                widest = gap;
                at = i;
            }

            reach = Math.Max(reach, end(sorted[i]));
        }

        return at < 0 ? (0, 0) : (widest, at);
    }
}
