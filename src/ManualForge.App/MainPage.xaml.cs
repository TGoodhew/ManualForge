using ManualForge.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ManualForge.App;

/// <summary>
/// The window's content.
///
/// There is no logic here on purpose. What remains is the two things that genuinely belong to a
/// window rather than to a view model: a file picker, which needs the native window handle, and a
/// timer that asks the card how it is doing. Everything else is in
/// <see cref="LibraryViewModel"/>, where it can be tested.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _gpuTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainPage()
    {
        InitializeComponent();

        _gpuTimer.Tick += (_, _) => ViewModel.RefreshGpu();
        Loaded += async (_, _) =>
        {
            ViewModel.RefreshGpu();
            _gpuTimer.Start();

            // The search tab needs to know whether an index exists for whatever folder is set.
            Search.Folder = ViewModel.Folder;
            await Search.RefreshAsync();

            Doctor.Folder = ViewModel.Folder;
            if (Doctor.RefreshCommand.CanExecute(null))
                await Doctor.RefreshAsync();
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

            Search.Folder = ViewModel.Folder;
            await Search.RefreshAsync();

            Doctor.Folder = ViewModel.Folder;
            if (Doctor.RefreshCommand.CanExecute(null))
                await Doctor.RefreshAsync();
        };
        Unloaded += (_, _) => _gpuTimer.Stop();
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
