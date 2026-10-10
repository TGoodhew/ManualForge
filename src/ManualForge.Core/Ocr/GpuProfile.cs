using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManualForge.Core.Ocr;

/// <summary>
/// What this machine has learned about one GPU: how many pages it runs best in flight, where it fell
/// off the memory cliff, and how fast it actually went.
///
/// <para>
/// Every one of these used to be a constant measured on one card and quoted for all of them - 104
/// pages a minute measured on an RTX 3060 Ti went on being quoted after the card was replaced, and
/// three pages in flight was right for a 16 GB card only because somebody measured that card (#31).
/// A profile is learned by running, and belongs to the card it was learned on.
/// </para>
/// </summary>
public sealed record GpuProfile
{
    public required string Name { get; init; }

    public required int TotalMiB { get; init; }

    public string? Driver { get; init; }

    /// <summary>The pages in flight the last tuned run settled on.</summary>
    public int? BestConcurrency { get; init; }

    /// <summary>
    /// The fewest pages in flight ever seen to spill or collapse on this card. Never started at or
    /// above, and only ever lowered: a cliff found once is not worth falling off twice.
    /// </summary>
    public int? UnsafeConcurrency { get; init; }

    /// <summary>
    /// Recognition speed of the last run long enough to measure, end to end. What `run` and
    /// `status` estimate from on this card.
    /// </summary>
    public double? RunPagesPerMinute { get; init; }

    /// <summary>Repair speed by page kind and resolution, keyed "Drawn/300" and the like.</summary>
    public Dictionary<string, double> RepairPagesPerMinute { get; init; } = [];

    public DateTimeOffset? UpdatedUtc { get; init; }
}

/// <summary>
/// Profiles for every GPU this machine has used, in one small JSON file under
/// <c>%LOCALAPPDATA%\ManualForge</c>. Reading it never fails a run: a missing or unreadable file is
/// a card with nothing learned yet.
/// </summary>
public sealed class GpuProfileStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object FileLock = new();

    public string Path { get; } = path ?? DefaultPath;

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManualForge", "gpu-profiles.json");

    /// <summary>The profile for this card, or null when it has never been used here.</summary>
    public GpuProfile? Find(GpuIdentity? gpu) =>
        gpu is not null && Load().TryGetValue(gpu.Key, out var profile) ? profile : null;

    /// <summary>Changes this card's profile, creating it on first use.</summary>
    public GpuProfile Update(GpuIdentity gpu, Func<GpuProfile, GpuProfile> change)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(change);

        lock (FileLock)
        {
            var all = Load();
            var current = all.TryGetValue(gpu.Key, out var found)
                ? found
                : new GpuProfile { Name = gpu.Name, TotalMiB = gpu.TotalMiB };

            var updated = change(current) with { Driver = gpu.Driver, UpdatedUtc = DateTimeOffset.UtcNow };
            all[gpu.Key] = updated;

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temporary = Path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
                File.Move(temporary, Path, overwrite: true);
            }
            catch (IOException)
            {
                // What was learned is lost, and the next run learns it again. Not worth failing for.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return updated;
        }
    }

    private Dictionary<string, GpuProfile> Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Dictionary<string, GpuProfile>>(File.ReadAllText(Path), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return [];
    }
}
