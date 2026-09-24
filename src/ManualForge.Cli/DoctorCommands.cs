using System.Globalization;
using System.Text.Json;
using ManualForge.Core.Auditing;
using ManualForge.Core.Ocr;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>
/// Finds pages whose text layer is present but incomplete, and reports them without changing
/// anything.
/// </summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var target = arguments.Positional(0)
            ?? throw new ArgumentException("Give the path to a folder of PDFs, or to one PDF.");

        if (!Directory.Exists(target) && !File.Exists(target))
            throw new FileNotFoundException($"No such file or folder: {target}", target);

        var options = OptionsFrom(arguments);

        var explain = arguments.GetInt("explain");
        if (explain is not null)
            return Explain(target, explain.Value, options, arguments.Get("dump"));

        var readingOrder = arguments.GetInt("reading-order");
        if (readingOrder is not null)
        {
            foreach (var line in UnderExtractionDetector.ReadingOrderOf(target, readingOrder.Value))
                Console.WriteLine(line);

            return 0;
        }

        var review = arguments.GetInt("review");
        if (review is not null)
            return Review(target, review.Value, options, arguments);

        if (arguments.Has("report"))
            return Report(target, arguments);

        var storePath = arguments.Get("doctor-db")
            ?? DoctorStore.DefaultPathFor(File.Exists(target)
                ? Path.GetDirectoryName(Path.GetFullPath(target))!
                : target);

        Console.WriteLine($"Library : {target}");
        Console.WriteLine($"Findings: {storePath}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Rule    : flag a page when over {options.UncoveredInkFraction:P2} of it is ink no " +
            $"extracted glyph accounts for, in at least {options.MinimumGlyphLikeBlobs} glyph-shaped clusters"));
        Console.WriteLine($"Render  : {options.AuditDpi} dpi, {options.Workers} document(s) at a time");
        Console.WriteLine();

        var runner = new DoctorRunner(loggerFactory.CreateLogger<DoctorRunner>());

        var lastReported = 0;
        var progress = new Progress<DoctorProgress>(p =>
        {
            if (p.DocumentsDone - lastReported < 5 && p.DocumentsDone != p.DocumentsTotal)
                return;

            lastReported = p.DocumentsDone;
            Console.Write(
                $"\r  {p.DocumentsDone:N0}/{p.DocumentsTotal:N0} documents, " +
                $"{p.FlaggedDocuments:N0} flagged, {p.FlaggedPages:N0} pages...   ");
        });

        var report = await runner.RunAsync(
            target, options, storePath, arguments.Has("recheck"), progress, cancellationToken)
            .ConfigureAwait(false);

        Console.Write("\r".PadRight(78) + "\r");

        PrintSummary(report);
        PrintStanding(storePath);
        PrintRanked(report, arguments.GetInt("limit") ?? 25, arguments.Has("detail"));

        var json = arguments.Get("json");
        if (json is not null)
        {
            await WriteJsonAsync(report, json, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Machine-readable findings written to {json}");
        }

        if (report.PagesFlagged > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing has been changed. To recover the missing text:");
            Console.WriteLine($"  manualforge repair \"{target}\"          OCR the flagged pages only");
            Console.WriteLine($"  manualforge index \"{target}\"           merge it into the search index");
        }

        return 0;
    }

    /// <summary>
    /// Audits one page and, on request, writes the picture the detector worked from: pale grey for
    /// ink an extracted glyph accounts for, black for ink that nothing accounts for, and a red box
    /// round every cluster counted as lettering.
    /// </summary>
    private static int Explain(string path, int pageNumber, DoctorOptions options, string? dump)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"--explain needs a single PDF, not a folder.", path);

        var (audit, diagnostic) = new UnderExtractionDetector(options).Explain(path, pageNumber);

        Console.WriteLine($"{Path.GetFileName(path)}, page {pageNumber}");
        Console.WriteLine($"  verdict            : {audit.Verdict}");
        Console.WriteLine($"  glyphs drawn       : {audit.GlyphsDrawn:N0} ({audit.CharactersDecoded:N0} decode)");
        Console.WriteLine($"  path / text ops    : {audit.PathPaintOperations:N0} / {audit.TextShowOperations:N0}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  ink                : {audit.Ink.InkFraction:P3} of the page"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  unaccounted for    : {audit.Ink.UncoveredInkFraction:P3}"));
        Console.WriteLine($"  glyph-like blobs   : {audit.Ink.GlyphLikeBlobs:N0}, tallest " +
                          $"{audit.Ink.LargestBlobHeightPt:F1} pt");
        Console.WriteLine($"  recoverable (est.) : {audit.EstimatedRecoverableCharacters:N0} characters");
        Console.WriteLine($"  suggested OCR dpi  : {audit.SuggestedOcrDpi}");

        foreach (var signal in audit.Signals)
            Console.WriteLine($"  signal             : {signal}");

        foreach (var letter in UnderExtractionDetector.DescribeLetters(path, pageNumber))
            Console.WriteLine($"  letter             : {letter}");

        if (dump is null || diagnostic is null)
        {
            diagnostic?.Dispose();
            return 0;
        }

        using (diagnostic)
        using (var data = diagnostic.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(dump))
        {
            data.SaveTo(file);
        }

        Console.WriteLine($"  picture            : {dump}");
        return 0;
    }

    /// <summary>
    /// Reads back what an earlier audit found, without auditing anything. The audit over a hundred
    /// thousand pages takes long enough that looking at its results should not require re-running
    /// it — and the results are what anybody actually argues with.
    /// </summary>
    private static int Report(string target, CommandLine arguments)
    {
        var root = File.Exists(target) ? Path.GetDirectoryName(Path.GetFullPath(target))! : target;
        var storePath = arguments.Get("doctor-db") ?? DoctorStore.DefaultPathFor(root);

        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine($"No audit at {storePath}. Run `manualforge doctor \"{target}\"` first.");
            return 1;
        }

        using var store = new DoctorStore(storePath, readOnly: true);

        // A single file asks a different question from a library: not "what should I repair next"
        // but "what happened to this one", page by page. That is the question a repair which
        // recovered nothing leaves behind, and nothing else here could answer it.
        if (File.Exists(target))
            return ReportDocument(store, target, arguments.GetInt("limit") ?? 25);

        var summary = store.Summary();
        var flagged = store.Flagged();

        var toRepair = flagged.Where(d => d.Verdict == DocumentVerdict.UnderExtracted).ToArray();
        var figures = flagged.Where(d => d.Verdict == DocumentVerdict.Figures).ToArray();
        var scans = flagged.Where(d => d.Verdict == DocumentVerdict.ScannedGaps).ToArray();

        Console.WriteLine($"Findings : {storePath}");
        Console.WriteLine($"Audited  : {summary.DocumentsAudited:N0} document(s)");
        Console.WriteLine(
            $"Flagged  : {summary.DocumentsFlagged:N0} document(s), {summary.FlaggedPages:N0} page(s); " +
            $"{summary.RepairedPages:N0} repaired, {summary.OutstandingPages:N0} outstanding");
        Console.WriteLine(
            $"           {summary.DrawnPages:N0} of those page(s) are drawn on the page; the rest are " +
            "lettering inside an image");
        Console.WriteLine(
            $"           {toRepair.Length:N0} drawn-and-missing document(s) " +
            $"({toRepair.Sum(d => (long)d.DrawnPages):N0} drawn pages), " +
            $"{figures.Length:N0} with isolated figure pages " +
            $"({figures.Sum(d => (long)d.FlaggedPages):N0} pages), " +
            $"{scans.Length:N0} scanned with OCR gaps " +
            $"({scans.Sum(d => (long)d.FlaggedPages):N0} pages)");
        Console.WriteLine();

        var limit = arguments.GetInt("limit") ?? 25;

        PrintStoredTable("Drawn rather than typeset — the content never had a text layer", toRepair, limit);
        PrintStoredTable("Otherwise sound, with figure pages whose labels did not extract", figures, limit);
        PrintStoredTable(
            "Scanned, and their existing OCR missed lettering — `repair --include-scans` does these",
            scans, limit);

        return 0;
    }

    /// <summary>
    /// Every flagged page of one document and what the repair got back for it, including the pages
    /// it got nothing back for.
    ///
    /// <para>
    /// A page that was rendered, recognised and returned no text is not the same as a page that was
    /// never looked at, and the summary counts cannot tell them apart. Seeing where those pages sit
    /// is most of the diagnosis: scattered, they are hard pages; consecutive, they are a property of
    /// the file.
    /// </para>
    /// </summary>
    private static int ReportDocument(DoctorStore store, string path, int limit)
    {
        var full = Path.GetFullPath(path);
        var document = store.Flagged(int.MaxValue)
            .FirstOrDefault(d => string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            Console.WriteLine($"{Path.GetFileName(full)} is not in the audit as a flagged document.");
            Console.WriteLine("Either it was sound, or it has not been audited. `doctor <folder>` audits.");
            return 0;
        }

        var findings = store.Findings(full, PageVerdict.UnderExtracted);
        var repairs = store.Repairs(full, document.ContentHash);

        var empty = findings
            .Where(f => repairs.TryGetValue(f.PageNumber, out var r) && r.OcrText.Length == 0)
            .Select(f => f.PageNumber)
            .ToArray();

        var unrepaired = findings.Where(f => !repairs.ContainsKey(f.PageNumber))
            .Select(f => f.PageNumber).ToArray();

        Console.WriteLine($"Document : {document.Title}");
        Console.WriteLine($"Verdict  : {document.Verdict}, {document.PageCount:N0} page(s)");
        Console.WriteLine(
            $"Flagged  : {document.FlaggedPages:N0} page(s), {document.DrawnPages:N0} drawn; " +
            $"{document.RepairedPages:N0} repaired, {unrepaired.Length:N0} not yet");
        Console.WriteLine(
            $"Recovered nothing : {empty.Length:N0} page(s)" +
            (empty.Length == 0 ? string.Empty : $" — {Ranges(empty)}"));
        Console.WriteLine();

        Console.WriteLine(
            $"  {"Page",6}{"Kind",10}{"Uncovered ink",15}{"Blobs",7}{"Expected",10}{"Recovered",11}  confidence");
        Console.WriteLine("  " + new string('-', 78));

        foreach (var finding in findings.Take(limit))
        {
            repairs.TryGetValue(finding.PageNumber, out var repair);

            var recovered = repair is null
                ? "not repaired"
                : repair.OcrText.Length == 0 ? "nothing" : $"{repair.OcrText.Length:N0} chars";

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {finding.PageNumber,6:N0}{finding.Kind,10}{finding.UncoveredInkFraction,15:P2}" +
                $"{finding.GlyphLikeBlobs,7:N0}{finding.RecoverableCharacters,10:N0}{recovered,11}  " +
                $"{(repair is null || repair.OcrText.Length == 0 ? string.Empty : repair.MeanConfidence.ToString("P0", CultureInfo.InvariantCulture))}"));
        }

        if (findings.Count > limit)
            Console.WriteLine($"  ... and {findings.Count - limit:N0} more flagged page(s)");

        Console.WriteLine();

        if (empty.Length > 0)
        {
            Console.WriteLine("To see what the detector saw on a page that recovered nothing:");
            Console.WriteLine(
                $"  manualforge doctor \"{path}\" --explain {empty[0]} --dump page{empty[0]}.png");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Page numbers as ranges, because forty consecutive pages should read as forty.</summary>
    private static string Ranges(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        var start = pages[0];
        var previous = pages[0];

        foreach (var page in pages.Skip(1).Append(int.MinValue))
        {
            if (page == previous + 1)
            {
                previous = page;
                continue;
            }

            parts.Add(start == previous ? $"{start}" : $"{start}-{previous}");
            start = previous = page;
        }

        return string.Join(", ", parts);
    }

    private static void PrintStoredTable(string heading, IReadOnlyList<AuditedDocument> documents, int limit)
    {
        if (documents.Count == 0)
            return;

        Console.WriteLine(heading);
        Console.WriteLine(
            $"  {"Document",-44}{"Pages",7}{"Flagged",9}{"Drawn",8}{"Share",8}{"Chars",9}  dpi");
        Console.WriteLine("  " + new string('-', 90));

        foreach (var document in documents.Take(limit))
        {
            var share = document.PageCount == 0 ? 0 : document.FlaggedPages / (double)document.PageCount;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Shorten(document.Title, 43),-44}{document.PageCount,7:N0}{document.FlaggedPages,9:N0}" +
                $"{document.DrawnPages,8:N0}{share,8:P0}{document.RecoverableCharacters,9:N0}  " +
                $"{document.SuggestedDpi}"));
        }

        if (documents.Count > limit)
            Console.WriteLine($"  ... and {documents.Count - limit:N0} more");

        Console.WriteLine();
    }

    /// <summary>
    /// Draws a reproducible sample of flagged and unflagged pages and writes the picture the
    /// detector worked from for each, so its error rate can be measured by eye rather than assumed.
    ///
    /// <para>
    /// This exists because a detector whose error rate is unknown is not finished. Both halves of
    /// the sample matter: the flagged pages give precision, the unflagged pages give recall, and it
    /// is the second half that an author is least inclined to look at.
    /// </para>
    /// </summary>
    private static int Review(string target, int count, DoctorOptions options, CommandLine arguments)
    {
        var root = File.Exists(target) ? Path.GetDirectoryName(Path.GetFullPath(target))! : target;
        var storePath = arguments.Get("doctor-db") ?? DoctorStore.DefaultPathFor(root);
        var into = arguments.Get("into") ?? Path.Combine(Path.GetTempPath(), "manualforge-review");
        var seed = arguments.GetInt("seed") ?? 1;

        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine($"No audit at {storePath}. Run `manualforge doctor` first.");
            return 1;
        }

        Directory.CreateDirectory(into);

        using var store = new DoctorStore(storePath, readOnly: true);
        var detector = new UnderExtractionDetector(options);

        var kind = arguments.Get("kind")?.ToLowerInvariant() switch
        {
            "drawn" => (PageKind?)PageKind.Drawn,
            "raster" or "scan" or "scanned" => PageKind.Raster,
            _ => null,
        };

        var sample = store.SampleFlagged(count, seed, kind)
            .Select(p => (p.Path, p.PageNumber, Flagged: true))
            .Concat(store.SampleUnflagged(count, seed).Select(p => (p.Path, p.PageNumber, Flagged: false)))
            .ToArray();

        Console.WriteLine($"Sample : {sample.Length} page(s), seed {seed}, into {into}");
        Console.WriteLine();

        var n = 0;
        foreach (var (path, pageNumber, flagged) in sample)
        {
            n++;
            var name = $"{(flagged ? "flagged" : "clean")}-{n:D2}-" +
                       $"{Sanitise(Path.GetFileNameWithoutExtension(path))}-p{pageNumber}";

            try
            {
                var (audit, diagnostic) = detector.Explain(path, pageNumber);

                if (diagnostic is not null)
                {
                    using (diagnostic)
                    using (var data = diagnostic.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                    using (var file = File.Create(Path.Combine(into, name + ".png")))
                    {
                        data.SaveTo(file);
                    }
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(flagged ? "FLAGGED" : "clean  ")}  {audit.Kind,-6}  {name}.png  " +
                    $"ink {audit.Ink.InkFraction:P2} ({audit.Ink.UncoveredInkFraction:P2} unaccounted), " +
                    $"{audit.Ink.GlyphLikeBlobs} blobs, {audit.CharactersDecoded} chars, " +
                    $"{audit.PathPaintOperations} path ops"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name}: could not be rendered — {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "Look at each picture: grey is ink an extracted glyph accounts for, black is ink nothing");
        Console.WriteLine(
            "accounts for, red boxes are what was counted as lettering. A flagged page is right if");
        Console.WriteLine(
            "there is readable text in black; a clean page is right if there is not.");

        return 0;
    }

    private static string Sanitise(string name)
    {
        var safe = name.ToCharArray();
        for (var i = 0; i < safe.Length; i++)
        {
            if (!char.IsLetterOrDigit(safe[i]) && safe[i] is not ('-' or '_'))
                safe[i] = '-';
        }

        return new string(safe);
    }

    public static DoctorOptions OptionsFrom(CommandLine arguments) => new()
    {
        AuditDpi = arguments.GetInt("audit-dpi") ?? new DoctorOptions().AuditDpi,
        UncoveredInkFraction = arguments.GetDouble("uncovered-ink") ?? new DoctorOptions().UncoveredInkFraction,
        MinimumGlyphLikeBlobs = arguments.GetInt("min-blobs") ?? new DoctorOptions().MinimumGlyphLikeBlobs,
        RenderBelowCharactersPerPage =
            arguments.GetInt("render-below-chars") ?? new DoctorOptions().RenderBelowCharactersPerPage,
        RenderAtOrAbovePathOperations =
            arguments.GetInt("render-above-paths") ?? new DoctorOptions().RenderAtOrAbovePathOperations,
        SamplePages = arguments.GetInt("sample") ?? 0,
        Workers = arguments.GetInt("workers") ?? new DoctorOptions().Workers,
    };

    private static void PrintSummary(DoctorReport report)
    {
        Console.WriteLine($"Audited   : {report.DocumentsAudited:N0} document(s), {report.PagesExamined:N0} pages " +
                          $"in {report.Elapsed.TotalMinutes:F1} min ({report.PagesPerMinute:N0} pages/min)");

        if (report.DocumentsUnchanged > 0)
            Console.WriteLine($"Unchanged : {report.DocumentsUnchanged:N0} (audited before, file not touched since)");

        if (report.DocumentsFailed > 0)
            Console.WriteLine($"Failed    : {report.DocumentsFailed:N0}");

        Console.WriteLine();
        Console.WriteLine(
            $"Drawn, missing  : {report.ToRepair.Count:N0} document(s), " +
            $"{report.ToRepair.Sum(a => (long)a.DrawnPageCount):N0} page(s) whose content is drawn and " +
            $"extracts as nothing, about " +
            $"{report.ToRepair.Sum(a => (long)a.EstimatedRecoverableCharacters):N0} characters recoverable");

        if (report.WithFigures.Count > 0)
        {
            Console.WriteLine(
                $"Figure pages    : {report.WithFigures.Count:N0} otherwise-sound document(s) with " +
                $"{report.WithFigures.Sum(a => (long)a.FlaggedPageCount):N0} page(s) whose figure labels " +
                "did not extract");
        }

        if (report.WithScanGaps.Count > 0)
        {
            Console.WriteLine(
                $"Scan gaps       : {report.WithScanGaps.Count:N0} scanned document(s) with " +
                $"{report.WithScanGaps.Sum(a => (long)a.FlaggedPageCount):N0} page(s) whose existing OCR " +
                "missed lettering — a different problem, and a much larger one");
        }

        if (report.PagesWithNoTextLayer > 0)
        {
            Console.WriteLine(
                $"No text layer   : {report.PagesWithNoTextLayer:N0} page(s) — scans the OCR path already covers, " +
                "not counted above");
        }

        if (report.PagesUndecodable > 0)
        {
            Console.WriteLine(
                $"Undecodable     : {report.PagesUndecodable:N0} page(s) — glyphs drawn that no reader can decode, " +
                "a different repair");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Where the library stands overall, repairs included. The run above only describes the files
    /// it looked at this time; this describes every file the audit knows about, which is what
    /// answers "is it fixed yet".
    /// </summary>
    private static void PrintStanding(string storePath)
    {
        if (!File.Exists(storePath))
            return;

        try
        {
            using var store = new DoctorStore(storePath, readOnly: true);
            var summary = store.Summary();

            if (summary.FlaggedPages == 0)
                return;

            Console.WriteLine(
                $"Standing  : {summary.FlaggedPages:N0} flagged page(s) in total; " +
                $"{summary.RepairedPages:N0} repaired, {summary.OutstandingPages:N0} outstanding");

            if (summary.OutstandingPages == 0)
            {
                Console.WriteLine(
                    "            Every page this audit has ever flagged now has its text recovered. " +
                    "Re-index to be sure search has it.");
            }

            Console.WriteLine();
        }
        catch (Exception)
        {
            // The standing is a summary of a summary; failing to print it must not fail the audit.
        }
    }

    private static void PrintRanked(DoctorReport report, int limit, bool detail)
    {
        if (report.Ranked.Count == 0)
        {
            Console.WriteLine("No document has a text layer that misses what is drawn on its pages.");
            return;
        }

        PrintTable(
            "Drawn rather than typeset — the content never had a text layer",
            report.ToRepair, limit, detail);
        PrintTable(
            "Otherwise sound, with figure pages whose labels did not extract",
            report.WithFigures, Math.Min(limit, 10), detail: false);
        PrintTable(
            "Scanned, and their existing OCR missed lettering — `repair --include-scans` does these",
            report.WithScanGaps, Math.Min(limit, 10), detail: false);
    }

    private static void PrintTable(
        string heading, IReadOnlyList<DocumentAudit> ranked, int limit, bool detail)
    {
        if (ranked.Count == 0)
            return;

        Console.WriteLine(heading);
        Console.WriteLine(
            $"  {"Document",-44}{"Pages",7}{"Flagged",9}{"Drawn",8}{"Chars",9}  Pages flagged");
        Console.WriteLine("  " + new string('-', 110));

        foreach (var audit in ranked.Take(limit))
        {
            var ranges = audit.FlaggedPageRanges();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Shorten(audit.Title, 43),-44}{audit.PageCount,7:N0}{audit.FlaggedPageCount,9:N0}" +
                $"{audit.DrawnPageCount,8:N0}{audit.EstimatedRecoverableCharacters,9:N0}  " +
                $"{Shorten(ranges, 28)}"));

            if (!detail)
                continue;

            foreach (var page in audit.Flagged)
                Console.WriteLine($"      {page.Describe()}");
            Console.WriteLine($"      suggested OCR resolution {audit.SuggestedOcrDpi} dpi");
        }

        if (ranked.Count > limit)
            Console.WriteLine($"  ... and {ranked.Count - limit:N0} more document(s)");

        Console.WriteLine();
    }

    private static async Task WriteJsonAsync(DoctorReport report, string path, CancellationToken cancellationToken)
    {
        var payload = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            documentsAudited = report.DocumentsAudited,
            pagesExamined = report.PagesExamined,
            pagesFlagged = report.PagesFlagged,
            drawnPagesFlagged = report.DrawnPagesFlagged,
            pagesWithNoTextLayer = report.PagesWithNoTextLayer,
            pagesUndecodable = report.PagesUndecodable,
            estimatedRecoverableCharacters = report.EstimatedRecoverableCharacters,
            documents = report.Ranked.Select(a => new
            {
                path = a.Path,
                title = a.Title,
                verdict = a.Verdict.ToString(),
                contentHash = a.ContentHash,
                pageCount = a.PageCount,
                flaggedPages = a.FlaggedPageCount,
                drawnPages = a.DrawnPageCount,
                rasterPages = a.RasterPageCount,
                estimatedRecoverableCharacters = a.EstimatedRecoverableCharacters,
                suggestedOcrDpi = a.SuggestedOcrDpi,
                pages = a.Flagged.Select(p => new
                {
                    page = p.PageNumber,
                    kind = p.Kind.ToString(),
                    charactersDecoded = p.CharactersDecoded,
                    pathPaintOperations = p.PathPaintOperations,
                    textShowOperations = p.TextShowOperations,
                    inkFraction = Round(p.Ink.InkFraction),
                    uncoveredInkFraction = Round(p.Ink.UncoveredInkFraction),
                    glyphLikeBlobs = p.Ink.GlyphLikeBlobs,
                    estimatedRecoverableCharacters = p.EstimatedRecoverableCharacters,
                    suggestedOcrDpi = p.SuggestedOcrDpi,
                    signals = p.Signals,
                }),
            }),
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
    }

    private static double Round(double value) => Math.Round(value, 6);

    private static string Shorten(string text, int width) =>
        text.Length <= width ? text : text[..(width - 3)] + "...";
}

