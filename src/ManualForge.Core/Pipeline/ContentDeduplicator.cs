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
    /// one whose name says most about what it is.
    ///
    /// The choice does not affect what any file ends up containing — the copies are identical, so
    /// the primary's result is equally valid for all of them. It decides which path the search
    /// index will cite for the document, which is why the second criterion is descriptiveness
    /// rather than brevity. Preferring the shortest name systematically picks part numbers over
    /// descriptions: `kei2015-sman.pdf` over `2015THD Service.pdf`, `LC574AL.pdf` over
    /// `LeCroy-5674 User.pdf`. Exactly backwards for a result a person has to recognise at a glance.
    ///
    /// Depth comes first because it needs no knowledge of any particular folder: a manual in the
    /// library root beats the same manual staged in a working subfolder, whatever that folder is
    /// called.
    /// </summary>
    private static readonly IComparer<string> PrimaryPreference =
        Comparer<string>.Create((a, b) =>
        {
            var depth = Depth(a).CompareTo(Depth(b));
            if (depth != 0) return depth;

            // More descriptive wins, so the comparison is reversed.
            var descriptive = Descriptiveness(b).CompareTo(Descriptiveness(a));
            if (descriptive != 0) return descriptive;

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// How much a filename says about its contents, approximated by the number of word-like runs
    /// in it. "HP 8340B, 41B Assembly Level Service" scores 4; "08340-90243" scores 0.
    /// </summary>
    private static int Descriptiveness(string path)
        => System.Text.RegularExpressions.Regex.Matches(
            Path.GetFileNameWithoutExtension(path), "[A-Za-z]{3,}").Count;

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
