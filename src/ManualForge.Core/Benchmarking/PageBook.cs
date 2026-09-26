using ManualForge.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace ManualForge.Core.Benchmarking;

/// <summary>One page of one document, to be collected into a test book.</summary>
public sealed record PageReference(string Path, int PageNumber);

/// <summary>What happened to one page while the book was assembled.</summary>
public sealed record PageOutcome(PageReference Source, int BookPage, string? Skipped = null);

/// <summary>
/// Collects pages from many PDFs into one image-only PDF.
///
/// <para>
/// Built to compare recognisers on equal terms. A comparison is only fair if both engines see the
/// same pixels and neither can cheat by reading a text layer that is already there, so every page
/// is rasterised and re-embedded as an image: whatever text the source carried is gone by
/// construction rather than by a filter that might miss some.
/// </para>
///
/// <para>
/// The page keeps its original physical size, so a 36-inch fold-out stays a 36-inch fold-out and a
/// recogniser that assumes letter-sized pages is caught out rather than flattered.
/// </para>
/// </summary>
public sealed class PageBook(PageRasteriser? rasteriser = null)
{
    private readonly PageRasteriser _rasteriser = rasteriser ?? new PageRasteriser();

    /// <summary>
    /// Renders each reference and writes them as one PDF. Returns what became of each, in order,
    /// including the ones that could not be rendered - a book that silently dropped a page would
    /// make the manifest lie about which source page a result came from.
    /// </summary>
    public IReadOnlyList<PageOutcome> Assemble(
        IEnumerable<PageReference> pages, string outputPath, int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outcomes = new List<PageOutcome>();
        using var book = new PdfDocument();
        book.Info.Title = "ManualForge page book";

        var bytesByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in pages)
        {
            try
            {
                if (!bytesByPath.TryGetValue(reference.Path, out var bytes))
                {
                    bytes = File.ReadAllBytes(reference.Path);
                    bytesByPath[reference.Path] = bytes;
                }

                using var rendered = _rasteriser.Render(bytes, reference.PageNumber - 1);
                Add(book, rendered.Bitmap, rendered.RequestedDpi, quality);
                outcomes.Add(new PageOutcome(reference, book.PageCount));
            }
            catch (Exception ex)
            {
                outcomes.Add(new PageOutcome(reference, 0, ex.Message));
            }
        }

        if (book.PageCount == 0)
            throw new InvalidOperationException("No page could be rendered, so there is no book to write.");

        book.Save(outputPath);
        return outcomes;
    }

    private static void Add(PdfDocument book, SKBitmap bitmap, int dpi, int quality)
    {
        // JPEG rather than PNG: a 300 dpi fold-out is tens of megabytes lossless, and the
        // artefacts JPEG introduces at 90 are far below what these scans already carry.
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("Could not encode the rendered page.");

        var page = book.AddPage();

        // The bitmap came back at some dpi; put it back at its true physical size so the page is
        // the size the original was, not a letter-sized approximation of it.
        var widthPt = bitmap.Width * 72.0 / dpi;
        var heightPt = bitmap.Height * 72.0 / dpi;
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);

        var bytes = data.ToArray();
        using var stream = new MemoryStream(bytes);
        using var image = XImage.FromStream(stream);
        using var canvas = XGraphics.FromPdfPage(page);
        canvas.DrawImage(image, 0, 0, widthPt, heightPt);
    }
}
