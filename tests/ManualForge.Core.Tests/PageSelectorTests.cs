using ManualForge.Core.Benchmarking;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Tests;

/// <summary>
/// Both limits in the selector exist because the obvious version produced a bad set, twice.
/// Deduplicating by file name let one page in repeatedly under different names — two manuals
/// carrying the same schematic — and deduplicating by geometry instead let a single densely-drawn
/// manual supply eleven of sixteen slots. Neither showed up in a count; both showed up in the list.
/// </summary>
public sealed class PageSelectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public PageSelectorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Pdf(string name)
    {
        var path = Path.Combine(_directory, name);
        if (!File.Exists(path))
            File.WriteAllText(path, "not a real pdf; the selector only checks it is there");
        return path;
    }

    /// <summary>Builds an audit database holding just the columns the selector reads.</summary>
    private string Database(params (string Path, int Page, string Kind, int PathOps, int Blobs, int Glyphs, double Uncovered)[] rows)
    {
        var path = Path.Combine(_directory, "doctor.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE page_findings (
                  path TEXT, page_number INTEGER, verdict TEXT, kind TEXT, glyphs INTEGER,
                  decoded INTEGER, path_ops INTEGER, text_ops INTEGER, ink REAL,
                  uncovered_ink REAL, blobs INTEGER, recoverable_chars INTEGER,
                  suggested_dpi INTEGER, signals TEXT)
                """;
            create.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO page_findings
                  (path, page_number, verdict, kind, glyphs, decoded, path_ops, text_ops,
                   ink, uncovered_ink, blobs, recoverable_chars, suggested_dpi, signals)
                VALUES ($p, $n, 'UnderExtracted', $k, $g, 0, $po, 0, 0.1, $u, $b, 0, 300, '')
                """;
            insert.Parameters.AddWithValue("$p", row.Path);
            insert.Parameters.AddWithValue("$n", row.Page);
            insert.Parameters.AddWithValue("$k", row.Kind);
            insert.Parameters.AddWithValue("$g", row.Glyphs);
            insert.Parameters.AddWithValue("$po", row.PathOps);
            insert.Parameters.AddWithValue("$b", row.Blobs);
            insert.Parameters.AddWithValue("$u", row.Uncovered);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void NoDocumentMaySupplyMoreThanItsShare()
    {
        var busy = Pdf("busy.pdf");
        var other = Pdf("other.pdf");
        var database = Database(
            (busy, 1, "Drawn", 9000, 0, 30, 0),
            (busy, 2, "Drawn", 8000, 0, 30, 0),
            (busy, 3, "Drawn", 7000, 0, 30, 0),
            (busy, 4, "Drawn", 6000, 0, 30, 0),
            (other, 1, "Drawn", 5000, 0, 30, 0));

        var chosen = new PageSelector(database).Select(PageShape.Drawn, count: 4, perDocument: 2);

        Assert.Equal(3, chosen.Count);
        Assert.Equal(2, chosen.Count(p => p.Path == busy));
        Assert.Single(chosen, p => p.Path == other);
    }

    [Fact]
    public void TheSamePageUnderTwoNamesIsTakenOnce()
    {
        // Identical geometry: one schematic printed in two manuals.
        var a = Pdf("LC574AL.pdf");
        var b = Pdf("LeCroy-5674 User.pdf");
        var database = Database(
            (a, 21, "Drawn", 26412, 0, 78, 0),
            (b, 21, "Drawn", 26412, 0, 78, 0));

        var chosen = new PageSelector(database).Select(PageShape.Drawn, count: 5);

        Assert.Single(chosen);
    }

    [Fact]
    public void TheDrawingHasToOutweighTheLabels()
    {
        var diagram = Pdf("diagram.pdf");
        var table = Pdf("table.pdf");
        var database = Database(
            (table, 1, "Drawn", 900, 0, 1100, 0),      // draws its rules, but is mostly text
            (diagram, 1, "Drawn", 30000, 0, 40, 0));   // a drawing with a few labels

        var chosen = new PageSelector(database).Select(PageShape.Drawn, count: 5);

        Assert.Equal(diagram, chosen[0].Path);
        Assert.True(chosen[0].DrawingPerLabel > chosen[^1].DrawingPerLabel);
    }

    [Fact]
    public void AScannedSheetIsFoundByItsInkRatherThanItsPaths()
    {
        // A photographed page has no path operations at all, so the drawn query cannot see it.
        var sheet = Pdf("sheet.pdf");
        var database = Database((sheet, 314, "Raster", 0, 13765, 26, 0.067));

        Assert.Empty(new PageSelector(database).Select(PageShape.Drawn, count: 5));
        Assert.Single(new PageSelector(database).Select(PageShape.Scanned, count: 5));
    }

    [Fact]
    public void APageWhoseFileHasGoneIsSkipped()
    {
        var present = Pdf("present.pdf");
        var database = Database(
            (Path.Combine(_directory, "deleted.pdf"), 1, "Drawn", 40000, 0, 30, 0),
            (present, 1, "Drawn", 9000, 0, 30, 0));

        var chosen = new PageSelector(database).Select(PageShape.Drawn, count: 5);

        Assert.Single(chosen);
        Assert.Equal(present, chosen[0].Path);
    }

    [Fact]
    public void AMissingDatabaseSaysWhatToRun()
    {
        var selector = new PageSelector(Path.Combine(_directory, "nothing-here.db"));
        var error = Assert.Throws<FileNotFoundException>(() => selector.Select(PageShape.Drawn, 5));
        Assert.Contains("doctor", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
