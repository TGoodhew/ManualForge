<#
.SYNOPSIS
    Installs ManualForge in a Windows Sandbox, checks it, uninstalls it, and reports what is left (#26).

.DESCRIPTION
    Run by test-in-sandbox.wsb when the sandbox logs on; tools\build-installer.ps1 writes that file.
    The sandbox is a clean Windows with no GPU, no .NET and no Windows App Runtime, so this tests the
    CPU path, chaining in both runtimes, the Start menu entry, and that uninstalling leaves nothing. It also
    walks the wizard to the library page, with and without GPIB MCP and on an upgrade, and cancels (#83).

    Reads the installer from C:\Installer (read-only) and writes everything to C:\Results, which is a
    folder on the host: transcript.txt, the installer's logs, ocr.txt, and summary.txt last of all, which
    says the run has finished. A sample.pdf beside the installer, if there is one, is OCR'd on the CPU.
    Exits 1 if a check failed, else 0; the .wsb then shuts the sandbox down either way.
    tools\invoke-sandbox-test.ps1 runs it from the host.

    Windows PowerShell 5.1: it is what a clean Windows has.
#>
$ErrorActionPreference = 'Continue'
$installerDir = 'C:\Installer'
$results = 'C:\Results'
$library = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Manuals'
$app = Join-Path $env:LOCALAPPDATA 'Programs\ManualForge'
$cli = Join-Path $app 'manualforge.exe'
$checks = New-Object System.Collections.Generic.List[string]
$exitCode = 1   # until the summary is written

Start-Transcript -Path (Join-Path $results 'transcript.txt') -Force | Out-Null

function Write-Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Check([string] $name, [bool] $passed, [string] $detail = '') {
    $line = '{0}  {1}{2}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { " - $detail" } else { '' })
    $checks.Add($line)
    Write-Host $line -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
}

function Invoke-Cli([string[]] $arguments) {
    Write-Host "> manualforge $($arguments -join ' ')"
    $output = & $cli @arguments 2>&1 | Out-String
    Write-Host $output
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output }
}

function Get-Leftovers {
    $found = @()
    if (Test-Path $app) { $found += "program folder $app" }
    if (Test-Path (Join-Path $env:LOCALAPPDATA 'ManualForge')) { $found += "data folder $env:LOCALAPPDATA\ManualForge" }
    if (Test-Path 'HKCU:\Software\ManualForge') { $found += 'registry HKCU\Software\ManualForge' }
    if ((Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue).MANUALFORGE_LIBRARY) { $found += 'environment MANUALFORGE_LIBRARY' }
    if (((Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue).Path) -like "*$app*") { $found += 'PATH entry' }
    if (Test-Path $shortcut) { $found += "shortcut $shortcut" }
    if (Test-Path $uninstallKey) { $found += 'Apps & features entry' }
    return $found
}

# ---- the wizard's library page (#83), driven by window messages

# Inno Setup's controls are its own classes, which UI Automation reports as plain panes with no patterns: it
# can find them by class and caption, but not press or fill them. So they are found through UI Automation and
# worked with the window messages a click or a typed value sends. As GPIB MCP's sandbox test does.
$gpibKey = 'HKCU:\Software\GpibMcp'
$pageQuestion = 'Which folder holds the PDF manuals'
$BM_CLICK = 0xF5; $WM_SETTEXT = 0x0C; $WM_GETTEXT = 0x0D

function Initialize-WizardDriver {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    if (-not ('WizardWindow' -as [type])) {
        Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class WizardWindow {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
'@
    }
}

# Ends any Setup still running - the .tmp that shows the wizard and the .exe that started it.
function Stop-Setup {
    Get-Process | Where-Object { $_.ProcessName -like 'ManualForge-Setup-*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# What the wizard is showing: the current page (other pages are off screen) and any message box. Inno's own
# message boxes are windows of their own, not under the wizard's, so every window of the process is looked at.
function Get-Shown($wizard) {
    $mine = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $wizard.Id
    foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $mine)) {
        $window
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { -not $_.Current.IsOffscreen }
    }
}

function Get-Texts($wizard) { (Get-Shown $wizard | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join "`r`n" }

function Find-Control($wizard, [string] $caption) {
    Get-Shown $wizard | Where-Object { ($_.Current.Name -replace '&', '') -like $caption -and $_.Current.IsEnabled } | Select-Object -First 1
}

# Posted, not sent: a click that opens a message box would otherwise wait until someone closed it.
function Invoke-Button($wizard, [string] $caption) {
    $control = Find-Control $wizard $caption
    if (-not $control) { return $false }
    [void][WizardWindow]::PostMessage([IntPtr]$control.Current.NativeWindowHandle, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
    return $true
}

function Get-LibraryBox($wizard) {
    $box = Get-Shown $wizard.Process | Where-Object { $_.Current.ClassName -like '*Edit*' } | Select-Object -First 1
    [IntPtr]$box.Current.NativeWindowHandle
}

function Get-LibraryBoxValue($wizard) {
    $text = New-Object System.Text.StringBuilder 1024
    [void][WizardWindow]::SendMessage((Get-LibraryBox $wizard), $WM_GETTEXT, [IntPtr]1024, $text)
    $text.ToString()
}

function Set-LibraryBox($wizard, [string] $value) {
    [void][WizardWindow]::SendMessage((Get-LibraryBox $wizard), $WM_SETTEXT, [IntPtr]::Zero, $value)
}

# Starts the wizard and clicks Next until the library page shows. Returns its process, or $null.
function Open-LibraryPage([string] $label, [string[]] $more = @()) {
    Initialize-WizardDriver
    Start-Process $setup.FullName -ArgumentList (@("/LOG=`"$results\wizard-$label.log`"") + $more) | Out-Null
    $process = $null
    for ($i = 0; $i -lt 60 -and -not $process; $i++) {
        $process = Get-Process | Where-Object { $_.ProcessName -like 'ManualForge-Setup-*' -and $_.Path -like '*.tmp' -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if (-not $process) { Start-Sleep -Seconds 1 }
    }
    if (-not $process) { Check "$label - the wizard opens" $false; Stop-Setup; return $null }
    for ($i = 0; $i -lt 8 -and (Get-Texts $process) -notmatch $pageQuestion; $i++) {
        # The licence page holds Next back until the agreement is accepted.
        Invoke-Button $process 'I accept*' | Out-Null
        if (-not (Invoke-Button $process 'Next')) { Start-Sleep -Seconds 1 }
    }
    if ((Get-Texts $process) -notmatch $pageQuestion) { Check "$label - the wizard reaches the library page" $false; Stop-Setup; return $null }
    return [pscustomobject]@{ Process = $process }
}

function Close-Wizard($wizard) {
    # Cancel, then "Exit Setup?" - Yes.
    if (Invoke-Button $wizard.Process 'Cancel') { Invoke-Button $wizard.Process 'Yes' | Out-Null }
    if (-not $wizard.Process.WaitForExit(20000)) { Stop-Setup }
}

# GPIB MCP as its installer leaves it: the key with a Manuals value ('' for none), or no key at all ($null).
function Set-GpibMcp($manuals) {
    Remove-Item $gpibKey -Recurse -Force -ErrorAction SilentlyContinue
    if ($null -ne $manuals) {
        New-Item -Path $gpibKey -Force | Out-Null
        Set-ItemProperty $gpibKey -Name Manuals -Value $manuals
    }
}

# Opens the library page, checks what its box starts with, and cancels. $expected '' means the box must be
# empty, and then Next must refuse to go on until a folder is typed.
function Test-LibraryPage([string] $label, [string] $expected, [bool] $saysGpib, [string[]] $more = @()) {
    $wizard = Open-LibraryPage $label $more
    if (-not $wizard) { return }
    $text = Get-Texts $wizard.Process
    $value = Get-LibraryBoxValue $wizard
    Check "$label - the box starts with $(if ($expected) { $expected } else { 'nothing' })" ($value -eq $expected) "'$value'"
    Check "$label - the page $(if ($saysGpib) { 'says' } else { 'does not say' }) it is GPIB MCP's folder" (($text -match 'This is GPIB MCP') -eq $saysGpib)
    if (-not $expected) {
        Invoke-Button $wizard.Process 'Next' | Out-Null
        # The message box shows under the wizard's window.
        $said = Get-Texts $wizard.Process
        Check "$label - Next with an empty box says a folder is needed" ($said -match 'needs a folder of manuals') (($said -split "`r`n" | Select-String 'folder') -join ' / ')
        Invoke-Button $wizard.Process 'OK' | Out-Null
        Check "$label - ... and stays on the page" ((Get-Texts $wizard.Process) -match $pageQuestion)
        Set-LibraryBox $wizard 'C:\Manuals-Typed'
        Invoke-Button $wizard.Process 'Next' | Out-Null
        Check "$label - with a folder typed, Next goes on" ((Get-Texts $wizard.Process) -notmatch $pageQuestion)
    }
    Close-Wizard $wizard
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ManualForge.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A6AE7B23-BB07-4BEE-9AD0-0B669785EDE5}_is1'

try {
    Write-Step 'Before'
    $setup = Get-ChildItem $installerDir -Filter 'ManualForge-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "Installer : $($setup.Name)"
    Write-Host "Windows   : $([Environment]::OSVersion.VersionString)"
    Write-Host "User      : $env:USERNAME"
    $before = Get-Leftovers
    Write-Host "Already present: $(if ($before) { $before -join '; ' } else { 'nothing' })"
    New-Item -ItemType Directory -Force $library | Out-Null
    $testStarted = Get-Date
    $gpibFolder = 'C:\Manuals-Gpib'; $switchFolder = 'C:\Manuals-Switch'

    Write-Step 'The library page: GPIB MCP installed with a manual folder'
    Set-GpibMcp $gpibFolder
    Test-LibraryPage 'gpib' $gpibFolder $true
    Test-LibraryPage 'gpib-switch' $switchFolder $false @("/LIBRARY=`"$switchFolder`"")
    Write-Step 'The library page: GPIB MCP not installed'
    Set-GpibMcp $null
    Test-LibraryPage 'no-gpib' '' $false
    Test-LibraryPage 'no-gpib-switch' $switchFolder $false @("/LIBRARY=`"$switchFolder`"")
    Write-Step 'The library page: GPIB MCP installed without a manual folder'
    Set-GpibMcp ''
    Test-LibraryPage 'gpib-none' '' $false
    Test-LibraryPage 'gpib-none-switch' $switchFolder $false @("/LIBRARY=`"$switchFolder`"")
    Stop-Setup

    Write-Step 'A silent install with no folder to use'
    Set-GpibMcp $null
    $refused = Start-Process $setup.FullName -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$results\install-no-library.log`"")
    Check 'it stops, and says why in its log' ($refused.ExitCode -ne 0 -and (Select-String -Path "$results\install-no-library.log" -Pattern 'needs a folder of manuals' -Quiet)) "code $($refused.ExitCode)"
    Check '... before installing anything' (-not (Test-Path $app) -and -not (Test-Path 'HKCU:\Software\ManualForge'))

    Write-Step 'Installing silently'
    $started = Get-Date
    $install = Start-Process $setup.FullName -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=mcp',
        "/LIBRARY=`"$library`"", "/LOG=`"$results\install.log`"")
    $minutes = ((Get-Date) - $started).TotalMinutes
    Check 'installer exit code 0' ($install.ExitCode -eq 0) ("code {0}, {1:N1} min" -f $install.ExitCode, $minutes)
    # Setup succeeds whatever its [Run] steps return, so look at what they returned.
    $runCodes = Select-String -Path "$results\install.log" -Pattern 'Process exit code: (\d+)' | ForEach-Object { [int] $_.Matches[0].Groups[1].Value }
    Check "installer's own steps exit 0" (-not ($runCodes | Where-Object { $_ -ne 0 })) ("codes " + ($runCodes -join ', '))

    Write-Step 'What the installer put on the machine'
    Check 'manualforge.exe present' (Test-Path $cli)
    Check 'ManualForge.App.exe present' (Test-Path (Join-Path $app 'ManualForge.App.exe'))
    $files = Get-ChildItem $app -Recurse -File -ErrorAction SilentlyContinue
    Write-Host ("Program folder: {0} files, {1:N0} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

    $runtimes = & "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes 2>&1 | Out-String
    Write-Host $runtimes
    Check '.NET 10 Desktop Runtime installed' ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.')
    $appRuntime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' -ErrorAction SilentlyContinue
    Check 'Windows App Runtime 1.8 installed' ([bool] $appRuntime) (($appRuntime | ForEach-Object { "$($_.Name) $($_.Version) $($_.Architecture)" }) -join ', ')

    Check 'Start menu shortcut' (Test-Path $shortcut)
    if (Test-Path $shortcut) {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
        Check 'shortcut points at the app' ($target -eq (Join-Path $app 'ManualForge.App.exe')) $target
    }
    $state = Get-ItemProperty 'HKCU:\Software\ManualForge' -ErrorAction SilentlyContinue
    Check 'library recorded from /LIBRARY=' ($state.Library -eq $library) "$($state.Library)"
    $variable = (Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue).MANUALFORGE_LIBRARY
    Check 'MANUALFORGE_LIBRARY set' ($variable -eq $library) "$variable"
    Check 'Apps & features entry' (Test-Path $uninstallKey)

    Write-Step 'The command line'
    $version = Invoke-Cli @('version')
    Check 'manualforge version' ($version.Code -eq 0 -and $version.Output -match 'manualforge\s+\S+') (($version.Output -split "`n")[0].Trim())
    $gpu = Invoke-Cli @('gpu')
    Check 'manualforge gpu runs (no GPU here)' ($gpu.Code -eq 0) "exit $($gpu.Code)"
    Check 'it says plainly there is no NVIDIA GPU, and runs on the CPU' ($gpu.Output -match 'Active provider\s*:\s*Cpu' -and $gpu.Output -match 'No NVIDIA GPU was found') (($gpu.Output -split "`n" | Select-String 'No NVIDIA|Active provider') -join ' / ')
    $setupGpu = Invoke-Cli @('setup', 'gpu', '--plan')
    Check 'setup gpu says there is nothing to download' ($setupGpu.Code -eq 0 -and $setupGpu.Output -match 'no NVIDIA GPU found') (($setupGpu.Output -split "`n" | Select-String 'GPU\s+:') -join '')
    Check 'the models were fetched during setup' (Test-Path (Join-Path $env:LOCALAPPDATA 'ManualForge\models\PP-OCRv5_mobile_det.onnx'))
    $status = Invoke-Cli @('status', $library)
    Check 'manualforge status' ($status.Code -eq 0) "exit $($status.Code)"
    $mcp = Invoke-Cli @('setup', 'mcp', '--list')
    Check 'manualforge setup mcp --list' ($mcp.Code -eq 0) "exit $($mcp.Code)"

    $sample = Join-Path $installerDir 'sample.pdf'
    if (Test-Path $sample) {
        Write-Step 'Reading two pages on the CPU'
        $started = Get-Date
        $ocr = Invoke-Cli @('ocr', $sample, '--pages', '1-2', '--engine', 'cpu', '--out', "$results\ocr.pdf", '--text', "$results\ocr.txt")
        $words = if (Test-Path "$results\ocr.txt") { ((Get-Content "$results\ocr.txt" -Raw) -split '\s+' | Where-Object { $_ }).Count } else { 0 }
        Check 'OCR on the CPU' ($ocr.Code -eq 0 -and $words -gt 0) ("exit {0}, {1} words, {2:N1} min" -f $ocr.Code, $words, ((Get-Date) - $started).TotalMinutes)
    }
    else { Write-Host 'No sample.pdf beside the installer; OCR not tried.' }

    Write-Step 'The desktop app'
    $window = Start-Process (Join-Path $app 'ManualForge.App.exe') -PassThru
    Start-Sleep -Seconds 15
    $window.Refresh()
    Check 'ManualForge.App starts and stays up' (-not $window.HasExited) $(if ($window.HasExited) { "exited with $($window.ExitCode)" } else { "window '$($window.MainWindowTitle)'" })
    if (-not $window.HasExited) {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save("$results\app.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $window.CloseMainWindow() | Out-Null
        if (-not $window.WaitForExit(10000)) { $window.Kill() }
    }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = $testStarted } -ErrorAction SilentlyContinue |
        Select-Object -First 10 | Format-List TimeCreated, ProviderName, Message | Out-String | Write-Host

    Write-Step 'The library page on an upgrade'
    Set-GpibMcp $null
    Test-LibraryPage 'upgrade' $library $false
    Set-GpibMcp $gpibFolder
    Test-LibraryPage 'upgrade-gpib' $gpibFolder $true
    Test-LibraryPage 'upgrade-switch' $switchFolder $false @("/LIBRARY=`"$switchFolder`"")
    Set-GpibMcp $null
    Stop-Setup

    Write-Step 'Uninstalling silently'
    $uninstaller = Get-ChildItem $app -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'uninstaller present' ([bool] $uninstaller)
    if ($uninstaller) {
        $uninstall = Start-Process $uninstaller.FullName -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$results\uninstall.log`"")
        Check 'uninstaller exit code 0' ($uninstall.ExitCode -eq 0) "code $($uninstall.ExitCode)"
        # The uninstaller copies itself to %TEMP% and returns before that copy has finished.
        for ($i = 0; $i -lt 60 -and (Test-Path $app); $i++) { Start-Sleep -Seconds 1 }
    }
    $left = Get-Leftovers
    Check 'nothing left after uninstalling' (-not $left) ($left -join '; ')
    Check 'library folder untouched' (Test-Path $library)
}
catch {
    Check 'script ran to the end' $false $_.Exception.Message
}
finally {
    Stop-Transcript | Out-Null
    $failed = @($checks | Where-Object { $_ -like 'FAIL*' }).Count
    @("ManualForge sandbox test, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "$($checks.Count) checks, $failed failed", '') + $checks |
        Set-Content -Path (Join-Path $results 'summary.txt') -Encoding UTF8
    $exitCode = [int] ($failed -gt 0)
}
# Said before going, so a sandbox that doesn't close shows whether this got here. Environment.Exit, not exit:
# nothing this started that is still running can then hold PowerShell open (#84).
Set-Content -Path (Join-Path $results 'exited.txt') -Value "exit $exitCode at $(Get-Date -Format 'HH:mm:ss')" -Encoding UTF8
[Environment]::Exit($exitCode)
