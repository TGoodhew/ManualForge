using ManualForge.Core.Diagnostics;
using ManualForge.Shell;
using ManualForge.Shell.ViewModels;
using Microsoft.UI.Xaml;

namespace ManualForge.App;

/// <summary>
/// Composition root. The only thing here that is not wiring is where the log goes.
/// </summary>
public partial class App : Application
{
    /// <summary>The main window, for pickers and any other interop that needs an HWND.</summary>
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>The shell's state. One instance, shared by every page that shows part of it.</summary>
    public static LibraryViewModel Library { get; private set; } = null!;

    public static SearchViewModel Search { get; private set; } = null!;

    public static DoctorViewModel Doctor { get; private set; } = null!;

    /// <summary>Files added from Explorer or a later launch, and the work on them (#89).</summary>
    public static AddQueueViewModel Adds { get; private set; } = null!;

    private static RunLog? _log;
    private static LibraryService? _service;

    public App()
    {
        InitializeComponent();

        // A XAML failure at startup otherwise leaves nothing behind but exit code 0xC000027B,
        // which says only "a stowed exception happened somewhere". Writing it down cost one
        // rebuild here and would have saved the one before it.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
        };
    }

    private static void Report(Exception exception)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManualForge", "logs", "crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        catch (Exception)
        {
            // Nothing useful to do if even that fails.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            Report(ex);
            throw;
        }
    }

    private void Start()
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // The same rolling JSON-lines log the command line writes, so a run started from either
        // ends up in one place.
        _log = RunLog.Open(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ManualForge", "logs", "manualforge-.jsonl"));

        _service = new LibraryService(new LibraryServiceOptions(), _log.Factory);
        var dispatcher = new DispatcherQueueAdapter(DispatcherQueue);
        Library = new LibraryViewModel(_service, dispatcher: dispatcher);
        Search = new SearchViewModel(new SearchService(_log.Factory), dispatcher);
        Doctor = new DoctorViewModel(new DoctorService(_log.Factory), dispatcher);
        Adds = new AddQueueViewModel(_service, Library, Doctor, Search, LibraryToAddTo);

        // A folder given on the command line, so the application can be started on one and so this
        // window can be driven by something other than a person with a mouse; else the folder it was
        // last closed on; else the library the installer recorded.
        var launch = LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1));
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var folder = StartFolder.Choose(
            launch.Start,
            settings.LastFolder,
            Environment.GetEnvironmentVariable("MANUALFORGE_LIBRARY"));
        if (folder is not null)
            Library.Folder = folder;

        // The window last, and that ordering is load bearing rather than tidy. MainWindow navigates
        // its frame to MainPage inside its own constructor, so MainPage's constructor runs before
        // this method's next statement does. Building the window first left every view model null
        // at that moment, and the page subscribes to them there — which crashed the application on
        // launch with nothing but 0xC000027B to show for it.
        Window = new MainWindow();

        Window.Closed += async (_, _) =>
        {
            // Remembered on closing rather than as it changes: the folder box changes with every
            // keystroke, and only the folder the window ends on is the one to open next time.
            if (Library.Folder is { Length: > 0 } last && Directory.Exists(last))
                new AppSettings(last).Save(AppSettings.DefaultPath);

            if (_service is not null)
                await _service.DisposeAsync();
            _log?.Dispose();
        };

        Window.Activate();

        if (launch.Add.Count > 0)
            _ = Adds.AddAsync(launch.Add);

        // Later launches hand their command line here and exit (Program). Each arrives on a
        // background thread, so it is taken to the window's.
        Program.OnRedirected(args => DispatcherQueue.TryEnqueue(() =>
        {
            BringForward();
            var later = LaunchArguments.Parse(Program.ArgumentsOf(args));

            // A folder given to a later launch, as the README says, unless the window is busy with another.
            if (StartFolder.Choose(later.Start, null, null) is { } other
                && Library.IsIdle && Doctor.IsIdle && Search.IsIdle && !Adds.IsWorking)
                Library.Folder = other;

            if (later.Add.Count > 0)
                _ = Adds.AddAsync(later.Add);
        }));
    }

    /// <summary>
    /// The library the installer recorded, read from where it keeps it rather than from this process's
    /// copy of the environment, which is as old as the process; else the folder the window is on.
    /// </summary>
    private static string? LibraryToAddTo()
    {
        foreach (var recorded in new[]
                 {
                     Environment.GetEnvironmentVariable("MANUALFORGE_LIBRARY", EnvironmentVariableTarget.User),
                     Environment.GetEnvironmentVariable("MANUALFORGE_LIBRARY"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(recorded) && Directory.Exists(recorded))
                return recorded;
        }

        return Library.Folder;
    }

    /// <summary>Restores the window if it was minimised and brings it to the front.</summary>
    private static void BringForward()
    {
        if (Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            {
                State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized,
            } presenter)
        {
            presenter.Restore();
        }

        Window.Activate();
        SetForegroundWindow(WindowHandle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}

/// <summary>Posts back onto the window's queue, which is first-in-first-out.</summary>
internal sealed class DispatcherQueueAdapter(Microsoft.UI.Dispatching.DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
            action();
        else
            queue.TryEnqueue(() => action());
    }
}
