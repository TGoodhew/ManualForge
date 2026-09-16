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
/// </summary>
public static class SearchQuery
{
    private static readonly string[] Operators = ["AND", "OR", "NOT", "NEAR("];

    /// <summary>
    /// Whether the text is already an FTS5 expression the user meant literally.
    /// </summary>
    public static bool LooksLikeExpression(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Uppercase, because FTS5's operators are case-sensitive and "and" in a sentence is not one.
        return Operators.Any(op => query.Contains(op, StringComparison.Ordinal))
               || query.Contains('*', StringComparison.Ordinal);
    }

    /// <summary>
    /// Prepares a query for <c>MATCH</c>. Returns null when there is nothing to search for.
    /// </summary>
    public static string? Prepare(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        if (LooksLikeExpression(query))
            return query;

        var terms = Split(query);
        if (terms.Count == 0)
            return null;

        return string.Join(' ', terms.Select(Quote));
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
