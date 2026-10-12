using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ManualForge.Core.Auditing;
using ManualForge.Core.Indexing;
using ManualForge.Core.Rendering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Mcp;

/// <summary>
/// Serves ManualForge's view of a manual's pages on the loopback address, so a citation in Claude's
/// answer can open the page it cites (#79). <see cref="PageLinks"/> says why it is a web page.
///
/// <para>
/// It runs inside the MCP server, which is running whenever an app that cites the library is. Every
/// app with ManualForge connected starts its own server; the first takes the port and the others,
/// finding it serving the same library, hand out its links. A port held by anything else, or by a
/// server for another library, leaves links out of the tools' answers.
/// </para>
///
/// <para>
/// What it will do is narrow: answer on 127.0.0.1 only; serve PDFs inside the library and nothing
/// else; refuse a request whose Host is not this address, so a web page cannot reach the library by
/// pointing a name of its own at 127.0.0.1; and draw one page at a time.
/// </para>
/// </summary>
public sealed class PageServer(ManualLibraryContext library, PageLinks links, ILogger<PageServer>? logger = null) : IHostedService, IDisposable
{
    /// <summary>Pages are drawn for reading on a screen, not for OCR: 150 dpi, in colour.</summary>
    private static readonly RasterOptions ViewOptions = new() { Dpi = 150, Grayscale = false, MaxPixels = 30_000_000 };

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _drawing = new(1, 1);
    private HttpListener? _listener;
    private Task? _serving;
    private CachedDocument? _cached;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"{links.Origin}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            links.Enabled = await AnotherServesThisLibraryAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Page links: port {Port} is in use ({Error}); {Outcome}.", links.Port, ex.Message,
                links.Enabled ? "another ManualForge server for this library answers there, so its links are used" : "links are left out of answers");
            return;
        }

        _listener = listener;
        links.Enabled = true;
        _serving = Task.Run(ServeAsync, CancellationToken.None);
        _logger.LogInformation("Page links: serving {Library} at {Origin}.", library.Root, links.Origin);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Stop();
        if (_serving is not null)
            await _serving.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _listener?.Close();
        _drawing.Dispose();
    }

    /// <summary>Whether the server already on the port is ManualForge's, for this same library.</summary>
    private async Task<bool> AnotherServesThisLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var answer = JsonDocument.Parse(await http.GetStringAsync(new Uri($"{links.Origin}/manualforge"), cancellationToken).ConfigureAwait(false));
            return answer.RootElement.TryGetProperty("library", out var root)
                   && string.Equals(root.GetString(), library.Root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        while (_listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var answer = await RespondAsync(context.Request.HttpMethod, context.Request.Headers["Host"], context.Request.Url?.AbsolutePath ?? "/").ConfigureAwait(false);
            response.StatusCode = answer.Status;
            response.ContentType = answer.ContentType;
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Cache-Control"] = "no-store";
            if (answer.ContentType.StartsWith("text/html", StringComparison.Ordinal))
                response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'";
            if (answer.FileName is not null)
                response.Headers["Content-Disposition"] = $"inline; filename=\"{answer.FileName.Replace("\"", "", StringComparison.Ordinal)}\"";

            if (answer.File is not null)
            {
                await using var file = File.OpenRead(answer.File);
                response.ContentLength64 = file.Length;
                if (context.Request.HttpMethod != "HEAD")
                    await file.CopyToAsync(response.OutputStream).ConfigureAwait(false);
            }
            else
            {
                response.ContentLength64 = answer.Body.Length;
                if (context.Request.HttpMethod != "HEAD")
                    await response.OutputStream.WriteAsync(answer.Body).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser went away mid-answer.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Page links: answering {Path} failed.", context.Request.Url?.AbsolutePath);
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>One answer, from what the request asked for. Separate from the listener so it can be tested.</summary>
    public async Task<Answer> RespondAsync(string method, string? host, string absolutePath)
    {
        if (method is not ("GET" or "HEAD"))
            return Answer.Text(405, "Only GET and HEAD.");

        // DNS rebinding: a web page that points a name of its own at 127.0.0.1 is still asking for
        // that name, and only this address is answered.
        if (!string.Equals(host, $"127.0.0.1:{links.Port}", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, $"localhost:{links.Port}", StringComparison.OrdinalIgnoreCase))
            return Answer.Text(403, "ManualForge answers only requests addressed to 127.0.0.1.");

        if (absolutePath == "/manualforge")
        {
            var about = JsonSerializer.Serialize(new { app = "manualforge", library = library.Root, version = Core.Diagnostics.BuildStamp.Version });
            return new Answer(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(about));
        }

        if (!PageLinks.TryParse(absolutePath, out var relative, out var page, out var picture))
            return Answer.Page(404, "Not found", "<p>That is not a link to a manual. Links to pages come from Claude's answers.</p>");

        var path = library.Resolve(relative);
        if (path is null)
        {
            return Answer.Page(404, "Not found",
                $"<p><b>{Html(relative)}</b> is not in the library. It may have been moved, renamed or deleted since the link was made.</p>");
        }

        if (page is null)
            return new Answer(200, "application/pdf", [], path, Path.GetFileName(path));

        await _drawing.WaitAsync().ConfigureAwait(false);
        try
        {
            var document = Document(path);
            if (page > document.PageCount)
            {
                return Answer.Page(404, "No such page",
                    $"<p><b>{Html(Path.GetFileName(path))}</b> has {document.PageCount:N0} pages, so there is no page {page}.</p>");
            }

            if (picture)
            {
                using var raster = new PageRasteriser(ViewOptions).Render(document.Bytes, page.Value - 1);
                return new Answer(200, "image/png", raster.EncodePng());
            }

            return new Answer(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(View(relative, path, page.Value, document)));
        }
        catch (PageUnreadableException ex)
        {
            return Answer.Page(500, "Cannot draw this page", $"<p>{Html(ex.Message)}</p>");
        }
        finally
        {
            _drawing.Release();
        }
    }

    /// <summary>The page view: where it is, the page drawn, and the text search has for it.</summary>
    private string View(string relative, string path, int page, CachedDocument document)
    {
        var name = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(relative);
        var previous = page > 1 ? links.ForPage(relative, page - 1) : null;
        var next = page < document.PageCount ? links.ForPage(relative, page + 1) : null;

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{Html(name)}, page {page}</title>
            <style>{Style}</style>
            </head>
            <body>
            <header>
              <div class="where"><span class="name">{Html(name)}</span>{(string.IsNullOrEmpty(folder) ? "" : $"<span class=\"folder\">{Html(folder)}</span>")}</div>
              <nav>
                {(previous is null ? "<span class=\"off\">&lsaquo; Previous</span>" : $"<a href=\"{Html(previous)}\" rel=\"prev\">&lsaquo; Previous</a>")}
                <span class="count">Page {page} of {document.PageCount:N0}</span>
                {(next is null ? "<span class=\"off\">Next &rsaquo;</span>" : $"<a href=\"{Html(next)}\" rel=\"next\">Next &rsaquo;</a>")}
                <a href="{Html(links.ForDocument(relative))}#page={page}">Whole PDF</a>
              </nav>
            </header>
            <main>
              <a href="{Html(links.ForPicture(relative, page))}" title="The page at full size"><img src="{Html(links.ForPicture(relative, page))}" alt="Page {page} of {Html(name)}"></a>
            """);

        if (document.Recovered.TryGetValue(page, out var repair) && repair.OcrText.Length > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"""
                  <section class="recovered">
                    <h2>Recovered by ManualForge</h2>
                    <p class="note">Read off the picture of the page by OCR, at {repair.MeanConfidence * 100:F0}% mean confidence: the PDF's own text does not hold it. Check it against the page, especially model numbers and command names.</p>
                    <pre{(IsLabels(repair.OcrText) ? " class=\"labels\"" : "")}>{Html(repair.OcrText)}</pre>
                  </section>
                """);
        }

        var embedded = document.Text(page);
        html.Append(CultureInfo.InvariantCulture, $"""
              <details>
                <summary>The PDF's own text on this page</summary>
                {(embedded.Length == 0 ? "<p class=\"note\">None: the PDF has no text on this page.</p>" : $"<pre>{Html(embedded)}</pre>")}
              </details>
            </main>
            {Script}
            </body>
            </html>
            """);

        return html.ToString();
    }

    /// <summary>The manual a page view is drawn from, kept while consecutive requests are for the same one.</summary>
    private CachedDocument Document(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_cached is { } cached && cached.Path == path && cached.Stamp == stamp)
            return cached;

        _cached = new CachedDocument(path, stamp, File.ReadAllBytes(path), library);
        return _cached;
    }

    private sealed class CachedDocument(string path, DateTime stamp, byte[] bytes, ManualLibraryContext library)
    {
        private IReadOnlyList<IndexedPageText>? _text;
        private IReadOnlyDictionary<int, PageRepair>? _recovered;
        private int? _pageCount;

        public string Path { get; } = path;
        public DateTime Stamp { get; } = stamp;
        public byte[] Bytes { get; } = bytes;
        public int PageCount => _pageCount ??= PageRasteriser.GetPageCount(Path);
        public IReadOnlyDictionary<int, PageRepair> Recovered => _recovered ??= library.Recovered(Path);

        public string Text(int page)
        {
            try
            {
                _text ??= LibraryIndexer.ExtractPages(Path);
            }
            catch (Exception)
            {
                _text = [];
            }

            return page <= _text.Count ? _text[page - 1].Text : string.Empty;
        }
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    /// <summary>
    /// Whether recovered text is a drawing's labels, one short line each, rather than prose. Labels
    /// are flowed into columns: one to a line, the 432A's block diagram ran to three screens.
    /// </summary>
    public static bool IsLabels(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 12 && lines.Select(l => l.Length).Order().ElementAt(lines.Length / 2) <= 16;
    }

    /// <summary>The arrow keys turn the page.</summary>
    private const string Script = """
        <script>
        document.addEventListener('keydown', function (e) {
          if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
          var link = document.querySelector(e.key === 'ArrowLeft' ? 'a[rel=prev]' : e.key === 'ArrowRight' ? 'a[rel=next]' : null);
          if (link) location.href = link.href;
        });
        </script>
        """;

    private const string Style = """
        :root { --bg: #f4f2ee; --paper: #ffffff; --fg: #1d1c1a; --muted: #6b665e; --line: #d9d4ca; --accent: #8a4b14; --ocr: #fff4dc; --ocr-line: #e6c98a; color-scheme: light; }
        @media (prefers-color-scheme: dark) { :root { --bg: #1b1a18; --paper: #26241f; --fg: #ece8e1; --muted: #a39d92; --line: #3a372f; --accent: #e0a46a; --ocr: #33291a; --ocr-line: #6b5530; color-scheme: dark; } }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
        header { position: sticky; top: 0; z-index: 1; display: flex; flex-wrap: wrap; gap: 8px 24px; align-items: baseline; justify-content: space-between; padding: 10px 16px; background: var(--bg); border-bottom: 1px solid var(--line); }
        .where { display: flex; flex-wrap: wrap; gap: 4px 12px; align-items: baseline; min-width: 0; }
        .name { font-weight: 600; overflow-wrap: anywhere; }
        .folder { color: var(--muted); font-size: 13px; overflow-wrap: anywhere; }
        nav { display: flex; flex-wrap: wrap; gap: 4px 18px; align-items: baseline; font-variant-numeric: tabular-nums; }
        nav a { color: var(--accent); text-decoration: none; }
        nav a:hover { text-decoration: underline; }
        nav .off { color: var(--muted); opacity: 0.5; }
        nav .count { color: var(--muted); }
        main { max-width: 1100px; margin: 0 auto; padding: 16px; display: grid; gap: 16px; }
        main > a { display: block; }
        img { display: block; width: 100%; height: auto; background: #fff; border: 1px solid var(--line); box-shadow: 0 1px 4px rgb(0 0 0 / 0.15); }
        section.recovered { background: var(--ocr); border: 1px solid var(--ocr-line); border-radius: 6px; padding: 12px 16px; }
        h2 { font-size: 15px; margin: 0 0 4px; }
        .note { color: var(--muted); font-size: 13px; margin: 0 0 8px; }
        details { background: var(--paper); border: 1px solid var(--line); border-radius: 6px; padding: 10px 16px; }
        summary { cursor: pointer; color: var(--muted); }
        pre.labels { columns: 12ch auto; column-gap: 24px; }
        pre { margin: 8px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.45 Consolas, "Cascadia Mono", monospace; }
        """;

    /// <summary>What to send back.</summary>
    public sealed record Answer(int Status, string ContentType, byte[] Body, string? File = null, string? FileName = null)
    {
        public static Answer Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

        public static Answer Page(int status, string title, string body) => new(status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
            $"<style>{Style}</style></head><body><main><h2>{WebUtility.HtmlEncode(title)}</h2>{body}</main></body></html>"));
    }
}
