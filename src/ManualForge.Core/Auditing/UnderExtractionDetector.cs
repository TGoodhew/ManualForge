using ManualForge.Core.Geometry;
using ManualForge.Core.Rendering;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Tokens;

namespace ManualForge.Core.Auditing;

/// <summary>What the audit concluded about a document as a whole.</summary>
public enum DocumentVerdict
{
    /// <summary>Nothing on any page went unextracted.</summary>
    Sound,

    /// <summary>
    /// A few figure pages carry lettering the text layer does not hold — the ordinary state of an
    /// illustrated manual. Worth repairing, not worth alarm.
    /// </summary>
    Figures,

    /// <summary>
    /// Enough of the document is drawn rather than typeset, and extracts as nothing, that searching
    /// it will mislead. This is the finding.
    /// </summary>
    UnderExtracted,

    /// <summary>
    /// A scanned document whose existing OCR missed a substantial part of its lettering — usually
    /// the schematics, tables and figure annotation. A real gap, and a different one: the pages are
    /// images, so the remedy is to recognise them better rather than to recognise them at all.
    /// </summary>
    ScannedGaps,
}

/// <summary>Everything the audit found in one document.</summary>
public sealed record DocumentAudit(
    string Path,
    string Title,
    string ContentHash,
    int PageCount,
    IReadOnlyList<PageAudit> Pages,
    string? Error = null)
{
    /// <summary>
    /// How the document as a whole reads. Set by the runner, which knows the threshold; the
    /// default suits a document with nothing wrong with it.
    /// </summary>
    public DocumentVerdict Verdict { get; init; } = DocumentVerdict.Sound;

    public IReadOnlyList<PageAudit> Flagged => Pages.Where(p => p.IsFlagged).ToArray();

    public int FlaggedPageCount => Pages.Count(p => p.IsFlagged);

    /// <summary>Flagged pages whose missing content is drawn on the page.</summary>
    public int DrawnPageCount => Pages.Count(p => p.IsDrawn);

    /// <summary>Flagged pages whose missing content is lettering inside an image.</summary>
    public int RasterPageCount => FlaggedPageCount - DrawnPageCount;

    public int EstimatedRecoverableCharacters =>
        Pages.Where(p => p.IsFlagged).Sum(p => p.EstimatedRecoverableCharacters);

    public double FlaggedShare => Pages.Count == 0 ? 0 : FlaggedPageCount / (double)Pages.Count;

    /// <summary>
    /// How the report is ranked. Recoverable characters dominate, because the point of the ranking
    /// is "where is the most missing text", not "which file is worst proportionally" — a 4-page
    /// leaflet that is entirely diagrams matters less than a command reference with 60 bad pages.
    /// </summary>
    public double Score => EstimatedRecoverableCharacters;

    /// <summary>Flagged pages collapsed into runs, which is how a person reads a page list.</summary>
    public string FlaggedPageRanges() => PageRanges.Format(Flagged.Select(p => p.PageNumber));

    /// <summary>Resolution the repair should use, taken from the smallest lettering found.</summary>
    public int SuggestedOcrDpi =>
        Flagged.Count == 0 ? 300 : Flagged.Max(p => p.SuggestedOcrDpi);
}

/// <summary>Formats a set of page numbers the way a person would write them.</summary>
public static class PageRanges
{
    public static string Format(IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().Order().ToArray();
        if (sorted.Length == 0)
            return string.Empty;

        var parts = new List<string>();
        var start = sorted[0];
        var previous = start;

        foreach (var page in sorted.Skip(1))
        {
            if (page == previous + 1)
            {
                previous = page;
                continue;
            }

            parts.Add(start == previous ? $"{start}" : $"{start}-{previous}");
            start = previous = page;
        }

        parts.Add(start == previous ? $"{start}" : $"{start}-{previous}");
        return string.Join(",", parts);
    }

