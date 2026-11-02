using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Pipeline;

/// <summary>What became of one file given to <see cref="LibraryAdder"/>.</summary>
public enum AddKind
{
    /// <summary>Copied into the library root, at <see cref="AddResult.LibraryPath"/>.</summary>
    Added,

    /// <summary>The library already holds these exact bytes, at <see cref="AddResult.LibraryPath"/>.</summary>
    AlreadyHeld,

    /// <summary>Not a PDF, a folder, or not there; nothing was done.</summary>
    Refused,

    /// <summary>The copy failed; <see cref="AddResult.Detail"/> says why.</summary>
    Failed,
}

public sealed record AddResult(string Source, AddKind Kind, string? LibraryPath, string? Detail = null)
{
    /// <summary>Whether the name was taken, so the file went in under another one.</summary>
    public bool Renamed => Kind == AddKind.Added && LibraryPath is not null
        && !string.Equals(Path.GetFileName(Source), Path.GetFileName(LibraryPath), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Copies manuals into the root of a library, for <c>manualforge add</c> and the app's Add (#89).
///
/// <para>
/// A file the library already holds is not copied again. "Holds" means the same bytes, anywhere in
/// the library or among the originals kept in the originals tree: a manual that has been made
/// searchable no longer matches its download, but its original does, so downloading it again would
/// otherwise add it twice. Sizes are compared first, and only a file of the same size as one held is
/// hashed, so adding to a library on OneDrive does not read the library.
/// </para>
///
/// <para>
/// A name already taken gets " (2)", " (3)" and so on, the way Explorer names a copy. Taken means a
/// file of that name in the root, an original of that name in the originals tree, or the name a
/// silenced file was renamed to (<see cref="LibraryProcessor.RepairedPathFor"/>): adding a different
/// manual under any of those would, when it was processed, push the earlier original aside under
/// <c>_replaced</c>. Nothing in the library is ever overwritten.
/// </para>
///
/// <para>
/// Each file is copied under a temporary name that discovery does not see, and moved into place
/// once it is all there, so a copy that fails part-way never leaves half a PDF in the library.
/// </para>
/// </summary>
public sealed class LibraryAdder(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public IReadOnlyList<AddResult> Add(
        LibraryOptions options,
        IEnumerable<string> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);

        var root = Path.GetFullPath(options.Root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such library folder: {root}");

        var held = HeldBySize(root, options);
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AddResult>();

        foreach (var given in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = Path.GetFullPath(given);
            var result = AddOne(root, options, source, held, hashes);
            results.Add(result);

            if (result.Kind == AddKind.Added)
                _logger.LogInformation("Added {Source} to the library as {Path}", source, result.LibraryPath);
            else if (result.Kind == AddKind.AlreadyHeld)
                _logger.LogInformation("{Source} is already in the library, as {Path}", source, result.LibraryPath);
            else
                _logger.LogWarning("Did not add {Source}: {Detail}", source, result.Detail);
        }

        return results;
    }

    private AddResult AddOne(
        string root,
        LibraryOptions options,
        string source,
        Dictionary<long, List<string>> held,
        Dictionary<string, string?> hashes)
    {
        if (Directory.Exists(source))
            return new AddResult(source, AddKind.Refused, null, "a folder; select the PDFs in it instead");

        if (!File.Exists(source))
            return new AddResult(source, AddKind.Refused, null, "not found");

        if (!string.Equals(Path.GetExtension(source), ".pdf", StringComparison.OrdinalIgnoreCase))
            return new AddResult(source, AddKind.Refused, null, "not a PDF");

        long size;
        try
        {
            size = new FileInfo(source).Length;
            if (held.TryGetValue(size, out var sameSize))
            {
                var hash = HashOf(source, hashes);
                foreach (var candidate in sameSize)
                {
                    if (hash is not null && HashOf(candidate, hashes) == hash)
                        return new AddResult(source, AddKind.AlreadyHeld, candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AddResult(source, AddKind.Failed, null, ex.Message);
        }

        var destination = FreeName(root, options, Path.GetFileName(source));
        var partial = Path.Combine(root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.adding");
        try
        {
            File.Copy(source, partial, overwrite: false);
            if (new FileInfo(partial).Length != size)
                throw new IOException("the copy came out a different size from the file");

            File.Move(partial, destination, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            return new AddResult(source, AddKind.Failed, null, ex.Message);
        }

        // Held from now on, so the same file given twice in one go is added once.
        if (!held.TryGetValue(size, out var list))
            held[size] = list = [];
        list.Add(destination);
        hashes[destination] = hashes.GetValueOrDefault(source);

        return new AddResult(source, AddKind.Added, destination);
    }

    /// <summary>
    /// The name to add a file under: its own if free, else the first of "name (2).pdf", "name (3).pdf"
    /// and so on that is.
    /// </summary>
    public static string FreeName(string root, LibraryOptions options, string fileName)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        var candidate = fileName;
        for (var n = 2; IsTaken(root, options, candidate); n++)
            candidate = $"{stem} ({n}){extension}";

        return Path.Combine(root, candidate);
    }

    private static bool IsTaken(string root, LibraryOptions options, string fileName)
    {
        var inRoot = Path.Combine(root, fileName);
        return File.Exists(inRoot)
               || File.Exists(LibraryProcessor.RepairedPathFor(inRoot))
               || File.Exists(Path.Combine(root, options.OriginalsFolderName, fileName));
    }

    /// <summary>
    /// Every PDF the library holds, by size: the library itself and the originals tree, but not the
    /// folders kept apart from it (<see cref="LibraryOptions.ExcludedFolderNames"/>).
    /// </summary>
    private static Dictionary<long, List<string>> HeldBySize(string root, LibraryOptions options)
    {
        var excluded = options.ExcludedFolderNames
            .Select(name => Path.Combine(root, name) + Path.DirectorySeparatorChar)
            .ToArray();

        var held = new Dictionary<long, List<string>>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*.pdf", enumeration))
        {
            if (excluded.Any(e => info.FullName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!held.TryGetValue(info.Length, out var list))
                held[info.Length] = list = [];
            list.Add(info.FullName);
        }

        return held;
    }

    private static string? HashOf(string path, Dictionary<string, string?> hashes)
    {
        if (hashes.TryGetValue(path, out var known) && known is not null)
            return known;

        try
        {
            using var stream = File.OpenRead(path);
            return hashes[path] = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A held file that cannot be read cannot be shown to be the same; it is not.
            return hashes[path] = null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
