using System.Globalization;
using ManualForge.Core.Ocr;
using ManualForge.Core.Setup;

namespace ManualForge.Cli;

/// <summary>
/// Puts in place what ManualForge needs from outside itself, and takes it away again (#26). The
/// installer runs these steps; a person can too.
/// </summary>
internal static class SetupCommand
{
    public static async Task<int> RunAsync(CommandLine arguments, CancellationToken cancellationToken)
    {
        return arguments.Positional(0)?.ToLowerInvariant() switch
        {
            "gpu" => await GpuAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: manualforge setup gpu [--plan] [--accept-nvidia-licences] [--no-tensorrt]");
        Console.Error.WriteLine("                             [--force] [--downloads <folder>] [--remove]");
        return 2;
    }

    /// <summary>
    /// Downloads CUDA's runtime and cuBLAS, cuDNN and, unless told not to, TensorRT, from NVIDIA, checks
    /// each against the SHA-256 NVIDIA publishes, and keeps only their DLLs, in a folder of ManualForge's
    /// own. Nothing goes on PATH, nothing needs administrator rights, and the display driver is never
    /// touched. A machine that already has them gets nothing.
    /// </summary>
    private static async Task<int> GpuAsync(CommandLine arguments, CancellationToken cancellationToken)
    {
        var root = CudaLibraries.ChainedLibrariesRoot;
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ManualForge-setup");
        var setup = new GpuLibrarySetup(http, root);

        Console.WriteLine($"GPU libraries : {root}");

        if (arguments.Has("remove"))
        {
            var removal = setup.Remove();
            Console.WriteLine(removal.BytesFreed > 0
                ? $"Removed       : {removal.BytesFreed / 1048576.0:N0} MB of NVIDIA libraries ManualForge had downloaded"
                : "Removed       : nothing; ManualForge had not downloaded any");
            if (removal.Left.Count == 0)
                return 0;

            Console.WriteLine($"Left          : {removal.Left.Count} file(s) in use; close what is using them and run this again:");
            foreach (var file in removal.Left)
                Console.WriteLine($"                {file}");
            return 1;
        }

        var gpu = GpuMemoryProbe.TryIdentify();
        if (gpu is null)
        {
            Console.WriteLine("GPU           : no NVIDIA GPU found (nvidia-smi is not there). ManualForge runs on the CPU;");
            Console.WriteLine("                nothing to download.");
            return 0;
        }

        Console.WriteLine($"GPU           : {gpu}");
        var driverCuda = GpuMemoryProbe.TryReadDriverCudaVersion();
        if (driverCuda is not null && driverCuda < GpuLibraryPins.MinimumDriverCuda)
        {
            Console.WriteLine($"Driver        : supports CUDA {driverCuda}; these libraries need CUDA {GpuLibraryPins.MinimumDriverCuda} or later.");
            Console.WriteLine("                Update the display driver from NVIDIA, then run this again. Until then");
            Console.WriteLine("                ManualForge runs on the CPU.");
            return 1;
        }

        if (driverCuda is not null)
            Console.WriteLine($"Driver        : supports CUDA {driverCuda}");

        var wantTensorRt = !arguments.Has("no-tensorrt");
        var force = arguments.Has("force");
        var found = CudaLibraries.LocateOnThisMachine();
        var ours = setup.Installed().Count > 0;

        // Libraries the machine already has, from a toolkit or by hand, are used as they are: the
        // installer adds nothing beside them unless asked to.
        if (!force && !ours && found.Complete && (!wantTensorRt || found.HasTensorRt))
        {
            Console.WriteLine($"Found         : {found.Describe()}");
            Console.WriteLine("                Nothing to download. `--force` downloads ManualForge's own copies anyway.");
            return 0;
        }

        Console.WriteLine("Reading NVIDIA's manifests...");
        var packages = await setup.ResolveAsync(wantTensorRt, cancellationToken).ConfigureAwait(false);

        if (!force && setup.IsCurrent(packages))
        {
            Console.WriteLine("Installed     : already in place and current:");
            foreach (var installed in setup.Installed())
                Console.WriteLine($"                {installed.Name} {installed.Version}");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("  Package                                           Version        Download");
        foreach (var package in packages)
        {
            var size = package.Size is { } bytes ? $"{bytes / 1048576.0,8:N0} MB" : "   ~1.9 GB";
            Console.WriteLine($"  {package.Name,-49} {package.Version,-14} {size}");
        }

        var known = packages.Where(p => p.Size is not null).Sum(p => p.Size!.Value) / 1073741824.0;
        Console.WriteLine($"  Total: {known + (wantTensorRt ? 1.9 : 0):N1} GB, checked against NVIDIA's published SHA-256, DLLs kept only");
        Console.WriteLine();
        Console.WriteLine("These come from NVIDIA, under NVIDIA's licences:");
        foreach (var licence in packages.Select(p => p.Licence).Distinct())
            Console.WriteLine($"  {licence}");

        if (arguments.Has("plan"))
            return 0;

        if (!arguments.Has("accept-nvidia-licences"))
        {
            Console.WriteLine();
            Console.WriteLine("Read them, then run again with --accept-nvidia-licences to download.");
            return 1;
        }

        var downloads = arguments.Get("downloads");
        var keep = downloads is not null;
        downloads ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManualForge", "downloads");

        Console.WriteLine();
        var lastLine = string.Empty;
        var progress = new Progress<GpuDownloadProgress>(p =>
        {
            var line = p.Total is { } total and > 0
                ? string.Format(CultureInfo.InvariantCulture, "  {0}: {1:N0} of {2:N0} MB", p.Name, p.Received / 1048576.0, total / 1048576.0)
                : string.Format(CultureInfo.InvariantCulture, "  {0}: {1:N0} MB", p.Name, p.Received / 1048576.0);
            Console.Write("\r" + line.PadRight(Math.Max(line.Length, lastLine.Length)));
            lastLine = line;
        });

        await setup.InstallAsync(packages, downloads, keep, progress, cancellationToken).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine();

        var now = CudaLibraries.LocateOnThisMachine();
        Console.WriteLine($"Installed     : {string.Join(", ", packages.Select(p => $"{p.Name} {p.Version}"))}");
        Console.WriteLine($"Found         : {now.Describe()}");
        Console.WriteLine("Check         : `manualforge gpu` loads the engine and says which provider it got.");
        return now.Complete ? 0 : 1;
    }
}
