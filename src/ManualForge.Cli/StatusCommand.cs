using System.Globalization;
using ManualForge.Core.Classification;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;

namespace ManualForge.Cli;

/// <summary>
/// Shows the work queue as it stands on disk, without changing anything.
///
/// The queue lives in a SQLite database, which is the right store for it but not something you can
/// read at a glance — and a machine that runs this tool need not have a SQLite client on it. This
/// command is that client. It opens the database read-only, so it is safe to run against a library
/// that is being processed at the time.
/// </summary>
internal static class StatusCommand
{
    public static int Run(CommandLine arguments)
    {
        var root = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a folder of PDFs.");

        var options = new LibraryOptions
        {
            Root = root,
            StatePath = arguments.Get("state"),
            OriginalsFolderName = arguments.Get("originals") ?? "_Originals",
        };

        var databasePath = LibraryProcessor.StatePathFor(options);
        Console.WriteLine($"Queue database : {databasePath}");

        if (!File.Exists(databasePath))
        {
            Console.WriteLine();
            Console.WriteLine("No queue yet. Run 'manualforge survey' to build one.");
            return 0;
        }

        var info = new FileInfo(databasePath);
        Console.WriteLine($"Last written   : {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        using var store = LibraryProcessor.OpenStore(options, readOnly: true);
        var records = store.All();

        PrintByStatus(records);
        PrintByClass(records);
        PrintOutstanding(records, arguments.GetInt("limit") ?? 10);
        PrintFailures(records);
        PrintRecentlyDone(store, records, arguments.GetInt("limit") ?? 10);

        return 0;
    }

    private static void PrintByStatus(IReadOnlyList<FileRecord> records)
    {
        // Files that have gone from disk are shown, but kept out of the totals so those describe
        // the library as it is now.
        var present = records.Where(r => r.Status != FileStatus.Missing).ToArray();
        var missing = records.Count - present.Length;

        Console.WriteLine($"  {"Status",-14}{"Files",8}{"Pages",12}");
        Console.WriteLine("  " + new string('-', 34));

        foreach (var status in Enum.GetValues<FileStatus>())
        {
            var inStatus = records.Where(r => r.Status == status).ToArray();
            if (inStatus.Length == 0)
                continue;
            Console.WriteLine($"  {status,-14}{inStatus.Length,8:N0}{inStatus.Sum(r => (long)r.PageCount),12:N0}");
        }

        Console.WriteLine("  " + new string('-', 34));
        Console.WriteLine($"  {"in library",-14}{present.Length,8:N0}{present.Sum(r => (long)r.PageCount),12:N0}");
        if (missing > 0)
            Console.WriteLine($"  ({missing:N0} more recorded previously but no longer on disk)");
        Console.WriteLine();
    }

    private static void PrintByClass(IReadOnlyList<FileRecord> records)
    {
        Console.WriteLine($"  {"Class",-14}{"Files",8}{"Pages",12}  Action");
        Console.WriteLine("  " + new string('-', 46));

        var present = records.Where(r => r.Status != FileStatus.Missing).ToArray();

        foreach (var textClass in Enum.GetValues<TextClass>())
        {
            var inClass = present.Where(r => r.TextClass == textClass).ToArray();
            if (inClass.Length == 0)
                continue;

            var actions = string.Join("/", inClass.Select(r => r.Action).Distinct().Order());
            Console.WriteLine(
                $"  {textClass,-14}{inClass.Length,8:N0}{inClass.Sum(r => (long)r.PageCount),12:N0}  {actions}");
        }

        Console.WriteLine();
    }

    private static void PrintOutstanding(IReadOnlyList<FileRecord> records, int limit)
    {
        // The same rule the run itself applies, so what is shown here is what will actually happen.
        var outstanding = records
            .Where(r => r.Action != ClassAction.Skip
                     && r.Status is not (FileStatus.Completed or FileStatus.Skipped or FileStatus.Missing))
            .OrderBy(r => r.Fingerprint.SizeBytes)
            .ToArray();

        var pages = outstanding.Sum(r => (long)r.PageCount);
        Console.WriteLine($"  Outstanding: {outstanding.Length:N0} files, {pages:N0} pages");

        if (pages > 0)
            Console.WriteLine($"  At the measured 55.9 pages/min on CUDA: {pages / 55.9 / 60:F1} hours");

        // Smallest first, which is the order they will be processed in.
        foreach (var record in outstanding.Take(limit))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {record.PageCount,5} pages  {record.TextClass,-13} {Shorten(record.Path),-52} {record.Status}"));
        }

        if (outstanding.Length > limit)
            Console.WriteLine($"    ... and {outstanding.Length - limit:N0} more");

        Console.WriteLine();
    }

    private static void PrintFailures(IReadOnlyList<FileRecord> records)
    {
        var failures = records.Where(r => r.Status == FileStatus.Failed).ToArray();
        var inProgress = records.Where(r => r.Status == FileStatus.InProgress).ToArray();

        if (inProgress.Length > 0)
        {
            // Either a file is being worked on right now, or a previous run died on it.
            Console.WriteLine($"  In progress: {inProgress.Length}");
            foreach (var record in inProgress)
                Console.WriteLine($"    {Shorten(record.Path)}  ({record.PagesCompleted} of {record.PageCount} pages done)");
            Console.WriteLine();
        }

        if (failures.Length == 0)
            return;

        Console.WriteLine($"  Failed: {failures.Length}");
        foreach (var record in failures.Take(20))
            Console.WriteLine($"    {Shorten(record.Path)}\n      {record.Error}");
        Console.WriteLine();
    }

    private static void PrintRecentlyDone(JobStore store, IReadOnlyList<FileRecord> records, int limit)
    {
        var done = records.Where(r => r.Status == FileStatus.Completed).ToArray();
        if (done.Length == 0)
            return;

        Console.WriteLine($"  Completed: {done.Length:N0} files, {done.Sum(r => (long)r.PageCount):N0} pages");
        Console.WriteLine($"  Originals kept alongside each, under the originals folder.");
        _ = store;
        _ = limit;
        Console.WriteLine();
    }

    private static string Shorten(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 50 ? name[..47] + "..." : name;
    }
}
