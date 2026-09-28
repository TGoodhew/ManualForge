using ManualForge.Core.Ocr;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Deskew is off unless asked for. PaddleOcrNet 2.2.0 maps a straightened page's text back 3-5 pt
/// beside its ink, and with it off the same pages read slightly more.
/// docs/measurements/deskew-offset.md.
/// </summary>
public sealed class DeskewDefaultTests
{
    [Fact]
    public void DeskewIsOffByDefault()
    {
        // The documented default, pinned: a flag wired backwards once went unnoticed for two
        // whole ingests because nothing asserted which way round it was.
        Assert.False(new OcrEngineOptions().Deskew);
    }
}
