using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ManualForge.Core.Auditing;
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
        "Results say where their text came from: a hit marked OCR was recovered from a rendered page " +
        "and may carry recognition errors, which matters if you are about to send it to an instrument. " +
        "If you already know the instrument model, gpib-mcp's manual_search is the better tool: it " +
        "narrows by model first and returns longer passages. This one is the fallback for when that " +
        "finds nothing, because it looks at all ~100,000 pages rather than the best dozen filenames.")]
    public string LibrarySearch(
        [Description("What to look for, e.g. 'HP-IB handshake lines' or 'YIG oscillator adjustment'. " +
                     "Ordinary words; quoting is handled. Command syntax can be pasted straight in - " +
                     "':TRIGger:MODE {EDGE|GLITch}' is read as the notation it is. " +
                     "AND, OR, NOT and NEAR( work if you want them.")]
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
            return NothingFound(query, index.Statistics());

        var text = new StringBuilder();
        text.AppendLine($"{hits.Count} result(s) for {query}:");
        text.AppendLine();

        var anyOcr = false;

        foreach (var hit in hits)
        {
            var marker = hit.MatchSource switch
            {
                TextSource.Ocr => "  [OCR]",
                TextSource.Mixed => "  [part OCR]",
                _ => string.Empty,
            };

            text.AppendLine($"--- {hit.Title}, page {hit.PageNumber}{marker} ---");
            text.AppendLine(hit.Snippet);
            text.AppendLine($"    path: {library.Relative(hit.Path)}");

            if (hit.MatchedOcrText)
            {
                anyOcr = true;
                text.AppendLine(
                    $"    what matched here was recovered by OCR from the rendered page, at " +
                    $"{hit.OcrConfidence:P0} mean confidence — the PDF's own text layer does not hold it, " +
                    "so read it as what the page appears to say.");
            }

            if (hit.HasDuplicates)
                text.AppendLine($"    (the same document is also at {hit.AlsoAt.Count} other path(s))");

            text.AppendLine();
        }

        text.AppendLine(
            "These are snippets, not answers. Read the page with read_manual_page before telling the " +
            "user what it says, and cite the manual and page.");

        if (anyOcr)
        {
            text.AppendLine(
                "At least one result above came from recognised text rather than the PDF's own. " +
                "Character confusions in this genre land in model numbers and command names — 0/O, " +
                "1/l/I, 5/S, 8/B — so quote those as what the page appears to say, and say where it " +
                "came from if the user is going to send it to an instrument.");
        }

        return text.ToString();
    }

    /// <summary>
    /// What to say when nothing matched.
    ///
    /// <para>
    /// This used to assert that a miss was "a real absence rather than a truncated search". That
    /// claim was wrong in exactly the case it mattered: a manual can carry a text layer, and so
    /// never be a candidate for OCR, while most of its content is drawn as vector graphics and
    /// extracts as nothing. The 54845A Programmer's Guide was in this library the whole time its
    /// commands were being reported as absent. So the claim is now conditional on the audit having
    /// been run and its findings having been repaired, and where they have not been, the suspect
    /// documents are named.
    /// </para>
    /// </summary>
    private string NothingFound(string query, IndexStatistics statistics)
    {
        var text = new StringBuilder();
        text.AppendLine($"Nothing in the library matched {query}.");
        text.AppendLine(
            $"The index covers {statistics.Documents:N0} manuals and {statistics.Pages:N0} pages.");

        var summary = library.AuditSummary();

        if (summary is null)
        {
            text.AppendLine();
            text.AppendLine(
                "This is NOT evidence that the library does not cover it. Nothing here has been " +
                "checked for the failure that matters: a PDF can carry a text layer for its prose " +
                "and draw its syntax diagrams, pin-outs and schematics as vector graphics, which " +
                "extract as nothing at all. Such a manual is indexed, looks healthy, and is silent " +
                "about most of its technical content — and the parts that do not extract are " +
                "disproportionately what anybody searches a service manual for.");
            text.AppendLine(
                $"Run `manualforge doctor \"{library.Root}\"` to find out how much of this library is " +
                "in that state. Until then, treat a miss as unproven.");
            return text.ToString();
        }

        if (summary.OutstandingPages > 0)
        {
            text.AppendLine();
            text.AppendLine(
                $"This is NOT a real absence yet. The audit has found {summary.OutstandingPages:N0} " +
                $"page(s) across {summary.DocumentsFlagged:N0} document(s) whose text layer is " +
                "incomplete — legible content is on the page and no text in the file holds it — and " +
                "those pages have not been repaired, so nothing on them can match any query.");
            text.AppendLine(
                $"Of those, {summary.DrawnPages:N0} are pages whose content is drawn as vector " +
                "graphics and never had a text layer at all — syntax diagrams, pin-outs, schematic " +
                "labels. The rest are lettering inside scanned images that an earlier OCR missed.");

            var outstanding = Outstanding();
            if (outstanding.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Worst affected, and worth checking by eye before concluding anything:");
                foreach (var document in outstanding)
                {
                    var kind = document.DrawnPages > 0
                        ? $"{document.DrawnPages:N0} of them drawn rather than typeset"
                        : "lettering inside scanned images";

                    text.AppendLine(
                        $"  {library.Relative(document.Path)} — {document.OutstandingPages:N0} of " +
                        $"{document.PageCount:N0} pages ({kind}), about " +
                        $"{document.RecoverableCharacters:N0} characters not indexed");
                }
            }

            text.AppendLine();
            text.AppendLine(
                $"`manualforge repair \"{library.Root}\"` recovers the drawn ones — add " +
                "`--include-scans` for the rest — and `manualforge index` puts the result in the index.");
            return text.ToString();
        }

        text.AppendLine();
        text.AppendLine(
            $"The library has been audited for pages whose text layer is incomplete — " +
            $"{summary.DocumentsAudited:N0} document(s) checked — and every page it flagged has been " +
            "repaired and re-indexed. So this is a real absence rather than a truncated search, with " +
            "one caveat that remains: a scan that was never OCR'd has no text to match. " +
            "Try different words before concluding it is not there.");

        return text.ToString();
    }

    private IReadOnlyList<AuditedDocument> Outstanding()
    {
        try
        {
            using var store = library.OpenAudit();
            return store.Outstanding(5);
        }
        catch (Exception)
        {
            return [];
        }
    }

    [McpServerTool(Name = "read_manual_page")]
    [Description(
        "Read the full text of one page of a manual, optionally with the pages either side. " +
        "Use this after library_search to read the passage properly: a snippet is enough to choose a " +
        "page, never enough to answer from. The text is what the PDF's own text layer holds, plus " +
        "anything the repair recovered from the rendered page, which is marked separately. On an " +
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

        var recovered = Recovered(resolved);

        var first = Math.Max(1, page - context);
        var last = Math.Min(pages.Count, page + context);

        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(resolved)}, page {page} of {pages.Count:N0}");
        text.AppendLine();

        for (var n = first; n <= last; n++)
        {
            if (first != last)
                text.AppendLine($"--- page {n} ---");

            var embedded = pages[n - 1];
            var hasRecovered = recovered.TryGetValue(n, out var repair) && repair.OcrText.Length > 0;

            if (embedded.IsEmpty && !hasRecovered)
            {
                text.AppendLine("(no text on this page)");
                text.AppendLine();
                continue;
            }

            if (!embedded.IsEmpty)
                text.AppendLine(embedded.Text);

            if (hasRecovered)
            {
                text.AppendLine();
                text.AppendLine(
                    $"--- recovered by OCR from the rendered page, {repair!.MeanConfidence:P0} mean " +
                    $"confidence at {repair.Dpi} dpi; the PDF's own text layer does not hold this ---");
                text.AppendLine(repair.OcrText);
            }

            text.AppendLine();
        }

        if (recovered.Count > 0)
        {
            text.AppendLine(
                "Text under a 'recovered by OCR' heading was read off a picture of the page because " +
                "the file's own text layer did not hold it. Treat it as what the page appears to say.");
        }

        return text.ToString();
    }

    /// <summary>Text the repair recovered for a document, keyed by page. Empty when there is none.</summary>
    private IReadOnlyDictionary<int, PageRepair> Recovered(string path)
    {
        if (!library.HasAudit)
            return new Dictionary<int, PageRepair>();

        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream));

            using var store = library.OpenAudit();
            return store.Repairs(path, hash);
        }
        catch (Exception)
        {
            return new Dictionary<int, PageRepair>();
        }
    }

    [McpServerTool(Name = "library_status")]
    [Description(
        "Report what the manual library holds, how current the search index is, which files are in " +
        "the folder but not in the index and why, and whether the library has been audited for pages " +
        "whose text layer is incomplete. " +
        "Worth checking before concluding something is not in the library: an index built before a " +
        "manual was added, before an image-only scan was OCR'd, or before an under-extracted manual " +
        "was repaired, will not find it.")]
    public string LibraryStatus()
    {
        if (!library.HasIndex)
            return library.MissingIndexAdvice();

        using var index = library.OpenIndex();
        var statistics = index.Statistics();
        var age = DateTime.Now - File.GetLastWriteTime(library.IndexPath);

        var text = new StringBuilder();
        text.AppendLine($"Library: {library.Root}");
        text.AppendLine($"Indexed: {statistics.Documents:N0} manuals, {statistics.Pages:N0} pages, " +
                        $"{statistics.SizeBytes / 1024.0 / 1024.0:F0} MB");
        text.AppendLine($"Built  : {age.TotalHours:F1} hours ago");

        if (statistics.RepairedPages > 0)
        {
            text.AppendLine(
                $"Repaired: {statistics.RepairedPages:N0} page(s) in {statistics.RepairedDocuments:N0} " +
                "document(s) carry text recovered by OCR as well as the PDF's own");
        }

        AppendReconciliation(text, index);
        AppendAudit(text);

        return text.ToString();
    }

    /// <summary>
    /// Accounts for every PDF in the folder that is not in the index, by name and by reason.
    ///
    /// <para>
    /// The previous version of this reported the gap as a bare pair of numbers and advised running
    /// the indexer. For a file that cannot be opened at all, that advice produces the same two
    /// numbers for ever while the file stays invisible to search — a footnote where there should
    /// have been something to act on.
    /// </para>
    /// </summary>
    private void AppendReconciliation(StringBuilder text, SearchIndex index)
    {
        LibraryReconciliation reconciliation;
        try
        {
            reconciliation = LibraryReconciler.Reconcile(library.Root, index);
        }
        catch (Exception)
        {
            // Counting the folder is a nicety; failing to must not fail the report.
            return;
        }

        if (reconciliation.IsClean)
        {
            text.AppendLine($"Files  : all {reconciliation.FilesOnDisk:N0} PDFs in the folder are indexed.");
            return;
        }

        if (reconciliation.NotIndexed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(
                $"NOT INDEXED: {reconciliation.NotIndexed.Count:N0} of the " +
                $"{reconciliation.FilesOnDisk:N0} PDFs in the folder are not in the index, so nothing " +
                "in them can be found by any search:");

            foreach (var file in reconciliation.NotIndexed.Take(12))
            {
                text.AppendLine(
                    $"  {library.Relative(file.Path)} ({file.SizeBytes / 1024.0 / 1024.0:F1} MB)");
                text.AppendLine($"      {file.Reason}");
            }

            if (reconciliation.NotIndexed.Count > 12)
                text.AppendLine($"  ... and {reconciliation.NotIndexed.Count - 12:N0} more");

            var fixable = reconciliation.NotIndexed.Count(f => f.FixedByReindexing);
            text.AppendLine(
                fixable == reconciliation.NotIndexed.Count
                    ? $"  All of these would be picked up by `manualforge index \"{library.Root}\"`."
                    : fixable == 0
                        ? "  None of these will be fixed by re-running the indexer; each needs a look."
                        : $"  {fixable:N0} of these would be picked up by re-running the indexer; the rest need a look.");
        }

        if (reconciliation.IndexedButGone.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(
                $"STALE: {reconciliation.IndexedButGone.Count:N0} document(s) are in the index but no " +
                "longer in the folder, so a search can return a page of a file that is not there:");

            foreach (var path in reconciliation.IndexedButGone.Take(8))
                text.AppendLine($"  {library.Relative(path)}");
        }
    }

    private void AppendAudit(StringBuilder text)
    {
        var summary = library.AuditSummary();

        text.AppendLine();

        if (summary is null)
        {
            text.AppendLine(
                "AUDIT  : never run. Nothing here has been checked for pages that carry a text layer " +
                "which does not hold what is drawn on them — the failure that makes a manual look " +
                "healthy and search as though it were empty. " +
                $"`manualforge doctor \"{library.Root}\"` reports it and changes nothing.");
            return;
        }

        text.AppendLine(
            $"AUDIT  : {summary.DocumentsAudited:N0} document(s) checked; " +
            $"{summary.DocumentsFlagged:N0} have page(s) whose text layer is incomplete, " +
            $"{summary.FlaggedPages:N0} page(s) in total ({summary.DrawnPages:N0} of them drawn rather " +
            $"than photographed), of which {summary.RepairedPages:N0} have been repaired.");

        if (summary.OutstandingPages > 0)
        {
            text.AppendLine(
                $"         {summary.OutstandingPages:N0} page(s) still hold text that no search can " +
                $"reach. `manualforge repair \"{library.Root}\"` recovers it.");
        }
    }
}
