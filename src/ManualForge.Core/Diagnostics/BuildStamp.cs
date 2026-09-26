using System.Reflection;

namespace ManualForge.Core.Diagnostics;

/// <summary>
/// Which build this is.
///
/// <para>
/// An installed binary used to be identifiable only by its file timestamp, which records when a
/// copy was made and says nothing about what was in it. On 25 Sep 2026 the installed CLI sat two
/// fixes behind <c>main</c> for an evening — still auditing with a detector every document called
/// disabled — and nothing reported it. The commit is stamped into
/// <see cref="AssemblyInformationalVersionAttribute"/> by <c>Directory.Build.props</c>; this reads
/// it back. Issue #18.
/// </para>
/// </summary>
public static class BuildStamp
{
    /// <summary>The running program's version, as <c>0.1.0+3441e55</c>, or <c>+…dirty</c>.</summary>
    public static string Version { get; } = Read();

    /// <summary>Where the running assembly was loaded from, so two installs can be told apart.</summary>
    public static string Location { get; } = LocationOf();

    /// <summary>The commit alone, or null when the build could not see a git repository.</summary>
    public static string? Commit
    {
        get
        {
            var plus = Version.IndexOf('+');
            if (plus < 0)
                return null;

            var stamp = Version[(plus + 1)..];
            if (stamp.StartsWith("nogit", StringComparison.Ordinal))
                return null;

            var dot = stamp.IndexOf('.');
            return dot < 0 ? stamp : stamp[..dot];
        }
    }

    /// <summary>True when the build had uncommitted changes, so the commit alone does not describe it.</summary>
    public static bool IsDirty => Version.EndsWith(".dirty", StringComparison.Ordinal);

    public static void Print()
    {
        Console.WriteLine($"manualforge {Version}");
        Console.WriteLine(Location);

        if (IsDirty)
            Console.WriteLine("Built from a working tree with uncommitted changes.");
    }

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildStamp).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        // No stamp at all rather than a wrong one: a missing attribute is a build-configuration
        // problem, and saying "0.1.0" would hide it behind something that looks like an answer.
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string LocationOf()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildStamp).Assembly;

        // Single-file and trimmed publishes report an empty Location, so fall back to the process.
        if (!string.IsNullOrEmpty(assembly.Location))
            return assembly.Location;

        return Environment.ProcessPath ?? "(unknown location)";
    }
}
