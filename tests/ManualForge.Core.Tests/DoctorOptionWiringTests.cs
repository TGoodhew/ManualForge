using ManualForge.Cli;
using ManualForge.Core.Auditing;

namespace ManualForge.Core.Tests;

/// <summary>
/// The doctor's defaults are documented in three places — <c>docs/UNDER-EXTRACTION.md</c>, the
/// measurement write-ups and the README — and none of them is checked by anything. Reverse video was
/// wired as <c>--no-reverse-video</c>, an opt-*out*, while every document described <c>--reverse-video</c>
/// as an opt-in and the detector as off by default. It ran on the whole corpus for two ingests before
/// anyone read the wiring. These tests pin the flags that carry a cost when they are wrong.
/// </summary>
public sealed class DoctorOptionWiringTests
{
    private static DoctorOptions From(params string[] args) =>
        DoctorCommand.OptionsFrom(CommandLine.Parse(["doctor", "library", .. args]));

    [Fact]
    public void ReverseVideoIsOffUnlessAskedFor()
    {
        Assert.False(From().ReverseVideo);
    }

    [Fact]
    public void ReverseVideoFlagTurnsItOn()
    {
        Assert.True(From("--reverse-video").ReverseVideo);
    }

    /// <summary>
    /// The discriminating case: before the fix this passed <em>and</em> the one above passed, because
    /// the option was true whatever you typed. Only asserting the default catches that.
    /// </summary>
    [Fact]
    public void ReverseVideoDefaultMatchesTheLibraryDefault()
    {
        Assert.Equal(new DoctorOptions().ReverseVideo, From().ReverseVideo);
    }

    [Fact]
    public void UnsetOptionsFallBackToTheLibraryDefaults()
    {
        var wired = From();
        var library = new DoctorOptions();

        Assert.Equal(library.AuditDpi, wired.AuditDpi);
        Assert.Equal(library.UncoveredInkFraction, wired.UncoveredInkFraction);
        Assert.Equal(library.MinimumGlyphLikeBlobs, wired.MinimumGlyphLikeBlobs);
        Assert.Equal(library.RenderBelowCharactersPerPage, wired.RenderBelowCharactersPerPage);
        Assert.Equal(library.RenderAtOrAbovePathOperations, wired.RenderAtOrAbovePathOperations);
        Assert.Equal(library.RenderAtOrAboveImageCoverage, wired.RenderAtOrAboveImageCoverage);
        Assert.Equal(library.RuleSegmentRun, wired.RuleSegmentRun);
        Assert.Equal(library.ReverseVideoMinimumAreaPt, wired.ReverseVideoMinimumAreaPt);
        Assert.Equal(library.ReverseVideoMinimumFill, wired.ReverseVideoMinimumFill);
    }

    [Theory]
    [InlineData("--audit-dpi", "220")]
    [InlineData("--min-blobs", "7")]
    [InlineData("--render-below-chars", "500")]
    public void AnExplicitValueOverridesTheDefault(string flag, string value)
    {
        var wired = From(flag, value);
        var library = new DoctorOptions();

        var changed = flag switch
        {
            "--audit-dpi" => wired.AuditDpi != library.AuditDpi,
            "--min-blobs" => wired.MinimumGlyphLikeBlobs != library.MinimumGlyphLikeBlobs,
            _ => wired.RenderBelowCharactersPerPage != library.RenderBelowCharactersPerPage,
        };

        Assert.True(changed, $"{flag} {value} did not reach the options object");
    }
}
