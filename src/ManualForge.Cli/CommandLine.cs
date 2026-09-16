using System.Globalization;
using ManualForge.Core.Ocr;

namespace ManualForge.Cli;

/// <summary>
/// A small argument parser. Hand-rolled to keep the dependency list to the agreed set; it handles
/// <c>--name value</c>, <c>--name=value</c> and bare <c>--flag</c>, which is all the prototype needs.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];

    public string? Command { get; private init; }

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine { Command = args.Length > 0 ? args[0] : null };

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                line._positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                line._options[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            // A following token that is not itself an option becomes this option's value.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                line._options[name] = args[++i];
                continue;
            }

            line._options[name] = null;
        }

        return line;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;

    public int? GetInt(string name) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public double? GetDouble(string name) =>
        double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    public OcrAccelerator Accelerator() => Get("engine")?.ToLowerInvariant() switch
    {
        "cuda" => OcrAccelerator.Cuda,
        "directml" or "dml" => OcrAccelerator.DirectMl,
        "cpu" => OcrAccelerator.Cpu,
        _ => OcrAccelerator.Auto,
    };

    /// <summary>Parses <c>--pages 1,4,7-9</c> into an explicit list of page numbers.</summary>
    public IReadOnlyList<int> PageRange()
    {
        var spec = Get("pages");
        if (string.IsNullOrWhiteSpace(spec))
            return [];

        var pages = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                var from = int.Parse(part[..dash], CultureInfo.InvariantCulture);
                var to = int.Parse(part[(dash + 1)..], CultureInfo.InvariantCulture);
                if (to < from)
                    throw new ArgumentException($"Page range '{part}' runs backwards.");
                pages.AddRange(Enumerable.Range(from, to - from + 1));
            }
            else
            {
                pages.Add(int.Parse(part, CultureInfo.InvariantCulture));
            }
        }

        return pages;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            ManualForge — searchable PDFs from scanned technical manuals (phase 1 prototype)

            usage:
              manualforge ocr <input.pdf> [options]      OCR a PDF and overlay an invisible text layer
              manualforge inspect <input.pdf>            Report page count, sizes and existing text
              manualforge status <folder>                Show the work queue on disk; changes nothing
              manualforge survey <folder>                Classify a library; changes nothing
              manualforge run <folder>                   Classify, flatten, OCR and replace, resumably
              manualforge gpu                            Report which execution provider is active

            ocr options:
              --out <path>            Output file. Default: <input>.searchable.pdf next to the source.
              --dpi <n>               Rasterisation resolution. Default 300.
              --pages <spec>          Pages to process, e.g. 1,4,7-9. Default: all.
              --engine <name>         auto | cuda | directml | cpu. Default auto.
              --batch <n>             Recognition batch size. Default 8.
              --min-confidence <x>    Drop words recognised below this score. Default 0.30.
              --text <path>           Also write a plain-text dump of what was recognised.
              --no-deskew             Skip deskewing before recognition.
              --no-denoise            Skip despeckling before recognition.
              --verify-ink            Re-render both files and prove the page image is unchanged.
              --dry-run               Do everything except write the output.
              --overwrite             Replace an existing output file.

            survey / run options:
              --policy <spec>         Per-class actions, e.g. ImageOnly=ocr,SuspectText=redo.
                                      Default: ImageOnly=ocr and everything else skipped.
              --originals <name>      Name of the originals folder. Default _Originals.
              --state <path>          State database. Default <root>/_Originals/manualforge.db.
              --csv <path>            Write the survey table to CSV (survey only).
              --limit <n>             Stop after n files (run only).
              --survey-only           Classify and report, then stop (run only).
              --retry-skipped         Reconsider files skipped by an earlier run.
              --no-dedup              Recognise every copy separately instead of once per document.
              --refuse-signed         Skip digitally signed files. By default they are processed,
                                      which invalidates their signatures; every one is reported.
              --trim-missing          Forget the records of files that are no longer on disk. They
                                      are reported either way, and left out of every total.

            general options:
              --models <path>         Model cache directory.
              --log <path>            JSON-lines log file.
              --verbose               Debug-level logging and full stack traces.

            The source file is opened read-only and is never modified.
            """);
    }
}
