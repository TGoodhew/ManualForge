using System.Security.Cryptography;
using System.Text;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace ManualForge.Core.Auditing;

/// <remarks>
/// A record rather than a class so a caller can take the options it was given and narrow them —
/// the desktop application does exactly that, adding the documents somebody ticked without having
/// to know what else is set.
/// </remarks>
public sealed record RepairOptions
{
    /// <summary>
    /// Render every page at this resolution instead of the one the audit suggested per page.
    /// </summary>
    public int? Dpi { get; init; }

    /// <summary>Ceiling on the per-page suggestion, so one odd page cannot ask for 1200 dpi.</summary>
    public int MaximumDpi { get; init; } = 600;

    /// <summary>Recognised words below this score are dropped before the text is stored.</summary>
    public double MinimumConfidence { get; init; } = 0.30;

    /// <summary>
    /// How much of a recognised word's box must lie under the embedded text layer before the word
    /// is treated as something the embedded layer already has.
    ///
    /// <para>
    /// This is what makes the repair a merge rather than a replacement. The headings on a figure
    /// page extract perfectly and will also be recognised; keeping both would put the OCR's reading
    /// of correctly-typeset text into the index beside the real thing, which is exactly the
    /// regression the merge exists to avoid.
    /// </para>
    /// </summary>
    public double CoveredThreshold { get; init; } = 0.6;

    /// <summary>Re-recognise pages that have already been repaired.</summary>
    public bool Force { get; init; }

    /// <summary>Stop after this many documents. The audit's own ranking decides which.</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Repair only these documents. Empty means every document the verdict filters below allow,
    /// which is what the command line does; the desktop application sets it to whatever was ticked,
    /// because choosing which manual to spend twenty minutes of GPU time on is a judgement somebody
    /// should be allowed to make.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Also repair documents the audit called merely figure-heavy, not just the ones it called
    /// under-extracted. Cheap — they are a handful of pages each.
    /// </summary>
    public bool IncludeFigurePages { get; init; } = true;

    /// <summary>
    /// Also repair scanned documents whose existing OCR missed lettering.
    ///
    /// <para>
    /// Off by default, and deliberately. On this corpus that is ten thousand pages against a few
    /// hundred for the drawn ones — hours of GPU time for a different problem from the one the
    /// audit was built to find. Worth doing, worth choosing to do.
    /// </para>
    /// </summary>
    public bool IncludeScannedPages { get; init; }
}

public sealed record RepairProgress(
    string Path, int PageNumber, int PagesDone, int PagesTotal, int WordsRecovered);

public sealed record RepairReport(
    int DocumentsRepaired,
    int PagesRepaired,
    int PagesAlreadyDone,
    int PagesFailed,
    int DocumentsStale,
    long WordsRecovered,
    long CharactersRecovered,
    double MeanConfidence,
    TimeSpan Elapsed)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : PagesRepaired / Elapsed.TotalMinutes;
}

/// <summary>
/// Recovers the text on pages the audit flagged, by rendering and recognising them.
///
/// <para>
/// Three rules shape this, and all three are about not making things worse.
/// </para>
///
/// <para>
/// <b>Only flagged pages.</b> The corpus is 100,830 pages and most of them are fine. Re-recognising
/// a page whose text layer is correct would replace correctly-spelled, correctly-spaced typesetting
/// with a machine's reading of a picture of it.
/// </para>
///
/// <para>
/// <b>Merge, never replace.</b> Recognised words that land under the embedded text layer are
/// dropped, because that text is already there and already right. What is kept is what the embedded
/// layer missed.
/// </para>
///
/// <para>
/// <b>Nothing is written to any PDF.</b> The recovered text goes into the audit store and is merged
/// at index time. Adding a second text layer to a page that already has one leaves an extractor
/// interleaving the two character by character, and a document that is less searchable than it was
/// before.
/// </para>
/// </summary>
public sealed class PageRepairer(IOcrEngine engine, ILogger<PageRepairer>? logger = null)
{
    private readonly IOcrEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<RepairReport> RepairAsync(
        DoctorStore store,
        RepairOptions? options = null,
        IProgress<RepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new RepairOptions();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var documents = store.Flagged()
            .Where(d => d.Verdict switch
            {
                DocumentVerdict.UnderExtracted => true,
                DocumentVerdict.Figures => options.IncludeFigurePages,
                DocumentVerdict.ScannedGaps => options.IncludeScannedPages,
                _ => false,
            })
            .Where(d => options.Force || d.OutstandingPages > 0)
            .Where(d => options.Paths.Count == 0
                        || options.Paths.Contains(d.Path, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (options.Limit is { } limit)
            documents = documents.Take(limit).ToArray();

        var totalPages = documents.Sum(d => d.FlaggedPages);

        var repaired = 0;
        var alreadyDone = 0;
        var failed = 0;
        var stale = 0;
        var documentsTouched = 0;
        long words = 0;
        long characters = 0;
        var confidenceTotal = 0.0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(document.Path))
            {
                _logger.LogWarning("{Path} is in the audit but no longer on disk", document.Path);
                continue;
            }

            var hash = await HashAsync(document.Path, cancellationToken).ConfigureAwait(false);
            if (hash != document.ContentHash)
            {
                // The page numbers in the audit describe the file as it was. Recovering text for
                // page 40 of a file that has since been rebuilt would attach it to whatever page 40
                // is now, which is worse than not repairing it at all.
                stale++;
                _logger.LogWarning(
                    "{Path} has changed since it was audited; re-run the audit before repairing it",
                    document.Path);
                continue;
            }

            var flagged = store.Findings(document.Path, PageVerdict.UnderExtracted)
                .Where(f => options.IncludeScannedPages || f.Kind == PageKind.Drawn
                            || document.Verdict == DocumentVerdict.Figures)
                .ToArray();

            if (flagged.Length == 0)
                continue;

            var existing = store.Repairs(document.Path, hash);

            var bytes = await File.ReadAllBytesAsync(document.Path, cancellationToken).ConfigureAwait(false);
            using var pdf = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });

