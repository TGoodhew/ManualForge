namespace ManualForge.Mcp;

/// <summary>
/// Links to ManualForge's own view of a page, served on this PC by <see cref="PageServer"/> (#79).
///
/// <para>
/// A citation in Claude Desktop cannot point at the PDF itself: the app drops <c>file:</c> links,
/// and links to any other registered scheme, from what it shows. It keeps <c>http</c> links and
/// opens them in the browser, so the server answers on the loopback address and the links point
/// there. They open at the cited page whatever the PDF viewer is: a <c>#page=23</c> on the PDF is
/// honoured by Edge's own viewer and ignored by Acrobat's extension for it, which is what opened
/// when this was tried.
/// </para>
///
/// <para>
/// A link carries the manual's path within the library and nothing above it, so no user name or
/// folder outside the library reaches the model.
/// </para>
/// </summary>
public sealed class PageLinks(int port)
{
    /// <summary>The port when <c>MANUALFORGE_PAGE_PORT</c> does not say otherwise.</summary>
    public const int DefaultPort = 47332;

    public int Port { get; } = port;

    /// <summary>
    /// Whether a server for this library is answering, here or in another ManualForge process. Tools
    /// leave links out when it is not, rather than hand Claude links that go nowhere.
    /// </summary>
    public bool Enabled { get; set; }

    public string Origin => $"http://127.0.0.1:{Port}";

    /// <summary>The page view: the page drawn, Previous and Next, and the text search has for it.</summary>
    public string ForPage(string relativePath, int page) => $"{ForDocument(relativePath)}/page/{page}";

    /// <summary>The page drawn, as PNG.</summary>
    public string ForPicture(string relativePath, int page) => $"{ForPage(relativePath, page)}.png";

    /// <summary>The whole PDF.</summary>
    public string ForDocument(string relativePath) => $"{Origin}/manuals/{Escape(relativePath)}";

    /// <summary>The port configured by <c>MANUALFORGE_PAGE_PORT</c>; null when it is set to <c>off</c>.</summary>
    public static int? ConfiguredPort()
    {
        var configured = Environment.GetEnvironmentVariable("MANUALFORGE_PAGE_PORT")?.Trim();
        if (string.IsNullOrEmpty(configured))
            return DefaultPort;

        if (string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(configured, out var port) && port is > 0 and < 65536 ? port : DefaultPort;
    }

    /// <summary>
    /// Reads a request's path: a manual, and perhaps a page of it, and whether the picture was asked
    /// for. False for anything else.
    /// </summary>
    /// <param name="absolutePath">The path as the request gives it, still escaped.</param>
    public static bool TryParse(string absolutePath, out string relativePath, out int? page, out bool picture)
    {
        relativePath = string.Empty;
        page = null;
        picture = false;

        const string prefix = "/manuals/";
        if (absolutePath is null || !absolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var segments = absolutePath[prefix.Length..].Split('/');
        if (segments.Length >= 3 && segments[^2] == "page")
        {
            var number = segments[^1];
            if (number.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                picture = true;
                number = number[..^4];
            }

            if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
                return false;

            page = n;
            segments = segments[..^2];
        }

        if (segments.Length == 0 || segments.Any(s => s.Length == 0))
            return false;

        relativePath = string.Join(Path.DirectorySeparatorChar, segments.Select(Uri.UnescapeDataString));
        return relativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string relativePath) =>
        string.Join('/', relativePath.Split('\\', '/').Select(Uri.EscapeDataString));
}
