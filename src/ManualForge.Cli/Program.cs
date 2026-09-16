using System.Globalization;
using ManualForge.Cli;
using ManualForge.Core.Diagnostics;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.Rendering;
using ManualForge.Core.Text;
using ManualForge.Core.Verification;
using Microsoft.Extensions.Logging;

var arguments = CommandLine.Parse(args);

if (arguments.Command is null or "help" or "--help" or "-h")
{
    CommandLine.PrintUsage();
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\nCancelling — finishing the page in flight, then stopping.");
    cancellation.Cancel();
};

var logPath = arguments.Get("log")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ManualForge", "logs", $"manualforge-{DateTime.Now:yyyyMMdd}.jsonl");

using var logProvider = new JsonFileLoggerProvider(logPath, arguments.Has("verbose") ? LogLevel.Debug : LogLevel.Information);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider).SetMinimumLevel(LogLevel.Debug));

try
{
    return arguments.Command switch
    {
        "gpu" => await GpuCommand.RunAsync(arguments, loggerFactory),
        "ocr" => await OcrCommand.RunAsync(arguments, loggerFactory, logPath, cancellation.Token),
        "inspect" => InspectCommand.Run(arguments),
        "survey" => SurveyCommand.Run(arguments, loggerFactory, cancellation.Token),
        "run" => await RunCommand.RunAsync(arguments, loggerFactory, logPath, cancellation.Token),
        _ => Unknown(arguments.Command),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    loggerFactory.CreateLogger("manualforge").LogError(ex, "Command {Command} failed", arguments.Command);
    if (arguments.Has("verbose"))
        Console.Error.WriteLine(ex);
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    CommandLine.PrintUsage();
    return 2;
}

namespace ManualForge.Cli
{
    /// <summary>
    /// Reports what the OCR engine resolved to, which is the first thing to check when throughput
    /// is disappointing: a silent fall back to CPU costs roughly an order of magnitude.
    /// </summary>
    internal static class GpuCommand
    {
        public static async Task<int> RunAsync(CommandLine arguments, ILoggerFactory loggerFactory)
        {
            var options = new OcrEngineOptions
            {
                Accelerator = arguments.Accelerator(),
                ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
            };

            Console.WriteLine($"Requested accelerator : {options.Accelerator}");
            Console.WriteLine($"Model cache           : {options.ModelCachePath}");

            await using var engine = new PaddleOcrEngine(options, loggerFactory.CreateLogger<PaddleOcrEngine>());

            Console.WriteLine($"Active provider       : {engine.Runtime.ExecutionProvider}");
            Console.WriteLine($"Using GPU             : {engine.Runtime.UsingGpu}");
            if (!string.IsNullOrWhiteSpace(engine.Runtime.AccelerationHint))
                Console.WriteLine($"Hint                  : {engine.Runtime.AccelerationHint}");

            if (!engine.Runtime.UsingGpu && options.Accelerator != OcrAccelerator.Cpu)
            {
                Console.WriteLine();
                Console.WriteLine("The GPU was not used. CUDA needs the CUDA 12 and cuDNN 9 runtime DLLs on PATH;");
                Console.WriteLine("if they are missing, --engine directml works on any DX12 GPU with no extra setup.");
            }

            return 0;
        }
    }

    /// <summary>Reports what a PDF looks like before anything is done to it.</summary>
    internal static class InspectCommand
    {
        public static int Run(CommandLine arguments)
        {
            var path = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a PDF.");

            Console.WriteLine($"File        : {path}");
            Console.WriteLine($"Size        : {new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"Pages       : {PageRasteriser.GetPageCount(path)}");

            var sizes = PageRasteriser.GetPageSizes(path);
            var distinct = sizes.Select(s => $"{s.Width:F0}x{s.Height:F0}").Distinct().Take(5).ToArray();
            Console.WriteLine($"Page sizes  : {string.Join(", ", distinct)}{(sizes.Count > 5 ? " ..." : "")} pt");

            var landscape = sizes
                .Select((size, index) => (Page: index + 1, size.Width, size.Height))
                .Where(p => p.Width > p.Height)
                .ToArray();
            if (landscape.Length > 0)
            {
                Console.WriteLine(
                    $"Landscape   : {landscape.Length} page(s) wider than tall, e.g. " +
                    string.Join(", ", landscape.Take(8).Select(p => $"p{p.Page} ({p.Width:F0}x{p.Height:F0})")));
            }

            var characters = TextLayerVerifier.CountExtractableCharacters(path);
            Console.WriteLine($"Text layer  : {characters:N0} extractable characters");
            Console.WriteLine(characters == 0
                ? "              (no text layer — a candidate for OCR)"
                : $"              ({characters / (double)sizes.Count:F0} characters per page on average)");

            return 0;
        }
    }

    internal static class OcrCommand
    {
        public static async Task<int> RunAsync(
            CommandLine arguments,
            ILoggerFactory loggerFactory,
            string logPath,
            CancellationToken cancellationToken)
        {
            var input = arguments.Positional(0) ?? throw new ArgumentException("Give the path to a PDF.");
            if (!File.Exists(input))
                throw new FileNotFoundException($"No such file: {input}", input);

            var output = arguments.Get("out")
                ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(input))!,
                    Path.GetFileNameWithoutExtension(input) + ".searchable.pdf");

            var dpi = arguments.GetInt("dpi") ?? 300;
            var engineOptions = new OcrEngineOptions
            {
                Accelerator = arguments.Accelerator(),
                ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
                BatchSize = arguments.GetInt("batch") ?? 8,
                Deskew = !arguments.Has("no-deskew"),
                Denoise = !arguments.Has("no-denoise"),
            };

            Console.WriteLine($"Source : {input}");
            Console.WriteLine($"Output : {output}{(arguments.Has("dry-run") ? "  (dry run, nothing written)" : "")}");
            Console.WriteLine($"Log    : {logPath}");
            Console.WriteLine();
            Console.Write("Loading OCR models... ");

            await using var engine = new PaddleOcrEngine(engineOptions, loggerFactory.CreateLogger<PaddleOcrEngine>());
            Console.WriteLine($"ready on {engine.Runtime.ExecutionProvider}.");
            Console.WriteLine();

            var builder = new SearchablePdfBuilder(
                engine,
                new PageRasteriser(new RasterOptions { Dpi = dpi }),
                new TextLayerWriter(new TextLayerOptions
                {
                    MinimumConfidence = arguments.GetDouble("min-confidence") ?? 0.30,
                }),
                loggerFactory.CreateLogger<SearchablePdfBuilder>());

            var progress = new Progress<PageReport>(report => Console.WriteLine(
                $"  page {report.PageNumber,4}  {report.WordsWritten,5} words  " +
                $"{report.PixelWidth}x{report.PixelHeight} @ {report.EffectiveDpi:F0} dpi" +
                $"{(report.Rotation != 0 ? $"  /Rotate {report.Rotation}" : "")}  " +
                $"raster {report.RasterTime.TotalMilliseconds,6:F0} ms  ocr {report.OcrTime.TotalMilliseconds,7:F0} ms"));

            var report = await builder.BuildAsync(
                input,
                output,
                new BuildOptions
                {
                    Pages = arguments.PageRange(),
                    DryRun = arguments.Has("dry-run"),
                    Overwrite = arguments.Has("overwrite"),
                    TextDumpPath = arguments.Get("text"),
                },
                progress,
                cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Pages processed : {report.Pages.Count} of {report.SourcePageCount}");
            Console.WriteLine($"Words written   : {report.TotalWordsWritten:N0}");
            Console.WriteLine($"Elapsed         : {report.TotalTime.TotalSeconds:F1} s  ({report.PagesPerMinute:F1} pages/min)");

            foreach (var warning in report.Warnings)
                Console.WriteLine($"warning: {warning}");

            if (report.OutputPath is null)
                return 0;

            Console.WriteLine();
            Console.WriteLine("Verifying the written file with PdfPig...");

            var verifications = builder.Verify(report.OutputPath, report.SourcePageCount);
            var worst = 0.0;
            foreach (var verification in verifications)
            {
                worst = Math.Max(worst, verification.WorstDeviationPt);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  page {verification.PageNumber,4}  {verification.ExtractedLetters,6:N0} characters  " +
                    $"{verification.MatchedWords}/{verification.Alignments.Count} words matched  " +
                    $"{(verification.HadTextAlready ? $"[{verification.PreExistingLetters:N0} pre-existing]  " : "")}" +
                    $"deviation mean {verification.MeanDeviationPt:F3} pt, " +
                    $"p95 {verification.PercentileDeviationPt(0.95):F3} pt, " +
                    $"worst {verification.WorstDeviationPt:F3} pt"));
            }

            var empty = verifications.Where(v => !v.HasText).ToArray();
            if (empty.Length > 0)
                Console.WriteLine($"warning: {empty.Length} page(s) came out with no text layer.");

            if (arguments.Has("verify-ink"))
            {
                Console.WriteLine();
                Console.WriteLine("Comparing rendered pages against the source...");

                var sourceBytes = await File.ReadAllBytesAsync(input, cancellationToken);
                var outputBytes = await File.ReadAllBytesAsync(report.OutputPath, cancellationToken);
                var clean = true;

                foreach (var page in report.Pages)
                {
                    var comparison = PageInkComparer.ComparePage(sourceBytes, outputBytes, page.PageNumber - 1);
                    clean &= comparison.IsIdentical;
                    Console.WriteLine(comparison.IsIdentical
                        ? $"  page {comparison.PageNumber,4}  identical ({comparison.TotalPixels:N0} pixels)"
                        : $"  page {comparison.PageNumber,4}  {comparison.DifferingPixels:N0} pixels differ " +
                          $"({comparison.DifferingFraction:P4}), largest channel change {comparison.MaxChannelDelta}");
                }

                Console.WriteLine(clean
                    ? "Page images are byte-identical: the text layer adds no ink."
                    : "warning: the rendered page changed. The text layer is not fully invisible.");
            }

            Console.WriteLine();
            Console.WriteLine(worst < 0.5
                ? $"Text layer verified: every word within {worst:F3} pt of its detected box."
                : $"Text layer written, but the worst word is {worst:F3} pt out — inspect before trusting it.");

            return 0;
        }
    }
}
