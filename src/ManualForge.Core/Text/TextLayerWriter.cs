using System.Globalization;
using System.Text;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ManualForge.Core.Text;

public sealed class TextLayerOptions
{
    /// <summary>
    /// Fraction of a word box's height by which the baseline is dropped below the box. Negative
    /// lifts it, which is what the measurement says it needs.
    ///
    /// <para>
    /// This was 0.0 with a comment guessing that "a small positive value helps when the corpus is
    /// mostly mixed case". The guess was wrong in its sign. Measured over 2,925 words of born-digital
    /// type, whose true baselines the page itself records: a word with a descender sits
    /// <b>-0.24</b> of its ink height above the bottom of that ink, and a word without one still
    /// sits at <b>-0.037</b>, because round letters are drawn fractionally below the baseline so
    /// that they look aligned. Both numbers hold at 200, 300 and 400 dpi, which is how you can tell
    /// they are typography rather than sampling.
    /// </para>
    /// <para>
    /// One constant has to serve both populations, and -0.04 is the one that costs least: mean
    /// baseline error falls from 0.86 pt to 0.66 pt, about a quarter. `manualforge baselines`
    /// re-measures it, and `docs/measurements/baseline-offset.md` has the working.
    /// </para>
    /// </summary>
    public double BaselineOffsetFraction { get; init; } = DefaultBaselineOffsetFraction;

    /// <summary>
    /// The same number as a constant, so that the verifier's default cannot drift away from the
    /// writer's. They were 0.0 in two places and stayed in step by luck; a measured value has no
    /// such excuse, and a verifier checking against a different intention from the writer's would
    /// report the difference as error.
    /// </summary>
    public const double DefaultBaselineOffsetFraction = -0.04;

    /// <summary>Words recognised below this confidence are left out of the text layer.</summary>
    public double MinimumConfidence { get; init; } = 0.30;

    /// <summary>
    /// Bounds on the horizontal scaling factor. A box whose aspect ratio implies a scale outside
    /// this range is almost always a detection artefact, and emitting it would smear one word
    /// across the page.
    /// </summary>
    public double MinHorizontalScale { get; init; } = 5.0;

    public double MaxHorizontalScale { get; init; } = 2000.0;

    /// <summary>Word boxes shorter than this many points are dropped as noise.</summary>
    public double MinFontSizePt { get; init; } = 1.0;
}

/// <summary>
/// What the writer put on the page. <see cref="Written"/> lists the words that actually reached the
/// content stream, in the order they were emitted — words can be dropped for low confidence or an
/// implausible box, and verification has to compare against what was written rather than against
/// what was recognised, or it walks out of step with the extracted letters.
/// </summary>
public sealed record TextLayerPageResult(
    IReadOnlyList<RecognisedWord> Written,
    int WordsSkipped,
    int BytesWritten)
{
    public int WordsWritten => Written.Count;
}

/// <summary>
/// Writes the invisible OCR text layer onto an existing page, leaving the page's own content —
/// crucially, the scanned image — byte-for-byte untouched.
///
/// Each word becomes one text-showing operation positioned by its own text matrix. The font size
/// comes from the height of the detected box and the horizontal scaling operator (Tz) stretches
/// the run to exactly the box's width. That last step is what makes selection land on the right
/// glyphs: without it the invisible text keeps the font's natural advance widths and drifts
/// further from the scan with every character.
/// </summary>
public sealed class TextLayerWriter(TextLayerOptions? options = null)
{
    private readonly TextLayerOptions _options = options ?? new TextLayerOptions();

    public TextLayerOptions Options => _options;

    public TextLayerPageResult WritePage(
        PdfPage page,
        PageGeometry geometry,
        IEnumerable<RecognisedWord> words,
        InvisibleFont font)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(font);

        var resourceName = EnsureFontResource(page, font);

        var content = new StringBuilder();
        var written = new List<RecognisedWord>();
        var skipped = 0;
        var lastFontSize = double.NaN;
        var lastScale = double.NaN;

        content.Append("q\n");
        content.Append("BT\n");
        // Text rendering mode 3: neither filled nor stroked, i.e. invisible but still selectable
        // and searchable. Set once for the whole block; text state survives across Tj operations.
        content.Append("3 Tr\n");

