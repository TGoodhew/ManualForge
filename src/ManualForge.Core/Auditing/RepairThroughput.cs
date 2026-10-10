namespace ManualForge.Core.Auditing;

/// <summary>
/// How fast a repair reads a page, by what kind of page it is and the resolution it is read at.
///
/// <para>
/// The estimate used to be one rate, 46 pages/min, for every page. That figure was an average over
/// two populations that differ by a factor of two, so it was optimistic exactly when a job was
/// longest: a repair made mostly of scanned pages was quoted at 3.0 hours and took 3.7 (#15).
/// </para>
///
/// <para>
/// Measured from the time between consecutive pages of the same document, over the 19,529 pages of
/// the full repair on 10 October 2026, on an RTX 5070 Ti (#25). The kind of page matters most: a
/// drawn page carries a few labels, while a scanned page is dense lettering the recogniser has to
/// read word by word. Resolution matters second. The suggested resolution was the first guess at
/// the cause, and on its own it does not even order the rates - 400 dpi runs faster than 300 on
/// scans - because it mostly stands in for the mix of kinds.
/// </para>
///
/// <list type="table">
///   <listheader><term>pages/min</term><description>300 dpi | 400 dpi | 600 dpi</description></listheader>
///   <item><term>Drawn</term><description>89.4 (3,001) | 70.8 (531) | 44.0 (354)</description></item>
///   <item><term>Raster</term><description>43.9 (13,628) | 45.4 (1,047) | 28.0 (393)</description></item>
/// </list>
///
/// <para>
/// The same measurement on the RTX 3060 Ti, on 1 October, gave 77.8, 65.6 and 41.9 for drawn pages
/// and 35.1, 39.3 and 25.0 for scanned ones: the new card is 5 to 25 per cent faster, most on the
/// scans. It also gave each document about 2 seconds before its first page - hashing it, reading
/// it, opening it. On the new card that time no longer shows between documents (median -0.2 s), so
/// it is no longer charged.
/// </para>
///
/// <para>
/// The 10 October repair took 407 minutes. This table estimates it at 406, and the 3060 Ti table
/// said 519. That is the run the table was measured on, so it shows the method is consistent, not
/// that it predicts. The 3060 Ti table, tested the same way on its own runs, estimated 537 for 532
/// minutes and 125 for 127, where the single rate it replaced said 451 and 91.
/// </para>
/// </summary>
public static class RepairThroughput
{
    /// <summary>Time spent on a document before its first page is read.</summary>
    public static readonly TimeSpan PerDocument = TimeSpan.Zero;

    /// <summary>Pages a minute for a page of this kind read at this resolution.</summary>
    public static double PagesPerMinute(PageKind kind, int dpi) => (kind, dpi) switch
    {
        (PageKind.Drawn, <= 300) => 89.4,
        (PageKind.Drawn, <= 400) => 70.8,
        (PageKind.Drawn, _) => 44.0,
        (_, <= 300) => 43.9,
        (_, <= 400) => 45.4,
        _ => 28.0,
    };

    /// <summary>How long reading these pages, spread over this many documents, should take.</summary>
    public static TimeSpan Estimate(IEnumerable<PlannedRepairPage> pages, int documents)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var minutes = pages.Sum(p => 1.0 / PagesPerMinute(p.Kind, p.Dpi));
        return TimeSpan.FromMinutes(minutes) + PerDocument * documents;
    }
}

/// <summary>One page a repair will read, and what decides how long it takes.</summary>
public sealed record PlannedRepairPage(string Path, int PageNumber, PageKind Kind, int Dpi);

/// <summary>
/// The pages a repair with given options will read, worked out without reading any of them, so the
/// cost can be stated before the work is started.
/// </summary>
public sealed record RepairPlan(IReadOnlyList<PlannedRepairPage> Pages, int Documents)
{
    public TimeSpan Estimate => RepairThroughput.Estimate(Pages, Documents);
}
