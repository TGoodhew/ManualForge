using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ManualForge.Core.Pdf;

/// <summary>
/// Carries a document's catalog entries - bookmarks, page labels, how it opens, its optional-content
/// layers - from a source document into one its pages were imported into.
///
/// <para>
/// PDFsharp's page import copies each page and everything the page reaches, and nothing else, so a
/// flattened copy came out with every page intact and no bookmarks: 7 of the library's 169 flattened
/// manuals lost their navigation pane and 5 their printed page numbers, silently (#28). The entries
/// cannot simply be assigned across, because they point at objects in the source: a bookmark's
/// destination is a source page, and the optional-content properties name the same layer
/// dictionaries the pages' content refers to. So each value is deep-copied, and every reference is
/// remapped - to the imported page, to the copy PDFsharp already made of anything a page reaches, or
/// to a fresh copy of anything else.
/// </para>
/// </summary>
internal sealed class CatalogCarrier
{
    /// <summary>
    /// The catalog entries carried across. Each is either self-contained or refers only to pages and
    /// to objects a page also reaches, which is what the remapping covers.
    /// </summary>
    internal static readonly string[] Carried =
    [
        "/Outlines", "/Names", "/Dests", "/PageLabels", "/PageMode", "/PageLayout", "/OpenAction",
        "/ViewerPreferences", "/OCProperties", "/Lang", "/Metadata", "/URI", "/Threads",
    ];

    /// <summary>
    /// Entries left behind on purpose, and why. Anything else a source catalog has that the copy
    /// does not is reported as lost without a reason, which is the case worth noticing.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> LeftBehind = new Dictionary<string, string>
    {
        ["/AcroForm"] = "form fields, and on a signed file a signature the rebuild invalidates anyway",
        ["/StructTreeRoot"] = "tagging for screen readers, which ties each page's marked content to a tree of its own",
        ["/MarkInfo"] = "says the file is tagged, which without its structure tree it no longer is",
        ["/Perms"] = "signature permissions, which do not survive the rebuild",
        ["/Legal"] = "a signature attestation, which does not survive the rebuild",
        ["/Version"] = "the copy declares its own PDF version",
        ["/Extensions"] = "the copy declares its own PDF version",
    };

    private readonly PdfDocument _target;
    private readonly Dictionary<PdfObjectID, PdfReference> _map = [];
    private readonly Dictionary<PdfObjectID, PdfReference> _pages = [];

    public CatalogCarrier(PdfDocument target) => _target = target;

    /// <summary>
    /// How many references inside the imported pages pointed at a copy of a page rather than the
    /// page itself, and were pointed back at the page.
    /// </summary>
    public int PageReferencesRepaired { get; private set; }

    /// <summary>
    /// Records which page became which, along with every object the import copied for each page,
    /// and repairs the references the import got wrong. Call once, after every page is imported.
    ///
    /// <para>
    /// The import copies a page's structure exactly, so source and target can be walked side by side
    /// and their references paired as they are met. One thing it copies wrongly: a reference from one
    /// page to another - a link annotation's destination, an annotation's own page - is followed and
    /// the other page imported again as a detached copy, so a link in the flattened file led to a page
    /// that is not in the document. 461A Mil Manual's bookmarks share their actions with such links,
    /// which is how this was found. Every such reference is pointed at the real imported page.
    /// </para>
    /// </summary>
    public void MapImportedPages(IReadOnlyList<(PdfPage Source, PdfPage Target)> pages)
    {
        foreach (var (source, target) in pages)
        {
            if (source.Reference is { } s && target.Reference is { } t)
            {
                _pages[s.ObjectID] = t;
                _map[s.ObjectID] = t;
            }
        }

        var seen = new HashSet<PdfObjectID>();
        foreach (var (source, target) in pages)
            PairContents(source, target, seen);
    }

    private void Pair(PdfItem? source, PdfItem? target, HashSet<PdfObjectID> seen, Action<PdfItem> replace)
    {
        if (source is PdfReference sourceReference && target is PdfReference targetReference)
        {
            if (_pages.TryGetValue(sourceReference.ObjectID, out var page))
            {
                // A page reached from inside another page: never walked into, and repointed if the
                // import made a copy of it.
                if (targetReference.ObjectID != page.ObjectID)
                {
                    replace(page);
                    PageReferencesRepaired++;
                }
                return;
            }

            if (!seen.Add(sourceReference.ObjectID))
                return;
            _map.TryAdd(sourceReference.ObjectID, targetReference);
            PairContents(sourceReference.Value, targetReference.Value, seen);
            return;
        }

        PairContents(source, target, seen);
    }

