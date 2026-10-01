using System.Text;

namespace ManualForge.Core.Pdf;

/// <summary>
/// One operator in a content stream with its operands, and where in the bytes it lies: from the
/// first operand to the end of the operator itself.
/// </summary>
internal sealed record ContentInstruction(string Operator, IReadOnlyList<string> Operands, int Start, int End);

/// <summary>
/// Splits a content stream into instructions without rewriting any of it, so that an edit can cut
/// byte ranges out of the original and leave everything else exactly as it was.
///
/// PDFsharp's own content parser cannot be trusted to write back what it read: a no-op round trip
/// through it changed how 10 of the 54 pages of HP_419A_op_service_manual_1966_HQ render, every one
/// of them a page carrying inline images. Splicing never re-serialises anything, so it cannot.
///
/// Inline images (BI ... ID data EI) come back as a single opaque instruction named BI.
/// </summary>
internal static class ContentStreamLexer
{
    /// <summary>The instructions in the stream, or null if it does not parse.</summary>
    public static IReadOnlyList<ContentInstruction>? Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var instructions = new List<ContentInstruction>();
        var operands = new List<string>();
        var operandStart = -1;
        var i = 0;

        while (true)
        {
            i = SkipWhitespaceAndComments(content, i);
            if (i >= content.Length)
                break;

            var start = i;
            var b = content[i];

            int end;
            if (b == '(')
                end = EndOfLiteralString(content, i);
            else if (b == '<' && i + 1 < content.Length && content[i + 1] == '<')
                end = EndOfDictionary(content, i);
            else if (b == '<')
                end = IndexOf(content, (byte)'>', i + 1) + 1;
            else if (b == '[')
                end = EndOfArray(content, i);
            else if (b == '/')
                end = EndOfRegular(content, i + 1);
            else if (b is (byte)')' or (byte)'>' or (byte)']' or (byte)'{' or (byte)'}')
                return null;
            else
                end = EndOfRegular(content, i);

            if (end <= start || end > content.Length)
                return null;

            var token = Encoding.Latin1.GetString(content, start, end - start);

            if (IsOperand(token))
            {
                if (operandStart < 0)
                    operandStart = start;
                operands.Add(token);
                i = end;
                continue;
            }

            if (token == "BI")
            {
                var imageEnd = EndOfInlineImage(content, end);
                if (imageEnd < 0)
                    return null;
                end = imageEnd;
            }

            instructions.Add(new ContentInstruction(token, operands.ToArray(), operandStart >= 0 ? operandStart : start, end));
            operands.Clear();
            operandStart = -1;
            i = end;
        }

        // Operands left with no operator mean the stream is not what we assumed.
        return operands.Count == 0 ? instructions : null;
    }

    private static bool IsOperand(string token)
    {
        var first = token[0];
        return first is '(' or '<' or '[' or '/' or '+' or '-' or '.' || char.IsAsciiDigit(first)
            || token is "true" or "false" or "null";
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b)
        => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static int SkipWhitespaceAndComments(byte[] content, int i)
    {
        while (i < content.Length)
        {
            if (IsWhitespace(content[i]))
            {
                i++;
            }
            else if (content[i] == '%')
            {
                while (i < content.Length && content[i] is not (10 or 13))
                    i++;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    private static int EndOfRegular(byte[] content, int i)
    {
        while (i < content.Length && !IsWhitespace(content[i]) && !IsDelimiter(content[i]))
            i++;
        return i;
    }

    private static int IndexOf(byte[] content, byte value, int from)
    {
        var found = Array.IndexOf(content, value, from);
        return found < 0 ? -2 : found;
    }

    private static int EndOfLiteralString(byte[] content, int i)
    {
        var depth = 0;
        for (; i < content.Length; i++)
        {
            switch (content[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                        return i + 1;
                    break;
            }
        }

        return -1;
    }

    /// <summary>The end of an array or dictionary, skipping anything nested in it.</summary>
    private static int EndOfNested(byte[] content, int i, int openLength, Func<int, int> closesAt)
    {
        i += openLength;
        while (true)
        {
            i = SkipWhitespaceAndComments(content, i);
            if (i >= content.Length)
                return -1;

            var close = closesAt(i);
            if (close > 0)
                return close;

            int end;
            if (content[i] == '(')
                end = EndOfLiteralString(content, i);
            else if (content[i] == '<' && i + 1 < content.Length && content[i + 1] == '<')
                end = EndOfDictionary(content, i);
            else if (content[i] == '<')
                end = IndexOf(content, (byte)'>', i + 1) + 1;
            else if (content[i] == '[')
                end = EndOfArray(content, i);
            else if (content[i] == '/')
                end = EndOfRegular(content, i + 1);
            else if (IsDelimiter(content[i]))
                return -1;
            else
                end = EndOfRegular(content, i);

            if (end <= i)
                return -1;
            i = end;
        }
    }

    private static int EndOfArray(byte[] content, int i)
        => EndOfNested(content, i, 1, at => content[at] == ']' ? at + 1 : -1);

    private static int EndOfDictionary(byte[] content, int i)
        => EndOfNested(content, i, 2,
            at => content[at] == '>' && at + 1 < content.Length && content[at + 1] == '>' ? at + 2 : -1);

    /// <summary>
    /// Where an inline image ends, given the position just after BI: past its parameters, the ID
    /// operator, one whitespace byte, the image data and the EI that ends it. The data is binary
    /// and has no length, so EI is recognised as it is by readers: on its own between whitespace.
    /// </summary>
    private static int EndOfInlineImage(byte[] content, int i)
    {
        while (true)
        {
            i = SkipWhitespaceAndComments(content, i);
            if (i >= content.Length)
                return -1;

            if (content[i] == 'I' && i + 1 < content.Length && content[i + 1] == 'D'
                && (i + 2 == content.Length || IsWhitespace(content[i + 2])))
            {
                i += 3;
                break;
            }

            int end;
            if (content[i] == '(')
                end = EndOfLiteralString(content, i);
            else if (content[i] == '<' && i + 1 < content.Length && content[i + 1] == '<')
                end = EndOfDictionary(content, i);
            else if (content[i] == '<')
                end = IndexOf(content, (byte)'>', i + 1) + 1;
            else if (content[i] == '[')
                end = EndOfArray(content, i);
            else if (content[i] == '/')
                end = EndOfRegular(content, i + 1);
            else
                end = EndOfRegular(content, i);

            if (end <= i)
                return -1;
            i = end;
        }

        for (var at = i; at + 1 < content.Length; at++)
        {
            if (content[at] == 'E' && content[at + 1] == 'I'
                && at > 0 && IsWhitespace(content[at - 1])
                && (at + 2 == content.Length || IsWhitespace(content[at + 2]) || IsDelimiter(content[at + 2])))
                return at + 2;
        }

        return -1;
    }
}
