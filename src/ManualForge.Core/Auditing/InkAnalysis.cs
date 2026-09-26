using ManualForge.Core.Geometry;
using SkiaSharp;

namespace ManualForge.Core.Auditing;

/// <summary>What a rendered page looks like once the extracted text is subtracted from it.</summary>
/// <param name="InkFraction">Share of the page's pixels that carry any mark at all.</param>
/// <param name="UncoveredInkFraction">
/// Share of the page's pixels carrying a mark that no extracted glyph accounts for.
/// </param>
/// <param name="GlyphLikeBlobs">
/// Clusters of that unaccounted-for ink which are the size, shape and density of lettering.
/// </param>
/// <param name="LargestBlobHeightPt">
/// Height of the tallest glyph-like blob, in points. A document whose blobs are all 4 pt is
/// annotation set in agate and wants a higher OCR resolution than one whose blobs are 10 pt.
/// </param>
public sealed record InkAnalysis(
    double InkFraction,
    double UncoveredInkFraction,
    int GlyphLikeBlobs,
    double LargestBlobHeightPt)
{
    public static readonly InkAnalysis NotRendered = new(-1, -1, 0, 0);

    public bool WasRendered => InkFraction >= 0;
}

/// <summary>
/// Measures the ink on a rendered page against the text a reader can extract from it.
///
/// <para>
/// This is the signal that catches the case the whole audit exists for. A page whose figures are
/// drawn as vector graphics extracts as a heading and nothing else, and no amount of looking at the
/// text alone can tell that apart from a page that really is nearly empty. Rendering it settles the
/// question: a sparse page has little ink, and a page whose content did not extract has plenty.
/// </para>
///
/// <para>
/// Ink alone would be too blunt. A schematic, an exploded parts diagram or a full-page waveform is
/// mostly ink and mostly has nothing to recover, and flagging all of those would trigger a
/// hundred-thousand-page re-OCR and teach everybody to ignore the report. So the ink that no glyph
/// accounts for is then sorted by shape, and only clusters the size and density of lettering count.
/// </para>
/// </summary>
public static class InkAnalyser
{
    /// <summary>
    /// Compares a rendered page against the glyph boxes extracted from it.
    /// </summary>
    /// <param name="bitmap">The rendered page, as the reader displays it: rotation applied.</param>
    /// <param name="geometry">Maps between that image and the page's own coordinates.</param>
    /// <param name="textBoxesDisplayPt">
    /// Extracted glyph rectangles in display space — origin at the visible bottom-left corner, Y
    /// upwards — which is what PdfPig reports and what <see cref="PageGeometry.ToDisplaySpace"/>
    /// produces.
    /// </param>
    public static InkAnalysis Analyse(
        SKBitmap bitmap,
        PageGeometry geometry,
        IReadOnlyList<RectD> textBoxesDisplayPt,
        DoctorOptions options)
        => Analyse(bitmap, geometry, textBoxesDisplayPt, options, out _);

    /// <param name="blobs">
    /// Where each glyph-like cluster was found, in image pixels. Kept so the diagnostic image can
    /// show its working: a detector whose reasoning cannot be looked at is one nobody will trust
    /// enough to act on.
    /// </param>
    public static InkAnalysis Analyse(
        SKBitmap bitmap,
        PageGeometry geometry,
        IReadOnlyList<RectD> textBoxesDisplayPt,
        DoctorOptions options,
        out IReadOnlyList<RectD> blobs)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(textBoxesDisplayPt);
        ArgumentNullException.ThrowIfNull(options);

        blobs = [];

        var width = bitmap.Width;
        var height = bitmap.Height;
        var total = (long)width * height;
        if (total == 0)
            return InkAnalysis.NotRendered;

        // 0 = blank, 1 = ink with no glyph over it, 2 = ink a glyph accounts for. Held as bytes
        // rather than bits because the flood fill below rewrites it in place.
        var mask = new byte[total];

