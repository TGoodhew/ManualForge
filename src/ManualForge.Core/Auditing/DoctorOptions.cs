namespace ManualForge.Core.Auditing;

/// <summary>
/// Thresholds for <see cref="UnderExtractionDetector"/>, every one of them justified in
/// <c>docs/UNDER-EXTRACTION.md</c> and overridable from the command line.
///
/// <para>
/// The defaults were measured, not chosen. Each one below records what it was measured against,
/// because a tuned constant nobody can explain becomes folklore, and folklore is how a detector
/// ends up firing on a thousand pages that nobody dares re-check.
/// </para>
/// </summary>
public sealed class DoctorOptions
{
    // ---------------------------------------------------------------------------------------
    // Stage one: which pages are worth rendering at all.
    //
    // Rendering is the expensive step and the discriminating one. Everything here exists to keep
    // it off the ninety-odd per cent of pages that are plainly fine, so that a corpus-wide audit
    // finishes in minutes rather than hours.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A page with at least this many extracted characters is left alone unless its content stream
    /// is unusually path-heavy.
    ///
    /// <para>
    /// 900 sits below the 1,500–3,000 a normal typeset page of this corpus yields and well above
    /// the 458/page average that the 54845A Programmer's Guide manages across its 110 pages. The
    /// gate is deliberately generous: a page over it can still be rendered by the path-operator
    /// rule below, so this number only decides what gets looked at first, never what is flagged.
    /// </para>
    /// </summary>
    public int RenderBelowCharactersPerPage { get; init; } = 900;

    /// <summary>
    /// A page painting at least this many path-fill or path-stroke operations is rendered whatever
    /// its character count, because that is what a drawn figure looks like from the content stream.
    ///
    /// <para>
    /// 40 is above what page furniture costs — a header rule, a footer rule, a table's ruling and a
    /// logo come to well under 40 painting operations — and far below the hundreds a syntax
    /// diagram, schematic or pin-out needs. Page 40 of the 54845A guide paints several hundred.
    /// </para>
    /// </summary>
    public int RenderAtOrAbovePathOperations { get; init; } = 40;

    /// <summary>
    /// Resolution for the audit render. Not the OCR resolution — this only has to make ink
    /// countable and glyph-sized blobs separable, and 150 dpi is where a 6 pt annotation is still
    /// twelve pixels tall and does not merge with its neighbour. Repair renders far higher.
    /// </summary>
    public int AuditDpi { get; init; } = 150;

    /// <summary>
    /// Grey level at or below which a pixel counts as ink, on 0 (black) to 255 (white).
    ///
    /// <para>
    /// 200 rather than a hard 254: PDFium antialiases vector strokes, so a hairline rule lands as a
    /// band of greys. Counting every off-white pixel as ink would let antialiasing dominate the ink
    /// fraction on a page whose only mark is a header rule.
    /// </para>
    /// </summary>
    public byte InkLevel { get; init; } = 200;

    // ---------------------------------------------------------------------------------------
    // Stage two: what counts as under-extracted.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// How far, in points, a glyph's bounding box is grown before ink is credited to it.
    ///
    /// <para>
    /// The extracted box is the glyph's own outline. Antialiasing spreads a stroke by about half a
    /// pixel each way, hinting moves stems, and descenders and accents routinely fall outside the
    /// box a reader reports. 1.5 pt absorbs all of that at 150 dpi without letting one line of
    /// text claim the ink of the line below it, which is 10–12 pt away.
    /// </para>
    /// </summary>
    public double TextBoxPaddingPt { get; init; } = 1.5;

    /// <summary>
    /// How far, as a fraction of the text's own point size, a glyph's box is grown sideways.
    ///
    /// <para>
    /// This one is here because of a measurement, not a hunch. The library's own OCR'd scans put
    /// their invisible text layer on the box the recogniser detected, and a CRNN's per-character
    /// timesteps consistently start about one character late — so the run begins a character to the
    /// right of the ink it describes, and the first letter of every word had no glyph over it. On
    /// 200CD.pdf that alone accounted for most of the unexplained ink on seven pages, and it would
    /// have done the same on every OCR'd scan in the corpus.
    /// </para>
    ///
    /// <para>
    /// 0.6 em is about one character's advance. Growing a text box sideways is nearly free in
    /// precision terms: what lies to the left and right of a word on its own line is almost always
    /// more of the same line.
    /// </para>
    /// </summary>
    public double TextBoxPaddingEm { get; init; } = 0.6;

    /// <summary>
    /// Ink that no extracted glyph accounts for, as a fraction of the whole page, above which the
    /// page is a candidate.
    ///
    /// <para>
    /// 0.002 — two pixels in a thousand. A page of clean prose leaves well under that once its
    /// glyph boxes are credited; the residue is rules and page furniture. A drawn figure leaves
    /// several times it. This threshold alone is not enough to flag, because a page can be mostly
    /// line art with no lettering in it at all, which is why <see cref="MinimumGlyphLikeBlobs"/>
    /// has to agree.
    /// </para>
    /// </summary>
    public double UncoveredInkFraction { get; init; } = 0.002;

