using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Indexing;

/// <summary>One document in the index.</summary>
public sealed record IndexedDocument(long Id, string Path, string Title, int PageCount, string ContentHash);

/// <summary>A page that matched, with enough context to be worth reading.</summary>
public sealed record SearchHit(
    string Path,
    string Title,
    int PageNumber,
    string Snippet,
    double Rank,
    IReadOnlyList<string> AlsoAt)
{
    /// <summary>Other paths holding the identical document, which the index deliberately folds together.</summary>
    public bool HasDuplicates => AlsoAt.Count > 0;
}

public sealed record IndexStatistics(int Documents, long Pages, long SizeBytes);

/// <summary>
/// A full-text index over the library: SQLite FTS5, unicode61, one row per page.
///
/// <para>
/// A page rather than a document, because the answer to "which pins carry the HP-IB handshake" is a
/// page number in a 600-page manual, and an index that can only say which manual has barely helped.
/// </para>
///
/// <para>
/// Two indexed columns. <c>text</c> is what a person reads and where snippets come from;
/// <c>alternates</c> holds the other reading of every hyphen the <see cref="Dehyphenator"/> had to
/// guess about, so a guess can cost an odd-looking snippet but never a missed result.
/// </para>
/// </summary>
public sealed class SearchIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    public SearchIndex(string databasePath, bool readOnly = false)
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

            CREATE TABLE IF NOT EXISTS documents (
                id            INTEGER PRIMARY KEY,
                path          TEXT NOT NULL UNIQUE,
                title         TEXT NOT NULL,
                page_count    INTEGER NOT NULL,
                content_hash  TEXT NOT NULL,
                indexed_utc   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS documents_hash ON documents(content_hash);

            -- The pages themselves. 'alternates' is indexed and never displayed; 'doc_id' and
            -- 'page_number' are UNINDEXED because they are how a hit is located, not what is
            -- searched, and indexing them would let a page number match a query for a part number.
            CREATE VIRTUAL TABLE IF NOT EXISTS pages USING fts5(
                text,
                alternates,
                doc_id UNINDEXED,
                page_number UNINDEXED,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            """;
        command.ExecuteNonQuery();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Whether this build of SQLite has FTS5 at all. Worth asking rather than assuming: the
    /// extension is optional, and a build without it fails only when the first table is created.
    /// </summary>
    public static bool IsFts5Available()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIRTUAL TABLE probe USING fts5(x)";
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds a document and its pages, replacing anything previously indexed for that path.
    /// </summary>
    public long AddDocument(
        string path, string title, string contentHash, IReadOnlyList<IndexedPageText> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pages);

        var full = Path.GetFullPath(path);

        using var transaction = _connection.BeginTransaction();

        RemoveInternal(full, transaction);

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO documents (path, title, page_count, content_hash, indexed_utc)
                VALUES ($p, $t, $n, $h, $u)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$p", full);
            insert.Parameters.AddWithValue("$t", title);
            insert.Parameters.AddWithValue("$n", pages.Count);
            insert.Parameters.AddWithValue("$h", contentHash);
            insert.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].IsEmpty)
                    continue;

                using var page = _connection.CreateCommand();
                page.Transaction = transaction;
                page.CommandText = """
                    INSERT INTO pages (text, alternates, doc_id, page_number)
                    VALUES ($x, $a, $d, $n)
                    """;
                page.Parameters.AddWithValue("$x", pages[i].Text);
                page.Parameters.AddWithValue("$a", pages[i].Alternates);
                page.Parameters.AddWithValue("$d", id);
                page.Parameters.AddWithValue("$n", i + 1);
                page.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        }
    }

    /// <summary>Removes a document and its pages.</summary>
    public void Remove(string path)
    {
        using var transaction = _connection.BeginTransaction();
        RemoveInternal(Path.GetFullPath(path), transaction);
        transaction.Commit();
    }

    private void RemoveInternal(string fullPath, SqliteTransaction transaction)
    {
        using var pages = _connection.CreateCommand();
        pages.Transaction = transaction;
        pages.CommandText =
            "DELETE FROM pages WHERE doc_id IN (SELECT id FROM documents WHERE path = $p)";
        pages.Parameters.AddWithValue("$p", fullPath);
        pages.ExecuteNonQuery();

        using var document = _connection.CreateCommand();
        document.Transaction = transaction;
        document.CommandText = "DELETE FROM documents WHERE path = $p";
        document.Parameters.AddWithValue("$p", fullPath);
        document.ExecuteNonQuery();
    }

    /// <summary>What is already indexed for a path, so an unchanged file can be skipped.</summary>
    public string? ContentHashOf(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT content_hash FROM documents WHERE path = $p";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Finds pages matching an FTS5 query, best first.
    /// </summary>
    /// <param name="foldDuplicates">
    /// Report a document once however many identical copies of it the library holds, listing the
    /// other paths alongside. Without this a library with four copies of one manual answers every
    /// question about it four times, which is the part that is actually annoying to live with.
    /// </param>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 20, bool foldDuplicates = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.path, d.title, p.page_number, d.content_hash,
                   snippet(pages, 0, '[', ']', ' … ', 24) AS snip,
                   bm25(pages, 10.0, 1.0) AS rank
            FROM pages p
            JOIN documents d ON d.id = p.doc_id
            WHERE pages MATCH $q
            ORDER BY rank
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$q", query);

        // Folding happens after the fact, so the limit has to allow for copies being dropped.
        command.Parameters.AddWithValue("$limit", foldDuplicates ? limit * 6 : limit);

        var hits = new List<SearchHit>();
        var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<(string Hash, int Page)>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var title = reader.GetString(1);
            var pageNumber = reader.GetInt32(2);
            var hash = reader.GetString(3);
            var snippet = reader.GetString(4);
            var rank = reader.GetDouble(5);

            if (foldDuplicates && !string.IsNullOrEmpty(hash))
            {
                if (!byHash.TryGetValue(hash, out var others))
                    byHash[hash] = others = [];

                if (!seen.Add((hash, pageNumber)))
                {
                    // A copy of a page already reported. Remember where it also lives.
                    if (!others.Contains(path, StringComparer.OrdinalIgnoreCase))
                        others.Add(path);
                    continue;
                }
            }

            hits.Add(new SearchHit(path, title, pageNumber, Tidy(snippet), rank, []));

            if (hits.Count >= limit)
                break;
        }

        if (!foldDuplicates)
            return hits;

        // Attach the other paths now that every row has been seen.
        return hits
            .Select(h =>
            {
                var others = byHash.TryGetValue(HashFor(h.Path), out var list)
                    ? list.Where(p => !string.Equals(p, h.Path, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : [];
                return others.Length == 0 ? h : h with { AlsoAt = others };
            })
            .ToArray();
    }

    private string HashFor(string path) => ContentHashOf(path) ?? string.Empty;

    /// <summary>Extracted text is full of line breaks; a snippet reads better as one line.</summary>
    private static string Tidy(string snippet) =>
        string.Join(' ', snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public IndexStatistics Statistics()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM documents), (SELECT COUNT(*) FROM pages)";
        using var reader = command.ExecuteReader();
        reader.Read();

        var size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        return new IndexStatistics(reader.GetInt32(0), reader.GetInt64(1), size);
    }

    public IReadOnlyList<IndexedDocument> Documents()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, path, title, page_count, content_hash FROM documents ORDER BY path";

        var documents = new List<IndexedDocument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            documents.Add(new IndexedDocument(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4)));
        }

        return documents;
    }

    /// <summary>Rebuilds the FTS index and reclaims space. Worth doing after a full pass.</summary>
    public void Optimise()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO pages(pages) VALUES('optimize')";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
