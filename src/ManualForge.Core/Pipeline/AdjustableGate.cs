using System.Diagnostics;
using ManualForge.Core.Ocr;
using Microsoft.Extensions.Logging;

namespace ManualForge.Core.Pipeline;

/// <summary>
/// Lets a number of workers through at once, where the number can change while they run. Lowering
/// it takes effect as pages in flight finish; nothing running is interrupted.
/// </summary>
internal sealed class AdjustableGate(int limit)
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private int _limit = Math.Max(1, limit);
    private int _active;

    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_lock)
        {
            if (_active < _limit)
            {
                _active++;
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
        }

        using (cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)))
            await waiter.Task.ConfigureAwait(false);
    }

    public void Exit()
    {
        lock (_lock)
        {
            _active--;
            Admit();
        }
    }

    public void SetLimit(int limit)
    {
        lock (_lock)
        {
            _limit = Math.Max(1, limit);
            Admit();
        }
    }

    private void Admit()
    {
        while (_active < _limit && _waiting.Count > 0)
        {
            // A waiter whose run was cancelled is skipped, not admitted.
            if (_waiting.Dequeue().TrySetResult())
                _active++;
        }
    }
}

/// <summary>
/// Counts pages as they finish and hands the tuner a window - with how much of the process's GPU memory
/// has gone out to system RAM - when there are enough of them and enough time has passed.
/// </summary>
internal sealed class TuningWindow
{
    private readonly object _lock = new();
    private readonly ConcurrencyController _tuner;
    private readonly AdjustableGate _gate;
    private readonly PipelineOptions _options;
    private readonly ILogger _logger;
    private readonly Func<int?> _readSpilled;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _pages;

    public TuningWindow(ConcurrencyController tuner, AdjustableGate gate, PipelineOptions options, ILogger logger)
    {
        _tuner = tuner;
        _gate = gate;
        _options = options;
        _logger = logger;
        _readSpilled = options.ReadSpilledMiB ?? (() => GpuMemoryProbe.TryReadSpilledMiB());

        // The models are loaded by now, so this is what a healthy process holds out there.
        tuner.Begin(_readSpilled());
    }

    public void PageDone()
    {
        _tuner.PageRead();

        ConcurrencyWindow window;
        lock (_lock)
        {
            _pages++;
            var level = _gate.Limit;
            if (_pages < Math.Max(12, 4 * level) || _clock.Elapsed < _options.TuningWindow)
                return;

            window = new ConcurrencyWindow(level, _pages, _clock.Elapsed, null);
            _pages = 0;
            _clock.Restart();
        }

        window = window with { SpilledMiB = _readSpilled() };

        ConcurrencyDecision? decision;
        lock (_lock)
            decision = _tuner.Observe(window);

        if (decision is null)
            return;

        _gate.SetLimit(decision.To);
        _logger.LogInformation(
            "Pages in flight {From} -> {To}: {Reason}", decision.From, decision.To, decision.Reason);
        _options.OnTuned?.Invoke(decision);
    }
}
