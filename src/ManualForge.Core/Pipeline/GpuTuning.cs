using ManualForge.Core.Ocr;

namespace ManualForge.Core.Pipeline;

/// <summary>
/// How a run chooses its pages in flight, and what it leaves behind for the next run on the same
/// card. Shared by the command line and the app so the two cannot drift apart.
///
/// <para>
/// The card is tuned one run at a time (#31). Each run keeps one setting, stepping down only if its
/// memory starts going out to system RAM. A run that finishes clean lets the next try one more; the
/// first setting that spills is remembered and never tried again. So the card settles on the most
/// pages in flight it holds, which on every card measured was also the fastest: each page added was
/// faster than the last until the card spilled (50, 75, 87, 91 pages a minute at one to four on the
/// RTX 5070 Ti), and spilling was slower than any of them.
/// </para>
///
/// <para>
/// Not within a run, because the memory arena never shrinks: a run that tried one more and went back
/// kept the larger arena, and spilled where a run that started lower never did.
/// </para>
/// </summary>
public sealed class GpuTuning
{
    /// <summary>
    /// The most pages in flight any run will try. Not a property of any card: set above anything one has
    /// been seen to want (four was the most a 16 GB card held), so that the card, not this, decides.
    /// </summary>
    public const int MaximumInFlight = 8;

    /// <summary>
    /// Where a card never seen before starts. Two held on the smallest card measured (8 GB) and doubled
    /// one page's speed on both; a card that cannot hold two spills, steps down within the run, and is
    /// not asked again. Starting at one would make a first run, which is usually the longest, a third
    /// slower everywhere else.
    /// </summary>
    public const int FirstGuess = 2;

    /// <summary>
    /// How far a CUDA run's memory in system RAM may rise before it counts as spilling. Measured on the
    /// RTX 5070 Ti (#31): a healthy run holds a steady 76 MiB there, and the smallest real spill added
    /// 246 and nearly halved the speed.
    /// </summary>
    public const int CudaSpillMarginMiB = 128;

    /// <summary>
    /// The same for TensorRT, which moves about 130 MiB out early in a run and brings most of it back
    /// within a minute, at no cost to speed: 132 and 68 MiB at three pages in flight, the fastest
    /// setting, on 11 Oct 2026. CUDA's margin took that for a spill and held the card at one page for
    /// good (#56). Its real spills were past 1.1 GB within seconds and went on growing, so anything
    /// between the two tells them apart.
    /// </summary>
    public const int TensorRtSpillMarginMiB = 512;

    /// <summary>The spill margin for the engine on this card.</summary>
    public static int SpillMarginFor(GpuIdentity? gpu) => gpu?.IsTensorRt == true ? TensorRtSpillMarginMiB : CudaSpillMarginMiB;

    private GpuTuning(GpuIdentity? gpu, GpuProfile? profile, ConcurrencyController? tuner, int fixedConcurrency)
    {
        Gpu = gpu;
        Profile = profile;
        Tuner = tuner;
        FixedConcurrency = fixedConcurrency;
    }

    public GpuIdentity? Gpu { get; }

    /// <summary>What was known about this card before the run.</summary>
    public GpuProfile? Profile { get; }

    /// <summary>Null when pages in flight are fixed: on the CPU, or when asked for.</summary>
    public ConcurrencyController? Tuner { get; }

    public int FixedConcurrency { get; }

    /// <summary>Whether this run is trying more pages in flight than the card has yet been seen to hold.</summary>
    public bool Exploring => Tuner is not null && Tuner.StartLevel > (Profile?.BestConcurrency ?? 0);

    /// <summary>
    /// On the GPU, tuned unless <paramref name="fixedConcurrency"/> says otherwise. On the CPU, one: the
    /// engine already spreads a page across every core, and more pages only contend.
    /// </summary>
    /// <param name="engine">The execution provider the run is using, which the card's profile is filed under.</param>
    public static GpuTuning For(bool usingGpu, int? fixedConcurrency, GpuProfileStore store, string? engine = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!usingGpu)
            return new GpuTuning(null, null, null, Math.Max(1, fixedConcurrency ?? 1));