            var touched = false;

            foreach (var finding in flagged)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!options.Force && existing.ContainsKey(finding.PageNumber))
                {
                    alreadyDone++;
                    continue;
                }

                var dpi = Math.Min(options.MaximumDpi, options.Dpi ?? finding.SuggestedDpi);

                try
                {
                    var repair = await RepairPageAsync(
                        pdf, bytes, document.Path, hash, finding.PageNumber, dpi, options, cancellationToken)
                        .ConfigureAwait(false);

                    store.SaveRepair(repair);

                    repaired++;
                    touched = true;
                    words += repair.WordCount;
                    characters += repair.OcrText.Length;
                    confidenceTotal += repair.MeanConfidence;

                    progress?.Report(new RepairProgress(
                        document.Path, finding.PageNumber, repaired + alreadyDone, totalPages, repair.WordCount));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning(
                        ex, "Could not repair page {Page} of {Path}", finding.PageNumber, document.Path);
                }
            }

            if (touched)
                documentsTouched++;
        }

        stopwatch.Stop();

        return new RepairReport(
            documentsTouched, repaired, alreadyDone, failed, stale, words, characters,
            repaired == 0 ? 0 : confidenceTotal / repaired,
            stopwatch.Elapsed);
    }

    private async Task<PageRepair> RepairPageAsync(
        PdfDocument pdf,
        byte[] bytes,
        string path,
        string hash,
        int pageNumber,
        int dpi,
        RepairOptions options,
        CancellationToken cancellationToken)
    {
        var page = pdf.GetPage(pageNumber);

        var rasteriser = new PageRasteriser(new RasterOptions
        {
            Dpi = dpi,

            // Colour, not greyscale. A screenshot of an instrument display is often light text on a
            // dark field, and desaturating it costs the contrast the recogniser needs.
            Grayscale = false,
        });

        using var raster = rasteriser.Render(bytes, pageNumber - 1);

        var recognised = await _engine
            .RecognisePageAsync(raster.EncodePng(), pageNumber, cancellationToken)
            .ConfigureAwait(false);

        var geometry = new PageGeometry(
            Math.Min(page.CropBox.Bounds.Left, page.CropBox.Bounds.Right),
            Math.Min(page.CropBox.Bounds.Bottom, page.CropBox.Bounds.Top),
            Math.Abs(page.CropBox.Bounds.Width),
            Math.Abs(page.CropBox.Bounds.Height),
            page.Rotation.Value,
            raster.Width,
            raster.Height);

        var covered = new EmbeddedTextMap(
            UnderExtractionDetector.TextBoxesDisplayPt(page), geometry, raster.Width, raster.Height);

        var text = new StringBuilder();
        var kept = 0;
        var confidence = 0.0;

        // Reading order, not detection order — and over runs rather than whole detected lines. A
        // detected line can already span two columns, in which case no amount of reordering lines
        // can separate them; see TextRuns for the page that proved it.
        foreach (var run in ReadingOrder.Sort(TextRuns(recognised, options), r => r.Box))
        {
            var parts = new List<string>();

            foreach (var word in run.Words)
            {
                if (covered.Fraction(word.BoxPx) >= options.CoveredThreshold)
                    continue;

                parts.Add(word.Text);
                kept++;
                confidence += word.Confidence;
            }

            if (parts.Count > 0)
                text.AppendLine(string.Join(' ', parts));
        }

        return new PageRepair(
            path,
            pageNumber,
            hash,
            dpi,
            text.ToString().TrimEnd(),
            kept == 0 ? 0 : confidence / kept,
            kept,
            DateTimeOffset.UtcNow);
    }

    /// <summary>One run of words that belong together: a phrase with no column gap inside it.</summary>
    private sealed record TextRun(RectD Box, IReadOnlyList<RecognisedWord> Words);

    /// <summary>
    /// Breaks the recogniser's lines into runs at the gaps that separate columns.
    ///
    /// <para>
    /// This exists because reordering whole lines was not enough. Page 2-60 of the 54845A
    /// Programmer's Guide has a note in the left margin and a syntax diagram beside it, and the
    /// detector returns <c>The AUX CHANnel channel_number</c> as a single line — the note's words
    /// and the diagram's label already merged into one box. Sorting lines cannot unpick that; the
    /// line itself has to be cut first, at the blank space that separates the two.
    /// </para>
    ///
    /// <para>
    /// The cut is made where the gap between consecutive words exceeds
    /// <see cref="ColumnGapInLineHeights"/> times the line's own height, which is far wider than
    /// any word space and far narrower than a real column gutter.
    /// </para>
    /// </summary>
    private static IReadOnlyList<TextRun> TextRuns(RecognisedPage page, RepairOptions options)
    {
        var runs = new List<TextRun>();

        foreach (var line in page.Lines)
        {
            var words = line.Words
                .Where(w => w.IsUsable && w.Confidence >= options.MinimumConfidence)
                .OrderBy(w => w.BoxPx.Left)
                .ToArray();

            if (words.Length == 0)
                continue;

            var height = line.BoxPx.Height > 0
                ? line.BoxPx.Height
                : words.Max(w => w.BoxPx.Height);

            var limit = Math.Max(1.0, height) * ColumnGapInLineHeights;

            var current = new List<RecognisedWord> { words[0] };

            for (var i = 1; i < words.Length; i++)
            {
                if (words[i].BoxPx.Left - words[i - 1].BoxPx.Right > limit)
                {
                    runs.Add(Run(current));
                    current = [];
                }

                current.Add(words[i]);
            }

            runs.Add(Run(current));
        }

        return runs;

        static TextRun Run(List<RecognisedWord> words) =>
            new(
                RectD.FromEdges(
                    words.Min(w => w.BoxPx.Left),
                    words.Min(w => w.BoxPx.Top),
                    words.Max(w => w.BoxPx.Right),
                    words.Max(w => w.BoxPx.Bottom)),
                words);
    }

    /// <summary>
    /// How wide a gap between two words, in multiples of the line's height, means they are in
    /// different columns rather than the same sentence.
    ///
    /// <para>
    /// A word space is about a third of a line's height; the space after a full stop is not much
    /// more. Two line heights is well clear of both and well under the gutters this corpus uses,
    /// and splitting a line one word too early costs a line break in the recovered text, which is
    /// invisible to search and harmless to read. Splitting one too late costs a sentence that says
    /// the opposite of what the page says.
    /// </para>
    /// </summary>
    private const double ColumnGapInLineHeights = 2.0;

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Where the embedded text layer already covers the page, as a coarse grid over the rendered image.
///
/// <para>
/// A grid rather than a rectangle-by-rectangle test because the embedded boxes are per glyph and a
/// recognised box is a whole word: overlapping a word against several hundred single-letter
/// rectangles one at a time answers the wrong question and answers it slowly.
/// </para>
/// </summary>
internal sealed class EmbeddedTextMap
{
    private const int CellPixels = 4;

