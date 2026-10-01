using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace ManualForge.Core.Pdf;

public sealed record StripResult(int PagesChanged, int TextBlocksRemoved, int FontResourcesRemoved)
{
    /// <summary>Blocks left in place because they draw text a reader can see.</summary>
    public int VisibleBlocksKept { get; init; }
}

/// <summary>
/// Removes an existing hidden text layer from a document, leaving everything that draws ink
/// untouched.
///
/// This exists for the strip-and-redo path: a manual carrying poor 2000s-era OCR cannot simply have
/// a second text layer added on top, because extractors would then return both, interleaved, and
/// searches would match the old bad text as readily as the new good text.
///
/// Only text drawn in render mode 3 - neither filled nor stroked - goes, because that is what an
/// OCR layer is. Text a reader can see is ink: a page header, a seller's stamp, a table of contents
/// typeset over a scan. Until 1 October 2026 every BT/ET block went, and a re-read took the
/// contents page out of one manual and a footer or stamp out of four others. Visible text stays,
/// and the new layer is written around it instead.
///
/// A block that shows only hidden text goes whole: everything between BT and ET is a text object
/// by definition and cannot paint an image or a path. In a block that mixes the two, a hidden
/// string goes on its own only where the next string is positioned afresh, so that nothing visible
/// moves; anything else is left for the caller's check to find.
///
/// The bytes are cut, never re-serialised - see <see cref="ContentStreamLexer"/> for why - and text
/// is found in form XObjects as well as in the page's own content, since that is where Acrobat puts
/// headers, footers and watermarks.
/// </summary>
public static class TextLayerStripper
{
    private const int HiddenMode = 3;

    private static readonly HashSet<string> ShowOperators = new(StringComparer.Ordinal) { "Tj", "TJ", "'", "\"" };

    /// <summary>Operators that position the next string without reference to the last one.</summary>
    private static readonly HashSet<string> PositioningOperators = new(StringComparer.Ordinal) { "Td", "TD", "Tm", "T*" };

    /// <summary>
    /// Text state operators. Legal outside a text object and carried in the graphics state past ET,
    /// so text later on the page - or in a form drawn after it - can depend on a block that goes.
    /// </summary>
    private static readonly HashSet<string> TextStateOperators =
        new(StringComparer.Ordinal) { "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts" };

    public static StripResult Strip(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pagesChanged = 0;
        var blocksRemoved = 0;
        var blocksKept = 0;
        var fontsRemoved = 0;
        var formsSeen = new HashSet<PdfObjectID>();

        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var pageResult = StripPageAndForms(page, formsSeen);
            blocksKept += pageResult.Kept;
            if (pageResult.Removed == 0)
                continue;

            pagesChanged++;
            blocksRemoved += pageResult.Removed;
            if (pageResult.FontsUnused)
                fontsRemoved += RemoveFontResources(page);
        }

