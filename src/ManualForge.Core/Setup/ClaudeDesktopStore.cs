using System.Text.RegularExpressions;

namespace ManualForge.Core.Setup;

/// <summary>
/// Where the Microsoft Store (MSIX) build of Claude Desktop keeps its settings (#98). Anthropic doesn't
/// document it, and it has moved. Older builds use the package's virtualised AppData,
/// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\Claude</c>. Current builds move their data to
/// <c>%LOCALAPPDATA%\Claude-Data\&lt;family, lower case&gt;</c> when they update, run with <c>--user-data-dir</c>
/// pointing there, and leave a <c>.user-data-left.&lt;family&gt;</c> marker in the old folder, whose config they
/// no longer read. The details are from anthropics/claude-code#26073 and from Claude Desktop 6.6395 on a real
/// PC; GPIB MCP finds it the same way (TGoodhew/GPIB-MCP#175).
/// </summary>
public static partial class ClaudeDesktopStore
{
    /// <summary>The package names Claude Desktop is published under; a family name is "&lt;name&gt;_&lt;publisher id&gt;".</summary>
    private static readonly string[] PackageNames = ["Claude", "AnthropicPBC.Claude"];

    /// <summary>Overrides the data folder, when set. Claude Desktop reads it itself.</summary>
    public const string UserDataVariable = "CLAUDE_USER_DATA_DIR";

    private const string MovedMarker = ".user-data-left.";

    [GeneratedRegex(@"--user-data-dir=(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDir();

    /// <summary>
    /// Finds the Store build's settings folder; false, with a null folder, when the Store build isn't
    /// installed. In order:
    /// 1. a running Store Claude Desktop's own <c>--user-data-dir</c>: the folder it is using right now;
    /// 2. <c>CLAUDE_USER_DATA_DIR</c>;
    /// 3. for the installed package, <c>Claude-Data\&lt;family&gt;</c> when it exists or the old folder's marker
    ///    says the data moved, else the old <c>LocalCache</c> folder: an older build that hasn't moved yet, and
    ///    the move takes the config with it.
    /// </summary>
    public static bool TryFind(McpEnvironment environment, out string? settingsDir)
    {
        ArgumentNullException.ThrowIfNull(environment);
        settingsDir = null;

        var installed = InstalledFamilies(environment);
        var running = RunningDataDir(environment);
        if (installed.Count == 0 && running is null)
            return false;

        settingsDir = running ?? Overridden(environment) ?? SettingsFor(environment, installed[0]);
        return true;
    }

    /// <summary>The installed Store packages' family names, from their folders under %LOCALAPPDATA%\Packages.</summary>
    internal static List<string> InstalledFamilies(McpEnvironment environment)
    {
        var packages = Path.Combine(environment.LocalAppData, "Packages");
        if (!Directory.Exists(packages))
            return [];

        return PackageNames
            .SelectMany(name => Directory.GetDirectories(packages, name + "_*"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsFamily)
            .ToList();
    }

    /// <summary>"Claude_pzs8sxrjxfjjc", not "Claude_Something_Else": a package name, then one publisher id.</summary>
    private static bool IsFamily(string folder)
    {
        var split = folder.LastIndexOf('_');
        return split > 0 && PackageNames.Contains(folder[..split], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The data folder of a Store Claude Desktop that is running now, or null.</summary>
    internal static string? RunningDataDir(McpEnvironment environment)
    {
        IReadOnlyList<RunningProcess> processes;
        try
        {
            processes = environment.RunningClaude();
        }
        catch (Exception)
        {
            return null;   // only a better answer than the files; never a reason to fail
        }

        foreach (var process in processes)
        {
            if (FamilyOf(process.ExecutablePath) is null)
                continue;   // Claude Code is claude.exe too

            var match = UserDataDir().Match(process.CommandLine);
            if (!match.Success)
                continue;

            var dir = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (dir.Length > 0)
                return dir.TrimEnd('\\');
        }

        return null;
    }

    /// <summary>
    /// The family name of the Store package a program runs from, or null when it isn't one. The program is at
    /// <c>...\WindowsApps\&lt;name&gt;_&lt;version&gt;_&lt;arch&gt;_&lt;resource&gt;_&lt;publisher id&gt;\...</c>, and the
    /// family is <c>&lt;name&gt;_&lt;publisher id&gt;</c>.
    /// </summary>
    internal static string? FamilyOf(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return null;

        var parts = executablePath.Split('\\', '/');
        var apps = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (apps < 0 || apps + 1 >= parts.Length)
            return null;

        var full = parts[apps + 1].Split('_');
        if (full.Length < 5)
            return null;

        var family = full[0] + "_" + full[^1];
        return IsFamily(family) ? family : null;
    }

    private static string? Overridden(McpEnvironment environment) =>
        environment.Variable(UserDataVariable) is { } dir && !string.IsNullOrWhiteSpace(dir) ? dir.Trim().Trim('"') : null;

    /// <summary>Where a package's settings are when the app isn't running to say.</summary>
    internal static string SettingsFor(McpEnvironment environment, string family)
    {
        var moved = Path.Combine(environment.LocalAppData, "Claude-Data", family.ToLowerInvariant());
        var old = Path.Combine(environment.LocalAppData, "Packages", family, "LocalCache", "Roaming", "Claude");
        var left = Directory.Exists(old) && Directory.EnumerateFiles(old, MovedMarker + "*").Any();
        return Directory.Exists(moved) || left ? moved : old;
    }
}
