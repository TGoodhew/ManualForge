using System.Globalization;
using System.Text;
using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pdf;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.State;
using ManualForge.Core.Text;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>
/// Classifies a whole library without changing anything, and prints the summary table to review
/// before committing to a run.
/// </summary>
internal static class SurveyCommand
{
    public static int Run(CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var root = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a folder of PDFs.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such folder: {root}");

        var options = new LibraryOptions
        {
            Root = root,
            Policy = ClassificationPolicy.Parse(arguments.Get("policy")),
            StatePath = arguments.Get("state"),
            Deduplicate = !arguments.Has("no-dedup"),
        };

        var processor = new LibraryProcessor(
            builder: null, new DocumentClassifier(), loggerFactory.CreateLogger<LibraryProcessor>());

        Console.WriteLine($"Surveying {root}");
        Console.WriteLine("Nothing is modified by this command.");
        Console.WriteLine();

        var seen = 0;
        var progress = new Progress<DocumentClassification>(c =>
        {
            if (++seen % 25 == 0)
                Console.Write($"\r  {seen} files...");
        });

        var records = processor.Survey(options, progress, cancellationToken);
        Console.Write("\r".PadRight(30) + "\r");

        var trimMissing = arguments.Has("trim-missing");
        PrintSummary(records, options, trimMissing);

        if (trimMissing)
        {
            var trimmed = processor.TrimMissing(options);
            Console.WriteLine($"Forgot {trimmed.Count:N0} record(s) for files that are no longer on disk.");
            Console.WriteLine();
        }

        var csv = arguments.Get("csv");
        if (csv is not null)
        {
            WriteCsv(records, csv);
            Console.WriteLine();
            Console.WriteLine($"Wrote {csv}");
        }

        return 0;
    }

    public static void PrintSummary(
        IReadOnlyList<FileRecord> allRecords, LibraryOptions options, bool trimmingMissing = false)
    {
        // Records for files that have gone from disk are excluded, so the table describes the
        // library as it stands rather than as it once did.
        var records = allRecords.Where(r => r.Status != FileStatus.Missing).ToArray();
        var totalPages = records.Sum(r => (long)r.PageCount);
        Console.WriteLine($"{records.Length:N0} files, {totalPages:N0} pages");
        Console.WriteLine();

        PrintMissing(allRecords, trimmingMissing);
        Console.WriteLine($"  {"Class",-21}{"Files",8}{"Pages",10}  {"Action",-14}");
        Console.WriteLine("  " + new string('-', 55));

        foreach (var textClass in Enum.GetValues<TextClass>())
        {
            var inClass = records.Where(r => r.TextClass == textClass).ToArray();
            if (inClass.Length == 0)
                continue;

            var pages = inClass.Sum(r => (long)r.PageCount);
            var action = options.Policy.ActionFor(textClass);
            Console.WriteLine($"  {textClass,-21}{inClass.Length,8:N0}{pages,10:N0}  {action,-14}");
        }

        Console.WriteLine();

        var blocked = records.Where(r => r.Blocker != ModificationBlocker.None).ToArray();
        if (blocked.Length > 0)
        {
            Console.WriteLine($"  {blocked.Length:N0} file(s) cannot be written to directly:");
            foreach (var group in blocked.GroupBy(r => r.Blocker).OrderByDescending(g => g.Count()))
            {
                var note = group.Key switch
                {
                    ModificationBlocker.OwnerPassword => "cleared by flattening",
                    ModificationBlocker.Signature => "flattening would drop the signature",
                    ModificationBlocker.Corrupt => "needs manual attention",
                    ModificationBlocker.UserPassword => "needs the password",
                    _ => "unknown cause",
                };
                Console.WriteLine($"    {group.Key,-16}{group.Count(),6:N0}   {note}");
            }
            Console.WriteLine();
        }

        // "Marked for work" must mean work still to do, not merely a non-Skip action. Counting by
        // action alone reported 155 files outstanding when every one of them was already finished,
        // which flatly contradicted the run that followed it.
        var duplicates = records.Where(r => r.Action == ClassAction.CopyFromDuplicate).ToArray();
        if (duplicates.Length > 0)
        {
            var spared = duplicates.Sum(r => (long)r.PageCount);
            Console.WriteLine(
                $"  {duplicates.Length:N0} file(s) are byte-identical copies of another and will be copied " +
                $"rather than recognised, sparing {spared:N0} pages " +
                $"(~{MeasuredThroughput.HoursFor(spared):F1} hours).");
            Console.WriteLine();
        }

        var marked = records.Where(r => r.Action != ClassAction.Skip).ToArray();
        var outstanding = marked
            .Where(r => r.Status is not (FileStatus.Completed or FileStatus.Skipped or FileStatus.Missing))
            .ToArray();
        var done = marked.Length - outstanding.Length;
        var workPages = outstanding.Sum(r => (long)r.PageCount);

        Console.WriteLine($"  Outstanding: {outstanding.Length:N0} files, {workPages:N0} pages" +
                          (done > 0 ? $"  ({done:N0} already done)" : ""));
        if (workPages > 0)
        {
            var hours = MeasuredThroughput.HoursFor(workPages);
            Console.WriteLine(
                $"  Estimated at {MeasuredThroughput.PagesPerMinuteOnGpu:F0} pages/min on CUDA: {hours:F1} hours");
            Console.WriteLine(
                "  A library-wide average, and the smallest files run first, so the last of those " +
                "hours are the densest material and will run longer than this.");
        }
    }

