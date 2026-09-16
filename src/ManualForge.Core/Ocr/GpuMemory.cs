using System.Diagnostics;
using System.Globalization;

namespace ManualForge.Core.Ocr;

/// <summary>What the card has, and how much of it somebody else is already using.</summary>
public sealed record GpuMemory(int TotalMiB, int UsedMiB, string? Name)
{
    public int FreeMiB => Math.Max(0, TotalMiB - UsedMiB);

    public override string ToString() =>
        $"{Name ?? "GPU"}: {FreeMiB:N0} MiB free of {TotalMiB:N0}";
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
    /// What one page in flight needs, measured on this corpus at 300 dpi: about 4.8 GB of working
    /// set on top of roughly 2.7 GB of models and CUDA context. The second and subsequent pages
    /// cost far less than the first because the ONNX Runtime arena is already grown, so this is
    /// deliberately the *marginal* figure rather than the first-page one.
    /// </summary>
    public const int MarginalMiBPerConcurrentPage = 700;

    /// <summary>Headroom left unused, so that a desktop that grows does not push the run over.</summary>
    public const int ReserveMiB = 512;

    /// <summary>Reads the first CUDA device's memory, or null if nvidia-smi is not there.</summary>
    public static GpuMemory? TryRead()
    {
        var line = RunNvidiaSmi("--query-gpu=memory.total,memory.used,name --format=csv,noheader,nounits");
        if (line is null)
            return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used))
        {
            return null;
        }

        return new GpuMemory(total, used, parts.Length > 2 ? parts[2] : null);
    }

    /// <summary>
    /// How many pages to keep in flight on the GPU, given what is free right now.
    /// </summary>
    /// <param name="memory">What the probe found, or null when it could not look.</param>
    /// <param name="ceiling">
    /// Never go above this however much memory there is. Past three the returns flatten while the
    /// risk of spilling does not, so there is no reason to chase it.
    /// </param>
    /// <remarks>
    /// One is always safe, because it is what a single page needs and that is already accounted
    /// for by the time this is asked. When the probe finds nothing — no NVIDIA card, no
    /// nvidia-smi, or CPU execution — one is also the right answer: on CPU the work is already
    /// spread across every core inside the engine, and adding outer concurrency only contends.
    /// </remarks>
    public static int ConcurrencyFor(GpuMemory? memory, int ceiling = 3)
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
