using System.Globalization;
using System.Text;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Benchmarking;

/// <summary>One configuration to measure. The name is what appears in the report.</summary>
public sealed record BenchmarkConfiguration(
    string Name,
    int Dpi = 300,
    bool Deskew = true,
    bool Denoise = true,
    OcrAccelerator Accelerator = OcrAccelerator.Auto)
{
    /// <summary>The default, so a run with no options still says what it measured.</summary>
    public static BenchmarkConfiguration Default => new("300 dpi, deskew, denoise");

    public override string ToString() =>
        $"{Dpi} dpi, {(Deskew ? "deskew" : "no deskew")}, {(Denoise ? "denoise" : "no denoise")}, {Accelerator}";
}

/// <summary>What one configuration scored on one page.</summary>
public sealed record PageResult(TruthPage Page, Accuracy Accuracy, TimeSpan Elapsed, string Recognised);

/// <summary>What one configuration scored overall, and per kind of page.</summary>
public sealed record BenchmarkResult(
    BenchmarkConfiguration Configuration,
    IReadOnlyList<PageResult> Pages,
    TimeSpan Elapsed)
{
    public Accuracy Overall => Pages.Aggregate(Accuracy.Zero, (a, p) => a + p.Accuracy);

    public Accuracy For(PageKind kind) =>
        Pages.Where(p => p.Page.Kind == kind).Aggregate(Accuracy.Zero, (a, p) => a + p.Accuracy);

    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : Pages.Count / Elapsed.TotalMinutes;
}