    /// <summary>Parses the form <see cref="Format"/> produces.</summary>
    public static IReadOnlyList<int> Parse(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return [];

        var pages = new List<int>();
        foreach (var part in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0
                && int.TryParse(part[..dash], out var from)
                && int.TryParse(part[(dash + 1)..], out var to)
                && to >= from)
            {
                pages.AddRange(Enumerable.Range(from, to - from + 1));
            }
            else if (int.TryParse(part, out var single))
            {
                pages.Add(single);
            }
        }

        return pages;
    }
}

/// <summary>
/// Finds pages whose text layer is present but incomplete.
///
/// <para>
/// The failure this looks for is invisible to every check that asks "does this file have a text
/// layer?". A manual typeset in FrameMaker and distilled to PDF carries perfect text for its prose
/// and draws its syntax diagrams, pin-outs and schematics as vector graphics, which extract as
/// nothing. The prose passing the check is exactly what stops anybody looking at the figures.
/// </para>
///
/// <para>
/// So the question is asked per page rather than per document. Most affected documents are mixed —
/// good prose, empty figures — and a per-document verdict either re-OCRs text that was already
/// right or skips the pages that were wrong.
/// </para>
///
/// <para>
/// Two stages. The first reads the page and costs nothing beyond the parse that extraction needs
/// anyway; it decides whether the page is worth rendering. The second renders it and compares the
/// ink against the glyph boxes, which is the measurement that actually settles it. Keeping the
/// render behind a gate is what makes a hundred-thousand-page audit finish in minutes.
/// </para>
/// </summary>
public sealed class UnderExtractionDetector(DoctorOptions? options = null)
{
    private readonly DoctorOptions _options = options ?? new DoctorOptions();

    public DoctorOptions Options => _options;

    public DocumentAudit Audit(
        string path, string contentHash = "", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var title = Path.GetFileNameWithoutExtension(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new DocumentAudit(path, title, contentHash, 0, [], ex.Message);
        }

        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
            var rasteriser = new PageRasteriser(new RasterOptions { Dpi = _options.AuditDpi });
            var outline = OutlineHeadings(document);

            var wanted = _options.SamplePages > 0
                ? Classification.DocumentClassifier.SamplePageNumbers(document.NumberOfPages, _options.SamplePages)
                : Enumerable.Range(1, document.NumberOfPages).ToArray();

            var audits = new List<PageAudit>(wanted.Count);

            foreach (var pageNumber in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audits.Add(AuditPage(document, bytes, rasteriser, pageNumber, outline));
            }

            var flagged = audits.Count(a => a.IsFlagged);
            var drawn = audits.Count(a => a.IsDrawn);
            var threshold = audits.Count * _options.DocumentFlagShare;

            // Drawn first, because a document that is both is the more serious of the two: content
            // that was never text at all is invisible in a way that badly-OCR'd content is not.
            var verdict = flagged == 0 ? DocumentVerdict.Sound
                : drawn >= threshold ? DocumentVerdict.UnderExtracted
                : flagged - drawn >= threshold ? DocumentVerdict.ScannedGaps
                : DocumentVerdict.Figures;

            return new DocumentAudit(path, title, contentHash, document.NumberOfPages, audits)
            {
                Verdict = verdict,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DocumentAudit(path, title, contentHash, 0, [], ex.Message);
        }
    }

    /// <summary>
    /// Audits one page and draws what the ink comparison saw, so a person can check the detector
    /// by eye rather than taking its word for it.
    /// </summary>
    /// <summary>
    /// A page's own extracted words, grouped into lines and put through the same reading order the
    /// repair uses.
    ///
    /// <para>
    /// Here to answer one question honestly: would the column cut mistake a close-set table for two
    /// columns and read it down instead of along? A synthetic fixture cannot settle that, because
    /// the whole risk is in the real spacing of a real table. This runs the ordering over geometry
    /// taken from an actual page so the answer can be looked at rather than argued about.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReadingOrderOf(string path, int pageNumber)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
        var page = document.GetPage(pageNumber);

        // Words into lines, the way the indexer does: by shared baseline.
        var lines = new List<(RectD Box, List<UglyToad.PdfPig.Content.Word> Words)>();
        foreach (var word in page.GetWords())
        {
            var box = word.BoundingBox;
            var line = lines.FirstOrDefault(l => Math.Abs(l.Box.Y - (page.Height - box.Top)) < 3.0);

            if (line.Words is null)
            {
                line = (new RectD(box.Left, page.Height - box.Top, box.Width, Math.Abs(box.Height)), []);
                lines.Add(line);
            }

            line.Words.Add(word);
        }