/// <summary>
/// Recovers the text on the pages the audit flagged, and nothing else.
/// </summary>
internal static class RepairCommand
{
    public static async Task<int> RunAsync(
        CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var target = arguments.Positional(0)
            ?? throw new ArgumentException("Give the path to a folder of PDFs, or to one PDF.");

        var root = File.Exists(target) ? Path.GetDirectoryName(Path.GetFullPath(target))! : target;
        var storePath = arguments.Get("doctor-db") ?? DoctorStore.DefaultPathFor(root);

        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine(
                $"No audit at {storePath}. Run `manualforge doctor \"{target}\"` first — repair only ever " +
                "touches pages the audit flagged, so without one there is nothing to do.");
            return 1;
        }

        using var store = new DoctorStore(storePath);
        var summary = store.Summary();

        var options = new RepairOptions
        {
            Dpi = arguments.GetInt("dpi"),
            MaximumDpi = arguments.GetInt("max-dpi") ?? new RepairOptions().MaximumDpi,
            MinimumConfidence = arguments.GetDouble("min-confidence") ?? new RepairOptions().MinimumConfidence,
            Force = arguments.Has("redo"),
            Limit = arguments.GetInt("limit"),
            IncludeFigurePages = !arguments.Has("worst-only"),
            IncludeScannedPages = arguments.Has("include-scans"),
        };

