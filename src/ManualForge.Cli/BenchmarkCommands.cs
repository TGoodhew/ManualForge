using ManualForge.Core.Benchmarking;
using ManualForge.Core.Indexing;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>
/// Seeds a ground-truth set: one text file per page, filled with what the current engine reads, for
/// a human to correct.
///
/// Correcting is far cheaper than transcribing, and it does not bias anything: what gets scored is
/// a later run measured against the corrected file.
/// </summary>
internal static class TruthCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var pdf = arguments.Positional(0) ?? throw new ArgumentException("Give a PDF to take pages from.");
        if (!File.Exists(pdf))
            throw new FileNotFoundException($"No such file: {pdf}");

        var output = arguments.Get("out") ?? throw new ArgumentException("Give --out <folder> for the truth set.");
        var library = arguments.Get("library") ?? Path.GetDirectoryName(Path.GetFullPath(pdf))!;

        var pages = arguments.PageRange();
        if (pages.Count == 0)
            throw new ArgumentException("Give --pages, e.g. --pages 6,29,31. Choose pages worth arguing about.");

        if (!Enum.TryParse<PageKind>(arguments.Get("kind") ?? "Mixed", ignoreCase: true, out var kind))
            throw new ArgumentException("--kind must be prose, table, schematic or mixed.");

        Console.WriteLine($"Seeding {pages.Count} page(s) of {Path.GetFileName(pdf)} as {kind}.");

        var seeds = new List<(string, int, PageKind, string)>();

        // Which file the seed text is taken from, when it is not the one being recorded.
        //
        // The manifest has to name the file whose text layer you want scored - point it at another
        // engine's copy and `benchmark --score-existing` reports that engine. But its text may be
        // the worse starting point to correct from, and the two copies are the same scan, so the
        // seed can come from whichever reads better without changing what is measured.
        var seedFrom = arguments.Get("seed-from") ?? pdf;
        if (!File.Exists(seedFrom))
            throw new FileNotFoundException($"No such file: {seedFrom}");

        if (!string.Equals(seedFrom, pdf, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Seed text taken from {Path.GetFileName(seedFrom)}; the manifest records {Path.GetFileName(pdf)}.");

        if (arguments.Has("from-text-layer"))
        {
            // Seed from whatever text the PDF already carries. Right when the file has a text layer
            // already - another engine's OCR, or born-digital - and much faster than recognising.
            var extracted = LibraryIndexer.ExtractPages(seedFrom, cancellationToken);
            foreach (var page in pages)
            {
                var text = page >= 1 && page <= extracted.Count ? extracted[page - 1].Text : string.Empty;
                seeds.Add((pdf, page, kind, text));
            }
        }
        else
        {
            var engineOptions = new OcrEngineOptions
            {
                Accelerator = arguments.Accelerator(),
                ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
            };

            Console.Write("Loading OCR models... ");
            await using var engine = new PaddleOcrEngine(engineOptions, loggerFactory.CreateLogger<PaddleOcrEngine>());
            Console.WriteLine($"ready on {engine.Runtime.ExecutionProvider}.");

            var rasteriser = new PageRasteriser(new RasterOptions { Dpi = arguments.GetInt("dpi") ?? 300 });
            var bytes = await File.ReadAllBytesAsync(seedFrom, cancellationToken).ConfigureAwait(false);

            foreach (var page in pages)
            {
                using var raster = rasteriser.Render(bytes, page - 1);
                var result = await engine
                    .RecognisePageAsync(raster.EncodePng(), page, cancellationToken)
                    .ConfigureAwait(false);

                seeds.Add((pdf, page, kind, string.Join('\n', result.Lines.Select(l => l.Text))));
                Console.WriteLine($"  page {page}: {result.WordCount} words");
            }
        }

        var written = GroundTruthSet.Seed(output, library, seeds);

        Console.WriteLine();
        Console.WriteLine($"Wrote {written} new text file(s) to {Path.GetFullPath(output)}.");
        Console.WriteLine("Existing files were left alone - corrections are the expensive part here.");
        Console.WriteLine();
        Console.WriteLine("Now correct them by hand against the page images. The benchmark is only");
        Console.WriteLine("as honest as these files are.");

        return 0;
    }
}