    private void PairContents(PdfItem? source, PdfItem? target, HashSet<PdfObjectID> seen)
    {
        if (source is PdfDictionary sourceDictionary && target is PdfDictionary targetDictionary)
        {
            foreach (var key in sourceDictionary.Elements.Keys.ToList())
            {
                // A page's parent is the page tree, which the import does not copy: the target
                // document has a tree of its own.
                if (key == "/Parent")
                    continue;
                Pair(sourceDictionary.Elements[key], targetDictionary.Elements[key], seen,
                    replacement => targetDictionary.Elements[key] = replacement);
            }
        }
        else if (source is PdfArray sourceArray && target is PdfArray targetArray
                 && sourceArray.Elements.Count == targetArray.Elements.Count)
        {
            for (var i = 0; i < sourceArray.Elements.Count; i++)
            {
                var index = i;
                Pair(sourceArray.Elements[i], targetArray.Elements[i], seen,
                    replacement => targetArray.Elements[index] = replacement);
            }
        }
    }

    /// <summary>
    /// Copies the carried entries of <paramref name="sourceCatalog"/> into the target's catalog.
    /// </summary>
    public void CarryCatalog(PdfDictionary sourceCatalog)
    {
        var targetCatalog = _target.Internals.Catalog;
        foreach (var key in Carried)
        {
            var item = sourceCatalog.Elements[key];
            if (item is null)
                continue;
            if (Copy(item) is { } copy)
                targetCatalog.Elements[key] = copy;
        }
    }

    private PdfItem? Copy(PdfItem item)
    {
        switch (item)
        {
            case PdfReference reference:
            {
                if (_map.TryGetValue(reference.ObjectID, out var mapped))
                    return mapped;

                switch (reference.Value)
                {
                    case PdfDictionary dictionary:
                    {
                        // Registered before its contents are copied, so a cycle - an outline item's
                        // parent, its siblings - comes back to this copy rather than recursing.
                        var copy = new PdfDictionary(_target);
                        _target.Internals.AddObject(copy);
                        _map[reference.ObjectID] = copy.Reference!;
                        Fill(dictionary, copy);
                        return copy.Reference;
                    }
                    case PdfArray array:
                    {
                        var copy = new PdfArray(_target);
                        _target.Internals.AddObject(copy);
                        _map[reference.ObjectID] = copy.Reference!;
                        Fill(array, copy);
                        return copy.Reference;
                    }
                    case null:
                        return null;
                    default:
                        // An indirect number, string or name: carried as a direct value.
                        return Copy(reference.Value);
                }
            }
            case PdfDictionary dictionary:
            {
                var copy = new PdfDictionary(_target);
                Fill(dictionary, copy);
                return copy;
            }
            case PdfArray array:
            {
                var copy = new PdfArray(_target);
                Fill(array, copy);
                return copy;
            }
            case PdfIntegerObject integer:
                return new PdfInteger(integer.Value);
            case PdfRealObject real:
                return new PdfReal(real.Value);
            case PdfNameObject name:
                return new PdfName(name.Value);
            case PdfStringObject text:
                return new PdfString(text.Value, text.Encoding);
            case PdfBooleanObject flag:
                return new PdfBoolean(flag.Value);
            default:
                // Names, numbers, strings, booleans and null are values with no owner, and are
                // shared rather than copied.
                return item;
        }
    }

    private void Fill(PdfDictionary source, PdfDictionary target)
    {
        var isStream = source.Stream is not null;
        foreach (var key in source.Elements.Keys)
        {
            // The stream's length is set when its bytes are, and may be an indirect number here.
            if (isStream && key == "/Length")
                continue;
            if (Copy(source.Elements[key]!) is { } copy)
                target.Elements[key] = copy;
        }

        // The bytes as stored, still under their own /Filter, so nothing is decoded or re-encoded.
        if (isStream)
            target.CreateStream(source.Stream!.Value);
    }

    private void Fill(PdfArray source, PdfArray target)
    {
        foreach (var element in source.Elements)
            target.Elements.Add(Copy(element) ?? PdfNull.Value);
    }
}
