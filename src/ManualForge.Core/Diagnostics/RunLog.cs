using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace ManualForge.Core.Diagnostics;

/// <summary>
/// The run log: one JSON object per line, rolled daily, kept for a month.
///
/// A library run is long and unattended. The log is the only record of what happened to which
/// file, so three properties matter more than the format does.
///
/// * <b>It must not stop a second process starting.</b> A run holds the log open for hours, and
///   anything short of shared access means no inspecting a file, no surveying and no second run
///   while one is going. The file sink is opened shared.
/// * <b>It must survive being killed.</b> Lines are flushed as they are written rather than
///   buffered, so a log that ends abruptly still ends with the last thing that happened.
/// * <b>It must not grow without bound.</b> Rolled by day and capped at a month.
///
/// This replaces a hand-rolled provider that did the first two and neither of the last two. The
/// JSON formatter ships inside Serilog itself, so the only packages involved are Serilog, its
/// Microsoft.Extensions.Logging bridge and the file sink.
/// </summary>
public sealed class RunLog : IDisposable
{
    private readonly Serilog.Core.Logger _logger;

    private RunLog(Serilog.Core.Logger logger, ILoggerFactory factory, string path)
    {
        _logger = logger;
        Factory = factory;
        Path = path;
    }

    /// <summary>The factory the rest of the application logs through.</summary>
    public ILoggerFactory Factory { get; }

    /// <summary>The file being written to right now, with today's date already substituted in.</summary>
    public string Path { get; }

    /// <summary>How many daily files are kept before the oldest is deleted.</summary>
    public const int RetainedDays = 30;

    /// <param name="pathTemplate">
    /// Where to write. The date is inserted before the extension, so
    /// <c>manualforge-.jsonl</c> becomes <c>manualforge-20260916.jsonl</c>.
    /// </param>
    /// <param name="verbose">Include debug-level events.</param>
    /// <param name="roll">
    /// Roll daily and insert the date. False writes to exactly the path given, which is what
    /// someone naming a file on the command line means by it.
    /// </param>
    public static RunLog Open(string pathTemplate, bool verbose = false, bool roll = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathTemplate);

        var full = System.IO.Path.GetFullPath(pathTemplate);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                formatter: new JsonFormatter(renderMessage: true),
                path: full,
                rollingInterval: roll ? RollingInterval.Day : RollingInterval.Infinite,
                retainedFileCountLimit: roll ? RetainedDays : null,
                shared: true,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .CreateLogger();

        // dispose: false — this type owns the logger and closes it itself, so that disposing the
        // factory (which several call sites do) cannot close the log out from under a run.
        var factory = new SerilogLoggerFactory(logger, dispose: false);
        return new RunLog(logger, factory, roll ? DatedPath(full, DateTime.Now) : full);
    }

    /// <summary>
    /// Where the sink will actually write today. Serilog inserts the date before the extension,
    /// and callers print this path, so it has to be worked out the same way here.
    /// </summary>
    public static string DatedPath(string pathTemplate, DateTime day)
    {
        var directory = System.IO.Path.GetDirectoryName(pathTemplate) ?? string.Empty;
        var stem = System.IO.Path.GetFileNameWithoutExtension(pathTemplate);
        var extension = System.IO.Path.GetExtension(pathTemplate);
        return System.IO.Path.Combine(directory, $"{stem}{day:yyyyMMdd}{extension}");
    }

    public void Dispose()
    {
        Factory.Dispose();
        _logger.Dispose();
    }
}
