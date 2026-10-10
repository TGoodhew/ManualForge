using ManualForge.Core.Ocr;

namespace ManualForge.Core.Pipeline;

/// <summary>
/// How a run chooses its pages in flight, and what it leaves behind for the next run on the same
/// card. Shared by the command line and the app so the two cannot drift apart.
/// </summary>
public sealed class GpuTuning
{
    /// <summary>
    /// The most pages in flight a tuned run will try. Not a property of any card: a ceiling on how
    /// many workers are started, set above anything a card has been seen to want (four was fastest
    /// on 16 GB, and six fell off the cliff), so the tuner rather than this decides.
    /// </summary>
    public const int MaximumInFlight = 8;

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

    /// <summary>
    /// On the GPU, tuned unless <paramref name="fixedConcurrency"/> says otherwise, starting where this
    /// card settled last time - or at one, on a card never seen, and climbing from there. On the CPU,
    /// one: the engine already spreads a page across every core, and more pages only contend.
    /// </summary>
    public static GpuTuning For(bool usingGpu, int? fixedConcurrency, GpuProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!usingGpu)
            return new GpuTuning(null, null, null, Math.Max(1, fixedConcurrency ?? 1));

        var gpu = GpuMemoryProbe.TryIdentify();
        return ForCard(gpu, store.Find(gpu), fixedConcurrency);
    }

    /// <summary>The same, on a card already identified.</summary>
    internal static GpuTuning ForCard(GpuIdentity? gpu, GpuProfile? profile, int? fixedConcurrency)
    {
        if (fixedConcurrency is { } n)
            return new GpuTuning(gpu, profile, null, Math.Max(1, n));

        var tuner = new ConcurrencyController(profile?.BestConcurrency ?? 1, MaximumInFlight, profile?.UnsafeConcurrency);
        return new GpuTuning(gpu, profile, tuner, tuner.Level);
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

        var history = Profile?.BestConcurrency is { } best
            ? $"settled on {best} last time on this card"
            : "first run on this card";
        var limit = Profile?.UnsafeConcurrency is { } bad ? $", never {bad} or more" : "";
        return $"tuned as it runs, starting at {Tuner.Level} ({history}{limit})";
    }

    /// <summary>
    /// Records what this run learned about the card: where it settled, any cliff it found, and how
    /// fast it went. <paramref name="pagesPerMinute"/> is end to end, and is left out for a run too
    /// short to say anything.
    /// </summary>
    public void Remember(GpuProfileStore store, double? pagesPerMinute)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Gpu is null)
            return;

        store.Update(Gpu, p => p with
        {
            BestConcurrency = Tuner?.BestLevel ?? p.BestConcurrency,
            UnsafeConcurrency = Lowest(p.UnsafeConcurrency, Tuner?.UnsafeFound),
            // Averaged with the last, so one unusual library does not set every estimate after it.
            RunPagesPerMinute = pagesPerMinute is not { } rate ? p.RunPagesPerMinute
                : p.RunPagesPerMinute is { } before ? (before + rate) / 2
                : rate,
        });
    }

    private static int? Lowest(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
