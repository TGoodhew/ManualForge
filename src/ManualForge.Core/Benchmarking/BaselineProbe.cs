using ManualForge.Core.Geometry;
using ManualForge.Core.Rendering;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManualForge.Core.Benchmarking;

/// <summary>Where one word's true baseline sits relative to the ink a detector would box.</summary>
/// <param name="Fraction">
/// As <c>TextLayerWriter</c> means it: the share of the ink box's height by which the baseline sits
/// <b>below</b> the bottom of the box. Zero for a word with no descender, whose ink stops at the
/// baseline; negative for a word with one, whose ink continues past it.
/// </param>
public sealed record BaselineSample(
    string ManualPath,
    int PageNumber,
    string Word,
    double Fraction,
    bool HasDescender,
    double InkHeightPx);

/// <summary>
/// Measures what <c>TextLayerOptions.BaselineOffsetFraction</c> should be, instead of guessing.
///
/// <para>
/// The recogniser returns a box round a word's ink. The text layer has to be written on the word's
/// <i>baseline</i>, and the two are not the same line: for <c>hello</c> the ink stops at the
/// baseline, while for <c>happy</c> it runs a fifth of the way past it. The setting is one constant
/// applied to every word, so the only honest way to choose it is to measure the distribution it is
/// approximating.
/// </para>
///
/// <para>
/// A born-digital page can answer this exactly and for free. Its content stream carries each word's
/// true baseline, and rendering it produces the same ink a detector would box — so for thousands of
/// real words, in the real typefaces of this corpus, the difference between the two is directly
/// observable. No recogniser is involved and no GPU: the question is geometry, not reading.
/// </para>
/// </summary>
public sealed class BaselineProbe
{
    /// <summary>Grey at or below which a pixel counts as ink, matching the audit's threshold.</summary>
    public byte InkLevel { get; init; } = 200;

    /// <summary>
    /// Ink shorter than this is skipped: at 300 dpi a lower-case letter is about 20 pixels tall, and
    /// anything under six is a speck, a rule, or part of a neighbouring line clipped by the box.
    /// </summary>
    public int MinimumInkHeightPx { get; init; } = 6;

    /// <summary>Characters that reach below the baseline in ordinary text faces.</summary>
    private const string Descenders = "gjpqy";

    /// <summary>
    /// Measures every usable word on one page.
    ///
    /// <para>
    /// Rotated pages are skipped rather than handled. The mapping between page and image space
    /// under a quarter turn is written down elsewhere and works, but a measurement whose job is to
    /// settle an argument should not also be the first user of a second coordinate convention.
    /// </para>
    /// </summary>
    public IReadOnlyList<BaselineSample> Measure(string path, int pageNumber, int dpi = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = File.ReadAllBytes(path);

        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return [];

        var page = document.GetPage(pageNumber);
        if (page.Rotation.Value % 360 != 0)
            return [];

        var rasteriser = new PageRasteriser(new RasterOptions { Dpi = dpi });
        using var raster = rasteriser.Render(bytes, pageNumber - 1);

        var geometry = new PageGeometry(
            Math.Min(page.CropBox.Bounds.Left, page.CropBox.Bounds.Right),
            Math.Min(page.CropBox.Bounds.Bottom, page.CropBox.Bounds.Top),
            Math.Abs(page.CropBox.Bounds.Width),
            Math.Abs(page.CropBox.Bounds.Height),
            0,
            raster.Width,
            raster.Height);

        var samples = new List<BaselineSample>();

        foreach (var word in page.GetWords())
        {
            var sample = Measure(word, raster.Bitmap, geometry, path, pageNumber);
            if (sample is not null)
                samples.Add(sample);
        }

        return samples;
    }

