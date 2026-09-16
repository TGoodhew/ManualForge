namespace ManualForge.Core.Geometry;

/// <summary>
/// Maps coordinates in a rasterised page image back into the PDF's default user space.
///
/// Three things make this non-trivial, and all three are common in scanned manuals:
///
/// 1. Image space has its origin at the top-left with Y growing down; PDF user space has its
///    origin at the bottom-left with Y growing up.
/// 2. A page may carry /Rotate 90, 180 or 270. The rasteriser (PDFium) renders the page as the
///    reader displays it, i.e. rotation already applied, but content stream coordinates are
///    always in the *unrotated* space. So a word that looks horizontal in the image may run
///    vertically in user space, and the text matrix has to rotate with it.
/// 3. The crop box need not start at (0,0). A page cropped to [36 36 612 756] puts the visible
///    top-left corner at user-space (36, 756), not (0, 792).
///
/// The mapping is derived rather than guessed: see the rotation cases in <see cref="ToUserSpace"/>.
/// </summary>
public sealed class PageGeometry
{
    /// <param name="cropLlx">Crop box lower-left X in unrotated user space (points).</param>
    /// <param name="cropLly">Crop box lower-left Y in unrotated user space (points).</param>
    /// <param name="cropWidth">Crop box width in points, before rotation.</param>
    /// <param name="cropHeight">Crop box height in points, before rotation.</param>
    /// <param name="rotation">/Rotate value; any multiple of 90, positive or negative.</param>
    /// <param name="pixelWidth">Width of the rendered image, which already reflects rotation.</param>
    /// <param name="pixelHeight">Height of the rendered image, which already reflects rotation.</param>
    public PageGeometry(
        double cropLlx,
        double cropLly,
        double cropWidth,
        double cropHeight,
        int rotation,
        int pixelWidth,
        int pixelHeight)
    {
        if (cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentException($"Crop box must have positive extent, got {cropWidth}x{cropHeight}.");
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentException($"Raster must have positive extent, got {pixelWidth}x{pixelHeight}.");

        CropLlx = cropLlx;
        CropLly = cropLly;
        CropWidth = cropWidth;
        CropHeight = cropHeight;
        Rotation = NormaliseRotation(rotation);
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public double CropLlx { get; }
    public double CropLly { get; }
    public double CropWidth { get; }
    public double CropHeight { get; }

    /// <summary>Page rotation normalised to 0, 90, 180 or 270.</summary>
    public int Rotation { get; }

    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public bool IsQuarterTurned => Rotation is 90 or 270;

    /// <summary>Width in points of the page as displayed, i.e. after rotation.</summary>
    public double VisualWidthPt => IsQuarterTurned ? CropHeight : CropWidth;

    /// <summary>Height in points of the page as displayed, i.e. after rotation.</summary>
    public double VisualHeightPt => IsQuarterTurned ? CropWidth : CropHeight;

    /// <summary>Points per image pixel across the displayed page width.</summary>
    public double PointsPerPixelX => VisualWidthPt / PixelWidth;

    /// <summary>Points per image pixel down the displayed page height.</summary>
    public double PointsPerPixelY => VisualHeightPt / PixelHeight;

    /// <summary>
    /// Effective render resolution implied by the raster size, horizontally. Used to sanity-check
    /// that the rasteriser honoured the DPI we asked for.
    /// </summary>
    public double EffectiveDpiX => 72.0 / PointsPerPixelX;

    public double EffectiveDpiY => 72.0 / PointsPerPixelY;

    public static int NormaliseRotation(int rotation)
    {
        var r = rotation % 360;
        if (r < 0) r += 360;
        // Round to the nearest quarter turn: /Rotate is defined as a multiple of 90, but corrupt
        // files do carry things like 89, and snapping beats throwing on a whole manual.
        return (int)(Math.Round(r / 90.0) * 90) % 360;
    }

    /// <summary>
    /// Maps a pixel in the rendered image to a point in unrotated PDF user space.
    /// <paramref name="px"/> grows right and <paramref name="py"/> grows down from the top-left.
    /// </summary>
    public PointD ToUserSpace(double px, double py)
    {
        // Step 1: image pixels -> "visual" points, still top-left origin, Y down.
        var vx = px * PointsPerPixelX;
        var vy = py * PointsPerPixelY;

        // Step 2: visual space -> unrotated crop-relative space, bottom-left origin, Y up.
        //
        // Writing (a, b) for a point measured from the unrotated top-left corner, so that
        // a = ux and b = CropHeight - uy, rotating the page R degrees clockwise for display maps
        // (a, b) to visual coordinates as follows:
        //
        //   R =   0: (vx, vy) = (a, b)
        //   R =  90: (vx, vy) = (h - b, a)          displayed size becomes h x w
        //   R = 180: (vx, vy) = (w - a, h - b)
        //   R = 270: (vx, vy) = (b, w - a)          displayed size becomes h x w
        //
        // Inverting each case and substituting back a = ux, b = h - uy gives:
        double ux, uy;
        switch (Rotation)
        {
            case 90:
                ux = vy;
                uy = vx;
                break;
            case 180:
                ux = CropWidth - vx;
                uy = vy;
                break;
            case 270:
                ux = CropWidth - vy;
                uy = CropHeight - vx;
                break;
            default: // 0
                ux = vx;
                uy = CropHeight - vy;
                break;
        }

        // Step 3: shift by the crop box origin to reach absolute user space.
        return new PointD(CropLlx + ux, CropLly + uy);
    }

    public PointD ToUserSpace(PointD imagePoint) => ToUserSpace(imagePoint.X, imagePoint.Y);

    /// <summary>
    /// Maps an image pixel into "display space": the page as the reader shows it, with rotation
    /// already applied, the origin at the visible bottom-left corner and Y growing upwards.
    ///
    /// This is not where the text is written — that is always unrotated user space — but it is the
    /// space text extractors normalise to, PdfPig included. Verification compares in this space
    /// because it is what a consumer of the finished PDF actually sees.
    /// </summary>
    public PointD ToDisplaySpace(double px, double py)
        => new(px * PointsPerPixelX, VisualHeightPt - py * PointsPerPixelY);

    public PointD ToDisplaySpace(PointD imagePoint) => ToDisplaySpace(imagePoint.X, imagePoint.Y);

    /// <summary>
    /// The rotation part of the text matrix for text that reads left-to-right in the image.
    /// Returned as the PDF matrix quadruple (a, b, c, d), which is a rotation by
    /// <see cref="Rotation"/> degrees counter-clockwise in user space.
    /// </summary>
    /// <remarks>
    /// For /Rotate 90 the image's "rightwards" direction maps to user-space +Y, so the invisible
    /// text has to run up the unrotated page in order to lie along the scanned glyphs once the
    /// reader applies the rotation.
    /// </remarks>
    public (double A, double B, double C, double D) TextRotationMatrix => Rotation switch
    {
        90 => (0, 1, -1, 0),
        180 => (-1, 0, 0, -1),
        270 => (0, -1, 1, 0),
        _ => (1, 0, 0, 1),
    };

    /// <summary>
    /// Converts a word box in image pixels into the placement of a single invisible text run:
    /// where its baseline starts in user space, how tall the glyphs should be, and how wide the
    /// run must end up.
    /// </summary>
    /// <param name="box">Word bounding box in image pixels, top-left origin.</param>
    /// <param name="baselineOffsetFraction">
    /// Fraction of the box height by which to drop the baseline below the bottom of the box, to
    /// account for descenders that the detector's ink box excluded. 0 puts the baseline exactly
    /// on the bottom edge.
    /// </param>
    public TextRunPlacement PlaceWordBox(RectD box, double baselineOffsetFraction = 0.0)
    {
        // The baseline starts at the bottom-left of the box *as seen in the image*, which is the
        // reading-order start regardless of page rotation because the box is expressed in image
        // space.
        var baselineStartPx = new PointD(box.Left, box.Bottom + box.Height * baselineOffsetFraction);
        var origin = ToUserSpace(baselineStartPx);

        // Height maps through the vertical pixel scale and width through the horizontal one.
        // Under a quarter turn the image's horizontal axis corresponds to the page's vertical
        // axis, but both scales come from the same DPI, so the run stays geometrically correct.
        var heightPt = box.Height * PointsPerPixelY;
        var widthPt = box.Width * PointsPerPixelX;

        var (a, b, c, d) = TextRotationMatrix;
        return new TextRunPlacement(origin, widthPt, heightPt, a, b, c, d);
    }
}

/// <summary>Everything needed to emit one invisible text run: where, how big, how wide, how turned.</summary>
public readonly record struct TextRunPlacement(
    PointD BaselineOrigin,
    double WidthPt,
    double FontSizePt,
    double A,
    double B,
    double C,
    double D);
