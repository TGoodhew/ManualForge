using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Pdf;

/// <summary>The dimensions and resolution of one image on one page, used to prove nothing was re-encoded.</summary>
public sealed record PageImageFingerprint(
    int PageNumber,
    double WidthPt,
    double HeightPt,
    int Rotation,
    IReadOnlyList<(int PixelWidth, int PixelHeight, string Filter)> Images)
{
    /// <summary>Effective resolution of the largest image on the page, horizontally.</summary>
    public double LargestImageDpi
    {
        get
        {
            if (Images.Count == 0 || WidthPt <= 0)
                return 0;
            var widest = Images.Max(i => i.PixelWidth);
            return widest * 72.0 / WidthPt;
        }
    }
}

public sealed record FlattenResult(
    string SourcePath,
    string OutputPath,
    int PageCount,
    long SourceBytes,
    long OutputBytes,
    ModificationBlocker BlockerCleared,
    IReadOnlyList<string> Discrepancies,
    IReadOnlyList<string> Dropped)
{
    public bool IsVerified => Discrepancies.Count == 0;

    public double SizeRatio => SourceBytes == 0 ? 0 : OutputBytes / (double)SourceBytes;
}

/// <summary>
/// Rebuilds a document that refuses modification into an equivalent one that permits it, by
/// importing its pages into a fresh document.
///
/// The important property is that this is a *structural* rebuild, not a re-render. PDFsharp's page
/// import carries the page's existing content streams and image XObjects across as they are, so a
/// forty-year-old CCITT G4 scan arrives on the other side as the same compressed bytes. Rasterising
/// the document and rebuilding it — what Ghostscript-based approaches do — would re-encode every
/// page and lose detail that cannot be recovered, which for a library of irreplaceable scans is
/// the one outcome worth ruling out completely.
///
/// Every flatten is verified before it is accepted: page count, per-page geometry and rotation, and
/// the pixel dimensions and compression filter of every image on every page must match the source,
/// and so must every bookmark's title, depth and destination page, and every link's destination.
///
/// <para>
/// The document's own entries - bookmarks, page labels, how it opens, its optional-content layers -
/// are carried across by <see cref="CatalogCarrier"/>. Page import alone drops them, which cost 7
/// flattened manuals their bookmarks before anyone noticed (#28). What is left behind on purpose -
/// forms, signatures, tagging - and anything else that does not survive is listed in
/// <see cref="FlattenResult.Dropped"/>, so it is reported rather than silent.
/// </para>
/// </summary>
public sealed class PdfFlattener
{
    /// <summary>
    /// Flattens <paramref name="sourcePath"/> to <paramref name="outputPath"/> and verifies the
    /// result against the source. Throws if the source cannot be imported at all.
    /// </summary>
    public FlattenResult Flatten(string sourcePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to flatten a file over itself.");

        var capabilities = PdfInspector.Inspect(sourcePath);
        if (capabilities.IsHopeless)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(sourcePath)}' cannot be imported at all: {capabilities.Detail}");
        }

        var sourceFingerprints = Fingerprint(sourcePath);
        var sourceBookmarks = Bookmarks.Read(sourcePath);
        var sourceLinks = Bookmarks.Links(sourcePath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + ".partial";

        try
        {
            using (var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import))
            using (var target = new PdfDocument())
            {
                // Carry the document information across; it is the only metadata worth keeping and
                // losing it would make the flattened copy look like a different document.
                target.Info.Title = source.Info.Title;
                target.Info.Author = source.Info.Author;
                target.Info.Subject = source.Info.Subject;
                target.Info.Keywords = source.Info.Keywords;
                target.Info.Creator = source.Info.Creator;

                var imported = new List<(PdfPage, PdfPage)>(source.PageCount);
                for (var i = 0; i < source.PageCount; i++)
                    imported.Add((source.Pages[i], target.AddPage(source.Pages[i])));

                var carrier = new CatalogCarrier(target);
                carrier.MapImportedPages(imported);
                carrier.CarryCatalog(source.Internals.Catalog);

                target.Save(temporary);
            }

            var outputFingerprints = Fingerprint(temporary);
            var discrepancies = Compare(sourceFingerprints, outputFingerprints).ToList();
            discrepancies.AddRange(CompareBookmarks(sourceBookmarks, Bookmarks.Read(temporary)));
            discrepancies.AddRange(CompareLinks(sourceLinks, Bookmarks.Links(temporary)));
            var dropped = DroppedEntries(sourcePath, temporary);

            // Only accept the flattened file once it has been shown to match. A mismatch leaves the
            // partial file in place for inspection rather than quietly replacing anything.
            if (discrepancies.Count > 0)
            {
                return new FlattenResult(
                    sourcePath, temporary, outputFingerprints.Count,
                    new FileInfo(sourcePath).Length, new FileInfo(temporary).Length,
                    capabilities.Blocker, discrepancies, dropped);
            }

            File.Move(temporary, outputPath, overwrite: true);

            return new FlattenResult(
                sourcePath, outputPath, outputFingerprints.Count,
                new FileInfo(sourcePath).Length, new FileInfo(outputPath).Length,
                capabilities.Blocker, discrepancies, dropped);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { /* leave it for inspection */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Records each page's geometry and the dimensions and filter of every image it carries.
    /// Comparing these before and after is what turns "the flatten seemed to work" into evidence.
    /// </summary>
    public static IReadOnlyList<PageImageFingerprint> Fingerprint(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var fingerprints = new List<PageImageFingerprint>(document.PageCount);

        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var images = new List<(int, int, string)>();

            var resources = page.Elements.GetDictionary("/Resources");
            var xObjects = resources?.Elements.GetDictionary("/XObject");

            if (xObjects is not null)
            {
                foreach (var key in xObjects.Elements.Keys)
                {
                    if (xObjects.Elements.GetObject(key) is not PdfDictionary xObject)
                        continue;
                    if (xObject.Elements.GetName("/Subtype") != "/Image")
                        continue;

                    var width = xObject.Elements.GetInteger("/Width");
                    var height = xObject.Elements.GetInteger("/Height");
                    var filter = DescribeFilter(xObject);
                    images.Add((width, height, filter));
                }
            }

            // Sort so that a difference in dictionary key order is not mistaken for a real change.
            images.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1)
                : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2)
                : string.CompareOrdinal(a.Item3, b.Item3));

            fingerprints.Add(new PageImageFingerprint(
                i + 1, page.Width.Point, page.Height.Point, page.Rotate, images));
        }

        return fingerprints;
    }

    private static string DescribeFilter(PdfDictionary image)
    {
        var filter = image.Elements["/Filter"];
        return filter switch
        {
            PdfName name => name.Value,
            PdfArray array => string.Join("+", array.Elements.Select(e => e is PdfName n ? n.Value : e?.ToString() ?? "?")),
            null => "(none)",
            _ => filter.ToString() ?? "?",
        };
    }

    public static IReadOnlyList<string> Compare(
        IReadOnlyList<PageImageFingerprint> before,
        IReadOnlyList<PageImageFingerprint> after)
    {
        var problems = new List<string>();

        if (before.Count != after.Count)
        {
            problems.Add($"Page count changed: {before.Count} became {after.Count}.");
            return problems;
        }

        for (var i = 0; i < before.Count; i++)
        {
            var a = before[i];
            var b = after[i];

            // A twentieth of a point of drift is float noise in the box arithmetic, not a change.
            if (Math.Abs(a.WidthPt - b.WidthPt) > 0.05 || Math.Abs(a.HeightPt - b.HeightPt) > 0.05)
            {
                problems.Add(
                    $"Page {a.PageNumber} changed size: {a.WidthPt:F2}x{a.HeightPt:F2} became {b.WidthPt:F2}x{b.HeightPt:F2} pt.");
            }

            if (a.Rotation != b.Rotation)
                problems.Add($"Page {a.PageNumber} rotation changed: {a.Rotation} became {b.Rotation}.");

            if (a.Images.Count != b.Images.Count)
            {
                problems.Add($"Page {a.PageNumber} carried {a.Images.Count} image(s), now carries {b.Images.Count}.");
                continue;
            }

            for (var j = 0; j < a.Images.Count; j++)
            {
                var (aw, ah, af) = a.Images[j];
                var (bw, bh, bf) = b.Images[j];

                if (aw != bw || ah != bh)
                {
                    problems.Add(
                        $"Page {a.PageNumber} image {j + 1} changed from {aw}x{ah} to {bw}x{bh} pixels — it was re-encoded.");
                }
                else if (!string.Equals(af, bf, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"Page {a.PageNumber} image {j + 1} changed compression from {af} to {bf} — it was re-encoded.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Every bookmark must survive with its title, depth and destination page. A difference is a
    /// failure of the carrying, not of the source, so it fails the flatten.
    /// </summary>
    public static IReadOnlyList<string> CompareBookmarks(IReadOnlyList<Bookmark> before, IReadOnlyList<Bookmark> after)
    {
        if (before.Count != after.Count)
            return [$"Bookmarks changed: {before.Count} became {after.Count}."];

        for (var i = 0; i < before.Count; i++)
        {
            if (before[i] != after[i])
                return [$"Bookmark {i + 1} changed: '{before[i]}' became '{after[i]}'."];
        }

        return [];
    }

    /// <summary>
    /// Every link that opened a page must still open the same page. Before #28 a flattened copy's
    /// links led to detached copies of their pages: 2235_lg's 141 links all did.
    /// </summary>
    public static IReadOnlyList<string> CompareLinks(
        IReadOnlyList<(int OnPage, int? Opens)> before, IReadOnlyList<(int OnPage, int? Opens)> after)
    {
        if (before.Count != after.Count)
            return [$"Links changed: {before.Count} became {after.Count}."];

        for (var i = 0; i < before.Count; i++)
        {
            if (before[i] != after[i])
            {
                var opens = after[i].Opens is { } page ? $"page {page}" : "a page outside the document";
                return [$"A link on page {before[i].OnPage} that opened page {before[i].Opens} now opens {opens}."];
            }
        }

        return [];
    }

    /// <summary>
    /// The catalog entries the source has and the copy does not, each with the reason it was left
    /// behind, or a plain statement that it was lost when there is no reason.
    /// </summary>
    private static IReadOnlyList<string> DroppedEntries(string sourcePath, string outputPath)
    {
        static HashSet<string> Keys(string path)
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return [.. document.Internals.Catalog.Elements.Keys];
        }

        var kept = Keys(outputPath);
        return Keys(sourcePath)
            .Where(key => key is not "/Type" and not "/Pages" && !kept.Contains(key))
            .Order(StringComparer.Ordinal)
            .Select(key => CatalogCarrier.LeftBehind.TryGetValue(key, out var reason)
                ? $"{key} left behind: {reason}."
                : $"{key} was lost.")
            .ToList();
    }
}