        var gpu = GpuMemoryProbe.TryIdentify(engine);
        return ForCard(gpu, store.Find(gpu), fixedConcurrency);
    }

    /// <summary>The same, on a card already identified.</summary>
    internal static GpuTuning ForCard(GpuIdentity? gpu, GpuProfile? profile, int? fixedConcurrency)
    {
        profile = Forgiven(profile, gpu);

        if (fixedConcurrency is { } n)
            return new GpuTuning(gpu, profile, null, Math.Max(1, n));

        var tuner = new ConcurrencyController(StartFor(profile)) { SpillMarginMiB = SpillMarginFor(gpu) };
        return new GpuTuning(gpu, profile, tuner, tuner.Level);
    }

    /// <summary>
    /// A profile with its unsafe setting dropped when the card is on a different driver from the one
    /// it was learned on. A spill is a fact about the card, the engine and the driver together, and a
    /// driver changes how memory goes out to system RAM. Only the ceiling goes: the best setting and the
    /// measured speeds still hold, and the next run finds the edge again if it is still there.
    /// </summary>
    internal static GpuProfile? Forgiven(GpuProfile? profile, GpuIdentity? gpu)
        => profile is { UnsafeConcurrency: not null, Driver: { } learnedOn }
           && gpu?.Driver is { } now
           && !string.Equals(learnedOn, now, StringComparison.OrdinalIgnoreCase)
            ? profile with { UnsafeConcurrency = null }
            : profile;

    /// <summary>
    /// Forgets the pages in flight learned for this card and engine, so the next run tunes them again
    /// from <see cref="FirstGuess"/>. The measured speeds are kept: they are what every estimate on the
    /// card is made from, and nothing about tuning made them wrong. `manualforge gpu --retune`.
    /// </summary>
    public static GpuProfile? Retune(GpuProfileStore store, GpuIdentity? gpu)
    {
        ArgumentNullException.ThrowIfNull(store);
        return gpu is null
            ? null
            : store.Update(gpu, p => p with { BestConcurrency = null, UnsafeConcurrency = null });
    }

    /// <summary>
    /// One more than the most the card has held, unless that is known to spill or is past the maximum;
    /// <see cref="FirstGuess"/> on a card never seen.
    /// </summary>
    public static int StartFor(GpuProfile? profile)
    {
        var limit = profile?.UnsafeConcurrency is { } bad ? Math.Max(1, bad - 1) : MaximumInFlight;
        var wanted = profile?.BestConcurrency is { } best ? best + 1 : FirstGuess;
        return Math.Clamp(wanted, 1, Math.Min(limit, MaximumInFlight));
    }

    /// <summary>The pipeline settings this implies, with whatever else the caller wants.</summary>
    public PipelineOptions Options(
        int rasterWorkers,
        Action<ConcurrencyDecision>? onTuned = null,
        IProgress<PipelinePageProgress>? progress = null) => new()
    {
        GpuConcurrency = FixedConcurrency,
        Tuner = Tuner,
        OnTuned = onTuned,
        RasterWorkers = Math.Max(1, rasterWorkers),
        Progress = progress,
    };

    /// <summary>One line for the console or the log: what is being done, and on what it is based.</summary>
    public string Describe()
    {
        if (Tuner is null)
            return $"{FixedConcurrency} page(s) on the GPU at once" + (Gpu is null ? "" : " (set on the command line)");

        var level = Tuner.StartLevel;
        if (Profile?.BestConcurrency is not { } best)
            return $"{level} page(s) on the GPU at once, the first try on this card";

        return Exploring
            ? $"{level} page(s) on the GPU at once, one more than this card has held so far ({best})"
            : $"{level} page(s) on the GPU at once, the most this card holds" +
              (Profile.UnsafeConcurrency is { } bad ? $" ({bad} spilled)" : "");
    }

    /// <summary>
    /// Records what this run learned about the card and returns the profile as it now stands, or null
    /// with no card to file it under. A clean run that read fewer than
    /// <see cref="MeasuredThroughput.PagesToLearnFrom"/> pages has not met enough to vouch for a
    /// setting. <paramref name="measured"/> is end to end, over the pages this run recognised itself;
    /// null for a run too short to say (<see cref="MeasuredRun.From"/>).
    /// </summary>
    public GpuProfile? Remember(GpuProfileStore store, MeasuredRun? measured)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Gpu is null)
            return null;

        return store.Update(Gpu, stored =>
        {
            var p = Forgiven(stored, Gpu)!;

            // Weighed against what was known by the pages behind each (#63), so one unusual run moves
            // the estimate without setting it.
            var learned = p;
            if (measured is not null)
            {
                var (rate, pages) = MeasuredRate.Fold(
                    p.RunPagesPerMinute, p.RunPagesMeasured, measured.PagesPerMinute, measured.Pages);
                learned = p with { RunPagesPerMinute = rate, RunPagesMeasured = pages };
            }

            if (Tuner is not { Watched: true } tuner)
                return learned;

            if (tuner.SpilledAt is { } spilled)
            {
                // Spilling while trying a setting is a fact about the card. Spilling at one it has held
                // before may be a game or a browser this afternoon: step the next run down, and let it
                // climb again, but do not rule the setting out.
                return learned with
                {
                    BestConcurrency = Math.Max(1, spilled - 1),
                    UnsafeConcurrency = Exploring ? Lowest(p.UnsafeConcurrency, spilled) : p.UnsafeConcurrency,
                };
            }

            return tuner.Pages >= MeasuredThroughput.PagesToLearnFrom
                ? learned with { BestConcurrency = Math.Max(p.BestConcurrency ?? 0, tuner.StartLevel) }
                : learned;
        });
    }

    /// <summary>What happened, and what the next run on this card will do, for the end of a run.</summary>
    public string Outcome(GpuProfile? after)
    {
        if (Tuner is null)
            return string.Empty;

        var what = Tuner.SpilledAt is { } spilled
            ? $"spilled at {spilled} and stepped down to {Tuner.Level}"
            : Tuner.Watched ? $"{Tuner.StartLevel} page(s) in flight held without spilling"
            : $"{Tuner.StartLevel} page(s) in flight; spilling could not be read, so nothing was learned";

        return after is null ? what : $"{what}. Next run: {StartFor(after)}";
    }

    private static int? Lowest(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
