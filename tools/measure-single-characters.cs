#:project ../src/ManualForge.Core/ManualForge.Core.csproj
#:property TargetFramework=net10.0-windows
#:property RuntimeIdentifier=win-x64
#:property Nullable=enable

// Single-character measurement for #22: our OCR under a named setting against Acrobat's text layer,
// word by word at the same position. Configs: detector settings, tall-box splitting, orphan-ink rescue.
// Rescue tuning via RESCUE_PAD, RESCUE_CROPPAD, RESCUE_MINW, RESCUE_GAP, RESCUE_ROWTEST, RESCUE_COLTEST;
// RESCUE_DEBUG=x0,x1,y0,y1 (points) prints candidates and rejections in that window.
// usage: dotnet run sweep.cs -- <test.pdf> <acrobat.pdf> <outdir> <pages e.g. 1-100> <config> [config...]
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ManualForge.Core.Ocr;
using ManualForge.Core.Rendering;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using UglyToad.PdfPig;

var inv = CultureInfo.InvariantCulture;
string testPdf = args[0], acroPdf = args[1], outDir = args[2];
var range = args[3].Split('-').Select(int.Parse).ToArray();
var pages = Enumerable.Range(range[0], range[^1] - range[0] + 1).ToList();
var configs = args.Skip(4).ToList();
const int Dpi = 300;
Directory.CreateDirectory(Path.Combine(outDir, "png"));

// ---- Content-token rule (recorded, so later rounds can compare) ----
// strip leading/trailing chars in  .,;:()[]{}'"*  then content iff ^[A-Za-z0-9][A-Za-z0-9\-./]*$
// single iff the stripped token is exactly one letter or digit.
var contentRx = new Regex(@"^[A-Za-z0-9][A-Za-z0-9\-./]*$");
static string Strip(string s) => s.Trim('.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '\'', '"', '*');
static string Fold(string s) => s.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');

// ---- Acrobat words, top-left origin, points ----
var acro = new Dictionary<int, List<W>>();
using (var doc = PdfDocument.Open(acroPdf))
{
    foreach (var p in pages)
    {
        var page = doc.GetPage(p);
        var crop = page.CropBox.Bounds;
        if (page.Rotation.Value != 0) Console.Error.WriteLine($"page {p} rotated {page.Rotation.Value}");
        acro[p] = page.GetWords().Select(w => new W(Strip(w.Text),
            w.BoundingBox.Left - crop.Left, crop.Top - w.BoundingBox.Top,
            w.BoundingBox.Right - crop.Left, crop.Top - w.BoundingBox.Bottom))
            .Where(w => contentRx.IsMatch(w.T)).ToList();
    }
}

// ---- Render once ----
var testBytes = File.ReadAllBytes(testPdf);
var rasteriser = new PageRasteriser(new RasterOptions { Dpi = Dpi });
foreach (var p in pages)
{
    var png = Path.Combine(outDir, "png", $"p{p:D3}.png");
    if (File.Exists(png)) continue;
    using var r = rasteriser.Render(testBytes, p - 1);
    File.WriteAllBytes(png, r.EncodePng());
}

// "file:NAME" re-scores a saved ours-NAME.tsv without recognising anything: no GPU. Anything else
// needs the models loaded.
bool needsGpu = configs.Any(c => !c.StartsWith("file:"));
if (needsGpu) CudaLibraries.Ensure();
await using var service = needsGpu ? new PaddleOcrService(new PaddleOcrServiceOptions
{
    ModelCachePath = new OcrEngineOptions().ModelCachePath,
    ExecutionProvider = OcrExecutionProvider.Cuda,
    UseGpu = true,
    Download = new ModelDownloadOptions { Offline = true },
    MaxImagePixels = 120_000_000,
}, logger: null) : null;

var summary = new StringBuilder();
summary.AppendLine("config\tacro_tokens\thit\tnot_hit_under\tmissed\tsingles\ts_hit\ts_stack\ts_merged\ts_misread\ts_missed\ts_present\tours_tokens\tours_extra\tseconds");

