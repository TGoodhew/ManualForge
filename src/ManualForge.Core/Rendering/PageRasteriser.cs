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
    /// Refuse to rasterise a page that would exceed this many pixels. A corrupt MediaBox can ask
    /// for a bitmap of many gigabytes, and the failure should be a clean skip, not an OOM.
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

        var renderOptions = new RenderOptions(
            Dpi: _options.Dpi,
            WithAnnotations: _options.WithAnnotations,
            WithFormFill: _options.WithFormFill,
            Grayscale: _options.Grayscale);

        var bitmap = Conversion.ToImage(pdfBytes, pageIndex, password: null, options: renderOptions)
            ?? throw new InvalidOperationException($"PDFium returned no bitmap for page {pageIndex + 1}.");

        var pixels = (long)bitmap.Width * bitmap.Height;
        if (pixels > _options.MaxPixels)
        {
            bitmap.Dispose();
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} rasterises to {bitmap.Width}x{bitmap.Height} " +
                $"({pixels:N0} pixels) at {_options.Dpi} dpi, over the {_options.MaxPixels:N0} pixel limit.");
        }

        return new RasterisedPage(pageIndex + 1, bitmap, _options.Dpi);
    }
}
