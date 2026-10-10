using ManualForge.Core.Ocr;

namespace ManualForge.Core.Pipeline;

/// <summary>A stretch of a run at one level of pages in flight: how far it got, and what the card looked like after.</summary>
public sealed record ConcurrencyWindow(int Level, int Pages, TimeSpan Elapsed, GpuMemory? Memory)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : Pages / Elapsed.TotalMinutes;
}

/// <summary>A change of pages in flight, and why.</summary>
public sealed record ConcurrencyDecision(int From, int To, string Reason);

/// <summary>
/// Chooses how many pages to keep in flight on the GPU while a run is going, from what the run is
/// actually doing on this card - not from a figure measured on another one (#31).
///
/// <para>
/// The shape it has to find, measured on two cards: throughput rises with pages in flight while there
/// is memory to hold them, levels off, and then - once the card is full and the driver starts paging
/// to system memory - collapses by an order of magnitude, with no error raised (83 to 7.6 pages a
/// minute on an 8 GB RTX 3060 Ti, 91.4 to 13.7 on a 16 GB RTX 5070 Ti). Where the knee and the cliff
/// sit depends on the card, on what else is using it, and on the pages.
/// </para>
///
/// <para>
/// So it climbs one page at a time and keeps a step only if it pays - more than <see cref="Gain"/>
/// faster, over windows long enough to average a mixed run of pages - and only while free memory
/// leaves room for another. It steps down at once when free memory falls into the reserve, whether
/// this run or a browser opened beside it took it, or when throughput falls to half of what a lower
/// level managed, which is what spilling looks like. A level that went over the edge is not tried
/// again, and is remembered for the card.
/// </para>
///
/// <para>
/// Pages in flight never change what is read - 1 to 4 pages gave the same 36,001 words - which is
/// what makes it safe to tune while running. Recognition batch size does change the words, and is
/// never touched here.
/// </para>
/// </summary>
public sealed class ConcurrencyController
{
    private readonly Dictionary<int, List<double>> _rates = [];
    private readonly Dictionary<int, List<int>> _used = [];
    private int _windowsAtLevel;
    private int? _probedFrom;
    private bool _settled;
    private int _accepted;

    /// <param name="start">Pages in flight to begin with: what this card settled on last time, or a cautious guess.</param>
    /// <param name="maximum">Never more than this, whatever the card seems able to take.</param>
    /// <param name="knownUnsafe">The fewest pages in flight this card has been seen to spill at, if any.</param>
    public ConcurrencyController(int start, int maximum, int? knownUnsafe = null)
    {
        Ceiling = Math.Max(1, Math.Min(maximum, knownUnsafe is { } bad ? bad - 1 : maximum));
        Level = Math.Clamp(start, 1, Ceiling);
        UnsafeFound = knownUnsafe;
        _accepted = Level;
    }

    /// <summary>
    /// How much faster a step up must be to be kept. Identical whole runs differ by about 5% on this
    /// hardware (86.8 against 82.9 pages a minute), and a window is shorter than a run, so a smaller
    /// gain could not be told apart from noise.
    /// </summary>
    public double Gain { get; init; } = 0.05;

    /// <summary>Windows measured at a level before it is judged.</summary>
    public int WindowsPerJudgement { get; init; } = 2;

    /// <summary>Throughput below this share of a lower level's is taken for spilling.</summary>
    public double CollapseShare { get; init; } = 0.5;

    public int Level { get; private set; }

    /// <summary>The most pages in flight this run may still try.</summary>
    public int Ceiling { get; private set; }

    /// <summary>The fewest pages in flight known to be unsafe on this card, from before the run or found in it.</summary>
    public int? UnsafeFound { get; private set; }

    /// <summary>
    /// The level to remember for this card: the last step up that paid. Not the fastest window seen,
    /// which includes steps given back for gaining too little, and not the level the run ended on,
    /// which a browser taking memory for ten minutes may have pushed down.
    /// </summary>
    public int BestLevel => _accepted;