foreach (var config in configs)
{
    var ours = new Dictionary<int, List<W>>();
    const double k = 72.0 / Dpi;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (config.StartsWith("file:"))
    {
        foreach (var p in pages) ours[p] = [];
        foreach (var f in File.ReadLines(Path.Combine(outDir, $"ours-{config[5..]}.tsv")).Select(l => l.Split('\t')))
            if (ours.TryGetValue(int.Parse(f[0]), out var list))
                list.Add(new W(f[5], double.Parse(f[1], inv), double.Parse(f[2], inv), double.Parse(f[3], inv), double.Parse(f[4], inv)));
    }
    else if (config is "engine" or "engine-off" or "engine-rescue" or "engine-deskew" or "engine-keep80" or "engine-nodenoise")
    {
        // The shipped engine, production settings throughout: as shipped (engine), with the rescue
        // and split off (engine-off), rescue without split (engine-rescue), or with the old deskew
        // default back on (engine-deskew). Before 28 Sep deskew was on in all of these.
        // ENGINE_LOG=<path> writes the engine's debug log there, each page headed by a PAGE line.
        Serilog.ILogger? serilog = Environment.GetEnvironmentVariable("ENGINE_LOG") is { } logPath
            ? Serilog.FileLoggerConfigurationExtensions.File(new Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo, logPath).CreateLogger()
            : null;
        using var logFactory = serilog is null ? null : new Serilog.Extensions.Logging.SerilogLoggerFactory(serilog);
        await using var engine = new PaddleOcrEngine(new OcrEngineOptions
        {
            Accelerator = OcrAccelerator.Cuda, OfflineModels = true,
            RescueOrphanGlyphs = config != "engine-off", SplitTallStacks = config is not ("engine-off" or "engine-rescue"),
            Deskew = config == "engine-deskew",
            Denoise = config != "engine-nodenoise",
            OrphanConfidence = config == "engine-keep80" ? 0.8 : OrphanGlyphs.MinimumConfidence,
        }, logFactory is null ? null : Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<PaddleOcrEngine>(logFactory));
        foreach (var p in pages)
        {
            serilog?.Information("PAGE {Page}", p);
            var png = File.ReadAllBytes(Path.Combine(outDir, "png", $"p{p:D3}.png"));
            var page = await engine.RecognisePageAsync(png, p);
            if (Environment.GetEnvironmentVariable("STACK_DIAG") == "1" && OrphanGlyphs.LineHeight(page.Lines) is { } lh)
            {
                // Which stack-shaped words can be cut, and why the rest cannot.
                using var img = EasyImageSharp.Image.Load<EasyImageSharp.PixelFormats.Rgb24>(png);
                int iw = img.Width, ih = img.Height;
                var inkMap = new bool[iw * ih];
                img.ProcessPixelRows(acc => { for (int y = 0; y < ih; y++) { var s = acc.GetRowSpan(y); for (int x = 0; x < iw; x++) inkMap[y * iw + x] = s[x].R < 128; } });
                foreach (var w in page.Lines.SelectMany(l => l.Words))
                {
                    var b = w.BoxPx;
                    bool tallNarrow = b.Height > 2 * lh && b.Height > b.Width;
                    if (!tallNarrow) continue;
                    var rows = TallStacks.Rows(inkMap, iw, ih, b, lh);
                    Console.WriteLine($"STACK\t{p}\t{b.Left * k:F1}\t{b.Top * k:F1}\t{b.Width * k:F1}\t{b.Height * k:F1}\t{b.Height / lh:F1}\t{TallStacks.IsStack(b, lh)}\t{rows.Count}\t{w.Text}");
                }
            }
            ours[p] = page.Lines.SelectMany(l => l.Words)
                .Select(w => new W(Strip(w.Text), w.BoxPx.Left * k, w.BoxPx.Top * k, w.BoxPx.Right * k, w.BoxPx.Bottom * k))
                .Where(w => contentRx.IsMatch(w.T)).ToList();
        }
    }
    else
    {
        var options = Options(config);
        var ocr = service!;
        foreach (var p in pages)
        {
            var bytes = File.ReadAllBytes(Path.Combine(outDir, "png", $"p{p:D3}.png"));
            var lines = config is "regions" or "split" or "splitpad"
                ? await ViaRegions(ocr, bytes, config)
                : (await ocr.ExtractTextFromImage(bytes, [OcrLanguage.English], options)).Lines;
            if (config.StartsWith("rescue"))
                lines = [.. lines, .. await RescueOrphans(ocr, bytes, lines, config == "rescue7" ? 0.7 : 0.9)];
            ours[p] = lines.SelectMany(l => l.Words is { Count: > 0 } ? l.Words.Select(w => (w.Text, w.BoundingBox)) : [(l.Text, l.BoundingBox)])
                .Select(w => new W(Strip(w.Text), w.BoundingBox.MinX * k, w.BoundingBox.MinY * k, w.BoundingBox.MaxX * k, w.BoundingBox.MaxY * k))
                .Where(w => contentRx.IsMatch(w.T)).ToList();
        }
    }
    sw.Stop();

    var tag = config.Replace(':', '_');
    if (!config.StartsWith("file:"))
        using (var tsv = new StreamWriter(Path.Combine(outDir, $"ours-{tag}.tsv")))
            foreach (var (p, ws) in ours) foreach (var w in ws)
                tsv.WriteLine(string.Join('\t', p, w.X0.ToString("F1", inv), w.Y0.ToString("F1", inv), w.X1.ToString("F1", inv), w.Y1.ToString("F1", inv), w.T));

    int all = 0, hit = 0, mis = 0, miss = 0, s = 0, sHit = 0, sMis = 0, sMiss = 0, sStack = 0, sMerged = 0, sPresent = 0, oursAll = 0, extra = 0;
    using var detail = new StreamWriter(Path.Combine(outDir, $"singles-{tag}.tsv"));
    // Every Acrobat token, not only singles, with what lies under it: what a breakdown of the
    // multi-character misses (splits, misreads, Acrobat's own errors) is made from.
    using var tokens = new StreamWriter(Path.Combine(outDir, $"tokens-{tag}.tsv"));
    using var calibration = new StreamWriter(Path.Combine(outDir, $"calibration-{tag}.tsv"));
    foreach (var p in pages)
    {
        var (dx, dy) = Calibrate(acro[p], ours[p]);
        calibration.WriteLine($"{p}\t{dx.ToString("F2", inv)}\t{dy.ToString("F2", inv)}");
        var used = new HashSet<W>();
        foreach (var a in acro[p])
        {
            double cx = (a.X0 + a.X1) / 2 + dx, cy = (a.Y0 + a.Y1) / 2 + dy;
            var under = ours[p].Where(o => cx >= o.X0 - 1.5 && cx <= o.X1 + 1.5 && cy >= o.Y0 - 1.5 && cy <= o.Y1 + 1.5).ToList();
            var single = a.T.Length == 1;
            var match = under.FirstOrDefault(o => single ? Fold(o.T) == Fold(a.T) : Fold(o.T).Contains(Fold(a.T)));
            foreach (var o in under) used.Add(o);
            all++; if (single) s++;
            // For singles: a stack is a box more than 12 pt tall (table text is ~4.5 pt), i.e. a
            // column read as one word; a merge is a normal-height word that contains the character.
            string verdict = match is not null ? "hit"
                : under.Count == 0 ? "missed"
                : single && under.Any(o => o.Y1 - o.Y0 > 12) ? "stack"
                : single && under.Any(o => Fold(o.T).Contains(Fold(a.T))) ? "merged"
                : "misread";
            switch (verdict)
            {
                case "hit": hit++; if (single) sHit++; break;
                case "missed": miss++; if (single) sMiss++; break;
                case "stack": mis++; sStack++; break;
                case "merged": mis++; sMerged++; break;
                default: mis++; if (single) sMis++; break;
            }
            // Presence: we put a token of one or two characters at this spot, whatever it reads.
            // Acrobat is itself wrong on many singles (1 for 7, e for 8), so agreement undercounts.
            if (single && (verdict == "hit" || under.Any(o => o.T.Length <= 2))) sPresent++;
            if (single)
                detail.WriteLine($"{p}\t{a.X0.ToString("F1", inv)}\t{a.Y0.ToString("F1", inv)}\t{a.T}\t{verdict}\t{string.Join(' ', under.Select(o => o.T))}");

            // Ours on the same line across the token's whole width, left to right: a token we split
            // in two shows up here as its pieces.
            var across = ours[p]
                .Where(o => o.X1 >= a.X0 + dx - 1.5 && o.X0 <= a.X1 + dx + 1.5 && cy >= o.Y0 - 1.5 && cy <= o.Y1 + 1.5)
                .OrderBy(o => o.X0).Select(o => o.T);
            tokens.WriteLine($"{p}\t{a.T}\t{verdict}\t{string.Join(' ', under.Select(o => o.T))}\t{string.Join(' ', across)}\t" +
                             $"{(a.X0 + dx).ToString("F1", inv)}\t{(a.Y0 + dy).ToString("F1", inv)}\t{(a.X1 + dx).ToString("F1", inv)}\t{(a.Y1 + dy).ToString("F1", inv)}");
        }
        oursAll += ours[p].Count;
        extra += ours[p].Count(o => !used.Contains(o));
    }
    summary.AppendLine($"{config}\t{all}\t{hit}\t{mis}\t{miss}\t{s}\t{sHit}\t{sStack}\t{sMerged}\t{sMis}\t{sMiss}\t{sPresent}\t{oursAll}\t{extra}\t{sw.Elapsed.TotalSeconds:F0}");
    Console.WriteLine(summary.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
}
File.AppendAllText(Path.Combine(outDir, "summary.tsv"), summary.ToString());

// Median shift of ours relative to Acrobat, from words (4+ chars) unique on the page in both layers.
static (double, double) Calibrate(List<W> a, List<W> o)
{
    var ua = a.Where(w => w.T.Length >= 4).GroupBy(w => w.T.ToUpperInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
    var uo = o.Where(w => w.T.Length >= 4).GroupBy(w => w.T.ToUpperInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
    var pairs = ua.Keys.Intersect(uo.Keys).Select(k => (dx: (uo[k].X0 + uo[k].X1 - ua[k].X0 - ua[k].X1) / 2, dy: (uo[k].Y0 + uo[k].Y1 - ua[k].Y0 - ua[k].Y1) / 2)).ToList();
    if (pairs.Count < 3) return (0, 0);
    static double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    return (Median(pairs.Select(q => q.dx)), Median(pairs.Select(q => q.dy)));
}

// Detect, then recognise caller-supplied regions. "regions" passes the detector's boxes through
// unchanged (the control); "split" cuts every tall, narrow box at the blank rows of its own ink
// profile, so a column of check digits is read one character per crop.
static async Task<List<OcrLine>> ViaRegions(PaddleOcrService service, byte[] png, string config)
{
    using var image = EasyImageSharp.Image.Load<EasyImageSharp.PixelFormats.Rgb24>(png);
    var plain = new RecognitionOptions
    {
        Grouping = TextGrouping.Line, ReturnWordBoxes = true, BatchSize = 8, DropScore = 0.30, UseDocOrientation = false,
    };
    var regions = (await service.DetectRegionsAsync(image, plain)).ToList();
    var polygons = new List<IReadOnlyList<OcrPoint>>();
    var pieces = new List<IReadOnlyList<OcrPoint>>();

    var heights = regions.Select(r => r.BoundingBox.MaxY - r.BoundingBox.MinY)
        .Where((h, i) => regions[i].BoundingBox.MaxX - regions[i].BoundingBox.MinX > h).OrderBy(h => h).ToList();
    double row = heights.Count > 0 ? heights[heights.Count / 2] : 20;

    foreach (var r in regions)
    {
        var b = r.BoundingBox;
        double w = b.MaxX - b.MinX, h = b.MaxY - b.MinY;
        if (config == "regions" || h < 2.5 * row || h < w)
        {
            polygons.Add(r.BoundingPolygon);
            continue;
        }
        int x0 = Math.Max(0, (int)b.MinX), x1 = Math.Min(image.Width - 1, (int)b.MaxX);
        int y0 = Math.Max(0, (int)b.MinY), y1 = Math.Min(image.Height - 1, (int)b.MaxY);
        var ink = new bool[y1 - y0 + 1];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1 && !ink[y - y0]; x++)
                if (image[x, y].R < 128) ink[y - y0] = true;
        // runs of inked rows; gaps of a single blank row are bridged
        var runs = new List<(int a, int b)>();
        for (int y = 0; y < ink.Length; y++)
        {
            if (!ink[y]) continue;
            if (runs.Count > 0 && y - runs[^1].b <= 2) runs[^1] = (runs[^1].a, y);
            else runs.Add((y, y));
        }
        foreach (var (ra, rb) in runs.Where(q => q.b - q.a >= 3))
        {
            // a run still taller than two rows had no gap to cut at; share it out evenly
            int n = Math.Max(1, (int)Math.Round((rb - ra + 1) / row));
            double step = (rb - ra + 1) / (double)n;
            for (int i = 0; i < n; i++)
            {
                double top = y0 + ra + i * step - 3, bottom = y0 + ra + (i + 1) * step + 3;
                pieces.Add([new OcrPoint(x0 - 2, top), new OcrPoint(x1 + 2, top), new OcrPoint(x1 + 2, bottom), new OcrPoint(x0 - 2, bottom)]);
            }
        }
    }

    var lines = new List<OcrLine>();
    if (polygons.Count > 0)
        lines.AddRange((await service.RecognizeRegionsAsync(image, polygons, [OcrLanguage.English], plain)).Lines);
    if (pieces.Count > 0)
        lines.AddRange((await service.RecognizeRegionsAsync(image, pieces, [OcrLanguage.English],
            config == "splitpad" ? plain with { CropPadding = 8 } : plain)).Lines);
    return lines;
}

// Ink the detector never boxed: connected components standing clear of every detected word, kept
// when shaped like a character (or a short run of them) at this page's line height, read from a
// tight crop, and kept only when the read is confident and alphanumeric.
static async Task<List<OcrLine>> RescueOrphans(PaddleOcrService service, byte[] png, IReadOnlyList<OcrLine> found, double minConfidence)
{
    using var image = EasyImageSharp.Image.Load<EasyImageSharp.PixelFormats.Rgb24>(png);
    int W = image.Width, H = image.Height;
    var heights = found.Select(l => l.BoundingBox.MaxY - l.BoundingBox.MinY)
        .Where((h, i) => found[i].BoundingBox.MaxX - found[i].BoundingBox.MinX > 2 * h).OrderBy(h => h).ToList();
    if (heights.Count < 5) return [];
    double row = heights[heights.Count / 2];

    var ink = new bool[W * H];
    image.ProcessPixelRows(acc =>
    {
        for (int y = 0; y < H; y++)
        {
            var span = acc.GetRowSpan(y);
            for (int x = 0; x < W; x++) ink[y * W + x] = span[x].R < 128;
        }
    });
    // Words, not lines: a line box spans the whole table row, orphan cells included. Word boxes
    // come from CTC timesteps and run narrower than the ink, so a glyph is an orphan only when it
    // stands clear of every word by a gap - otherwise it is a word's own first or last letter.
    // The gap scales with the larger of the page's line height and the word's own, so big lettering
    // does not shed its first letter as an orphan.
    double gap = double.Parse(Environment.GetEnvironmentVariable("RESCUE_GAP") ?? "0.4");
    var allBoxes = found.SelectMany(l => l.Words is { Count: > 0 } ? l.Words.Select(w => w.BoundingBox) : [l.BoundingBox]).ToList();
    // Some word boxes come back a fraction of a point tall; they still block, but cannot vouch for a row.
    var wordBoxes = allBoxes.Where(b => b.MaxY - b.MinY > 0.3 * row).ToList();
    var words = allBoxes.Select(b =>
    {
        double h = Math.Max(row, b.MaxY - b.MinY), gx = gap * h, gy = 0.15 * h;
        return (x0: b.MinX - gx, y0: b.MinY - gy, x1: b.MaxX + gx, y1: b.MaxY + gy);
    }).ToList();
    bool NearWord(int x0, int y0, int x1, int y1) =>
        words.Any(w => x1 >= w.x0 && x0 <= w.x1 && y1 >= w.y0 && y0 <= w.y1);
    // A real orphan shares a text row with detected words: its middle lies inside a word of about
    // its size, somewhere along the same row. A stroke of a drawing usually has no such neighbour.
    bool rowTest = Environment.GetEnvironmentVariable("RESCUE_ROWTEST") != "0";
    bool OnTextRow((int x0, int y0, int x1, int y1) c)
    {
        double cy = (c.y0 + c.y1) / 2.0, ink = c.y1 - c.y0 + 1;
        return wordBoxes.Any(b => cy > b.MinY && cy < b.MaxY
            && b.MaxY - b.MinY >= 0.8 * ink && b.MaxY - b.MinY <= 2.2 * ink
            && Math.Min(Math.Abs(b.MinX - c.x1), Math.Abs(c.x0 - b.MaxX)) < 15 * row);
    }

    var debugWindow = Environment.GetEnvironmentVariable("RESCUE_DEBUG")?.Split(',').Select(double.Parse).ToArray();
    // 8-connected components
    var boxes = new List<(int x0, int y0, int x1, int y1)>();
    var stack = new Stack<int>();
    for (int i = 0; i < ink.Length; i++)
    {
        if (!ink[i]) continue;
        int x0 = W, y0 = H, x1 = 0, y1 = 0;
        ink[i] = false; stack.Push(i);
        while (stack.Count > 0)
        {
            int j = stack.Pop(), x = j % W, y = j / W;
            if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int n = ny * W + nx;
                    if (ink[n]) { ink[n] = false; stack.Push(n); }
                }
        }
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        // a glyph: inked height between 0.4 and 1.2 rows, no wider than a row and a half
        bool glyph = bh >= 0.4 * row && bh <= 1.2 * row && bw <= 1.5 * row, near = glyph && NearWord(x0, y0, x1, y1);
        if (glyph && !near)
            boxes.Add((x0, y0, x1, y1));
        else if (debugWindow is { } dw && x0 * 0.24 < dw[1] && x1 * 0.24 > dw[0] && y0 * 0.24 < dw[3] && y1 * 0.24 > dw[2] && bh > 0.2 * row)
            Console.WriteLine($"  reject x {x0 * 0.24:F1}-{x1 * 0.24:F1} y {y0 * 0.24:F1}-{y1 * 0.24:F1} ({bw}x{bh}px, row {row:F0}px): {(glyph ? "near a word" : "shape")}");
    }

    // join glyphs sitting side by side on one baseline into one candidate
    boxes.Sort((a, b) => a.x0.CompareTo(b.x0));
    var merged = new List<(int x0, int y0, int x1, int y1)>();
    foreach (var b in boxes)
    {
        int k = merged.FindIndex(m => b.x0 - m.x1 <= 0.35 * row && b.x0 >= m.x0
            && Math.Min(m.y1, b.y1) - Math.Max(m.y0, b.y0) > 0.5 * Math.Min(m.y1 - m.y0, b.y1 - b.y0));
        if (k < 0) merged.Add(b);
        else merged[k] = (merged[k].x0, Math.Min(merged[k].y0, b.y0), Math.Max(merged[k].x1, b.x1), Math.Max(merged[k].y1, b.y1));
    }
    if (rowTest) merged = merged.Where(OnTextRow).ToList();
    // Table orphans come in columns - check digits, quantities - while strokes of a drawing are
    // scattered. Keep a candidate only when at least two others share its column.
    if (Environment.GetEnvironmentVariable("RESCUE_COLTEST") != "0")
    {
        var kept = merged;
        merged = kept.Where(c => kept.Count(o => !o.Equals(c)
            && Math.Abs((o.x0 + o.x1) - (c.x0 + c.x1)) / 2.0 < 0.5 * row) >= 2).ToList();
    }
    if (merged.Count == 0) return [];

    double pad = double.Parse(Environment.GetEnvironmentVariable("RESCUE_PAD") ?? "0.1") * row;
    // A lone 1 is a crop a few pixels wide, which the recogniser reads as 4. Widen narrow crops
    // sideways only: padding on all sides shrinks the glyph once the crop is scaled to line height.
    double minWidth = double.Parse(Environment.GetEnvironmentVariable("RESCUE_MINW") ?? "0.8") * row;
    var polygons = merged.Select(m =>
    {
        double side = Math.Max(pad, (minWidth - (m.x1 - m.x0)) / 2);
        return (IReadOnlyList<OcrPoint>)[
            new OcrPoint(m.x0 - side, m.y0 - pad), new OcrPoint(m.x1 + side, m.y0 - pad),
            new OcrPoint(m.x1 + side, m.y1 + pad), new OcrPoint(m.x0 - side, m.y1 + pad)];
    }).ToList();
    var options = new RecognitionOptions
    {
        Grouping = TextGrouping.Word, ReturnWordBoxes = true, DropScore = 0, UseDocOrientation = false,
        UseTextLineOrientation = false, CropPadding = int.Parse(Environment.GetEnvironmentVariable("RESCUE_CROPPAD") ?? "0"),
    };
    var read = await service.RecognizeRegionsAsync(image, polygons, [OcrLanguage.English], options);
    if (Environment.GetEnvironmentVariable("RESCUE_DEBUG") is { } window)
    {
        var w = window.Split(',').Select(double.Parse).ToArray(); // x0,x1,y0,y1 in points
        double k = 72.0 / 300;
        Console.WriteLine($"row {row * k:F1} pt; {boxes.Count} glyphs, {merged.Count} candidates, {read.Lines.Count} reads");
        foreach (var m in merged.Where(m => m.x0 * k < w[1] && m.x1 * k > w[0] && m.y0 * k < w[3] && m.y1 * k > w[2]).OrderBy(m => m.y0))
        {
            var l = read.Lines.FirstOrDefault(l => l.BoundingBox.MaxX > m.x0 && l.BoundingBox.MinX < m.x1 && l.BoundingBox.MaxY > m.y0 && l.BoundingBox.MinY < m.y1);
            Console.WriteLine($"  cand x {m.x0 * k:F1}-{m.x1 * k:F1} y {m.y0 * k:F1}-{m.y1 * k:F1} -> '{l?.Text}' {l?.Confidence:F2}");
        }
    }
    return read.Lines.Where(l => l.Confidence >= minConfidence && l.Text.Trim().Length > 0 && l.Text.Trim().All(char.IsLetterOrDigit)).ToList();
}

// Production settings (orientation classifier off: these pages are upright, and verification
// overruled it on 24 of 26), with one detector knob changed per config.
static RecognitionOptions Options(string config)
{
    var d = new DetectionOptions();
    d = config switch
    {
        "baseline" or "regions" or "split" or "splitpad" or "drop0" or "rescue" or "rescue7" => d,
        "det0.2" => d with { DetThreshold = 0.2 },
        "det0.15" => d with { DetThreshold = 0.15 },
        "box0.5" => d with { BoxThreshold = 0.5 },
        "box0.4" => d with { BoxThreshold = 0.4 },
        "box0.3" => d with { BoxThreshold = 0.3 },
        "unclip1.2" => d with { UnclipRatio = 1.2 },
        "unclip2.0" => d with { UnclipRatio = 2.0 },
        "dilate" => d with { UseDilation = true },
        "slow" => d with { ScoreMode = DetectionScoreMode.Slow },
        "minh32" => d with { MinTextHeight = 32 },
        "contrast" => d with { EnhanceContrast = true },
        _ => throw new ArgumentException(config),
    };
    return new RecognitionOptions
    {
        Grouping = TextGrouping.Line,
        ReturnWordBoxes = true,
        BatchSize = 8,
        DropScore = config == "drop0" ? 0.0 : 0.30,
        UseDocOrientation = false,
        Preprocessing = new PreprocessingOptions { Deskew = true, Denoise = true },
        Detection = d,
    };
}

record W(string T, double X0, double Y0, double X1, double Y1);
