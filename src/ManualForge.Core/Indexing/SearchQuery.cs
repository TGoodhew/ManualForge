using System.Text;

namespace ManualForge.Core.Indexing;

/// <summary>
/// Turns what somebody typed into something FTS5 will accept.
///
/// <para>
/// This is not optional politeness. FTS5's MATCH takes a query language, not a phrase, and the
/// punctuation that fills technical documentation collides with it. The very first real query asked
/// of this index — <c>HP-IB handshake</c> — failed with <c>no such column: IB</c>, because a bare
/// hyphen reads as an operator. Part numbers, SCPI mnemonics and model numbers would all do the
/// same.
/// </para>
///
/// <para>
/// So ordinary typing is quoted term by term, which makes every character literal and joins the
/// terms with the implicit AND that a search box is expected to mean. Anyone who actually wants the
/// query language can still have it: a query already containing an FTS5 operator is passed through
/// untouched.
/// </para>
///
/// <para>
/// One class of query gets more than that. Command syntax is routinely copied out of a manual and
/// pasted straight into a search box — <c>:TRIGger:MODE {EDGE|GLITch|ADVanced}</c> — and as a
/// phrase it matches nothing at all, because a syntax diagram draws <c>TRIGger</c>, <c>MODE</c> and
/// each alternative in separate boxes that are nowhere near each other in reading order. Read as
/// the notation it is, the same string is a perfectly good query: the colons separate levels that
/// must all appear, the braces offer alternatives of which one will do, and
/// <c>&lt;N&gt;</c> is a placeholder standing for nothing in particular. See
/// <see cref="ExpandNotation"/>.
/// </para>
/// </summary>
public static class SearchQuery
{
    private static readonly string[] Operators = ["AND", "OR", "NOT", "NEAR("];

    /// <summary>The characters that make a term command-syntax notation rather than a word.</summary>
    private static readonly char[] NotationCharacters = [':', '{', '}', '[', ']', '<', '>', '|'];

    /// <summary>
    /// Whether the query is one bare word — <c>ATTenuation</c>, <c>PROTection</c>, <c>SKEW</c> —
    /// rather than command syntax or a phrase.
    ///
    /// <para>
    /// This is the population bm25 serves worst, because a bare instrument term appears in hundreds
    /// of manuals and the page that <em>defines</em> it competes with every page that merely
    /// mentions it. It is also the only population the recovered-text bias measurably helps: see
    /// <c>docs/measurements/ranking-recovered-text.md</c>, where weighting recovered matches moved
    /// <c>ATTenuation</c> from 14 to 1 and <c>PROTection</c> from 11 to 2, while pushing every
    /// colon-delimited command the other way.
    /// </para>
    /// </summary>
    public static bool IsBareTerm(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();

        // A phrase, a quoted string or anything with notation is not a bare term.
        if (trimmed.IndexOfAny(NotationCharacters) >= 0)
            return false;

        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '*')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the text is already an FTS5 expression the user meant literally.
    /// </summary>
    public static bool LooksLikeExpression(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Contains('*', StringComparison.Ordinal))
            return true;

        // Whole words, not substrings. FTS5's operators are case-sensitive, so uppercase is the
        // right test — but an uppercase substring is not: WORD contains OR, COMMAND contains AND
        // and NOTE contains NOT. Matching on the substring handed ":WAVeform:FORMat
        // {ASCii|BYTE|WORD|LONG}" to FTS5 as a raw expression, which is a syntax error, and did the
        // same to any query with COMMAND in it.
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            if (Operators.Any(op => op.EndsWith('(') && token.StartsWith(op, StringComparison.Ordinal)))
                return true;

            if (!Operators.Any(op => !op.EndsWith('(') && string.Equals(token, op, StringComparison.Ordinal)))
                continue;

            // An operator needs something on both sides of it. A phrase that merely ends in one —
            // "FIT BOTTOM EDGE UNDER LUGS AND", copied off a page — was being handed to FTS5 as an
            // expression and came back as a syntax error rather than as results. These manuals are
            // written in capitals, so AND, OR and NOT are ordinary words in them far more often
            // than they are operators.
            if (i > 0 && i < tokens.Length - 1)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Prepares a query as ordinary typing, whatever it looks like: every term quoted, nothing read
    /// as an operator. The fallback for an expression FTS5 will not accept.
    /// </summary>
    public static string? PrepareLiteral(string? query) => Prepare(query, literal: true);

    /// <summary>
    /// Prepares a query for <c>MATCH</c>. Returns null when there is nothing to search for.
    /// </summary>
    public static string? Prepare(string? query) => Prepare(query, literal: false);

    private static string? Prepare(string? query, bool literal)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        if (!literal && LooksLikeExpression(query))
            return query;

        var terms = Split(query);
        if (terms.Count == 0)
            return null;

        var parts = new List<string>(terms.Count);
        foreach (var term in terms)
        {
            var expanded = ExpandNotation(term);
            if (expanded is not null)
                parts.Add(expanded);
        }