/// <summary>Measures configurations against a ground-truth set.</summary>
internal static class BenchmarkCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var truthPath = arguments.Get("truth")
            ?? arguments.Positional(0)
            ?? throw new ArgumentException("Give --truth <folder>, the ground-truth set to measure against.");

        var library = arguments.Get("library");
        var truth = GroundTruthSet.Load(truthPath, library);

        if (truth.Pages.Count == 0)
        {
            Console.Error.WriteLine($"No usable pages in {truthPath}. Seed one with `manualforge truth`.");
            return 1;
        }

        Console.WriteLine($"Ground truth: {truth.Pages.Count} page(s) across {truth.Kinds.Count()} kind(s)");
        foreach (var kind in truth.Kinds)
            Console.WriteLine($"  {kind,-12}{truth.Pages.Count(p => p.Kind == kind),4} page(s)");
        Console.WriteLine();

        var configurations = Configurations(arguments).ToArray();
        var results = new List<BenchmarkResult>(configurations.Length + 1);

        // Whatever text the manuals already carry, scored against the same truth. When the truth
        // set points at another engine's output, this column is that engine's score - which is the
        // only way to answer "is this better" with a number rather than an opinion.
        if (arguments.Has("score-existing"))
        {
            Console.WriteLine("Scoring the text already in these files...");
            results.Add(BenchmarkRunner.ScoreExistingText(truth, arguments.Get("existing-name") ?? "existing text layer",
                (path, page) =>
                {
                    var pages = LibraryIndexer.ExtractPages(path, cancellationToken);
                    return page >= 1 && page <= pages.Count ? pages[page - 1].Text : null;
                }));
        }

        var runner = new BenchmarkRunner(
            configuration => new PaddleOcrEngine(
                new OcrEngineOptions
                {
                    Accelerator = configuration.Accelerator,
                    ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
                    Deskew = configuration.Deskew,
                    Denoise = configuration.Denoise,
                },
                loggerFactory.CreateLogger<PaddleOcrEngine>()),
            loggerFactory.CreateLogger<BenchmarkRunner>());

        foreach (var configuration in configurations)
        {
            Console.WriteLine($"Running: {configuration.Name} ({configuration})");

            var progress = new Progress<PageResult>(p =>
                Console.WriteLine(
                    $"    {p.Page.Label,-30} CER {p.Accuracy.CharacterErrorRate,7:P1}  " +
                    $"WER {p.Accuracy.WordErrorRate,7:P1}  unordered {p.Accuracy.UnorderedWordErrorRate,7:P1}" +
                    (p.Accuracy.ShareFromOrdering > 0.25
                        ? $"  ({p.Accuracy.ShareFromOrdering:P0} of the word error is reading order)"
                        : string.Empty)));

            results.Add(await runner.RunAsync(truth, configuration, progress, cancellationToken).ConfigureAwait(false));
            Console.WriteLine();
        }

        Console.WriteLine(BenchmarkRunner.Table(results));

        var csv = arguments.Get("csv");
        if (csv is not null)
        {
            await File.WriteAllTextAsync(csv, BenchmarkRunner.Csv(results), cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Per-page detail written to {csv}");
        }

        return 0;
    }

    /// <summary>
    /// What to measure. One configuration by default; --sweep turns on the comparisons this project
    /// has been deferring, which is the reason the harness exists.
    /// </summary>
    private static IEnumerable<BenchmarkConfiguration> Configurations(CommandLine arguments)
    {
        var accelerator = arguments.Accelerator();
        var dpi = arguments.GetInt("dpi") ?? 300;

        if (!arguments.Has("sweep"))
        {
            yield return new BenchmarkConfiguration(
                $"{dpi} dpi" + (arguments.Has("no-deskew") ? ", no deskew" : "") + (arguments.Has("no-denoise") ? ", no denoise" : ""),
                dpi,
                !arguments.Has("no-deskew"),
                !arguments.Has("no-denoise"),
                accelerator);
            yield break;
        }

        // Does preprocessing earn its CPU? Both passes are OpenCV work on every page, and nobody
        // has measured what they buy.
        yield return new BenchmarkConfiguration("300 dpi, both", 300, true, true, accelerator);
        yield return new BenchmarkConfiguration("300 dpi, no deskew", 300, false, true, accelerator);
        yield return new BenchmarkConfiguration("300 dpi, no denoise", 300, true, false, accelerator);
        yield return new BenchmarkConfiguration("300 dpi, neither", 300, false, false, accelerator);

        // And is 300 dpi the right resolution, or just the one that was picked?
        yield return new BenchmarkConfiguration("200 dpi, both", 200, true, true, accelerator);
        yield return new BenchmarkConfiguration("400 dpi, both", 400, true, true, accelerator);
    }
}
