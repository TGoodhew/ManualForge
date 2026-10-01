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
/// Measured on 1 October 2026 over the 21,262 repairs on record, from the time between consecutive
/// pages of the same document on an RTX 3060 Ti. The kind of page matters most: a drawn page
/// carries a few labels, while a scanned page is dense lettering the recogniser has to read word by
/// word. Resolution matters second. The suggested resolution was the first guess at the cause, and
/// on its own it does not even order the rates - 400 dpi ran faster than 300 - because it mostly
/// stands in for the mix of kinds.
/// </para>
///
/// <list type="table">
///   <listheader><term>pages/min</term><description>300 dpi | 400 dpi | 600 dpi</description></listheader>
///   <item><term>Drawn</term><description>77.8 (3,022) | 65.6 (532) | 41.9 (353)</description></item>
///   <item><term>Raster</term><description>35.1 (13,856) | 39.3 (1,050) | 25.0 (390)</description></item>
/// </list>
///
/// <para>
/// Each document also costs a little before its first page - hashing it, reading it, opening it -
/// about 2 seconds on the 1 October run, whose records are all still on file. Earlier runs suggest
/// 15, but only because pages repaired again since have vanished from between their neighbours, so
/// their gaps span whole documents that are no longer there.
/// </para>
///
/// <para>
/// Checked against the two large repairs on record: 20,758 pages that took 532 minutes are
/// estimated at 537, and 4,201 that took 127 at 125. The single rate said 451 and 91.
/// </para>
/// </summary>
public static class RepairThroughput
{
    /// <summary>Time spent on a document before its first page is read.</summary>
    public static readonly TimeSpan PerDocument = TimeSpan.FromSeconds(2);

    /// <summary>Pages a minute for a page of this kind read at this resolution.</summary>
    public static double PagesPerMinute(PageKind kind, int dpi) => (kind, dpi) switch
    {
        (PageKind.Drawn, <= 300) => 77.8,
        (PageKind.Drawn, <= 400) => 65.6,
        (PageKind.Drawn, _) => 41.9,
        (_, <= 300) => 35.1,
        (_, <= 400) => 39.3,
        _ => 25.0,
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
