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
        Loaded += (_, _) =>
        {
            ViewModel.RefreshGpu();
            _gpuTimer.Start();
        };
        Unloaded += (_, _) => _gpuTimer.Stop();
    }

    public LibraryViewModel ViewModel => App.Library;

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
