using PDFtoImage;
using SkiaSharp;

namespace ManualForge.Core.Rendering;

public sealed class RasterOptions
{
    /// <summary>Resolution to rasterise at. 300 is the usual sweet spot for 1950s-80s scans.</summary>
    public int Dpi { get; init; } = 300;

    /// <summary>
    /// Render greyscale rather than colour. Almost every page in this corpus is bitonal, and
    /// greyscale cuts the bitmap to a quarter of the size with no loss for OCR.
    /// </summary>
    public bool Grayscale { get; init; } = true;

    /// <summary>
    /// Leave annotations out of the raster. They are not part of the scan, and OCRing them would
    /// put text into the layer that does not correspond to page content.
    /// </summary>
    public bool WithAnnotations { get; init; } = false;

    public bool WithFormFill { get; init; } = false;

    /// <summary>
    /// The most pixels a page is rasterised to. A page that would exceed it at <see cref="Dpi"/> is
    /// drawn at the highest resolution that fits instead, so a fold-out is read rather than refused
    /// and a corrupt MediaBox asking for gigabytes is never drawn at full size.
    /// </summary>
    public long MaxPixels { get; init; } = 120_000_000;
}

/// <summary>A rendered page bitmap plus the facts needed to map it back onto the PDF.</summary>
public sealed class RasterisedPage(int pageNumber, SKBitmap bitmap, int requestedDpi) : IDisposable
{
    public int PageNumber { get; } = pageNumber;
    public SKBitmap Bitmap { get; } = bitmap;
    public int RequestedDpi { get; } = requestedDpi;
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>Encodes the bitmap as PNG, which is the form the OCR engine accepts.</summary>
    public byte[] EncodePng()
    {
        using var data = Bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException($"Failed to encode page {PageNumber} as PNG.");
        return data.ToArray();
    }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Renders PDF pages to bitmaps with PDFium. PDFium applies /Rotate and crops to the crop box, so
/// the bitmap matches what a reader displays; <see cref="Geometry.PageGeometry"/> undoes both to
/// get back to content-stream coordinates.
/// </summary>
public class PageRasteriser(RasterOptions? options = null)
{
    private readonly RasterOptions _options = options ?? new RasterOptions();

    public RasterOptions Options => _options;

    public static int GetPageCount(string path)
    {
        using var stream = File.OpenRead(path);
        return Conversion.GetPageCount(stream);
    }

    /// <summary>Page sizes in points, as PDFium sees them: after rotation, crop box applied.</summary>
    public static IReadOnlyList<System.Drawing.SizeF> GetPageSizes(string path)
    {
        using var stream = File.OpenRead(path);
        return (IReadOnlyList<System.Drawing.SizeF>)Conversion.GetPageSizes(stream);
    }

    /// <param name="pageIndex">Zero-based page index.</param>
    /// <remarks>
    /// Virtual so a test can count what the pipeline actually asked for. The bound on rasterised
    /// pages waiting for the GPU is the property that keeps memory flat on a long manual, and a
    /// test that cannot see the rasteriser can only assert it indirectly - which is how the first
    /// version of that test came to pass against an unbounded queue.
    /// </remarks>
    public virtual RasterisedPage Render(byte[] pdfBytes, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        // A page too large to recognise at the resolution asked for - a 36-inch fold-out at 600 dpi
        // is 118 M pixels - is read at the highest resolution that fits, rather than refused. Word
        // positions follow the raster's real size, so the text still lands on the ink; only that
        // page is read at less than the rest. Sized before rendering, so a corrupt MediaBox asking
        // for gigabytes is never drawn at all.
        var dpi = DpiThatFits(pdfBytes, pageIndex);
        var bitmap = RenderAt(pdfBytes, pageIndex, dpi);

        var pixels = (long)bitmap.Width * bitmap.Height;
        if (pixels > _options.MaxPixels)
        {
            bitmap.Dispose();
            throw new PageUnreadableException(pageIndex + 1,
                $"Page {pageIndex + 1} rasterises to {pixels:N0} pixels at {dpi} dpi, over the " +
                $"{_options.MaxPixels:N0} pixel limit.");
        }

        return new RasterisedPage(pageIndex + 1, bitmap, _options.Dpi);
    }

    /// <summary>The requested resolution, or the highest below it at which the page fits the pixel limit.</summary>
    private int DpiThatFits(byte[] pdfBytes, int pageIndex)
    {
        System.Drawing.SizeF size;
        try
        {
            size = Conversion.GetPageSize(pdfBytes, pageIndex, password: null);
        }
        catch (Exception ex) when (ex is PDFtoImage.Exceptions.PdfInvalidFormatException
                                       or PDFtoImage.Exceptions.PdfPageNotFoundException)
        {
            throw new PageUnreadableException(pageIndex + 1, $"PDFium cannot read page {pageIndex + 1}: {ex.Message}", ex);
        }

        var pixels = size.Width / 72.0 * _options.Dpi * (size.Height / 72.0 * _options.Dpi);
        if (pixels <= _options.MaxPixels)
            return _options.Dpi;

        var fitting = (int)Math.Floor(_options.Dpi * Math.Sqrt(_options.MaxPixels / pixels) * 0.99);
        if (fitting < 1)
        {
            throw new PageUnreadableException(pageIndex + 1,
                $"Page {pageIndex + 1} measures {size.Width:F0} x {size.Height:F0} pt, too large to rasterise at any resolution.");
        }

        return fitting;
    }

    /// <summary>
    /// One render. A page PDFium cannot load or draw is unreadable for good: the same bytes give
    /// the same answer on every attempt.
    /// </summary>
    private SKBitmap RenderAt(byte[] pdfBytes, int pageIndex, int dpi)
    {
        var renderOptions = new RenderOptions(
            Dpi: dpi,
            WithAnnotations: _options.WithAnnotations,
            WithFormFill: _options.WithFormFill,
            Grayscale: _options.Grayscale);

        try
        {
            return Conversion.ToImage(pdfBytes, pageIndex, password: null, options: renderOptions)
                ?? throw new PageUnreadableException(pageIndex + 1, $"PDFium returned no bitmap for page {pageIndex + 1}.");
        }
        catch (Exception ex) when (ex is PDFtoImage.Exceptions.PdfInvalidFormatException
                                       or PDFtoImage.Exceptions.PdfPageNotFoundException)
        {
            throw new PageUnreadableException(pageIndex + 1, $"PDFium cannot read page {pageIndex + 1}: {ex.Message}", ex);
        }
    }
}
