using System.Text.Json;

namespace ManualForge.Core.Setup;

/// <summary>
/// One client ManualForge changed: which, where, and the <c>manualforge</c> entry it had before, if it
/// had one (<see cref="McpClient.Snapshot"/>).
/// </summary>
public sealed record McpRegistration(string Client, string Location, string? Previous);

/// <summary>What removing ManualForge did to one client.</summary>
public sealed record McpRemoval(McpClient Client, McpChange Change, bool Restored);

/// <summary>
/// The record of what <c>manualforge setup mcp</c> changed, so that removing ManualForge undoes exactly
/// that (#26): an entry it added is taken out, an entry it replaced is put back as it was, and an entry it
/// found already in place, or one the user added by hand, is left alone. Kept in
/// <c>%LOCALAPPDATA%\ManualForge\mcp-registrations.json</c>.
/// </summary>
public sealed class McpRegistrations
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    private readonly List<McpRegistration> _entries;

    private McpRegistrations(string path, List<McpRegistration> entries)
    {
        _path = path;
        _entries = entries;
    }

    public IReadOnlyList<McpRegistration> Entries => _entries;

    public static string PathFor(McpEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Path.Combine(environment.LocalAppData, "ManualForge", "mcp-registrations.json");
    }

    public static McpRegistrations Load(McpEnvironment environment)
    {
        var path = PathFor(environment);
        var entries = File.Exists(path)
            ? JsonSerializer.Deserialize<List<McpRegistration>>(File.ReadAllText(path)) ?? []
            : [];
        return new McpRegistrations(path, entries);
    }

    /// <summary>
    /// Registers with one client, and records it if anything changed. A client already recorded keeps the
    /// entry it had before ManualForge first touched it: registering again over ManualForge's own entry
    /// must not make that the one to put back.
    /// </summary>
    public McpChange Register(McpClient client, McpServerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(client);
        var previous = client.Snapshot();
        var change = client.Register(entry);
        if (change.Changed && _entries.All(e => e.Client != client.Id))
        {
            _entries.Add(new McpRegistration(client.Id, client.Location, previous));
            Save();
        }

        return change;
    }

    /// <summary>Undoes what was recorded for these clients, and forgets it.</summary>
    public IReadOnlyList<McpRemoval> Remove(IEnumerable<McpClient> clients)
    {
        var removals = new List<McpRemoval>();
        foreach (var client in clients)
        {
            var recorded = _entries.FirstOrDefault(e => e.Client == client.Id);
            if (recorded is null)
                continue;

            var change = recorded.Previous is null ? client.Unregister() : client.Restore(recorded.Previous);
            removals.Add(new McpRemoval(client, change, recorded.Previous is not null));
            if (change.Changed || change.Note is null)
                _entries.Remove(recorded);
        }

        Save();
        return removals;
    }

    private void Save()
    {
        if (_entries.Count == 0)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json) + Environment.NewLine);
    }
}