        Console.WriteLine($"Library  : {root}");
        Console.WriteLine($"Findings : {storePath}");
        Console.WriteLine(
            $"Flagged  : {summary.FlaggedPages:N0} page(s) in {summary.DocumentsFlagged:N0} document(s), " +
            $"{summary.OutstandingPages:N0} still to do");
        Console.WriteLine(
            $"           {summary.DrawnPages:N0} of them are drawn on the page; the rest are lettering " +
            "inside an image");
        Console.WriteLine(options.IncludeScannedPages
            ? "Scope    : drawn pages and scanned ones — this will take hours on a corpus this size"
            : "Scope    : drawn pages only. --include-scans also redoes scanned pages whose OCR missed text");
        Console.WriteLine();

        if (summary.OutstandingPages == 0 && !options.Force)
        {
            Console.WriteLine("Every flagged page has already been recovered. --redo does them again.");
            return 0;
        }

        var engineOptions = new OcrEngineOptions
        {
            Accelerator = arguments.Accelerator(),
            ModelCachePath = arguments.Get("models") ?? new OcrEngineOptions().ModelCachePath,
            BatchSize = arguments.GetInt("batch") ?? 8,

            // Both off, deliberately. Deskew and despeckle exist for photographs of paper; these
            // pages are rendered from vector drawing instructions and are already straight and
            // already clean. Deskewing a straight page can only rotate it, and despeckling erodes
            // the 4 pt annotation on a syntax diagram, which is the text this whole exercise is
            // trying to recover.
            Deskew = false,
            Denoise = false,

            CpuThreads = arguments.GetInt("cpu-threads"),
        };

