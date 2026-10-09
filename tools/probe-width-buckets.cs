#:package Microsoft.ML.OnnxRuntime.Gpu@1.30.0
#:property ManagePackageVersionsCentrally=false
// #32: would rounding batch widths to buckets, with a session per bucket, remove the per-change cost? GPU. docs/measurements/recognition-shape-changes.md
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Usage: bucketprobe <rec.onnx> [calls=400]
// Would rounding batch widths to a few sizes, with one session per size, remove the per-change cost?
var model = args[0];
var calls = args.Length > 1 ? int.Parse(args[1]) : 400;
int[] buckets = [320, 480, 640, 960, 1280, 1920, 2560, 3200];

InferenceSession Open()
{
    using var cuda = new OrtCUDAProviderOptions();
    cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0" });
    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    so.AppendExecutionProvider_CUDA(cuda);
    return new InferenceSession(model, so);
}

var rng = new Random(32);
// Real batch widths: at least 320 (PaddleOcrNet's floor), mostly short, a tail of long lines.
int RealWidth() => Math.Max(320, Math.Min(3200, (int)(48 * Math.Exp(rng.NextDouble() * 4.2))));
var widths = Enumerable.Range(0, calls).Select(_ => RealWidth()).ToArray();
// Most batches are full; about one in eight is the short last batch of a page.
var counts = Enumerable.Range(0, calls).Select(_ => rng.Next(8) == 0 ? rng.Next(1, 8) : 8).ToArray();
int Bucket(int w) => buckets.First(b => b >= w);

var cache = new Dictionary<(int, int), NamedOnnxValue>();
NamedOnnxValue Input(string name, int n, int w)
{
    if (cache.TryGetValue((n, w), out var v)) return v;
    var data = new float[n * 3 * 48 * w];
    for (var k = 0; k < data.Length; k += 97) data[k] = (float)rng.NextDouble();
    return cache[(n, w)] = NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, [n, 3, 48, w]));
}

void Measure(string label, Func<int, (InferenceSession Session, int N, int W)> plan, int threads)
{
    // Warm each session on each shape it will see, so first-use costs are not counted.
    var seen = new HashSet<(InferenceSession, int, int)>();
    for (var i = 0; i < calls; i++)
    {
        var (s, n, w) = plan(i);
        if (seen.Add((s, n, w))) { using var _ = s.Run([Input(s.InputMetadata.Keys.First(), n, w)]); }
    }
    var sw = Stopwatch.StartNew();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (var i = t; i < calls; i += threads)
        {
            var (s, n, w) = plan(i);
            using var r = s.Run([Input(s.InputMetadata.Keys.First(), n, w)]);
        }
    });
    var useful = Enumerable.Range(0, calls).Sum(i => counts[i] * widths[i]) / 1000.0;
    Console.WriteLine($"{label,-58} {sw.Elapsed.TotalMilliseconds / calls,6:F2} ms/call  ({threads} thr)");
}

using var one = Open();
var perBucket = buckets.ToDictionary(b => b, _ => Open());

foreach (var threads in new[] { 1, 3 })
{
    Measure("as now: one session, exact widths and counts", i => (one, counts[i], widths[i]), threads);
    Measure("one session, widths rounded to buckets", i => (one, counts[i], Bucket(widths[i])), threads);
    Measure("session per bucket, rounded widths, exact counts", i => (perBucket[Bucket(widths[i])], counts[i], Bucket(widths[i])), threads);
    Measure("session per bucket, rounded widths, batch padded to 8", i => (perBucket[Bucket(widths[i])], 8, Bucket(widths[i])), threads);
    Console.WriteLine();
}
var mean = widths.Average(); var meanB = widths.Select(Bucket).Average();
Console.WriteLine($"mean width {mean:F0}, rounded {meanB:F0} ({100 * (meanB / mean - 1):F0}% more pixels); distinct widths {widths.Distinct().Count()}");
foreach (var s in perBucket.Values) s.Dispose();