        var ink = 0L;
        Grey(bitmap, options.InkLevel, mask, width, height, ref ink);
        if (ink == 0)
            return new InkAnalysis(0, 0, 0, 0);

        var covered = MarkCovered(mask, width, height, geometry, textBoxesDisplayPt, options.TextBoxPaddingPt);

        var uncovered = ink - covered;
        if (uncovered <= 0)
            return new InkAnalysis(ink / (double)total, 0, 0, 0);

        var found = CountGlyphLikeBlobs(mask, width, height, geometry, options);
        blobs = found;

        return new InkAnalysis(
            ink / (double)total,
            uncovered / (double)total,
            found.Count,
            found.Count == 0 ? 0 : found.Max(b => b.Height) * geometry.PointsPerPixelY);
    }

    /// <summary>
    /// Renders what the analysis saw: covered ink in pale grey, ink no glyph accounts for in
    /// black, and a box round every cluster that was counted as lettering.
    /// </summary>
    public static SKBitmap Diagnose(
        SKBitmap bitmap,
        PageGeometry geometry,
        IReadOnlyList<RectD> textBoxesDisplayPt,
        DoctorOptions options)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var mask = new byte[(long)width * height];

        var ink = 0L;
        Grey(bitmap, options.InkLevel, mask, width, height, ref ink);
        MarkCovered(mask, width, height, geometry, textBoxesDisplayPt, options.TextBoxPaddingPt);

        // Copied before the flood fill consumes it.
        var uncovered = (byte[])mask.Clone();
        var blobs = CountGlyphLikeBlobs(mask, width, height, geometry, options);

        var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.White);

            using var covered = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xC8) };
            using var missed = new SKPaint { Color = SKColors.Black };

            for (var y = 0; y < height; y++)
            {
                var offset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var value = uncovered[offset + x];
                    if (value == 0)
                        continue;

                    canvas.DrawPoint(x, y, value == 2 ? covered : missed);
                }
            }

            using var outline = new SKPaint
            {
                Color = new SKColor(0xD0, 0x00, 0x00),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };

            foreach (var blob in blobs)
            {
                canvas.DrawRect(
                    (float)blob.Left - 1, (float)blob.Top - 1,
                    (float)blob.Width + 2, (float)blob.Height + 2,
                    outline);
            }
        }

        return output;
    }

    /// <summary>Fills the mask with 1 wherever the page carries a mark.</summary>
    private static void Grey(SKBitmap bitmap, byte inkLevel, byte[] mask, int width, int height, ref long ink)
    {
        // A copy to Gray8 is one pass in native code and makes the read below a flat byte scan.
        // Some colour types will not convert; falling back to GetPixel is slow but never wrong.
        using var grey = bitmap.ColorType == SKColorType.Gray8 ? null : bitmap.Copy(SKColorType.Gray8);
        var source = grey ?? bitmap;

        var pixels = source.GetPixelSpan();
        if (pixels.Length >= (long)source.RowBytes * height && source.ColorType == SKColorType.Gray8)
        {
            var stride = source.RowBytes;
            var count = 0L;
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * stride, width);
                var offset = y * width;
                for (var x = 0; x < width; x++)
                {
                    if (row[x] <= inkLevel)
                    {
                        mask[offset + x] = 1;
                        count++;
                    }
                }
            }

            ink = count;
            return;
        }

        var slow = 0L;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var colour = bitmap.GetPixel(x, y);
                // Rec. 601 luma, which is what a greyscale conversion would have produced anyway.
                var luma = (byte)((colour.Red * 299 + colour.Green * 587 + colour.Blue * 114) / 1000);
                if (luma > inkLevel)
                    continue;

                mask[y * width + x] = 1;
                slow++;
            }
        }

        ink = slow;
    }

    /// <summary>
    /// Promotes ink under an extracted glyph box from 1 to 2, and returns how much was promoted.
    /// </summary>
    private static long MarkCovered(
        byte[] mask,
        int width,
        int height,
        PageGeometry geometry,
        IReadOnlyList<RectD> boxes,
        double paddingPt)
    {
        var covered = 0L;

        foreach (var box in boxes)
        {
            // Display space is Y-up from the visible bottom-left corner; image space is Y-down from
            // the top-left. So the box's top edge is the smaller image Y.
            var left = (box.Left - paddingPt) / geometry.PointsPerPixelX;
            var right = (box.Right + paddingPt) / geometry.PointsPerPixelX;
            var top = (geometry.VisualHeightPt - box.Bottom - paddingPt) / geometry.PointsPerPixelY;
            var bottom = (geometry.VisualHeightPt - box.Top + paddingPt) / geometry.PointsPerPixelY;

            var x0 = Math.Max(0, (int)Math.Floor(Math.Min(left, right)));
            var x1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(left, right)));
            var y0 = Math.Max(0, (int)Math.Floor(Math.Min(top, bottom)));
            var y1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(top, bottom)));

            for (var y = y0; y <= y1; y++)
            {
                var offset = y * width;
                for (var x = x0; x <= x1; x++)
                {
                    if (mask[offset + x] != 1)
                        continue;

                    mask[offset + x] = 2;
                    covered++;
                }
            }
        }

        return covered;
    }

    /// <summary>
    /// Counts the clusters of unaccounted-for ink that look like lettering.
    ///
    /// <para>
    /// Eight-connected flood fill, consuming the mask as it goes. Shape is judged on the cluster's
    /// bounding box and how much of it the cluster fills, which is enough to separate a character
    /// from a connector line, an arrowhead run, a rounded bubble or a table rule without needing to
    /// recognise anything.
    /// </para>
    /// </summary>
    private static List<RectD> CountGlyphLikeBlobs(
        byte[] mask, int width, int height, PageGeometry geometry, DoctorOptions options)
    {
        var minHeightPx = options.MinimumBlobHeightPt / geometry.PointsPerPixelY;
        var maxHeightPx = options.MaximumBlobHeightPt / geometry.PointsPerPixelY;
        var maxWidthPx = options.MaximumBlobWidthPt / geometry.PointsPerPixelX;

        var blobs = new List<RectD>();
        var blocks = new List<Extent>();
        var stack = new Stack<int>(256);

        for (var seed = 0; seed < mask.Length; seed++)
        {
            if (mask[seed] != 1)
                continue;

            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var minY = int.MaxValue;
            var maxY = int.MinValue;
            var pixels = 0;

            stack.Push(seed);
            mask[seed] = 3;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var y = index / width;
                var x = index - y * width;

                pixels++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                // Abandoning a cluster that has already outgrown any letter saves walking the
                // whole border of a full-page box, which on a schematic is most of the ink.
                if (maxX - minX > maxWidthPx * 2 && maxY - minY > maxHeightPx * 2)
                {
                    // Back on the stack so the drain expands this pixel's neighbours too. Leaving
                    // them unvisited would let the rest of the shape be re-seeded as a crowd of
                    // small clusters, which is exactly what this rule is meant to avoid counting.
                    stack.Push(index);

                    var extent = options.ReverseVideo ? new Extent() : null;
                    extent?.Add(minX, minY);
                    extent?.Add(maxX, maxY);
                    Drain(mask, stack, width, height, extent);

                    if (extent is not null)
                        blocks.Add(extent);

                    pixels = -1;
                    break;
                }

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width || (dx == 0 && dy == 0))
                            continue;

                        var neighbour = ny * width + nx;
                        if (mask[neighbour] != 1)
                            continue;

                        mask[neighbour] = 3;
                        stack.Push(neighbour);
                    }
                }
            }

            if (pixels < 0)
                continue;

            var blobWidth = maxX - minX + 1;
            var blobHeight = maxY - minY + 1;

            // Anything solid and big enough to carry a word is worth looking inside, whatever the
            // letter filter goes on to decide about it. The first attempt asked only about clusters
            // *too big to be letters*, and missed the case this was written for: the AVCHD badge on
            // a camcorder page is 29 by 11 points, which is large for a letter and comfortably
            // small enough to pass as one.
            if (options.ReverseVideo)
                blocks.Add(Extent.Of(minX, minY, maxX, maxY, pixels));

            if (blobHeight < minHeightPx || blobHeight > maxHeightPx || blobWidth > maxWidthPx)
                continue;

            var aspect = blobWidth / (double)blobHeight;
            if (aspect > options.MaximumBlobAspect || aspect < 1.0 / options.MaximumBlobAspect)
                continue;

            if (pixels / (double)(blobWidth * blobHeight) < options.MinimumBlobFill)
                continue;

            blobs.Add(new RectD(minX, minY, blobWidth, blobHeight));
        }

        var kept = DropRuleSegments(blobs, geometry, options);

        if (options.ReverseVideo && blocks.Count > 0)
            kept.AddRange(LetteringInsideBlocks(mask, width, height, geometry, options, blocks));

        return kept;
    }

    /// <summary>
    /// Finds lettering printed <i>through</i> a block of ink rather than with it.
    ///
    /// <para>
    /// White text on a black button is the one shape this detector is structurally blind to: the
    /// letters are an absence of ink inside a block of it, so a filter looking for marks finds the
    /// block and nothing else. After the render gate was fixed this became the leading known cause
    /// of missed pages — see issue #12.
    /// </para>
    ///
    /// <para>
    /// The blocks arrive already measured, because the walk above has to drain them anyway. Two
    /// conditions decide which are worth looking inside, and both matter: the block must be
    /// <b>solid</b>, since a page border or a table outline is also a huge cluster and its "inside"
    /// is the whole page; and it must be <b>ink nothing accounts for</b>, which it is by
    /// construction — the walk only ever traverses uncovered ink, so a block whose label the text
    /// layer already holds is never offered here at all.
    /// </para>
    /// </summary>
    private static List<RectD> LetteringInsideBlocks(
        byte[] mask,
        int width,
        int height,
        PageGeometry geometry,
        DoctorOptions options,
        List<Extent> blocks)
    {
        var found = new List<RectD>();

        var minimumArea = options.ReverseVideoMinimumAreaPt
                          / (geometry.PointsPerPixelX * geometry.PointsPerPixelY);
        var maximumArea = (long)width * height * options.ReverseVideoMaximumPageShare;

        var probe = Environment.GetEnvironmentVariable("MANUALFORGE_REVERSE_VIDEO_PROBE") == "1";
        var examined = 0;

        foreach (var block in blocks)
        {
            var area = (long)block.Width * block.Height;

            if (probe)
            {
                var why = area < minimumArea ? "too small"
                    : area > maximumArea ? "too large"
                    : block.Fill < options.ReverseVideoMinimumFill ? "not solid"
                    : "examined";

                Console.Error.WriteLine(
                    $"    cluster {block.Width,4}x{block.Height,-4} fill {block.Fill,5:P0}  {why}");
            }

            if (area < minimumArea || area > maximumArea)
                continue;

            if (block.Fill < options.ReverseVideoMinimumFill)
                continue;

            examined++;
            var holes = HolesIn(mask, width, geometry, options, block);

            if (probe && holes.Count > 0)
            {
                Console.Error.WriteLine(
                    $"  block {block.Width}x{block.Height}px fill {block.Fill:P0} -> {holes.Count} hole(s)");
            }

            found.AddRange(holes);
        }

        if (probe)
        {
            Console.Error.WriteLine(
                $"  reverse video: {blocks.Count} cluster(s), {examined} examined, {found.Count} hole(s); " +
                $"area {minimumArea:N0}-{maximumArea:N0} px²");
        }

        return found;
    }

    /// <summary>
    /// Glyph-shaped holes enclosed by one block of ink.
    ///
    /// <para>
    /// "Enclosed" is the load-bearing word, and it is why this floods inwards from the edge rather
    /// than simply counting light pixels. A block is rarely a perfect rectangle, so the corners of
    /// its bounding box are page background; counting those would find one enormous hole and, worse,
    /// would let a block that merely touches the margin swallow the entire page.
    /// </para>
    /// </summary>
    private static List<RectD> HolesIn(
        byte[] mask, int width, PageGeometry geometry, DoctorOptions options, Extent block)
    {
        var w = block.Width;
        var h = block.Height;

        // 0 = a hole not yet accounted for, 1 = ink, 2 = background reached from outside.
        var local = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                local[y * w + x] = mask[(block.MinY + y) * width + block.MinX + x] == 0 ? (byte)0 : (byte)1;
        }

        var queue = new Queue<int>();
        for (var x = 0; x < w; x++)
        {
            Seed(local, queue, x, w);
            Seed(local, queue, (h - 1) * w + x, w);
        }

        for (var y = 0; y < h; y++)
        {
            Seed(local, queue, y * w, w);
            Seed(local, queue, y * w + w - 1, w);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var y = index / w;
            var x = index - y * w;

            if (x > 0) Seed(local, queue, index - 1, w);
            if (x < w - 1) Seed(local, queue, index + 1, w);
            if (y > 0) Seed(local, queue, index - w, w);
            if (y < h - 1) Seed(local, queue, index + w, w);
        }

        // What is left at 0 is enclosed, and is measured with the same filter as ordinary
        // lettering. Whatever those thresholds are worth they are worth the same here, and a second
        // set of numbers to defend would be worse than the blind spot.
        return Clusters(local, w, h, geometry, options)
            .Select(r => new RectD(block.MinX + r.Left, block.MinY + r.Top, r.Width, r.Height))
            .ToList();
    }

    private static void Seed(byte[] local, Queue<int> queue, int index, int width)
    {
        if (local[index] != 0)
            return;

        local[index] = 2;
        queue.Enqueue(index);
    }

    /// <summary>
    /// Connected runs of <c>0</c> in a small grid, kept only if they are the size, shape and
    /// density of a character. The same thresholds the page-wide filter uses, deliberately.
    /// </summary>
    private static List<RectD> Clusters(
        byte[] cells, int width, int height, PageGeometry geometry, DoctorOptions options)
    {
        var minHeightPx = options.MinimumBlobHeightPt / geometry.PointsPerPixelY;
        var maxHeightPx = options.MaximumBlobHeightPt / geometry.PointsPerPixelY;
        var maxWidthPx = options.MaximumBlobWidthPt / geometry.PointsPerPixelX;

        var found = new List<RectD>();
        var stack = new Stack<int>(128);

        for (var seed = 0; seed < cells.Length; seed++)
        {
            if (cells[seed] != 0)
                continue;

            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var minY = int.MaxValue;
            var maxY = int.MinValue;
            var pixels = 0;

            stack.Push(seed);
            cells[seed] = 3;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var y = index / width;
                var x = index - y * width;

                pixels++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width || (dx == 0 && dy == 0))
                            continue;

                        var neighbour = ny * width + nx;
                        if (cells[neighbour] != 0)
                            continue;

                        cells[neighbour] = 3;
                        stack.Push(neighbour);
                    }
                }
            }

            var blobWidth = maxX - minX + 1;
            var blobHeight = maxY - minY + 1;

            if (blobHeight < minHeightPx || blobHeight > maxHeightPx || blobWidth > maxWidthPx)
                continue;

            var aspect = blobWidth / (double)blobHeight;
            if (aspect > options.MaximumBlobAspect || aspect < 1.0 / options.MaximumBlobAspect)
                continue;

            if (pixels / (double)(blobWidth * blobHeight) < options.MinimumBlobFill)
                continue;

            found.Add(new RectD(minX, minY, blobWidth, blobHeight));
        }

        return found;
    }

    /// <summary>
    /// Removes blobs that are fragments of a ruled line rather than characters.
    ///
    /// <para>
    /// Shape cannot separate the two: a piece of column rule between two table rows is a few pixels
    /// each way, solid, and aspect near one, which is also a description of the letter o. Position
    /// separates them. A rule broken by its own intersections leaves fragments of identical width
    /// at one x, repeated down the page; lettering does not do that, because glyphs differ in width
    /// even when they are left-aligned in a column.
    /// </para>
    /// </summary>
    private static List<RectD> DropRuleSegments(
        List<RectD> blobs, PageGeometry geometry, DoctorOptions options)
    {
        if (options.RuleSegmentRun <= 0 || blobs.Count < options.RuleSegmentRun)
            return blobs;

        var tolerance = Math.Max(1.0, options.RuleSegmentTolerancePt / geometry.PointsPerPixelX);
        var maximumWidth = options.RuleSegmentMaximumWidthPt / geometry.PointsPerPixelX;
        var dropped = new HashSet<int>();

        // Two conditions, and both are needed. Alignment alone would take a column of left-aligned
        // text with it; narrowness alone would take the thin strokes of ordinary lettering. A blob
        // that is hairline-thin *and* one of several identical ones down a single x is a rule.
        var groups = blobs
            .Select((blob, index) => (blob, index))
            .Where(b => b.blob.Width <= maximumWidth)
            .GroupBy(b => (
                Left: (int)Math.Round(b.blob.Left / tolerance),
                Width: (int)Math.Round(b.blob.Width / tolerance)));

        foreach (var group in groups)
        {
            if (group.Count() < options.RuleSegmentRun)
                continue;

            foreach (var (_, index) in group)
                dropped.Add(index);
        }

        if (dropped.Count == 0)
            return blobs;

        var kept = new List<RectD>(blobs.Count - dropped.Count);
        for (var i = 0; i < blobs.Count; i++)
        {
            if (!dropped.Contains(i))
                kept.Add(blobs[i]);
        }

        return kept;
    }

    /// <summary>A cluster's bounding box and how solidly it fills it, accumulated pixel by pixel.</summary>
    private sealed class Extent
    {
        public int MinX { get; private set; } = int.MaxValue;

        public int MaxX { get; private set; } = int.MinValue;

        public int MinY { get; private set; } = int.MaxValue;

        public int MaxY { get; private set; } = int.MinValue;

        public long Pixels { get; private set; }

        public int Width => MaxX - MinX + 1;

        public int Height => MaxY - MinY + 1;

        /// <summary>How much of the bounding box is ink. A solid block is near 1; an outline is not.</summary>
        public double Fill => Width <= 0 || Height <= 0 ? 0 : Pixels / (double)((long)Width * Height);

        public void Add(int x, int y)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
            Pixels++;
        }

        /// <summary>A cluster the walk already measured, rather than one being accumulated.</summary>
        public static Extent Of(int minX, int minY, int maxX, int maxY, long pixels)
        {
            var extent = new Extent();
            extent.Add(minX, minY);
            extent.Add(maxX, maxY);
            extent.Pixels = pixels;
            return extent;
        }
    }

    /// <summary>
    /// Finishes consuming a cluster already known to be too big to be a letter, optionally
    /// measuring it on the way through.
    ///
    /// <para>
    /// A cluster too big to be a letter is not automatically uninteresting: a solid block of ink is
    /// exactly what white lettering is printed on, and the only way to look inside one is to know
    /// where it is. Measuring costs nothing extra, because the drain visits every pixel either way.
    /// </para>
    /// </summary>
    private static void Drain(byte[] mask, Stack<int> stack, int width, int height, Extent? extent = null)
    {
        while (stack.Count > 0)
        {
            var index = stack.Pop();
            var y = index / width;
            var x = index - y * width;

            extent?.Add(x, y);

            for (var dy = -1; dy <= 1; dy++)
            {
                var ny = y + dy;
                if (ny < 0 || ny >= height)
                    continue;

                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    if (nx < 0 || nx >= width || (dx == 0 && dy == 0))
                        continue;

                    var neighbour = ny * width + nx;
                    if (mask[neighbour] != 1)
                        continue;

                    mask[neighbour] = 3;
                    stack.Push(neighbour);
                }
            }
        }
    }
}
