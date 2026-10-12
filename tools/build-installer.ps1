<#
.SYNOPSIS
    Builds ManualForge's installer (#26): one folder of program, prerequisites pinned by hash, and an
    Inno Setup package that downloads everything else.

.DESCRIPTION
    1. Publishes the command line, the MCP server and the desktop app into one folder, framework-
       dependent and without debug symbols. 296 MB, against the 896 MB of publishing them separately:
       the ONNX Runtime CUDA provider (178 MB) and SkiaSharp's symbols (85 MB) were each shipped twice.
    2. Pins the three runtimes the installer chains in when a machine lacks them:
         * .NET 10 Desktop Runtime, from Microsoft's release manifest, checked against its SHA-512;
         * Windows App Runtime 1.8 and the Visual C++ Redistributable (which ONNX Runtime needs),
           each resolved from Microsoft's "latest" link to the versioned download behind it and
           checked for a valid Microsoft signature.
       Each is downloaded here once, and its SHA-256 is written into the installer, which refuses a
       download that doesn't match.
    3. Compiles installer\ManualForge.iss with Inno Setup 6 into artifacts\installer\package.

    Nothing NVIDIA publishes is in the package: the installer runs `manualforge setup gpu`, which
    fetches it from NVIDIA (docs/measurements/installer-gpu-libraries.md).

.EXAMPLE
    ./tools/build-installer.ps1
    ./tools/build-installer.ps1 -SkipCompile    # publish and pin only, without Inno Setup
#>
[CmdletBinding()]
param(
    [string] $Iscc = (@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1),
    [switch] $SkipCompile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'artifacts\installer'
$stage = Join-Path $out 'stage'
$cache = Join-Path $out 'cache'
$packageDir = Join-Path $out 'package'
New-Item -ItemType Directory -Force $out, $cache, $packageDir | Out-Null

function Write-Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. the program, one folder

Write-Step 'Publishing into one folder'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$common = @('-c', 'Release', '-o', $stage, '-nologo', '-v', 'q', '-p:DebugType=none', '-p:DebugSymbols=false')
foreach ($project in 'ManualForge.Cli', 'ManualForge.Mcp') {
    dotnet publish (Join-Path $repo "src\$project\$project.csproj") @common
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
# The window on the shared runtimes the installer chains in, rather than carrying its own.
dotnet publish (Join-Path $repo 'src\ManualForge.App\ManualForge.App.csproj') @common '-p:SelfContained=false' '-p:WindowsAppSDKSelfContained=false'
if ($LASTEXITCODE -ne 0) { throw 'Publishing ManualForge.App failed.' }

# Symbols that come inside packages (SkiaSharp's native .pdb) ignore DebugType.
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($required in 'manualforge.exe', 'ManualForge.Mcp.exe', 'ManualForge.App.exe', 'ManualForge.App.pri', 'Assets\AppIcon.ico') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "The published folder has no $required." }
}

