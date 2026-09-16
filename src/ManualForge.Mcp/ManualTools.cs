using System.ComponentModel;
using System.Text;
using ManualForge.Core.Indexing;
using ModelContextProtocol.Server;

namespace ManualForge.Mcp;

/// <summary>
/// The tools this server offers.
///
/// <para>
/// The descriptions matter as much as the code. A machine with both this and gpib-mcp installed has
/// two ways to look in the same folder of manuals, and they are good at opposite things: gpib-mcp
/// narrows by filename and is right when the model is known, this ranks by content across every page
/// and is right when it is not. If the descriptions do not say so, the choice between them is a coin
/// toss, and a wrong toss looks like "the library does not have this".
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ManualTools(ManualLibraryContext library)
{
    [McpServerTool(Name = "library_search")]
    [Description(
        "Search the FULL TEXT of every page of every manual in the local library, ranked by relevance. " +
        "Use this when you do NOT already know which manual holds the answer - a question about a " +
        "technique, a signal name, a part number, or anything where no filename would give it away. " +
        "Returns the manual, the page number and a snippet; follow up with read_manual_page to read " +
        "the surrounding text before answering, and always cite the manual and page. " +
        "If you already know the instrument model, gpib-mcp's manual_search is the better tool: it " +
        "narrows by model first and returns longer passages. This one is the fallback for when that " +
        "finds nothing, because it looks at all ~100,000 pages rather than the best dozen filenames.")]
    public string LibrarySearch(
        [Description("What to look for, e.g. 'HP-IB handshake lines' or 'YIG oscillator adjustment'. " +
                     "Ordinary words; quoting is handled. AND, OR, NOT and NEAR( work if you want them.")]
        string query,
        [Description("Results to return. Default 8, maximum 30.")] int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Give something to search for.";

        if (!library.HasIndex)
            return library.MissingIndexAdvice();

        using var index = library.OpenIndex();
        var hits = index.Search(query, Math.Clamp(limit, 1, 30));

        if (hits.Count == 0)
        {
            var statistics = index.Statistics();
            return $"Nothing in the library matched {query}. " +
                   $"The index covers {statistics.Documents:N0} manuals and {statistics.Pages:N0} pages, " +
                   "so this is a real absence rather than a truncated search - though a manual that was " +
                   "never OCR'd has no text to match. Try different words before concluding it is not there.";
        }

        var text = new StringBuilder();
        text.AppendLine($"{hits.Count} result(s) for {query}:");
        text.AppendLine();

        foreach (var hit in hits)
        {
            text.AppendLine($"--- {hit.Title}, page {hit.PageNumber} ---");
            text.AppendLine(hit.Snippet);
            text.AppendLine($"    path: {library.Relative(hit.Path)}");

            if (hit.HasDuplicates)
                text.AppendLine($"    (the same document is also at {hit.AlsoAt.Count} other path(s))");

            text.AppendLine();
        }

        text.AppendLine(
            "These are snippets, not answers. Read the page with read_manual_page before telling the " +
            "user what it says, and cite the manual and page.");

        return text.ToString();
    }

    [McpServerTool(Name = "read_manual_page")]
    [Description(
        "Read the full text of one page of a manual, optionally with the pages either side. " +
        "Use this after library_search to read the passage properly: a snippet is enough to choose a " +
        "page, never enough to answer from. The text is what the PDF's own text layer holds, so on an " +
        "OCR'd scan it carries OCR errors - quote it as what the manual appears to say.")]
    public string ReadManualPage(
        [Description("The manual, as the path returned by library_search.")] string path,
        [Description("Page number, 1-based, as returned by library_search.")] int page,
        [Description("Also include this many pages either side. Default 0, maximum 3.")] int context = 0)
    {
        var resolved = library.Resolve(path);
        if (resolved is null)
            return $"'{path}' is not a file inside the manual library.";

        context = Math.Clamp(context, 0, 3);

        IReadOnlyList<IndexedPageText> pages;
        try
        {
            pages = LibraryIndexer.ExtractPages(resolved);
        }
        catch (Exception ex)
        {
            return $"Could not read {path}: {ex.Message}";
        }

        if (page < 1 || page > pages.Count)
            return $"{path} has {pages.Count} pages; {page} is outside it.";

        var first = Math.Max(1, page - context);
        var last = Math.Min(pages.Count, page + context);

        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(resolved)}, page {page} of {pages.Count:N0}");
        text.AppendLine();

        for (var n = first; n <= last; n++)
        {
            if (first != last)
                text.AppendLine($"--- page {n} ---");

            text.AppendLine(pages[n - 1].IsEmpty ? "(no text on this page)" : pages[n - 1].Text);
            text.AppendLine();
        }

        return text.ToString();
    }

    [McpServerTool(Name = "library_status")]
    [Description(
        "Report what the manual library holds and how current the search index is. " +
        "Worth checking before concluding something is not in the library: an index built before a " +
        "manual was added, or before an image-only scan was OCR'd, will not find it.")]
    public string LibraryStatus()
    {
        if (!library.HasIndex)
            return library.MissingIndexAdvice();

        using var index = library.OpenIndex();
        var statistics = index.Statistics();
        var age = DateTime.Now - File.GetLastWriteTime(library.IndexPath);

        var pdfCount = 0;
        try
        {
            pdfCount = LibraryIndexer.Discover(library.Root, new IndexOptions()).Count();
        }
        catch (Exception)
        {
            // Counting the folder is a nicety; failing to must not fail the report.
        }

        var text = new StringBuilder();
        text.AppendLine($"Library: {library.Root}");
        text.AppendLine($"Indexed: {statistics.Documents:N0} manuals, {statistics.Pages:N0} pages, " +
                        $"{statistics.SizeBytes / 1024.0 / 1024.0:F0} MB");
        text.AppendLine($"Built  : {age.TotalHours:F1} hours ago");

        if (pdfCount > statistics.Documents)
        {
            text.AppendLine(
                $"NOTE   : the folder holds {pdfCount:N0} PDFs but only {statistics.Documents:N0} are indexed. " +
                "Run `manualforge index` to pick up the rest.");
        }

        return text.ToString();
    }
}
