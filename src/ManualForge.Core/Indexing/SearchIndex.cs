using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ManualForge.Core.Indexing;

/// <summary>One document in the index.</summary>
public sealed record IndexedDocument(long Id, string Path, string Title, int PageCount, string ContentHash);

/// <summary>
/// Adjustments applied to bm25's score after retrieval, each off at 1.0.
///
/// <para>
/// bm25 scores on term frequency and page length and has no notion of what a page is *for*. The
/// page that defines <c>:WAVeform:SOURce</c> mentions it once, in a diagram; a page discussing
/// waveform sources at length mentions the words thirty times and wins. These are attempts to say
/// what a defining page looks like, and they are separate knobs because the only honest way to
/// adopt one is to measure it alone.
/// </para>
/// <para>
/// A multiplier rather than an added constant: bm25 returns a negative score where more negative is
/// better, so multiplying widens an existing lead and cannot drag a poor match to the top.
/// </para>
/// </summary>
public sealed record RankingBias
{
    /// <summary>
    /// For a page carrying a query term on a line of its own — a label, a heading or a cell rather
    /// than a word inside a sentence. This is the shape of a syntax diagram, a pin-out and a
    /// component-locator table, and it is what a single-token query like <c>ATTenuation</c> is
    /// almost always looking for.
    /// </summary>
    public double Label { get; init; } = 1.0;

    /// <summary>
    /// For a hit whose matched words came from text the repair read off the rendered page. The
    /// premise is that a page whose text had to be recovered is disproportionately the page that
    /// draws rather than discusses — which is the defining page. The premise may be wrong; that is
    /// what measuring is for.
    /// </summary>
    public double Recovered { get; init; } = 1.0;

    /// <summary>Whether anything here would change an ordering.</summary>
    public bool Any => Label != 1.0 || Recovered != 1.0;

