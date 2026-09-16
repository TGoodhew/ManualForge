using ManualForge.Core.Indexing;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>Builds the full-text index over a library.</summary>
internal static class IndexCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var root = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a folder of PDFs.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such folder: {root}");

        if (!SearchIndex.IsFts5Available())
        {
            Console.Error.WriteLine("This build of SQLite has no FTS5, so there is nothing to index into.");
            return 1;
        }

        var options = new IndexOptions
        {
            IndexPath = arguments.Get("index"),
            Force = arguments.Has("reindex"),
            SidecarDirectory = arguments.Get("sidecars"),
        };

        var indexPath = options.IndexPath ?? LibraryIndexer.DefaultIndexPath(root);

        Console.WriteLine($"Library : {root}");
        Console.WriteLine($"Index   : {indexPath}");
        if (options.SidecarDirectory is not null)
            Console.WriteLine($"Sidecars: {options.SidecarDirectory}");
        Console.WriteLine();

        var indexer = new LibraryIndexer(loggerFactory.CreateLogger<LibraryIndexer>());

        var lastLine = 0;
        var progress = new Progress<IndexProgress>(p =>
        {
            if (p.DocumentsDone == lastLine)
                return;

            lastLine = p.DocumentsDone;
            if (p.DocumentsDone % 10 == 0 || p.DocumentsDone == p.DocumentsTotal)
                Console.Write($"\r  {p.DocumentsDone:N0}/{p.DocumentsTotal:N0} documents, {p.PagesIndexed:N0} pages...");
        });

        var report = await indexer.IndexAsync(root, options, progress, cancellationToken).ConfigureAwait(false);
        Console.Write("\r".PadRight(70) + "\r");

        Console.WriteLine($"Indexed   : {report.DocumentsIndexed:N0} document(s), {report.PagesIndexed:N0} pages");
        Console.WriteLine($"Unchanged : {report.DocumentsUnchanged:N0}");
        if (report.DocumentsWithoutText > 0)
        {
            Console.WriteLine(
                $"No text   : {report.DocumentsWithoutText:N0} (image-only and never OCR'd; " +
                "run `manualforge run` first if they should be searchable)");
        }

        if (report.DocumentsFailed > 0)
            Console.WriteLine($"Failed    : {report.DocumentsFailed:N0}");

        Console.WriteLine($"Took      : {report.Elapsed.TotalMinutes:F1} min ({report.PagesPerMinute:N0} pages/min)");

        using var index = new SearchIndex(indexPath);
        var statistics = index.Statistics();
        Console.WriteLine(
            $"Index     : {statistics.Documents:N0} documents, {statistics.Pages:N0} pages, " +
            $"{statistics.SizeBytes / 1024.0 / 1024.0:F1} MB");

        return report.DocumentsFailed > 0 ? 1 : 0;
    }
}

/// <summary>Queries the index.</summary>
internal static class SearchCommand
{
    public static int Run(CommandLine arguments)
    {
        var query = arguments.Positional(0)
            ?? throw new ArgumentException("Give something to search for.");

        var root = arguments.Get("library");
        var indexPath = arguments.Get("index")
            ?? (root is not null ? LibraryIndexer.DefaultIndexPath(root) : null)
            ?? throw new ArgumentException("Give --library <folder> or --index <path>.");

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"No index at {indexPath}. Build one with `manualforge index <folder>`.");
            return 1;
        }

        using var index = new SearchIndex(indexPath, readOnly: true);

        var limit = arguments.GetInt("limit") ?? 10;
        var hits = index.Search(query, limit, foldDuplicates: !arguments.Has("show-duplicates"));

        if (hits.Count == 0)
        {
            Console.WriteLine($"Nothing matched {query}.");
            return 0;
        }

        Console.WriteLine($"{hits.Count} result(s) for {query}:");
        Console.WriteLine();

        foreach (var hit in hits)
        {
            Console.WriteLine($"  {hit.Title}  page {hit.PageNumber:N0}");
            Console.WriteLine($"    {hit.Snippet}");
            Console.WriteLine($"    {hit.Path}");

            if (hit.HasDuplicates)
            {
                Console.WriteLine(
                    $"    (the same document is also at {hit.AlsoAt.Count} other path(s); " +
                    "--show-duplicates lists them separately)");
            }

            Console.WriteLine();
        }

        return 0;
    }
}
