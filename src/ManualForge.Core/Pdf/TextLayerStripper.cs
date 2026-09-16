using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace ManualForge.Core.Pdf;

public sealed record StripResult(int PagesChanged, int TextBlocksRemoved, int FontResourcesRemoved);

/// <summary>
/// Removes an existing text layer from a document, leaving everything that draws ink untouched.
///
/// This exists for the strip-and-redo path: a manual carrying poor 2000s-era OCR cannot simply have
/// a second text layer added on top, because extractors would then return both, interleaved, and
/// searches would match the old bad text as readily as the new good text.
///
/// Only text-showing operations go. Everything between BT and ET is a text object by definition and
/// cannot contain image or path painting, so dropping those blocks whole is safe: the scanned page
/// image is drawn outside them and is never touched.
/// </summary>
public static class TextLayerStripper
{
    private static readonly HashSet<string> TextOperators =
        new(StringComparer.Ordinal) { "BT", "ET" };

    public static StripResult Strip(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pagesChanged = 0;
        var blocksRemoved = 0;
        var fontsRemoved = 0;

        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var (changed, blocks) = StripPage(page);
            if (!changed)
                continue;

            pagesChanged++;
            blocksRemoved += blocks;
            fontsRemoved += RemoveFontResources(page);
        }

        return new StripResult(pagesChanged, blocksRemoved, fontsRemoved);
    }

    /// <summary>Removes every BT/ET block from a single page.</summary>
    public static (bool Changed, int BlocksRemoved) StripPage(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        CSequence content;
        try
        {
            content = ContentReader.ReadContent(page);
        }
        catch (Exception)
        {
            // A content stream we cannot parse is one we must not rewrite.
            return (false, 0);
        }

        var kept = new CSequence();
        var depth = 0;
        var removed = 0;

        foreach (var item in content)
        {
            if (item is COperator op && TextOperators.Contains(op.OpCode.Name))
            {
                if (op.OpCode.Name == "BT")
                {
                    depth++;
                    if (depth == 1)
                        removed++;
                }
                else if (depth > 0)
                {
                    depth--;
                }
                continue;
            }

            if (depth == 0)
                kept.Add(item);
        }

        if (removed == 0)
            return (false, 0);

        // An unbalanced BT with no matching ET means the page's structure is not what we assumed.
        // Leaving it alone is the safe answer; a half-stripped page is worse than an unstripped one.
        if (depth != 0)
            return (false, 0);

        page.Contents.ReplaceContent(kept);
        return (true, removed);
    }

    /// <summary>
    /// Drops the page's font resources. Without this the fonts stay embedded, and a stripped
    /// document keeps carrying the weight of a text layer it no longer has.
    /// </summary>
    private static int RemoveFontResources(PdfPage page)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        var fonts = resources?.Elements.GetDictionary("/Font");
        if (fonts is null)
            return 0;

        var count = fonts.Elements.Count;
        resources!.Elements.Remove("/Font");
        return count;
    }
}
