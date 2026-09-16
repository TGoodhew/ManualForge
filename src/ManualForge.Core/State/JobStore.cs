using System.Globalization;
using System.Security.Cryptography;
using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.State;

public enum FileStatus
{
    /// <summary>Discovered but not yet classified.</summary>
    Discovered,

    /// <summary>Classified, waiting for a decision or for work to start.</summary>
    Classified,

    /// <summary>Work has begun. A file left in this state is what a crash looks like.</summary>
    InProgress,

    /// <summary>Finished and verified.</summary>
    Completed,

    /// <summary>Deliberately left alone, per the class policy.</summary>
    Skipped,

    /// <summary>Work was attempted and failed.</summary>
    Failed,
}

public enum PageStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>Identity of a source file, used to notice when one changes underneath us.</summary>
public readonly record struct FileFingerprint(long SizeBytes, long ModifiedTicks, string ContentHash)
{
    /// <summary>
    /// Size, modification time and a hash of the first and last 256 KB. Hashing whole files would
    /// mean re-reading 5.9 GB on every resume; the ends of a PDF cover the header, the trailer and
    /// the cross-reference table, which is where any rewrite shows up.
    /// </summary>
    public static FileFingerprint Of(string path)
    {
        var info = new FileInfo(path);
        const int window = 256 * 1024;

        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();

        var buffer = new byte[window];
        var read = stream.Read(buffer, 0, window);
        sha.TransformBlock(buffer, 0, read, null, 0);

        if (info.Length > window * 2)
        {
            stream.Seek(-window, SeekOrigin.End);
            read = stream.Read(buffer, 0, window);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        var hash = Convert.ToHexString(sha.Hash!);

        return new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, hash);
    }
}

public sealed record FileRecord(
    string Path,
    FileStatus Status,
    FileFingerprint Fingerprint,
    int PageCount,
    TextClass TextClass,
    double AlphanumericPerPage,
    double PlausibleTokenRatio,
    double CommonWordShare,
    ModificationBlocker Blocker,
    ClassAction Action,
    string? OutputPath,
    string? OriginalPath,
    string? Error,
    int PagesCompleted);

