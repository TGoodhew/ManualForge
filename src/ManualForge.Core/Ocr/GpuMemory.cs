using System.Diagnostics;
using System.Globalization;

namespace ManualForge.Core.Ocr;

/// <summary>
/// What the card has, how much of it somebody else is already using, and how busy it is.
/// </summary>
/// <param name="UtilisationPercent">
/// How much of the last sampling period the GPU spent executing. Null when it was not asked for.
/// Worth showing next to the memory figure rather than instead of it: this application's two
/// constraints pull in opposite directions, with utilisation saying whether to send more work and
/// free memory saying whether there is room to.
/// </param>
public sealed record GpuMemory(int TotalMiB, int UsedMiB, string? Name, int? UtilisationPercent = null)
{
    public int FreeMiB => Math.Max(0, TotalMiB - UsedMiB);

    public int UsedPercent => TotalMiB <= 0 ? 0 : (int)Math.Round(100.0 * UsedMiB / TotalMiB);

    public override string ToString() =>
        $"{Name ?? "GPU"}: {FreeMiB:N0} MiB free of {TotalMiB:N0}"
        + (UtilisationPercent is { } u ? $", {u}% busy" : string.Empty);
}

/// <summary>
/// Reads VRAM from nvidia-smi, which is the only way to see it without taking a dependency on
/// NVML or CUDA interop.
///
/// This matters more than a display statistic. Running more pages through the engine at once is
/// the single biggest throughput lever available — the GPU averages 40% utilisation with one page
/// in flight, because each page alternates CPU phases with GPU phases — but going past what VRAM
/// holds does not fail cleanly. Windows lets the driver spill into system memory over PCIe, and
/// throughput falls off a cliff rather than erroring: measured at 83 pages/min with room to spare
/// and 7.6 pages/min once the card was full. An order of magnitude slower, with no exception to
/// tell you why.
///
/// So concurrency is chosen from free VRAM at startup, and the figure that matters is free rather
/// than total: the desktop, a browser and whatever else is running already hold 2.6-2.9 GB of this
/// 8 GB card before any work begins.
/// </summary>
public static class GpuMemoryProbe
{
    /// <summary>
    /// Budgeted cost of each extra page in flight. A budget rather than a measurement, and the
    /// distinction is worth being honest about.
    ///
    /// What was actually observed on an 8 GB card is that peak VRAM barely moved with concurrency
    /// on a single document — 7,613, 7,655 and 7,709 MiB at one, two and three pages — because
    /// ONNX Runtime's arena is already grown by the time the second page arrives. But across
    /// twenty-four pages of varied size the same card reached 7,950 MiB and collapsed. So the
    /// marginal cost is driven by the variety of page sizes the arena has to accommodate, not by
    /// the number of pages in flight, and no constant describes it properly.
    ///
    /// 700 MiB is therefore chosen to be comfortably larger than anything observed, so that the
    /// arithmetic errs towards fewer pages. The failure it is erring away from is not a slowdown.
    /// </summary>
    public const int MarginalMiBPerConcurrentPage = 700;

    /// <summary>Headroom left unused, so that a desktop that grows does not push the run over.</summary>
    public const int ReserveMiB = 512;

    /// <summary>
    /// Reads the first CUDA device, or null if nvidia-smi is not there. Memory and utilisation come
    /// from one invocation because the UI samples this on a timer and each call is a process start.
    /// </summary>
    public static GpuMemory? TryRead()
    {
        var line = RunNvidiaSmi(
            "--query-gpu=memory.total,memory.used,name,utilization.gpu --format=csv,noheader,nounits");
        if (line is null)
            return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used))
        {
            return null;
        }

        int? utilisation = parts.Length > 3
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var busy)
            ? busy
            : null;

        return new GpuMemory(total, used, parts.Length > 2 ? parts[2] : null, utilisation);
    }

    /// <summary>
    /// How many pages to keep in flight on the GPU, given what is free right now.
    /// </summary>
    /// <param name="memory">What the probe found, or null when it could not look.</param>
    /// <param name="ceiling">
    /// Never go above this however much memory there is.
    ///
    /// Two by default, not three. Three was the fastest setting measured — 83.3 pages a minute
    /// against 78.1 — but it was also the setting that tipped an 8 GB card into spilling once the
    /// pages were varied enough, and the difference between those two figures is 6% while the
    /// difference between either and spilling is a factor of ten. Three is available by asking for
    /// it explicitly, which is the right way round for a choice with that shape.
    /// </param>
    /// <remarks>
    /// One is always safe, because it is what a single page needs and that is already accounted
    /// for by the time this is asked. When the probe finds nothing — no NVIDIA card, no
    /// nvidia-smi, or CPU execution — one is also the right answer: on CPU the work is already
    /// spread across every core inside the engine, and adding outer concurrency only contends.
    /// </remarks>
    public static int ConcurrencyFor(GpuMemory? memory, int ceiling = 2)
    {
        if (memory is null || ceiling < 1)
            return 1;

        var spare = memory.FreeMiB - ReserveMiB;
        if (spare <= 0)
            return 1;

        return Math.Clamp(1 + spare / MarginalMiBPerConcurrentPage, 1, ceiling);
    }

    private static string? RunNvidiaSmi(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* best effort */ }
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch (Exception)
        {
            // No nvidia-smi, no NVIDIA driver, or no permission. None of those is an error here:
            // it just means we cannot see VRAM and must assume there is none to spare.
            return null;
        }
    }
}