        return new StripResult(pagesChanged, blocksRemoved, fontsRemoved) { VisibleBlocksKept = blocksKept };
    }

    /// <summary>Removes the hidden text from a single page's own content.</summary>
    public static (bool Changed, int BlocksRemoved) StripPage(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var result = StripStream(ReadPage(page));
        if (result is null || result.Removed == 0)
            return (false, 0);

        WritePage(page, result.Content);
        return (true, result.Removed);
    }

    private sealed record StreamResult(byte[] Content, int Removed, int Kept, bool TextRemains);

    private static (int Removed, int Kept, bool FontsUnused) StripPageAndForms(
        PdfPage page, HashSet<PdfObjectID> formsSeen)
    {
        var removed = 0;
        var kept = 0;

        var own = StripStream(ReadPage(page));
        if (own is not null)
        {
            kept += own.Kept;
            if (own.Removed > 0)
            {
                WritePage(page, own.Content);
                removed += own.Removed;
            }
        }

        var formsUseOwnResources = true;
        foreach (var form in FormsOf(page.Elements.GetDictionary("/Resources")))
        {
            if (form.Elements.GetDictionary("/Resources") is null)
                formsUseOwnResources = false;
            var (formRemoved, formKept) = StripForm(form, formsSeen);
            removed += formRemoved;
            kept += formKept;
        }

        // The page's fonts can go only when nothing left on it can name one: no text, no text
        // state carried forward, and no form leaning on the page's resources for its own.
        var fontsUnused = own is { TextRemains: false } && formsUseOwnResources;
        return (removed, kept, fontsUnused);
    }

    /// <summary>Strips a form and the forms it draws, each once however many pages share it.</summary>
    private static (int Removed, int Kept) StripForm(PdfDictionary form, HashSet<PdfObjectID> formsSeen)
    {
        if (form.Reference is { } reference && !formsSeen.Add(reference.ObjectID))
            return (0, 0);

        var removed = 0;
        var kept = 0;

        if (form.Stream is not null)
        {
            var result = StripStream(form.Stream.UnfilteredValue);
            if (result is not null)
            {
                kept += result.Kept;
                if (result.Removed > 0)
                {
                    form.Stream.Value = result.Content;
                    form.Elements.Remove("/Filter");
                    form.Elements.Remove("/DecodeParms");
                    removed += result.Removed;
                }
            }
        }

        foreach (var inner in FormsOf(form.Elements.GetDictionary("/Resources")))
        {
            var (innerRemoved, innerKept) = StripForm(inner, formsSeen);
            removed += innerRemoved;
            kept += innerKept;
        }

        return (removed, kept);
    }

    private static IEnumerable<PdfDictionary> FormsOf(PdfDictionary? resources)
    {
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        if (xobjects is null)
            yield break;

        foreach (var key in xobjects.Elements.Keys)
        {
            var value = xobjects.Elements[key];
            if ((value is PdfReference reference ? reference.Value : value) is PdfDictionary dictionary
                && dictionary.Elements.GetName("/Subtype") == "/Form")
                yield return dictionary;
        }
    }

    /// <summary>The page's content streams joined, as a reader joins them, or null if one is unreadable.</summary>
    private static byte[]? ReadPage(PdfPage page)
    {
        try
        {
            using var joined = new MemoryStream();
            foreach (var item in page.Contents.Elements)
            {
                if ((item is PdfReference reference ? reference.Value : item) is not PdfDictionary { Stream: { } stream })
                    return null;
                joined.Write(stream.UnfilteredValue);
                joined.WriteByte((byte)'\n');
            }

            return joined.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WritePage(PdfPage page, byte[] content)
    {
        page.Contents.Elements.Clear();
        page.Contents.AppendContent().CreateStream(content);
    }

    private sealed class Block(int start)
    {
        public int Start { get; } = start;
        public int End { get; set; }

        /// <summary>Indexes of the strings it shows, and whether each is hidden.</summary>
        public List<(int Index, bool Hidden)> Shows { get; } = [];

        public bool AllHidden => Shows.All(s => s.Hidden);
    }

    /// <summary>
    /// The content without its hidden text, or null when it must be left alone. Each block removed
    /// is replaced by the text state it set, wherever anything after it could still depend on that.
    /// </summary>
    private static StreamResult? StripStream(byte[]? content)
    {
        if (content is null)
            return null;

        var instructions = ContentStreamLexer.Read(content);
        if (instructions is null)
            return null;

        // First pass: find each block and the render mode each of its strings is shown in. The
        // mode lives in the graphics state, so it is tracked through q/Q and across blocks.
        var blocks = new List<Block>();
        var modes = new Stack<int>();
        var mode = 0;
        Block? open = null;

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            switch (instruction.Operator)
            {
                case "q":
                    modes.Push(mode);
                    break;
                case "Q":
                    if (modes.Count > 0)
                        mode = modes.Pop();
                    break;
                case "Tr":
                    mode = instruction.Operands.Count > 0 ? IntegerOf(instruction.Operands[^1]) ?? 0 : 0;
                    break;
                case "BT":
                    // A BT inside a block means the structure is not what we assumed; a
                    // half-stripped stream is worse than an unstripped one.
                    if (open is not null)
                        return null;
                    open = new Block(i);
                    break;
                case "ET":
                    if (open is null)
                        return null;
                    open.End = i;
                    blocks.Add(open);
                    open = null;
                    break;
                default:
                    if (open is not null && ShowOperators.Contains(instruction.Operator))
                        open.Shows.Add((i, mode == HiddenMode));
                    break;
            }
        }

        if (open is not null)
            return null;

        // Second pass: decide what goes. A whole block when all it shows is hidden; otherwise each
        // hidden string whose successor is positioned afresh, so that nothing visible moves.
        var cuts = new List<(int First, int Last, string Replacement)>();
        var removedBlocks = 0;
        var keptBlocks = 0;
        var textRemains = false;
        var lastDependent = LastDependent(instructions, blocks);

        foreach (var block in blocks)
        {
            if (block.AllHidden)
            {
                removedBlocks++;
                var state = block.End < lastDependent ? StateSetBy(instructions, block) : "";
                if (state.Length > 0)
                    textRemains = true;
                cuts.Add((block.Start, block.End, state));
                continue;
            }

            keptBlocks++;
            textRemains = true;
            var cutAny = false;
            for (var s = 0; s < block.Shows.Count; s++)
            {
                var (index, hidden) = block.Shows[s];
                if (!hidden)
                    continue;

                var nextShow = s + 1 < block.Shows.Count ? block.Shows[s + 1].Index : block.End;
                var repositioned = nextShow == block.End
                    || instructions[nextShow].Operator is "'" or "\""
                    || Enumerable.Range(index + 1, nextShow - index - 1)
                        .Any(k => PositioningOperators.Contains(instructions[k].Operator));
                if (!repositioned)
                    continue;

                cuts.Add((index, index, ReplacementForShow(instructions[index])));
                cutAny = true;
            }

            if (cutAny)
                removedBlocks++;
        }

        if (cuts.Count == 0)
            return new StreamResult(content, 0, keptBlocks, textRemains);

        return new StreamResult(Splice(content, instructions, cuts), removedBlocks, keptBlocks, textRemains);
    }

    /// <summary>The last instruction that could inherit text state: a kept block, or any form drawn.</summary>
    private static int LastDependent(IReadOnlyList<ContentInstruction> instructions, List<Block> blocks)
    {
        var last = -1;
        for (var i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].Operator == "Do")
            {
                last = i;
                break;
            }
        }

        foreach (var block in blocks.Where(b => !b.AllHidden))
            last = Math.Max(last, block.Start);
        return last;
    }

    /// <summary>The text state a block leaves behind, as operators that can stand outside it.</summary>
    private static string StateSetBy(IReadOnlyList<ContentInstruction> instructions, Block block)
    {
        var state = new StringBuilder();
        for (var i = block.Start + 1; i < block.End; i++)
        {
            var instruction = instructions[i];
            if (TextStateOperators.Contains(instruction.Operator))
            {
                state.Append(string.Join(' ', instruction.Operands)).Append(' ').Append(instruction.Operator).Append('\n');
            }
            else if (instruction.Operator == "TD" && instruction.Operands.Count == 2)
            {
                // TD sets the leading as well as moving.
                var ty = NumberOf(instruction.Operands[1]);
                if (ty is not null)
                    state.Append((-ty.Value).ToString(CultureInfo.InvariantCulture)).Append(" TL\n");
            }
            else if (instruction.Operator == "\"" && instruction.Operands.Count == 3)
            {
                state.Append(instruction.Operands[0]).Append(" Tw\n").Append(instruction.Operands[1]).Append(" Tc\n");
            }
        }

        return state.ToString();
    }

    /// <summary>What stays when a hidden string inside a kept block goes: whatever it did besides showing.</summary>
    private static string ReplacementForShow(ContentInstruction show) => show.Operator switch
    {
        "'" => "T*",
        "\"" when show.Operands.Count == 3 => $"{show.Operands[0]} Tw {show.Operands[1]} Tc T*",
        _ => "",
    };

    private static byte[] Splice(
        byte[] content, IReadOnlyList<ContentInstruction> instructions, List<(int First, int Last, string Replacement)> cuts)
    {
        using var output = new MemoryStream(content.Length);
        var position = 0;
        foreach (var (first, last, replacement) in cuts.OrderBy(c => c.First))
        {
            var start = instructions[first].Start;
            output.Write(content, position, start - position);
            if (replacement.Length > 0)
            {
                output.Write(Encoding.ASCII.GetBytes(replacement));
                output.WriteByte((byte)'\n');
            }
            position = instructions[last].End;
        }

        output.Write(content, position, content.Length - position);
        return output.ToArray();
    }

    private static double? NumberOf(string token)
        => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? IntegerOf(string token) => NumberOf(token) is { } value ? (int)Math.Round(value) : null;

    /// <summary>
    /// Drops the page's font resources. Without this the fonts stay embedded, and a stripped
    /// document keeps carrying the weight of a text layer it no longer has.
    /// </summary>
    private static int RemoveFontResources(PdfPage page)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        var fonts = resources?.Elements.GetDictionary("/Font");
        if (fonts is null)
            return 0;

        var count = fonts.Elements.Count;
        resources!.Elements.Remove("/Font");
        return count;
    }
}
