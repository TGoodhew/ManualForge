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
              manualforge doctor <folder>                Find pages whose text layer is incomplete
              manualforge repair <folder>                OCR just those pages and keep what they missed
              manualforge index <folder>                 Build the full-text index over a library
              manualforge search <query> --library <f>    Query it: manual, page and snippet
              manualforge reconcile <folder>             Which PDFs are not in the index, and why
              manualforge truth <input.pdf> --pages …     Seed hand-correctable ground-truth pages
              manualforge benchmark --truth <folder>      Measure character and word error rates
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
              --gpu-concurrency <n>   Pages to recognise at once. The default is chosen from free
                                      VRAM; going past what the card holds collapses throughput
                                      rather than slowing it, so raise this deliberately.
              --raster-workers <n>    CPU threads turning pages into bitmaps. Default 2.
              --cpu-threads <n>       Cap the threads ONNX Runtime uses per operator. The default
                                      is every core, which is right for a CPU-only run and rude if
                                      you want to use the machine while it works.

            doctor options (also: audit):
              --detail                Per-page numbers for every flagged page, not just a count.
              --explain <page>        Audit one page of one PDF and print every signal.
              --dump <path.png>       With --explain, draw what the ink comparison saw: grey for
                                      ink an extracted glyph accounts for, black for ink nothing
                                      accounts for, red boxes round clusters counted as lettering.
              --json <path>           Machine-readable findings, which `repair` does not need but
                                      anything else looking at this does.
              --recheck               Re-audit files that have not changed since the last run.
              --report                Print what an earlier audit found; audit nothing.
              --reading-order <page>  Print one page's own text in the reading order the repair
                                      uses. For checking on a real page that a close-set table is
                                      read along its rows rather than down its columns.
              --review <n>            Draw n flagged and n unflagged pages at random and write the
                                      picture the detector worked from for each, so its error rate
                                      can be measured by eye. --seed <n> and --into <folder> make
                                      the sample reproducible; --kind drawn|raster narrows it.
              --uncovered-ink <x>     Flag above this share of the page being unaccounted-for ink.
                                      Default 0.002. See docs/UNDER-EXTRACTION.md for why.
              --min-blobs <n>         And at least this many glyph-shaped clusters of it. Default
                                      40. Both must hold: ink alone flags every schematic.
              --audit-dpi <n>         Resolution for the audit render. Default 150.
              --render-below-chars <n>  Render a page with fewer than this many characters. 900.
              --render-above-paths <n>  Render a page painting at least this many paths. 40.
              --sample <n>            Audit n pages per document instead of all of them.
              --workers <n>           Documents at once. Default half the cores.
              --doctor-db <path>      Findings database. Default <root>/_Originals/manualforge-doctor.db.

            repair options:
              --dpi <n>               Override the per-page resolution the audit suggested.
              --max-dpi <n>           Ceiling on that suggestion. Default 600.
              --min-confidence <x>    Drop words recognised below this score. Default 0.30.
              --redo                  Re-recognise pages an earlier run already did.
              --limit <n>             Stop after n documents, worst first.
              --worst-only            Skip documents the audit called merely figure-heavy.
              --include-scans         Also redo scanned pages whose existing OCR missed lettering.
                                      A different problem from the one the audit was built to find,
                                      and on a corpus this size, hours of work. Off by default.

            index / search options:
              --index <path>          Index database. Default <root>/_Originals/manualforge-index.db.
              --library <folder>      Which library to search, when --index is not given.
              --reindex               Re-read every document, not just the ones that changed.
              --sidecars <folder>     Also write one plain-text file per document.
              --no-repairs            Ignore text the repair recovered. For proving, by diffing,
                                      exactly what the merge changed and what it left alone.
              --limit <n>             Results to show. Default 10.
              --show-duplicates       List identical copies separately instead of folding them.

            truth / benchmark options:
              --truth <folder>        The ground-truth set: one corrected text file per page.
              --out <folder>          Where `truth` writes the set to seed.
              --pages <spec>          Pages to take as ground truth, e.g. 6,29,31.
              --kind <name>           prose | table | schematic | mixed. Reported separately,
                                      because one average hides the case that is actually bad.
              --library <folder>      Root the manifest's relative paths resolve against.
              --from-text-layer       Seed from the text already in the PDF rather than by OCR.
              --seed-from <file>      Take the seed text from this copy instead, while the manifest
                                      still records the file given. For two copies of one scan with
                                      different text layers: measure one, correct from the other.
              --sweep                 Measure deskew, denoise and resolution against each other.
              --score-existing        Also score the text already in the files - point the truth
                                      set at another engine's output to compare against it.
              --csv <path>            Write per-page detail.

            general options:
              --models <path>         Model cache directory.
              --log <path>            JSON-lines log file, written exactly there. The default is a
                                      daily-rolling file under %LOCALAPPDATA%, kept for 30 days.
              --verbose               Debug-level logging and full stack traces.

            The source file is opened read-only and is never modified.
            """);
    }
}
