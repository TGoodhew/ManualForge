using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Pdf;

/// <summary>One bookmark: how deep it sits, what it says, and the page it opens, if any.</summary>
/// <param name="Page">One-based, or null when it opens no page of this document (a link, a script).</param>
public sealed record Bookmark(int Depth, string Title, int? Page)
{
    public override string ToString() =>
        $"{new string(' ', Depth * 2)}{Title} -> {(Page is { } p ? $"page {p}" : "no page")}";
}

/// <summary>
/// Reads a document's bookmarks as a flat list in reading order, each resolved to the page it opens.
/// Comparing the lists of a source and its flattened copy is what shows that a bookmark still lands
/// on its page, rather than merely that an outline exists.
/// </summary>
public static class Bookmarks
{
    /// <summary>A malformed outline that loops back on itself stops here instead of forever.</summary>
    private const int Limit = 100_000;

    public static IReadOnlyList<Bookmark> Read(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Read(document);
    }

    public static IReadOnlyList<Bookmark> Read(PdfDocument document)
    {
        var pages = new Dictionary<PdfObjectID, int>();
        for (var i = 0; i < document.PageCount; i++)
        {
            if (document.Pages[i].Reference is { } reference)
                pages.TryAdd(reference.ObjectID, i + 1);
        }

        var catalog = document.Internals.Catalog;
        var result = new List<Bookmark>();
        if (catalog.Elements.GetDictionary("/Outlines") is not { } root)
            return result;

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        Walk(root.Elements.GetDictionary("/First"), 0);
        return result;

        void Walk(PdfDictionary? item, int depth)
        {
            while (item is not null && result.Count < Limit && seen.Add(item))
            {
                var title = item.Elements.GetString("/Title") ?? string.Empty;
                result.Add(new Bookmark(depth, title, PageOf(item)));
                Walk(item.Elements.GetDictionary("/First"), depth + 1);
                item = item.Elements.GetDictionary("/Next");
            }
        }

        int? PageOf(PdfDictionary item)
        {
            var destination = Resolve(item.Elements["/Dest"]);
            if (destination is null && item.Elements.GetDictionary("/A") is { } action
                && action.Elements.GetName("/S") == "/GoTo")
            {
                destination = Resolve(action.Elements["/D"]);
            }

            return destination is { Elements.Count: > 0 } array
                   && array.Elements[0] is PdfReference page
                   && pages.TryGetValue(page.ObjectID, out var number)
                ? number
                : null;
        }

        // An explicit destination is an array whose first element is the page; a named one is looked
        // up in the catalog's /Dests dictionary or its /Names /Dests tree, and may be wrapped in /D.
        PdfArray? Resolve(PdfItem? destination)
        {
            switch (Value(destination))
            {
                case PdfArray array:
                    return array;
                case PdfDictionary wrapped:
                    return Value(wrapped.Elements["/D"]) as PdfArray;
                case PdfName name:
                    return Resolve(catalog.Elements.GetDictionary("/Dests")?.Elements[name.Value]);
                case PdfString text:
                    var tree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
                    return tree is null ? null : Resolve(LookUp(tree, text.Value, 0));
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Every link annotation that opens a page of this document, as (page it sits on, page it
    /// opens). A link whose destination is a page outside the document's page tree counts with a
    /// null target, which is how a flatten that imported a detached copy of the page shows up.
    /// </summary>
    public static IReadOnlyList<(int OnPage, int? Opens)> Links(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var pages = new Dictionary<PdfObjectID, int>();
        for (var i = 0; i < document.PageCount; i++)
        {
            if (document.Pages[i].Reference is { } reference)
                pages.TryAdd(reference.ObjectID, i + 1);
        }

        var result = new List<(int, int?)>();
        for (var i = 0; i < document.PageCount; i++)
        {
            if (document.Pages[i].Elements.GetArray("/Annots") is not { } annotations)
                continue;

            foreach (var element in annotations.Elements)
            {
                if (Value(element) is not PdfDictionary annotation || annotation.Elements.GetName("/Subtype") != "/Link")
                    continue;

                var destination = annotation.Elements["/Dest"];
                if (destination is null && annotation.Elements.GetDictionary("/A") is { } action
                    && action.Elements.GetName("/S") == "/GoTo")
                {
                    destination = action.Elements["/D"];
                }

                // Only explicit destinations name a page object; named ones go through the catalog,
                // which bookmarks already check.
                if (Value(destination) is PdfArray { Elements.Count: > 0 } array && array.Elements[0] is PdfReference page)
                    result.Add((i + 1, pages.TryGetValue(page.ObjectID, out var number) ? number : null));
            }
        }

        return result;
    }

    private static PdfItem? Value(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static PdfItem? LookUp(PdfDictionary node, string name, int depth)
    {
        if (depth > 64)
            return null;

        if (node.Elements.GetArray("/Names") is { } names)
        {
            for (var i = 0; i + 1 < names.Elements.Count; i += 2)
            {
                if (Value(names.Elements[i]) is PdfString key && key.Value == name)
                    return names.Elements[i + 1];
            }
        }

        if (node.Elements.GetArray("/Kids") is { } kids)
        {
            foreach (var kid in kids.Elements)
            {
                if (Value(kid) is PdfDictionary child && LookUp(child, name, depth + 1) is { } found)
                    return found;
            }
        }

        return null;
    }
}
