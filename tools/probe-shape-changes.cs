#:package Microsoft.ML.OnnxRuntime.Gpu@1.30.0
#:property ManagePackageVersionsCentrally=false
// #32: what a change of recogniser input shape costs, whether switching models or another thread's shape changes slow a call, and whether the cost overlaps across threads. GPU. docs/measurements/recognition-shape-changes.md
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Usage: switchprobe <modelsDir> [calls=200] [search=HEURISTIC]
// Is a Run slow because its input shape changed, or because a different session ran in between?
var dir = args[0];
var calls = args.Length > 1 ? int.Parse(args[1]) : 200;
var search = args.Length > 2 ? args[2] : "HEURISTIC";

InferenceSession Open(string file)
{
    using var cuda = new OrtCUDAProviderOptions();
    cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0", ["cudnn_conv_algo_search"] = search });
    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    so.AppendExecutionProvider_CUDA(cuda);
    return new InferenceSession(Path.Combine(dir, file), so);
}

using var rec = Open("en_PP-OCRv5_mobile_rec_infer.onnx");
using var cls = Open("PP-LCNet_x1_0_textline_ori.onnx");
var recIn = rec.InputMetadata.Keys.First();
var clsIn = cls.InputMetadata.Keys.First();
var rng = new Random(32);

NamedOnnxValue Input(string name, int[] shape)
{
    var data = new float[shape.Aggregate(1, (a, b) => a * b)];
    for (var k = 0; k < data.Length; k += 97) data[k] = (float)rng.NextDouble();
    return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, shape));
}

double RunTimed(InferenceSession s, NamedOnnxValue v)
{
    var sw = Stopwatch.StartNew();
    using var r = s.Run([v]);
    return sw.Elapsed.TotalMilliseconds;
}

void Report(string label, List<double> t)
{
    t.Sort();
    Console.WriteLine($"{label,-44} mean {t.Average(),7:F2} ms  median {t[t.Count / 2],7:F2}  p95 {t[(int)(t.Count * 0.95)],7:F2}");
}

var clsInput = Input(clsIn, [6, 3, 80, 160]);
var recInput = Input(recIn, [8, 3, 48, 640]);

// Warm both.
for (var i = 0; i < 20; i++) { RunTimed(cls, clsInput); RunTimed(rec, recInput); }

var a = new List<double>(); for (var i = 0; i < calls; i++) a.Add(RunTimed(cls, clsInput));
Report("cls alone, fixed 6x80x160", a);
var b = new List<double>(); for (var i = 0; i < calls; i++) b.Add(RunTimed(rec, recInput));
Report("rec alone, fixed 8x48x640", b);

var c1 = new List<double>(); var c2 = new List<double>();
for (var i = 0; i < calls; i++) { c1.Add(RunTimed(cls, clsInput)); c2.Add(RunTimed(rec, recInput)); }
Report("cls, alternating with rec (both fixed)", c1);
Report("rec, alternating with cls (both fixed)", c2);

// Same, but from three threads at once, as three pages in flight do.
var d = new System.Collections.Concurrent.ConcurrentBag<double>();
var sw3 = Stopwatch.StartNew();
Parallel.For(0, 3, _ =>
{
    for (var i = 0; i < calls / 3; i++) { d.Add(RunTimed(cls, clsInput)); d.Add(RunTimed(rec, recInput)); }
});
Report("cls+rec alternating, 3 threads (per call)", d.ToList());
Console.WriteLine($"  3 threads: {2 * 3 * (calls / 3)} calls in {sw3.Elapsed.TotalMilliseconds:F0} ms wall");

// Varying widths, profiling off: what does a change of shape really cost?
int[] widths = Enumerable.Range(0, calls).Select(_ => 48 * rng.Next(2, 30)).ToArray();
var recVaried = widths.Select(w => Input(recIn, [8, 3, 48, w])).ToArray();
var e = new List<double>(); for (var i = 0; i < calls; i++) e.Add(RunTimed(rec, recVaried[i]));
Report("rec alone, varying widths", e);

// The stall test: cls on a fixed shape on one thread while another thread runs rec on varying widths.
var stop = false;
var clsDuring = new List<double>();
var recThread = Task.Run(() => { for (var i = 0; i < calls; i++) RunTimed(rec, recVaried[i]); stop = true; });
while (!Volatile.Read(ref stop)) clsDuring.Add(RunTimed(cls, clsInput));
recThread.Wait();
Report("cls fixed, while rec varies on another thread", clsDuring);

stop = false;
var clsDuringFixed = new List<double>();
recThread = Task.Run(() => { for (var i = 0; i < calls; i++) RunTimed(rec, recInput); stop = true; });
while (!Volatile.Read(ref stop)) clsDuringFixed.Add(RunTimed(cls, clsInput));
recThread.Wait();
Report("cls fixed, while rec fixed on another thread", clsDuringFixed);

// Does the per-change cost run in parallel across threads, or queue behind something shared?
foreach (var threads in new[] { 1, 3, 6 })
{
    var per = calls / threads * threads;
    var swT = Stopwatch.StartNew();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (var i = t; i < per; i += threads) RunTimed(rec, recVaried[i]);
    });
    Console.WriteLine($"rec varying widths, {threads} thread(s): {per} calls in {swT.Elapsed.TotalMilliseconds,6:F0} ms wall ({swT.Elapsed.TotalMilliseconds / per:F2} ms/call)");
}
foreach (var threads in new[] { 1, 3 })
{
    var per = calls / threads * threads;
    var swT = Stopwatch.StartNew();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (var i = t; i < per; i += threads) RunTimed(rec, recInput);
    });
    Console.WriteLine($"rec fixed width,     {threads} thread(s): {per} calls in {swT.Elapsed.TotalMilliseconds,6:F0} ms wall ({swT.Elapsed.TotalMilliseconds / per:F2} ms/call)");
}