        foreach (var word in words)
        {
            if (!word.IsUsable || word.Confidence < _options.MinimumConfidence)
            {
                skipped++;
                continue;
            }

            var placement = geometry.PlaceWordBox(word.BoxPx, _options.BaselineOffsetFraction);
            if (placement.FontSizePt < _options.MinFontSizePt || placement.WidthPt <= 0)
            {
                skipped++;
                continue;
            }

            var hex = font.EncodeToHex(word.Text);
            if (hex is null)
            {
                skipped++;
                continue;
            }

            var glyphs = InvisibleFont.GlyphCountOfHex(hex);

            // Every glyph in the invisible font advances by exactly half an em, so the run's
            // natural width is known in closed form and the scale needed to reach the detected
            // box width is a straight ratio. Tz is expressed as a percentage.
            var naturalWidth = placement.FontSizePt * glyphs
                * (GlyphlessTrueTypeFont.AdvanceWidth / (double)GlyphlessTrueTypeFont.UnitsPerEm);
            if (naturalWidth <= 0)
            {
                skipped++;
                continue;
            }

            var scale = 100.0 * placement.WidthPt / naturalWidth;
            if (scale < _options.MinHorizontalScale || scale > _options.MaxHorizontalScale)
            {
                skipped++;
                continue;
            }

            if (!NearlyEqual(placement.FontSizePt, lastFontSize))
            {
                content.Append(CultureInfo.InvariantCulture, $"{resourceName} {F(placement.FontSizePt)} Tf\n");
                lastFontSize = placement.FontSizePt;
            }

            if (!NearlyEqual(scale, lastScale))
            {
                content.Append(CultureInfo.InvariantCulture, $"{F(scale)} Tz\n");
                lastScale = scale;
            }

            content.Append(CultureInfo.InvariantCulture,
                $"{F(placement.A)} {F(placement.B)} {F(placement.C)} {F(placement.D)} " +
                $"{F(placement.BaselineOrigin.X)} {F(placement.BaselineOrigin.Y)} Tm\n");
            content.Append(CultureInfo.InvariantCulture, $"<{hex}> Tj\n");
            written.Add(word);
        }

        content.Append("ET\n");
        content.Append("Q\n");

        if (written.Count == 0)
            return new TextLayerPageResult([], skipped, 0);

        var bytes = Encoding.ASCII.GetBytes(content.ToString());
        AppendContentStream(page, bytes);
        return new TextLayerPageResult(written, skipped, bytes.Length);
    }

    /// <summary>
    /// Appends a content stream to the page, first wrapping whatever was already there in q/Q.
    /// Scanned pages routinely leave the graphics state modified — an unbalanced q, or a CTM set
    /// for the page image and never restored — and without the wrapper our text would inherit it
    /// and land somewhere else entirely.
    /// </summary>
    private static void AppendContentStream(PdfPage page, byte[] bytes)
    {
        var prologue = page.Contents.PrependContent();
        prologue.CreateStream("q\n"u8.ToArray());

        var epilogue = page.Contents.AppendContent();
        var payload = new byte[2 + bytes.Length];
        "Q\n"u8.CopyTo(payload);
        bytes.CopyTo(payload, 2);
        epilogue.CreateStream(payload);
    }

    /// <summary>
    /// Registers the invisible font in the page's resource dictionary and returns the name to use
    /// in the content stream, avoiding any name the page already uses.
    /// </summary>
    private static string EnsureFontResource(PdfPage page, InvisibleFont font)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(page.Owner);
            page.Elements["/Resources"] = resources;
        }

        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }

        var reference = font.FontDictionary.Reference
            ?? throw new InvalidOperationException("The invisible font is not an indirect object.");

        // Re-use the entry if this page already has one pointing at our font, so that re-running
        // over a page does not accumulate resource entries.
        foreach (var key in fonts.Elements.Keys)
        {
            if (fonts.Elements[key] is PdfReference existing && existing.ObjectID == reference.ObjectID)
                return key;
        }

        var name = "/MFInvisible";
        var suffix = 0;
        while (fonts.Elements.ContainsKey(name))
            name = "/MFInvisible" + (++suffix).ToString(CultureInfo.InvariantCulture);

        fonts.Elements[name] = reference;
        return name;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.0005;

    /// <summary>Formats a number for a content stream: invariant, no exponent, four decimals.</summary>
    private static string F(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException($"Refusing to write a non-finite number ({value}) to a content stream.", nameof(value));
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
