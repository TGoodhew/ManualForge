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

    Step 1 also builds ManualForge.ExplorerMenu.dll, the handler for "Add to ManualForge library" on
    Windows 11's first right-click menu (#89), with Visual Studio's C++ tools, and, in a signed build,
    packs installer\explorer-menu\AppxManifest.xml into the sparse package that declares it. Windows
    registers only a package whose signature it trusts, so an unsigned build has no package, and Setup
    offers the Send To entry alone.

    With -SignScript, ManualForge's own programs are signed after publishing (step 1), and Inno Setup
    signs the Setup program and the uninstaller it builds (step 3), all with that script. It is given
    the files to sign, and must sign and timestamp them and fail if it cannot. The signing account and
    its tools live outside this repository, so the script's path is given here, or in
    MANUALFORGE_SIGN_SCRIPT, never written into it. Without one the build is unsigned, and says so.
    Other publishers' DLLs are left as they are.

    Nothing NVIDIA publishes is in the package: the installer runs `manualforge setup gpu`, which
    fetches it from NVIDIA (docs/measurements/installer-gpu-libraries.md).

.EXAMPLE
    ./tools/build-installer.ps1
    ./tools/build-installer.ps1 -SkipCompile    # publish and pin only, without Inno Setup
    ./tools/build-installer.ps1 -SignScript C:\signing\Sign-Files.ps1
#>
[CmdletBinding()]
param(
    [string] $Iscc = (@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1),
    [switch] $SkipCompile,
    [string] $SignScript = $env:MANUALFORGE_SIGN_SCRIPT
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

# The Explorer menu's handler (#89). Native, so Visual Studio's MSBuild rather than dotnet.
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio with the C++ x64 tools is needed to build ManualForge.ExplorerMenu.dll.' }
$menuProject = Join-Path $repo 'src\ManualForge.ExplorerMenu'
& $msbuild (Join-Path $menuProject 'ManualForge.ExplorerMenu.vcxproj') -p:Configuration=Release -p:Platform=x64 -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Building ManualForge.ExplorerMenu failed.' }
Copy-Item (Join-Path $menuProject 'bin\Release\ManualForge.ExplorerMenu.dll') $stage

# ManualForge's own programs only: everyone else's DLLs are theirs to sign, and most already are.
if ($SignScript) {
    if (-not (Test-Path $SignScript)) { throw "No signing script at $SignScript." }
    Write-Step 'Signing ManualForge''s own programs'
    $own = Get-ChildItem $stage -File | Where-Object { $_.Name -match '^(manualforge\.exe|ManualForge\..+\.(exe|dll))$' } |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' } | ForEach-Object FullName
    & $SignScript @own
    $unsigned = $own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' }
    if ($unsigned) { throw "Still not validly signed: $($unsigned -join ', ')" }
    Write-Host ("   {0} file(s), signed by {1}" -f $own.Count, (Get-AuthenticodeSignature $own[0]).SignerCertificate.Subject)
}
else {
    Write-Warning 'Not signed: no -SignScript and no MANUALFORGE_SIGN_SCRIPT.'
}

$version = (& (Join-Path $stage 'manualforge.exe') version | Select-Object -First 1) -replace '^manualforge\s+', ''
if ($version -match '\.dirty$') { Write-Warning "Built from a working tree with uncommitted changes: $version" }
# The sparse package that puts the handler on the first menu (#89): signed, or not at all.
if ($SignScript) {
    Write-Step 'The Explorer menu''s package'
    $makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" |
        Sort-Object { [version] $_.Directory.Parent.Name } | Select-Object -Last 1
    if (-not $makeappx) { throw 'makeappx.exe was not found; install the Windows SDK.' }

    $layout = Join-Path $out 'explorer-menu'
    if (Test-Path $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
    $packageVersion = ($version -replace '[+-].*$', '') + '.0'
    (Get-Content (Join-Path $repo 'installer\explorer-menu\AppxManifest.xml') -Raw).Replace('@VERSION@', $packageVersion) |
        Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

    # The logos the manifest names, from the app's icon, in the package and beside the program both.
    Add-Type -AssemblyName System.Drawing
    foreach ($size in 44, 50, 150) {
        $icon = New-Object System.Drawing.Icon (Join-Path $stage 'Assets\AppIcon.ico'), 256, 256
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($icon.ToBitmap(), 0, 0, $size, $size)
        $graphics.Dispose(); $icon.Dispose()
        $bitmap.Save((Join-Path $layout "Assets\PackageLogo$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
    Copy-Item (Join-Path $layout 'Assets\PackageLogo*.png') (Join-Path $stage 'Assets')

    $msix = Join-Path $stage 'ManualForge.ExplorerMenu.msix'
    & $makeappx.FullName pack /d $layout /p $msix /nv /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'makeappx could not pack the Explorer menu''s package.' }
    & $SignScript $msix
    if ((Get-AuthenticodeSignature $msix).Status -ne 'Valid') { throw "$msix is not validly signed." }
    Write-Host "   ManualForge.ExplorerMenu.msix $packageVersion, signed"
}
else {
    Write-Warning 'No Explorer menu package: Windows registers only a signed one, so this build has Send To alone.'
}

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
$arguments = @($defines.GetEnumerator() | ForEach-Object { "/D$($_.Key)=$($_.Value)" })
if ($SignScript) {
    # Inno Setup runs this for Setup and for the uninstaller it builds into it; $f is the file, $q a quote.
    $shell = (Get-Process -Id $PID).Path
    $arguments += '/DSign', "/Smanualforge=`$q$shell`$q -NoProfile -ExecutionPolicy Bypass -File `$q$((Resolve-Path $SignScript).Path)`$q `$f"
}
$arguments += Join-Path $repo 'installer\ManualForge.iss'
& $Iscc /Q @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$package = Get-ChildItem $packageDir -Filter 'ManualForge-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("   {0}: {1:N0} MB" -f $package.Name, ($package.Length / 1MB))
if ($SignScript) {
    $signature = Get-AuthenticodeSignature $package.FullName
    if ($signature.Status -ne 'Valid') { throw "$($package.Name) is not validly signed ($($signature.Status))." }
    Write-Host "   signed by $($signature.SignerCertificate.Subject), timestamped by $($signature.TimeStamperCertificate.Subject)"
}

# A clean Windows to try it in, with Windows Sandbox enabled; the sandbox is given no GPU (vGPU off), so
# this tests the CPU path, the runtimes, the Start menu entry and uninstalling, and it is thrown away
# when closed. Keep vGPU off: with it on, a test sandbox twice hard-reset the host (#84). In sandbox\:
#   test-in-sandbox.wsb  runs installer\test-in-sandbox.ps1 at logon: installs silently, checks,
#                        uninstalls, writes what it found to results\ (summary.txt last), and shuts the
#                        sandbox down. tools\invoke-sandbox-test.ps1 starts it, waits, prints the summary,
#                        and closes the sandbox if it did not close itself.
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
    <Command>$([System.Security.SecurityElement]::Escape($command))</Command>
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
"@ ('cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Installer\test-in-sandbox.ps1' +
    # Whatever the test did, shut down when it ends; shutdown.txt shows whether that was even tried.
    ' & shutdown.exe /s /f /t 0 > C:\Results\shutdown.txt 2>&1')
Write-Sandbox 'try-in-sandbox.wsb' '' 'explorer.exe C:\Installer'
Write-Host '   tools\invoke-sandbox-test.ps1 runs the test, and closes the sandbox if it does not close itself.'
