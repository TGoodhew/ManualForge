using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Auditing;

/// <summary>One document's standing in the audit, as the store holds it.</summary>
public sealed record AuditedDocument(
    string Path,
    string Title,
    string ContentHash,
    int PageCount,
    int FlaggedPages,
    int DrawnPages,
    int RepairedPages,
    int RecoverableCharacters,
    int SuggestedDpi,
    DocumentVerdict Verdict,
    DateTimeOffset AuditedUtc,
    string? Error)
{
    public int OutstandingPages => Math.Max(0, FlaggedPages - RepairedPages);

    public bool IsRepaired => FlaggedPages > 0 && OutstandingPages == 0;
}

/// <summary>OCR text recovered for one page, and how far it is to be trusted.</summary>
public sealed record PageRepair(
    string Path,
    int PageNumber,
    string ContentHash,
    int Dpi,
    string OcrText,
    double MeanConfidence,
    int WordCount,
    DateTimeOffset RepairedUtc);

/// <summary>
/// Where the audit's findings and the repair's recovered text live.
///
/// <para>
/// Deliberately not the search index. The index is derived from the library and is rebuilt whenever
/// a file changes; the findings are expensive to produce and must outlive that. Keeping them apart
/// also means the audit can be run, read and argued with before anything is re-indexed, which is
/// the order the work actually wants to happen in.
/// </para>
///
/// <para>
/// The recovered text is stored here rather than written back into the PDFs, and that is the whole
/// safety argument for the repair. A page that already carries a text layer must never be given a
/// second one — an extractor interleaves the two character by character and the document comes out
/// less searchable than it went in. Merging happens at index time, from this store, and the source
/// files are never touched.
/// </para>
/// </summary>
public sealed class DoctorStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public DoctorStore(string databasePath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);

        if (!readOnly)
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        _connection.Open();

        if (readOnly)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS audited_documents (
                path              TEXT PRIMARY KEY,
                title             TEXT NOT NULL,
                content_hash      TEXT NOT NULL,
                page_count        INTEGER NOT NULL,
                flagged_pages     INTEGER NOT NULL,
                drawn_pages       INTEGER NOT NULL DEFAULT 0,
                recoverable_chars INTEGER NOT NULL,
                suggested_dpi     INTEGER NOT NULL,
                verdict           TEXT NOT NULL DEFAULT 'Sound',
                audited_utc       TEXT NOT NULL,
                error             TEXT
            );

            CREATE TABLE IF NOT EXISTS page_findings (
                path              TEXT NOT NULL,
                page_number       INTEGER NOT NULL,
                verdict           TEXT NOT NULL,
                kind              TEXT NOT NULL DEFAULT 'Drawn',
                glyphs            INTEGER NOT NULL,
                decoded           INTEGER NOT NULL,
                path_ops          INTEGER NOT NULL,
                text_ops          INTEGER NOT NULL,
                ink               REAL NOT NULL,
                uncovered_ink     REAL NOT NULL,
                blobs             INTEGER NOT NULL,
                recoverable_chars INTEGER NOT NULL,
                suggested_dpi     INTEGER NOT NULL,
                signals           TEXT NOT NULL,
                PRIMARY KEY (path, page_number)
            );

            CREATE TABLE IF NOT EXISTS page_repairs (
                path            TEXT NOT NULL,
                page_number     INTEGER NOT NULL,
                content_hash    TEXT NOT NULL,
                dpi             INTEGER NOT NULL,
                ocr_text        TEXT NOT NULL,
                mean_confidence REAL NOT NULL,
                word_count      INTEGER NOT NULL,
                repaired_utc    TEXT NOT NULL,
                PRIMARY KEY (path, page_number)
            );
            """;
        command.ExecuteNonQuery();
    }

    public string DatabasePath { get; }

    /// <summary>Where the audit lives for a given library, alongside the search index.</summary>
    public static string DefaultPathFor(string root) =>
        Path.Combine(Path.GetFullPath(root), "_Originals", "manualforge-doctor.db");

    /// <summary>Replaces everything held about one document.</summary>
    public void Save(DocumentAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var full = Path.GetFullPath(audit.Path);

        using var transaction = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM page_findings WHERE path = $p";
            clear.Parameters.AddWithValue("$p", full);
            clear.ExecuteNonQuery();
        }

        using (var document = _connection.CreateCommand())
        {
            document.Transaction = transaction;
            document.CommandText = """
                INSERT INTO audited_documents
                    (path, title, content_hash, page_count, flagged_pages, drawn_pages,
                     recoverable_chars, suggested_dpi, verdict, audited_utc, error)
                VALUES ($p, $t, $h, $n, $f, $dp, $r, $d, $v, $u, $e)
                ON CONFLICT(path) DO UPDATE SET
                    title = excluded.title,
                    content_hash = excluded.content_hash,
                    page_count = excluded.page_count,
                    flagged_pages = excluded.flagged_pages,
                    drawn_pages = excluded.drawn_pages,
                    recoverable_chars = excluded.recoverable_chars,
                    suggested_dpi = excluded.suggested_dpi,
                    verdict = excluded.verdict,
                    audited_utc = excluded.audited_utc,
                    error = excluded.error
                """;
            document.Parameters.AddWithValue("$p", full);
            document.Parameters.AddWithValue("$t", audit.Title);
            document.Parameters.AddWithValue("$h", audit.ContentHash);
            document.Parameters.AddWithValue("$n", audit.PageCount);
            document.Parameters.AddWithValue("$f", audit.FlaggedPageCount);
            document.Parameters.AddWithValue("$dp", audit.DrawnPageCount);
            document.Parameters.AddWithValue("$r", audit.EstimatedRecoverableCharacters);
            document.Parameters.AddWithValue("$d", audit.SuggestedOcrDpi);
            document.Parameters.AddWithValue("$v", audit.Verdict.ToString());
            document.Parameters.AddWithValue("$u", Now());
            document.Parameters.AddWithValue("$e", (object?)audit.Error ?? DBNull.Value);
            document.ExecuteNonQuery();
        }

        // Only the interesting pages are kept. Writing a row for all 100,830 pages of the library
        // would turn a report into a second database of its own, and a page the audit found
        // nothing on has nothing to say later.
        foreach (var page in audit.Pages.Where(p => p.Verdict != PageVerdict.Fine))
        {
            using var finding = _connection.CreateCommand();
            finding.Transaction = transaction;
            finding.CommandText = """
                INSERT INTO page_findings
                    (path, page_number, verdict, kind, glyphs, decoded, path_ops, text_ops,
                     ink, uncovered_ink, blobs, recoverable_chars, suggested_dpi, signals)
                VALUES ($p, $n, $v, $k, $g, $d, $po, $to, $i, $u, $b, $r, $dpi, $s)
                """;
            finding.Parameters.AddWithValue("$p", full);
            finding.Parameters.AddWithValue("$n", page.PageNumber);
            finding.Parameters.AddWithValue("$v", page.Verdict.ToString());
            finding.Parameters.AddWithValue("$k", page.Kind.ToString());
            finding.Parameters.AddWithValue("$g", page.GlyphsDrawn);
            finding.Parameters.AddWithValue("$d", page.CharactersDecoded);
            finding.Parameters.AddWithValue("$po", page.PathPaintOperations);
            finding.Parameters.AddWithValue("$to", page.TextShowOperations);
            finding.Parameters.AddWithValue("$i", page.Ink.InkFraction);
            finding.Parameters.AddWithValue("$u", page.Ink.UncoveredInkFraction);
            finding.Parameters.AddWithValue("$b", page.Ink.GlyphLikeBlobs);
            finding.Parameters.AddWithValue("$r", page.EstimatedRecoverableCharacters);
            finding.Parameters.AddWithValue("$dpi", page.SuggestedOcrDpi);
            finding.Parameters.AddWithValue("$s", page.SignalSummary);
            finding.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>What was recorded for a document last time, so an unchanged file can be skipped.</summary>
    public string? ContentHashOf(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT content_hash FROM audited_documents WHERE path = $p";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        return command.ExecuteScalar() as string;
    }

    /// <summary>Audited documents that have at least one flagged page, worst first.</summary>
    public IReadOnlyList<AuditedDocument> Flagged(int limit = int.MaxValue)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT a.path, a.title, a.content_hash, a.page_count, a.flagged_pages, a.drawn_pages,
                   (SELECT COUNT(*) FROM page_repairs r
                     WHERE r.path = a.path AND r.content_hash = a.content_hash),
                   a.recoverable_chars, a.suggested_dpi, a.verdict, a.audited_utc, a.error
            FROM audited_documents a
            WHERE a.flagged_pages > 0
            ORDER BY a.recoverable_chars DESC, a.flagged_pages DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        return Read(command);
    }

    public AuditedDocument? Document(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT a.path, a.title, a.content_hash, a.page_count, a.flagged_pages, a.drawn_pages,
                   (SELECT COUNT(*) FROM page_repairs r
                     WHERE r.path = a.path AND r.content_hash = a.content_hash),
                   a.recoverable_chars, a.suggested_dpi, a.verdict, a.audited_utc, a.error
            FROM audited_documents a
            WHERE a.path = $p
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        return Read(command).FirstOrDefault();
    }

    private static List<AuditedDocument> Read(SqliteCommand command)
    {
        var documents = new List<AuditedDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            documents.Add(new AuditedDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                Enum.TryParse<DocumentVerdict>(reader.GetString(9), out var verdict)
                    ? verdict
                    : DocumentVerdict.Sound,
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return documents;
    }

    /// <summary>Findings for one document, in page order.</summary>
    public IReadOnlyList<PageFinding> Findings(string path, PageVerdict? verdict = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT page_number, verdict, kind, glyphs, decoded, path_ops, text_ops,
                   ink, uncovered_ink, blobs, recoverable_chars, suggested_dpi, signals
            FROM page_findings
            WHERE path = $p AND ($v IS NULL OR verdict = $v)
            ORDER BY page_number
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$v", (object?)verdict?.ToString() ?? DBNull.Value);

        var findings = new List<PageFinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            findings.Add(new PageFinding(
                reader.GetInt32(0),
                Enum.Parse<PageVerdict>(reader.GetString(1)),
                Enum.TryParse<PageKind>(reader.GetString(2), out var kind) ? kind : PageKind.Drawn,
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetDouble(7), reader.GetDouble(8), reader.GetInt32(9),
                reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12)));
        }

        return findings;
    }

    /// <summary>Records what OCR recovered from one page.</summary>
    public void SaveRepair(PageRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO page_repairs
                (path, page_number, content_hash, dpi, ocr_text, mean_confidence, word_count, repaired_utc)
            VALUES ($p, $n, $h, $d, $t, $c, $w, $u)
            ON CONFLICT(path, page_number) DO UPDATE SET
                content_hash = excluded.content_hash,
                dpi = excluded.dpi,
                ocr_text = excluded.ocr_text,
                mean_confidence = excluded.mean_confidence,
                word_count = excluded.word_count,
                repaired_utc = excluded.repaired_utc
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(repair.Path));
        command.Parameters.AddWithValue("$n", repair.PageNumber);
        command.Parameters.AddWithValue("$h", repair.ContentHash);
        command.Parameters.AddWithValue("$d", repair.Dpi);
        command.Parameters.AddWithValue("$t", repair.OcrText);
        command.Parameters.AddWithValue("$c", repair.MeanConfidence);
        command.Parameters.AddWithValue("$w", repair.WordCount);
        command.Parameters.AddWithValue("$u", Now());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Recovered text for a document, keyed by page. Only repairs made against the file as it
    /// stands now are returned: if the PDF has changed since, its page numbering may have too, and
    /// text recovered from the old one must not be attached to the new.
    /// </summary>
    public IReadOnlyDictionary<int, PageRepair> Repairs(string path, string contentHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT page_number, content_hash, dpi, ocr_text, mean_confidence, word_count, repaired_utc
            FROM page_repairs
            WHERE path = $p AND content_hash = $h
            ORDER BY page_number
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$h", contentHash);

        var repairs = new Dictionary<int, PageRepair>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pageNumber = reader.GetInt32(0);
            repairs[pageNumber] = new PageRepair(
                path, pageNumber, reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
        }

        return repairs;
    }

    /// <summary>
    /// A one-line reckoning of where the audit stands, which is what a search result needs before
    /// it can honestly claim an absence is real.
    /// </summary>
    public AuditSummary Summary()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM audited_documents),
                   (SELECT COUNT(*) FROM audited_documents WHERE flagged_pages > 0),
                   (SELECT COALESCE(SUM(flagged_pages), 0) FROM audited_documents),
                   (SELECT COALESCE(SUM(drawn_pages), 0) FROM audited_documents),
                   (SELECT COUNT(*) FROM page_repairs r JOIN audited_documents a
                        ON a.path = r.path AND a.content_hash = r.content_hash)
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new AuditSummary(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4));
    }

    /// <summary>Documents with flagged pages that have not been repaired, worst first.</summary>
    public IReadOnlyList<AuditedDocument> Outstanding(int limit = 5) =>
        Flagged(int.MaxValue).Where(d => d.OutstandingPages > 0).Take(limit).ToArray();

    /// <summary>
    /// A random sample of pages the audit flagged, for checking by eye.
    ///
    /// <para>
    /// Seeded, so the sample can be re-drawn and re-checked by somebody else. An error rate quoted
    /// from a sample nobody else can reproduce is not much better than no error rate at all.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Path, int PageNumber)> SampleFlagged(
        int count, int seed, PageKind? kind = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path, page_number FROM page_findings
            WHERE verdict = 'UnderExtracted' AND ($k IS NULL OR kind = $k)
            ORDER BY page_number, path
            """;
        command.Parameters.AddWithValue("$k", (object?)kind?.ToString() ?? DBNull.Value);

        var all = new List<(string, int)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                all.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return Draw(all, count, seed);
    }

    /// <summary>
    /// A random sample of pages the audit looked at and did not flag — the other half of the
    /// question, and the half a detector's author is least inclined to ask.
    /// </summary>
    public IReadOnlyList<(string Path, int PageNumber)> SampleUnflagged(int count, int seed)
    {
        using var documents = _connection.CreateCommand();
        documents.CommandText =
            "SELECT path, page_count FROM audited_documents WHERE page_count > 0 AND error IS NULL";

        var candidates = new List<(string Path, int Pages)>();
        using (var reader = documents.ExecuteReader())
        {
            while (reader.Read())
                candidates.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        if (candidates.Count == 0)
            return [];

        var flagged = new HashSet<(string, int)>();
        using (var findings = _connection.CreateCommand())
        {
            findings.CommandText = "SELECT path, page_number FROM page_findings";
            using var reader = findings.ExecuteReader();
            while (reader.Read())
                flagged.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var random = new Random(seed);
        var picked = new List<(string, int)>();
        var seen = new HashSet<(string, int)>();

        for (var attempt = 0; attempt < count * 200 && picked.Count < count; attempt++)
        {
            var (path, pages) = candidates[random.Next(candidates.Count)];
            var page = random.Next(1, pages + 1);

            if (flagged.Contains((path, page)) || !seen.Add((path, page)))
                continue;

            picked.Add((path, page));
        }

        return picked;
    }

    private static List<(string, int)> Draw(List<(string, int)> all, int count, int seed)
    {
        var random = new Random(seed);
        for (var i = all.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (all[i], all[j]) = (all[j], all[i]);
        }

        return all.Take(count).ToList();
    }

    /// <summary>Forgets a document entirely: findings, repairs and all.</summary>
    public void Remove(string path)
    {
        var full = Path.GetFullPath(path);
        using var transaction = _connection.BeginTransaction();

        foreach (var table in new[] { "page_findings", "page_repairs", "audited_documents" })
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE path = $p";
            command.Parameters.AddWithValue("$p", full);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

public sealed record PageFinding(
    int PageNumber,
    PageVerdict Verdict,
    PageKind Kind,
    int GlyphsDrawn,
    int CharactersDecoded,
    int PathPaintOperations,
    int TextShowOperations,
    double InkFraction,
    double UncoveredInkFraction,
    int GlyphLikeBlobs,
    int RecoverableCharacters,
    int SuggestedDpi,
    string Signals);

public sealed record AuditSummary(
    int DocumentsAudited,
    int DocumentsFlagged,
    long FlaggedPages,
    long DrawnPages,
    long RepairedPages)
{
    public long OutstandingPages => Math.Max(0, FlaggedPages - RepairedPages);
}
