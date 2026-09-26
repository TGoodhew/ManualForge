using System.Globalization;
using ManualForge.Core.Benchmarking;
using ManualForge.Core.Rendering;

namespace ManualForge.Cli;

/// <summary>
/// Collects pages from many PDFs into one image-only PDF, for comparing recognisers on equal terms.
///
/// <para>
/// The point is that neither engine can read a text layer that is already there: every page is
/// rasterised and re-embedded as an image, so whatever text the source carried is gone by
/// construction. Hand the result to another OCR engine, run <c>manualforge ocr</c> over the same
/// file, and the two outputs describe the same pixels.
/// </para>
/// </summary>
internal static class AssembleCommand
{
    public static int Run(CommandLine arguments)
    {
        var listPath = arguments.Get("pages")
            ?? throw new ArgumentException(
                "Give --pages <file>, a list of 'path<TAB>page' lines (# comments allowed).");

        var output = arguments.Get("out")
            ?? throw new ArgumentException("Give --out <file.pdf>.");

        if (!File.Exists(listPath))
            throw new FileNotFoundException($"No page list at {listPath}");

        var references = new List<PageReference>();
        foreach (var raw in File.ReadLines(listPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[^1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var page))
            {
                Console.Error.WriteLine($"skipping unreadable line: {line}");
                continue;
            }

            references.Add(new PageReference(string.Join('\t', parts[..^1]).Trim(), page));
        }

        if (references.Count == 0)
            throw new ArgumentException($"No usable lines in {listPath}.");

        var dpi = arguments.GetInt("dpi") ?? 300;
        var quality = arguments.GetInt("quality") ?? 90;

        Console.WriteLine($"Pages   : {references.Count:N0} from {references.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} document(s)");
        Console.WriteLine($"Render  : {dpi} dpi, greyscale, JPEG quality {quality}");
        Console.WriteLine($"Output  : {output}");
        Console.WriteLine();

        var book = new PageBook(new PageRasteriser(new RasterOptions { Dpi = dpi }));
        var outcomes = book.Assemble(references, output, quality);

        var written = outcomes.Where(o => o.Skipped is null).ToList();
        var skipped = outcomes.Where(o => o.Skipped is not null).ToList();

        // The manifest is the whole point of keeping the outcomes: a result on book page 7 means
        // nothing unless it can be traced back to the document and page it came from.
        var manifest = Path.ChangeExtension(output, ".manifest.tsv");
        using (var writer = new StreamWriter(manifest))
        {
            writer.WriteLine("bookPage\tsourcePage\tsourceFile");
            foreach (var o in written)
                writer.WriteLine($"{o.BookPage}\t{o.Source.PageNumber}\t{o.Source.Path}");
        }

        Console.WriteLine($"Wrote   : {written.Count:N0} page(s)");
        Console.WriteLine($"Manifest: {manifest}");

        if (skipped.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Could not render {skipped.Count:N0}:");
            foreach (var o in skipped.Take(10))
                Console.WriteLine($"  {Path.GetFileName(o.Source.Path)} p{o.Source.PageNumber}: {o.Skipped}");
        }

        Console.WriteLine();
        Console.WriteLine("There is no text layer in this file. Recognise it with another engine and");
        Console.WriteLine("with `manualforge ocr`, and the two outputs describe the same pixels.");

        return skipped.Count == 0 ? 0 : 3;
    }
}
