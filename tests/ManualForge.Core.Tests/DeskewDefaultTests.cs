using ManualForge.Core.Ocr;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// Deskew is off unless asked for. With it off the same pages read slightly more. Under
/// PaddleOcrNet 2.2.0 it also put a straightened page's text up to 6.6 pt beside its ink; 2.2.1 fixes
/// most of that. docs/measurements/deskew-offset.md.
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
