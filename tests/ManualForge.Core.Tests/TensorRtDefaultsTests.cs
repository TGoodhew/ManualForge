using ManualForge.Core.Ocr;
using PaddleOcrNet.Services;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// TensorRT's documented defaults, pinned (#53). A default that changes silently changes what every
/// later run reads, and the reverse-video flag once went unnoticed for two ingests that way.
/// </summary>
public sealed class TensorRtDefaultsTests
{
    [Fact]
    public void TheDetectorStaysOnCudaByDefault()
    {
        // 151 pages a minute against 165 with the detector on TensorRT too, for 1.0% of words read
        // differently against 2.3%: Tony's choice, 10 Oct 2026.
        Assert.Equal(TensorRtModels.Classifier | TensorRtModels.Recognizer, new OcrEngineOptions().TensorRtModels);
    }

    [Fact]
    public void AutoIsTheDefaultAcceleratorSoTensorRtIsUsedWhereInstalled()
    {
        Assert.Equal(OcrAccelerator.Auto, new OcrEngineOptions().Accelerator);
    }

    [Theory]
    [InlineData(TensorRtModels.Classifier | TensorRtModels.Recognizer, "cls+rec")]
    [InlineData(TensorRtModels.All, "det+cls+rec")]
    [InlineData(TensorRtModels.Recognizer, "rec")]
    public void ThePageCacheNamesTheModelsOnTensorRt(TensorRtModels models, string expected)
    {
        // Each combination reads differently, so each must be its own entry in the page cache.
        Assert.Equal(expected, PaddleOcrEngine.TensorRtModelNames(models));
    }
}
