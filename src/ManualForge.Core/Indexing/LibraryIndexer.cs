using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace ManualForge.Core.Indexing;

public sealed class IndexOptions
{
    /// <summary>Where the index lives. Defaults to one beside the job state.</summary>
    public string? IndexPath { get; init; }

    /// <summary>Folders excluded from indexing, matching the processor's own exclusions.</summary>
    public IReadOnlyList<string> ExcludedFolderNames { get; init; } = ["BASELINE", "_Originals", "_GroundTruth"];

    /// <summary>Re-index every document even if its content has not changed.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Also write the plain-text sidecar next to the index, one file per document.
    ///
    /// The index is what answers questions; the sidecars are what let something else — a grep, a
    /// different tool, a future me — work over the same text without this application.
    /// </summary>
    public string? SidecarDirectory { get; init; }
}

public sealed record IndexProgress(string Path, int DocumentsDone, int DocumentsTotal, int PagesIndexed);

public sealed record IndexReport(
    int DocumentsIndexed,
    int DocumentsUnchanged,
    int DocumentsFailed,
    int DocumentsWithoutText,
    long PagesIndexed,
    TimeSpan Elapsed)
{
    public double PagesPerMinute => Elapsed.TotalMinutes <= 0 ? 0 : PagesIndexed / Elapsed.TotalMinutes;
}

/// <summary>
/// Walks a library and puts every page of every document into the search index.
///
/// <para>
/// This reads the finished PDFs rather than anything the OCR pipeline produced, and that is
/// deliberate. Most of a library is never OCR'd by this application at all — three quarters of the
/// corpus it was built against already had a good text layer and was correctly left alone — so an
/// index fed from recognition results would cover only the quarter that needed work. Reading the
/// files means the index describes the library rather than the run.
/// </para>
/// </summary>
public sealed class LibraryIndexer(ILogger<LibraryIndexer>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<IndexReport> IndexAsync(
        string root,
        IndexOptions? options = null,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        options ??= new IndexOptions();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var indexPath = options.IndexPath ?? DefaultIndexPath(root);

        using var index = new SearchIndex(indexPath);

        var files = Discover(root, options).ToArray();
        var indexed = 0;
        var unchanged = 0;
        var failed = 0;
        var withoutText = 0;
        long pages = 0;

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);

                if (!options.Force && index.ContentHashOf(path) == hash)
                {
                    unchanged++;
                    continue;
                }

                var text = ExtractPages(path, cancellationToken);
                if (text.Count == 0 || text.All(t => t.IsEmpty))
                {
                    // An image-only document that was never OCR'd has nothing to index. Recording
                    // it anyway means a later run can tell "indexed, no text" from "never seen",
                    // and the count is worth reporting rather than hiding.
                    withoutText++;
                }

                index.AddDocument(path, Title(root, path), hash, text);
                indexed++;
                pages += text.Count(t => !t.IsEmpty);

                if (options.SidecarDirectory is not null)
                    await WriteSidecarAsync(root, path, text, options.SidecarDirectory, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Could not index {Path}", path);
            }

            progress?.Report(new IndexProgress(path, indexed + unchanged + failed, files.Length, (int)pages));
        }

        index.Optimise();
        stopwatch.Stop();

        var report = new IndexReport(indexed, unchanged, failed, withoutText, pages, stopwatch.Elapsed);

        _logger.LogInformation(
            "Indexed {Indexed} document(s) ({Unchanged} unchanged, {Failed} failed, {Empty} with no text), " +
            "{Pages} pages in {Seconds:F1}s",
            report.DocumentsIndexed, report.DocumentsUnchanged, report.DocumentsFailed,
            report.DocumentsWithoutText, report.PagesIndexed, stopwatch.Elapsed.TotalSeconds);

        return report;
    }

    /// <summary>Extracts and prepares every page of one document.</summary>
    public static IReadOnlyList<IndexedPageText> ExtractPages(
        string path, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });

        var pages = new List<IndexedPageText>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // GetWords, not Text: many PDFs position words rather than emitting spaces, and
                // the raw string comes back as one unbroken run. Lines are rebuilt from the words'
                // own vertical positions so the de-hyphenator has line ends to work with.
                pages.Add(Dehyphenator.Prepare(Lines(page)));
            }
            catch (Exception)
            {
                // One page that will not parse must not cost the other five hundred.
                pages.Add(new IndexedPageText(string.Empty, string.Empty));
            }
        }

        return pages;
    }

    /// <summary>
    /// Rebuilds a page's lines from word positions. The de-hyphenator needs to know where lines
    /// end, and that is a fact about geometry rather than about the characters.
    /// </summary>
    private static string Lines(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToArray();
        if (words.Length == 0)
            return string.Empty;

        var lines = new List<(double Top, List<string> Words)>();

        foreach (var word in words)
        {
            var top = word.BoundingBox.Top;
            var line = lines.FirstOrDefault(l => Math.Abs(l.Top - top) < 3.0);

            if (line.Words is null)
            {
                line = (top, []);
                lines.Add(line);
            }

            line.Words.Add(word.Text);
        }

        return string.Join('\n', lines
            .OrderByDescending(l => l.Top)
            .Select(l => string.Join(' ', l.Words)));
    }

    private static async Task WriteSidecarAsync(
        string root, string path, IReadOnlyList<IndexedPageText> pages,
        string directory, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        var target = Path.Combine(directory, Path.ChangeExtension(relative, ".txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var text = string.Join(
            Environment.NewLine,
            pages.Select((p, i) => $"--- page {i + 1} ---{Environment.NewLine}{p.Text}"));

        await File.WriteAllTextAsync(target, text, new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A name a person would recognise: the file name without its extension.</summary>
    private static string Title(string root, string path) => Path.GetFileNameWithoutExtension(path);

    public static string DefaultIndexPath(string root) =>
        Path.Combine(Path.GetFullPath(root), "_Originals", "manualforge-index.db");

    public static IEnumerable<string> Discover(string root, IndexOptions options)
    {
        var full = Path.GetFullPath(root);
        var excluded = options.ExcludedFolderNames
            .Select(name => Path.Combine(full, name) + Path.DirectorySeparatorChar)
            .ToArray();

        return Directory.EnumerateFiles(full, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !excluded.Any(e => f.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
