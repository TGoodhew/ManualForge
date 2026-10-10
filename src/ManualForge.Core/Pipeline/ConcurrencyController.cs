namespace ManualForge.Core.Pipeline;

/// <summary>
/// A stretch of a run at one level of pages in flight: how far it got, and how much of this process's
/// GPU memory had been pushed out into system memory by the end of it. Null when that cannot be read.
/// </summary>
public sealed record ConcurrencyWindow(int Level, int Pages, TimeSpan Elapsed, int? SpilledMiB)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : Pages / Elapsed.TotalMinutes;
}

/// <summary>A change of pages in flight, and why.</summary>
public sealed record ConcurrencyDecision(int From, int To, string Reason);

/// <summary>
/// Watches a run for the card going over the edge, and steps its pages in flight down when it does.
/// It never steps up: finding how many a card can take is done one run at a time (<see cref="GpuTuning"/>), because
/// trying more within a run costs the rest of that run (#31).
///
/// <para>
/// The edge: once the card is full, the driver starts keeping this process's memory in system RAM, and
/// throughput falls with no error raised (83 to 7.6 pages a minute on an 8 GB RTX 3060 Ti; 86 to 46 on a
/// 16 GB RTX 5070 Ti when 322 MiB went out). Where it is depends on the card, on what else is using
/// it, and on the pages.
/// </para>
///
/// <para>
/// Free memory does not say where it is: ONNX Runtime's arena grows into whatever the card has, so a
/// healthy run leaves it nearly full. What does say is the memory going out to system RAM, which
/// Windows counts per process: a steady 76 MiB at three pages, 322 at four, 4.3 GB at six.
/// </para>
///
/// <para>
/// Why not climb within a run: the arena never gives memory back, and grows in steps that double. A run
/// that tried four pages and went back to three kept four's arena, and spilled at three where a run
/// that started at three never did - 68.6 pages a minute against 79.6, on the same pages (10 Oct 2026,
/// docs/measurements/gpu-autotuning.md).
/// </para>
/// </summary>
public sealed class ConcurrencyController
{
    private int? _baseline;
    private int _pages;

    /// <param name="level">Pages in flight for this run.</param>
    public ConcurrencyController(int level)
    {
        Level = Math.Max(1, level);
        StartLevel = Level;
    }

    /// <summary>
    /// How far this process's memory in system RAM may rise above the least it has held there before
    /// it counts as spilling. A healthy run holds a steady few dozen MiB there (76 on the 5070 Ti); the
    /// smallest spill measured added 246.
    /// </summary>
    public int SpillMarginMiB { get; init; } = 128;

    /// <summary>Pages in flight now.</summary>
    public int Level { get; private set; }

    /// <summary>Pages in flight the run began with.</summary>
    public int StartLevel { get; }

    /// <summary>The level at which this run first spilled, or null if it never did.</summary>
    public int? SpilledAt { get; private set; }

    /// <summary>Whether the spill could be read at all. A run that could not see it has proved nothing.</summary>
    public bool Watched { get; private set; }

    /// <summary>Pages the GPU has read in this run, not counting any taken from the cache.</summary>
    public int Pages => Volatile.Read(ref _pages);

    /// <summary>Counts one page read.</summary>
    public void PageRead() => Interlocked.Increment(ref _pages);

    /// <summary>What the process held in system RAM before the first page, once the models were loaded.</summary>
    public void Begin(int? spilledMiB)
    {
        if (spilledMiB is { } mib)
            _baseline = Math.Min(_baseline ?? mib, mib);
    }

    /// <summary>Takes in one window, and returns the change to make, if any.</summary>
    public ConcurrencyDecision? Observe(ConcurrencyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.SpilledMiB is not { } spilled)
            return null;

        Watched = true;
        _baseline = Math.Min(_baseline ?? spilled, spilled);

        // Measured from the least the run has held there. After a step down the mark moves up to what
        // is out now: the arena does not hand back what went, and the question then is whether more goes.
        if (spilled - _baseline <= SpillMarginMiB)
            return null;

        var gone = spilled - _baseline.Value;
        _baseline = spilled;
        SpilledAt ??= Level;

        if (Level <= 1)
            return null;

        var decision = new ConcurrencyDecision(
            Level, Level - 1, $"{gone:N0} MiB of GPU memory gone out to system RAM: spilling");
        Level--;
        return decision;
    }
}
