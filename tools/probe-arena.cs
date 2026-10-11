#:package Microsoft.ML.OnnxRuntime.Gpu@1.30.0
#:package System.Diagnostics.PerformanceCounter@10.0.12
#:property TargetFramework=net10.0-windows
#:property ManagePackageVersionsCentrally=false
// #34 (docs/measurements/gpu-autotuning.md): does ONNX Runtime's CUDA arena strategy decide whether three pages in flight spill?
// Detector at each real page's size, then recogniser batches of varying width, on N threads sharing
// one session each, as PaddleOcrNet does. Samples this process's dedicated and shared GPU memory.
// usage: dotnet run tools/probe-arena.cs -- <modelsDir> <pngDir or page-sizes.tsv> <strategy kNextPowerOfTwo|kSameAsRequested> <memLimitMiB or 0> [threads=3] [pages=200]
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

string dir = args[0], pngDir = args[1], strategy = args[2];
long limitMiB = long.Parse(args[3]);
int threads = args.Length > 4 ? int.Parse(args[4]) : 3, pages = args.Length > 5 ? int.Parse(args[5]) : 200;

// Real page sizes, from the PNG headers, rounded to the detector's multiple of 32 and capped at 4000.
var sizes = pngDir.EndsWith(".tsv") ? File.ReadAllLines(pngDir).Select(l => l.Split('	')).Where(f => f.Length == 4).Select(f =>
{
    int w = int.Parse(f[2]), ht = int.Parse(f[3]);
    double scale = Math.Min(1.0, 4000.0 / Math.Max(w, ht));
    return (H: Math.Max(32, (int)Math.Round(ht * scale / 32) * 32), W: Math.Max(32, (int)Math.Round(w * scale / 32) * 32));
}).ToArray() : Directory.GetFiles(pngDir, "*.png").Order().Select(f =>
{
    using var s = File.OpenRead(f); var h = new byte[24]; s.ReadExactly(h);
    int w = (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19], ht = (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23];
    double scale = Math.Min(1.0, 4000.0 / Math.Max(w, ht));
    return (H: Math.Max(32, (int)Math.Round(ht * scale / 32) * 32), W: Math.Max(32, (int)Math.Round(w * scale / 32) * 32));
}).ToArray();

InferenceSession Open(string file)
{
    using var cuda = new OrtCUDAProviderOptions();
    var o = new Dictionary<string, string> { ["device_id"] = "0", ["arena_extend_strategy"] = strategy };
    if (limitMiB > 0) o["gpu_mem_limit"] = (limitMiB * 1024 * 1024).ToString();
    cuda.UpdateOptions(o);
    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    so.AppendExecutionProvider_CUDA(cuda);
    return new InferenceSession(Path.Combine(dir, file), so);
}

using var det = Open("PP-OCRv5_mobile_det.onnx");
using var rec = Open("en_PP-OCRv5_mobile_rec_infer.onnx");
using var cls = Open("PP-LCNet_x1_0_textline_ori.onnx");
string detIn = det.InputMetadata.Keys.First(), recIn = rec.InputMetadata.Keys.First(), clsIn = cls.InputMetadata.Keys.First();

static NamedOnnxValue Input(string name, int[] shape, Random rng)
{
    var data = new float[shape.Aggregate(1, (a, b) => a * b)];
    for (var k = 0; k < data.Length; k += 97) data[k] = (float)rng.NextDouble();
    return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, shape));
}

int pid = Environment.ProcessId;
long Counter(string name)
{
    long sum = 0;
    var cat = new PerformanceCounterCategory("GPU Process Memory");
    foreach (var i in cat.GetInstanceNames().Where(n => n.StartsWith($"pid_{pid}_", StringComparison.Ordinal)))
        using (var c = new PerformanceCounter("GPU Process Memory", name, i, true)) sum += c.RawValue;
    return sum / (1024 * 1024);
}

long peakDed = 0, peakShared = 0, baseShared = Counter("Shared Usage");
var samples = new List<string>();
var done = false;
var clock = Stopwatch.StartNew();
var sampler = Task.Run(() =>
{
    while (!Volatile.Read(ref done))
    {
        long d = Counter("Dedicated Usage"), s = Counter("Shared Usage");
        peakDed = Math.Max(peakDed, d); peakShared = Math.Max(peakShared, s);
        lock (samples) samples.Add($"{clock.Elapsed.TotalSeconds:F1},{d},{s}");
        Thread.Sleep(500);
    }
});

var next = -1;
var perPage = new System.Collections.Concurrent.ConcurrentBag<double>();
Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
{
    var rng = new Random(34 + t);
    int p;
    while ((p = Interlocked.Increment(ref next)) < pages)
    {
        var sw = Stopwatch.StartNew();
        var (h, w) = sizes[p % sizes.Length];
        using (det.Run([Input(detIn, [1, 3, h, w], rng)])) { }
        // A page's lines, in batches of 8 padded to the batch's widest, as PaddleOcrNet does.
        for (var b = 0; b < 6; b++)
        {
            using (cls.Run([Input(clsIn, [6, 3, 80, 160], rng)])) { }
            using (rec.Run([Input(recIn, [8, 3, 48, 48 * rng.Next(2, 30)], rng)])) { }
        }
        perPage.Add(sw.Elapsed.TotalMilliseconds);
    }
});
Volatile.Write(ref done, true);
sampler.Wait();
var secs = clock.Elapsed.TotalSeconds;
Console.WriteLine($"{strategy} limit {limitMiB} MiB, {threads} threads: {pages} pages in {secs:F1} s ({pages / secs * 60:F1} pages/min); peak dedicated {peakDed:N0} MiB, shared {baseShared:N0} -> {peakShared:N0} MiB");
File.WriteAllLines($"arena-{Path.GetFileNameWithoutExtension(pngDir)}-{strategy}-{limitMiB}-{threads}-{DateTime.Now:HHmmss}.csv", ["seconds,dedicatedMiB,sharedMiB", .. samples]);