$version = (& (Join-Path $stage 'manualforge.exe') version | Select-Object -First 1) -replace '^manualforge\s+', ''
if ($version -match '\.dirty$') { Write-Warning "Built from a working tree with uncommitted changes: $version" }
$files = Get-ChildItem $stage -Recurse -File
Write-Host ("   {0}: {1} files, {2:N0} MB" -f $version, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

# ---------------------------------------------------------------- 2. the runtimes, pinned

function Get-Pinned([string] $url, [string] $name) {
    $path = Join-Path $cache $name
    if (-not (Test-Path $path)) { Invoke-WebRequest $url -OutFile $path -UseBasicParsing }
    return $path
}

Write-Step '.NET 10 Desktop Runtime'
$releases = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$desktop = $releases.releases[0].windowsdesktop
$runtimeFile = $desktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
$dotnet = Get-Pinned $runtimeFile.url (Split-Path $runtimeFile.url -Leaf)
$sha512 = (Get-FileHash $dotnet -Algorithm SHA512).Hash
if ($sha512 -ne $runtimeFile.hash.ToUpperInvariant()) { throw "$dotnet does not match the SHA-512 in Microsoft's release manifest." }
$dotnetSha256 = (Get-FileHash $dotnet -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "   $($desktop.version), SHA-512 matches the manifest"

# Where one of Microsoft's "latest" links points today: a versioned download, which is what gets pinned,
# so an installer built now keeps fetching the build it was checked against.
function Resolve-Latest([string] $url, [string] $expectedHost) {
    $handler = [System.Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        while ($url -match '^https://aka\.ms/') {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $url)
            $url = $client.SendAsync($request).GetAwaiter().GetResult().Headers.Location.AbsoluteUri
        }
    }
    finally { $client.Dispose() }
    if (-not $url -or $url -notmatch "^https://$([regex]::Escape($expectedHost))/") { throw "The link did not lead to $($expectedHost): $url" }
    return $url
}

function Assert-SignedByMicrosoft([string] $path) {
    $signature = Get-AuthenticodeSignature $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "$path is not validly signed by Microsoft ($($signature.Status))."
    }
}

