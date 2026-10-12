using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ManualForge.App;

/// <summary>
/// A control's help, written once: <c>local:Help.Text="..."</c> sets both its tooltip, for the mouse
/// and keyboard focus, and <see cref="AutomationProperties.HelpTextProperty"/>, which screen readers
/// announce and do not reliably take from a tooltip (#75). Two attributes saying the same thing would
/// drift apart; this one cannot.
/// </summary>
public static class Help
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Help), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string;
        ToolTipService.SetToolTip(element, string.IsNullOrWhiteSpace(text) ? null : text);
        AutomationProperties.SetHelpText(element, text ?? string.Empty);
    }
}
