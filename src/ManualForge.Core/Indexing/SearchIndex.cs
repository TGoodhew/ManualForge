using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Indexing;

/// <summary>One document in the index.</summary>
public sealed record IndexedDocument(long Id, string Path, string Title, int PageCount, string ContentHash);

/// <summary>Where the text on a page came from.</summary>
public enum TextSource
{
    /// <summary>The PDF's own text layer. Correctly spelled, correctly spaced, exactly as typeset.</summary>
    Embedded,

    /// <summary>
    /// Recognised from a rendered image by the repair, because the text layer did not hold it.
    /// Carries recognition errors, and callers are told so — which matters when the string is a
    /// command about to be sent to an instrument.
    /// </summary>
    Ocr,

    /// <summary>Both: the page has an embedded layer and a repair added what it missed.</summary>
    Mixed,
}

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

    /// <summary>Where the page's text came from.</summary>
    public TextSource PageSource { get; init; } = TextSource.Embedded;

    /// <summary>
    /// Where the words that actually matched came from, which is the question a caller cares
    /// about. A page can be mostly typeset prose and still have matched on a recognised diagram
    /// label.
    /// </summary>
    public TextSource MatchSource { get; init; } = TextSource.Embedded;

    /// <summary>Mean recogniser confidence for the OCR'd part of the page, when there is one.</summary>
    public double OcrConfidence { get; init; }

    /// <summary>Whether any part of what matched was recognised rather than extracted.</summary>
    public bool MatchedOcrText => MatchSource is TextSource.Ocr or TextSource.Mixed;

    /// <summary>
    /// A label for the hit's source, empty when there is nothing to say.
    ///
    /// <para>
    /// A string rather than a flag because it is bound straight into a XAML <c>Run</c>, and an
    /// empty run draws nothing — which is a smaller thing to get right than a visibility converter,
    /// and one that fails at build time rather than at run time when it is wrong.
    /// </para>
    /// </summary>
    public string SourceLabel => MatchSource switch
    {
        TextSource.Ocr => string.Create(
            System.Globalization.CultureInfo.CurrentCulture, $"OCR {OcrConfidence:P0}"),
        TextSource.Mixed => string.Create(
            System.Globalization.CultureInfo.CurrentCulture, $"part OCR {OcrConfidence:P0}"),
        _ => string.Empty,
    };
}

/// <summary>Text recovered for one page by the repair, as the index holds it.</summary>
public sealed record PageProvenance(int EmbeddedCharacters, int OcrCharacters, double OcrConfidence, string OcrText);

public sealed record IndexStatistics(int Documents, long Pages, long SizeBytes)
{
    /// <summary>Pages carrying text the repair recovered, which are not wholly to be trusted.</summary>
    public long RepairedPages { get; init; }

