using ManualForge.Core.Auditing;
using ManualForge.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>
/// Adds manuals to the library and does the whole job on them (#89): copies them into the library
/// root, then the run, the audit, the repair with scanned pages included, and the index, over just
/// those files. What Claude sessions in other projects have been typing by hand as four commands
/// after downloading a manual.
/// </summary>
internal static class AddCommand
{
    /// <summary>Options handed on to the run and the repair, which read pages and so take them.</summary>
    private static readonly string[] Forwarded = ["engine", "models", "cpu-threads", "gpu-concurrency", "batch"];

    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, string logPath, CancellationToken cancellationToken)
    {
        var sources = arguments.Positionals;
        if (sources.Count == 0)
            throw new ArgumentException("Give the PDFs to add.");

        var root = arguments.Get("library") ?? Environment.GetEnvironmentVariable("MANUALFORGE_LIBRARY");
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Which library? Give --library <folder>; MANUALFORGE_LIBRARY is not set.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such library folder: {root}");
        root = Path.GetFullPath(root);

        Console.WriteLine($"Library : {root}");
        Console.WriteLine();

        var results = new LibraryAdder(loggerFactory.CreateLogger<LibraryAdder>())
            .Add(new LibraryOptions { Root = root }, sources, cancellationToken);

        foreach (var result in results)
            Console.WriteLine(Describe(result));

        var added = results.Where(r => r.Kind == AddKind.Added).Select(r => r.LibraryPath!).ToArray();
        var exit = results.Any(r => r.Kind is AddKind.Failed or AddKind.Refused) ? 1 : 0;

        Console.WriteLine();
        if (added.Length == 0)
        {
            Console.WriteLine("Nothing new to add.");
            return exit;
        }

        if (arguments.Has("copy-only"))
        {
            Console.WriteLine($"Copied {added.Length:N0} file(s). Not processed (--copy-only).");
            return exit;
        }

        var list = WriteList(added);
        try
        {
            Console.WriteLine($"== Making {added.Length:N0} file(s) searchable ==");
            Console.WriteLine();
            exit = Math.Max(exit, await RunCommand.RunAsync(
                Command(arguments, "run", root, "--documents", list), loggerFactory, logPath, cancellationToken)
                .ConfigureAwait(false));

            // A file whose printed text was silenced comes back under another name.
            var finished = added.Select(Current).Where(File.Exists).ToArray();

            Console.WriteLine();
            Console.WriteLine($"== Auditing {finished.Length:N0} file(s) ==");
            var audit = await new DoctorRunner(loggerFactory.CreateLogger<DoctorRunner>())
                .RunAsync(root, new DoctorOptions(), cancellationToken: cancellationToken, paths: finished)
                .ConfigureAwait(false);

            var flagged = audit.Audits.Where(a => a.FlaggedPageCount > 0).Select(a => a.Path).ToArray();
            Console.WriteLine(flagged.Length == 0
                ? $"Audited {audit.DocumentsAudited:N0}: every page's text is all there."
                : $"Audited {audit.DocumentsAudited:N0}: {audit.PagesFlagged:N0} page(s) in {flagged.Length:N0} file(s) " +
                  "have lettering their text misses.");
            if (audit.DocumentsFailed > 0)
                exit = 1;

            if (flagged.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("== Recovering their text ==");
                File.WriteAllLines(list, flagged);
                exit = Math.Max(exit, await RepairCommand.RunAsync(
                    Command(arguments, "repair", root, "--documents", list, "--include-scans"), loggerFactory, cancellationToken)
                    .ConfigureAwait(false));
            }

            Console.WriteLine();
            Console.WriteLine("== Indexing ==");
            exit = Math.Max(exit, await IndexCommand.RunAsync(
                CommandLine.Parse(["index", root]), loggerFactory, cancellationToken)
                .ConfigureAwait(false));
        }
        finally
        {
            File.Delete(list);
        }

        return exit;
    }

    public static string Describe(AddResult result) => result.Kind switch
    {
        AddKind.Added when result.Renamed =>
            $"  added    {Path.GetFileName(result.Source)}, as {Path.GetFileName(result.LibraryPath)}: that name was taken",
        AddKind.Added => $"  added    {Path.GetFileName(result.Source)}",
        AddKind.AlreadyHeld => $"  already  {Path.GetFileName(result.Source)}: the library has it, as {result.LibraryPath}",
        AddKind.Refused => $"  skipped  {result.Source}: {result.Detail}",
        _ => $"  failed   {result.Source}: {result.Detail}",
    };

    /// <summary>Where a file added to the library is now: renamed if the run silenced its text.</summary>
    private static string Current(string path) =>
        !File.Exists(path) && File.Exists(LibraryProcessor.RepairedPathFor(path))
            ? LibraryProcessor.RepairedPathFor(path)
            : path;

    private static string WriteList(IEnumerable<string> paths)
    {
        var list = Path.Combine(Path.GetTempPath(), $"manualforge-add-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(list, paths);
        return list;
    }

    /// <summary>The command line for one step, with the options this one was given for reading pages.</summary>
    private static CommandLine Command(CommandLine arguments, params string[] step)
    {
        var line = new List<string>(step);
        foreach (var name in Forwarded)
        {
            if (!arguments.Has(name))
                continue;

            line.Add($"--{name}");
            if (arguments.Get(name) is { } value)
                line.Add(value);
        }

        return CommandLine.Parse([.. line]);
    }
}