    private readonly bool[] _cells;
    private readonly int _columns;
    private readonly int _rows;

    public EmbeddedTextMap(
        IReadOnlyList<RectD> boxesDisplayPt, PageGeometry geometry, int width, int height)
    {
        _columns = Math.Max(1, (width + CellPixels - 1) / CellPixels);
        _rows = Math.Max(1, (height + CellPixels - 1) / CellPixels);
        _cells = new bool[_columns * _rows];

        foreach (var box in boxesDisplayPt)
        {
            var left = box.Left / geometry.PointsPerPixelX;
            var right = box.Right / geometry.PointsPerPixelX;
            var top = (geometry.VisualHeightPt - box.Bottom) / geometry.PointsPerPixelY;
            var bottom = (geometry.VisualHeightPt - box.Top) / geometry.PointsPerPixelY;

            var c0 = Math.Clamp((int)(Math.Min(left, right) / CellPixels), 0, _columns - 1);
            var c1 = Math.Clamp((int)(Math.Max(left, right) / CellPixels), 0, _columns - 1);
            var r0 = Math.Clamp((int)(Math.Min(top, bottom) / CellPixels), 0, _rows - 1);
            var r1 = Math.Clamp((int)(Math.Max(top, bottom) / CellPixels), 0, _rows - 1);

            for (var r = r0; r <= r1; r++)
            {
                for (var c = c0; c <= c1; c++)
                    _cells[r * _columns + c] = true;
            }
        }
    }

    /// <summary>What share of a recognised word's box the embedded text layer already covers.</summary>
    public double Fraction(RectD boxPx)
    {
        var c0 = Math.Clamp((int)(boxPx.Left / CellPixels), 0, _columns - 1);
        var c1 = Math.Clamp((int)(boxPx.Right / CellPixels), 0, _columns - 1);
        var r0 = Math.Clamp((int)(boxPx.Top / CellPixels), 0, _rows - 1);
        var r1 = Math.Clamp((int)(boxPx.Bottom / CellPixels), 0, _rows - 1);

        var total = 0;
        var marked = 0;

        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                total++;
                if (_cells[r * _columns + c])
                    marked++;
            }
        }

        return total == 0 ? 0 : marked / (double)total;
    }
}
