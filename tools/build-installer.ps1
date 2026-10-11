<#
.SYNOPSIS
    Builds ManualForge's installer (#26): one folder of program, prerequisites pinned by hash, and an
    Inno Setup package that downloads everything else.

.DESCRIPTION
    1. Publishes the command line, the MCP server and the desktop app into one folder, framework-
       dependent and without debug symbols. 296 MB, against the 896 MB of publishing them separately:
       the ONNX Runtime CUDA provider (178 MB) and SkiaSharp's symbols (85 MB) were each shipped twice.
    2. Pins the two runtimes the installer chains in when a machine lacks them:
         * .NET 10 Desktop Runtime, from Microsoft's release manifest, checked against its SHA-512;
         * Windows App Runtime 1.8, resolved from Microsoft's "latest 1.8" link to the versioned
           download behind it, checked for a valid Microsoft signature.
       Each is downloaded here once, and its SHA-256 is written into the installer, which refuses a
       download that doesn't match.
    3. Compiles installer\ManualForge.iss with Inno Setup 6 into artifacts\installer.

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
New-Item -ItemType Directory -Force $out, $cache | Out-Null

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

Write-Step 'Windows App Runtime 1.8'
# Where Microsoft's "latest 1.8" link points today: a versioned download, which is what gets pinned, so
# an installer built now keeps fetching the build it was checked against.
$handler = [System.Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
try {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, 'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe')
    $runtimeUrl = $client.SendAsync($request).GetAwaiter().GetResult().Headers.Location.AbsoluteUri
}
finally { $client.Dispose() }
if (-not $runtimeUrl -or $runtimeUrl -notmatch '^https://download\.microsoft\.com/') { throw "The Windows App Runtime link did not lead to download.microsoft.com: $runtimeUrl" }
$appRuntime = Get-Pinned $runtimeUrl ('WindowsAppRuntimeInstall-x64-' + ($runtimeUrl -split '/')[-2] + '.exe')
$signature = Get-AuthenticodeSignature $appRuntime
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "$appRuntime is not validly signed by Microsoft ($($signature.Status))."
}
$appRuntimeSha256 = (Get-FileHash $appRuntime -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "   $((Get-Item $appRuntime).VersionInfo.ProductVersion), signed by Microsoft"

# ---------------------------------------------------------------- 3. the package

if ($SkipCompile) { Write-Host ''; Write-Host "Published to $stage; not compiled (-SkipCompile)."; return }
if (-not $Iscc) { throw 'Inno Setup 6 was not found. Install it (winget install JRSoftware.InnoSetup), or pass -Iscc.' }

Write-Step 'Compiling the installer'
$defines = @{
    AppVersion        = $version
    StageDir          = $stage
    OutputDir         = $out
    RepoDir           = $repo
    DotNetUrl         = $runtimeFile.url
    DotNetSha256      = $dotnetSha256
    DotNetVersion     = $desktop.version
    AppRuntimeUrl     = $runtimeUrl
    AppRuntimeSha256  = $appRuntimeSha256
}
$arguments = @($defines.GetEnumerator() | ForEach-Object { "/D$($_.Key)=$($_.Value)" }) + (Join-Path $repo 'installer\ManualForge.iss')
& $Iscc /Q @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$package = Get-ChildItem $out -Filter 'ManualForge-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("   {0}: {1:N0} MB" -f $package.Name, ($package.Length / 1MB))
