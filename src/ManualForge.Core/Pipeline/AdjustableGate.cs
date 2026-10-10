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
/// Counts pages as they finish and hands the tuner a window when there are enough of them and enough
/// time has passed to say something.
/// </summary>
internal sealed class TuningWindow(
    ConcurrencyController tuner, AdjustableGate gate, PipelineOptions options, ILogger logger)
{
    private readonly object _lock = new();
    private readonly Func<int?> _readSpilled = options.ReadSpilledMiB ?? (() => GpuMemoryProbe.TryReadSpilledMiB());
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _pages;
    private bool _warm;

    public void PageDone()
    {
        ConcurrencyWindow window;
        lock (_lock)
        {
            _pages++;
            var level = gate.Limit;
            if (_pages < Math.Max(12, 4 * level) || _clock.Elapsed < options.TuningWindow)
                return;

            window = new ConcurrencyWindow(level, _pages, _clock.Elapsed, null);
            _pages = 0;
            _clock.Restart();

            // The first window pays for loading the models and, on a card's first run, compiling its
            // kernels - 13.8 s for the first page on a new card against 4.9 s after. It says nothing
            // about pages in flight.
            if (!_warm)
            {
                _warm = true;
                return;
            }
        }

        window = window with { SpilledMiB = _readSpilled() };

        ConcurrencyDecision? decision;
        lock (_lock)
            decision = tuner.Observe(window);

        if (decision is null)
            return;

        gate.SetLimit(decision.To);
        logger.LogInformation(
            "Pages in flight {From} -> {To}: {Reason}", decision.From, decision.To, decision.Reason);
        options.OnTuned?.Invoke(decision);
    }
}