    /// <summary>
    /// What search does unless told otherwise: the label boost on, the recovered-text boost off.
    ///
    /// <para>
    /// Measured three ways before being made the default, because a ranking change tuned against
    /// the 33 hand-read strings will flatter itself — those strings were chosen because they
    /// failed. On the ground truth it takes 27 of 33 to 31 within the first 25, and 21 to 25 within
    /// the first ten. On 25 repaired pages from other documents it changes nothing. On 40 ordinary
    /// pages quoted at random from the library, one page in forty slips off first place and
    /// nothing leaves the first ten.
    /// </para>
    /// <para>
    /// So: a large gain on the queries this library is actually asked, an unmeasurable cost on
    /// ordinary prose, and a mechanism that is a fact about technical manuals rather than about the
    /// 54845A — a term printed on a line of its own is a label, and a page of labels is usually the
    /// page that defines the thing. `--rank-labels 1.0` turns it off.
    /// </para>
    /// <para>
    /// The recovered-text boost stays off. It was measured at the same time on the premise that a
    /// page whose text had to be recovered is disproportionately the defining page, and the
    /// numbers did not support it: better in the first ten, worse in the first 25, and it pushed
    /// `:WAVeform:SOURce` back out of reach. Kept as a flag, and recorded here rather than
    /// rediscovered.
    /// </para>
    /// </summary>
    public static readonly RankingBias Default = new() { Label = 1.4 };
}

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

    /// <summary>
    /// Documents in the index holding no text on any page: image-only scans nobody has OCR'd.
    ///
    /// <para>
    /// Worth a number of its own because it is the difference between "not in this library" and
    /// "in this library and invisible to search", and a caller told only the first will pass a
    /// wrong answer on to somebody. <see cref="SearchIndex.DocumentsWithoutText"/> names them.
    /// </para>
    /// </summary>
    public int DocumentsWithoutText { get; init; }
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

        // Checked before anything is read or written, on an existing file either way. Something
        // else is being invited to depend on this file, and a reader that quietly misreads a
        // schema it does not understand is worse than one that stops.
        EnsureSchemaIsReadable();

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

        using var stamp = _connection.CreateCommand();
        stamp.CommandText = $"PRAGMA user_version = {SchemaVersion}";
        stamp.ExecuteNonQuery();
    }

    /// <summary>
    /// The schema this build understands, written into the file as <c>PRAGMA user_version</c>.
    ///
    /// <para>
    /// It rises whenever an existing column changes meaning — not when one is added. A reader that
    /// does not know about a new column carries on correctly; a reader that misunderstands an old
    /// one is quietly wrong, and that is the case this number exists to make loud. Adding
    /// <c>supplement_hash</c> did not raise it; redefining what <c>text</c> holds would.
    /// </para>
    /// <para>
    /// Version 1 is every index built before the number existed, which is why an unstamped file is
    /// read rather than refused: those indexes are correct, they simply predate anybody promising
    /// anything about them.
    /// </para>
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Refuses an index written by a newer build, and says so in a sentence a caller can act on.
    /// </summary>
    private void EnsureSchemaIsReadable()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version <= SchemaVersion)
            return;

        throw new InvalidOperationException(
            $"{DatabasePath} was written by a newer version of ManualForge: its schema is version " +
            $"{version} and this build understands {SchemaVersion}. Update ManualForge, or point " +
            "at a different index. Nothing was read from it, because a reader that guesses at a " +
            "schema it does not know is worse than one that stops.");
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
    /// <param name="note">
    /// Told when the query had to be re-read as ordinary words because FTS5 rejected it as an
    /// expression. A search that quietly changes the question is worse than one that says so.
    /// </param>
    /// <remarks>
    /// The fallback exists because the query language and ordinary typing overlap. Somebody who
    /// writes <c>power NOT supply</c> means the operator; somebody who pastes a line off a page
    /// that happens to contain one does not, and cannot be expected to know FTS5 exists. When the
    /// guess is wrong the query is re-run with every term quoted rather than failed, since an
    /// error message in place of results is the worst of the three possible answers.
    /// </remarks>
    /// <param name="model">
    /// An instrument model the caller already knows, such as <c>54845A</c>. Manuals whose title or
    /// path mentions it are ranked higher — biased, never filtered, because the answer to a
    /// question about one instrument is often printed in another instrument's manual, and a filter
    /// would hide exactly those.
    /// </param>
    /// <param name="ranking">
    /// Score adjustments applied after retrieval. Null takes <see cref="RankingBias.Default"/>;
    /// <c>new RankingBias()</c> leaves bm25's ordering alone.
    /// </param>
    public IReadOnlyList<SearchHit> Search(
        string query,
        int limit = 20,
        bool foldDuplicates = true,
        Action<string>? note = null,
        string? model = null,
        RankingBias? ranking = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        ranking ??= RankingBias.Default;

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

        // Folding happens after the fact, so the limit has to allow for copies being dropped. A
        // model bias needs a wider window still: a hit it would promote from rank 80 to rank 3 has
        // to have been fetched, and re-ordering a list of ten cannot reach it.
        var window = foldDuplicates ? limit * 6 : limit;
        if (!string.IsNullOrWhiteSpace(model) || ranking?.Any == true)
            window = Math.Min(Math.Max(window * 10, 200), 2000);

        command.Parameters.AddWithValue("$limit", window);

        // Every candidate is read before any is chosen, so that folding identical copies together
        // cannot silently shorten the result list.
        var candidates = new List<(SearchHit Hit, string Hash, double Score)>();

        using (var reader = Execute(command, query, prepared, note))
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

                // bm25 returns a negative score where more negative is better, so multiplying a
                // matching hit's score widens its lead. A multiplier rather than a constant, so
                // the bias scales with how well the page matched in the first place: it promotes a
                // good hit in the right manual, and cannot drag a poor one to the top.
                var score = rank;
                if (Mentions(model, title, path))
                    score *= ModelBias;

                if (ranking is not null)
                {
                    if (ranking.Label != 1.0 && CarriesALabel(terms, fullText))
                        score *= ranking.Label;

                    if (ranking.Recovered != 1.0 && hit.MatchSource == TextSource.Ocr)
                        score *= ranking.Recovered;
                }

                candidates.Add((hit, hash, score));
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

    /// <summary>
    /// How much better a hit scores for being in a manual whose title or path names the model the
    /// caller asked about. 1.5 promotes the right manual's page past a handful of near-equals
    /// without letting it overtake a page that matched the words far better — which is the
    /// behaviour wanted, because the model is a hint about relevance and not a statement of it.
    /// </summary>
    private const double ModelBias = 1.5;

    /// <summary>
    /// Whether the page carries one of the query's terms on a line of its own, rather than inside a
    /// sentence.
    ///
    /// <para>
    /// "On a line of its own" is read generously — a line no more than a few characters longer than
    /// the term — because a diagram's label often has a stray mark or a continuation on it, and
    /// because the lines here were rebuilt from word positions rather than read from the file. A
    /// term of three characters or fewer is ignored: too many of them sit alone somewhere on a page
    /// by accident.
    /// </para>
    /// </summary>
    private static bool CarriesALabel(IReadOnlyList<string> terms, string text)
    {
        if (text.Length == 0)
            return false;

        foreach (var term in terms)
        {
            if (term.Length <= 3)
                continue;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.Length > term.Length + 8)
                    continue;

                if (trimmed.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether a hit's manual is named for the model the caller asked about.</summary>
    private static bool Mentions(string? model, string title, string path)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        return title.Contains(model, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the MATCH, and if FTS5 rejects the expression, runs it again with every term quoted.
    /// </summary>
    private static SqliteDataReader Execute(
        SqliteCommand command, string query, string prepared, Action<string>? note)
    {
        try
        {
            return command.ExecuteReader();
        }
        catch (SqliteException)
        {
            var literal = SearchQuery.PrepareLiteral(query);

            // Nothing to fall back to: either it was already being read literally, or there is no
            // literal reading of it either, and then the error is the honest answer.
            if (literal is null || string.Equals(literal, prepared, StringComparison.Ordinal))
                throw;

            command.Parameters["$q"].Value = literal;
            note?.Invoke(
                "That is not a valid search expression, so it was read as ordinary words instead. " +
                "AND, OR and NOT act as operators only with something on both sides of them.");

            return command.ExecuteReader();
        }
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

        var documents = reader.GetInt32(0);
        var pages = reader.GetInt64(1);
        reader.Close();

        var size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        return new IndexStatistics(documents, pages, size)
        {
            RepairedPages = repairedPages,
            RepairedDocuments = repairedDocuments,
            DocumentsWithoutText = DocumentsWithoutText(int.MaxValue).Count,
        };
    }

    /// <summary>
    /// Documents that are in the index and hold no text at all, newest first, up to a limit.
    ///
    /// <para>
    /// These are the library's blind spots. The document is known, its page count is known, and
    /// searching for anything on any of its pages returns nothing — not because the library lacks
    /// the answer but because nobody has run OCR over it. Silence about that is what turns
    /// "nothing matched" into a false negative that a caller repeats as fact.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DocumentsWithoutText(int limit = 5)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.path
            FROM documents d
            WHERE NOT EXISTS (
                SELECT 1 FROM pages p
                WHERE p.doc_id = d.id AND LENGTH(TRIM(p.text)) > 0
            )
            ORDER BY d.indexed_utc DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));

        return paths;
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

    /// <summary>
    /// Bytes the file is holding that nothing is using — free pages left behind by updating
    /// documents in place. <see cref="Optimise"/> merges the full-text index; it does not give
    /// these back, which is why an index that grew is larger than the same index built fresh.
    /// </summary>
    public long ReclaimableBytes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA freelist_count";
        var free = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        command.CommandText = "PRAGMA page_size";
        var size = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        return free * size;
    }

    /// <summary>
    /// Rewrites the database without its free pages.
    ///
    /// <para>
    /// Deliberately not part of an ordinary index run. VACUUM rewrites the whole file, needs room
    /// for a second copy of it while it works, and on a 300 MB index living in OneDrive it means
    /// re-uploading 300 MB — a cost nobody asked for as the tail of a routine build. Offered when
    /// it is worth having, run when it is asked for.
    /// </para>
    /// </summary>
    public void Compact()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "VACUUM";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
