using ManualForge.Core.Auditing;
using ManualForge.Core.Indexing;

namespace ManualForge.Mcp;

/// <summary>
/// Which library this server serves, and how to get at its index.
///
/// The folder comes from <c>MANUALFORGE_LIBRARY</c>, matching how gpib-mcp takes
/// <c>GPIB_MCP_MANUALS</c> — the same machine will often point both at the same folder, and two
/// conventions for one idea would be a trap.
/// </summary>
public sealed class ManualLibraryContext
{
    private ManualLibraryContext(string root) => Root = root;

    public string Root { get; }

    public string IndexPath => LibraryIndexer.DefaultIndexPath(Root);

    public bool HasIndex => File.Exists(IndexPath);

    /// <summary>Where `manualforge doctor` keeps what it found, and what the repair recovered.</summary>
    public string DoctorPath => DoctorStore.DefaultPathFor(Root);

    public bool HasAudit => File.Exists(DoctorPath);

    public DoctorStore OpenAudit() => new(DoctorPath, readOnly: true);

    /// <summary>
    /// The audit's standing, or null when there is none. Kept separate from the index because a
    /// search result has to be able to say "this library has never been checked for pages whose
    /// text layer is incomplete" — which is a different, and more honest, answer than silence.
    /// </summary>
    public AuditSummary? AuditSummary()
    {
        if (!HasAudit)
            return null;

        try
        {
            using var store = OpenAudit();
            return store.Summary();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The configured library, or null when the variable is unset or points nowhere.</summary>
    public static ManualLibraryContext? FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("MANUALFORGE_LIBRARY");
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var root = configured.Trim().Trim('"');
        return Directory.Exists(root) ? new ManualLibraryContext(Path.GetFullPath(root)) : null;
    }

    public static ManualLibraryContext At(string root) => new(Path.GetFullPath(root));

    public SearchIndex OpenIndex() => new(IndexPath, readOnly: true);

    /// <summary>A path relative to the library, which is what a tool result should quote.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path);

    /// <summary>
    /// Resolves a path a tool was given, refusing anything outside the library.
    ///
    /// The path arrives as a tool argument, which means it arrives from a model reading text it was
    /// handed, so "..\\..\\..\\secrets.txt" has to bounce off something.
    /// </summary>
    public string? Resolve(string relativeOrFull)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFull))
            return null;

        var candidate = relativeOrFull.Trim().Trim('"');

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(Root, candidate));
        }
        catch (Exception)
        {
            return null;
        }

        var inside = full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);

        return inside && File.Exists(full) ? full : null;
    }

    /// <summary>
    /// What to say when there is no index. A tool that answers "nothing found" when it never looked
    /// is worse than one that says it cannot look.
    /// </summary>
    public string MissingIndexAdvice() =>
        $"There is no search index for {Root} yet, so this tool cannot look anything up. " +
        $"Build one with `manualforge index \"{Root}\"` — it takes a few minutes for a library of " +
        "a hundred thousand pages — or press \"Build index\" in the ManualForge application. " +
        "Until then, gpib-mcp's manual_search can still find things by model name.";
}