    /// <summary>
    /// How many glyph-shaped clusters of unaccounted-for ink a page must carry before it is
    /// flagged.
    ///
    /// <para>
    /// This is the precision half of the test and the reason a plain schematic does not fire. Long
    /// thin strokes, large bubbles and whole rules are excluded by shape before they are counted,
    /// so what is left is ink the size and density of lettering. 40 is roughly six or seven short
    /// words: below that, the recoverable text is a figure number and not worth a re-OCR; above it,
    /// there is a caption, a pin-out table or a syntax diagram's worth of labels to recover.
    /// </para>
    /// </summary>
    public int MinimumGlyphLikeBlobs { get; init; } = 40;

    /// <summary>Smallest blob height in points that can be lettering. Below this it is a speck.</summary>
    public double MinimumBlobHeightPt { get; init; } = 2.5;

    /// <summary>
    /// Largest blob height in points that can be lettering. Above this it is a bubble, a box or a
    /// rule, not a character. Display headings in this corpus top out around 24 pt.
    /// </summary>
    public double MaximumBlobHeightPt { get; init; } = 30.0;

    /// <summary>
    /// Largest blob width in points that can be one character or a touching pair. Wider than this
    /// and it is a connector line, an arrow run or a box edge.
    /// </summary>
    public double MaximumBlobWidthPt { get; init; } = 30.0;

    /// <summary>
    /// Least share of its own bounding box a blob must fill to be glyph-like. A letter fills
    /// 25–60% of its box; a diagonal connector or an L-shaped corner fills far less.
    /// </summary>
    public double MinimumBlobFill { get; init; } = 0.18;

    /// <summary>
    /// Most extreme width-to-height ratio, either way round, a glyph-like blob may have. An 'l' is
    /// about 1:6 and an 'm' about 1.2:1; a rule fragment is 40:1.
    /// </summary>
    public double MaximumBlobAspect { get; init; } = 8.0;

    /// <summary>
    /// Characters assumed per glyph-like blob when estimating how much text a repair would
    /// recover. Slightly below one, because touching characters merge into a single blob and
    /// because a few blobs are arrowheads rather than letters. The estimate is for ranking the
    /// report, and is labelled as an estimate wherever it is shown.
    /// </summary>
    public double CharactersPerBlob { get; init; } = 0.85;

    // ---------------------------------------------------------------------------------------
    // Supporting signals. None of these flags a page on its own; they rank it and explain it.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Below this many decoded characters per page, with glyphs drawn on the page, the text layer
    /// is present but unreadable — a custom encoding with no /ToUnicode, typically. Matches the
    /// classifier's own threshold so the two agree about the same file.
    /// </summary>
    public int UndecodableCharactersPerPage { get; init; } = 100;

    /// <summary>
    /// A page carrying an image that covers at least this share of it, and no text, is a scan. The
    /// existing OCR path already handles those, and lumping them in here would bury the finding
    /// this tool exists for under tens of thousands of pages that are not news.
    /// </summary>
    public double ScannedPageImageCoverage { get; init; } = 0.5;

    /// <summary>
    /// Share of a document's pages that must be under-extracted before the document itself is
    /// called under-extracted rather than merely having figures in it.
    ///
    /// <para>
    /// Almost every illustrated manual has a handful of pages whose figure labels are drawn rather
    /// than set, and saying so about all of them would drown the finding that matters. The two test
    /// cases sit either side of this by a wide margin: the 54845A Programmer's Guide has 68% of its
    /// pages flagged, and the TDS3014B Programming Manual — whose commands all extract correctly —
    /// has 1.4%, being six screenshots and a character chart. Anywhere from about 3% to about 50%
    /// would separate those two, so 10% is chosen for having the most room either side rather than
    /// for fitting them tightly.
    /// </para>
    ///
    /// <para>
    /// This only decides how the report reads. The flagged pages are still listed, still counted,
    /// and still repaired.
    /// </para>
    /// </summary>
    public double DocumentFlagShare { get; init; } = 0.10;

    /// <summary>
    /// Image coverage at or above which a flagged page's missing lettering is taken to be inside an
    /// image rather than drawn on the page.
    ///
    /// <para>
    /// 0.2 separates the cases cleanly on this corpus and does not need to be near a boundary: a
    /// scanned page is a single image covering upwards of 90%, a screenshot figure covers a third
    /// of a page, and a vector syntax diagram has no image on the page at all.
    /// </para>
    /// </summary>
    public double RasterPageImageCoverage { get; init; } = 0.2;

    /// <summary>Pages to sample per document. Zero means every page, which is the default.</summary>
    public int SamplePages { get; init; }

    /// <summary>Documents to audit at once. Defaults to half the cores, leaving the machine usable.</summary>
    public int Workers { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Folders excluded from the audit, matching the indexer's own exclusions.</summary>
    public IReadOnlyList<string> ExcludedFolderNames { get; init; } = ["BASELINE", "_Originals", "_GroundTruth"];
}
