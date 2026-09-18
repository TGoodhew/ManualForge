using System.Globalization;

namespace ManualForge.Core.Auditing;

/// <summary>
/// Where the content a page did not extract actually lives, which decides what to do about it.
///
/// <para>
/// The distinction matters more than it looks. Both kinds are genuinely under-extracted, but they
/// are different problems with different remedies and wildly different scale, and reporting them as
/// one list buries the smaller and more tractable of the two under the larger. On this corpus the
/// drawn pages are a few hundred; the raster ones are ten thousand.
/// </para>
/// </summary>
public enum PageKind
{
    /// <summary>
    /// The missing content is drawn on the page as vector graphics — a syntax diagram, a pin-out, a
    /// schematic label. It never had a text layer and no amount of re-OCRing the document as a
    /// whole would have found it. This is the failure the audit exists for.
    /// </summary>
    Drawn,

    /// <summary>
    /// The missing content is lettering inside an image: a scanned page whose OCR missed it, or a
    /// screenshot pasted into a digital document. Real, and worth recovering, but the existing
    /// classify-and-OCR path is already the right tool and the set is very much larger.
    /// </summary>
    Raster,
}

/// <summary>What the audit concluded about one page.</summary>
public enum PageVerdict
{
    /// <summary>The text layer accounts for what is on the page.</summary>
    Fine,

    /// <summary>
    /// The page carries a text layer and there is legible content on it that the layer does not
    /// hold. This is the finding the audit exists for.
    /// </summary>
    UnderExtracted,

    /// <summary>
    /// No text layer at all, and a scanned image where the text should be. Reported separately
    /// because the existing OCR path already covers it — it is not news, and burying the finding
    /// above under tens of thousands of these would make the report useless.
    /// </summary>
    NoTextLayer,

    /// <summary>
    /// Glyphs are drawn but almost nothing decodes: a custom encoding with no /ToUnicode, or text
    /// converted to outlines. Also not news — the classifier already calls this UnreadableTextLayer
    /// — but it is a different repair from the one above and is kept apart.
    /// </summary>
    Undecodable,

    /// <summary>The page could not be read or rendered.</summary>
    Unreadable,
}

/// <summary>
/// Every signal measured for one page, plus the verdict they add up to.
///
/// <para>
/// The signals are all kept, not just the deciding one. A verdict with no numbers behind it cannot
/// be argued with, and the first thing anybody sensible does with a detector's output is argue with
/// it.
/// </para>
/// </summary>
public sealed record PageAudit(
    int PageNumber,
    int GlyphsDrawn,
    int CharactersDecoded,
    int PathPaintOperations,
    int TextShowOperations,
    int ImageCount,
    double ImageCoverage,
    bool HasType3Font,
    bool HasFontWithoutToUnicode,
    InkAnalysis Ink,
    bool OutlineHeadingMissing,
    PageVerdict Verdict,
    IReadOnlyList<string> Signals)
{
    public bool IsFlagged => Verdict == PageVerdict.UnderExtracted;

    /// <summary>Where the content this page did not extract lives.</summary>
    public PageKind Kind { get; init; } = PageKind.Drawn;

    /// <summary>Flagged, and the missing content is drawn rather than photographed.</summary>
    public bool IsDrawn => IsFlagged && Kind == PageKind.Drawn;

    /// <summary>
    /// Characters a repair would be expected to recover, from the count of glyph-shaped clusters
    /// of ink the text layer does not account for. An estimate, and labelled as one everywhere it
    /// is shown.
    /// </summary>
    public int EstimatedRecoverableCharacters { get; init; }

    /// <summary>
    /// Resolution a repair should render this page at. Small annotation needs more pixels per
    /// glyph than body text does, and diagram labels in this corpus run from 4 pt upwards.
    /// </summary>
    public int SuggestedOcrDpi { get; init; } = 300;

    public string SignalSummary => string.Join(", ", Signals);

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"page {PageNumber}: {Verdict} ({Kind}), {CharactersDecoded} chars, " +
        $"{PathPaintOperations} path ops, ink {Ink.InkFraction:P2} " +
        $"({Ink.UncoveredInkFraction:P2} unaccounted for), {Ink.GlyphLikeBlobs} glyph-like blobs");
}
