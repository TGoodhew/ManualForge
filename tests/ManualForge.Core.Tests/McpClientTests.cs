using System.Text.Json.Nodes;
using ManualForge.Core.Setup;

namespace ManualForge.Core.Tests;

/// <summary>
/// `manualforge setup mcp` (#26): one entry in every client that can launch a local MCP server, merged
/// into its settings without disturbing anything else, copied aside first, and taken out again. Every
/// case runs against a scratch profile; nothing on the machine running the tests is touched.
/// </summary>
public sealed class McpClientTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "manualforge-mcp-" + Guid.NewGuid().ToString("N"));

    private readonly List<(string Command, IReadOnlyList<string> Arguments)> _ran = [];

    public McpClientTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly McpServerEntry Entry =
        new(@"C:\Users\someone\AppData\Local\Programs\ManualForge\ManualForge.Mcp.exe", @"D:\Manuals");

    private string AppData => Path.Combine(_home, "AppData", "Roaming");

    private string LocalAppData => Path.Combine(_home, "AppData", "Local");

    private McpEnvironment Environment(bool claudeOnPath = false) => new(
        _home, AppData, LocalAppData, Path.Combine(_home, "Program Files"),
        command => claudeOnPath && command == "claude",
        (command, arguments) =>
        {
            _ran.Add((command, arguments));
            return (0, "");
        });

    private McpClient Client(string id, bool claudeOnPath = false) =>
        McpClients.All(Environment(claudeOnPath)).Single(c => c.Id == id);

    private string Installed(params string[] parts)
    {
        var path = Path.Combine([_home, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    // ---------------------------------------------------------------- JSON clients

    [Fact]
    public void AStoreClaudeDesktopKeepsEverythingElseInItsSettings()
    {
        // The shape of the real file: preferences, another key, and another server beside ours.
        var folder = Installed("AppData", "Local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        var config = Path.Combine(folder, "claude_desktop_config.json");
        File.WriteAllText(config, """
            {
              "mcpServers": { "gpib": { "command": "C:\\gpib\\gpib-mcp.exe" } },
              "coworkUserFilesPath": "C:\\Users\\someone\\Claude",
              "preferences": { "menuBarEnabled": false }
            }
            """);

        var client = Client("claude-desktop-store");
        Assert.True(client.IsInstalled);
        Assert.Equal(McpClientState.NotRegistered, client.State(Entry));

        var change = client.Register(Entry);

        Assert.True(change.Changed);
        Assert.NotNull(change.Backup);
        Assert.True(File.Exists(change.Backup));
        var after = Read(config);
        Assert.Equal(@"C:\gpib\gpib-mcp.exe", after["mcpServers"]!["gpib"]!["command"]!.GetValue<string>());
        Assert.Equal(Entry.Command, after["mcpServers"]!["manualforge"]!["command"]!.GetValue<string>());
        Assert.Equal(Entry.Library, after["mcpServers"]!["manualforge"]!["env"]!["MANUALFORGE_LIBRARY"]!.GetValue<string>());
        Assert.False(after["preferences"]!["menuBarEnabled"]!.GetValue<bool>());
        Assert.NotNull(after["coworkUserFilesPath"]);
        Assert.Equal(McpClientState.Registered, client.State(Entry));
    }

    [Fact]
    public void RegisteringTwiceChangesNothingTheSecondTime()
    {
        Installed("AppData", "Roaming", "Claude");
        var client = Client("claude-desktop");

        Assert.True(client.Register(Entry).Changed);
        var written = File.ReadAllText(((JsonMcpClient)client).ConfigPath);

        var again = client.Register(Entry);

        Assert.False(again.Changed);
        Assert.Null(again.Backup);
        Assert.Equal(written, File.ReadAllText(((JsonMcpClient)client).ConfigPath));
    }

    [Fact]
    public void AnEntryPointingElsewhereIsUpdated()
    {
        Installed(".cursor");
        var client = Client("cursor");
        client.Register(Entry with { Command = @"C:\old\ManualForge.Mcp.exe" });

        Assert.Equal(McpClientState.Different, client.State(Entry));
        Assert.True(client.Register(Entry).Changed);
        Assert.Equal(McpClientState.Registered, client.State(Entry));
    }

    [Fact]
    public void VsCodeAndVisualStudioGetATypedServer()
    {
        Installed("AppData", "Roaming", "Code", "User");
        var client = Client("vscode");

        client.Register(Entry);

        var server = Read(((JsonMcpClient)client).ConfigPath)["servers"]!["manualforge"]!;
        Assert.Equal("stdio", server["type"]!.GetValue<string>());
        Assert.Equal(Entry.Command, server["command"]!.GetValue<string>());
    }

    [Fact]
    public void RemovingTakesOutOnlyManualForge()
    {
        Installed(".gemini");
        var client = (JsonMcpClient)Client("gemini");
        File.WriteAllText(client.ConfigPath, """{ "theme": "dark", "mcpServers": { "other": { "command": "x.exe" } } }""");
        client.Register(Entry);

        var change = client.Unregister();

        Assert.True(change.Changed);
        var after = Read(client.ConfigPath);
        Assert.Equal("dark", after["theme"]!.GetValue<string>());
        Assert.NotNull(after["mcpServers"]!["other"]);
        Assert.Null(after["mcpServers"]!["manualforge"]);
        Assert.False(client.Unregister().Changed);   // nothing left to take out
        Assert.False(File.Exists(client.ConfigPath + ".before-manualforge.bak"));   // and no copy left behind
    }

    [Fact]
    public void AFileManualForgeCreatedGoesWhenItsEntryDoes()
    {
        Installed("AppData", "Roaming", "Code", "User");
        var client = (JsonMcpClient)Client("vscode");
        client.Register(Entry);
        Assert.True(File.Exists(client.ConfigPath));

        client.Unregister();

        Assert.False(File.Exists(client.ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(client.ConfigPath)!));
    }

    [Fact]
    public void TheCopyFromBeforeManualForgeIsNeverOverwritten()
    {
        // 11 Oct: a register and a remove in the same second overwrote the only copy of the original.
        Installed(".cursor");
        var client = (JsonMcpClient)Client("cursor");
        File.WriteAllText(client.ConfigPath, """{ "mcpServers": { "mine": { "command": "mine.exe" } } }""");
        var original = File.ReadAllText(client.ConfigPath);

        var first = client.Register(Entry);
        client.Register(Entry with { Library = @"E:\Other" });
        client.Register(Entry);

        Assert.Equal(original, File.ReadAllText(first.Backup!));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(client.ConfigPath)!, "*.bak"));
    }

    [Fact]
    public void SettingsNobodyChangedSinceGoBackByteForByte()
    {
        // The issue's test: the settings after uninstalling are the settings from before installing,
        // layout and comments included, not the same values written out again.
        Installed("AppData", "Roaming", "Code", "User");
        var client = (JsonMcpClient)Client("vscode");
        var before = "{\n    // the web one is for work\n    \"servers\": {\"web\": {\"url\": \"https://example.test/mcp\"}}\n}\n";
        File.WriteAllText(client.ConfigPath, before);

        client.Register(Entry);
        var removal = client.Unregister();

        Assert.True(removal.Changed);
        Assert.Null(removal.Backup);
        Assert.Equal(before, File.ReadAllText(client.ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(client.ConfigPath)!, "*.bak"));
    }

    [Fact]
    public void ACopyHoldingCommentsTheEditLostIsKeptWhenTheSettingsChangedSince()
    {
        Installed("AppData", "Roaming", "Code", "User");
        var client = (JsonMcpClient)Client("vscode");
        File.WriteAllText(client.ConfigPath, """
            {
              // the web one is for work
              "servers": { "web": { "url": "https://example.test/mcp" } }
            }
            """);

        client.Register(Entry);
        var settings = Read(client.ConfigPath);
        settings["servers"]!["another"] = new JsonObject { ["command"] = "another.exe" };
        File.WriteAllText(client.ConfigPath, settings.ToJsonString());
        var removal = client.Unregister();

        Assert.NotNull(removal.Backup);
        Assert.Contains("// the web one is for work", File.ReadAllText(removal.Backup!), StringComparison.Ordinal);
        Assert.NotNull(Read(client.ConfigPath)["servers"]!["another"]);
    }

    [Fact]
    public void CodexSettingsNobodyChangedSinceGoBackByteForByte()
    {
        Installed(".codex");
        var config = Path.Combine(_home, ".codex", "config.toml");
        var before = "model = \"gpt-5\"   # the default\n\n[mcp_servers.other]\ncommand = 'other.exe'\n\n\n";
        File.WriteAllText(config, before);
        var client = Client("codex");

        client.Register(Entry);
        client.Unregister();

        Assert.Equal(before, File.ReadAllText(config));
        Assert.False(File.Exists(config + ".before-manualforge.bak"));
    }

    [Fact]
    public void SettingsThatAreNotJsonAreLeftAlone()
    {
        Installed(".lmstudio");
        var client = (JsonMcpClient)Client("lmstudio");
        File.WriteAllText(client.ConfigPath, "{ this is not json");

        Assert.Equal(McpClientState.Unreadable, client.State(Entry));
        var change = client.Register(Entry);

        Assert.False(change.Changed);
        Assert.Equal("{ this is not json", File.ReadAllText(client.ConfigPath));
    }

    [Fact]
    public void CommentsThatWouldBeLostAreSaid()
    {
        Installed("AppData", "Roaming", "Code", "User");
        var client = (JsonMcpClient)Client("vscode");
        File.WriteAllText(client.ConfigPath, """
            {
              // my servers
              "servers": { "web": { "url": "https://example.test/mcp" } },
            }
            """);

        var change = client.Register(Entry);

        Assert.True(change.Changed);
        Assert.Contains("comments", change.Note, StringComparison.Ordinal);
        Assert.Contains("// my servers", File.ReadAllText(change.Backup!), StringComparison.Ordinal);
        Assert.NotNull(Read(client.ConfigPath)["servers"]!["web"]);
    }

    [Theory]
    [InlineData("""{ "url": "https://example.test" }""", false)]
    [InlineData("""{ /* note */ "a": 1 }""", true)]
    [InlineData("{ \"a\": 1 } // trailing", true)]
    public void ACommentIsNotAUrl(string text, bool comments) => Assert.Equal(comments, JsonMcpClient.HasComments(text));

    [Fact]
    public void AClientThatIsNotInstalledIsNotTouched()
    {
        var client = Client("windsurf");

        Assert.False(client.IsInstalled);
        Assert.Equal(McpClientState.NotInstalled, client.State(Entry));
        Assert.False(Directory.Exists(Path.Combine(_home, ".codeium")));
    }

    // ---------------------------------------------------------------- Claude Code

    [Fact]
    public void ClaudeCodesOwnCommandIsUsedWhenItIsThere()
    {
        File.WriteAllText(Path.Combine(_home, ".claude.json"), """{ "numStartups": 4 }""");
        var client = Client("claude-code", claudeOnPath: true);

        client.Register(Entry);

        Assert.Equal("claude mcp (user scope)", client.Location);
        var add = Assert.Single(_ran, r => r.Arguments.Contains("add"));
        Assert.Equal(
            ["mcp", "add", "manualforge", "--scope", "user", "--env", @"MANUALFORGE_LIBRARY=D:\Manuals", "--", Entry.Command],
            add.Arguments);
        Assert.Equal("""{ "numStartups": 4 }""", File.ReadAllText(Path.Combine(_home, ".claude.json")));
    }

    [Fact]
    public void AnEntryClaudeCodeWroteItselfCountsAsRegistered()
    {
        // What `claude mcp add` leaves in ~/.claude.json: its own keys beside ours. Same program, same
        // library, so nothing needs doing.
        File.WriteAllText(Path.Combine(_home, ".claude.json"), $$"""
            { "mcpServers": { "manualforge": { "type": "stdio", "command": {{System.Text.Json.JsonSerializer.Serialize(Entry.Command)}},
              "args": [], "env": { "MANUALFORGE_LIBRARY": {{System.Text.Json.JsonSerializer.Serialize(Entry.Library)}} } } } }
            """);
        var client = Client("claude-code", claudeOnPath: true);

        Assert.Equal(McpClientState.Registered, client.State(Entry));
        Assert.False(client.Register(Entry).Changed);
        Assert.Empty(_ran);
    }

    [Fact]
    public void WithoutItsCommandClaudeCodesSettingsAreEdited()
    {
        File.WriteAllText(Path.Combine(_home, ".claude.json"), """{ "numStartups": 4, "projects": {} }""");
        var client = Client("claude-code");

        client.Register(Entry);

        Assert.Empty(_ran);
        var after = Read(Path.Combine(_home, ".claude.json"));
        Assert.Equal(4, after["numStartups"]!.GetValue<int>());
        Assert.Equal(Entry.Command, after["mcpServers"]!["manualforge"]!["command"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- Codex (TOML) and Continue (YAML)

    [Fact]
    public void CodexKeepsItsOtherTablesAndGetsOurs()
    {
        Installed(".codex");
        var config = Path.Combine(_home, ".codex", "config.toml");
        File.WriteAllLines(config,
        [
            "model = 'gpt-5'",
            "",
            "[mcp_servers.other]",
            "command = 'other.exe'",
        ]);
        var client = Client("codex");

        Assert.True(client.Register(Entry).Changed);
        Assert.Equal(McpClientState.Registered, client.State(Entry));
        var text = File.ReadAllText(config);
        Assert.Contains("[mcp_servers.other]", text, StringComparison.Ordinal);
        Assert.Contains(@"command = 'C:\Users\someone\AppData\Local\Programs\ManualForge\ManualForge.Mcp.exe'", text, StringComparison.Ordinal);
        Assert.Contains(@"MANUALFORGE_LIBRARY = 'D:\Manuals'", text, StringComparison.Ordinal);

        // Updated in place, not appended twice.
        client.Register(Entry with { Library = @"E:\Manuals" });
        Assert.Single(File.ReadAllLines(config), line => line == "[mcp_servers.manualforge]");

        Assert.True(client.Unregister().Changed);
        Assert.Equal(["model = 'gpt-5'", "", "[mcp_servers.other]", "command = 'other.exe'"], File.ReadAllLines(config));
        Assert.False(File.Exists(config + ".before-manualforge.bak"));
    }

    [Fact]
    public void ContinueGetsAFileOfItsOwnAndLosesIt()
    {
        Installed(".continue");
        var client = Client("continue");

        client.Register(Entry);
        var file = client.Location;
        Assert.True(File.Exists(file));
        Assert.Contains("name: manualforge", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(McpClientState.Registered, client.State(Entry));

        client.Unregister();
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void EveryClientInTheIssueIsKnown()
    {
        var ids = McpClients.All(Environment()).Select(c => c.Id).ToArray();

        Assert.Equal(
            ["claude-desktop-store", "claude-desktop", "claude-code", "vscode", "vscode-insiders", "visual-studio",
             "cursor", "windsurf", "gemini", "codex", "lmstudio", "continue"],
            ids);
    }

    // ---------------------------------------------------------------- the record removal works from

    private McpRegistrations Record(bool claudeOnPath = false) => McpRegistrations.Load(Environment(claudeOnPath));

    private IReadOnlyList<McpClient> Clients(bool claudeOnPath = false) => McpClients.All(Environment(claudeOnPath));

    [Fact]
    public void RemovingTakesOutOnlyWhatWasRecorded()
    {
        // VS Code is registered by setup; Cursor already had the same entry, put there by hand, so setup
        // changes nothing in it and an uninstall must leave it.
        Installed("AppData", "Roaming", "Code", "User");
        Installed(".cursor");
        var cursor = (JsonMcpClient)Client("cursor");
        cursor.Register(Entry);
        var byHand = File.ReadAllText(cursor.ConfigPath);

        var record = Record();
        Assert.True(record.Register(Client("vscode"), Entry).Changed);
        Assert.False(record.Register(cursor, Entry).Changed);
        Assert.Equal("vscode", Assert.Single(record.Entries).Client);

        var removals = Record().Remove(Clients());

        Assert.Equal("vscode", Assert.Single(removals).Client.Id);
        Assert.False(File.Exists(((JsonMcpClient)Client("vscode")).ConfigPath));
        Assert.Equal(byHand, File.ReadAllText(cursor.ConfigPath));
        Assert.False(File.Exists(McpRegistrations.PathFor(Environment())));
    }

    [Fact]
    public void AnEntryManualForgeReplacedIsPutBackAsItWas()
    {
        var folder = Installed("AppData", "Roaming", "Claude");
        var config = Path.Combine(folder, "claude_desktop_config.json");
        var before = """{ "mcpServers": { "manualforge": { "command": "D:\\dev\\ManualForge.Mcp.exe", "args": ["--verbose"] } }, "theme": "light" }""";
        File.WriteAllText(config, before);

        var record = Record();
        record.Register(Client("claude-desktop"), Entry);
        // Registering again, on another library, must not make ManualForge's own entry the one to put back.
        Record().Register(Client("claude-desktop"), Entry with { Library = @"E:\Manuals" });
        Assert.Equal(Entry.Command, Read(config)["mcpServers"]!["manualforge"]!["command"]!.GetValue<string>());

        var removal = Assert.Single(Record().Remove(Clients()));

        Assert.True(removal.Restored);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), Read(config)));
        Assert.False(File.Exists(config + ".before-manualforge.bak"));
    }

    [Fact]
    public void WithoutARecordNothingIsRemoved()
    {
        Installed("AppData", "Roaming", "Code", "User");
        var vscode = (JsonMcpClient)Client("vscode");
        vscode.Register(Entry);

        Assert.Empty(Record().Remove(Clients()));
        Assert.Equal(McpClientState.Registered, vscode.State(Entry));
    }

    [Fact]
    public void ACodexTableManualForgeReplacedIsPutBack()
    {
        Installed(".codex");
        var config = Path.Combine(_home, ".codex", "config.toml");
        string[] before = ["model = 'gpt-5'", "", "[mcp_servers.manualforge]", "command = 'D:\\dev\\ManualForge.Mcp.exe'"];
        File.WriteAllLines(config, before);

        Record().Register(Client("codex"), Entry);
        Assert.Contains(@"MANUALFORGE_LIBRARY = 'D:\Manuals'", File.ReadAllText(config), StringComparison.Ordinal);

        Assert.True(Assert.Single(Record().Remove(Clients())).Restored);
        Assert.Equal(before, File.ReadAllLines(config));
    }

    [Fact]
    public void ClaudeCodesOwnCommandPutsBackTheEntryItHadBefore()
    {
        var previous = """{"type":"stdio","command":"D:\\dev\\ManualForge.Mcp.exe","args":[]}""";
        File.WriteAllText(Path.Combine(_home, ".claude.json"), $$"""{ "mcpServers": { "manualforge": {{previous}} } }""");

        Record(claudeOnPath: true).Register(Client("claude-code", claudeOnPath: true), Entry);
        _ran.Clear();

        Assert.True(Assert.Single(Record(claudeOnPath: true).Remove(Clients(claudeOnPath: true))).Restored);
        var add = Assert.Single(_ran, r => r.Arguments.Contains("add-json"));
        Assert.Equal(["mcp", "add-json", "manualforge", previous, "--scope", "user"], add.Arguments);
    }
}