        var merged = lines
            .Select(l =>
            {
                var left = l.Words.Min(w => w.BoundingBox.Left);
                var right = l.Words.Max(w => w.BoundingBox.Right);
                var height = Math.Max(1, l.Box.Height);
                return (
                    Box: new RectD(left, l.Box.Y, right - left, height),
                    Text: string.Join(' ', l.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            })
            .ToArray();

        return ReadingOrder.Sort(merged, m => m.Box).Select(m => m.Text).ToArray();
    }

    /// <summary>
    /// The first few extracted letters of a page with the geometry they report, which is the only
    /// way to tell a detector that is wrong from a text layer that is.
    /// </summary>
    public static IReadOnlyList<string> DescribeLetters(string path, int pageNumber, int take = 12)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
        var page = document.GetPage(pageNumber);

        return page.Letters.Take(take).Select(l => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"'{l.Value}' font={l.FontName} size={l.PointSize:F2} " +
            $"bbox=({l.BoundingBox.Left:F1},{l.BoundingBox.Bottom:F1})-" +
            $"({l.BoundingBox.Right:F1},{l.BoundingBox.Top:F1}) " +
            $"loose=({l.GlyphRectangleLoose.Left:F1},{l.GlyphRectangleLoose.Bottom:F1})-" +
            $"({l.GlyphRectangleLoose.Right:F1},{l.GlyphRectangleLoose.Top:F1}) " +
            $"baseline=({l.StartBaseLine.X:F1},{l.StartBaseLine.Y:F1})-({l.EndBaseLine.X:F1},{l.EndBaseLine.Y:F1})"))
            .ToArray();
    }

    public (PageAudit Audit, SkiaSharp.SKBitmap? Diagnostic) Explain(string path, int pageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = File.ReadAllBytes(path);
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = _options.AuditDpi });

        var audit = AuditPage(document, bytes, rasteriser, pageNumber, OutlineHeadings(document));

        try
        {
            var page = document.GetPage(pageNumber);
            using var raster = rasteriser.Render(bytes, pageNumber - 1);
            var geometry = GeometryFor(page, raster.Width, raster.Height);
            return (audit, InkAnalyser.Diagnose(raster.Bitmap, geometry, TextBoxesDisplayPt(page, _options), _options));
        }
        catch (Exception)
        {
            return (audit, null);
        }
    }

    private PageAudit AuditPage(
        PdfDocument document,
        byte[] bytes,
        PageRasteriser rasteriser,
        int pageNumber,
        IReadOnlyDictionary<int, List<string>> outline)
    {
        Page page;
        try
        {
            page = document.GetPage(pageNumber);
        }
        catch (Exception)
        {
            return Unreadable(pageNumber, "the page could not be parsed");
        }

        var signals = new List<string>();

        var glyphs = page.Letters.Count;
        var decoded = page.Letters.Count(l => l.Value.Length > 0 && char.IsLetterOrDigit(l.Value[0]));
        var (pathOps, textOps) = CountOperations(page);
        var (images, imageCoverage) = Images(page);
        var (type3, noToUnicode) = FontFlags(document, page);
        var headingMissing = OutlineHeadingMissing(page, outline);

        if (type3)
            signals.Add("Type 3 font");
        if (noToUnicode)
            signals.Add("embedded font with no /ToUnicode");
        if (headingMissing)
            signals.Add("a heading the outline places on this page is not in its text");

        // A scanned page. The existing classify-and-OCR path owns this case; saying so here and
        // moving on keeps the report about the finding it exists for.
        if (glyphs == 0 && imageCoverage >= _options.ScannedPageImageCoverage)
        {
            signals.Add($"no text layer, and an image covering {imageCoverage:P0} of the page");
            return new PageAudit(
                pageNumber, glyphs, decoded, pathOps, textOps, images, imageCoverage,
                type3, noToUnicode, InkAnalysis.NotRendered, headingMissing,
                PageVerdict.NoTextLayer, signals);
        }

        // Glyphs drawn, nothing decodable. Adding a second text layer to a page that already has
        // one is the mistake that produces "BBrrooaaddbbaanndd", so this is called out as its own
        // thing rather than swept in with the pages below.
        if (glyphs > 0 && decoded < _options.UndecodableCharactersPerPage && glyphs >= decoded * 4)
        {
            signals.Add($"{glyphs} glyphs drawn but only {decoded} characters decode");
            return new PageAudit(
                pageNumber, glyphs, decoded, pathOps, textOps, images, imageCoverage,
                type3, noToUnicode, InkAnalysis.NotRendered, headingMissing,
                PageVerdict.Undecodable, signals);
        }

        var worthRendering =
            glyphs < _options.RenderBelowCharactersPerPage
            || pathOps >= _options.RenderAtOrAbovePathOperations
            || imageCoverage >= _options.RenderAtOrAboveImageCoverage;

        if (!worthRendering)
        {
            return new PageAudit(
                pageNumber, glyphs, decoded, pathOps, textOps, images, imageCoverage,
                type3, noToUnicode, InkAnalysis.NotRendered, headingMissing,
                PageVerdict.Fine, signals);
        }

        InkAnalysis ink;
        try
        {
            using var raster = rasteriser.Render(bytes, pageNumber - 1);
            ink = InkAnalyser.Analyse(
                raster.Bitmap,
                GeometryFor(page, raster.Width, raster.Height),
                TextBoxesDisplayPt(page, _options),
                _options);
        }
        catch (Exception)
        {
            signals.Add("the page could not be rendered, so the ink comparison was not made");
            return new PageAudit(
                pageNumber, glyphs, decoded, pathOps, textOps, images, imageCoverage,
                type3, noToUnicode, InkAnalysis.NotRendered, headingMissing,
                PageVerdict.Unreadable, signals);
        }

        var flagged = ink.UncoveredInkFraction >= _options.UncoveredInkFraction
                      && ink.GlyphLikeBlobs >= _options.MinimumGlyphLikeBlobs;

        // Where the missing content lives. Asked of the images on the page rather than of the path
        // operators, because that is the direct question: a scanned page is one big image, a
        // screenshot covers a good part of one, and a vector syntax diagram has no image at all.
        var kind = imageCoverage >= _options.RasterPageImageCoverage ? PageKind.Raster : PageKind.Drawn;

        if (flagged)
        {
            signals.Add(kind == PageKind.Raster
                ? $"the missing lettering is inside an image covering {imageCoverage:P0} of the page"
                : "the missing content is drawn on the page, not photographed");

            signals.Add(
                $"{ink.UncoveredInkFraction:P2} of the page is ink no extracted glyph accounts for");
            signals.Add($"{ink.GlyphLikeBlobs} glyph-shaped clusters of it");
            if (pathOps >= _options.RenderAtOrAbovePathOperations)
                signals.Add($"{pathOps} path-painting operations, so the page draws its content");
        }

        return new PageAudit(
            pageNumber, glyphs, decoded, pathOps, textOps, images, imageCoverage,
            type3, noToUnicode, ink, headingMissing,
            flagged ? PageVerdict.UnderExtracted : PageVerdict.Fine,
            signals)
        {
            Kind = kind,
            EstimatedRecoverableCharacters =
                flagged ? (int)Math.Round(ink.GlyphLikeBlobs * _options.CharactersPerBlob) : 0,
            SuggestedOcrDpi = SuggestDpi(ink),
        };
    }

    /// <summary>
    /// Resolution to OCR at, from the size of the lettering that was missed.
    ///
    /// <para>
    /// 300 dpi gives a 10 pt glyph about 42 pixels of height, which is comfortable. The annotation
    /// on a syntax diagram is often 5 or 6 pt, which at 300 dpi is 25 pixels and marginal, so those
    /// pages are rendered higher. Tuning this per document rather than globally is what the corpus
    /// needs; a blanket 600 dpi would quadruple the work for the pages that did not need it.
    /// </para>
    /// </summary>
    private static int SuggestDpi(InkAnalysis ink) => ink.LargestBlobHeightPt switch
    {
        <= 0 => 300,
        < 7.0 => 600,
        < 9.0 => 400,
        _ => 300,
    };

    private static PageAudit Unreadable(int pageNumber, string why) =>
        new(pageNumber, 0, 0, 0, 0, 0, 0, false, false, InkAnalysis.NotRendered, false,
            PageVerdict.Unreadable, [why]);

    /// <summary>
    /// Glyph boxes in display space, which is the space PdfPig reports and the space
    /// <see cref="PageGeometry.ToDisplaySpace"/> maps into.
    /// </summary>
    public static IReadOnlyList<RectD> TextBoxesDisplayPt(Page page) =>
        TextBoxesDisplayPt(page, new DoctorOptions());

    public static IReadOnlyList<RectD> TextBoxesDisplayPt(Page page, DoctorOptions options)
    {
        var boxes = new List<RectD>(page.Letters.Count);
        foreach (var letter in page.Letters)
        {
            // The loose rectangle is the glyph's advance box rather than its tight outline, which
            // is what should be credited: a space inside a word is not ink, but it is also not
            // something OCR would find, and crediting it stops inter-letter gaps counting as
            // unaccounted-for ink.
            var box = letter.GlyphRectangleLoose;
            var left = Math.Min(box.Left, box.Right);
            var right = Math.Max(box.Left, box.Right);
            var bottom = Math.Min(box.Bottom, box.Top);
            var top = Math.Max(box.Bottom, box.Top);

            // That rectangle comes from the font's own metrics, and on this corpus those metrics
            // are routinely wrong. The bold headings in the TDS3014B programmer's manual report a
            // box that stops below their own ascenders, so the top of every heading counted as ink
            // nothing accounted for, and 43 perfectly good pages were flagged because of it.
            //
            // A box built from the baseline and the rendered point size does not depend on the
            // metrics being right. 0.30 em below the baseline covers the deepest descender and
            // 0.95 em above it covers an accented capital, which together come to about one line
            // of text — so a line still cannot claim the ink of the line below it.
            if (letter.TextOrientation == TextOrientation.Horizontal)
            {
                var size = letter.PointSize > 0 ? letter.PointSize : top - bottom;
                var baseline = letter.StartBaseLine.Y;

                left = Math.Min(left, Math.Min(letter.StartBaseLine.X, letter.EndBaseLine.X))
                       - options.TextBoxPaddingEm * size;
                right = Math.Max(right, Math.Max(letter.StartBaseLine.X, letter.EndBaseLine.X))
                        + options.TextBoxPaddingEm * size;
                bottom = Math.Min(bottom, baseline - 0.30 * size);
                top = Math.Max(top, baseline + 0.95 * size);
            }

            boxes.Add(new RectD(left, bottom, right - left, top - bottom));
        }

        return boxes;
    }

    private static PageGeometry GeometryFor(Page page, int pixelWidth, int pixelHeight)
    {
        var crop = page.CropBox.Bounds;
        return new PageGeometry(
            Math.Min(crop.Left, crop.Right),
            Math.Min(crop.Bottom, crop.Top),
            Math.Abs(crop.Width),
            Math.Abs(crop.Height),
            page.Rotation.Value,
            pixelWidth,
            pixelHeight);
    }

    /// <summary>
    /// Counts path painting against text showing in the content stream. No rendering needed, and
    /// on its own it is the cheapest way to tell a page that draws its content from one that sets
    /// it.
    /// </summary>
    private static (int PathOps, int TextOps) CountOperations(Page page)
    {
        var paths = 0;
        var texts = 0;

        try
        {
            foreach (var operation in page.Operations)
            {
                // Matching on the namespace rather than on each operator type: PDF has ten path
                // painting operators and four text showing ones, and a list of them here would be
                // one more thing to keep in step with the library.
                var space = operation.GetType().Namespace;
                if (space is null)
                    continue;

                if (space.EndsWith("PathPainting", StringComparison.Ordinal))
                    paths++;
                else if (space.EndsWith("TextShowing", StringComparison.Ordinal))
                    texts++;
            }
        }
        catch (Exception)
        {
            // A content stream that will not re-parse costs this one signal, not the page.
        }

        return (paths, texts);
    }

    private static (int Count, double Coverage) Images(Page page)
    {
        try
        {
            var area = Math.Abs(page.CropBox.Bounds.Area);
            if (area <= 0)
                return (page.NumberOfImages, 0);

            var covered = 0.0;
            var count = 0;
            foreach (var image in page.GetImages())
            {
                count++;
                covered = Math.Max(covered, Math.Abs(image.BoundingBox.Area) / area);
            }

            return (count, Math.Min(1.0, covered));
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Whether the page uses a Type 3 font, or an embedded font with no /ToUnicode CMap. Both
    /// extract as nothing or as mojibake, so both are worth naming in the report even though
    /// neither decides the verdict on its own.
    /// </summary>
    private static (bool Type3, bool NoToUnicode) FontFlags(PdfDocument document, Page page)
    {
        var type3 = false;
        var missing = false;

        var seen = new HashSet<IndirectReference>();

        foreach (var letter in page.Letters)
        {
            var reference = letter.FontDetails?.FontDictionaryReference;
            if (reference is null || !seen.Add(reference.Value))
                continue;

            try
            {
                if (document.Structure.GetObject(reference.Value).Data is not DictionaryToken font)
                    continue;

                if (font.TryGet(NameToken.Subtype, out NameToken subtype)
                    && string.Equals(subtype.Data, "Type3", StringComparison.Ordinal))
                {
                    type3 = true;
                }

                // A simple font with no /ToUnicode is only a problem when its encoding is not one
                // a reader knows, so the CMap's absence is reported rather than treated as fatal.
                if (!font.ContainsKey(NameToken.ToUnicode))
                    missing = true;
            }
            catch (Exception)
            {
                // A font dictionary that will not resolve tells us nothing either way.
            }
        }

        return (type3, missing);
    }

    /// <summary>
    /// Headings the document's own outline places on each page.
    ///
    /// <para>
    /// This is the cross-check the printed table of contents would give, taken from the PDF outline
    /// instead. The outline names a page directly, so no folio has to be parsed and no
    /// chapter-relative page number has to be resolved — both of which are guesses, and a guess
    /// inside a detector is a false positive waiting to happen.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<int, List<string>> OutlineHeadings(PdfDocument document)
    {
        var byPage = new Dictionary<int, List<string>>();

        try
        {
            if (!document.TryGetBookmarks(out var bookmarks))
                return byPage;

            foreach (var node in bookmarks.GetNodes().OfType<DocumentBookmarkNode>())
            {
                if (node.PageNumber <= 0 || string.IsNullOrWhiteSpace(node.Title))
                    continue;

                if (!byPage.TryGetValue(node.PageNumber, out var titles))
                    byPage[node.PageNumber] = titles = [];

                titles.Add(node.Title);
            }
        }
        catch (Exception)
        {
            // No outline, or one that will not parse. The signal simply does not fire.
        }

        return byPage;
    }

    /// <summary>
    /// Whether a heading the outline places on this page is absent from the page's own text.
    ///
    /// <para>
    /// Only the heading's distinctive words are looked for, and only when there are some: an
    /// outline entry of "Introduction" or "2" proves nothing about a page either way.
    /// </para>
    /// </summary>
    private static bool OutlineHeadingMissing(Page page, IReadOnlyDictionary<int, List<string>> outline)
    {
        if (!outline.TryGetValue(page.Number, out var titles))
            return false;

        string text;
        try
        {
            text = string.Concat(page.Letters.Select(l => l.Value));
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var title in titles)
        {
            var words = title
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('.', ',', ':', ';', '(', ')', '-'))
                .Where(w => w.Length >= 4)
                .ToArray();

            if (words.Length == 0)
                continue;

            var found = words.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (found * 2 < words.Length)
                return true;
        }

        return false;
    }
}
