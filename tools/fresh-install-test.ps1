<#
.SYNOPSIS
    Tests ManualForge's installer on a freshly installed Windows PC, and gathers everything worth
    knowing about how it went (#26).

.DESCRIPTION
    Run it as yourself, NOT as administrator, from a normal PowerShell window:

        powershell -NoProfile -ExecutionPolicy Bypass -Command "iwr https://raw.githubusercontent.com/TGoodhew/ManualForge/main/tools/fresh-install-test.ps1 -OutFile $env:TEMP\fresh-install-test.ps1 -UseBasicParsing; & $env:TEMP\fresh-install-test.ps1"

    What it does, in order. You are asked for something only where it says so:
      1. Fetches a portable GitHub CLI into its own folder (nothing is installed), and signs you in to
         GitHub in the browser if it is not signed in already.
      2. Records the PC as it is: Windows, CPU, memory, GPU and driver, and which runtimes are present.
      3. Downloads the newest draft installer (tag installer-test-*) and a sample PDF from GitHub,
         and checks the installer against the SHA-256 GitHub holds for it.
      4. Starts the installer for you to click through - YOU answer its pages and Windows' prompts.
      5. Checks what was installed, then reads the sample with the command line on the CPU and, when
         there is an NVIDIA GPU, on the GPU; runs survey/run/index/search on a small test library;
         asks the MCP server for its tools and a search; starts the app from the Start menu.
      6. Upgrades over the running install - YOU start Claude Desktop first, so its MCP server is
         running - and checks that Setup named and stopped it.
      7. Asks whether to uninstall, and if so - YOU click through the uninstaller - checks what is left,
         and that every AI app's settings, your user environment and the test library are as they were.
      8. Zips the results to the Desktop and attaches them to the draft release.

    Only ManualForge's own state and the PC's description are collected. The screenshot is of the
    ManualForge window only. Nothing ManualForge itself needs (.NET, the Visual C++ runtime, the
    Windows App Runtime, NVIDIA's libraries) is installed by this script: that is the installer's job,
    and what is being tested.

.PARAMETER Tag
    The draft release to test. Default: the newest whose tag starts installer-test-.

.PARAMETER Installer
    Use this installer instead of downloading one. With -Sample, needs no GitHub at all.

.PARAMETER Sample
    Use this PDF instead of downloading sample.pdf.

.PARAMETER Silent
    Install and uninstall without the wizard (/VERYSILENT, MCP task only; the NVIDIA licences cannot
    be accepted silently, so no GPU libraries). For rehearsing the script in Windows Sandbox.

.PARAMETER WithoutTensorRt
    Install the GPU libraries without TensorRT, and check that ManualForge then runs on CUDA.

.PARAMETER Unattended
    Ask nothing: uninstall at the end without asking.

.PARAMETER NoUpload
    Leave the results on the Desktop only.

.PARAMETER ResultsRoot
    Where the results folder goes. Default: the Desktop.
#>
[CmdletBinding()]
param(
    [string] $Tag,
    [string] $Installer,
    [string] $Sample,
    [switch] $Silent,
    [switch] $WithoutTensorRt,
    [switch] $Unattended,
    [switch] $NoUpload,
    [string] $ResultsRoot = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell downloads crawl with a progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'TGoodhew/ManualForge'
$AppId = '{A6AE7B23-BB07-4BEE-9AD0-0B669785EDE5}'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = Join-Path $ResultsRoot "ManualForge-FreshInstallTest-$env:COMPUTERNAME-$stamp"
$work = Join-Path $env:LOCALAPPDATA 'ManualForge-FreshInstallTest'
$downloads = Join-Path $work "downloads-$stamp"
$library = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ManualForge Test Library'
$app = Join-Path $env:LOCALAPPDATA 'Programs\ManualForge'
$cli = Join-Path $app 'manualforge.exe'
$data = Join-Path $env:LOCALAPPDATA 'ManualForge'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ManualForge.lnk'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${AppId}_is1"
$searchWord = 'HANDSHAKE'   # on page 1 of the sample

New-Item -ItemType Directory -Force $results, $work, $downloads | Out-Null
$checks = New-Object System.Collections.Generic.List[string]
$timeline = New-Object System.Collections.Generic.List[string]
$scriptStarted = Get-Date
Start-Transcript -Path (Join-Path $results 'transcript.txt') -Force | Out-Null

# ---------------------------------------------------------------- helpers

function Write-Step([string] $text) {
    $line = '{0:HH:mm:ss}  {1}' -f (Get-Date), $text
    $timeline.Add($line)
    Write-Host ''
    Write-Host "== $line" -ForegroundColor Cyan
}

function Check([string] $name, [bool] $passed, [string] $detail = '') {
    $line = '{0}  {1}{2}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { " - $detail" } else { '' })
    $checks.Add($line)
    Write-Host $line -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
}

function Note([string] $name, [string] $detail) {
    $line = "NOTE  $name - $detail"
    $checks.Add($line)
    Write-Host $line -ForegroundColor Yellow
}

function Say([string] $text) { Write-Host $text -ForegroundColor Yellow }

function Ask([string] $question, [bool] $default) {
    if ($Unattended) { return $default }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$question $hint"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer -match '^[Yy]'
}

# Runs a program, saves what it printed to <name>.txt in the results, and says how long it took.
function Invoke-Logged([string] $name, [string] $exe, [string[]] $arguments) {
    Write-Host "> $(Split-Path $exe -Leaf) $($arguments -join ' ')"
    $started = Get-Date
    $output = & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Out-String
    $code = $LASTEXITCODE
    $seconds = ((Get-Date) - $started).TotalSeconds
    Set-Content -Path (Join-Path $results "$name.txt") -Value $output -Encoding UTF8
    Write-Host $output
    return [pscustomobject]@{ Code = $code; Output = $output; Seconds = $seconds }
}

function Get-FolderMB([string] $path) {
    if (-not (Test-Path $path)) { return 0 }
    return [math]::Round(((Get-ChildItem $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB), 1)
}

function Save-Text([string] $name, $value) {
    $value | Out-String -Width 250 | Set-Content -Path (Join-Path $results $name) -Encoding UTF8
}

function Get-Runtimes {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    $vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
    [pscustomobject]@{
        DotNet             = if (Test-Path $dotnet) { (& $dotnet --list-runtimes 2>&1 | Out-String).Trim() } else { '(no dotnet.exe)' }
        VisualCpp          = if ($vc -and $vc.Installed -eq 1) { $vc.Version } else { '(none)' }
        WindowsAppRuntime  = ((Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue |
                               ForEach-Object { "$($_.Name) $($_.Version) $($_.Architecture)" }) -join '; ')
    }
}

# Files of AI apps that name the ManualForge MCP server. Only the paths are recorded, not what is in them.
function Find-McpReferences {
    $places = @(
        "$env:APPDATA\Claude", "$env:APPDATA\Code\User", "$env:APPDATA\Code - Insiders\User",
        "$env:USERPROFILE\.claude.json", "$env:USERPROFILE\.cursor", "$env:USERPROFILE\.codeium",
        "$env:USERPROFILE\.gemini", "$env:USERPROFILE\.codex", "$env:USERPROFILE\.continue",
        "$env:USERPROFILE\.lmstudio", "$env:USERPROFILE\.mcp.json", "$env:LOCALAPPDATA\Microsoft\VisualStudio"
    )
    $places += Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude' }
    $files = foreach ($place in $places) {
        if (Test-Path $place -PathType Leaf) { Get-Item $place }
        elseif (Test-Path $place) {
            Get-ChildItem $place -Recurse -Depth 3 -File -Include *.json, *.toml, *.yaml, *.yml -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -lt 5MB -and $_.Name -notlike '*.before-manualforge.bak' }
        }
    }
    @($files | Where-Object { Select-String -LiteralPath $_.FullName -Pattern 'ManualForge\.Mcp' -Quiet -ErrorAction SilentlyContinue } |
        ForEach-Object { $_.FullName })
}

# Every settings file `manualforge setup mcp` can write, as McpClients knows them.
function Get-McpConfigFiles {
    $store = Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | Select-Object -First 1
    $files = [ordered]@{
        'claude-desktop'  = "$env:APPDATA\Claude\claude_desktop_config.json"
        'claude-code'     = "$env:USERPROFILE\.claude.json"
        'vscode'          = "$env:APPDATA\Code\User\mcp.json"
        'vscode-insiders' = "$env:APPDATA\Code - Insiders\User\mcp.json"
        'visual-studio'   = "$env:USERPROFILE\.mcp.json"
        'cursor'          = "$env:USERPROFILE\.cursor\mcp.json"
        'windsurf'        = "$env:USERPROFILE\.codeium\windsurf\mcp_config.json"
        'gemini'          = "$env:USERPROFILE\.gemini\settings.json"
        'codex'           = "$env:USERPROFILE\.codex\config.toml"
        'lmstudio'        = "$env:USERPROFILE\.lmstudio\mcp.json"
        'continue'        = "$env:USERPROFILE\.continue\mcpServers\manualforge.yaml"
    }
    if ($store) { $files['claude-desktop-store'] = Join-Path $store.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
    return $files
}

# What each settings file holds: its bytes' hash and, for JSON, its servers. An app rewrites its own
# settings while it runs (Claude Code counts its starts), so a changed hash with the same servers is
# the app's doing, not ManualForge's.
function Get-McpConfigState {
    $state = [ordered]@{}
    foreach ($entry in (Get-McpConfigFiles).GetEnumerator()) {
        $path = $entry.Value
        if (-not (Test-Path -LiteralPath $path)) { $state[$entry.Key] = $null; continue }
        $servers = $null
        if ($path -like '*.json') {
            try {
                $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $servers = (@($json.mcpServers, $json.servers) | Where-Object { $_ } | ConvertTo-Json -Depth 10 -Compress)
            } catch { $servers = '(unreadable)' }
        }
        $state[$entry.Key] = [pscustomobject]@{ Path = $path; Hash = (Get-FileHash -LiteralPath $path).Hash; Servers = $servers }
    }
    return $state
}

function Compare-McpConfigState($before, $after) {
    foreach ($key in $before.Keys) {
        $b = $before[$key]; $a = $after[$key]
        if (-not $b -and -not $a) { continue }
        if (-not $b) { Check "$key settings as before" $false "created and left behind: $($a.Path)"; continue }
        if (-not $a) { Check "$key settings as before" $false "deleted: $($b.Path)"; continue }
        if ($a.Hash -eq $b.Hash) { Check "$key settings as before" $true 'byte for byte'; continue }
        if ($b.Servers -and $a.Servers -eq $b.Servers) { Check "$key settings as before" $true 'same servers; the app changed its own settings meanwhile'; continue }
        Check "$key settings as before" $false "differs: $($a.Path)"
    }
}

function Get-UserEnvironment {
    $values = Get-ItemProperty 'HKCU:\Environment'
    $values.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name)=$($_.Value)" } | Sort-Object
}

function Get-FileHashes([string] $folder) {
    Get-ChildItem -LiteralPath $folder -Recurse -File -Force -ErrorAction SilentlyContinue |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.FullName.Substring($folder.Length + 1) } | Sort-Object
}

# How each app would start ManualForge's MCP server: the command and environment its settings name.
function Get-McpLaunches {
    foreach ($entry in (Get-McpConfigFiles).GetEnumerator()) {
        $path = $entry.Value
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $command = $null; $arguments = @(); $variables = @{}
        if ($path -like '*.json') {
            try { $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { continue }
            $server = @($json.mcpServers.manualforge, $json.servers.manualforge) | Where-Object { $_ } | Select-Object -First 1
            if (-not $server) { continue }
            $command = $server.command
            $arguments = @($server.args)
            if ($server.env) { $server.env.PSObject.Properties | ForEach-Object { $variables[$_.Name] = $_.Value } }
        }
        elseif ($path -like '*.toml') {
            $text = Get-Content -LiteralPath $path -Raw
            if ($text -notmatch "(?ms)^\[mcp_servers\.manualforge\]\s*\r?\ncommand = '([^']+)'") { continue }
            $command = $Matches[1]
            if ($text -match "MANUALFORGE_LIBRARY = '([^']+)'") { $variables['MANUALFORGE_LIBRARY'] = $Matches[1] }
        }
        elseif ($path -like '*.yaml') {
            $text = Get-Content -LiteralPath $path -Raw
            if ($text -match 'command: "([^"]+)"') { $command = $Matches[1] -replace '\\\\', '\' }
            if ($text -match 'MANUALFORGE_LIBRARY: "([^"]+)"') { $variables['MANUALFORGE_LIBRARY'] = $Matches[1] -replace '\\\\', '\' }
        }
        if ($command) { [pscustomobject]@{ Client = $entry.Key; Command = $command; Arguments = $arguments; Environment = $variables } }
    }
}

function Get-Leftovers {
    $found = @()
    if (Test-Path $app) { $found += "program folder $app" }
    if (Test-Path $data) { $found += "data folder $data ($(Get-FolderMB $data) MB)" }
    if (Test-Path 'HKCU:\Software\ManualForge') { $found += 'registry HKCU\Software\ManualForge' }
    $environment = Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue
    if ($environment.MANUALFORGE_LIBRARY) { $found += 'environment MANUALFORGE_LIBRARY' }
    if ("$($environment.Path)" -like "*$app*") { $found += 'PATH entry' }
    if (Test-Path $shortcut) { $found += "Start menu shortcut" }
    if (Test-Path $uninstallKey) { $found += 'Apps entry' }
    return $found
}

# A screenshot of one window only, so nothing else on the screen is collected.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class FreshWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
function Save-WindowShot([IntPtr] $window, [string] $path) {
    [FreshWindow]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds 700
    $rect = New-Object FreshWindow+Rect
    if (-not [FreshWindow]::GetWindowRect($window, [ref] $rect)) { return $false }
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $false }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $true
}

# Starts a program the way Explorer would, so SmartScreen and UAC behave as they do for a user, and
# waits for that process only (not for the app the installer may start at the end).
function Start-AndWait([string] $exe, [string] $arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $exe
    $info.Arguments = $arguments
    $info.UseShellExecute = $true
    try { $process = [System.Diagnostics.Process]::Start($info) }
    catch { Write-Host "Did not start: $($_.Exception.Message)" -ForegroundColor Red; return $null }   # e.g. "Don't run" at SmartScreen
    if (-not $process) { return $null }
    $process.WaitForExit()
    return $process.ExitCode
}

# Setup and the uninstaller each hand over to a copy of themselves in %TEMP% (is-*.tmp\ManualForge-Setup-*.tmp,
# and _unins.tmp in Inno Setup 6.7, _iu*.tmp in older ones). The uninstaller's copy outlives the original,
# and asks about the data folder after it has gone.
function Wait-ForSetupCopies {
    $deadline = (Get-Date).AddMinutes(30)
    while ((Get-Date) -lt $deadline -and (Get-Process | Where-Object { $_.Name -like '_unins*' -or $_.Name -like '_iu*' -or $_.Name -like 'ManualForge-Setup-*' })) { Start-Sleep -Seconds 1 }
}

# A minimal MCP client over stdio: initialize, list the tools, call two of them. Started the way an
# app's settings say: this command, these arguments, these variables, and nothing else of ours.
function Test-McpServer([string] $server, [string[]] $arguments, [hashtable] $variables, [string] $name = 'mcp-exchange') {
    $info = New-Object System.Diagnostics.ProcessStartInfo $server
    $info.Arguments = (@($arguments) | Where-Object { $_ } | ForEach-Object { "`"$_`"" }) -join ' '
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.EnvironmentVariables.Remove('MANUALFORGE_LIBRARY')
    foreach ($key in $variables.Keys) { $info.EnvironmentVariables[$key] = $variables[$key] }
    $process = [System.Diagnostics.Process]::Start($info)
    $stderr = $process.StandardError.ReadToEndAsync()
    $exchange = New-Object System.Collections.Generic.List[string]
    $send = {
        param($message)
        $json = $message | ConvertTo-Json -Compress -Depth 10
        $exchange.Add(">> $json")
        $process.StandardInput.WriteLine($json); $process.StandardInput.Flush()
    }
    $receive = {
        param($id)
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(60000) -or $null -eq $read.Result) { break }
            $exchange.Add("<< $($read.Result)")
            $reply = $read.Result | ConvertFrom-Json
            if ($reply.id -eq $id) { return $reply }
        }
        return $null
    }
    try {
        & $send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'fresh-install-test'; version = '1' } } }
        $init = & $receive 1
        & $send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        & $send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        $tools = & $receive 2
        & $send @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'library_status'; arguments = @{} } }
        $status = & $receive 3
        & $send @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'library_search'; arguments = @{ query = $searchWord } } }
        $search = & $receive 4
    }
    finally {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.WaitForExit(10000)) { $process.Kill() }
        $exchange.Add('-- stderr --'); $exchange.Add($stderr.Result)
        Save-Text "$name.txt" $exchange
    }
    [pscustomobject]@{
        Initialized = [bool] $init.result
        Tools       = @($tools.result.tools | ForEach-Object { $_.name })
        StatusText  = (@($status.result.content | ForEach-Object { $_.text }) -join "`n")
        SearchText  = (@($search.result.content | ForEach-Object { $_.text }) -join "`n")
    }
}

$gh = $null
function Get-PortableGh {
    $folder = Join-Path $work 'gh'
    $exe = Join-Path $folder 'bin\gh.exe'
    if (Test-Path $exe) { return $exe }
    $release = Invoke-RestMethod 'https://api.github.com/repos/cli/cli/releases/latest' -UseBasicParsing
    $version = $release.tag_name.TrimStart('v')
    $zipName = "gh_${version}_windows_amd64.zip"
    $zipAsset = $release.assets | Where-Object name -eq $zipName
    $sumAsset = $release.assets | Where-Object name -eq "gh_${version}_checksums.txt"
    $zip = Join-Path $work $zipName
    Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing
    $sumsFile = Join-Path $work 'gh-checksums.txt'
    Invoke-WebRequest $sumAsset.browser_download_url -OutFile $sumsFile -UseBasicParsing
    $sums = Get-Content $sumsFile -Raw
    $expected = ($sums -split "`n" | Where-Object { $_ -match [regex]::Escape($zipName) } | Select-Object -First 1) -replace '\s.*$', ''
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "GitHub CLI download does not match its published checksum." }
    Expand-Archive $zip -DestinationPath $folder -Force
    Remove-Item -LiteralPath $zip -Force
    return $exe
}

# ---------------------------------------------------------------- 0. the tools this script needs

try {
    Write-Step 'Starting'
    Write-Host "Results go to $results"
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($Silent) { Note 'elevated' "$elevated (a silent rehearsal has no prompts to test)" }
    else {
        Check 'script not run as administrator' (-not $elevated) $(if ($elevated) { 'run from an ordinary window: the installer is per user, and its prompts are part of the test' } else { '' })
        if ($elevated -and -not (Ask 'This window is elevated. Carry on anyway?' $false)) { throw 'Stopped: run from a window that is not elevated.' }
    }

    $needGitHub = -not ($Installer -and $Sample) -or -not $NoUpload
    if ($needGitHub) {
        Write-Step 'GitHub CLI (portable, in its own folder; nothing is installed)'
        $gh = Get-PortableGh
        Write-Host (& $gh --version | Select-Object -First 1)
        & $gh auth status --hostname github.com 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Say 'Signing in to GitHub: a code is shown here, and the browser opens for you to enter it.'
            & $gh auth login --hostname github.com --git-protocol https --web
        }
        & $gh auth status --hostname github.com 2>&1 | Out-Null
        Check 'signed in to GitHub' ($LASTEXITCODE -eq 0)
        if ($LASTEXITCODE -ne 0) { throw 'Not signed in to GitHub, so the draft release cannot be read.' }
    }

    # ------------------------------------------------------------ 1. the PC as it is

    Write-Step 'This PC, before installing'
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpus = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor)
    $nvidiaSmi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
    $hasNvidiaAdapter = [bool] ($gpus | Where-Object { $_.Name -match 'NVIDIA' })
    $system = [ordered]@{
        Computer       = $env:COMPUTERNAME
        Windows        = "$($os.Caption) $($os.Version) build $($os.BuildNumber).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"
        DisplayVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion
        Installed      = $os.InstallDate
        Locale         = (Get-Culture).Name
        TimeZone       = (Get-TimeZone).Id
        Cpu            = "$($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) threads)"
        MemoryGB       = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        FreeDiskGB     = [math]::Round((Get-PSDrive ($env:LOCALAPPDATA.Substring(0, 1))).Free / 1GB, 1)
        PowerShell     = $PSVersionTable.PSVersion.ToString()
        Elevated       = $elevated
        UacLevel       = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).ConsentPromptBehaviorAdmin
        SmartScreen    = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' -ErrorAction SilentlyContinue).SmartScreenEnabled
    }
    try { $system.Defender = (Get-MpComputerStatus -ErrorAction Stop | Select-Object -ExpandProperty RealTimeProtectionEnabled) } catch { $system.Defender = 'unknown' }
    Save-Text 'system.txt' (([pscustomobject] $system | Format-List | Out-String) + ($gpus | Format-List | Out-String))
    ([pscustomobject] $system | Format-List | Out-String) + ($gpus | Format-Table Name, DriverVersion -AutoSize | Out-String) | Write-Host
    if (Test-Path $nvidiaSmi) { Invoke-Logged 'nvidia-smi-before' $nvidiaSmi @() | Out-Null }
    elseif ($hasNvidiaAdapter) {
        Note 'NVIDIA card but no nvidia-smi' 'the NVIDIA driver is not installed, so the installer will not offer the GPU libraries'
        if (Ask 'Stop here, to install the NVIDIA driver first? (No carries on without the GPU)' $true) { throw 'Stopped so the NVIDIA driver can be installed. Run this again afterwards.' }
    }

    $runtimesBefore = Get-Runtimes
    Save-Text 'runtimes-before.txt' ($runtimesBefore | Format-List)
    $runtimesBefore | Format-List | Out-String | Write-Host
    $mcpBefore = Find-McpReferences
    $mcpConfigBefore = Get-McpConfigState
    $environmentBefore = @(Get-UserEnvironment)
    Save-Text 'environment-before.txt' $environmentBefore
    $appsPresent = @()
    foreach ($candidate in @("$env:LOCALAPPDATA\AnthropicClaude", "$env:LOCALAPPDATA\Programs\Microsoft VS Code", "$env:ProgramFiles\Microsoft VS Code")) {
        if (Test-Path $candidate) { $appsPresent += $candidate }
    }
    $appsPresent += Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude*' -ErrorAction SilentlyContinue | ForEach-Object FullName
    Save-Text 'ai-apps-before.txt' (@('AI apps found:') + $appsPresent + @('', 'Files naming ManualForge.Mcp:') + $mcpBefore)

    $existing = Get-Leftovers
    if ($existing) {
        Note 'ManualForge already here' ($existing -join '; ')
        $uninstaller = Get-ChildItem $app -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($uninstaller -and (Ask 'ManualForge is already installed. Uninstall it first, so this is a clean test?' $true)) {
            Start-AndWait $uninstaller.FullName "/SILENT /SUPPRESSMSGBOXES /LOG=`"$results\uninstall-previous.log`"" | Out-Null
            Wait-ForSetupCopies
            for ($i = 0; $i -lt 60 -and (Test-Path $app); $i++) { Start-Sleep -Seconds 1 }
        }
    }

    # ------------------------------------------------------------ 2. the installer

    Write-Step 'Downloading the installer'
    if (-not $Installer -or -not $Sample) {
        if (-not $Tag) {
            $drafts = & $gh release list --repo $Repo --limit 50 --json tagName,isDraft,createdAt | ConvertFrom-Json |
                Where-Object { $_.tagName -like 'installer-test-*' } | Sort-Object createdAt -Descending
            $Tag = ($drafts | Select-Object -First 1).tagName
        }
        if (-not $Tag) { throw "No release tagged installer-test-* on $Repo." }
        Write-Host "Release: $Tag"
        $assets = (& $gh release view $Tag --repo $Repo --json assets | ConvertFrom-Json).assets
        if (-not $Installer) {
            & $gh release download $Tag --repo $Repo --pattern 'ManualForge-Setup-*.exe' --dir $downloads --clobber
            $Installer = (Get-ChildItem $downloads -Filter 'ManualForge-Setup-*.exe' | Select-Object -First 1).FullName
            $asset = $assets | Where-Object { $_.name -eq (Split-Path $Installer -Leaf) }
            $actual = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
            Check 'installer matches the SHA-256 GitHub holds' ("sha256:$actual" -eq $asset.digest) $actual
            # As if downloaded with a browser, so SmartScreen treats it as a user's download would be.
            Set-Content -LiteralPath $Installer -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://github.com/$Repo/releases" -ErrorAction SilentlyContinue
        }
        if (-not $Sample) {
            & $gh release download $Tag --repo $Repo --pattern 'sample.pdf' --dir $downloads --clobber
            $Sample = Join-Path $downloads 'sample.pdf'
        }
    }
    Write-Host "Installer: $Installer ($([math]::Round((Get-Item $Installer).Length / 1MB)) MB)"
    Write-Host "Sample   : $Sample"
    $signature = Get-AuthenticodeSignature $Installer
    Note 'installer signature' "$($signature.Status)"
    # The test library is this script's own folder, and the uninstaller rightly leaves it behind.
    # Start it empty, so that each run is a first run rather than a second pass over the last one.
    if (Test-Path -LiteralPath $library) { Remove-Item -LiteralPath $library -Recurse -Force }
    New-Item -ItemType Directory -Force $library | Out-Null
    Copy-Item -LiteralPath $Sample -Destination (Join-Path $library 'sample.pdf') -Force

    Write-Step 'Installing'
    if ($Silent) {
        $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=mcp /LIBRARY=`"$library`" /LOG=`"$results\install.log`""
    }
    else {
        $arguments = "/LIBRARY=`"$library`" /LOG=`"$results\install.log`""
        # Ticked for the user to see; only the GPU libraries, without TensorRT, and the AI apps.
        if ($WithoutTensorRt) { $arguments += ' /TASKS="gpu,mcp"' }
        Say ''
        Say 'The installer is starting. Please:'
        Say '  * If SmartScreen says "Windows protected your PC": More info, then Run anyway.'
        Say "  * Library folder: keep the one it suggests ($library) - it holds the test sample."
        if ($WithoutTensorRt) { Say '  * NVIDIA libraries: tick CUDA/cuDNN but leave TensorRT UNTICKED, and accept the licences.' }
        else { Say '  * If it offers the NVIDIA libraries: tick both (CUDA/cuDNN and TensorRT) and accept the licences.' }
        Say '  * Leave "Connect ManualForge to the AI apps" ticked. "Let a terminal run manualforge" is up to you.'
        Say '  * Say yes to the Windows permission prompts.'
        Say '  * At the end, untick "Start ManualForge" (this script starts it later), then Finish.'
        Say 'Everything is timed and logged; no need to note anything down.'
        Say ''
    }
    $installStarted = Get-Date
    $installCode = Start-AndWait $Installer $arguments
    Wait-ForSetupCopies
    $installMinutes = ((Get-Date) - $installStarted).TotalMinutes
    Check 'installer finished with exit code 0' ($installCode -eq 0) ("code {0}, {1:N1} min including your clicks" -f $installCode, $installMinutes)
    if ($installCode -ne 0 -and -not (Test-Path $cli)) { throw "The installer did not install ManualForge (exit code $installCode)." }

    if (Test-Path "$results\install.log") {
        $log = Get-Content "$results\install.log"
        $runCodes = @($log | Select-String 'Process exit code: (\d+)' | ForEach-Object { [int] $_.Matches[0].Groups[1].Value })
        Check "installer's own steps exit 0" (-not ($runCodes | Where-Object { $_ -ne 0 })) ("codes " + ($runCodes -join ', '))
        $log | Select-String 'Runtimes missing|Installing the (Visual|\.NET|Windows)|: exit code|Parameters:' | ForEach-Object { Note 'install log' ($_.Line -replace '^\S+ \S+\s+', '') }
    }

    # ------------------------------------------------------------ 3. what was installed

    Write-Step 'What the installer put on the PC'
    Check 'manualforge.exe present' (Test-Path $cli)
    Check 'ManualForge.App.exe present' (Test-Path (Join-Path $app 'ManualForge.App.exe'))
    Note 'program folder' ("{0} MB" -f (Get-FolderMB $app))
    $runtimesAfter = Get-Runtimes
    Save-Text 'runtimes-after-install.txt' ($runtimesAfter | Format-List)
    Check '.NET 10 Desktop Runtime present' ($runtimesAfter.DotNet -match 'Microsoft\.WindowsDesktop\.App 10\.')
    Check 'Visual C++ runtime 14.40 or later present' ($runtimesAfter.VisualCpp -match '^v?14\.(4\d|[5-9]\d)') $runtimesAfter.VisualCpp
    Check 'Windows App Runtime 1.8 present' ($runtimesAfter.WindowsAppRuntime -match 'WindowsAppRuntime\.1\.8') $runtimesAfter.WindowsAppRuntime
    Check 'Start menu shortcut' (Test-Path $shortcut)
    $state = Get-ItemProperty 'HKCU:\Software\ManualForge' -ErrorAction SilentlyContinue
    Save-Text 'registry-after-install.txt' $state
    Note 'library chosen' "$($state.Library)"
    Check 'MANUALFORGE_LIBRARY set' ([bool] (Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue).MANUALFORGE_LIBRARY)
    Check 'Apps entry' (Test-Path $uninstallKey)
    $gpuRecord = Join-Path $data 'gpu\installed.json'
    if (Test-Path $gpuRecord) {
        Copy-Item $gpuRecord (Join-Path $results 'gpu-installed.json')
        Note 'NVIDIA libraries' ("{0} MB in {1}" -f (Get-FolderMB (Join-Path $data 'gpu')), (Join-Path $data 'gpu'))
    }
    else { Note 'NVIDIA libraries' 'not installed' }

    Write-Step 'The command line'
    $version = Invoke-Logged 'version' $cli @('version')
    Check 'manualforge version' ($version.Code -eq 0) (($version.Output -split "`n")[0].Trim())
    $gpu = Invoke-Logged 'gpu' $cli @('gpu')
    Check 'manualforge gpu' ($gpu.Code -eq 0)
    $provider = if ($gpu.Output -match 'Active provider\s*:\s*(\S+)') { $Matches[1] } else { 'unknown' }
    Note 'active provider' $provider
    if ($hasNvidiaAdapter -and (Test-Path $gpuRecord)) {
        Check 'GPU in use after installing the NVIDIA libraries' ($provider -ne 'Cpu') $provider
        $withTensorRt = [bool] (Get-Content $gpuRecord -Raw | ConvertFrom-Json | Where-Object { $_.Name -eq 'TensorRT' })
        $expected = if ($withTensorRt) { 'TensorRt' } else { 'Cuda' }
        Check "runs on $expected, as the libraries chosen allow" ($provider -eq $expected) $provider
        if ($WithoutTensorRt) { Check 'TensorRT not downloaded, as chosen' (-not $withTensorRt) }
    }
    elseif ($hasNvidiaAdapter) { Note 'GPU' "no NVIDIA libraries installed, so the CPU: $provider" }
    if (-not $hasNvidiaAdapter) {
        Check 'no NVIDIA card: says it runs on the CPU' ($provider -eq 'Cpu' -and $gpu.Output -match 'No NVIDIA GPU was found') $provider
    }
    $mcpList = Invoke-Logged 'setup-mcp-list' $cli @('setup', 'mcp', '--list', '--library', $library)
    Check 'manualforge setup mcp --list' ($mcpList.Code -eq 0)
    if (Test-Path $nvidiaSmi) { Invoke-Logged 'nvidia-smi-after' $nvidiaSmi @() | Out-Null }

    Write-Step 'Reading the sample (4 pages) on the CPU'
    $ocrCpu = Invoke-Logged 'ocr-cpu' $cli @('ocr', $Sample, '--pages', '1-4', '--engine', 'cpu', '--out', "$work\ocr-cpu.pdf", '--overwrite', '--text', "$results\ocr-cpu.txt")
    $wordsCpu = if (Test-Path "$results\ocr-cpu.txt") { @((Get-Content "$results\ocr-cpu.txt" -Raw) -split '\s+' | Where-Object { $_ -and $_ -notmatch '^-+$' }).Count } else { 0 }
    Check 'OCR on the CPU' ($ocrCpu.Code -eq 0 -and $wordsCpu -gt 0) ("{0} words, {1:N0} s" -f $wordsCpu, $ocrCpu.Seconds)

    if ($provider -ne 'Cpu' -and $provider -ne 'unknown') {
        Write-Step "Reading the sample (4 pages) on the GPU, twice: the first builds and caches what the GPU needs"
        foreach ($pass in 1, 2) {
            $ocrGpu = Invoke-Logged "ocr-gpu-$pass" $cli @('ocr', $Sample, '--pages', '1-4', '--out', "$work\ocr-gpu.pdf", '--overwrite', '--text', "$results\ocr-gpu-$pass.txt")
            $wordsGpu = if (Test-Path "$results\ocr-gpu-$pass.txt") { @((Get-Content "$results\ocr-gpu-$pass.txt" -Raw) -split '\s+' | Where-Object { $_ -and $_ -notmatch '^-+$' }).Count } else { 0 }
            Check "OCR on the GPU, pass $pass" ($ocrGpu.Code -eq 0 -and $wordsGpu -gt 0) ("{0} words, {1:N0} s" -f $wordsGpu, $ocrGpu.Seconds)
        }
    }

    Write-Step 'A small library: survey, run, index, search'
    $survey = Invoke-Logged 'library-survey' $cli @('survey', $library)
    Check 'survey' ($survey.Code -eq 0)
    $run = Invoke-Logged 'library-run' $cli @('run', $library)
    Check 'run' ($run.Code -eq 0) ("{0:N1} min" -f ($run.Seconds / 60))
    $index = Invoke-Logged 'library-index' $cli @('index', $library)
    Check 'index' ($index.Code -eq 0)
    $search = Invoke-Logged 'library-search' $cli @('search', $searchWord, '--library', $library)
    Check "search finds '$searchWord'" ($search.Code -eq 0 -and $search.Output -match 'sample')
    $status = Invoke-Logged 'library-status' $cli @('status', $library)
    Check 'status' ($status.Code -eq 0)
    Check 'original kept in _Originals' ([bool] (Get-ChildItem (Join-Path $library '_Originals') -Recurse -Filter '*.pdf' -ErrorAction SilentlyContinue))

    Write-Step 'The MCP server, as each AI app would start it'
    $launches = @(Get-McpLaunches)
    if (-not $launches) { Note 'MCP' 'no AI app here has ManualForge registered; starting the server directly instead' }
    $direct = [pscustomobject]@{ Client = 'direct'; Command = (Join-Path $app 'ManualForge.Mcp.exe'); Arguments = @(); Environment = @{ MANUALFORGE_LIBRARY = $library } }
    foreach ($launch in $(if ($launches) { $launches } else { @($direct) })) {
        $mcp = Test-McpServer $launch.Command $launch.Arguments $launch.Environment "mcp-exchange-$($launch.Client)"
        $who = "MCP from $($launch.Client)"
        Check "$who initializes" $mcp.Initialized $launch.Command
        Check "$who lists its tools" ($mcp.Tools.Count -ge 3) ($mcp.Tools -join ', ')
        Check "$who answers library_status" ([bool] $mcp.StatusText)
        Check "$who finds '$searchWord'" ($mcp.SearchText -match 'sample')
    }

    Write-Step 'The app, from the Start menu'
    Get-Process ManualForge.App -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(5000)) { $_.Kill() } }
    $appStarted = Get-Date
    Start-Process $shortcut
    $window = $null
    for ($i = 0; $i -lt 60 -and -not $window; $i++) {
        Start-Sleep -Seconds 1
        $window = Get-Process ManualForge.App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    }
    Check 'app opens a window from the Start menu' ([bool] $window) $(if ($window) { "{0:N1} s, '{1}'" -f ((Get-Date) - $appStarted).TotalSeconds, $window.MainWindowTitle } else { 'no window within 60 s' })
    if ($window) {
        Start-Sleep -Seconds 8
        $window.Refresh()
        Check 'app still running after 8 s' (-not $window.HasExited)
        if (-not $window.HasExited) {
            Save-WindowShot $window.MainWindowHandle (Join-Path $results 'app.png') | Out-Null
            $window.CloseMainWindow() | Out-Null
            if (-not $window.WaitForExit(10000)) { $window.Kill() }
        }
    }

    # ------------------------------------------------------------ 4. upgrading over a running install

    Write-Step 'Upgrading over the running install'
    if (-not $Silent -and -not $Unattended) {
        Say 'Now start Claude Desktop - or quit it (from the tray icon) and start it again, if it is open - so that it'
        Say 'starts ManualForge''s MCP server. Then Setup runs again over this install, on its own; it should stop'
        Say 'that server and say Claude Desktop started it. Claude Desktop may then show an error for ManualForge;'
        Say 'that is expected.'
        Read-Host 'Press Enter once Claude Desktop is open' | Out-Null
    }
    $running = @(Get-CimInstance Win32_Process -Filter "Name = 'ManualForge.Mcp.exe'" | Where-Object { $_.ExecutablePath -like "$app\*" })
    $ours = $null
    if (-not $running) {
        Note 'upgrade' 'no app had started the MCP server, so this script starts one, for Setup to find and stop'
        # As an app starts it: stdin held open, which it reads until the app goes away.
        $info = New-Object System.Diagnostics.ProcessStartInfo (Join-Path $app 'ManualForge.Mcp.exe')
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.EnvironmentVariables['MANUALFORGE_LIBRARY'] = $library
        $ours = [System.Diagnostics.Process]::Start($info)
        Start-Sleep -Seconds 3
        $running = @(Get-CimInstance Win32_Process -Filter "Name = 'ManualForge.Mcp.exe'" | Where-Object { $_.ExecutablePath -like "$app\*" })
    }
    Check 'an MCP server running before the upgrade' ([bool] $running) (($running | ForEach-Object { "process $($_.ProcessId)" }) -join ', ')
    $upgradeStarted = Get-Date
    $upgradeCode = Start-AndWait $Installer "/SILENT /SUPPRESSMSGBOXES /NORESTART /LIBRARY=`"$library`" /LOG=`"$results\upgrade.log`""
    Wait-ForSetupCopies
    Check 'upgrade finished with exit code 0' ($upgradeCode -eq 0) ("code {0}, {1:N1} min" -f $upgradeCode, ((Get-Date) - $upgradeStarted).TotalMinutes)
    $upgradeLog = if (Test-Path "$results\upgrade.log") { Get-Content "$results\upgrade.log" -Raw } else { '' }
    $named = @([regex]::Matches($upgradeLog, 'ManualForge\.Mcp\.exe \(process \d+\), started by ([^\r\n]+)') |
        ForEach-Object { $_.Groups[1].Value.Trim() } | Sort-Object -Unique)
    Check 'upgrade named the app that started each MCP server' ($upgradeLog -match 'Stopped:' -and $named) ($named -join ', ')
    if (-not $ours -and -not $Silent -and -not $Unattended) { Check 'upgrade named Claude Desktop' ($named -contains 'Claude Desktop') ($named -join ', ') }
    Check 'upgrade stopped them' ($running -and -not @($running | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }))
    Check 'upgrade installed no runtime again' ($upgradeLog -match 'Runtimes missing: \.NET 10 0, Windows App Runtime 1\.8 0, Visual C\+\+ 0')
    $versionAfter = Invoke-Logged 'version-after-upgrade' $cli @('version')
    Check 'same build after the upgrade' (($versionAfter.Output -split "`n")[0].Trim() -eq ($version.Output -split "`n")[0].Trim()) (($versionAfter.Output -split "`n")[0].Trim())
    $gpuAfter = Invoke-Logged 'gpu-after-upgrade' $cli @('gpu')
    $providerAfter = if ($gpuAfter.Output -match 'Active provider\s*:\s*(\S+)') { $Matches[1] } else { 'unknown' }
    Check 'same provider after the upgrade' ($providerAfter -eq $provider) $providerAfter
    Check 'upgrade kept the models' (Test-Path (Join-Path $data 'models\PP-OCRv5_mobile_det.onnx'))

    # What ManualForge wrote for itself, before an uninstall takes it away.
    if (Test-Path (Join-Path $data 'logs')) { Copy-Item (Join-Path $data 'logs') (Join-Path $results 'manualforge-logs') -Recurse -Force }
    Get-ChildItem $data -Filter '*.json' -ErrorAction SilentlyContinue | Copy-Item -Destination $results
    Save-Text 'data-folder.txt' (Get-ChildItem $data -Directory -ErrorAction SilentlyContinue | ForEach-Object { '{0,-30} {1,10} MB' -f $_.Name, (Get-FolderMB $_.FullName) })
    $mcpInstalled = Find-McpReferences
    Save-Text 'mcp-files-after-install.txt' $mcpInstalled
    Note 'AI app files naming ManualForge' $(if ($mcpInstalled) { $mcpInstalled -join '; ' } else { 'none (no AI apps installed?)' })

    # ------------------------------------------------------------ 5. uninstalling

    if (Ask 'Uninstall ManualForge now, to test removing it? (No keeps it installed, pointed at the test library)' $true) {
        Write-Step 'Uninstalling'
        $libraryBefore = @(Get-FileHashes $library)
        Save-Text 'library-before-uninstall.txt' $libraryBefore
        $uninstaller = Get-ChildItem $app -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        Check 'uninstaller present' ([bool] $uninstaller)
        if ($uninstaller) {
            if ($Silent) { $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$results\uninstall.log`"" }
            else {
                $arguments = "/LOG=`"$results\uninstall.log`""
                Say 'The uninstaller is starting. If it says ManualForge is running, OK to stop it. Then Yes to removing'
                Say 'ManualForge, Yes to removing its models, logs and GPU data, and OK to its closing message.'
            }
            $uninstallStarted = Get-Date
            $uninstallCode = Start-AndWait $uninstaller.FullName $arguments
            Wait-ForSetupCopies
            for ($i = 0; $i -lt 60 -and (Test-Path $app); $i++) { Start-Sleep -Seconds 1 }
            Check 'uninstaller exit code 0' ($uninstallCode -eq 0) ("code {0}, {1:N1} min" -f $uninstallCode, ((Get-Date) - $uninstallStarted).TotalMinutes)
        }
        $left = Get-Leftovers
        Check 'nothing of ManualForge left' (-not $left) ($left -join '; ')
        if (Test-Path "$results\uninstall.log") {
            Get-Content "$results\uninstall.log" | Select-String 'started by' | ForEach-Object { Note 'uninstall stopped' $_.Line.Trim() }
        }
        $mcpLeft = @($mcpInstalled | Where-Object { Test-Path $_ } | Where-Object { Select-String -LiteralPath $_ -Pattern 'ManualForge\.Mcp' -Quiet })
        Check 'AI apps no longer name ManualForge' (-not $mcpLeft) ($mcpLeft -join '; ')
        Compare-McpConfigState $mcpConfigBefore (Get-McpConfigState)
        $environmentAfter = @(Get-UserEnvironment)
        Save-Text 'environment-after-uninstall.txt' $environmentAfter
        $environmentDiff = @(Compare-Object $environmentBefore $environmentAfter | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        Check 'user environment as before installing' (-not $environmentDiff) ($environmentDiff -join '; ')
        $libraryAfter = @(Get-FileHashes $library)
        $libraryDiff = @(Compare-Object $libraryBefore $libraryAfter | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        Save-Text 'library-diff.txt' $libraryDiff
        Check 'test library byte for byte as before uninstalling' (-not $libraryDiff) ("{0} files{1}" -f $libraryAfter.Count, $(if ($libraryDiff) { "; differs: $($libraryDiff -join '; ')" }))
        $runtimesEnd = Get-Runtimes
        Save-Text 'runtimes-after-uninstall.txt' ($runtimesEnd | Format-List)
        Note 'runtimes kept after uninstalling (by design; Settings > Apps removes them)' ("VC++ {0}; .NET 10 {1}" -f $runtimesEnd.VisualCpp, [bool] ($runtimesEnd.DotNet -match 'WindowsDesktop\.App 10\.'))
    }
    else { Note 'uninstall' 'not tested: ManualForge left installed' }
}
catch {
    Check 'script ran to the end' $false $_.Exception.Message
    Write-Host $_.ScriptStackTrace
}
finally {
    Write-Step 'Gathering the results'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2, 3; StartTime = $scriptStarted } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'ManualForge|onnx|dotnet|\.NET|WindowsAppRuntime|VC_redist|Inno' } |
        Select-Object -First 50 | Format-List TimeCreated, ProviderName, Id, LevelDisplayName, Message |
        Out-String -Width 250 | Set-Content (Join-Path $results 'event-log.txt') -Encoding UTF8
    $failed = @($checks | Where-Object { $_ -like 'FAIL*' }).Count
    $summary = @("ManualForge fresh-install test on $env:COMPUTERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
                 "Release: $Tag", "Installer: $(if ($Installer) { Split-Path $Installer -Leaf })",
                 "$(@($checks | Where-Object { $_ -notlike 'NOTE*' }).Count) checks, $failed failed", '') + $checks + @('', 'Timeline:') + $timeline
    $summary | Set-Content -Path (Join-Path $results 'summary.txt') -Encoding UTF8
    Stop-Transcript | Out-Null

    $zip = "$results.zip"
    Compress-Archive -Path "$results\*" -DestinationPath $zip -Force
    Write-Host ''
    $summary | Select-Object -First 4 | ForEach-Object { Write-Host $_ }
    Write-Host "Results: $zip"
    if (-not $NoUpload -and $gh -and $Tag) {
        & $gh release upload $Tag $zip --repo $Repo --clobber
        if ($LASTEXITCODE -eq 0) { Write-Host "Attached to the draft release $Tag on GitHub." -ForegroundColor Green }
        else { Write-Host 'Could not attach the results to the release; the zip on the Desktop has everything.' -ForegroundColor Red }
    }
    Write-Host 'Done. You can close this window.'
}
