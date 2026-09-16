using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ManualForge.Core.Diagnostics;

/// <summary>
/// Writes one JSON object per line to a log file, on a background writer so logging never blocks
/// the pipeline.
///
/// Rolled by hand rather than taken from a logging package because the run needs exactly one
/// thing — a machine-readable record of what happened to which file — and a 100-line provider is
/// a smaller commitment than another dependency.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 8192);
    private readonly StreamWriter _writer;
    private readonly Thread _thread;
    private readonly LogLevel _minimum;
    private volatile bool _disposed;

    public JsonFileLoggerProvider(string path, LogLevel minimum = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        _minimum = minimum;

        // FileShare.ReadWrite, not FileShare.Read: a long library run holds this file open for
        // hours, and anything less stops a second process starting at all — no inspecting a file,
        // no surveying, no second run. Append mode keeps whole lines from interleaving.
        // If the file is unavailable anyway, fall back to a process-specific name rather than
        // failing to start over a log file.
        var stream = TryOpen(Path) ?? OpenFallback(ref path);
        Path = System.IO.Path.GetFullPath(path);

        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ManualForge log writer",
        };
        _thread.Start();
    }

    public string Path { get; }

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream OpenFallback(ref string path)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        var candidate = System.IO.Path.Combine(
            directory, $"{stem}-{Environment.ProcessId}{extension}");

        var stream = TryOpen(candidate)
            ?? throw new IOException($"Could not open a log file at '{path}' or '{candidate}'.");

        path = candidate;
        return stream;
    }

    private void Pump()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                _writer.WriteLine(line);
                // Flush when the queue drains, so a crash loses at most the in-flight batch while
                // a busy run still batches its writes.
                if (_queue.Count == 0)
                    _writer.Flush();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        finally
        {
            try { _writer.Flush(); } catch (ObjectDisposedException) { /* already gone */ }
        }
    }

    private void Enqueue(string line)
    {
        if (_disposed)
            return;
        // Drop rather than block: losing a log line is always better than stalling the pipeline.
        _queue.TryAdd(line);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _writer.Dispose();
        _queue.Dispose();
    }

    private sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var buffer = new MemoryStream(256);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                json.WriteString("message", formatter(state, exception));

                if (eventId.Id != 0)
                    json.WriteNumber("eventId", eventId.Id);

                // Structured values from the message template, so the log can be queried on the
                // things that matter: file, page, provider.
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        if (key == "{OriginalFormat}")
                            continue;
                        WriteValue(json, key, value);
                    }
                }

                if (exception is not null)
                {
                    json.WriteString("exception", exception.GetType().FullName);
                    json.WriteString("exceptionMessage", exception.Message);
                    json.WriteString("stackTrace", exception.StackTrace ?? string.Empty);
                }

                json.WriteEndObject();
            }

            provider.Enqueue(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        private static void WriteValue(Utf8JsonWriter json, string key, object? value)
        {
            switch (value)
            {
                case null: json.WriteNull(key); break;
                case string s: json.WriteString(key, s); break;
                case bool b: json.WriteBoolean(key, b); break;
                case int i: json.WriteNumber(key, i); break;
                case long l: json.WriteNumber(key, l); break;
                case double d when double.IsFinite(d): json.WriteNumber(key, d); break;
                case float f when float.IsFinite(f): json.WriteNumber(key, f); break;
                default: json.WriteString(key, value.ToString() ?? string.Empty); break;
            }
        }
    }
}
