namespace ManualForge.Shell;

/// <summary>
/// How the view model gets back onto the thread that owns the UI.
///
/// This exists instead of <see cref="Progress{T}"/>, which looks like the obvious choice and is
/// the wrong one here. Progress posts its callbacks and returns, so a report made just before a run
/// finishes can be applied after the code that awaited it has already drawn its conclusions. That
/// produced both halves of the same bug: counts that were short because the last reports had not
/// landed, and then duplicated rows because they landed after the totals were rebuilt.
///
/// A dispatcher makes the ordering explicit. The real one is the window's queue, which is
/// first-in-first-out, so every report posted during a run is applied before the continuation that
/// follows the await. The test one runs inline, which makes the same guarantee trivially and means
/// the shell's behaviour can be asserted rather than waited for.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs the callback where it was raised. The default, and what the tests use.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    public static readonly InlineDispatcher Instance = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports through a dispatcher rather than a post.</summary>
internal sealed class DispatchedProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
{
    public void Report(T value) => dispatcher.Post(() => handler(value));
}