/// <summary>
/// Runs configurations over hand-corrected pages and reports how wrong each one was.
///
/// <para>
/// This exists because "better than Acrobat" is a claim, and a claim about OCR quality that nobody
/// measured is worth nothing. It is also the only honest way to settle the questions this project
/// has been deferring: whether deskewing and despeckling earn their CPU, what resolution is
/// actually needed, and what the text layer's baseline offset should be.
/// </para>
///
/// <para>
/// Results are reported per page and per kind of page rather than as one number, because one number
/// hides the thing most worth knowing — an engine that reads prose at 1% error and parts tables at
/// 15% is not a 3% engine, it is two different engines and only one of them is good.
/// </para>
/// </summary>
public sealed class BenchmarkRunner(
    Func<BenchmarkConfiguration, IOcrEngine> engineFactory,
    ILogger<BenchmarkRunner>? logger = null)
{
    private readonly Func<BenchmarkConfiguration, IOcrEngine> _engineFactory =
        engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<BenchmarkResult> RunAsync(
        GroundTruthSet truth,
        BenchmarkConfiguration configuration,
        IProgress<PageResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(configuration);

        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = configuration.Dpi });
        await using var engine = _engineFactory(configuration);

        var results = new List<PageResult>(truth.Pages.Count);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var page in truth.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageWatch = System.Diagnostics.Stopwatch.StartNew();
            string recognised;

            try
            {
                var bytes = await File.ReadAllBytesAsync(page.ManualPath, cancellationToken).ConfigureAwait(false);
                using var raster = rasteriser.Render(bytes, page.PageNumber - 1);

                var result = await engine
                    .RecognisePageAsync(raster.EncodePng(), page.PageNumber, cancellationToken)
                    .ConfigureAwait(false);

                // Reading order comes from the line boxes rather than from whatever order the
                // detector emitted them in, or a two-column page scores as garbage for a reason
                // that has nothing to do with recognition.
                recognised = string.Join(
                    '\n',
                    result.Lines
                        .OrderByDescending(l => l.BoxPx.Top * -1)
                        .Select(l => l.Text));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not recognise {Label}", page.Label);
                recognised = string.Empty;
            }

            pageWatch.Stop();

            var accuracy = ErrorRate.Measure(page.Text, recognised);
            var pageResult = new PageResult(page, accuracy, pageWatch.Elapsed, recognised);

            results.Add(pageResult);
            progress?.Report(pageResult);

            _logger.LogInformation(
                "{Config} {Label}: CER {Cer:P1}, WER {Wer:P1}",
                configuration.Name, page.Label, accuracy.CharacterErrorRate, accuracy.WordErrorRate);
        }

        watch.Stop();
        return new BenchmarkResult(configuration, results, watch.Elapsed);
    }

    /// <summary>
    /// Scores text that is already in the PDFs, with no OCR at all.
    ///
    /// This is how another engine's output is measured: point the truth set at a folder of manuals
    /// somebody else OCR'd, and what gets scored is their text layer against the same ground truth.
    /// It is the only way to answer "is this better than Acrobat" with a number.
    /// </summary>
    public static BenchmarkResult ScoreExistingText(
        GroundTruthSet truth,
        string name,
        Func<string, int, string?> readPage)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(readPage);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<PageResult>(truth.Pages.Count);

        foreach (var page in truth.Pages)
        {
            string? existing;
            try
            {
                existing = readPage(page.ManualPath, page.PageNumber);
            }
            catch (Exception)
            {
                existing = null;
            }

            results.Add(new PageResult(
                page, ErrorRate.Measure(page.Text, existing ?? string.Empty), TimeSpan.Zero, existing ?? string.Empty));
        }

        watch.Stop();
        return new BenchmarkResult(new BenchmarkConfiguration(name), results, watch.Elapsed);
    }

    /// <summary>A table of several configurations side by side, for the console.</summary>
    public static string Table(IReadOnlyList<BenchmarkResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            return "Nothing was measured.";

        var kinds = results
            .SelectMany(r => r.Pages.Select(p => p.Page.Kind))
            .Distinct()
            .Order()
            .ToArray();

        var text = new StringBuilder();
        var width = Math.Max(24, results.Max(r => r.Configuration.Name.Length) + 2);

        text.Append("  ").Append("Configuration".PadRight(width));
        text.Append("CER".PadLeft(8)).Append("WER".PadLeft(8)).Append("WER unord".PadLeft(11));
        foreach (var kind in kinds)
            text.Append($"{kind} CER".PadLeft(16));
        text.AppendLine("pages/min".PadLeft(11));

        text.Append("  ").AppendLine(new string('-', width + 27 + kinds.Length * 16 + 11));

        foreach (var result in results)
        {
            var overall = result.Overall;
            text.Append("  ").Append(result.Configuration.Name.PadRight(width));
            text.Append(overall.CharacterErrorRate.ToString("P1", CultureInfo.InvariantCulture).PadLeft(8));
            text.Append(overall.WordErrorRate.ToString("P1", CultureInfo.InvariantCulture).PadLeft(8));
            text.Append(overall.UnorderedWordErrorRate.ToString("P1", CultureInfo.InvariantCulture).PadLeft(11));

            foreach (var kind in kinds)
            {
                var forKind = result.For(kind);
                text.Append((forKind.TruthCharacters == 0
                        ? "-"
                        : forKind.CharacterErrorRate.ToString("P1", CultureInfo.InvariantCulture))
                    .PadLeft(16));
            }

            text.AppendLine((result.PagesPerMinute <= 0 ? "-" : result.PagesPerMinute.ToString("F1", CultureInfo.InvariantCulture)).PadLeft(11));
        }

        return text.ToString();
    }

    /// <summary>Per-page detail, for export.</summary>
    public static string Csv(IReadOnlyList<BenchmarkResult> results)
    {
        var lines = new List<string> { "configuration,manual,page,kind,characterErrors,truthCharacters,cer,wordErrors,truthWords,wer,werUnordered,shareFromOrdering,seconds" };

        foreach (var result in results)
        {
            foreach (var page in result.Pages)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"\"{result.Configuration.Name}\",\"{Path.GetFileName(page.Page.ManualPath)}\",{page.Page.PageNumber}," +
                    $"{page.Page.Kind},{page.Accuracy.CharacterErrors},{page.Accuracy.TruthCharacters}," +
                    $"{page.Accuracy.CharacterErrorRate:F4},{page.Accuracy.WordErrors},{page.Accuracy.TruthWords}," +
                    $"{page.Accuracy.WordErrorRate:F4},{page.Accuracy.UnorderedWordErrorRate:F4}," +
                    $"{page.Accuracy.ShareFromOrdering:F3},{page.Elapsed.TotalSeconds:F2}"));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
