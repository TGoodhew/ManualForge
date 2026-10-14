<#
.SYNOPSIS
    Installs ManualForge in a Windows Sandbox, checks it, uninstalls it, and reports what is left (#26).

.DESCRIPTION
    Run by test-in-sandbox.wsb when the sandbox logs on; tools\build-installer.ps1 writes that file.
    The sandbox is a clean Windows with no GPU, no .NET and no Windows App Runtime, so this tests the
    CPU path, chaining in both runtimes, the Start menu entry, and that uninstalling leaves nothing.

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
