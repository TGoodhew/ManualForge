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

/// <summary>A card, as far as tuning is concerned: its name and how much memory it has.</summary>
public sealed record GpuIdentity(string Name, int TotalMiB, string? Driver)
{
    /// <summary>What a profile is filed under. Two cards of the same model are the same card here.</summary>
    public string Key => $"{Name}|{TotalMiB}";

    public override string ToString() =>
        $"{Name} ({TotalMiB:N0} MiB{(Driver is null ? "" : $", driver {Driver}")})";
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
/// So the card is watched while it works. Pages in flight are not chosen from a figure for any
/// card but found by the run itself (ConcurrencyController, #31): it climbs while another page
/// pays and the memory to hold it is free, and steps back from a collapse or a nearly full card.
/// The free figure matters, not the total: the desktop, a browser and whatever else is running
/// hold 2.6-2.9 GB of either card before any work begins.
/// </summary>
public static class GpuMemoryProbe
{
    /// <summary>
    /// Which card this is: what a tuned setting or a measured speed belongs to. Null without
    /// nvidia-smi. The driver is reported, not keyed on - a driver update moves speed by a few per
    /// cent, and re-tuning from nothing for that would cost more than it learned.
    /// </summary>
    public static GpuIdentity? TryIdentify()
    {
        var line = RunNvidiaSmi("--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits");
        if (line is null)
            return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            return null;
        }

        return new GpuIdentity(parts[0], total, parts.Length > 2 ? parts[2] : null);
    }

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
