using ManualForge.Core.Auditing;
using ManualForge.Core.Benchmarking;

namespace ManualForge.Cli;

/// <summary>
/// Finds test pages by shape, and writes a list `assemble` can consume.
///
/// <para>
/// Separate from <c>assemble</c> on purpose: choosing what to test and building the test are
/// different decisions, and the choice is the one that needs arguing about. A list on disk can be
/// read, edited by hand and committed alongside the result it produced.
/// </para>
/// </summary>
internal static class PagesCommand
{
    public static int Run(CommandLine arguments)
    {
        var library = arguments.Positional(0)
            ?? throw new ArgumentException("Give the path to the library folder.");

        var database = arguments.Get("doctor-db") ?? DoctorStore.DefaultPathFor(library);

        var shape = (arguments.Get("shape") ?? "drawn").ToLowerInvariant() switch
        {
            "drawn" or "diagram" or "circuit" => PageShape.Drawn,
            "scanned" or "raster" or "sheet" => PageShape.Scanned,
            var other => throw new ArgumentException(
                $"Unknown --shape '{other}'. Use drawn or scanned."),
        };

        var count = arguments.GetInt("limit") ?? 16;
        var perDocument = arguments.GetInt("per-document") ?? 2;

        var pages = new PageSelector(database).Select(shape, count, perDocument);

        Console.WriteLine($"Audit   : {database}");
        Console.WriteLine($"Shape   : {shape}");
        Console.WriteLine($"Found   : {pages.Count:N0} page(s) from " +
                          $"{pages.Select(p => Path.GetFileName(p.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} document(s)");
        Console.WriteLine();

        var label = shape == PageShape.Drawn ? "paths" : "blobs";
        foreach (var page in pages)
        {
            Console.WriteLine(
                $"  {page.Weight,7:N0} {label} /{page.Glyphs,5} glyphs   " +
                $"{Path.GetFileName(page.Path),-46} p{page.PageNumber}");
        }

        var output = arguments.Get("out");
        if (output is not null)
        {
            using var writer = new StreamWriter(output);
            writer.WriteLine($"# {shape} pages, chosen on the audit's geometry rather than on text");
            foreach (var page in pages)
                writer.WriteLine($"{page.Path}\t{page.PageNumber}");

            Console.WriteLine();
            Console.WriteLine($"Written : {output}");
            Console.WriteLine($"          manualforge assemble --pages \"{output}\" --out <book.pdf>");
        }

        return pages.Count == 0 ? 2 : 0;
    }
}
