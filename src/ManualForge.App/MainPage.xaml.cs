using ManualForge.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ManualForge.App;

/// <summary>
/// The window's content.
///
/// There is no logic here on purpose. What remains is what genuinely belongs to a window rather than
/// to a view model: file pickers, which need the native window handle; a timer that asks the card how
/// it is doing; and opening a manual from a list (<see cref="FileActions"/>). Everything else is in
/// <see cref="LibraryViewModel"/>, where it can be tested.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _gpuTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainPage()
    {
        InitializeComponent();
        WireDragToPan();
        FileActions.Attach(SearchResultsList);
        FileActions.Attach(ResultsList);
        FileActions.Attach(DoctorList);

        _gpuTimer.Tick += (_, _) => ViewModel.RefreshGpu();
        Loaded += async (_, _) =>
        {
            ViewModel.RefreshGpu();
            _gpuTimer.Start();

            await ShareFolderAsync();
        };

        // The diagnostic picture arrives as PNG bytes, because the view model has no business
        // knowing what a BitmapImage is. Turning those into one is a view concern, and this is
        // the view.
        Doctor.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(DoctorViewModel.Diagnostic))
                await ShowDiagnosticAsync();
        };

        // Choosing a folder in the library tab is choosing which index to search.
        ViewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(LibraryViewModel.Folder))
                return;

            await ShareFolderAsync();
        };
        Unloaded += (_, _) => _gpuTimer.Stop();
    }

    /// <summary>
    /// Gives the search and doctor tabs the library's folder, then has each read what it has for it,
    /// side by side. The doctor tab used to be given the folder only after the search tab had read the
    /// index's statistics, which took 53 seconds on a 630-manual library: until then it said "Pick a
    /// folder" with its buttons disabled and nothing to say anything was happening (#74).
    /// </summary>
    private async Task ShareFolderAsync()
    {
        Search.Folder = ViewModel.Folder;
        Doctor.Folder = ViewModel.Folder;

        var doctor = Doctor.RefreshCommand.CanExecute(null) ? Doctor.RefreshAsync() : Task.CompletedTask;
        await Task.WhenAll(Search.RefreshAsync(), doctor);
    }

    public LibraryViewModel ViewModel => App.Library;

    public SearchViewModel Search => App.Search;

    public DoctorViewModel Doctor => App.Doctor;

    /// <summary>
    /// Puts the doctor's diagnostic picture on screen. The bytes come from the view model; a
    /// <c>BitmapImage</c> is a XAML type and stays here.
    /// </summary>
    private async Task ShowDiagnosticAsync()
    {
        if (Doctor.Diagnostic is not { Length: > 0 } bytes)
        {
            DiagnosticImage.Source = null;
            return;
        }

        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }

            await image.SetSourceAsync(stream);
        }

        DiagnosticImage.Source = image;
    }

    private Windows.Foundation.Point? _dragFrom;
    private double _dragFromX;
    private double _dragFromY;

    /// <summary>
    /// Drag the diagnostic picture with the mouse to move around it, rather than finding the scroll
    /// bars at its edges. The scroll bars stay, and touch pans by itself. Positions are taken against
    /// the scroller rather than the picture, which moves under the pointer as it scrolls.
    /// </summary>
    private void WireDragToPan()
    {
        DiagnosticScroller.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                return;

            var point = e.GetCurrentPoint(DiagnosticScroller);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _dragFrom = point.Position;
            _dragFromX = DiagnosticScroller.HorizontalOffset;
            _dragFromY = DiagnosticScroller.VerticalOffset;
            DiagnosticScroller.CapturePointer(e.Pointer);
            e.Handled = true;
        }), handledEventsToo: true);

        DiagnosticScroller.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (_dragFrom is not { } from)
                return;

            var at = e.GetCurrentPoint(DiagnosticScroller).Position;
            DiagnosticScroller.ChangeView(_dragFromX - (at.X - from.X), _dragFromY - (at.Y - from.Y), null, disableAnimation: true);
            e.Handled = true;
        }), handledEventsToo: true);

        void Stop(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_dragFrom is null)
                return;

            _dragFrom = null;
            DiagnosticScroller.ReleasePointerCapture(e.Pointer);
        }

        DiagnosticScroller.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Stop), handledEventsToo: true);
        DiagnosticScroller.PointerCaptureLost += (_, _) => _dragFrom = null;
    }

    /// <summary>Enter searches. A keystroke is a view concern; what it does is not.</summary>
    private void SearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        e.Handled = true;
        if (Search.SearchCommand.CanExecute(null))
            Search.SearchCommand.Execute(null);
    }

    private async void BrowseAsync(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.Folder = folder.Path;
    }

    private async void ExportCsvAsync(object sender, RoutedEventArgs e)
        => await SaveReportAsync(".csv", "Comma-separated values");

    private async void ExportJsonAsync(object sender, RoutedEventArgs e)
        => await SaveReportAsync(".json", "JSON");

    private async Task SaveReportAsync(string extension, string label)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "manualforge-report",
        };
        picker.FileTypeChoices.Add(label, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
            await ViewModel.SaveReportAsync(file.Path);
    }
}
