using System.Text.Json;
using ManualForge.Core.Auditing;
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
            DoctorStorePath = arguments.Get("doctor-db"),
            WithoutRepairs = arguments.Has("no-repairs"),
            Workers = arguments.GetInt("workers") ?? new IndexOptions().Workers,
            Compact = arguments.Has("compact"),
            SidecarsBesidePdfs = arguments.Has("sidecars-inline"),
        };

        var indexPath = options.IndexPath ?? LibraryIndexer.DefaultIndexPath(root);

        Console.WriteLine($"Library : {root}");
        Console.WriteLine($"Index   : {indexPath}");
        Console.WriteLine(
            $"Workers : {options.Workers} extracting at once, one writing - --workers changes it");
        if (options.SidecarDirectory is not null)
            Console.WriteLine($"Sidecars: {options.SidecarDirectory}");
        if (options.SidecarsBesidePdfs)
            Console.WriteLine("Sidecars: one <name>.txt beside each PDF, in the library folder itself");
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

        if (report.DocumentsWithRecoveredText > 0)
        {
            Console.WriteLine(
                $"Recovered : {report.PagesWithRecoveredText:N0} page(s) in " +
                $"{report.DocumentsWithRecoveredText:N0} document(s) also carry text the repair read off " +
                "the rendered page");
        }

        if (report.DocumentsFailed > 0)
            Console.WriteLine($"Failed    : {report.DocumentsFailed:N0}");

        Console.WriteLine($"Took      : {report.Elapsed.TotalMinutes:F1} min ({report.PagesPerMinute:N0} pages/min)");

        using var index = new SearchIndex(indexPath);
        var statistics = index.Statistics();
        Console.WriteLine(
            $"Index     : {statistics.Documents:N0} documents, {statistics.Pages:N0} pages, " +
            $"{statistics.SizeBytes / 1024.0 / 1024.0:F1} MB");

        // A tenth of the file, or 50 MB, is enough to be worth a sentence. Below that it is noise,
        // and rewriting a cloud-synced index to save it would cost more than it returns.
        if (report.ReclaimableBytes > Math.Max(50L * 1024 * 1024, statistics.SizeBytes / 10))
        {
            Console.WriteLine(
                $"Free space: {report.ReclaimableBytes / 1024.0 / 1024.0:F0} MB of the file is free pages " +
                "left by updating documents in place — `--compact` rewrites the index without them");
        }

        PrintReconciliation(root, index, options);

        return report.DocumentsFailed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Names every PDF in the folder that did not make it into the index, and why. A count on its
    /// own says there is a problem; this says which file and whether re-running would help.
    /// </summary>
    private static void PrintReconciliation(string root, SearchIndex index, IndexOptions options)
    {
        LibraryReconciliation reconciliation;
        try
        {
            reconciliation = LibraryReconciler.Reconcile(root, index, options);
        }
        catch (Exception)
        {
            return;
        }

        if (reconciliation.IsClean)
            return;

        Console.WriteLine();

        foreach (var file in reconciliation.NotIndexed)
        {
            Console.WriteLine($"not indexed: {Path.GetFileName(file.Path)}");
            Console.WriteLine($"             {file.Reason}");
        }

        foreach (var path in reconciliation.IndexedButGone)
            Console.WriteLine($"indexed but no longer on disk: {Path.GetFileName(path)}");
    }
}

/// <summary>
/// Queries the index.
///
/// <para>
/// Answers a person by default and a program on request. The exit code is part of that contract:
/// <b>0</b> results, <b>2</b> searched and nothing matched, <b>1</b> could not search at all. A
/// caller deciding whether to fall back to its own file-name search needs 1 and 2 to be different,
/// because they mean opposite things — one says this library has nothing, the other says nobody
/// asked it.
/// </para>
/// <para>
/// With <c>--json</c> or <c>--files-only</c>, stdout carries machine output and nothing else.
/// Every note, caveat and warning goes to stderr, where a pipe will not swallow it and a parser
/// will not trip over it.
/// </para>
/// </summary>
internal static class SearchCommand
{
    /// <summary>Searched the index, and it holds nothing matching. Not an error.</summary>
    private const int NothingMatched = 2;

    /// <summary>Could not search: no index, or one this build cannot read. Not the same thing.</summary>
    private const int CouldNotSearch = 1;