Write-Step 'Windows App Runtime 1.8'
$runtimeUrl = Resolve-Latest 'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe' 'download.microsoft.com'
$appRuntime = Get-Pinned $runtimeUrl ('WindowsAppRuntimeInstall-x64-' + ($runtimeUrl -split '/')[-2] + '.exe')
Assert-SignedByMicrosoft $appRuntime
$appRuntimeSha256 = (Get-FileHash $appRuntime -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "   $((Get-Item $appRuntime).VersionInfo.ProductVersion), signed by Microsoft"

Write-Step 'Visual C++ Redistributable'
# ONNX Runtime imports MSVCP140, MSVCP140_1, VCRUNTIME140 and VCRUNTIME140_1, which a clean Windows does
# not have: without them nothing can be read, on the CPU or the GPU (found in Windows Sandbox).
$vcUrl = Resolve-Latest 'https://aka.ms/vc14/vc_redist.x64.exe' 'download.visualstudio.microsoft.com'
$vcRedist = Get-Pinned $vcUrl ('VC_redist.x64-' + ($vcUrl -split '/')[-2].Substring(0, 12) + '.exe')
Assert-SignedByMicrosoft $vcRedist
$vcSha256 = (Get-FileHash $vcRedist -Algorithm SHA256).Hash.ToLowerInvariant()
$vcVersion = (Get-Item $vcRedist).VersionInfo.ProductVersion
Write-Host "   $vcVersion, signed by Microsoft"

# ---------------------------------------------------------------- 3. the package

if ($SkipCompile) { Write-Host ''; Write-Host "Published to $stage; not compiled (-SkipCompile)."; return }
if (-not $Iscc) { throw 'Inno Setup 6 was not found. Install it (winget install JRSoftware.InnoSetup), or pass -Iscc.' }

# The NVIDIA archives this build pins, as `setup gpu` resolves them from NVIDIA's manifests. Setup
# compares them with what an earlier install recorded, so an upgrade over current libraries neither
# offers the download nor asks for NVIDIA's licences again (#78).
Write-Step 'Reading the NVIDIA libraries this build pins'
$pins = & (Join-Path $stage 'manualforge.exe') setup gpu --pins
if ($LASTEXITCODE -ne 0 -or -not $pins) { throw 'manualforge setup gpu --pins failed; NVIDIA''s manifests could not be read.' }
$pins | ForEach-Object { Write-Host "   $_" }
$pinned = $pins | ForEach-Object { $folder, $sha, $null = $_ -split ' ', 3; [pscustomobject]@{ Folder = $folder; Sha256 = $sha } }
$gpuCorePins = ($pinned | Where-Object Folder -in 'cuda', 'cudnn' | ForEach-Object Sha256) -join ','
$tensorRtPins = ($pinned | Where-Object Folder -eq 'tensorrt' | ForEach-Object Sha256) -join ','
if (-not $gpuCorePins -or -not $tensorRtPins -or (($gpuCorePins + ',' + $tensorRtPins) -notmatch '^[0-9a-f]{64}(,[0-9a-f]{64})*$')) { throw "setup gpu --pins did not give a SHA-256 for each of CUDA, cuDNN and TensorRT:`n$($pins -join "`n")" }

Write-Step 'Compiling the installer'
$defines = @{
    AppVersion        = $version
    # The four-part number Windows shows in the file's properties: 1.0.0+3441e55 is 1.0.0.0.
    FileVersion       = ($version -replace '[+-].*$', '') + '.0'
    StageDir          = $stage
    OutputDir         = $packageDir
    RepoDir           = $repo
    DotNetUrl         = $runtimeFile.url
    DotNetSha256      = $dotnetSha256
    DotNetVersion     = $desktop.version
    AppRuntimeUrl     = $runtimeUrl
    AppRuntimeSha256  = $appRuntimeSha256
    VcRedistUrl       = $vcUrl
    VcRedistSha256    = $vcSha256
    VcRedistVersion   = $vcVersion
    GpuCorePins       = $gpuCorePins
    TensorRtPins      = $tensorRtPins
}
$arguments = @($defines.GetEnumerator() | ForEach-Object { "/D$($_.Key)=$($_.Value)" }) + (Join-Path $repo 'installer\ManualForge.iss')
& $Iscc /Q @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$package = Get-ChildItem $packageDir -Filter 'ManualForge-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("   {0}: {1:N0} MB" -f $package.Name, ($package.Length / 1MB))

# A clean Windows to try it in, with Windows Sandbox enabled; the sandbox is given no GPU (vGPU off), so
# this tests the CPU path, the runtimes, the Start menu entry and uninstalling, and it is thrown away
# when closed. In sandbox\:
#   test-in-sandbox.wsb  runs installer\test-in-sandbox.ps1 at logon: installs silently, checks,
#                        uninstalls, and writes what it found to results\ (summary.txt last).
#   try-in-sandbox.wsb   just opens C:\Installer, to click through the wizard by hand.
#   kit\                 what the sandbox sees as C:\Installer: the package and the test script. A
#                        sample.pdf put here is OCR'd on the CPU by the test, and kept across builds.
# Only kit\ and results\ are mapped, never this folder: once a sandbox has mapped a folder, Windows
# keeps it shared after the sandbox closes, and Inno Setup can no longer write a package into it.
$sandboxDir = Join-Path $out 'sandbox'
$kit = Join-Path $sandboxDir 'kit'
$results = Join-Path $sandboxDir 'results'
New-Item -ItemType Directory -Force $kit, $results | Out-Null
Get-ChildItem $kit -Filter 'ManualForge-Setup-*.exe' | Remove-Item -Force
Copy-Item -LiteralPath $package.FullName, (Join-Path $repo 'installer\test-in-sandbox.ps1') -Destination $kit
function Write-Sandbox([string] $name, [string] $extraFolders, [string] $command) {
    $path = Join-Path $sandboxDir $name
    @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$kit</HostFolder>
      <SandboxFolder>C:\Installer</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>$extraFolders
  </MappedFolders>
  <vGPU>Disable</vGPU>
  <Networking>Enable</Networking>
  <LogonCommand>
    <Command>$command</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Path $path -Encoding UTF8
    Write-Host "   Sandbox: $path"
}
Write-Sandbox 'test-in-sandbox.wsb' @"

    <MappedFolder>
      <HostFolder>$results</HostFolder>
      <SandboxFolder>C:\Results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
"@ 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Installer\test-in-sandbox.ps1'
Write-Sandbox 'try-in-sandbox.wsb' '' 'explorer.exe C:\Installer'
