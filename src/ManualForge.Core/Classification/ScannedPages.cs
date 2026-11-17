using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace ManualForge.Core.Classification;

/// <summary>
/// Pages that are a scan with no text layer of their own, in a document that may have text
/// elsewhere.
///
/// <para>
/// A great many HP manuals were reissued by Agilent as the old scan with a typeset cover and a
/// "Hewlett-Packard to Agilent Technologies Transition" page in front of it. Judged as a whole,
/// such a document has a clean text layer, and was left alone: <c>85033C Operating and
/// service.pdf</c> went into the library with twenty of its twenty-two pages unsearchable,
/// because page 2 alone carries enough prose to lift the sampled average over the image-only
/// threshold. This is how the pages that still need recognising are found.
/// </para>
///
/// <para>
/// The rule is the one the audit uses for a page with no text layer: an image covering most of
/// the page and nothing hidden drawn over it. A few visible glyphs are allowed - a stamped page
/// number or part number on an otherwise scanned page - because the writer leaves out every
/// recognised word that would land on visible text. Hidden text never is: a page that has any
/// already carries a layer, and a second one would interleave with it.
/// </para>
/// </summary>
public static class ScannedPages
{
    /// <summary>
    /// How much of the page its largest image must cover. The audit's own figure: a scanned page
    /// is one image covering upwards of 90%, a screenshot figure in a typeset page a third or less.
    /// </summary>
    public const double MinimumImageCoverage = 0.5;

    /// <summary>
    /// Visible glyphs a page may draw and still count as a scan: a folio, a part number, a
    /// one-line stamp. Well under the hundred a page of text draws in its first two lines.
    /// </summary>
    public const int MaximumStrayGlyphs = 40;

    /// <summary>Whether this page is a scan that carries no text layer of its own.</summary>
    public static bool IsScanWithoutText(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var letters = page.Letters;
        if (letters.Count > MaximumStrayGlyphs)
            return false;

        if (letters.Any(l => l.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip))
            return false;

        return LargestImageCoverage(page) >= MinimumImageCoverage;
    }

    /// <summary>The share of the page its largest image covers, 0 to 1.</summary>
    public static double LargestImageCoverage(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            var area = Math.Abs(page.CropBox.Bounds.Area);
            if (area <= 0)
                return 0;

            var covered = 0.0;
            foreach (var image in page.GetImages())
                covered = Math.Max(covered, Math.Abs(image.BoundingBox.Area) / area);

            return Math.Min(1.0, covered);
        }
        catch (Exception)
        {
            // An image that will not parse is no evidence of a scan.
            return 0;
        }
    }

    /// <summary>
    /// Every page of the document, 1-based, that is a scan without text. Every page, not a sample:
    /// this is asked of a document about to be recognised, where a scan left out is a page left
    /// unsearchable.
    /// </summary>
    public static IReadOnlyList<int> In(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

        var found = new List<int>();
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            try
            {
                if (IsScanWithoutText(document.GetPage(pageNumber)))
                    found.Add(pageNumber);
            }
            catch (Exception)
            {
                // A page that will not parse is left as it is; it is not known to be a scan.
            }
        }

        return found;
    }
}
