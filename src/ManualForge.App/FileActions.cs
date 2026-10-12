using System.ComponentModel;
using System.Diagnostics;
using ManualForge.Core.Indexing;
using ManualForge.Core.Pipeline;
using ManualForge.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace ManualForge.App;

/// <summary>
/// What a list of manuals lets you do with one: open it in the PDF viewer Windows has for PDFs, on a
/// double-click or Enter; or, from its right-click menu (or the Menu key), open it, show it in its
/// folder, or copy its path. The Search, Results and Doctor lists all show manuals, and all three
/// behave the same.
///
/// The viewer opens at the first page. Windows has no way to ask whichever viewer is the default to
/// go to a page, so a search hit's page number is the one to turn to.
/// </summary>
public static class FileActions
{
    /// <summary>A manual's tooltip: where it is, and that it can be opened.</summary>
    public static string Hint(string path) => $"{path}\nDouble-click to open; right-click for more.";

    /// <summary>Wires a list's double-click, Enter key and context menu to the manual in each row.</summary>
    public static void Attach(ListView list)
    {
        list.DoubleTapped += (_, e) =>
        {
            // A double-click on a row's tick box is two ticks, not a request to open the manual.
            if (e.OriginalSource is DependencyObject source && !IsInButton(source) && PathOf(source) is { } path)
            {
                e.Handled = true;
                Open(list, path);
            }
        };

        list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && e.OriginalSource is ListViewItem item && PathOf(item) is { } path)
            {
                e.Handled = true;
                Open(list, path);
            }
        };

        list.ContextRequested += (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement source || PathOf(source) is not { } path)
                return;

            e.Handled = true;
            var menu = Menu(list, path);
            if (e.TryGetPosition(source, out var point))
                menu.ShowAt(source, new FlyoutShowOptions { Position = point });
            else
                menu.ShowAt(source);
        };
    }

    private static MenuFlyout Menu(FrameworkElement owner, string path)
    {
        var open = new MenuFlyoutItem { Text = "Open", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += (_, _) => Open(owner, path);

        var folder = new MenuFlyoutItem { Text = "Show in folder", Icon = new SymbolIcon(Symbol.Folder) };
        folder.Click += (_, _) => ShowInFolder(owner, path);

        var copy = new MenuFlyoutItem { Text = "Copy path", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
        };

        var menu = new MenuFlyout();
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(copy);
        return menu;
    }

    private static void Open(FrameworkElement owner, string path)
    {
        if (!File.Exists(path))
        {
            _ = ExplainAsync(owner, path, "It is no longer there. It may have been moved or deleted since the list was made.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            // Most often no app is set to open PDFs.
            _ = ExplainAsync(owner, path, ex.Message);
        }
    }

    private static void ShowInFolder(FrameworkElement owner, string path)
    {
        if (!File.Exists(path))
        {
            _ = ExplainAsync(owner, path, "It is no longer there. It may have been moved or deleted since the list was made.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private static async Task ExplainAsync(FrameworkElement owner, string path, string reason)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = "Could not open the manual",
            Content = new TextBlock { Text = $"{path}\n\n{reason}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    /// <summary>The manual a row shows, found from anything inside the row.</summary>
    private static string? PathOf(DependencyObject element)
    {
        for (var at = element; at is not null; at = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at))
        {
            var item = at is ListViewItem row ? row.Content : (at as FrameworkElement)?.DataContext;
            switch (item)
            {
                case SearchHit hit: return hit.Path;
                case FileOutcome outcome: return outcome.Path;
                case DoctorRow doctor: return doctor.Path;
            }

            if (at is ListView)
                return null;
        }

        return null;
    }

    private static bool IsInButton(DependencyObject element)
    {
        for (var at = element; at is not null and not ListViewItem; at = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is ButtonBase)
                return true;
        }

        return false;
    }
}
