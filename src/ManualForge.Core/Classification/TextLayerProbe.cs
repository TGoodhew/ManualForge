using UglyToad.PdfPig;

namespace ManualForge.Core.Classification;

/// <summary>
/// Asks, structurally, whether a document's pages already draw text.
///
/// This is the check that guards the one thing the OCR path must never do: add a text layer to a
/// page that already has one. Two layers do not merge — an extractor sorts them together by
/// position and returns them interleaved character by character, so "Broadband" comes back as
/// "BBrrooaaddbbaanndd" and the document is less searchable than before it was touched.
///
/// It counts glyphs, not decoded characters, because those two come apart. A font with a custom
/// encoding and no /ToUnicode draws thousands of glyphs a page and decodes to punctuation soup;
/// the text is unmistakably there, and only the reading of it fails. Three files in this library
/// gained a second layer that way before this existed.
/// </summary>
public static class TextLayerProbe
{
    /// <summary>
    /// Sampled pages, 1-based, that draw at least one glyph. Empty means the document is safe to
    /// write a text layer onto.
    /// </summary>
    /// <param name="samplePages">
    /// How many pages to look at. Sampling rather than reading every page keeps this to a few
    /// milliseconds on a 600-page manual; a document whose text layer is confined to pages nobody
    /// sampled is one whose text layer is nearly absent anyway.
    /// </param>
    public static IReadOnlyList<int> PagesWithText(string path, int samplePages = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

            var found = new List<int>();
            foreach (var pageNumber in DocumentClassifier.SamplePageNumbers(document.NumberOfPages, samplePages))
            {
                try
                {
                    if (document.GetPage(pageNumber).Letters.Count > 0)
                        found.Add(pageNumber);
                }
                catch (Exception)
                {
                    // A page that will not parse tells us nothing either way, and one bad page
                    // must not condemn the document.
                }
            }

            return found;
        }
        catch (Exception)
        {
            // If the file cannot be opened at all, this is not the check that should say so.
            return [];
        }
    }
}