    public static int Run(CommandLine arguments)
    {
        var query = arguments.Positional(0)
            ?? throw new ArgumentException("Give something to search for.");

        var root = arguments.Get("library");
        var indexPath = arguments.Get("index")
            ?? (root is not null ? LibraryIndexer.DefaultIndexPath(root) : null)
            ?? throw new ArgumentException("Give --library <folder> or --index <path>.");

        var asJson = arguments.Has("json");
        var filesOnly = arguments.Has("files-only");
        var machine = asJson || filesOnly;

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"No index at {indexPath}. Build one with `manualforge index <folder>`.");
            return CouldNotSearch;
        }

        using var index = new SearchIndex(indexPath, readOnly: true);

        var limit = arguments.GetInt("limit") ?? 10;
        var hits = index.Search(
            query, limit,
            foldDuplicates: !arguments.Has("show-duplicates"),
            note: message =>
            {
                // To stderr always: it is a caveat about the answer, and a caller parsing stdout
                // still needs to hear it.
                Console.Error.WriteLine(message);
                if (!machine)
                    Console.Error.WriteLine();
            },
            model: arguments.Get("model"),
            ranking: new RankingBias
            {
                Label = arguments.GetDouble("rank-labels") ?? RankingBias.Default.Label,
                Recovered = arguments.GetDouble("rank-recovered") ?? RankingBias.Default.Recovered,
            });

        if (machine)
            return MachineOutput(index, hits, asJson, root, indexPath);

        if (hits.Count == 0)
        {
            Console.WriteLine($"Nothing matched {query}.");
            PrintMissWarning(root, indexPath);
            return NothingMatched;
        }

        Console.WriteLine($"{hits.Count} result(s) for {query}:");
        Console.WriteLine();

        foreach (var hit in hits)
        {
            var marker = hit.MatchSource switch
            {
                TextSource.Ocr => "  [OCR]",
                TextSource.Mixed => "  [part OCR]",
                _ => string.Empty,
            };

            Console.WriteLine($"  {hit.Title}  page {hit.PageNumber:N0}{marker}");
            Console.WriteLine($"    {hit.Snippet}");
            Console.WriteLine($"    {hit.Path}");

            if (hit.MatchedOcrText)
            {
                Console.WriteLine(
                    $"    (recovered by OCR at {hit.OcrConfidence:P0} mean confidence; the file's own " +
                    "text layer does not hold this)");
            }

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

    /// <summary>
    /// Output for a program rather than a person: one JSON document, or one path per line.
    ///
    /// <para>
    /// The JSON carries the caveats as data — how many pages of this library hold text nobody has
    /// checked, and how many documents are in it with no text at all — because a caller that
    /// cannot see those will report "not in the library" for something that is in the library and
    /// merely invisible. GPIB-MCP is the caller this exists for, and it passes the caveat on.
    /// </para>
    /// </summary>
    private static int MachineOutput(
        SearchIndex index, IReadOnlyList<SearchHit> hits, bool asJson, string? root, string indexPath)
    {
        if (!asJson)
        {
            // Distinct paths in rank order: what a caller choosing which files to open wants, and
            // nothing else on stdout.
            foreach (var path in hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                Console.WriteLine(path);

            return hits.Count == 0 ? NothingMatched : 0;
        }

        var statistics = index.Statistics();

        var payload = new
        {
            schemaVersion = SearchIndex.SchemaVersion,
            index = indexPath,
            documents = statistics.Documents,
            pages = statistics.Pages,
            results = hits.Select(hit => new
            {
                path = hit.Path,
                title = hit.Title,
                page = hit.PageNumber,
                snippet = hit.Snippet,
                rank = hit.Rank,
                source = hit.MatchSource.ToString(),
                recoveredByOcr = hit.MatchedOcrText,
                ocrConfidence = hit.MatchedOcrText ? hit.OcrConfidence : 0,
                alsoAt = hit.AlsoAt,
            }),
            caveats = new
            {
                pagesFromOcr = statistics.RepairedPages,
                documentsWithNoTextAtAll = statistics.DocumentsWithoutText,
                someOfThem = index.DocumentsWithoutText(5),
                unrepairedFlaggedPages = OutstandingPages(root, indexPath),
            },
        };

        Console.WriteLine(JsonSerializer.Serialize(
            payload, new JsonSerializerOptions { WriteIndented = true }));

        return hits.Count == 0 ? NothingMatched : 0;
    }

    /// <summary>
    /// Pages the audit flagged and the repair has not reached, or null when nobody has audited.
    /// Null and zero mean different things and the caller is told which.
    /// </summary>
    private static long? OutstandingPages(string? root, string indexPath)
    {
        var folder = root ?? Path.GetDirectoryName(Path.GetDirectoryName(indexPath));
        if (folder is null)
            return null;

        var doctorPath = DoctorStore.DefaultPathFor(folder);
        if (!File.Exists(doctorPath))
            return null;

        try
        {
            using var store = new DoctorStore(doctorPath, readOnly: true);
            return store.Summary().OutstandingPages;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Says what a miss does and does not prove. It does not prove absence while there are pages in
    /// the library whose text layer is known to be incomplete and which have not been repaired —
    /// and it proves even less when nobody has looked.
    /// </summary>
    private static void PrintMissWarning(string? root, string indexPath)
    {
        var folder = root ?? Path.GetDirectoryName(Path.GetDirectoryName(indexPath));
        if (folder is null)
            return;

        var doctorPath = DoctorStore.DefaultPathFor(folder);

        if (!File.Exists(doctorPath))
        {
            Console.WriteLine();
            Console.WriteLine(
                "That is not the same as it not being there. This library has never been checked for");
            Console.WriteLine(
                "pages that carry a text layer which does not hold what is drawn on them — prose that");
            Console.WriteLine(
                "extracts perfectly beside figures, syntax diagrams and pin-outs that extract as");
            Console.WriteLine($"nothing.  manualforge doctor \"{folder}\"  reports it and changes nothing.");
            return;
        }

        try
        {
            using var store = new DoctorStore(doctorPath, readOnly: true);
            var summary = store.Summary();

            if (summary.OutstandingPages == 0)
                return;

            Console.WriteLine();
            Console.WriteLine(
                $"{summary.OutstandingPages:N0} page(s) in this library are known to hold text that no");
            Console.WriteLine(
                "search can reach, so a miss is not yet evidence of absence. Worst affected:");

            foreach (var document in store.Outstanding(5))
            {
                Console.WriteLine(
                    $"  {Path.GetFileName(document.Path)}  —  {document.OutstandingPages:N0} of " +
                    $"{document.PageCount:N0} pages");
            }

            Console.WriteLine($"  manualforge repair \"{folder}\"  recovers it.");
        }
        catch (Exception)
        {
            // The warning is a courtesy; failing to produce it must not fail the search.
        }
    }
}

/// <summary>
/// Accounts for the difference between the PDFs in a folder and the documents in its index.
///
/// The counterpart of what the MCP server's <c>library_status</c> reports, for a terminal. It was
/// worth making a command of its own because the answer "579 present, 575 indexed" is not
/// actionable and the answer "these four files, and here is why each one" is.
/// </summary>
internal static class ReconcileCommand
{
    public static int Run(CommandLine arguments)
    {
        var root = arguments.Positional(0) ?? arguments.Get("library")
            ?? throw new ArgumentException("Give the path to a folder of PDFs.");

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such folder: {root}");

        var indexPath = arguments.Get("index") ?? LibraryIndexer.DefaultIndexPath(root);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"No index at {indexPath}. Build one with `manualforge index <folder>`.");
            return 1;
        }

        // Trimming writes, so the index cannot be opened read-only when it is asked for.
        var trimMissing = arguments.Has("trim-missing");

        using var index = new SearchIndex(indexPath, readOnly: !trimMissing);
        var reconciliation = LibraryReconciler.Reconcile(root, index, new IndexOptions());

        Console.WriteLine($"Library : {root}");
        Console.WriteLine($"Index   : {indexPath}");
        Console.WriteLine($"On disk : {reconciliation.FilesOnDisk:N0} PDF(s)");
        Console.WriteLine($"Indexed : {reconciliation.DocumentsIndexed:N0} document(s)");
        Console.WriteLine();

        if (reconciliation.IsClean)
        {
            Console.WriteLine("Every PDF in the folder is in the index, and every indexed document is still there.");
            return 0;
        }

        if (reconciliation.NotIndexed.Count > 0)
        {
            Console.WriteLine($"Not indexed — nothing in these can be found by any search:");
            foreach (var file in reconciliation.NotIndexed)
            {
                Console.WriteLine($"  {Path.GetRelativePath(root, file.Path)}  ({file.SizeBytes / 1024.0 / 1024.0:F1} MB)");
                Console.WriteLine($"      {file.Reason}");
                Console.WriteLine(file.FixedByReindexing
                    ? "      → re-running `manualforge index` picks this up"
                    : "      → re-running the indexer will not change this; it needs a look");
            }

            Console.WriteLine();
        }

        if (reconciliation.IndexedButGone.Count > 0)
        {
            Console.WriteLine("Indexed but no longer on disk — a search can return a page of a file that is not there:");
            foreach (var path in reconciliation.IndexedButGone)
                Console.WriteLine($"  {Path.GetRelativePath(root, path)}");

            Console.WriteLine();

            // `index --reindex` does not clear these: it rebuilds the documents it finds on disk and
            // never visits one that has gone, so the orphan rows and their pages survive a rebuild.
            // Removing them is the only way back to a clean index short of deleting the file.
            if (trimMissing)
            {
                foreach (var path in reconciliation.IndexedButGone)
                    index.Remove(path);

                Console.WriteLine(
                    $"Forgot {reconciliation.IndexedButGone.Count:N0} document(s) whose file is gone. " +
                    "Their pages are out of the index and can no longer be returned by a search.");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("  → `manualforge reconcile <folder> --trim-missing` forgets them.");
                Console.WriteLine();
            }
        }

        return 0;
    }
}