    /// <summary>
    /// Reports records whose files have gone from disk. Printed on every survey and every run,
    /// whatever else was asked for: a file that vanished is either a move somebody meant or a loss
    /// somebody did not, and silently dropping it from the totals decides which without asking.
    /// </summary>
    private static void PrintMissing(IReadOnlyList<FileRecord> allRecords, bool trimming)
    {
        var missing = allRecords.Where(r => r.Status == FileStatus.Missing).ToArray();
        if (missing.Length == 0)
            return;

        Console.WriteLine($"  {missing.Length:N0} recorded file(s) are no longer on disk and are left out of the totals:");
        foreach (var record in missing.Take(10))
            Console.WriteLine($"    {record.Path}");
        if (missing.Length > 10)
            Console.WriteLine($"    ...and {missing.Length - 10:N0} more.");

        Console.WriteLine(trimming
            ? "  --trim-missing was given, so their records are being forgotten."
            : "  Pass --trim-missing to forget them.");
        Console.WriteLine();
    }

    private static void WriteCsv(IReadOnlyList<FileRecord> records, string path)
    {
        var lines = new List<string>(records.Count + 1)
        {
            "path,pages,class,action,status,alnumPerPage,plausibleRatio,commonWordShare,blocker,error",
        };

        foreach (var r in records)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"\"{r.Path.Replace("\"", "\"\"")}\",{r.PageCount},{r.TextClass},{r.Action},{r.Status}," +
                $"{r.AlphanumericPerPage:F0},{r.PlausibleTokenRatio:F3},{r.CommonWordShare:F3},{r.Blocker}," +
                $"\"{(r.Error ?? string.Empty).Replace("\"", "\"\"").ReplaceLineEndings(" ")}\""));
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}

