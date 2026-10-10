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
/// Chooses how many pages to keep in flight on the GPU while a run is going, from what the run is
/// actually doing on this card - not from a figure measured on another one (#31).
///
/// <para>
/// The shape it has to find: throughput rises with pages in flight, levels off, and then - once the
/// card is full and the driver starts keeping this process's memory in system RAM - falls, with no
/// error raised (83 to 7.6 pages a minute on an 8 GB RTX 3060 Ti; 86 to 46 on a 16 GB RTX 5070 Ti
/// when 322 MiB went out). Where that happens depends on the card, on what else is using it, and on
/// the pages.
/// </para>
///
/// <para>
/// Free memory does not say where it is. ONNX Runtime's arena grows into whatever the card has, so a
/// healthy run at three pages leaves under a gigabyte free, and a first version of this that kept a
/// reserve stepped back from settings that were fine. What does say is the memory itself going out
/// to system RAM: Windows counts it per process, and it sat at 76 MiB at three pages, went to 322 at
/// four and to 4.3 GB at six (docs/measurements/gpu-autotuning.md).
/// </para>
///
/// <para>
/// So it climbs one page at a time, keeping a step only if it pays - more than <see cref="Gain"/>
/// faster over windows long enough to average a mixed run of pages - and steps down at once when this
/// process's memory starts going out, or when throughput falls under half what a lower level managed.
/// A level that went over the edge while being tried is not tried again, and is remembered for the
/// card. One that went over later, after it had been fine, may have been pushed by another program,
/// so it only lowers where the next run starts.
/// </para>
///
/// <para>
/// Pages in flight never change what is read - 1 to 6 pages gave the same 36,001 words - which is
/// what makes it safe to tune while running. Recognition batch size does change the words, and is
/// never touched here.
/// </para>
/// </summary>
public sealed class ConcurrencyController
{
    private readonly Dictionary<int, List<double>> _rates = [];
    private int _windowsAtLevel;
    private int? _probedFrom;
    private bool _settled;
    private int _accepted;
    private int? _spillBaseline;

    /// <param name="start">Pages in flight to begin with: what this card settled on last time, or one.</param>
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

    /// <summary>
    /// How far this process's memory in system RAM may rise above the least it has held there before
    /// it counts as spilling. A healthy run holds a steady few dozen MiB there (76 on the 5070 Ti); the
    /// smallest spill measured added 246.
    /// </summary>
    public int SpillMarginMiB { get; init; } = 128;

    public int Level { get; private set; }

    /// <summary>The most pages in flight this run may still try.</summary>
    public int Ceiling { get; private set; }

    /// <summary>The fewest pages in flight known to be unsafe on this card, from before the run or found in it.</summary>
    public int? UnsafeFound { get; private set; }

    /// <summary>
    /// The level to remember for this card: the last step up that paid, less any that later spilled.
    /// Not the fastest window seen, which includes steps given back for gaining too little.
    /// </summary>
    public int BestLevel => _accepted;

    /// <summary>Takes in one window, and returns the change to make, if any.</summary>
    public ConcurrencyDecision? Observe(ConcurrencyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A window measured before the last change took effect describes the old level.
        if (window.Level != Level || window.Pages <= 0)
            return null;

        var rate = window.PagesPerMinute;
        Record(_rates, Level, rate);
        _windowsAtLevel++;

        // Memory going out to system RAM, measured from the least this run has held there. After a
        // step down the mark moves up to what is out now: the arena does not hand back what went,
        // and the question then is whether more goes.
        if (window.SpilledMiB is { } spilled)
        {
            _spillBaseline = Math.Min(_spillBaseline ?? spilled, spilled);
            if (Level > 1 && spilled - _spillBaseline > SpillMarginMiB)
            {
                var decision = StepDown(
                    $"{spilled - _spillBaseline:N0} MiB of GPU memory gone out to system RAM: spilling",
                    againstTheCard: _probedFrom is not null);
                _spillBaseline = spilled;
                return decision;
            }
        }

        // Fallen off the cliff: a lower level was at least twice as fast.
        var lower = _rates.Where(kv => kv.Key < Level && kv.Value.Count > 0).Select(kv => kv.Value.Average()).DefaultIfEmpty(0).Max();
        if (Level > 1 && lower > 0 && rate < CollapseShare * lower)
            return StepDown($"{rate:F1} pages/min, under half the {lower:F1} a lower level managed: spilling",
                againstTheCard: _probedFrom is not null);

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

        _probedFrom = Level;
        return Move(Level + 1, $"trying {Level + 1} at {_rates[Level].Average():F1} pages/min");
    }

    /// <summary>
    /// Down one, for the rest of the run. A level that spilled while being tried is a fact about the
    /// card and is remembered as unsafe. One that spilled after it had been fine may have been pushed
    /// by another program, so it only lowers where the next run starts, and that run may climb again.
    /// </summary>
    private ConcurrencyDecision StepDown(string reason, bool againstTheCard)
    {
        if (againstTheCard)
            UnsafeFound = Math.Min(UnsafeFound ?? int.MaxValue, Level);

        _accepted = Math.Min(_accepted, Level - 1);
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
