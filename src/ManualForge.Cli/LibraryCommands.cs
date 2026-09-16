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

        PrintSummary(records, options);

        var csv = arguments.Get("csv");
        if (csv is not null)
        {
            WriteCsv(records, csv);
            Console.WriteLine();
            Console.WriteLine($"Wrote {csv}");
        }

        return 0;
    }

    public static void PrintSummary(IReadOnlyList<FileRecord> records, LibraryOptions options)
    {
        var totalPages = records.Sum(r => (long)r.PageCount);
        Console.WriteLine($"{records.Count:N0} files, {totalPages:N0} pages");
        Console.WriteLine();
        Console.WriteLine($"  {"Class",-14}{"Files",8}{"Pages",10}  {"Action",-14}");
        Console.WriteLine("  " + new string('-', 48));

        foreach (var textClass in Enum.GetValues<TextClass>())
        {
            var inClass = records.Where(r => r.TextClass == textClass).ToArray();
            if (inClass.Length == 0)
                continue;

            var pages = inClass.Sum(r => (long)r.PageCount);
            var action = options.Policy.ActionFor(textClass);
            Console.WriteLine($"  {textClass,-14}{inClass.Length,8:N0}{pages,10:N0}  {action,-14}");
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
        var marked = records.Where(r => r.Action != ClassAction.Skip).ToArray();
        var outstanding = marked
            .Where(r => r.Status is not (FileStatus.Completed or FileStatus.Skipped))
            .ToArray();
        var done = marked.Length - outstanding.Length;
        var workPages = outstanding.Sum(r => (long)r.PageCount);

        Console.WriteLine($"  Outstanding: {outstanding.Length:N0} files, {workPages:N0} pages" +
                          (done > 0 ? $"  ({done:N0} already done)" : ""));
        if (workPages > 0)
        {
            // 55.9 pages/min is what the 3060 Ti measured on this corpus at 300 dpi.
            var hours = workPages / 55.9 / 60;
            Console.WriteLine($"  Estimated at 55.9 pages/min on CUDA: {hours:F1} hours");
        }
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
            AllowSignedFiles = arguments.Has("allow-signed"),
            RetrySkipped = arguments.Has("retry-skipped"),
        };

        var engineOptions = new OcrEngineOptions
        {
            Accelerator = arguments.Accelerator(),
            ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
            BatchSize = arguments.GetInt("batch") ?? 8,
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

        var builder = new SearchablePdfBuilder(
            engine,
            new PageRasteriser(new RasterOptions { Dpi = arguments.GetInt("dpi") ?? 300 }),
            new TextLayerWriter(),
            loggerFactory.CreateLogger<SearchablePdfBuilder>());

        var processor = new LibraryProcessor(builder, new DocumentClassifier(), loggerFactory.CreateLogger<LibraryProcessor>());

        // Always survey first. It is cheap on an already-surveyed library because unchanged files
        // are left alone, and it is what makes a re-run over a finished folder a no-op.
        Console.WriteLine("Classifying...");
        var records = processor.Survey(options, null, cancellationToken);
        SurveyCommand.PrintSummary(records, options);
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

            Console.WriteLine(succeeded
                ? $"  {done,4}  {name,-44} {outcome.WordsWritten,7:N0} words  {outcome.Duration.TotalSeconds,6:F1}s{flag}" +
                  (options.DryRun ? "  (dry run)" : "")
                : $"  {done,4}  {name,-44} {outcome.Status}: {outcome.Error}");
        });

        var outcomes = processor.Run(options, progress, cancellationToken);

        Console.WriteLine();
        Console.WriteLine(options.DryRun
            ? $"Would process: {outcomes.Count(o => o.Error is null):N0}"
            : $"Completed : {outcomes.Count(o => o.Status == FileStatus.Completed):N0}");
        Console.WriteLine($"Failed    : {outcomes.Count(o => o.Status == FileStatus.Failed):N0}");
        Console.WriteLine($"Flattened : {outcomes.Count(o => o.WasFlattened):N0}");
        Console.WriteLine($"Words     : {outcomes.Sum(o => (long)o.WordsWritten):N0}");

        var worst = outcomes.Where(o => o.Status == FileStatus.Completed).Select(o => o.WorstDeviationPt).DefaultIfEmpty(0).Max();
        Console.WriteLine($"Worst alignment deviation: {worst:F3} pt");

        foreach (var failure in outcomes.Where(o => o.Status == FileStatus.Failed).Take(10))
            Console.WriteLine($"  failed: {Path.GetFileName(failure.Path)} — {failure.Error}");

        return outcomes.Any(o => o.Status == FileStatus.Failed) ? 1 : 0;
    }
}