    private BaselineSample? Measure(
        Word word, SKBitmap bitmap, PageGeometry geometry, string path, int pageNumber)
    {
        var text = word.Text.Trim();
        if (text.Length < 2 || !text.Any(char.IsLetter))
            return null;

        // Words whose letters do not share a baseline are set on a curve or rotated inside an
        // otherwise upright page, and neither has a baseline this measurement can speak about.
        var baselines = word.Letters.Select(letter => letter.StartBaseLine.Y).ToArray();
        if (baselines.Length == 0 || baselines.Max() - baselines.Min() > 0.5)
            return null;

        var baselinePt = baselines.Average();
        var box = word.BoundingBox;

        var left = (int)Math.Floor(Math.Min(box.Left, box.Right) / geometry.PointsPerPixelX);
        var right = (int)Math.Ceiling(Math.Max(box.Left, box.Right) / geometry.PointsPerPixelX);
        var top = (int)Math.Floor((geometry.VisualHeightPt - Math.Max(box.Top, box.Bottom)) / geometry.PointsPerPixelY);
        var bottom = (int)Math.Ceiling((geometry.VisualHeightPt - Math.Min(box.Top, box.Bottom)) / geometry.PointsPerPixelY);

        left = Math.Clamp(left, 0, bitmap.Width - 1);
        right = Math.Clamp(right, 0, bitmap.Width - 1);
        top = Math.Clamp(top, 0, bitmap.Height - 1);
        bottom = Math.Clamp(bottom, 0, bitmap.Height - 1);

        if (right - left < 2 || bottom - top < MinimumInkHeightPx)
            return null;

        // The box comes from the font's own metrics and those are routinely wrong in this corpus -
        // a box that stops short of its own ascenders is documented in the detector. Clipped to it,
        // a descender's ink is invisible and every word measures as though it had none, which is
        // exactly the wrong answer to the question being asked. So the band is widened by half the
        // box on each side and the word's ink is then followed out of the box rather than cut off
        // at it: rows are taken while they keep having ink, and a blank row ends the word. A
        // neighbouring line cannot be reached across that gap.
        var margin = (int)Math.Ceiling((bottom - top) * 0.5);
        var searchTop = Math.Clamp(top - margin, 0, bitmap.Height - 1);
        var searchBottom = Math.Clamp(bottom + margin, 0, bitmap.Height - 1);

        var hasInk = new bool[searchBottom - searchTop + 1];
        for (var y = searchTop; y <= searchBottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var grey = (pixel.Red * 77 + pixel.Green * 151 + pixel.Blue * 28) >> 8;

                if (grey > InkLevel)
                    continue;

                hasInk[y - searchTop] = true;
                break;
            }
        }

        // Start from ink inside the metrics box, which is certainly this word's own.
        var seed = -1;
        for (var y = top; y <= bottom && seed < 0; y++)
        {
            if (hasInk[y - searchTop])
                seed = y;
        }

        if (seed < 0)
            return null;

        var inkTop = seed;
        while (inkTop - 1 >= searchTop && hasInk[inkTop - 1 - searchTop])
            inkTop--;

        var inkBottom = seed;
        while (inkBottom + 1 <= searchBottom && hasInk[inkBottom + 1 - searchTop])
            inkBottom++;

        if (inkBottom - inkTop < MinimumInkHeightPx)
            return null;

        var height = (double)(inkBottom - inkTop + 1);
        var baselinePx = (geometry.VisualHeightPt - baselinePt) / geometry.PointsPerPixelY;

        // The writer's convention: baseline = box.Bottom + height * fraction, with y increasing
        // downwards. A word whose ink stops at its baseline gives zero; a descender gives a
        // negative number, because its ink continues below the line the text should sit on.
        var fraction = (baselinePx - (inkBottom + 1)) / height;

        // Beyond this the box is not the word's own ink - a neighbouring line clipped in, or a
        // rule crossing the box - and including it would measure the clipping rather than the type.
        if (Math.Abs(fraction) > 0.6)
            return null;

        return new BaselineSample(
            path, pageNumber, text, fraction, text.Any(c => Descenders.Contains(c, StringComparison.Ordinal)), height);
    }

    /// <summary>
    /// The constant that costs the least, and what it costs — in points, which is the unit the
    /// error is actually felt in.
    /// </summary>
    public static BaselineRecommendation Recommend(IReadOnlyList<BaselineSample> samples, double pointsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
            return new BaselineRecommendation(0, 0, 0, 0, 0);

        double ErrorAt(double candidate) => samples
            .Average(s => Math.Abs(s.Fraction - candidate) * s.InkHeightPx * pointsPerPixel);

        var best = 0.0;
        var bestError = ErrorAt(0.0);

        // A hundredth of a box height is far finer than anything that matters; the sweep is cheap
        // and stating the minimum beats arguing about it.
        for (var candidate = -0.40; candidate <= 0.20001; candidate += 0.01)
        {
            var error = ErrorAt(candidate);
            if (error >= bestError)
                continue;

            bestError = error;
            best = candidate;
        }

        return new BaselineRecommendation(
            Math.Round(best, 2),
            bestError,
            ErrorAt(0.0),
            samples.Average(s => s.Fraction),
            samples.Count);
    }
}

/// <summary>What the measurement concluded.</summary>
public sealed record BaselineRecommendation(
    double Fraction,
    double MeanErrorPt,
    double MeanErrorAtZeroPt,
    double MeanFraction,
    int Samples);
