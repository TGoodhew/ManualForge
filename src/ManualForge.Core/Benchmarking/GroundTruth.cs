using System.Globalization;
using System.Text;

namespace ManualForge.Core.Benchmarking;

/// <summary>
/// What a page mostly is. The whole point of separating them: an engine that reads clean prose
/// beautifully can still make a mess of a parts table, and one average hides that completely.
/// </summary>
public enum PageKind
{
    /// <summary>Running text. The easy case, and the one most benchmarks stop at.</summary>
    Prose,

    /// <summary>A parts list or specification table: columns, part numbers, units.</summary>
    Table,

    /// <summary>A schematic or diagram: sparse labels, reference designators, rotated text.</summary>
    Schematic,

    /// <summary>Text wrapped around figures, or a page that is genuinely several of these.</summary>
    Mixed,
}

/// <summary>Where a page's truth text came from, which decides what a score from it may be called.</summary>
public enum TruthSource
{
    /// <summary>
    /// Read off the page by a person and corrected by hand. The only kind that measures accuracy on
    /// the material this application exists for: a scan, with its halftones, bleed-through and skew.
    /// </summary>
    HandCorrected,

    /// <summary>
    /// The publisher's own typesetting, taken from a born-digital page's text layer.
    ///
    /// <para>
    /// Free, exact, and easier than the real thing. A clean render of digital type is not a 1965
    /// photocopy, so a score against these is the recogniser's <b>floor</b> rather than its accuracy
    /// — and geometric questions, where the page's glyph positions are exact, are the ones it
    /// answers properly.
    /// </para>
    /// </summary>
    PublisherText,
}

/// <summary>One page of known-correct text to measure against.</summary>
public sealed record TruthPage(string ManualPath, int PageNumber, PageKind Kind, string Text)
{
    public string Label => $"{Path.GetFileNameWithoutExtension(ManualPath)} p{PageNumber}";

    /// <summary>Where this page's text came from. Defaults to hand-corrected, as older sets are.</summary>
    public TruthSource Source { get; init; } = TruthSource.HandCorrected;
}

/// <summary>
/// A folder of hand-corrected pages, and the manifest naming them.
///
/// <para>
/// Ground truth is the expensive part of a benchmark and the only part that cannot be automated, so
/// the shape here is chosen to make correcting a page as cheap as possible: one plain text file per
/// page, seeded from whatever the current engine produced, for a human to fix in a text editor.
/// Correcting is much faster than transcribing, and seeding does not bias the measurement because
/// what is being scored is a later run against the corrected file.
/// </para>
///
/// <para>
/// The manifest is CSV rather than anything cleverer because it is meant to be opened and edited.
/// </para>
/// </summary>
public sealed class GroundTruthSet
{
    public const string ManifestName = "manifest.csv";

    private GroundTruthSet(string root, IReadOnlyList<TruthPage> pages)
    {
        Root = root;
        Pages = pages;
    }

    public string Root { get; }

    public IReadOnlyList<TruthPage> Pages { get; }

    public IEnumerable<PageKind> Kinds => Pages.Select(p => p.Kind).Distinct().Order();

    /// <summary>Reads a set, skipping rows whose text file has gone.</summary>
    public static GroundTruthSet Load(string root, string? libraryRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.GetFullPath(root);
        var manifest = Path.Combine(full, ManifestName);

        if (!File.Exists(manifest))
            throw new FileNotFoundException($"No {ManifestName} in {full}. Seed one with `manualforge truth`.", manifest);

        var pages = new List<TruthPage>();

        foreach (var line in File.ReadAllLines(manifest).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            if (fields.Count < 4)
                continue;

            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageNumber))
                continue;

            if (!Enum.TryParse<PageKind>(fields[2], ignoreCase: true, out var kind))
                kind = PageKind.Mixed;

            var textPath = Path.Combine(full, fields[3]);
            if (!File.Exists(textPath))
                continue;

            var manualPath = fields[0];
            if (libraryRoot is not null && !Path.IsPathRooted(manualPath))
                manualPath = Path.Combine(Path.GetFullPath(libraryRoot), manualPath);

            // A set written before the column existed is hand-corrected by definition: nothing else
            // could produce one at the time.
            var source = fields.Count > 4 && Enum.TryParse<TruthSource>(fields[4], ignoreCase: true, out var parsed)
                ? parsed
                : TruthSource.HandCorrected;

            pages.Add(new TruthPage(manualPath, pageNumber, kind, File.ReadAllText(textPath))
            {
                Source = source,
            });
        }

        return new GroundTruthSet(full, pages);
    }

    /// <summary>
    /// Writes a manifest and one text file per page, seeded with text for a human to correct.
    ///
    /// <para>
    /// Nothing already here is lost. Existing text files are never overwritten, because corrections
    /// are the expensive thing; and rows already in the manifest are kept, because a set is built up
    /// over several calls - one per kind of page, since a kind applies to a whole call - and a
    /// manifest written fresh each time would quietly deregister everything seeded before it. The
    /// text files would still be sitting there, which is the worst version of the bug: the work
    /// looks present and is simply never measured.
    /// </para>
    /// </summary>
    public static int Seed(
        string root,
        string libraryRoot,
        IEnumerable<(string ManualPath, int PageNumber, PageKind Kind, string Seed)> pages,
        TruthSource source = TruthSource.HandCorrected)
    {
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);

        // Existing rows, keyed so a page seeded again replaces its own row rather than duplicating.
        var rowsByPage = new Dictionary<(string Manual, int Page), string>();
        var order = new List<(string Manual, int Page)>();

        var manifestPath = Path.Combine(full, ManifestName);
        if (File.Exists(manifestPath))
        {
            foreach (var line in File.ReadAllLines(manifestPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = ParseCsvLine(line);
                if (fields.Count < 4 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingPage))
                    continue;

                var key = (fields[0], existingPage);
                if (!rowsByPage.ContainsKey(key))
                    order.Add(key);

                rowsByPage[key] = line;
            }
        }

        var written = 0;

        foreach (var (manualPath, pageNumber, kind, seed) in pages)
        {
            var relativeManual = Path.IsPathRooted(manualPath)
                ? Path.GetRelativePath(Path.GetFullPath(libraryRoot), manualPath)
                : manualPath;

            var textFile = $"{Safe(Path.GetFileNameWithoutExtension(relativeManual))}.p{pageNumber:D4}.txt";
            var textPath = Path.Combine(full, textFile);

            if (!File.Exists(textPath))
            {
                File.WriteAllText(textPath, seed, new UTF8Encoding(false));
                written++;
            }

            var key = (relativeManual, pageNumber);
            if (!rowsByPage.ContainsKey(key))
                order.Add(key);

            rowsByPage[key] = string.Join(
                ',', Quote(relativeManual), pageNumber, kind, Quote(textFile), source);
        }

        var rows = new List<string> { "manual,page,kind,textFile,source" };
        rows.AddRange(order.Select(k => rowsByPage[k]));

        File.WriteAllLines(manifestPath, rows, new UTF8Encoding(false));
        return written;
    }

    private static string Safe(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    /// <summary>A CSV line, honouring quoted fields since paths contain commas often enough.</summary>
    public static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