/// <summary>Processes a library according to the class policy, resumably.</summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, string logPath, CancellationToken cancellationToken)
    {
        var root = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a folder of PDFs.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such folder: {root}");

        var options = new LibraryOptions
        {
            Root = root,
            Policy = ClassificationPolicy.Parse(arguments.Get("policy")),
            StatePath = arguments.Get("state"),
            DryRun = arguments.Has("dry-run"),
            Limit = arguments.GetInt("limit") ?? 0,
            OriginalsFolderName = arguments.Get("originals") ?? "_Originals",
            RefuseSignedFiles = arguments.Has("refuse-signed"),
            RetrySkipped = arguments.Has("retry-skipped"),
            Deduplicate = !arguments.Has("no-dedup"),
        };

        var engineOptions = new OcrEngineOptions
        {
            Accelerator = arguments.Accelerator(),
            ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
            BatchSize = arguments.GetInt("batch") ?? 8,
            CpuThreads = arguments.GetInt("cpu-threads"),
        };

        Console.WriteLine($"Library : {root}");
        Console.WriteLine($"Originals: {Path.Combine(Path.GetFullPath(root), options.OriginalsFolderName)}");
        Console.WriteLine($"Log     : {logPath}");
        if (options.DryRun)
            Console.WriteLine("Dry run : nothing will be moved or replaced.");
        Console.WriteLine();

        Console.Write("Loading OCR models... ");
        await using var engine = new PaddleOcrEngine(engineOptions, loggerFactory.CreateLogger<PaddleOcrEngine>());
        Console.WriteLine($"ready on {engine.Runtime.ExecutionProvider}.");

        // Recognition results live in the same database as the rest of the job state, so an
        // interrupted document resumes from the page it reached rather than from page one.
        using var pageCache = new SqlitePageOcrCache(LibraryProcessor.StatePathFor(options));

        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = arguments.GetInt("dpi") ?? 300 });

        var builder = new SearchablePdfBuilder(
            engine,
            rasteriser,
            new TextLayerWriter(),
            loggerFactory.CreateLogger<SearchablePdfBuilder>(),
            pageCache);

        // How many pages to keep on the GPU at once. The measured reason this is not simply "more"
        // is that going past what VRAM holds does not slow down, it collapses: the driver spills to
        // system memory over PCIe and throughput falls from 83 to 7.6 pages a minute with no error
        // raised. So it is sized from what is actually free, not from the card's nominal capacity.
        var vram = GpuMemoryProbe.TryRead();
        var concurrency = arguments.GetInt("gpu-concurrency")
            ?? (engine.Runtime.UsingGpu ? GpuMemoryProbe.ConcurrencyFor(vram) : 1);

        var pipelineOptions = new PipelineOptions
        {
            GpuConcurrency = Math.Max(1, concurrency),
            RasterWorkers = Math.Max(1, arguments.GetInt("raster-workers") ?? 2),
        };

        var pipeline = new RecognitionPipeline(
            engine, rasteriser, pageCache, builder.SettingsFingerprint,
            loggerFactory.CreateLogger<RecognitionPipeline>());

        var processor = new LibraryProcessor(
            builder, new DocumentClassifier(), loggerFactory.CreateLogger<LibraryProcessor>(),
            pageCache, pipeline);

        Console.WriteLine(vram is null
            ? "GPU memory : not reported; recognising one page at a time."
            : $"GPU memory : {vram}");
        Console.WriteLine(
            $"Pipeline   : {pipelineOptions.GpuConcurrency} page(s) on the GPU at once, " +
            $"{pipelineOptions.RasterWorkers} rasteriser(s)" +
            (arguments.GetInt("gpu-concurrency") is null ? "" : " (set on the command line)"));
        Console.WriteLine();

        // Always survey first. It is cheap on an already-surveyed library because unchanged files
        // are left alone, and it is what makes a re-run over a finished folder a no-op.
        Console.WriteLine("Classifying...");
        var records = processor.Survey(options, null, cancellationToken);
        var trimMissing = arguments.Has("trim-missing");
        SurveyCommand.PrintSummary(records, options, trimMissing);
        if (trimMissing)
        {
            var trimmed = processor.TrimMissing(options);
            Console.WriteLine($"Forgot {trimmed.Count:N0} record(s) for files that are no longer on disk.");
        }

        Console.WriteLine();

        if (arguments.Has("survey-only"))
            return 0;

        var outstanding = LibraryProcessor.OpenStore(options);
        var pending = outstanding.Outstanding().Count;
        outstanding.Dispose();

        if (pending == 0)
        {
            Console.WriteLine("Nothing outstanding. Every file is already finished or deliberately skipped.");
            return 0;
        }

        Console.WriteLine($"Processing {pending:N0} file(s)...");
        Console.WriteLine();

        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        var donePages = 0L;

        var done = 0;
        var progress = new Progress<FileOutcome>(outcome =>
        {
            done++;
            var name = Path.GetFileName(outcome.Path);
            if (name.Length > 44) name = name[..41] + "...";
            var flag = outcome.WasFlattened ? " [flattened]" : "";

            // In a dry run nothing reaches Completed by design, so report the work that was done
            // rather than printing every file as though it had failed.
            var succeeded = outcome.Status == FileStatus.Completed
                || (options.DryRun && outcome.Error is null);

            // Pages and a running rate rather than a per-file duration. With the pipeline
            // recognising ahead, a file's own elapsed time is only what assembly took - a fraction
            // of a second for a manual whose pages were already recognised - and printing that
            // reads as though the whole document took it.
            donePages += outcome.PageCount;
            var rate = runWatch.Elapsed.TotalMinutes > 0 ? donePages / runWatch.Elapsed.TotalMinutes : 0;

            Console.WriteLine(succeeded
                ? $"  {done,4}  {name,-44} {outcome.PageCount,5:N0} pp {outcome.WordsWritten,7:N0} words  " +
                  $"{rate,6:F1} pp/min{flag}" + (options.DryRun ? "  (dry run)" : "")
                : $"  {done,4}  {name,-44} {outcome.Status}: {outcome.Error}");
        });

        var outcomes = await processor
            .RunAsync(options, progress, cancellationToken, pipelineOptions)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(options.DryRun
            ? $"Would process: {outcomes.Count(o => o.Error is null):N0}"
            : $"Completed : {outcomes.Count(o => o.Status == FileStatus.Completed):N0}");
        Console.WriteLine($"Failed    : {outcomes.Count(o => o.Status == FileStatus.Failed):N0}");
        Console.WriteLine($"Flattened : {outcomes.Count(o => o.WasFlattened):N0}");
        Console.WriteLine($"Words     : {outcomes.Sum(o => (long)o.WordsWritten):N0}");

        var worst = outcomes.Where(o => o.Status == FileStatus.Completed).Select(o => o.WorstDeviationPt).DefaultIfEmpty(0).Max();
        Console.WriteLine($"Worst alignment deviation: {worst:F3} pt");

        var completedPages = outcomes.Where(o => o.Status == FileStatus.Completed).Sum(o => (long)o.PageCount);
        if (completedPages > 0 && runWatch.Elapsed.TotalMinutes > 0)
        {
            Console.WriteLine(
                $"Throughput: {completedPages:N0} pages in {runWatch.Elapsed.TotalMinutes:F1} min, " +
                $"{completedPages / runWatch.Elapsed.TotalMinutes:F1} pages/min");
        }

        // Signatures are invalidated by default, but never quietly.
        var signed = outcomes.Where(o => o.SignatureInvalidated).ToArray();
        if (signed.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{signed.Length} file(s) were digitally signed; their signatures are now invalid.");
            Console.WriteLine("The untouched originals are kept, so this is reversible.");
            foreach (var o in signed)
                Console.WriteLine($"  {Path.GetFileName(o.Path)}");
        }

        foreach (var failure in outcomes.Where(o => o.Status == FileStatus.Failed).Take(10))
            Console.WriteLine($"  failed: {Path.GetFileName(failure.Path)} — {failure.Error}");

        return outcomes.Any(o => o.Status == FileStatus.Failed) ? 1 : 0;
    }
}
