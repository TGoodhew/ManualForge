<#
.SYNOPSIS
    Lists every control in the desktop app that a person can use but that has no help text (#75).

.DESCRIPTION
    Opens ManualForge.App on a library, visits each tab, and asks Windows UI Automation for the
    controls a person can operate: buttons, checkboxes, drop-downs, text and number boxes, tabs. Each
    should have help text, set with local:Help.Text in MainPage.xaml, which is both its tooltip and
    what a screen reader announces. Every one, and every table, also needs a name; a table needs no
    tooltip, which would cover its rows.

    Exits 1, listing them, if any control lacks either; 0 otherwise. Parts of other controls (a
    drop-down's own button, a number box's spin buttons, scroll bars, the window's caption buttons)
    are not counted: they come with WinUI and describe themselves.

.EXAMPLE
    ./tools/check-ui-help.ps1
    ./tools/check-ui-help.ps1 -App "$env:LOCALAPPDATA\Programs\ManualForge\ManualForge.App.exe" -Library D:\Manuals
#>
[CmdletBinding()]
param(
    [string] $App = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\installer\stage\ManualForge.App.exe'),
    [string] $Library = $env:MANUALFORGE_LIBRARY
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $App)) { throw "No app at $App. Run tools\build-installer.ps1 -SkipCompile first, or pass -App." }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$T = [Windows.Automation.TreeScope]
$C = [Windows.Automation.ControlType]

$checked = @($C::Button, $C::CheckBox, $C::ComboBox, $C::Edit, $C::Spinner, $C::TabItem, $C::List)
# Parts WinUI builds into other controls, by automation id or name.
$builtIn = '^(Minimize|Maximize|Restore|Close|DropDownButton|UpSpinButton|DownSpinButton|ScrollLeftButton|ScrollRightButton|AddButton|DeleteButton|TabListView|InputBox|.*ScrollBar.*|LineUp|LineDown|PageUp|PageDown)$'

$arguments = if ($Library) { "`"$Library`"" } else { '' }
$process = Start-Process $App -ArgumentList $arguments -PassThru
try {
    for ($i = 0; $i -lt 80 -and $process.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 250; $process.Refresh() }
    Start-Sleep -Seconds 3
    $window = $A::FromHandle($process.MainWindowHandle)

    $seen = @{}
    $missing = New-Object System.Collections.Generic.List[string]
    function Inspect([string] $where) {
        foreach ($element in $window.FindAll($T::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            $current = $element.Current
            if ($checked -notcontains $current.ControlType -or $current.IsOffscreen) { continue }
            if ($current.AutomationId -match $builtIn -or $current.Name -match $builtIn) { continue }
            # A list's own rows are content, not controls; the list itself is checked.
            if ($current.ClassName -eq 'ListViewItem' -and $current.ControlType -ne $C::TabItem) { continue }
            # Unnamed controls are kept apart per tab, so two of them cannot hide behind one entry.
            $key = "$($current.ControlType.ProgrammaticName)|$($current.Name)|$($current.AutomationId)"
            if (-not $current.Name) { $key += "|$where|$($current.BoundingRectangle)" }
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            $label = if ($current.Name) { "'$($current.Name)'" } else { '(no name)' }
            $kind = $current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            if (-not $current.HelpText -and $current.ControlType -ne $C::List) { $missing.Add("$where  $kind ${label}: no help text") }
            if (-not $current.Name) { $missing.Add("$where  $kind (no name): no name for a screen reader") }
        }
    }

    Inspect 'window'
    $tabs = $window.FindAll($T::Descendants, (New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), $C::TabItem))
    foreach ($tab in $tabs) {
        $name = $tab.Current.Name
        $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1500
        Inspect "[$name]"
    }

    "Checked {0} control(s) across the window and {1} tab(s)." -f $seen.Count, $tabs.Count
    if ($missing.Count -eq 0) { 'Every control has help text and a name.'; exit 0 }
    $missing
    exit 1
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; if (-not $process.WaitForExit(5000)) { $process.Kill() } }
}