/// <summary>
/// Per-file and per-page progress, kept in SQLite so a crash, a reboot or a cancelled run resumes
/// where it stopped rather than starting over.
///
/// Two properties matter and both are tested:
///
/// * <b>Resumable.</b> A file interrupted mid-way is left as InProgress with its finished pages
///   recorded, so the next run redoes only what is missing.
/// * <b>Idempotent.</b> Re-running over a finished folder does nothing at all, unless a source file
///   has changed — which the fingerprint detects, and which resets that file's state.
/// </summary>
public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public JobStore(string databasePath)
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
        Initialise();
    }

    public string DatabasePath { get; }

    private void Initialise()
    {
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS files (
                path              TEXT PRIMARY KEY,
                status            TEXT NOT NULL,
                size_bytes        INTEGER NOT NULL,
                modified_ticks    INTEGER NOT NULL,
                content_hash      TEXT NOT NULL,
                page_count        INTEGER NOT NULL DEFAULT 0,
                text_class        TEXT NOT NULL DEFAULT 'Unreadable',
                alnum_per_page    REAL NOT NULL DEFAULT 0,
                plausible_ratio   REAL NOT NULL DEFAULT 0,
                common_share      REAL NOT NULL DEFAULT 0,
                blocker           TEXT NOT NULL DEFAULT 'None',
                action            TEXT NOT NULL DEFAULT 'Skip',
                output_path       TEXT,
                original_path     TEXT,
                error             TEXT,
                updated_utc       TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pages (
                path          TEXT NOT NULL,
                page_number   INTEGER NOT NULL,
                status        TEXT NOT NULL,
                words_written INTEGER NOT NULL DEFAULT 0,
                deviation_pt  REAL NOT NULL DEFAULT 0,
                ocr_ms        INTEGER NOT NULL DEFAULT 0,
                error         TEXT,
                updated_utc   TEXT NOT NULL,
                PRIMARY KEY (path, page_number)
            );

            CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);
            CREATE INDEX IF NOT EXISTS idx_pages_path ON pages(path, status);
            """);
    }

    /// <summary>
    /// Registers a file, returning its record. If it is already known and unchanged the existing
    /// record comes back untouched; if the source has changed, its state and page progress are
    /// cleared so it is processed afresh.
    /// </summary>
    public FileRecord Register(string path)
    {
        var full = Path.GetFullPath(path);
        var fingerprint = FileFingerprint.Of(full);
        var existing = Find(full);

        if (existing is not null)
        {
            if (existing.Fingerprint == fingerprint)
                return existing;

            // The source changed since we last saw it. Anything recorded about it is now stale.
            ClearPages(full);
            Upsert(full, FileStatus.Discovered, fingerprint, 0, TextClass.Unreadable, 0, 0, 0,
                ModificationBlocker.None, ClassAction.Skip, null, null, null);
            return Find(full)!;
        }

        Upsert(full, FileStatus.Discovered, fingerprint, 0, TextClass.Unreadable, 0, 0, 0,
            ModificationBlocker.None, ClassAction.Skip, null, null, null);
        return Find(full)!;
    }

    public void RecordClassification(
        string path,
        DocumentClassification classification,
        PdfCapabilities capabilities,
        ClassAction action)
    {
        var full = Path.GetFullPath(path);
        var existing = Find(full) ?? Register(full);

        Upsert(full, FileStatus.Classified, existing.Fingerprint,
            classification.PageCount, classification.Class,
            classification.AlphanumericPerPage, classification.PlausibleTokenRatio, classification.CommonWordShare,
            capabilities.Blocker, action, existing.OutputPath, existing.OriginalPath, classification.Error);
    }

    public void SetStatus(string path, FileStatus status, string? error = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE files SET status = $s, error = $e, updated_utc = $u WHERE path = $p";
        command.Parameters.AddWithValue("$s", status.ToString());
        command.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$u", Now());
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Re-reads the fingerprint of whatever file is now at <paramref name="path"/> and records it.
    ///
    /// This has to happen when a file completes. Until then the stored fingerprint describes the
    /// *source* as it was before processing, but processing replaces that file with the searchable
    /// version — so without this the record describes a file that no longer exists there. The
    /// consequence is not theoretical: restoring an original from the originals tree then matches
    /// the stale fingerprint exactly, the file looks unchanged and already finished, and it is
    /// never reprocessed.
    /// </summary>
    public void UpdateFingerprint(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return;

        var fingerprint = FileFingerprint.Of(full);

        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE files SET size_bytes = $sz, modified_ticks = $mt, content_hash = $h, updated_utc = $u " +
            "WHERE path = $p";
        command.Parameters.AddWithValue("$sz", fingerprint.SizeBytes);
        command.Parameters.AddWithValue("$mt", fingerprint.ModifiedTicks);
        command.Parameters.AddWithValue("$h", fingerprint.ContentHash);
        command.Parameters.AddWithValue("$u", Now());
        command.Parameters.AddWithValue("$p", full);
        command.ExecuteNonQuery();
    }

    public void SetPaths(string path, string? outputPath, string? originalPath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE files SET output_path = $o, original_path = $r, updated_utc = $u WHERE path = $p";
        command.Parameters.AddWithValue("$o", (object?)outputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$r", (object?)originalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$u", Now());
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    public void RecordPage(
        string path, int pageNumber, PageStatus status,
        int wordsWritten = 0, double deviationPt = 0, long ocrMilliseconds = 0, string? error = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pages (path, page_number, status, words_written, deviation_pt, ocr_ms, error, updated_utc)
            VALUES ($p, $n, $s, $w, $d, $m, $e, $u)
            ON CONFLICT(path, page_number) DO UPDATE SET
                status = excluded.status, words_written = excluded.words_written,
                deviation_pt = excluded.deviation_pt, ocr_ms = excluded.ocr_ms,
                error = excluded.error, updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$n", pageNumber);
        command.Parameters.AddWithValue("$s", status.ToString());
        command.Parameters.AddWithValue("$w", wordsWritten);
        command.Parameters.AddWithValue("$d", deviationPt);
        command.Parameters.AddWithValue("$m", ocrMilliseconds);
        command.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$u", Now());
        command.ExecuteNonQuery();
    }

    /// <summary>Page numbers already finished for a file, so a resumed run can skip them.</summary>
    public IReadOnlySet<int> CompletedPages(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT page_number FROM pages WHERE path = $p AND status = 'Completed'";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));

        var pages = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            pages.Add(reader.GetInt32(0));
        return pages;
    }

    /// <summary>
    /// Puts every deliberately-skipped file back to Discovered so the next survey reclassifies it.
    ///
    /// Skipping is a decision, not a fact about the file, and decisions change: a policy is
    /// widened, or a file was skipped for a reason that has since been fixed. Without this the only
    /// way to revisit a skipped file is to delete the state database, which throws away the history
    /// of everything else as well.
    /// </summary>
    public int ResetSkipped()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE files SET status = 'Discovered', page_count = 0, error = NULL, updated_utc = $u " +
            "WHERE status = 'Skipped'";
        command.Parameters.AddWithValue("$u", Now());
        return command.ExecuteNonQuery();
    }

    public void ClearPages(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM pages WHERE path = $p";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    public FileRecord? Find(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, f.status, f.size_bytes, f.modified_ticks, f.content_hash, f.page_count,
                   f.text_class, f.alnum_per_page, f.plausible_ratio, f.common_share, f.blocker,
                   f.action, f.output_path, f.original_path, f.error,
                   (SELECT COUNT(*) FROM pages p WHERE p.path = f.path AND p.status = 'Completed')
            FROM files f WHERE f.path = $p
            """;
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public IReadOnlyList<FileRecord> All()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, f.status, f.size_bytes, f.modified_ticks, f.content_hash, f.page_count,
                   f.text_class, f.alnum_per_page, f.plausible_ratio, f.common_share, f.blocker,
                   f.action, f.output_path, f.original_path, f.error,
                   (SELECT COUNT(*) FROM pages p WHERE p.path = f.path AND p.status = 'Completed')
            FROM files f ORDER BY f.path
            """;

        var records = new List<FileRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(ReadRecord(reader));
        return records;
    }

    /// <summary>
    /// Files still needing work: everything whose action is not Skip and which is not already
    /// Completed. This is what makes a second run over a finished folder a no-op.
    /// </summary>
    public IReadOnlyList<FileRecord> Outstanding()
        => All().Where(r => r.Action != ClassAction.Skip
                         && r.Status is not (FileStatus.Completed or FileStatus.Skipped))
                .ToList();

    public IReadOnlyDictionary<TextClass, int> ClassCounts()
        => All().GroupBy(r => r.TextClass).ToDictionary(g => g.Key, g => g.Count());

    private static FileRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<FileStatus>(reader.GetString(1)),
        new FileFingerprint(reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)),
        reader.GetInt32(5),
        Enum.Parse<TextClass>(reader.GetString(6)),
        reader.GetDouble(7),
        reader.GetDouble(8),
        reader.GetDouble(9),
        Enum.Parse<ModificationBlocker>(reader.GetString(10)),
        Enum.Parse<ClassAction>(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.GetInt32(15));

    private void Upsert(
        string path, FileStatus status, FileFingerprint fingerprint, int pageCount, TextClass textClass,
        double alnumPerPage, double plausibleRatio, double commonShare,
        ModificationBlocker blocker, ClassAction action,
        string? outputPath, string? originalPath, string? error)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files (path, status, size_bytes, modified_ticks, content_hash, page_count,
                               text_class, alnum_per_page, plausible_ratio, common_share, blocker,
                               action, output_path, original_path, error, updated_utc)
            VALUES ($p, $s, $sz, $mt, $h, $pc, $tc, $a, $pl, $cw, $b, $ac, $o, $r, $e, $u)
            ON CONFLICT(path) DO UPDATE SET
                status = excluded.status, size_bytes = excluded.size_bytes,
                modified_ticks = excluded.modified_ticks, content_hash = excluded.content_hash,
                page_count = excluded.page_count, text_class = excluded.text_class,
                alnum_per_page = excluded.alnum_per_page, plausible_ratio = excluded.plausible_ratio,
                common_share = excluded.common_share, blocker = excluded.blocker,
                action = excluded.action, output_path = excluded.output_path,
                original_path = excluded.original_path, error = excluded.error,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$p", path);
        command.Parameters.AddWithValue("$s", status.ToString());
        command.Parameters.AddWithValue("$sz", fingerprint.SizeBytes);
        command.Parameters.AddWithValue("$mt", fingerprint.ModifiedTicks);
        command.Parameters.AddWithValue("$h", fingerprint.ContentHash);
        command.Parameters.AddWithValue("$pc", pageCount);
        command.Parameters.AddWithValue("$tc", textClass.ToString());
        command.Parameters.AddWithValue("$a", alnumPerPage);
        command.Parameters.AddWithValue("$pl", plausibleRatio);
        command.Parameters.AddWithValue("$cw", commonShare);
        command.Parameters.AddWithValue("$b", blocker.ToString());
        command.Parameters.AddWithValue("$ac", action.ToString());
        command.Parameters.AddWithValue("$o", (object?)outputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$r", (object?)originalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$u", Now());
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