        Console.Write("Loading OCR models... ");
        await using var engine = new PaddleOcrEngine(engineOptions, loggerFactory.CreateLogger<PaddleOcrEngine>());
        Console.WriteLine($"ready on {engine.Runtime.ExecutionProvider}.");
        Console.WriteLine();
        Console.WriteLine(
            "Case and punctuation are preserved exactly as recognised: nothing here lower-cases, " +
            "spell-checks or normalises. BYTeorder stays BYTeorder.");
        Console.WriteLine();

        var repairer = new PageRepairer(engine, loggerFactory.CreateLogger<PageRepairer>());

        var last = string.Empty;
        var progress = new Progress<RepairProgress>(p =>
        {
            if (!string.Equals(last, p.Path, StringComparison.Ordinal))
            {
                last = p.Path;
                Console.WriteLine();
                Console.WriteLine($"  {Path.GetFileName(p.Path)}");
            }

            Console.Write($"\r    page {p.PageNumber,5}  {p.WordsRecovered,4} words recovered  " +
                          $"({p.PagesDone:N0}/{p.PagesTotal:N0})     ");
        });

        var report = await repairer.RepairAsync(store, options, progress, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"Repaired  : {report.PagesRepaired:N0} page(s) across {report.DocumentsRepaired:N0} document(s)");
        Console.WriteLine($"Recovered : {report.WordsRecovered:N0} words, {report.CharactersRecovered:N0} characters");
        Console.WriteLine($"Confidence: {report.MeanConfidence:P1} mean");

        if (report.PagesAlreadyDone > 0)
            Console.WriteLine($"Already   : {report.PagesAlreadyDone:N0} page(s) recovered by an earlier run");

        if (report.DocumentsStale > 0)
        {
            Console.WriteLine(
                $"Stale     : {report.DocumentsStale:N0} document(s) changed since the audit; " +
                "re-run `manualforge doctor` over them");
        }

        if (report.PagesFailed > 0)
            Console.WriteLine($"Failed    : {report.PagesFailed:N0} page(s)");

        Console.WriteLine($"Took      : {report.Elapsed.TotalMinutes:F1} min ({report.PagesPerMinute:N0} pages/min)");
        Console.WriteLine();
        Console.WriteLine("No PDF was written to. The recovered text is in the audit database, and reaches");
        Console.WriteLine($"search the next time the index is built:  manualforge index \"{root}\"");

        return report.PagesFailed > 0 ? 1 : 0;
    }
}
