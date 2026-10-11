#:package Microsoft.ML.OnnxRuntime.Gpu@1.30.0
#:property TargetFramework=net10.0-windows
#:property ManagePackageVersionsCentrally=false
// #34: does ONNX Runtime's TensorRT provider avoid the per-shape cost cuDNN pays, and does it read the same?
// The recogniser through the CUDA provider and through TensorRT (dynamic-shape profile, FP32, engine
// cache), at fixed and varying widths, then the two outputs compared on identical inputs.
// usage (TensorRT 10 for CUDA 13 on PATH, e.g. C:/Tools/tensorrt-10.16.1.11/bin):
//   dotnet run tools/probe-tensorrt.cs -- <modelsDir> <engineCacheDir> [calls=200]
// docs/measurements/recognition-shape-changes.md
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

string dir = args[0], cache = args[1];
int calls = args.Length > 2 ? int.Parse(args[2]) : 200;
const string model = "en_PP-OCRv5_mobile_rec_infer.onnx";
Directory.CreateDirectory(cache);

InferenceSession Cuda()
{
    using var cuda = new OrtCUDAProviderOptions();
    cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0" });
    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    so.AppendExecutionProvider_CUDA(cuda);
    return new InferenceSession(Path.Combine(dir, model), so);
}

InferenceSession Trt(string input)
{
    using var trt = new OrtTensorRTProviderOptions();
    trt.UpdateOptions(new Dictionary<string, string>
    {
        ["device_id"] = "0",
        ["trt_fp16_enable"] = "0",
        ["trt_engine_cache_enable"] = "1",
        ["trt_engine_cache_path"] = cache,
        ["trt_timing_cache_enable"] = "1",
        ["trt_timing_cache_path"] = cache,
        ["trt_profile_min_shapes"] = $"{input}:1x3x48x48",
        ["trt_profile_opt_shapes"] = $"{input}:8x3x48x640",
        ["trt_profile_max_shapes"] = $"{input}:8x3x48x3200",
    });
    using var cuda = new OrtCUDAProviderOptions();
    cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0" });
    var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    so.AppendExecutionProvider_Tensorrt(trt);
    so.AppendExecutionProvider_CUDA(cuda);   // for anything TensorRT will not take
    return new InferenceSession(Path.Combine(dir, model), so);
}

var rng = new Random(34);
DenseTensor<float> Data(int[] shape)
{
    var data = new float[shape.Aggregate(1, (a, b) => a * b)];
    for (var k = 0; k < data.Length; k++) data[k] = (float)(rng.NextDouble() * 2 - 1);
    return new DenseTensor<float>(data, shape);
}

using var cudaSession = Cuda();
var input = cudaSession.InputMetadata.Keys.First();
var build = Stopwatch.StartNew();
using var trtSession = Trt(input);
var warm = Data([8, 3, 48, 640]);
using (trtSession.Run([NamedOnnxValue.CreateFromTensor(input, warm)])) { }
Console.WriteLine($"TensorRT session and first call: {build.Elapsed.TotalSeconds:F1} s (engine built, or loaded from {cache})");

int[] widths = Enumerable.Range(0, calls).Select(_ => 48 * rng.Next(2, 30)).ToArray();
var varied = widths.Select(w => Data([8, 3, 48, w])).ToArray();
var fixedInput = Data([8, 3, 48, 640]);

double Time(InferenceSession s, DenseTensor<float>[] inputs, int threads)
{
    var sw = Stopwatch.StartNew();
    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
    {
        for (var i = t; i < inputs.Length; i += threads)
            using (s.Run([NamedOnnxValue.CreateFromTensor(input, inputs[i])])) { }
    });
    return sw.Elapsed.TotalMilliseconds / inputs.Length;
}

foreach (var (name, s) in new[] { ("CUDA", cudaSession), ("TensorRT", trtSession) })
{
    Time(s, varied.Take(20).ToArray(), 1);   // warm
    Console.WriteLine($"{name,-9} fixed 640:  1 thread {Time(s, Enumerable.Repeat(fixedInput, calls).ToArray(), 1),6:F2} ms/call   3 threads {Time(s, Enumerable.Repeat(fixedInput, calls).ToArray(), 3),6:F2}");
    Console.WriteLine($"{name,-9} varying:    1 thread {Time(s, varied, 1),6:F2} ms/call   3 threads {Time(s, varied, 3),6:F2}");
    Console.WriteLine($"{name,-9} varying, second pass over the same widths: {Time(s, varied, 1),6:F2} ms/call");
}

// Same inputs, both providers: how far apart are the outputs, and do they pick the same character
// at every position? The recogniser's output is [batch, timesteps, classes]; CTC decoding takes the
// argmax at each timestep, so agreement there is agreement in what is read.
double maxDiff = 0; long positions = 0, agree = 0;
foreach (var x in varied.Take(60))
{
    using var a = cudaSession.Run([NamedOnnxValue.CreateFromTensor(input, x)]);
    using var b = trtSession.Run([NamedOnnxValue.CreateFromTensor(input, x)]);
    var ta = a.First().AsTensor<float>(); var tb = b.First().AsTensor<float>();
    var dims = ta.Dimensions.ToArray();
    int classes = dims[^1];
    var fa = ta.ToArray(); var fb = tb.ToArray();
    for (var k = 0; k < fa.Length; k++) maxDiff = Math.Max(maxDiff, Math.Abs(fa[k] - fb[k]));
    for (var p = 0; p < fa.Length / classes; p++)
    {
        int ia = 0, ib = 0;
        for (var c = 1; c < classes; c++)
        {
            if (fa[p * classes + c] > fa[p * classes + ia]) ia = c;
            if (fb[p * classes + c] > fb[p * classes + ib]) ib = c;
        }
        positions++; if (ia == ib) agree++;
    }
}
Console.WriteLine($"Outputs, 60 batches: largest difference {maxDiff:E2}; argmax agrees at {agree:N0} of {positions:N0} positions ({100.0 * agree / positions:F3}%)");
