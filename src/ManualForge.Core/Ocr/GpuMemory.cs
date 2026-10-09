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
/// the single biggest throughput lever available — the GPU is idle for most of each page with one
/// in flight, because each page alternates CPU phases with GPU phases — but going past what VRAM
/// holds does not fail cleanly. Windows lets the driver spill into system memory over PCIe, and
/// throughput falls off a cliff rather than erroring. Measured on both cards this has run on: an
/// 8 GB RTX 3060 Ti went from 83 pages/min with room to spare to 7.6 once full, and a 16 GB
/// RTX 5070 Ti from 91.4 at four pages to 13.7 at six. An order of magnitude slower, with no
/// exception to tell you why.
///
/// So concurrency is chosen from free VRAM at startup, and the figure that matters is free rather
/// than total: the desktop, a browser and whatever else is running hold 2.6-2.9 GB of either card
/// before any work begins.
/// </summary>
public static class GpuMemoryProbe
{
    /// <summary>
    /// Budgeted cost of each extra page in flight. A budget rather than a measurement, and the
    /// distinction is worth being honest about.
    ///
    /// ONNX Runtime's arena grows into whatever room it is given, so what a page costs depends on
    /// the card. On an 8 GB card peak VRAM barely moved with concurrency on a single document —
    /// 7,613, 7,655 and 7,709 MiB at one, two and three pages — because there was no more room to
    /// grow into, and across twenty-four pages of varied size it reached 7,950 MiB and collapsed.
    /// On a 16 GB card, over a hundred varied pages, each extra page added 1.5-3.6 GB: 8.2, 11.8,
    /// 13.3 and 15.7 GB at one to four pages (docs/measurements/gpu-concurrency-5070ti.md).
    ///
    /// 3,000 MiB is the middle of what the larger card measured, rounded up. It was 700, chosen
    /// on the 8 GB card where nothing larger was ever seen, and at 700 the arithmetic would have
    /// put eighteen pages on the 16 GB card: only the ceiling kept it off the cliff. Erring
    /// towards fewer pages is the right way round, because the failure it is erring away from is
    /// not a slowdown.
    /// </summary>
    public const int MarginalMiBPerConcurrentPage = 3_000;

    /// <summary>
    /// The smallest card that gets three pages in flight by default. 16 GB cards report a little
    /// under 16,384 MiB (the RTX 5070 Ti reports 16,303), so the line sits well below that.
    /// </summary>
    public const int LargeCardMiB = 15_000;

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
    /// Never go above this however much memory there is. Null takes <see cref="CeilingFor"/>.
    /// </param>
    /// <remarks>
    /// One is always safe, because it is what a single page needs and that is already accounted
    /// for by the time this is asked. When the probe finds nothing — no NVIDIA card, no
    /// nvidia-smi, or CPU execution — one is also the right answer: on CPU the work is already
    /// spread across every core inside the engine, and adding outer concurrency only contends.
    /// </remarks>
    public static int ConcurrencyFor(GpuMemory? memory, int? ceiling = null)
    {
        if (memory is null)
            return 1;

        var limit = ceiling ?? CeilingFor(memory);
        if (limit < 1)
            return 1;

        var spare = memory.FreeMiB - ReserveMiB;
        if (spare <= 0)
            return 1;

        return Math.Clamp(1 + spare / MarginalMiBPerConcurrentPage, 1, limit);
    }

    /// <summary>
    /// The most pages in flight a card gets without being asked: three from 16 GB, two below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two on an 8 GB card, not three. Three was the fastest setting measured there — 83.3 pages a
    /// minute against 78.1 — but it was also the setting that tipped the card into spilling once
    /// the pages were varied enough, and the difference between those two figures is 6% while the
    /// difference between either and spilling is a factor of ten.
    /// </para>
    /// <para>
    /// Three on a 16 GB card, not four, for the same reason one step up. Four was the fastest there
    /// at 91.4 pages a minute against 82.9-86.8, but it peaked at 15.7 GB of 16.3, leaving nothing
    /// for a browser or a game opened during a run, and six had already collapsed to 13.7. Three
    /// peaked at 12.4-13.3 GB. Nothing larger has been measured, so a bigger card gets three too.
    /// </para>
    /// <para>
    /// More is available by asking for it explicitly, which is the right way round for a choice
    /// with that shape.
    /// </para>
    /// </remarks>
    public static int CeilingFor(GpuMemory memory) => memory.TotalMiB >= LargeCardMiB ? 3 : 2;

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
