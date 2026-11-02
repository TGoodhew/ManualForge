using System.Text.Json;

namespace ManualForge.Shell;

/// <summary>
/// Which library the app opens on. The installer asks for the library and records it in
/// <c>MANUALFORGE_LIBRARY</c>, so the app had the answer and still opened on an empty box.
/// </summary>
public static class StartFolder
{
    /// <summary>
    /// A folder given on the command line, so the app can be started on one; else the folder the app
    /// was last closed on; else the library the installer recorded. Each only if it is still there.
    /// </summary>
    public static string? Choose(
        IEnumerable<string> arguments, string? lastFolder, string? libraryVariable, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exists ??= Directory.Exists;

        return arguments.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && exists(a))
               ?? new[] { lastFolder, libraryVariable }.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && exists(f!));
    }
}

/// <summary>
/// The app's command line: <c>[folder] [--add file…]</c>. Everything after <c>--add</c> is a file to
/// add to the library (#89), which is how Explorer's Send To passes the selection; what comes before
/// it is what <see cref="StartFolder.Choose"/> looks at. Kept apart, because a folder sent to the
/// library would otherwise be opened as the library.
/// </summary>
public sealed record LaunchArguments(IReadOnlyList<string> Start, IReadOnlyList<string> Add)
{
    public const string AddSwitch = "--add";

    public static LaunchArguments Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var all = arguments.ToArray();
        var at = Array.FindIndex(all, a => string.Equals(a, AddSwitch, StringComparison.OrdinalIgnoreCase));
        return at < 0
            ? new LaunchArguments(all, [])
            : new LaunchArguments(all[..at], all[(at + 1)..].Where(a => !string.IsNullOrWhiteSpace(a)).ToArray());
    }
}

/// <summary>
/// What the app remembers between runs, in <c>%LOCALAPPDATA%\ManualForge\app.json</c>: for now only
/// the folder it was closed on. Unreadable or missing settings are no settings.
/// </summary>
public sealed record AppSettings(string? LastFolder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManualForge", "app.json");

    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings((string?)null)
                : new AppSettings((string?)null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings((string?)null);
        }
    }

    /// <summary>Writes the settings; a failure to remember a folder is not worth stopping the app over.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
