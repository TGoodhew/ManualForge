using System.Reflection;
using ManualForge.Core.Diagnostics;

namespace ManualForge.Core.Tests;

/// <summary>
/// The point of the stamp is to answer "which build is this?" for an installed binary. That only
/// works if the MSBuild target actually ran, and a target that silently stops running is exactly
/// the failure it was written to prevent — so the stamp is asserted rather than assumed.
/// </summary>
public sealed class BuildStampTests
{
    private static string InformationalVersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "";

    [Fact]
    public void TheCommitIsStampedIntoTheAssembly()
    {
        var stamped = InformationalVersionOf(typeof(BuildStamp).Assembly);

        Assert.Contains('+', stamped);
        Assert.DoesNotContain("nogit", stamped, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStampNamesAnActualCommit()
    {
        var stamped = InformationalVersionOf(typeof(BuildStamp).Assembly);
        var commit = stamped[(stamped.IndexOf('+') + 1)..].Split('.')[0];

        // A short hash, not a branch name or a placeholder.
        Assert.Equal(7, commit.Length);
        Assert.All(commit, c => Assert.True(Uri.IsHexDigit(c), $"'{commit}' is not a commit hash"));
    }

    [Fact]
    public void VersionAndLocationAreAlwaysAnswerable()
    {
        // Whatever the build looked like, these must not throw and must not come back empty:
        // a diagnostic that fails when things are wrong is worth nothing.
        Assert.False(string.IsNullOrWhiteSpace(BuildStamp.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildStamp.Location));
    }

    [Fact]
    public void ADirtyBuildSaysSoRatherThanNamingACommitAlone()
    {
        // Not asserting which way round it is - that depends on the tree the tests run in - but the
        // two must agree, because "built from <commit>" is false if the tree had edits.
        Assert.Equal(BuildStamp.Version.EndsWith(".dirty", StringComparison.Ordinal), BuildStamp.IsDirty);
    }
}
