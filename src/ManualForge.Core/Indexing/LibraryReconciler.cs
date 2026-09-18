using UglyToad.PdfPig;

namespace ManualForge.Core.Indexing;

/// <summary>A PDF that is in the library but not in the index, and why.</summary>
public sealed record UnindexedFile(string Path, long SizeBytes, string Reason)
{
    /// <summary>
    /// Whether re-running the indexer would fix it. A file that has simply never been indexed will;
    /// one that cannot be opened will not, and needs a person.
    /// </summary>
    public bool FixedByReindexing { get; init; }
}

/// <summary>What the library holds against what the index holds.</summary>
public sealed record LibraryReconciliation(
    int FilesOnDisk,
    int DocumentsIndexed,
    IReadOnlyList<UnindexedFile> NotIndexed,
    IReadOnlyList<string> IndexedButGone)
{
    public bool IsClean => NotIndexed.Count == 0 && IndexedButGone.Count == 0;
}

/// <summary>
/// Accounts for the difference between the number of PDFs in a library and the number of documents
/// in its index.
///
/// <para>
/// That difference used to be reported as a bare pair of numbers — 579 present, 575 indexed — with
/// a suggestion to re-run the indexer. Which is unhelpful in the case that matters: if a file
/// cannot be opened at all, re-running the indexer will produce exactly the same two numbers, for
/// ever, and the four files stay invisible to search while the status line keeps implying the fix
/// is one command away. Naming the files and saying why is the difference between a footnote and
/// something somebody can act on.
/// </para>
/// </summary>
public static class LibraryReconciler
{
    public static LibraryReconciliation Reconcile(
        string root, SearchIndex index, IndexOptions? options = null, int examine = 25)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(index);

        options ??= new IndexOptions();

        var onDisk = LibraryIndexer.Discover(root, options)
            .ToDictionary(Path.GetFullPath, StringComparer.OrdinalIgnoreCase);

        var indexed = index.Documents()
            .Select(d => d.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var notIndexed = new List<UnindexedFile>();
        foreach (var path in onDisk.Keys.Where(p => !indexed.Contains(p)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var size = 0L;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // A file that cannot even be stat'd is still worth naming.
            }

            notIndexed.Add(notIndexed.Count < examine
                ? Diagnose(path, size)
                : new UnindexedFile(path, size, "not examined") { FixedByReindexing = true });
        }

        var gone = indexed
            .Where(p => !onDisk.ContainsKey(p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LibraryReconciliation(onDisk.Count, indexed.Count, notIndexed, gone);
    }

    /// <summary>
    /// Opens the file the way the indexer would, and reports what stopped it. Opening is the
    /// expensive part, so this is done for the first handful only — which is enough, because a
    /// reconcile gap worth chasing is a handful.
    /// </summary>
    private static UnindexedFile Diagnose(string path, long size)
    {
        if (size == 0)
        {
            return new UnindexedFile(
                path, size, "the file is empty — zero bytes. A placeholder, or a copy that failed.");
        }

        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

            if (document.NumberOfPages == 0)
                return new UnindexedFile(path, size, "opens, but has no pages");

            // Whether it reads is a question to answer by reading, not by looking at a flag. Most
            // "encrypted" PDFs in a library like this are encrypted with an empty user password to
            // stop editing, and their text comes out perfectly; reporting encryption as the reason
            // a file is missing would send somebody hunting for a password that does not exist.
            var letters = document.GetPage(1).Letters.Count;

            var encrypted = document.IsEncrypted
                ? ", and is encrypted with an empty password, which does not stop it being read"
                : string.Empty;

            return new UnindexedFile(
                path, size,
                $"reads fine — {document.NumberOfPages} pages, {letters:N0} characters on page 1{encrypted}. " +
                "It has simply never been indexed: the index is older than the file.")
            {
                FixedByReindexing = true,
            };
        }
        catch (Exception ex)
        {
            return new UnindexedFile(path, size, $"cannot be opened: {ex.Message}");
        }
    }
}