        // Explicit AND throughout. Juxtaposition means AND in FTS5 only between phrases; put a
        // parenthesised group next to a phrase and the query silently matches nothing. Writing the
        // operator makes every combination behave the same way.
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// Reads one whitespace-separated term as command-syntax notation, and returns the FTS5
    /// expression for it — or null when it carries nothing searchable.
    ///
    /// <para>
    /// The notation is the one every SCPI manual in this corpus uses on its syntax pages:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>:</c> separates levels of a command, each of which must appear. They are joined by AND
    /// rather than kept as a phrase because a syntax diagram draws each level in its own box, and
    /// the recovered text for that page holds them in whatever order the boxes were drawn.
    /// </description></item>
    /// <item><description>
    /// <c>{a|b|c}</c> and <c>[a|b|c]</c> offer alternatives, so any one of them will do.
    /// </description></item>
    /// <item><description>
    /// <c>&lt;anything&gt;</c> is a placeholder — <c>&lt;N&gt;</c>, <c>&lt;NR3&gt;</c>,
    /// <c>&lt;value&gt;</c> — and stands for text that is not on the page. It is dropped.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// A term with none of that in it comes back as a quoted phrase exactly as before, so ordinary
    /// searching is untouched. In particular a hyphen is not a separator here: <c>HP-IB</c> and
    /// <c>08340-60019</c> are single things, and splitting them would lose the only precision they
    /// have.
    /// </para>
    /// </summary>
    public static string? ExpandNotation(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        if (term.IndexOfAny(NotationCharacters) < 0)
            return term.Any(char.IsLetterOrDigit) ? Quote(term) : null;

        // Each element of 'conjuncts' must appear; each inner list is a set of alternatives of
        // which one will do.
        var conjuncts = new List<List<string>>();
        var alternatives = new List<string>();
        var current = new StringBuilder();
        var inGroup = false;

        void FlushWord()
        {
            var word = current.ToString().Trim();
            current.Clear();
            if (word.Any(char.IsLetterOrDigit))
                alternatives.Add(word);
        }

        void FlushConjunct()
        {
            FlushWord();
            if (alternatives.Count > 0)
                conjuncts.Add([.. alternatives]);
            alternatives.Clear();
        }

        for (var i = 0; i < term.Length; i++)
        {
            var c = term[i];

            switch (c)
            {
                case '<':
                    // A placeholder. Skip to its close, or to the end if it never closes.
                    var close = term.IndexOf('>', i + 1);
                    i = close < 0 ? term.Length : close;
                    continue;

                case '{':
                case '[':
                    FlushConjunct();
                    inGroup = true;
                    continue;

                case '}':
                case ']':
                    FlushConjunct();
                    inGroup = false;
                    continue;

                case '|':
                    // An alternative, inside a group or not: DC50|DCFifty is written bare.
                    FlushWord();
                    continue;

                case ',':
                    // A separator inside a group is another alternative; outside one it is just
                    // punctuation in a phrase.
                    if (inGroup)
                    {
                        FlushWord();
                        continue;
                    }

                    current.Append(c);
                    continue;

                case ':':
                    FlushConjunct();
                    continue;

                default:
                    current.Append(c);
                    continue;
            }
        }

        FlushConjunct();

        if (conjuncts.Count == 0)
            return null;

        var parts = conjuncts.Select(group => group.Count == 1
            ? Quote(group[0])
            : "(" + string.Join(" OR ", group.Select(Quote)) + ")");

        // Explicit AND, not the implicit one that juxtaposition gives. FTS5's implicit AND joins
        // phrases into a phrase list and does not reach across a parenthesised group, so
        // `"TRIGger" "EDGE" ("CHANnel" OR "AUX")` silently matches nothing at all. Spelling the
        // operator out is the difference between a query that works and one that answers "no".
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// The literal words a query is looking for, used to work out whether a hit matched text that
    /// was extracted or text that was recognised. Runs of letters and digits only, because that is
    /// what the tokeniser indexes.
    /// </summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var terms = new List<string>();
        var current = new StringBuilder();

        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            Take(terms, current);
        }

        Take(terms, current);
        return terms;

        static void Take(List<string> into, StringBuilder buffer)
        {
            var word = buffer.ToString();
            buffer.Clear();

            // One-character runs are noise: the 'N' of a <N> placeholder, the '2' of a page
            // reference. They would match almost anything and tell us nothing about where a hit
            // came from.
            if (word.Length >= 2 && !Operators.Contains(word, StringComparer.Ordinal))
                into.Add(word);
        }
    }

    /// <summary>
    /// Splits on whitespace, but keeps anything the user put in quotes together as one phrase.
    /// </summary>
    private static List<string> Split(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in query)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                Flush(terms, current);
                continue;
            }

            current.Append(c);
        }

        Flush(terms, current);
        return terms;
    }

    private static void Flush(List<string> terms, StringBuilder current)
    {
        var term = current.ToString().Trim();
        current.Clear();

        // A term of pure punctuation tokenises to nothing and would make FTS5 complain about an
        // empty phrase.
        if (term.Length > 0 && term.Any(char.IsLetterOrDigit))
            terms.Add(term);
    }

    /// <summary>Wraps a term as an FTS5 string literal, where a quote is escaped by doubling it.</summary>
    private static string Quote(string term) => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
