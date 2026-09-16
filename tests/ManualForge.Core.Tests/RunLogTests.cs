using System.Text.Json;
using ManualForge.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ManualForge.Core.Tests;

/// <summary>
/// The run log is the only record of what an unattended overnight run did, so what is tested here
/// is the three things that would make it useless: unparseable lines, a lock that stops a second
/// process starting, and a tail lost when the process is killed.
/// </summary>
public class RunLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public RunLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public void EveryLineIsOneJsonObjectCarryingItsStructuredProperties()
    {
        var path = Path_("run.jsonl");

        using (var log = RunLog.Open(path, roll: false))
        {
            var logger = log.Factory.CreateLogger("test");
            logger.LogInformation("Completed {Path}: {Words} words", @"C:\Manuals\8340B.pdf", 25853);
            logger.LogWarning("{Path} is digitally signed", @"C:\Manuals\signed.pdf");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("Information", first.GetProperty("Level").GetString());

        // The properties have to survive as values, not just interpolated into a sentence, or the
        // log cannot be queried for what happened to a particular file.
        var properties = first.GetProperty("Properties");
        Assert.Equal(@"C:\Manuals\8340B.pdf", properties.GetProperty("Path").GetString());
        Assert.Equal(25853, properties.GetProperty("Words").GetInt32());

        Assert.Contains("25853 words", first.GetProperty("RenderedMessage").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Warning", JsonDocument.Parse(lines[1]).RootElement.GetProperty("Level").GetString());
    }

    [Fact]
    public void AnOpenLogDoesNotStopASecondProcessStarting()
    {
        var path = Path_("shared.jsonl");

        // A library run holds this file open for hours. Anything less than shared access means no
        // inspecting a file, no surveying and no second run while one is going.
        using var first = RunLog.Open(path, roll: false);
        using var second = RunLog.Open(path, roll: false);

        first.Factory.CreateLogger("a").LogInformation("from the first");
        second.Factory.CreateLogger("b").LogInformation("from the second");

        var text = ReadWhileOpen(path);
        Assert.Contains("from the first", text, StringComparison.Ordinal);
        Assert.Contains("from the second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesAreReadableWithoutClosingTheLog()
    {
        var path = Path_("live.jsonl");

        using var log = RunLog.Open(path, roll: false);
        log.Factory.CreateLogger("test").LogInformation("Page {Page} done", 7);

        // Nothing is buffered waiting for a clean shutdown, so a run killed mid-flight still ends
        // with the last thing that happened.
        Assert.Contains("Page 7 done", ReadWhileOpen(path), StringComparison.Ordinal);
    }

    [Fact]
    public void DebugEventsAreKeptOnlyWhenAskedFor()
    {
        var quiet = Path_("quiet.jsonl");
        var verbose = Path_("verbose.jsonl");

        using (var log = RunLog.Open(quiet, verbose: false, roll: false))
            log.Factory.CreateLogger("test").LogDebug("a detail");

        using (var log = RunLog.Open(verbose, verbose: true, roll: false))
            log.Factory.CreateLogger("test").LogDebug("a detail");

        Assert.Empty(File.ReadAllText(quiet));
        Assert.Contains("a detail", File.ReadAllText(verbose), StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportedPathIsWhereTheLogActuallyLands()
    {
        var template = Path_("manualforge-.jsonl");

        using var log = RunLog.Open(template);
        log.Factory.CreateLogger("test").LogInformation("hello");

        // The CLI prints this path. It has to be the file, not the template.
        Assert.Equal(RunLog.DatedPath(template, DateTime.Now), log.Path);
        Assert.True(File.Exists(log.Path));
    }

    [Fact]
    public void ADatedPathPutsTheDateBeforeTheExtension()
        => Assert.Equal(
            System.IO.Path.Combine(_directory, "manualforge-20260916.jsonl"),
            RunLog.DatedPath(Path_("manualforge-.jsonl"), new DateTime(2026, 9, 16)));

    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
