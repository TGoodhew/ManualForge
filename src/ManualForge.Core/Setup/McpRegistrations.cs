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
        if (_entries.FirstOrDefault(e => e.Client == client.Id) is { } recorded)
            FollowMove(client, recorded);

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

            var (now, undone) = FollowMove(client, recorded);
            if (now is null || undone is { Changed: false, Note: not null })
            {
                // The entry was only in the file the client no longer reads, and is out of it now; or it
                // couldn't be taken out of there, and the record stays as it was.
                removals.Add(new McpRemoval(client, undone ?? new McpChange(false, null), recorded.Previous is not null));
                continue;
            }

            var change = now.Previous is null ? client.Unregister() : client.Restore(now.Previous);
            if (undone is { Changed: true } && !change.Changed && change.Note is null)
                change = undone;
            removals.Add(new McpRemoval(client, change, now.Previous is not null));
            if (change.Changed || change.Note is null)
                _entries.Remove(now);
        }

        Save();
        return removals;
    }

    /// <summary>
    /// An earlier install registered with a client whose settings have since moved (#98: current Store
    /// builds of Claude Desktop keep them in <c>Claude-Data</c>, and no longer read the old file). The entry is
    /// taken out of the old file, as removing ManualForge would have done. When the client took its settings
    /// with it, ManualForge's entry is in the new file too, so the record follows it there, still holding the
    /// entry from before ManualForge; otherwise the record is forgotten and the next registration starts
    /// afresh. Returns the record as it now stands, null when forgotten, and what was done to the old file.
    /// When the old file can't be changed, everything is left as it was, so nothing is lost.
    /// </summary>
    private (McpRegistration? Now, McpChange? Undone) FollowMove(McpClient client, McpRegistration recorded)
    {
        if (string.IsNullOrEmpty(recorded.Location) || SameLocation(recorded.Location, client.Location)
            || client.At(recorded.Location) is not { } old)
        {
            return (recorded, null);
        }

        var there = old.Snapshot();
        var carried = there is not null && string.Equals(there, client.Snapshot(), StringComparison.Ordinal);

        McpChange? undone = null;
        if (there is not null)
        {
            undone = recorded.Previous is null ? old.Unregister() : old.Restore(recorded.Previous);
            if (!undone.Changed && undone.Note is not null)
                return (recorded, undone);
        }

        _entries.Remove(recorded);
        var now = carried ? recorded with { Location = client.Location } : null;
        if (now is not null)
            _entries.Add(now);
        Save();
        return (now, undone);
    }

    private static bool SameLocation(string a, string b)
    {
        static string Normalise(string location)
        {
            try
            {
                return Path.GetFullPath(location).TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return location;   // not a path: "claude mcp (user scope)"
            }
        }

        return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
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
