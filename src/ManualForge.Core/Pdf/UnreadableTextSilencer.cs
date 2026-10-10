using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace ManualForge.Core.Pdf;

/// <summary>
/// Makes printed text that no reader can decode extract as spaces, without changing how a single
/// page looks.
///
/// <para>
/// Some manuals are typeset in fonts whose character codes mean nothing outside the font: a
/// subsetting tool renumbered the glyphs, or shifted them by a constant, and wrote no table saying
/// what they stand for. The page prints perfectly and extracts as <c>*($SSOLDQFHV</c>. That text
/// cannot be stripped - it is the ink on the page, and removing it removes the letters (it did, to
/// five manuals, on 30 September 2026) - and while it is there the OCR layer is written around it,
/// which on such a page means hardly anywhere.
/// </para>
///
/// <para>
/// So it is left drawn and made silent instead. Every font gets a /ToUnicode map that sends every
/// code to a space. Viewers draw from the glyph programs and never consult that map, so rendering
/// is untouched; extractors consult nothing else, so the garbage goes, and the recognised layer can
/// cover the page. Embedded fonts that happen to be readable are silenced too: this runs only on a
/// document whose text was judged unreadable as a whole, and the recognised layer reads every glyph
/// on the page, readable or not. Fonts that are not embedded are left alone (see
/// <see cref="Embeds"/>).
/// </para>
/// </summary>
public static class UnreadableTextSilencer
{
    public static int Silence(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        PdfDictionary? oneByte = null;
        PdfDictionary? twoByte = null;
        var silenced = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        foreach (var font in FontsIn(document))
        {
            if (!Embeds(font) || !silenced.Add(font))
                continue;

            var composite = font.Elements.GetName("/Subtype") == "/Type0";
            var map = composite
                ? twoByte ??= BlankMap(document, bytes: 2)
                : oneByte ??= BlankMap(document, bytes: 1);
            font.Elements.SetReference("/ToUnicode", map);
        }

        return silenced.Count;
    }

    /// <summary>
    /// Whether the font carries its own glyphs, and so can carry its own numbering. A font that is
    /// not embedded is drawn by the viewer from a standard font of that name through a standard
    /// encoding, so its text always decodes - the Times and Helvetica on Agilent's front pages
    /// among the unreadable ones. Those are left alone: PdfPig ignores a /ToUnicode on them, so
    /// silencing would hide them from other readers while ours still kept the recognised words off
    /// them. Type 3 fonts are their own glyph programs, so always count.
    /// </summary>
    private static bool Embeds(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type3")
            return true;

        var described = font;
        if (subtype == "/Type0")
        {
            if (font.Elements.GetArray("/DescendantFonts") is not { Elements.Count: > 0 } descendants
                || descendants.Elements.GetDictionary(0) is not { } descendant)
                return false;
            described = descendant;
        }

        return described.Elements.GetDictionary("/FontDescriptor") is { } descriptor
            && (descriptor.Elements.ContainsKey("/FontFile")
                || descriptor.Elements.ContainsKey("/FontFile2")
                || descriptor.Elements.ContainsKey("/FontFile3"));
    }

    /// <summary>
    /// Every font a page, form, Type 3 glyph procedure or annotation appearance can name: the
    /// indirect ones from the cross-reference table, and any written directly into a resource
    /// dictionary, which the table never lists.
    /// </summary>
    private static IEnumerable<PdfDictionary> FontsIn(PdfDocument document)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is PdfDictionary dictionary && dictionary.Elements.GetName("/Type") == "/Font")
                yield return dictionary;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages)
        {
            foreach (var font in FontsInResources(page.Elements.GetDictionary("/Resources"), seen))
                yield return font;
        }
    }

    private static IEnumerable<PdfDictionary> FontsInResources(PdfDictionary? resources, HashSet<PdfDictionary> seen)
    {
        if (resources is null || !seen.Add(resources))
            yield break;

        if (resources.Elements.GetDictionary("/Font") is { } fonts)
        {
            foreach (var key in fonts.Elements.Keys)
            {
                if (fonts.Elements.GetDictionary(key) is not { } font)
                    continue;
                yield return font;
                foreach (var inner in FontsInResources(font.Elements.GetDictionary("/Resources"), seen))
                    yield return inner;
            }
        }

        if (resources.Elements.GetDictionary("/XObject") is { } xobjects)
        {
            foreach (var key in xobjects.Elements.Keys)
            {
                if (xobjects.Elements.GetDictionary(key) is { } form
                    && form.Elements.GetName("/Subtype") == "/Form")
                {
                    foreach (var inner in FontsInResources(form.Elements.GetDictionary("/Resources"), seen))
                        yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// A CMap sending every code of the given width to U+0020. A bfrange with a string destination
    /// counts upwards from it, so each range maps through an array of spaces instead: one range for
    /// single-byte codes, 256 for two-byte ones, compressed to a few hundred bytes.
    /// </summary>
    private static PdfDictionary BlankMap(PdfDocument document, int bytes)
    {
        var spaces = string.Concat(Enumerable.Repeat("<0020>", 256));
        var text = new StringBuilder()
            .Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
            .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Append("/CMapName /ManualForge-Unreadable def\n/CMapType 2 def\n")
            .Append("1 begincodespacerange\n")
            .Append(bytes == 1 ? "<00> <FF>\n" : "<0000> <FFFF>\n")
            .Append("endcodespacerange\n");

        if (bytes == 1)
        {
            text.Append("1 beginbfrange\n<00> <FF> [").Append(spaces).Append("]\nendbfrange\n");
        }
        else
        {
            // At most 100 ranges to a block.
            for (var start = 0; start < 256; start += 100)
            {
                var count = Math.Min(100, 256 - start);
                text.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfrange\n");
                for (var high = start; high < start + count; high++)
                {
                    text.Append(CultureInfo.InvariantCulture, $"<{high:X2}00> <{high:X2}FF> [")
                        .Append(spaces).Append("]\n");
                }
                text.Append("endbfrange\n");
            }
        }

        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");

        var map = new PdfDictionary(document);
        map.CreateStream(new FlateDecode().Encode(Encoding.ASCII.GetBytes(text.ToString())));
        map.Elements["/Filter"] = new PdfName("/FlateDecode");
        document.Internals.AddObject(map);
        return map;
    }
}
