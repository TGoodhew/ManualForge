using System.Globalization;
using ManualForge.Core.Ocr;

namespace ManualForge.Cli;

/// <summary>
/// A small argument parser. Hand-rolled to keep the dependency list to the agreed set; it handles
/// <c>--name value</c>, <c>--name=value</c> and bare <c>--flag</c>, which is all this tool needs.
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
        "tensorrt" or "trt" => OcrAccelerator.TensorRt,
        "cuda" => OcrAccelerator.Cuda,
        "directml" or "dml" => OcrAccelerator.DirectMl,
        "cpu" => OcrAccelerator.Cpu,
        _ => OcrAccelerator.Auto,
    };

    /// <summary>
    /// <c>--tensorrt-models rec,cls,det</c>: which models TensorRT runs; the rest run on CUDA.
    /// Absent, the engine's default (<see cref="OcrEngineOptions.TensorRtModels"/>).
    /// </summary>
    public PaddleOcrNet.Services.TensorRtModels TensorRtModels()
    {
        if (Get("tensorrt-models") is not { Length: > 0 } list)
            return new OcrEngineOptions().TensorRtModels;

        var models = PaddleOcrNet.Services.TensorRtModels.None;
        foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            models |= name.ToLowerInvariant() switch
            {
                "det" or "detector" => PaddleOcrNet.Services.TensorRtModels.Detector,
                "cls" or "classifier" => PaddleOcrNet.Services.TensorRtModels.Classifier,
                "rec" or "recognizer" or "recogniser" => PaddleOcrNet.Services.TensorRtModels.Recognizer,
                _ => throw new ArgumentException($"--tensorrt-models: '{name}' is not det, cls or rec."),
            };
        }

        return models;
    }

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
            ManualForge — searchable PDFs from scanned technical manuals

            usage:
              manualforge ocr <input.pdf> [options]     OCR a PDF and overlay an invisible text layer
              manualforge inspect <input.pdf>           Report page count, sizes and existing text
              manualforge status <folder>               Show the work queue on disk; changes nothing
              manualforge survey <folder>               Classify a library; changes no PDF
              manualforge run <folder>                  Classify, flatten, OCR and replace, resumably
              manualforge doctor <folder|file.pdf>      Find pages whose text layer is incomplete (also: audit)
              manualforge repair <folder|file.pdf>      OCR just those pages and keep what they missed
              manualforge index <folder>                Build the full-text index over a library
              manualforge search <query> --library <f>  Query it: manual, page and snippet
              manualforge reconcile <folder>            Which PDFs are not in the index, and why
              manualforge gpu                           Report which execution provider is active, and the
                                                        pages in flight learned for it; --retune forgets them
              manualforge setup gpu                     Download CUDA, cuDNN and TensorRT from NVIDIA into
                                                        ManualForge's own folder; --remove takes them away
              manualforge setup mcp                     Register the MCP server with every app here that can
                                                        use it; --list shows where, --remove takes it out
              manualforge setup models                  Fetch the OCR models now, rather than on the first run
              manualforge version                       Which build this is, and where it was loaded from
              manualforge truth <input.pdf> --pages …   Seed hand-correctable ground-truth pages
              manualforge benchmark --truth <folder>    Measure character and word error rates
              manualforge baselines --library <folder>  Measure where words' baselines sit against their ink
              manualforge pages <folder> --shape drawn  Find test pages by shape, from the audit's geometry
              manualforge assemble --pages <list>       Collect pages from many PDFs into one image-only PDF

            ocr options:
              --out <path>            Output file. Default: <input>.searchable.pdf next to the source.
              --dpi <n>               Rasterisation resolution. Default 300.
              --pages <spec>          Pages to process, e.g. 1,4,7-9. Default: all.
              --engine <name>         auto | tensorrt | cuda | cpu. Default auto, which uses TensorRT
                                      when it is installed, CUDA otherwise, and the CPU when neither loads.
              --batch <n>             Recognition batch size. Default 8.
              --min-confidence <x>    Drop words below this score when WRITING the text layer.
                                      Default 0.30. It does not change what the recogniser
                                      returns; --drop-score does that.
              --drop-score <x>        The recogniser's own floor: a candidate below this never
                                      comes back at all. Default 0.30. Lowering it keeps weak
                                      readings, which is worth trying on material that reads
                                      as noise.
              --text <path>           Also write a plain-text dump of what was recognised.
              --deskew                Straighten skewed pages before recognition. Off by default:
                                      it reads no more for it, and on a few pages it still places
                                      the text layer 1-2 pt below the ink.
              --no-denoise            Skip despeckling before recognition.
              --server-models         PP-OCRv5's larger networks. About 6% more text on tables
                                      for 9.4x the runtime. Off by default.
              --trust-orientation     Turn every page the way the orientation classifier says,
                                      without reading it again as it stands. The classifier
                                      misfires on about a third of upright pages; this is how
                                      pages were read before that was measured.
              --no-orphan-rescue      Leave lone characters the detector never boxed, such as
                                      a parts list's check digits and quantities, unread.
                                      This is how pages were read before that was measured.
              --no-stack-split        Leave a column of table rows boxed as one tall word, such
                                      as NNNNN or mmmmm, as it was read, instead of cutting it
                                      into its rows and reading each.
              --verify-ink            Re-render both files and prove the page image is unchanged.
              --dry-run               Do everything except write the output.
              --overwrite             Replace an existing output file.

            survey and run options:
              --policy <spec>         Per-class actions, e.g. ImageOnly=ocr,SuspectText=redo.
                                      Default: ImageOnly=ocr and everything else skipped.
              --state <path>          State database. Default <root>/_Originals/manualforge.db.
              --no-dedup              Recognise every copy separately instead of once per document.
              --trim-missing          Forget the records of files that are no longer on disk. They
                                      are reported either way, and left out of every total.

            survey options:
              --csv <path>            Write the survey table to CSV.

            run options:
              --originals <name>      Name of the originals folder. Default _Originals.
              --engine <name>         As for ocr.
              --tensorrt-models <list>
                                      Which models TensorRT runs: any of det, cls, rec. Default
                                      cls,rec; the rest run on CUDA. Adding det is about 9%
                                      faster and reads about twice as many words differently.
              --dpi <n>               Rasterisation resolution. Default 300.
              --batch <n>             Recognition batch size. Default 8.
              --limit <n>             Stop after n files.
              --dry-run               Do all the work except replacing files.
              --survey-only           Classify and report, then stop.
              --retry-skipped         Reconsider files skipped by an earlier run.
              --redo-completed        Recognise every finished file again from its kept original,
                                      replacing the searchable copy an earlier run made. That copy
                                      is kept under _superseded in the originals folder. With
                                      --dry-run it times the work and changes nothing.
              --documents <file>      Run only the PDFs listed in <file>, one path per line.
                                      With --redo-completed, reads just those again.
              --refuse-signed         Skip digitally signed files. By default they are processed,
                                      which invalidates their signatures; every one is reported.
              --gpu-concurrency <n>   Pages to recognise at once, fixed. By default each run tries
                                      one more than the card last held, until one spills to
                                      system memory, and remembers the result for the card.
                                      Going past what the card holds slows the run sharply.
              --raster-workers <n>    CPU threads turning pages into bitmaps. Default 2.
              --cpu-threads <n>       Cap the threads ONNX Runtime uses per operator. The default
                                      is every core, which is right for a CPU-only run and rude if
                                      you want to use the machine while it works.

            status options:
              --state <path>          State database. Default <root>/_Originals/manualforge.db.
              --originals <name>      Name of the originals folder. Default _Originals.
              --limit <n>             How many outstanding and recently finished files to list.
                                      Default 10.

            doctor options (also: audit):
              --detail                Per-page numbers for every flagged page, not just a count.
              --limit <n>             How many documents each ranked list shows. Default 25.
              --explain <page>        Audit one page of one PDF and print every signal.
              --dump <path.png>       With --explain, draw what the ink comparison saw: grey for
                                      ink an extracted glyph accounts for, black for ink nothing
                                      accounts for, red boxes round clusters counted as lettering.
              --json <path>           Machine-readable findings, which `repair` does not need but
                                      anything else looking at this does.
              --recheck               Re-audit files that have not changed since the last run.
              --trim-missing          Forget audited files that are no longer on disk, with their
                                      findings and repairs. The standing counts them until then.
              --report                Print what an earlier audit found; audit nothing. Given a
                                      PDF rather than a folder, page by page for that file.
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
              --render-below-chars <n>
                                      Render a page with fewer than this many characters. 900.
              --render-above-paths <n>
                                      Render a page painting at least this many paths. 40.
              --render-above-image <x>
                                      Render a page whose images cover at least this share of it.
                                      Default 0.10; 1.0 turns this test off.
              --rule-run <n>          Read this many same-width clusters, evenly spaced down one
                                      column, as a ruled line rather than lettering. Default 3;
                                      0 turns this test off.
              --sample <n>            Audit n pages per document instead of all of them.
              --workers <n>           Documents at once. Default half the cores.
              --doctor-db <path>      Findings database. Default <root>/_Originals/manualforge-doctor.db.
              --no-reverse-video      Do not look inside solid blocks of ink for glyph-shaped holes:
                                      white lettering on black, such as an instrument screenshot's
                                      readouts. On by default: 17 of 20 pages it adds give the
                                      repair real text. docs/measurements/reverse-video.md.
              --reverse-video-area <x>
                                      The smallest solid block worth looking inside, in square
                                      points. Default 180.
              --reverse-video-fill <x>
                                      How much of a block must be ink for it to count as solid.
                                      Default 0.6.

            repair options:
              --dpi <n>               Override the per-page resolution the audit suggested.
              --max-dpi <n>           Ceiling on that suggestion. Default 600.
              --min-confidence <x>    Drop words below this score when keeping what was read.
                                      Default 0.30.
              --engine <name>         As for ocr.
              --batch <n>             Recognition batch size. Default 8.
              --cpu-threads <n>       Cap the threads ONNX Runtime uses per operator.
              --redo                  Re-recognise pages an earlier run already did.
              --redo-before <time>    Re-recognise only pages repaired before <time>, e.g.
                                      "2026-09-28 20:00". Unlike --redo it can be restarted with
                                      the same time and carries on where it stopped.
              --limit <n>             Stop after n documents, worst first.
              --documents <file>      Repair only the PDFs listed in <file>, one path per line.
              --plan                  Say which pages this would read and how long it should
                                      take, then stop. Loads no models and uses no GPU.
              --worst-only            Skip documents the audit called merely figure-heavy.
              --include-scans         Also redo scanned pages whose existing OCR missed lettering.
                                      A different problem from the one the audit was built to find,
                                      and on a corpus this size, hours of work. Off by default.
              --doctor-db <path>      Findings database. Default <root>/_Originals/manualforge-doctor.db.

            index options:
              --index <path>          Index database. Default <root>/_Originals/manualforge-index.db.
              --reindex               Re-read every document, not just the ones that changed.
              --compact               Rewrite the index without the free space updates leave in it.
              --sidecars <folder>     Also write one plain-text file per document.
              --sidecars-inline       Also write one <name>.txt beside each PDF, in the library
                                      folder itself. Off by default.
              --no-repairs            Ignore text the repair recovered. For proving, by diffing,
                                      exactly what the merge changed and what it left alone.
              --workers <n>           Documents extracted at once. Default a third of the cores,
                                      at most 8.
              --doctor-db <path>      Where to find the text the repair recovered. Default
                                      <root>/_Originals/manualforge-doctor.db.

            search options:
              --library <folder>      Which library to search, when --index is not given.
              --index <path>          Index database to search instead.
              --limit <n>             Results to show. Default 10.
              --model <name>          Rank manuals whose title or file name holds this instrument
                                      model higher, without hiding the others.
              --show-duplicates       List identical copies separately instead of folding them.
              --json                  One JSON document: every hit, plus caveats. For programs.
              --files-only            Ranked paths, one per line. For programs.
              --rank-labels <x>       Ranking multipliers for a term on a line of its own, for
              --rank-recovered <x>    text the repair recovered when the query is one bare word,
              --rank-notation <x>     and for command mnemonics in their own case. Default 1.4
                                      each; 1 turns one off.
              The exit code is 0 with results, 2 when nothing matched and 1 when it could not
              search. With --json or --files-only, notes and caveats go to stderr.

            reconcile options:
              --library <folder>      The library, if it is not given first.
              --index <path>          Index database. Default <root>/_Originals/manualforge-index.db.
              --trim-missing          Forget indexed documents whose file has gone.
                                      `index --reindex` does not clear these - it rebuilds what it
                                      finds on disk and never visits what has been deleted - so
                                      without this a search can return a page of a file that is
                                      not there.

            gpu options:
              --engine <name>         Which engine to report on, as for ocr. Default auto.
              --retune                Forget the pages in flight learned for this card and engine,
                                      so the next run learns them again. Measured speeds are kept.

            pages options:
              --shape drawn|scanned   drawn: a circuit diagram, where the drawing outweighs the
                                      labels. scanned: a photographed sheet, few glyphs and much
                                      ink no glyph accounts for. Default drawn.
              --limit <n>             How many pages. Default 16.
              --per-document <n>      At most this many from any one document, so a single densely
                                      drawn manual cannot supply the whole set. Default 2.
              --out <file>            Write the list for `assemble` to read.
              --doctor-db <path>      Findings database. Default <root>/_Originals/manualforge-doctor.db.

            assemble options:
              --pages <file>          A list of 'path<TAB>page' lines; # starts a comment.
              --out <file.pdf>        Where to write. A .manifest.tsv is written beside it mapping
                                      each book page back to its source document and page.
              --dpi <n>               Rasterisation resolution. Default 300.
              --quality <n>           JPEG quality, 1-100. Default 90.

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
              --publisher             truth only: seed from born-digital pages of a library (given
                                      first or by --library), whose text layer is the publisher's
                                      own typesetting, instead of from one PDF. With --count <n>
                                      (default 12), --min-chars <n> (default 900),
                                      --per-document <n> (default 2) and --seed <n> (default 1).
              --dpi <n>               Rasterisation resolution. Default 300.
              --engine <name>         As for ocr.
              --sweep                 Measure deskew, denoise and resolution against each other.
              --no-deskew             benchmark only: skip deskewing, which is on by default here.
              --no-denoise            benchmark only: skip despeckling.
              --score-existing        Also score the text already in the files - point the truth
                                      set at another engine's output to compare against it.
              --existing-name <name>  What to call that column. Default "existing text layer".
              --csv <path>            Write per-page detail.

            baselines options:
              --library <folder>      The library to take born-digital pages from.
              --pages <n>             How many pages. Default 12.
              --dpi <n>               Rasterisation resolution. Default 300.
              --seed <n>              Seed for choosing the pages at random. Default 1.

            setup gpu options:
              --plan                  Say what would be downloaded, and the licences, and stop.
              --accept-nvidia-licences
                                      Required to download: NVIDIA's libraries come under its
                                      licences.
              --no-tensorrt           Leave TensorRT out (1.9 GB less; about half the speed).
              --force                 Download ManualForge's own copies even if CUDA is already found.
              --downloads <folder>    Keep the archives there and reuse them; otherwise they are
                                      deleted once their DLLs are in place.
              --remove                Delete the libraries `setup gpu` downloaded, and nothing else.

            setup mcp options:
              --library <folder>      The library the server searches. Default: MANUALFORGE_LIBRARY.
              --client <id,...>       Only these: claude-desktop-store, claude-desktop, claude-code,
                                      vscode, vscode-insiders, visual-studio, cursor, windsurf,
                                      gemini, codex, lmstudio, continue.
              --server <exe>          The server to register. Default: the one beside this program.
              --list                  Which apps are here, and whether each has ManualForge.
              --plan                  What registering would change, changing nothing.
              --remove                Undo what registering recorded: take out the entries it added,
                                      put back the ones it replaced, and leave the rest.
              --remove --all          Take out every entry named manualforge, whoever added it.

            general options:
              --models <path>         Model cache directory.
              --log <path>            JSON-lines log file, written exactly there. The default is a
                                      daily-rolling file under %LOCALAPPDATA%, kept for 30 days.
              --verbose               Debug-level logging and full stack traces.

            The source file is opened read-only and is never modified.
            """);
    }
}
