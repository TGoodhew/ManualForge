using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManualForge.Core.Setup;

/// <summary>
/// Where a user's applications keep their settings, read from the environment rather than from the
/// shell's known folders, so that a test, or a check against a scratch profile, can point every client
/// somewhere harmless.
/// </summary>
public sealed record McpEnvironment(
    string UserProfile,
    string AppData,
    string LocalAppData,
    string ProgramFiles,
    Func<string, bool> OnPath,
    Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> Run)
{
    public static McpEnvironment FromProcess() => new(
        Variable("USERPROFILE", Environment.SpecialFolder.UserProfile),
        Variable("APPDATA", Environment.SpecialFolder.ApplicationData),
        Variable("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
        Variable("ProgramFiles", Environment.SpecialFolder.ProgramFiles),
        IsOnPath,
        RunProcess);

    private static string Variable(string name, Environment.SpecialFolder fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : Environment.GetFolderPath(fallback);

    private static bool IsOnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(directory => new[] { ".exe", ".cmd", ".bat" }.Any(extension => File.Exists(Path.Combine(directory, command + extension))));

    private static (int, string) RunProcess(string command, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, output);
    }
}

/// <summary>What every client is told: run this program, with the library here.</summary>
public sealed record McpServerEntry(string Command, string Library)
{
    public const string Name = "manualforge";

    public const string LibraryVariable = "MANUALFORGE_LIBRARY";
}

/// <summary>Where a client stands with ManualForge.</summary>
public enum McpClientState
{
    NotInstalled,
    NotRegistered,
    Registered,

    /// <summary>Registered, but pointing at another program or library than the one asked for.</summary>
    Different,

    /// <summary>Its settings file could not be read, so it is left alone.</summary>
    Unreadable,
}

/// <summary>What registering or removing did to one client's settings.</summary>
public sealed record McpChange(bool Changed, string? Backup, string? Note = null);

/// <summary>
/// An application that can launch a local MCP server, and how to tell it about ManualForge (#26). Each
/// knows where its settings live, whether it is installed, and how to add and remove one entry without
/// disturbing anything else in the file.
/// </summary>
public abstract class McpClient(string id, string displayName)
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    /// <summary>The file that holds its servers, or a description of how they are kept.</summary>
    public abstract string Location { get; }

    public abstract bool IsInstalled { get; }

    public abstract McpClientState State(McpServerEntry entry);

    public abstract McpChange Register(McpServerEntry entry);

    public abstract McpChange Unregister();

    /// <summary>
    /// The <c>manualforge</c> entry as it stands, in the client's own form, or null when it has none. Taken
    /// before ManualForge changes an entry that was already there, so removing ManualForge can put it back.
    /// </summary>
    public abstract string? Snapshot();

    /// <summary>Puts back an entry <see cref="Snapshot"/> took.</summary>
    public abstract McpChange Restore(string snapshot);

    /// <summary>What to tell the user afterwards, when the client reads its settings only at start.</summary>
    public virtual string? RestartNote => null;

    /// <summary>
    /// Copies a settings file aside the first time ManualForge changes it: beside it, named for ManualForge,
    /// so the user can see whose it is and put it back. Never overwritten, so it stays the file as it was
    /// before ManualForge touched it. A first version stamped each copy with the time, and a register and
    /// a remove in the same second overwrote the only copy of the original.
    /// </summary>
    protected static string? Backup(string path)
    {
        if (!File.Exists(path))
            return null;

        var backup = BackupPathFor(path);
        if (!File.Exists(backup))
            File.Copy(path, backup);
        return backup;
    }

    protected static string BackupPathFor(string path) => path + ".before-manualforge.bak";

    /// <summary>
    /// Once ManualForge's entry is out again, the copy of the file from before it went in has nothing
    /// left to restore, and an uninstall should leave nothing behind. Kept only when it holds comments
    /// the edit could not keep, which exist nowhere else.
    /// </summary>
    protected static void DropBackup(string path)
    {
        var backup = BackupPathFor(path);
        try
        {
            if (File.Exists(backup) && !JsonMcpClient.HasComments(File.ReadAllText(backup)))
                File.Delete(backup);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A client whose servers sit in one JSON object of a settings file: <c>mcpServers</c> for Claude
/// Desktop, Cursor, Windsurf, Gemini CLI, LM Studio and Claude Code; <c>servers</c>, with a
/// <c>type</c>, for VS Code and Visual Studio.
/// </summary>
public class JsonMcpClient(
    string id, string displayName, string configPath, Func<bool> installed, string serversKey, bool typed, string? restartNote = null)
    : McpClient(id, displayName)
{
    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    public string ConfigPath { get; } = configPath;

    public override string Location => ConfigPath;

    public override bool IsInstalled => installed();

    public override string? RestartNote => restartNote;

    public override McpClientState State(McpServerEntry entry)
    {
        if (!IsInstalled)
            return McpClientState.NotInstalled;

        if (!TryRead(out var root))
            return McpClientState.Unreadable;

        if (root?[serversKey]?[McpServerEntry.Name] is not JsonObject existing)
            return McpClientState.NotRegistered;

        return Launches(existing, entry) ? McpClientState.Registered : McpClientState.Different;
    }

    /// <summary>
    /// Whether an entry already starts this server on this library. Clients add their own keys - Claude
    /// Code's command writes <c>"type": "stdio"</c> and an empty <c>args</c> - so this asks what it would
    /// launch rather than whether it is written the same way.
    /// </summary>
    internal static bool Launches(JsonObject existing, McpServerEntry entry)
    {
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        var sameCommand = string.Equals(Text(existing["command"]), entry.Command, StringComparison.OrdinalIgnoreCase);
        var sameLibrary = string.Equals(Text(existing["env"]?[McpServerEntry.LibraryVariable]), entry.Library, StringComparison.OrdinalIgnoreCase);
        var noArguments = existing["args"] is null || existing["args"] is JsonArray { Count: 0 };
        return sameCommand && sameLibrary && noArguments;
    }

    public override McpChange Register(McpServerEntry entry)
    {
        if (!TryRead(out var root))
            return new McpChange(false, null, "its settings could not be read as JSON, so they were left alone");

        root ??= new JsonObject();
        if (root[serversKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[serversKey] = servers;
        }

        if (servers[McpServerEntry.Name] is JsonObject existing && Launches(existing, entry))
            return new McpChange(false, null);

        servers[McpServerEntry.Name] = Entry(entry);
        return Write(root);
    }

    public override McpChange Unregister()
    {
        if (!File.Exists(ConfigPath) || !TryRead(out var root) || root?[serversKey] is not JsonObject servers
            || !servers.ContainsKey(McpServerEntry.Name))
        {
            return new McpChange(false, null);
        }

        servers.Remove(McpServerEntry.Name);

        // A file ManualForge created holds nothing else once its entry is out: it goes too.
        if (servers.Count == 0 && root!.Count == 1 && !File.Exists(BackupPathFor(ConfigPath)))
        {
            File.Delete(ConfigPath);
            return new McpChange(true, null);
        }

        if (PutBackIfUnchanged(root!))
            return new McpChange(true, null);

        var change = Write(root!);
        DropBackup(ConfigPath);
        return change with { Backup = File.Exists(BackupPathFor(ConfigPath)) ? BackupPathFor(ConfigPath) : null };
    }

    public override string? Snapshot() =>
        TryRead(out var root) && root?[serversKey]?[McpServerEntry.Name] is JsonObject existing
            ? existing.ToJsonString()
            : null;

    public override McpChange Restore(string snapshot)
    {
        if (!TryRead(out var root))
            return new McpChange(false, null, "its settings could not be read as JSON, so they were left alone");

        root ??= new JsonObject();
        if (root[serversKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[serversKey] = servers;
        }

        servers[McpServerEntry.Name] = JsonNode.Parse(snapshot);
        if (PutBackIfUnchanged(root))
            return new McpChange(true, null);

        var change = Write(root);
        DropBackup(ConfigPath);
        return change with { Backup = File.Exists(BackupPathFor(ConfigPath)) ? BackupPathFor(ConfigPath) : null };
    }

    /// <summary>
    /// When taking ManualForge out leaves the settings saying exactly what they said before it went in,
    /// the copy from before goes back byte for byte, with its layout and comments, rather than the same
    /// settings written out again. Anything changed since, by the user or the app, keeps the rewrite.
    /// </summary>
    private bool PutBackIfUnchanged(JsonObject root)
    {
        var backup = BackupPathFor(ConfigPath);
        try
        {
            if (!File.Exists(backup)
                || JsonNode.Parse(File.ReadAllText(backup), documentOptions: Reading) is not JsonObject before
                || !JsonNode.DeepEquals(before, root))
            {
                return false;
            }

            File.Copy(backup, ConfigPath, overwrite: true);
            File.Delete(backup);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What goes under the server's name.</summary>
    protected JsonObject Entry(McpServerEntry entry)
    {
        var node = new JsonObject();
        if (typed)
            node["type"] = "stdio";
        node["command"] = entry.Command;
        node["env"] = new JsonObject { [McpServerEntry.LibraryVariable] = entry.Library };
        return node;
    }

    private bool TryRead(out JsonObject? root)
    {
        root = null;
        if (!File.Exists(ConfigPath))
            return true;

        try
        {
            var text = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(text))
                return true;

            root = JsonNode.Parse(text, documentOptions: Reading) as JsonObject;
            return root is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private McpChange Write(JsonObject root)
    {
        var hadComments = File.Exists(ConfigPath) && HasComments(File.ReadAllText(ConfigPath));
        var backup = Backup(ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, root.ToJsonString(Writing) + Environment.NewLine);
        return new McpChange(true, backup, hadComments ? "comments in the file were not kept; the backup has them" : null);
    }

    /// <summary>Whether a settings file holds comments, which rewriting it as plain JSON would lose.</summary>
    internal static bool HasComments(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                    return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}

/// <summary>
/// Claude Code. Its user settings are <c>~/.claude.json</c>, which it rewrites while it runs, so its own
/// <c>claude mcp</c> command is used when it is on PATH and the file is edited only when it is not.
/// </summary>
public sealed class ClaudeCodeClient(McpEnvironment environment)
    : JsonMcpClient(
        "claude-code", "Claude Code",
        Path.Combine(environment.UserProfile, ".claude.json"),
        () => environment.OnPath("claude") || File.Exists(Path.Combine(environment.UserProfile, ".claude.json")),
        "mcpServers", typed: false, "Claude Code sessions pick it up when they next start.")
{
    public override string Location => environment.OnPath("claude") ? "claude mcp (user scope)" : ConfigPath;

    public override McpChange Register(McpServerEntry entry)
    {
        if (!environment.OnPath("claude"))
            return base.Register(entry);

        if (State(entry) == McpClientState.Registered)
            return new McpChange(false, null);

        environment.Run("claude", ["mcp", "remove", McpServerEntry.Name, "--scope", "user"]);
        var (exit, output) = environment.Run("claude",
        [
            "mcp", "add", McpServerEntry.Name, "--scope", "user",
            "--env", $"{McpServerEntry.LibraryVariable}={entry.Library}", "--", entry.Command,
        ]);

        return exit == 0 ? new McpChange(true, null) : new McpChange(false, null, "claude mcp add failed: " + output.Trim());
    }

    public override McpChange Unregister()
    {
        if (!environment.OnPath("claude"))
            return base.Unregister();

        if (State(new McpServerEntry("", "")) == McpClientState.NotRegistered)
            return new McpChange(false, null);

        var (exit, output) = environment.Run("claude", ["mcp", "remove", McpServerEntry.Name, "--scope", "user"]);
        return exit == 0 ? new McpChange(true, null) : new McpChange(false, null, "claude mcp remove failed: " + output.Trim());
    }

    public override McpChange Restore(string snapshot)
    {
        if (!environment.OnPath("claude"))
            return base.Restore(snapshot);

        environment.Run("claude", ["mcp", "remove", McpServerEntry.Name, "--scope", "user"]);
        var (exit, output) = environment.Run("claude", ["mcp", "add-json", McpServerEntry.Name, snapshot, "--scope", "user"]);
        return exit == 0 ? new McpChange(true, null) : new McpChange(false, null, "claude mcp add-json failed: " + output.Trim());
    }
}

/// <summary>
/// OpenAI's Codex CLI, whose settings are TOML (<c>~/.codex/config.toml</c>). ManualForge owns one
/// table, <c>[mcp_servers.manualforge]</c>, and its <c>env</c> sub-table; it replaces or removes
/// exactly those lines and leaves the rest of the file as it was.
/// </summary>
public sealed class CodexClient(McpEnvironment environment) : McpClient("codex", "OpenAI Codex CLI")
{
    private string Directory => Path.Combine(environment.UserProfile, ".codex");

    private string ConfigPath => Path.Combine(Directory, "config.toml");

    public override string Location => ConfigPath;

    public override bool IsInstalled => System.IO.Directory.Exists(Directory);

    public override McpClientState State(McpServerEntry entry)
    {
        if (!IsInstalled)
            return McpClientState.NotInstalled;

        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : [];
        var ours = Ours(lines);
        if (ours.Count == 0)
            return McpClientState.NotRegistered;

        return ours.Select(i => lines[i]).SequenceEqual(Table(entry)) ? McpClientState.Registered : McpClientState.Different;
    }

    public override McpChange Register(McpServerEntry entry)
    {
        if (State(entry) == McpClientState.Registered)
            return new McpChange(false, null);

        return new McpChange(true, WriteTables(Table(entry)));
    }

    public override string? Snapshot()
    {
        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : [];
        var ours = Ours(lines);
        return ours.Count == 0 ? null : string.Join('\n', ours.Select(i => lines[i]));
    }

    public override McpChange Restore(string snapshot)
    {
        WriteTables(snapshot.Split('\n'));
        if (!PutBackIfUnchanged(File.ReadAllLines(ConfigPath)))
            DropBackup(ConfigPath);
        return new McpChange(true, null);
    }

    /// <summary>Replaces ManualForge's tables with these lines, at the end of the file, and returns the backup.</summary>
    private string? WriteTables(IEnumerable<string> tables)
    {
        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : [];
        var ours = Ours(lines.ToArray());
        foreach (var index in ours.OrderDescending())
            lines.RemoveAt(index);

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0)
            lines.Add("");
        lines.AddRange(tables);

        var backup = Backup(ConfigPath);
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllLines(ConfigPath, lines);
        return backup;
    }

    public override McpChange Unregister()
    {
        if (!File.Exists(ConfigPath))
            return new McpChange(false, null);

        var lines = File.ReadAllLines(ConfigPath).ToList();
        var ours = Ours(lines.ToArray());
        if (ours.Count == 0)
            return new McpChange(false, null);

        foreach (var index in ours.OrderDescending())
            lines.RemoveAt(index);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        if (!PutBackIfUnchanged(lines))
        {
            if (lines.Count == 0)
                File.Delete(ConfigPath);
            else
                File.WriteAllLines(ConfigPath, lines);
            DropBackup(ConfigPath);
        }

        return new McpChange(true, null);
    }

    /// <summary>As for JSON settings: the copy from before goes back byte for byte when the lines match it.</summary>
    private bool PutBackIfUnchanged(IReadOnlyList<string> lines)
    {
        var backup = BackupPathFor(ConfigPath);
        if (!File.Exists(backup))
            return false;

        var before = File.ReadAllLines(backup).ToList();
        while (before.Count > 0 && string.IsNullOrWhiteSpace(before[^1]))
            before.RemoveAt(before.Count - 1);
        if (!before.SequenceEqual(lines))
            return false;

        File.Copy(backup, ConfigPath, overwrite: true);
        File.Delete(backup);
        return true;
    }

    internal static IReadOnlyList<string> Table(McpServerEntry entry) =>
    [
        $"[mcp_servers.{McpServerEntry.Name}]",
        $"command = {Quote(entry.Command)}",
        "",
        $"[mcp_servers.{McpServerEntry.Name}.env]",
        $"{McpServerEntry.LibraryVariable} = {Quote(entry.Library)}",
    ];

    /// <summary>The line numbers of ManualForge's tables: each header to the line before the next header.</summary>
    internal static List<int> Ours(string[] lines)
    {
        var ours = new List<int>();
        var inOurs = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                inOurs = trimmed is "[mcp_servers.manualforge]" or "[mcp_servers.manualforge.env]";
            }

            if (inOurs)
                ours.Add(i);
        }

        return ours;
    }

    /// <summary>A TOML literal string: single quotes, no escapes, so Windows paths need nothing doubled.</summary>
    private static string Quote(string value) => value.Contains('\'') ? JsonSerializer.Serialize(value) : $"'{value}'";
}

/// <summary>
/// Continue, which reads one YAML file per MCP server from <c>~/.continue/mcpServers</c>. ManualForge's is
/// its own file, so adding and removing it touch nothing else.
/// </summary>
public sealed class ContinueClient(McpEnvironment environment) : McpClient("continue", "Continue")
{
    private string ConfigPath => Path.Combine(environment.UserProfile, ".continue", "mcpServers", "manualforge.yaml");

    public override string Location => ConfigPath;

    public override bool IsInstalled => Directory.Exists(Path.Combine(environment.UserProfile, ".continue"));

    public override McpClientState State(McpServerEntry entry) =>
        !IsInstalled ? McpClientState.NotInstalled
        : !File.Exists(ConfigPath) ? McpClientState.NotRegistered
        : File.ReadAllText(ConfigPath) == Yaml(entry) ? McpClientState.Registered
        : McpClientState.Different;

    public override McpChange Register(McpServerEntry entry)
    {
        if (State(entry) == McpClientState.Registered)
            return new McpChange(false, null);

        var backup = Backup(ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, Yaml(entry));
        return new McpChange(true, backup);
    }

    public override McpChange Unregister()
    {
        if (!File.Exists(ConfigPath))
            return new McpChange(false, null);

        File.Delete(ConfigPath);
        return new McpChange(true, null);
    }

    public override string? Snapshot() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;

    public override McpChange Restore(string snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, snapshot);
        DropBackup(ConfigPath);
        return new McpChange(true, null);
    }

    internal static string Yaml(McpServerEntry entry) => string.Join('\n',
        "name: ManualForge",
        "version: 0.0.1",
        "schema: v1",
        "mcpServers:",
        "  - name: manualforge",
        $"    command: {JsonSerializer.Serialize(entry.Command)}",
        "    env:",
        $"      {McpServerEntry.LibraryVariable}: {JsonSerializer.Serialize(entry.Library)}",
        "");
}

/// <summary>Every client ManualForge knows how to register with, as found on this machine.</summary>
public static class McpClients
{
    public static IReadOnlyList<McpClient> All(McpEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var home = environment.UserProfile;

        // The Microsoft Store build keeps its settings in its package's virtualised AppData, not %APPDATA%.
        var store = Directory.Exists(Path.Combine(environment.LocalAppData, "Packages"))
            ? Directory.GetDirectories(Path.Combine(environment.LocalAppData, "Packages"), "Claude_*").FirstOrDefault()
            : null;

        return
        [
            new JsonMcpClient("claude-desktop-store", "Claude Desktop (Microsoft Store)",
                Path.Combine(store ?? Path.Combine(environment.LocalAppData, "Packages", "Claude_"), "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"),
                () => store is not null, "mcpServers", typed: false, "Quit Claude Desktop and start it again to pick it up."),
            new JsonMcpClient("claude-desktop", "Claude Desktop",
                Path.Combine(environment.AppData, "Claude", "claude_desktop_config.json"),
                () => Directory.Exists(Path.Combine(environment.AppData, "Claude")), "mcpServers", typed: false,
                "Quit Claude Desktop and start it again to pick it up."),
            new ClaudeCodeClient(environment),
            new JsonMcpClient("vscode", "VS Code",
                Path.Combine(environment.AppData, "Code", "User", "mcp.json"),
                () => Directory.Exists(Path.Combine(environment.AppData, "Code", "User")), "servers", typed: true),
            new JsonMcpClient("vscode-insiders", "VS Code Insiders",
                Path.Combine(environment.AppData, "Code - Insiders", "User", "mcp.json"),
                () => Directory.Exists(Path.Combine(environment.AppData, "Code - Insiders", "User")), "servers", typed: true),
            new JsonMcpClient("visual-studio", "Visual Studio",
                Path.Combine(home, ".mcp.json"),
                () => VisualStudioInstalled(environment), "servers", typed: true, "Visual Studio picks it up when it next starts."),
            new JsonMcpClient("cursor", "Cursor",
                Path.Combine(home, ".cursor", "mcp.json"),
                () => Directory.Exists(Path.Combine(home, ".cursor")), "mcpServers", typed: false),
            new JsonMcpClient("windsurf", "Windsurf",
                Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"),
                () => Directory.Exists(Path.Combine(home, ".codeium", "windsurf")), "mcpServers", typed: false),
            new JsonMcpClient("gemini", "Gemini CLI",
                Path.Combine(home, ".gemini", "settings.json"),
                () => Directory.Exists(Path.Combine(home, ".gemini")), "mcpServers", typed: false),
            new CodexClient(environment),
            new JsonMcpClient("lmstudio", "LM Studio",
                Path.Combine(home, ".lmstudio", "mcp.json"),
                () => Directory.Exists(Path.Combine(home, ".lmstudio")), "mcpServers", typed: false),
            new ContinueClient(environment),
        ];
    }

    private static bool VisualStudioInstalled(McpEnvironment environment)
    {
        var root = Path.Combine(environment.ProgramFiles, "Microsoft Visual Studio");
        return Directory.Exists(root)
               && Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories)
                   .Any(edition => File.Exists(Path.Combine(edition, "Common7", "IDE", "devenv.exe")));
    }
}
