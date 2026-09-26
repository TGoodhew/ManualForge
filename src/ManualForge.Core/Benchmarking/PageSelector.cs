using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Benchmarking;

/// <summary>What sort of page to go looking for.</summary>
public enum PageShape
{
    /// <summary>A circuit diagram: the drawing dominates, labels are sparse.</summary>
    Drawn,

    /// <summary>A photographed schematic sheet: few glyphs, much ink nothing accounts for.</summary>
    Scanned,
}

/// <summary>One page the selector chose, and the numbers that chose it.</summary>
public sealed record SelectedPage(string Path, int PageNumber, int Weight, int Glyphs)
{
    public double DrawingPerLabel => Weight / (double)(Glyphs + 1);
}

/// <summary>
/// Chooses test pages from the audit's own geometry rather than from their text.
///
/// <para>
/// The first attempt at this picked pages by counting component designators in the extracted text,
/// which has a flaw that only shows up when you look at what it cannot reach: a page whose text
/// layer is empty has no designators to count, so the method systematically avoids the pages most
/// worth testing. The most drawing-dominated pages in this corpus carry <b>zero</b> glyphs — vector
/// schematics whose labels are drawn as paths — and no amount of text analysis will find one.
/// Counting path-painting operations finds them immediately.
/// </para>
///
/// <para>
/// It also could not tell a circuit diagram from a parts list, both being dense in designators.
/// Geometry can: a diagram paints thousands of paths and sets a handful of labels.
/// </para>
/// </summary>
public sealed class PageSelector(string doctorDatabasePath)
{
    private readonly string _path = doctorDatabasePath;

    /// <summary>
    /// Pages of the given shape, best first, at most <paramref name="perDocument"/> from any one
    /// document.
    ///
    /// <para>
    /// Both limits earn their place. Without a same-page signature the same page arrives repeatedly
    /// under different file names — two manuals carrying one schematic — and without a per-document
    /// cap a single densely-drawn manual supplies most of the set. Each was seen happening.
    /// </para>
    /// </summary>
    public IReadOnlyList<SelectedPage> Select(PageShape shape, int count, int perDocument = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perDocument, 1);

        if (!File.Exists(_path))
            throw new FileNotFoundException($"No audit database at {_path}. Run `manualforge doctor` first.");

        using var connection = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = shape switch
        {
            // Ranked by how far the drawing outweighs the labelling, which is what makes a page a
            // diagram rather than a table that happens to draw its rules.
            PageShape.Drawn =>
                """
                SELECT path, page_number, path_ops, glyphs
                FROM page_findings
                WHERE kind = 'Drawn' AND path_ops >= 800 AND glyphs BETWEEN 20 AND 1200
                ORDER BY (path_ops * 1.0 / (glyphs + 1)) DESC
                """,

            // A photographed sheet has no path operations at all - it is one image - so the signal
            // is ink the text layer cannot account for, in glyph-shaped clusters.
            _ =>
                """
                SELECT path, page_number, blobs, glyphs
                FROM page_findings
                WHERE kind = 'Raster' AND glyphs <= 250 AND blobs >= 150 AND uncovered_ink >= 0.04
                ORDER BY blobs DESC
                """,
        };

        var chosen = new List<SelectedPage>();
        var signatures = new HashSet<(int, int)>();
        var perDocumentCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var reader = command.ExecuteReader();
        while (reader.Read() && chosen.Count < count)
        {
            var path = reader.GetString(0);
            if (!File.Exists(path))
                continue;

            var page = new SelectedPage(path, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));

            // Identical geometry means the same page, whatever the file is called.
            if (!signatures.Add((page.Weight, page.Glyphs)))
                continue;

            var document = Path.GetFileName(path);
            perDocumentCount.TryGetValue(document, out var used);
            if (used >= perDocument)
                continue;

            perDocumentCount[document] = used + 1;
            chosen.Add(page);
        }

        return chosen;
    }
}
