using System.Security.Cryptography;
using ManualForge.Core.Classification;
using ManualForge.Core.State;
using Microsoft.Extensions.Logging;

namespace ManualForge.Core.Pipeline;

public sealed record DuplicateGroup(string Primary, IReadOnlyList<string> Copies, string ContentHash, int PageCount)
{
    /// <summary>Pages that would be recognised twice if every copy were processed separately.</summary>
    public int RedundantPages => Copies.Count * PageCount;
}

public sealed record DeduplicationReport(
    int FilesExamined,
    int DistinctDocuments,
    IReadOnlyList<DuplicateGroup> Groups)
{
    public int RedundantFiles => Groups.Sum(g => g.Copies.Count);
    public int RedundantPages => Groups.Sum(g => g.RedundantPages);
}

/// <summary>
/// Finds files that are byte-for-byte identical and arranges for each distinct document to be
/// recognised once.
///
/// Libraries assembled over years accumulate copies: a manual filed under two model numbers, a
/// folder left behind by an earlier tool, the same scan downloaded twice. Recognising each copy
/// separately costs GPU hours and, worse, makes the search index return the same manual several
/// times over, which is the part that is actually annoying to live with.
///
/// Matching is by content hash rather than filename, because copies rarely keep the same name —
/// prefixes get added, punctuation gets sanitised, model numbers get appended. One copy is chosen
/// as the primary and processed normally; the rest take its finished result, so every path still
/// opens a searchable file.
/// </summary>
public sealed class ContentDeduplicator(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Hashes the given files, records their identity, and marks non-primary copies to be taken
    /// from their primary rather than recognised again.
    /// </summary>
    /// <param name="candidates">
    /// The files to consider. Only files that would otherwise be processed need deduplicating;
    /// hashing the whole library to spare work on files nobody is touching is wasted effort.
    /// </param>
    public DeduplicationReport Apply(
        JobStore store,
        IReadOnlyList<FileRecord> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(candidates);

        var byHash = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);

        foreach (var record in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-use a hash already recorded for this file. Register() clears it whenever the
            // file's fingerprint changes, so a stale hash cannot survive an edit.
            var hash = record.ContentHash;
            if (string.IsNullOrEmpty(hash))
            {
                hash = TryHash(record.Path);
                if (hash is null)
                    continue;
            }

            if (!byHash.TryGetValue(hash, out var list))
                byHash[hash] = list = [];
            list.Add(record);
        }

        var groups = new List<DuplicateGroup>();

        foreach (var (hash, members) in byHash)
        {
            if (members.Count == 1)
            {
                store.SetContentIdentity(members[0].Path, hash, duplicateOf: null);
                continue;
            }

            var ordered = members.OrderBy(r => r.Path, PrimaryPreference).ToArray();
            var primary = ordered[0];
            var copies = ordered.Skip(1).ToArray();

            store.SetContentIdentity(primary.Path, hash, duplicateOf: null);

            foreach (var copy in copies)
            {
                store.SetContentIdentity(copy.Path, hash, duplicateOf: primary.Path);
                store.SetAction(copy.Path, ClassAction.CopyFromDuplicate);
            }

            groups.Add(new DuplicateGroup(
                primary.Path, copies.Select(c => c.Path).ToArray(), hash, primary.PageCount));

            _logger.LogInformation(
                "{Count} copies of the same document; recognising {Primary} and copying to the rest",
                members.Count, primary.Path);
        }

        return new DeduplicationReport(candidates.Count, byHash.Count, groups);
    }

    /// <summary>
    /// Chooses which copy to treat as the original: the one nearest the top of the tree, then the
    /// one with the shortest name. That favours <c>2235_lg.pdf</c> over
    /// <c>_OCR_QUEUE\005_2235_lg.pdf</c> without needing to know anything about either folder.
    /// </summary>
    private static readonly IComparer<string> PrimaryPreference =
        Comparer<string>.Create((a, b) =>
        {
            var depth = Depth(a).CompareTo(Depth(b));
            if (depth != 0) return depth;

            var length = Path.GetFileName(a).Length.CompareTo(Path.GetFileName(b).Length);
            if (length != 0) return length;

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);

    private static string? TryHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
