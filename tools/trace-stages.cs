#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.23
#:property ManagePackageVersionsCentrally=false
// #32: thread-time by pipeline stage from a dotnet-trace capture (dotnet-trace collect --profile dotnet-sampled-thread-time -- manualforge run ...). CPU only. docs/measurements/recognition-shape-changes.md
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

// Usage: dotnet run tools/trace-stages.cs -- <run.nettrace> [topN]
// Every SampleProfiler sample is one thread at one moment. Each is put in the first stage whose
// pattern appears in its stack (leaf first), so a thread inside InferenceSession.Run called from the
// detector counts as "det: Run", not as "det: cpu". Reported as thread-seconds.
var path = args[0];
var top = args.Length > 1 ? int.Parse(args[1]) : 40;
var etlx = TraceLog.CreateFromEventPipeDataFile(path);
using var log = new TraceLog(etlx);

string Classify(List<string> frames)
{
    bool Has(string s) => frames.Any(f => f.Contains(s, StringComparison.Ordinal));
    var leaf = frames.Count > 0 ? frames[0] : "";
    var inRun = Has("InferenceSession.Run") || Has("NativeMethods.OrtRun");
    if (inRun && Has("DbTextDetector")) return "det: ORT Run";
    if (inRun && Has("SvtrRecognizer")) return "rec: ORT Run";
    if (inRun && Has("TextLineClassifier")) return "cls: ORT Run";
    if (inRun && Has("DocPreprocessor")) return "doc-orient: ORT Run";
    if (inRun) return "other: ORT Run";
    if (Has("OrphanGlyphs") || Has("RescueOrphan")) return "ours: orphan glyphs";
    if (Has("TallStacks") || Has("SplitTallStacks")) return "ours: tall stacks";
    if (Has("DBPostProcess")) return "det: post-process (cpu)";
    if (Has("DbTextDetector")) return "det: pre-process (cpu)";
    if (Has("SvtrRecognizer") || Has("CtcDecoder") || Has("Recognition.")) return "rec: cpu (crop/pack/CTC)";
    if (Has("TextLineClassifier")) return "cls: cpu";
    if (Has("Denoise") || Has("Preprocess")) return "pre-process (denoise etc.)";
    if (Has("ImageSharp") && (Has("Decode") || Has("Load"))) return "image decode";
    if (Has("EncodePng") || Has("PngEncoder")) return "png encode";
    if (Has("PageRasteriser") || Has("PDFium") || Has("Pdfium")) return "rasterise";
    if (Has("Sqlite") || Has("PageOcrCache")) return "page cache";
    if (Has("TextLayer") || Has("PdfSharp") || Has("SearchablePdf")) return "write / verify pdf";
    if (Has("GC") && leaf.Contains("GC")) return "gc";
    if (Has("ExtractTextFromImage") || Has("PaddleOcr")) return "paddle: other";
    if (Has("LowLevelLifoSemaphore") || Has("WaitHandle") || Has("Monitor.Wait") || Has("SpinWait")
        || Has("Thread.Sleep") || Has("WaitForSignal") || Has("Channel") || Has("WorkerThreadStart")
        || Has("TimerQueue") || Has("WaitSubsystem") || Has("LowLevelMonitor")) return "idle / waiting";
    return "other";
}

var byStage = new Dictionary<string, int>();
var byThreadStage = new Dictionary<(int, string), int>();
var leafs = new Dictionary<string, int>();
var total = 0;
double first = double.MaxValue, last = 0;

foreach (var ev in log.Events)
{
    if (ev.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
    var stack = ev.CallStack();
    var frames = new List<string>();
    for (var s = stack; s != null; s = s.Caller)
        frames.Add($"{s.CodeAddress.ModuleName}!{s.CodeAddress.FullMethodName}");
    var stage = Classify(frames);
    byStage[stage] = byStage.GetValueOrDefault(stage) + 1;
    byThreadStage[(ev.ThreadID, stage)] = byThreadStage.GetValueOrDefault((ev.ThreadID, stage)) + 1;
    if (stage is "other" or "paddle: other")
    {
        var key = stage + "  " + string.Join(" <- ", frames.Take(4));
        leafs[key] = leafs.GetValueOrDefault(key) + 1;
    }
    total++;
    first = Math.Min(first, ev.TimeStampRelativeMSec);
    last = Math.Max(last, ev.TimeStampRelativeMSec);
}

var wall = (last - first) / 1000.0;
var threads = byThreadStage.Keys.Select(k => k.Item1).Distinct().Count();
// Samples are taken at a fixed interval per thread; express each stage as thread-seconds.
var perSample = wall * threads / Math.Max(1, total);
Console.WriteLine($"samples {total:N0}, wall {wall:F1} s, managed threads seen {threads}");
Console.WriteLine();
foreach (var (stage, n) in byStage.OrderByDescending(kv => kv.Value))
{
    var nThreads = byThreadStage.Where(kv => kv.Key.Item2 == stage).Count();
    Console.WriteLine($"{stage,-34} {n,8:N0} samples  {n * perSample,7:F1} thread-s  {100.0 * n / total,5:F1}%  on {nThreads} threads");
}

Console.WriteLine();
Console.WriteLine("Unclassified stacks, top:");
foreach (var (k, n) in leafs.OrderByDescending(kv => kv.Value).Take(top))
    Console.WriteLine($"{n,7:N0}  {k}");