    /// <summary>
    /// The memory kept free: 5% of the card, and never less than 512 MiB. A share rather than a
    /// figure, so that it means the same on an 8 GB card as on a 32 GB one.
    /// </summary>
    public static int ReserveMiB(GpuMemory memory) => Math.Max(512, memory.TotalMiB / 20);

    /// <summary>Takes in one window, and returns the change to make, if any.</summary>
    public ConcurrencyDecision? Observe(ConcurrencyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A window measured before the last change took effect describes the old level.
        if (window.Level != Level || window.Pages <= 0)
            return null;

        var rate = window.PagesPerMinute;
        Record(_rates, Level, rate);
        if (window.Memory is { } m)
            Record(_used, Level, m.UsedMiB);
        _windowsAtLevel++;

        // Out of memory, whoever took it: step down now, before the driver starts paging. Held
        // against the card only if this run's own step up did it; memory another program took is
        // a fact about this afternoon, not about the card.
        if (window.Memory is { } memory && memory.FreeMiB < ReserveMiB(memory) && Level > 1)
            return StepDown($"{memory.FreeMiB:N0} MiB free, under the {ReserveMiB(memory):N0} MiB reserve",
                againstTheCard: _probedFrom is not null);

        // Fallen off the cliff: a lower level was at least twice as fast.
        var lower = _rates.Where(kv => kv.Key < Level && kv.Value.Count > 0).Select(kv => kv.Value.Average()).DefaultIfEmpty(0).Max();
        if (Level > 1 && lower > 0 && rate < CollapseShare * lower)
            return StepDown($"{rate:F1} pages/min, under half the {lower:F1} a lower level managed: spilling",
                againstTheCard: true);

        if (_windowsAtLevel < WindowsPerJudgement)
            return null;

        // A step up being judged: keep it only if it paid.
        if (_probedFrom is { } previous)
        {
            var now = _rates[Level].Average();
            var before = _rates[previous].Average();
            _probedFrom = null;

            if (now < before * (1 + Gain))
            {
                var tried = Level;
                Ceiling = previous;
                _settled = true;
                return Move(previous, $"{tried} pages gave {now:F1} pages/min against {before:F1} at {previous}: not worth it");
            }

            _accepted = Level;
        }

        if (_settled || Level >= Ceiling)
        {
            _settled = true;
            return null;
        }

        // Room for one more? The cost of a page is what the last step up added, or a tenth of the
        // card before any step has been seen.
        if (window.Memory is { } room)
        {
            var growth = Growth(room);
            if (room.FreeMiB - growth < ReserveMiB(room))
            {
                _settled = true;
                return null;
            }
        }

        _probedFrom = Level;
        return Move(Level + 1, $"trying {Level + 1} at {_rates[Level].Average():F1} pages/min");
    }

    private int Growth(GpuMemory memory)
    {
        var steps = _used.Keys.Where(k => _used.ContainsKey(k + 1))
            .Select(k => (int)(_used[k + 1].Max() - _used[k].Max()))
            .Where(g => g > 0)
            .ToList();
        return steps.Count > 0 ? steps.Max() : memory.TotalMiB / 10;
    }

    private ConcurrencyDecision StepDown(string reason, bool againstTheCard)
    {
        if (againstTheCard)
        {
            UnsafeFound = Math.Min(UnsafeFound ?? int.MaxValue, Level);
            _accepted = Math.Min(_accepted, Level - 1);
        }

        Ceiling = Math.Min(Ceiling, Level - 1);
        _probedFrom = null;
        _settled = true;
        return Move(Level - 1, reason);
    }

    private ConcurrencyDecision Move(int to, string reason)
    {
        var decision = new ConcurrencyDecision(Level, to, reason);
        Level = to;
        _windowsAtLevel = 0;
        return decision;
    }

    private static void Record<T>(Dictionary<int, List<T>> into, int level, T value)
    {
        if (!into.TryGetValue(level, out var list))
            into[level] = list = [];
        list.Add(value);
    }
}
