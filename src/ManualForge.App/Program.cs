using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ManualForge.App;

/// <summary>
/// The entry point, in place of the one XAML generates, so that there is only ever one ManualForge
/// window (#89).
///
/// <para>
/// A second launch - Send To with more manuals, or the Start menu while the window is open - hands its
/// command line to the instance already running and exits. That instance queues the files behind
/// whatever it is doing, so two copies never compete for the card. Windows App SDK's
/// <see cref="AppInstance"/> does the handing over, which works for an unpackaged app.
/// </para>
/// </summary>
public static class Program
{
    private const string InstanceKey = "ManualForge.App";

    /// <summary>Activations handed over before the window was ready for them, oldest first.</summary>
    private static readonly ConcurrentQueue<AppActivationArguments> Pending = new();

    private static Action<AppActivationArguments>? _deliver;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            RedirectTo(instance);
            return 0;
        }

        instance.Activated += (_, args) =>
        {
            if (Volatile.Read(ref _deliver) is { } deliver)
                deliver(args);
            else
                Pending.Enqueue(args);
        };

        Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Where activations handed over from later launches go from now on, starting with any that came
    /// before the window was ready. Called on a background thread for each.
    /// </summary>
    public static void OnRedirected(Action<AppActivationArguments> deliver)
    {
        Volatile.Write(ref _deliver, deliver);
        while (Pending.TryDequeue(out var args))
            deliver(args);
    }

    /// <summary>
    /// The arguments of a handed-over launch. An unpackaged app's launch arguments are its whole
    /// command line, program name and all, as one string.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsOf(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Launch
            || args.Data is not Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            || string.IsNullOrWhiteSpace(launch.Arguments))
        {
            return [];
        }

        var argv = CommandLineToArgvW(launch.Arguments, out var count);
        if (argv == IntPtr.Zero)
            return [];

        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++)
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>
    /// Hands this launch to the running instance and brings its window forward. The hand-over is
    /// waited for on another thread while this one pumps COM, as Microsoft's single-instance sample
    /// does: blocking the STA outright can deadlock it.
    /// </summary>
    private static void RedirectTo(AppInstance instance)
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();

        // This launch is in the foreground, having just been started from Explorer; the running one is
        // not, and Windows only lets a window come forward if the foreground one allows it.
        AllowSetForegroundWindow(instance.ProcessId);

        using var done = new ManualResetEvent(false);
        _ = Task.Run(() =>
        {
            try
            {
                instance.RedirectActivationToAsync(args).AsTask().Wait();
            }
            finally
            {
                done.Set();
            }
        });

        _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
    }

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
