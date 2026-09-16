using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManualForge.Core.Geometry;
using ManualForge.Core.Ocr;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.State;

/// <summary>
/// Stores recognised words in the same SQLite database as the rest of the job state, so resume
/// survives a crash, a reboot or a cancelled run rather than just an exception.
/// </summary>
public sealed class SqlitePageOcrCache : IPageOcrCache, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqlitePageOcrCache(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS page_ocr (
                path         TEXT NOT NULL,
                page_number  INTEGER NOT NULL,
                settings     TEXT NOT NULL,
                words_json   TEXT NOT NULL,
                updated_utc  TEXT NOT NULL,
                PRIMARY KEY (path, page_number, settings)
            );
            """;
        command.ExecuteNonQuery();

        // Added after the first full run. Rows written before it report zero, which callers read
        // as "not known" and fall back to rasterising, so nothing has to be discarded.
        AddColumnIfMissing("pixel_width", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("pixel_height", "INTEGER NOT NULL DEFAULT 0");
    }

    private void AddColumnIfMissing(string name, string definition)
    {
        using var existing = _connection.CreateCommand();
        existing.CommandText = "SELECT 1 FROM pragma_table_info('page_ocr') WHERE name = $n";
        existing.Parameters.AddWithValue("$n", name);
        if (existing.ExecuteScalar() is not null)
            return;

        using var add = _connection.CreateCommand();
        add.CommandText = $"ALTER TABLE page_ocr ADD COLUMN {name} {definition}";
        add.ExecuteNonQuery();
    }

    public string DatabasePath { get; }

    public CachedPage? TryGet(string documentPath, int pageNumber, string settingsFingerprint)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT words_json, pixel_width, pixel_height FROM page_ocr " +
            "WHERE path = $p AND page_number = $n AND settings = $s";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(documentPath));
        command.Parameters.AddWithValue("$n", pageNumber);
        command.Parameters.AddWithValue("$s", settingsFingerprint);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize(reader.GetString(0), PageOcrJsonContext.Default.StoredWordArray);
            if (stored is null)
                return null;

            var words = stored.Select(w => new RecognisedWord(w.T, new RectD(w.X, w.Y, w.W, w.H), w.C)).ToArray();
            return new CachedPage(reader.GetInt32(1), reader.GetInt32(2), words);
        }
        catch (JsonException)
        {
            // Unreadable cache entry: recognise the page again rather than trusting it.
            return null;
        }
    }

    public void Save(string documentPath, int pageNumber, string settingsFingerprint, CachedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var stored = page.Words
            .Select(w => new StoredWord(w.Text, w.BoxPx.X, w.BoxPx.Y, w.BoxPx.Width, w.BoxPx.Height, w.Confidence))
            .ToArray();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO page_ocr (path, page_number, settings, words_json, updated_utc, pixel_width, pixel_height)
            VALUES ($p, $n, $s, $j, $u, $w, $h)
            ON CONFLICT(path, page_number, settings) DO UPDATE SET
                words_json = excluded.words_json, updated_utc = excluded.updated_utc,
                pixel_width = excluded.pixel_width, pixel_height = excluded.pixel_height
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(documentPath));
        command.Parameters.AddWithValue("$n", pageNumber);
        command.Parameters.AddWithValue("$s", settingsFingerprint);
        command.Parameters.AddWithValue("$j", JsonSerializer.Serialize(stored, PageOcrJsonContext.Default.StoredWordArray));
        command.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$w", page.PixelWidth);
        command.Parameters.AddWithValue("$h", page.PixelHeight);
        command.ExecuteNonQuery();
    }

    public void Clear(string documentPath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM page_ocr WHERE path = $p";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(documentPath));
        command.ExecuteNonQuery();
    }

    /// <summary>Pages cached for a document under these settings. Used for progress reporting.</summary>
    public int CachedPageCount(string documentPath, string settingsFingerprint)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM page_ocr WHERE path = $p AND settings = $s";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(documentPath));
        command.Parameters.AddWithValue("$s", settingsFingerprint);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

}

/// <summary>
/// A word as stored. Deliberately terse: a 639-page manual holds a few hundred thousand of these,
/// and the property names are repeated in every one.
/// </summary>
internal sealed record StoredWord(
    [property: JsonPropertyName("t")] string T,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H,
    [property: JsonPropertyName("c")] double C);

[JsonSerializable(typeof(StoredWord[]))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class PageOcrJsonContext : JsonSerializerContext;
