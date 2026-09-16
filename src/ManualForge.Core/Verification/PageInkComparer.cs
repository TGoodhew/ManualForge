using ManualForge.Core.Rendering;
using SkiaSharp;

namespace ManualForge.Core.Verification;

public sealed record InkComparison(int PageNumber, long DifferingPixels, long TotalPixels, int MaxChannelDelta)
{
    public bool IsIdentical => DifferingPixels == 0;

    public double DifferingFraction => TotalPixels == 0 ? 0 : DifferingPixels / (double)TotalPixels;
}

/// <summary>
/// Renders the same page from the source and from the output and compares them pixel for pixel.
///
/// This is the direct test of the central promise: the page image is untouched and the text layer
/// adds no ink. Checking that the text is invisible by reading the content stream would only prove
/// that the right operator was written; rendering proves that the operator had the intended effect,
/// and catches the cases that matter — a stray graphics state left by the original content, a
/// rendering mode a viewer treats differently, a font that turned out not to be blank after all.
/// </summary>
public static class PageInkComparer
{
    /// <param name="dpi">
    /// Comparison resolution. Lower than the OCR resolution is fine and much faster: ink that is
    /// invisible at 300 dpi cannot appear at 150.
    /// </param>
    public static InkComparison ComparePage(byte[] sourcePdf, byte[] outputPdf, int pageIndex, int dpi = 150)
    {
        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = dpi, Grayscale = false });

        using var before = rasteriser.Render(sourcePdf, pageIndex);
        using var after = rasteriser.Render(outputPdf, pageIndex);

        if (before.Width != after.Width || before.Height != after.Height)
        {
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} changed size: {before.Width}x{before.Height} became {after.Width}x{after.Height}.");
        }

        return Compare(pageIndex + 1, before.Bitmap, after.Bitmap);
    }

    private static InkComparison Compare(int pageNumber, SKBitmap before, SKBitmap after)
    {
        var beforePixels = before.Pixels;
        var afterPixels = after.Pixels;

        long differing = 0;
        var maxDelta = 0;

        for (var i = 0; i < beforePixels.Length; i++)
        {
            var a = beforePixels[i];
            var b = afterPixels[i];
            if (a == b)
                continue;

            differing++;
            maxDelta = Math.Max(maxDelta, Math.Max(
                Math.Abs(a.Red - b.Red),
                Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))));
        }

        return new InkComparison(pageNumber, differing, beforePixels.Length, maxDelta);
    }
}