    public int RepairedDocuments { get; init; }
}

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

            -- Where each page's text came from. Only pages the repair touched get a row, so this
            -- stays a few thousand rows beside a hundred thousand pages. The recovered text is kept
            -- here as well as being folded into the FTS row above, so a hit can be attributed: a
            -- term that appears in ocr_text was recognised, and the caller has to be told.
            CREATE TABLE IF NOT EXISTS page_provenance (
                doc_id          INTEGER NOT NULL,
                page_number     INTEGER NOT NULL,
                embedded_chars  INTEGER NOT NULL,
                ocr_chars       INTEGER NOT NULL,
                ocr_confidence  REAL NOT NULL,
                ocr_text        TEXT NOT NULL,
                PRIMARY KEY (doc_id, page_number)
            );
            """;
        command.ExecuteNonQuery();

        Migrate();
    }

    /// <summary>
    /// Brings an index built by an earlier version up to date. An index over a hundred thousand
    /// pages takes long enough to build that throwing one away over a new column would be rude.
    /// </summary>
    private void Migrate()
    {
        if (HasColumn("documents", "supplement_hash"))
            return;

        using var command = _connection.CreateCommand();
        command.CommandText =
            "ALTER TABLE documents ADD COLUMN supplement_hash TEXT NOT NULL DEFAULT ''";
        command.ExecuteNonQuery();
    }

    private bool HasColumn(string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        command.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private bool HasTable(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t";
        command.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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
    /// <param name="provenance">
    /// For any page whose text the repair added to, where that text came from. Pages not named here
    /// are taken to be entirely the PDF's own.
    /// </param>
    /// <param name="supplementHash">
    /// Identifies the repair that was folded in. Held beside the content hash so a document whose
    /// file has not changed but whose recovered text has is still re-indexed.
    /// </param>
    public long AddDocument(
        string path,
        string title,
        string contentHash,
        IReadOnlyList<IndexedPageText> pages,
        IReadOnlyDictionary<int, PageProvenance>? provenance = null,
        string supplementHash = "")
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
                INSERT INTO documents (path, title, page_count, content_hash, supplement_hash, indexed_utc)
                VALUES ($p, $t, $n, $h, $s, $u)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$p", full);
            insert.Parameters.AddWithValue("$t", title);
            insert.Parameters.AddWithValue("$n", pages.Count);
            insert.Parameters.AddWithValue("$h", contentHash);
            insert.Parameters.AddWithValue("$s", supplementHash);
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

            if (provenance is not null)
            {
                foreach (var (pageNumber, source) in provenance)
                {
                    using var row = _connection.CreateCommand();
                    row.Transaction = transaction;
                    row.CommandText = """
                        INSERT INTO page_provenance
                            (doc_id, page_number, embedded_chars, ocr_chars, ocr_confidence, ocr_text)
                        VALUES ($d, $n, $e, $o, $c, $t)
                        """;
                    row.Parameters.AddWithValue("$d", id);
                    row.Parameters.AddWithValue("$n", pageNumber);
                    row.Parameters.AddWithValue("$e", source.EmbeddedCharacters);
                    row.Parameters.AddWithValue("$o", source.OcrCharacters);
                    row.Parameters.AddWithValue("$c", source.OcrConfidence);
                    row.Parameters.AddWithValue("$t", source.OcrText);
                    row.ExecuteNonQuery();
                }
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

        using var sources = _connection.CreateCommand();
        sources.Transaction = transaction;
        sources.CommandText =
            "DELETE FROM page_provenance WHERE doc_id IN (SELECT id FROM documents WHERE path = $p)";
        sources.Parameters.AddWithValue("$p", fullPath);
        sources.ExecuteNonQuery();

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
    /// Which repair was folded into what is already indexed for a path. A file can be untouched
    /// while the text recovered from it has changed, and skipping on the content hash alone would
    /// mean a repair never reached search.
    /// </summary>
    public string SupplementHashOf(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT supplement_hash FROM documents WHERE path = $p";
        command.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        return command.ExecuteScalar() as string ?? string.Empty;
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

        // What arrives here is what somebody typed, and MATCH takes a query language rather than a
        // phrase. See SearchQuery for why a hyphen is enough to break it.
        var prepared = SearchQuery.Prepare(query);
        if (prepared is null)
            return [];

        var terms = SearchQuery.Terms(query);
        var hasProvenance = HasTable("page_provenance");

        using var command = _connection.CreateCommand();
        command.CommandText = hasProvenance
            ? """
            SELECT d.path, d.title, p.page_number, d.content_hash,
                   snippet(pages, 0, '[', ']', ' … ', 24) AS snip,
                   bm25(pages, 10.0, 1.0) AS rank,
                   p.text,
                   v.ocr_text, v.ocr_confidence, v.embedded_chars
            FROM pages p
            JOIN documents d ON d.id = p.doc_id
            LEFT JOIN page_provenance v ON v.doc_id = p.doc_id AND v.page_number = p.page_number
            WHERE pages MATCH $q
            ORDER BY rank
            LIMIT $limit
            """
            : """
            SELECT d.path, d.title, p.page_number, d.content_hash,
                   snippet(pages, 0, '[', ']', ' … ', 24) AS snip,
                   bm25(pages, 10.0, 1.0) AS rank,
                   p.text,
                   NULL AS ocr_text, 0.0 AS ocr_confidence, 0 AS embedded_chars
            FROM pages p
            JOIN documents d ON d.id = p.doc_id
            WHERE pages MATCH $q
            ORDER BY rank
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$q", prepared);

        // Folding happens after the fact, so the limit has to allow for copies being dropped.
        command.Parameters.AddWithValue("$limit", foldDuplicates ? limit * 6 : limit);

        // Every candidate is read before any is chosen, so that folding identical copies together
        // cannot silently shorten the result list.
        var candidates = new List<(SearchHit Hit, string Hash, double Score)>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var title = reader.GetString(1);
                var pageNumber = reader.GetInt32(2);
                var hash = reader.GetString(3);
                var snippet = reader.GetString(4);
                var rank = reader.GetDouble(5);

                var fullText = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                var ocrText = reader.IsDBNull(7) ? null : reader.GetString(7);
                var confidence = reader.IsDBNull(8) ? 0 : reader.GetDouble(8);
                var embeddedChars = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);

                var pageSource = ocrText is null
                    ? TextSource.Embedded
                    : embeddedChars > 0 ? TextSource.Mixed : TextSource.Ocr;

                var hit = new SearchHit(path, title, pageNumber, Tidy(snippet), rank, [])
                {
                    PageSource = pageSource,
                    MatchSource = Attribute(terms, fullText, ocrText),
                    OcrConfidence = ocrText is null ? 0 : confidence,
                };

                candidates.Add((hit, hash, rank));
            }
        }

        var hits = new List<SearchHit>();
        var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<(string Hash, int Page)>();

        foreach (var (hit, hash, _) in candidates.OrderBy(c => c.Score))
        {
            if (foldDuplicates && !string.IsNullOrEmpty(hash))
            {
                if (!byHash.TryGetValue(hash, out var others))
                    byHash[hash] = others = [];

                if (!seen.Add((hash, hit.PageNumber)))
                {
                    // A copy of a page already reported. Remember where it also lives.
                    if (!others.Contains(hit.Path, StringComparer.OrdinalIgnoreCase))
                        others.Add(hit.Path);
                    continue;
                }
            }

            hits.Add(hit);

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

    /// <summary>
    /// Works out whether what matched was text the PDF carried or text the repair recovered.
    ///
    /// <para>
    /// The page's whole text is the embedded layer with the recovered text appended, so a term that
    /// occurs in the recovered text has been matched there, and a term that occurs more often in
    /// the whole page than in the recovered text has also been matched in the embedded layer. No
    /// second copy of the embedded text is needed to tell them apart, which matters when the
    /// alternative is storing every repaired page twice.
    /// </para>
    /// </summary>
    public static TextSource Attribute(IReadOnlyList<string> terms, string fullText, string? ocrText)
    {
        if (string.IsNullOrEmpty(ocrText))
            return TextSource.Embedded;

        var inOcr = false;
        var inEmbedded = false;

        foreach (var term in terms)
        {
            var inOcrCount = Occurrences(ocrText, term);
            if (inOcrCount > 0)
                inOcr = true;

            if (Occurrences(fullText, term) > inOcrCount)
                inEmbedded = true;
        }

        if (inOcr && inEmbedded)
            return TextSource.Mixed;

        return inOcr ? TextSource.Ocr : TextSource.Embedded;
    }

    private static int Occurrences(string text, string term)
    {
        if (text.Length == 0 || term.Length == 0)
            return 0;

        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += term.Length;
        }

        return count;
    }

    /// <summary>Extracted text is full of line breaks; a snippet reads better as one line.</summary>
    private static string Tidy(string snippet) =>
        string.Join(' ', snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public IndexStatistics Statistics()
    {
        var repairedPages = 0L;
        var repairedDocuments = 0;

        if (HasTable("page_provenance"))
        {
            using var repaired = _connection.CreateCommand();
            repaired.CommandText =
                "SELECT COUNT(*), COUNT(DISTINCT doc_id) FROM page_provenance WHERE ocr_chars > 0";
            using var counts = repaired.ExecuteReader();
            counts.Read();
            repairedPages = counts.GetInt64(0);
            repairedDocuments = counts.GetInt32(1);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM documents), (SELECT COUNT(*) FROM pages)";
        using var reader = command.ExecuteReader();
        reader.Read();

        var size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        return new IndexStatistics(reader.GetInt32(0), reader.GetInt64(1), size)
        {
            RepairedPages = repairedPages,
            RepairedDocuments = repairedDocuments,
        };
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
