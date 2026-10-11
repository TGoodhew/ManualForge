<#
.SYNOPSIS
    Prepares a Windows machine for ManualForge's GPU path: CUDA 13 and cuDNN 9, and optionally
    TensorRT 10, checked rather than assumed.

.DESCRIPTION
    ONNX Runtime's CUDA provider does not fail loudly. When it cannot load a library it declines to
    register, execution falls back to the CPU, and the only symptom is that the work takes about ten
    times as long. On a job measured in hours nobody notices for a while, so every step here checks
    its own result and the script ends by asking the application itself what provider it got.

    Two traps this exists to avoid, both of which leave a directory that exists and holds no DLLs:

      * CUDA 13 moved its Windows DLLs from `bin` into `bin\x64`. A path written down from a 12.x
        install points somewhere real and empty.
      * The cuDNN zip extracts into a nested folder; its DLLs are three levels below where the zip
        lands.

    TensorRT is optional. With it, recognition runs about twice as fast (#34, #53); without it,
    everything works on CUDA alone. It comes from NVIDIA's own package index as the
    tensorrt_cu13_libs wheel (about 1.9 GB), checked against the SHA-256 the index publishes, and only
    its DLLs are kept. No Python is needed or installed.

    It does not install .NET, Visual Studio or the display driver, and it does not touch the manual
    library. It does not build or publish the application either - that is a different script for a
    different occasion, because this one is run after a rebuild and that one after every change.

.PARAMETER WhatIf
    Prints what would be downloaded and installed, with sizes, and changes nothing.

.PARAMETER SkipTensorRt
    Leave TensorRT out. ManualForge then runs on CUDA, which reads a few words differently from
    TensorRT and takes about twice as long.

.EXAMPLE
    ./tools/setup-cuda.ps1 -WhatIf

.EXAMPLE
    ./tools/setup-cuda.ps1 -DownloadDirectory D:\Installers
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Pinned to what is known to work here. Overridable, and printed, so that a support question
    # has an answer rather than a guess.
    [string] $CudaVersion = '13.4',
    [string] $CudnnVersion = '9.26.0.51',
    [string] $TensorRtVersion = '10.16.1.11',

    # TensorRT is optional; see the description.
    [switch] $SkipTensorRt,

    # Re-used if the installers are already here, because 2 GB is worth not fetching twice.
    [string] $DownloadDirectory = "$env:USERPROFILE\Downloads",

    # Where cuDNN is extracted. The toolkit installs itself wherever NVIDIA puts it.
    [string] $InstallRoot = 'C:\Tools',

    # Check and report only. Implied by -WhatIf; here as well so it can be asked for plainly.
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'

# Exactly what ONNX Runtime 1.30's CUDA provider imports. CUDA 12 carries the same libraries with a
# _12 suffix and will not satisfy these, which is why the major versions are written out rather than
# discovered. Kept in step with Ocr/CudaLibraries.cs, which is what the application itself looks for.
$RequiredDlls = @(
    'cublas64_13.dll',
    'cublasLt64_13.dll',
    'cudart64_13.dll',
    'cudnn64_9.dll',
    'cudnn_graph64_9.dll'
)

$CudaProbe = 'cublasLt64_13.dll'
$CudnnProbe = 'cudnn64_9.dll'

# What ONNX Runtime 1.30's TensorRT provider imports, both from one directory. Kept in step with
# CudaLibraries.TensorRtLibraries.
$TensorRtDlls = @('nvinfer_10.dll', 'nvonnxparser_10.dll')

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Good([string] $text) { Write-Host "   $text" -ForegroundColor Green }
function Write-Warn([string] $text) { Write-Host "   $text" -ForegroundColor Yellow }

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
    Finds a directory containing a named DLL, searching a few levels below each root. Bounded, so
    that a root given by mistake costs a moment rather than a walk of the volume - and searching
    rather than assuming a layout is the whole point, since both known traps are wrong assumptions
    about layout.
#>
function Find-DllDirectory {
    param([string[]] $Roots, [string] $FileName, [int] $Depth = 4)

    # CUDA 13 ships an arm64 copy of every one of these DLLs beside the x64 one, with identical
    # file names. Loading the wrong architecture fails, and it fails the same silent way as a
    # missing file: the provider declines and everything runs on the CPU. So the architecture is
    # chosen rather than stumbled into.
    $mine = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'Arm64' { 'arm64' }
        'X86'   { 'win32' }
        default { 'x64' }
    }

    $foreign = @('x64', 'arm64', 'win32', 'x86', 'aarch64') | Where-Object { $_ -ne $mine }

    foreach ($root in $Roots) {
        if (-not $root -or -not (Test-Path $root)) { continue }

        $hits = Get-ChildItem -Path $root -Filter $FileName -Recurse -Depth $Depth -File `
            -ErrorAction SilentlyContinue

        $usable = $hits | Where-Object {
            $parts = $_.DirectoryName.ToLowerInvariant() -split '[\\/]'
            -not ($parts | Where-Object { $foreign -contains $_ })
        }

        # A directory named for this architecture is the surest match; anything not named for
        # another one will do, since plenty of layouts do not mention architecture at all.
        $best = $usable | Sort-Object -Property @{
            Expression = { ($_.DirectoryName.ToLowerInvariant() -split '[\\/]') -contains $mine }
            Descending = $true
        } | Select-Object -First 1

        if ($best) { return $best.DirectoryName }
    }

    return $null
}

function Get-CudaRoots {
    @(
        $env:CUDA_PATH,
        "$env:ProgramFiles\NVIDIA GPU Computing Toolkit\CUDA",
        "${env:ProgramFiles(x86)}\NVIDIA GPU Computing Toolkit\CUDA"
    ) | Where-Object { $_ }
}

function Get-CudnnRoots {
    @(
        $env:CUDNN_PATH,
        $InstallRoot,
        "$env:ProgramFiles\NVIDIA\CUDNN",
        "$env:ProgramFiles\NVIDIA GPU Computing Toolkit\CUDA"
    ) | Where-Object { $_ }
}

function Get-TensorRtRoots {
    @(
        $env:TENSORRT_PATH,
        $InstallRoot,
        "$env:ProgramFiles\NVIDIA\TensorRT"
    ) | Where-Object { $_ }
}

# The TensorRT directory, or null. Both libraries must be there: half an install fails to attach
# exactly as no install does.
function Find-TensorRt {
    $directory = Find-DllDirectory -Roots (Get-TensorRtRoots) -FileName $TensorRtDlls[0]
    if ($directory -and -not ($TensorRtDlls | Where-Object { -not (Test-Path (Join-Path $directory $_)) })) {
        return $directory
    }
    return $null
}

function Test-Everything {
    $cuda = Find-DllDirectory -Roots (Get-CudaRoots) -FileName $CudaProbe
    $cudnn = Find-DllDirectory -Roots (Get-CudnnRoots) -FileName $CudnnProbe

    $missing = @()
    foreach ($dll in $RequiredDlls) {
        $found = ($cuda -and (Test-Path (Join-Path $cuda $dll))) -or
                 ($cudnn -and (Test-Path (Join-Path $cudnn $dll)))
        if (-not $found) { $missing += $dll }
    }

    [pscustomobject]@{ Cuda = $cuda; Cudnn = $cudnn; Missing = $missing; TensorRt = (Find-TensorRt) }
}

# ---------------------------------------------------------------------------------------------

Write-Host "ManualForge GPU prerequisites"
Write-Host "  CUDA  : $CudaVersion"
Write-Host "  cuDNN : $CudnnVersion"
Write-Host "  TensorRT : $(if ($SkipTensorRt) { 'skipped' } else { "$TensorRtVersion (optional)" })"
Write-Host "  Cache : $DownloadDirectory"
Write-Host "  cuDNN goes to: $InstallRoot"
Write-Host ""
Write-Host "The CUDA toolkit installer needs administrator rights. Extracting cuDNN and setting"
Write-Host "PATH do not. You are $(if (Test-Admin) { 'running elevated' } else { 'NOT running elevated' })."

Write-Step 'What is already here'

$driver = $null
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    $driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>$null | Select-Object -First 1)
    if ($driver) { Write-Good "Display driver $driver" }
}

if (-not $driver) {
    Write-Warn 'No nvidia-smi, so there is either no NVIDIA GPU or no driver. Nothing here will help until there is.'
    if (-not $CheckOnly) { return }
}

$state = Test-Everything

if ($state.Cuda) { Write-Good "CUDA libraries in $($state.Cuda)" } else { Write-Warn 'No CUDA 13 libraries found' }
if ($state.Cudnn) { Write-Good "cuDNN libraries in $($state.Cudnn)" } else { Write-Warn 'No cuDNN 9 libraries found' }
if ($state.TensorRt) { Write-Good "TensorRT libraries in $($state.TensorRt)" } else { Write-Warn 'No TensorRT 10 libraries found (optional)' }

if ($state.Missing.Count -eq 0) {
    Write-Step 'Already configured'
    Write-Good 'Every library ONNX Runtime imports is present. Nothing to do.'
}
else {
    Write-Warn "Missing: $($state.Missing -join ', ')"
}

if ($CheckOnly) {
    Write-Step 'Check only, so nothing was changed'
    return
}

# ---------------------------------------------------------------------------------------------

if ($state.Missing -contains $CudaProbe -or -not $state.Cuda) {
    Write-Step "CUDA Toolkit $CudaVersion"

    $installer = Join-Path $DownloadDirectory "cuda_${CudaVersion}_windows_network.exe"
    $url = "https://developer.download.nvidia.com/compute/cuda/$CudaVersion.0/network_installers/" +
           "cuda_${CudaVersion}.0_windows_network.exe"

    if (Test-Path $installer) {
        Write-Good "Using the installer already in $DownloadDirectory"
    }
    elseif ($PSCmdlet.ShouldProcess($url, 'Download the CUDA network installer (about 30 MB, then ~2 GB of components)')) {
        Invoke-WebRequest -Uri $url -OutFile $installer
        Write-Good "Downloaded to $installer"
    }

    if (-not (Test-Admin)) {
        Write-Warn 'The toolkit installer needs administrator rights. Re-run this script elevated, or install CUDA by hand, then run it again to finish the rest.'
        return
    }

    # Named components, deliberately, and no display driver among them. A toolkit installer that
    # is allowed to install its bundled driver will happily replace a newer one with an older one,
    # and a machine that loses its driver to a setup script has been made worse, not better.
    $components = "cudart_$CudaVersion cublas_$CudaVersion cublas_dev_$CudaVersion"

    if ($PSCmdlet.ShouldProcess($installer, "Install $components silently, without the bundled display driver")) {
        Write-Host "   Installing. This takes a few minutes and the window will look idle."
        $process = Start-Process -FilePath $installer -ArgumentList "-s $components" -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "The CUDA installer exited with $($process.ExitCode). Nothing further was attempted."
        }
        Write-Good 'Installed'
    }
}

if ($state.Missing -contains $CudnnProbe -or -not $state.Cudnn) {
    Write-Step "cuDNN $CudnnVersion"

    $zip = Join-Path $DownloadDirectory "cudnn-windows-x86_64-$CudnnVersion`_cuda13-archive.zip"
    $url = "https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/" +
           "cudnn-windows-x86_64-$CudnnVersion`_cuda13-archive.zip"

    if (Test-Path $zip) {
        Write-Good "Using the archive already in $DownloadDirectory"
    }
    elseif ($PSCmdlet.ShouldProcess($url, 'Download cuDNN (about 700 MB)')) {
        Invoke-WebRequest -Uri $url -OutFile $zip
        Write-Good "Downloaded to $zip"
    }

    if ($PSCmdlet.ShouldProcess($InstallRoot, 'Extract cuDNN')) {
        New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
        Expand-Archive -Path $zip -DestinationPath $InstallRoot -Force

        # Where it landed is found rather than constructed. The archive nests its DLLs several
        # levels down, and building that path by hand is the second of the two traps.
        $found = Find-DllDirectory -Roots @($InstallRoot) -FileName $CudnnProbe
        if (-not $found) { throw "Extracted cuDNN but could not find $CudnnProbe under $InstallRoot." }

        Write-Good "Extracted; libraries are in $found"
    }
}

if (-not $SkipTensorRt -and -not $state.TensorRt) {
    Write-Step "TensorRT $TensorRtVersion (optional)"

    $wheelName = "tensorrt_cu13_libs-$TensorRtVersion-py3-none-win_amd64.whl"
    $wheel = Join-Path $DownloadDirectory $wheelName
    $index = 'https://pypi.nvidia.com/tensorrt-cu13-libs/'

    # The index page links each file with its SHA-256 in the URL fragment, which is what the
    # download is checked against.
    $page = Invoke-WebRequest -Uri $index -UseBasicParsing
    $link = $page.Links | Where-Object { $_.href -like "*$wheelName*" } | Select-Object -First 1
    if (-not $link) { throw "$wheelName is not listed at $index. Pass -TensorRtVersion with a version that is." }
    $href = $link.href
    $expected = if ($href -match 'sha256=([0-9a-f]{64})') { $Matches[1] } else { $null }
    $url = if ($href -like 'http*') { ($href -split '#')[0] } else { $index + ($href -split '#')[0] }

    if (Test-Path $wheel) {
        Write-Good "Using the wheel already in $DownloadDirectory"
    }
    elseif ($PSCmdlet.ShouldProcess($url, 'Download TensorRT (about 1.9 GB)')) {
        Invoke-WebRequest -Uri $url -OutFile $wheel
        Write-Good "Downloaded to $wheel"
    }

    if ((Test-Path $wheel) -and $PSCmdlet.ShouldProcess($InstallRoot, 'Check and extract the TensorRT DLLs')) {
        if ($expected) {
            $actual = (Get-FileHash -Algorithm SHA256 -Path $wheel).Hash.ToLowerInvariant()
            if ($actual -ne $expected) { throw "$wheelName does not match the SHA-256 the index publishes. Delete it and run again." }
            Write-Good 'SHA-256 matches the index'
        }
        else {
            Write-Warn 'The index gave no SHA-256 for this file, so it could not be checked.'
        }

        # A wheel is a zip. Only its DLLs are wanted; nothing of Python is installed.
        $target = Join-Path $InstallRoot "tensorrt-$TensorRtVersion\bin"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($wheel)
        try {
            foreach ($entry in $archive.Entries | Where-Object { $_.FullName -like 'tensorrt_libs/*.dll' }) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $entry.Name), $true)
            }
        }
        finally { $archive.Dispose() }

        if (-not (Find-TensorRt)) { throw "Extracted TensorRT but could not find $($TensorRtDlls -join ' and ') under $InstallRoot." }
        Write-Good "Extracted; libraries are in $target"
    }
}

# ---------------------------------------------------------------------------------------------

Write-Step 'PATH'

$state = Test-Everything
$wanted = @($state.Cuda, $state.Cudnn, $(if (-not $SkipTensorRt) { $state.TensorRt })) | Where-Object { $_ }

if ($wanted.Count -eq 0) {
    Write-Warn 'Nothing to add: no library directories were found.'
}
else {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = ($current -split ';') | Where-Object { $_ }
    $added = @()

    foreach ($directory in $wanted) {
        # Idempotent: running this twice must not append a duplicate, and a machine that has been
        # set up already should come out of it unchanged.
        if ($entries -contains $directory) { continue }
        $entries += $directory
        $added += $directory
    }

    if ($added.Count -eq 0) {
        Write-Good 'They are all already on the user PATH.'
    }
    elseif ($PSCmdlet.ShouldProcess('user PATH', "Add $($added -join ', ')")) {
        [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
        $env:Path = "$env:Path;$($added -join ';')"
        Write-Good "Added $($added -join ', ')"
        Write-Warn 'Open a new terminal before relying on this - the current one inherited the old PATH.'
    }

    # Belt and braces, and what the application itself reads if PATH is wrong anyway.
    if (-not $env:CUDA_PATH -and $state.Cuda) {
        [Environment]::SetEnvironmentVariable('CUDA_PATH', (Split-Path (Split-Path $state.Cuda)), 'User')
    }
}

Write-Step 'Verify'

$state = Test-Everything
if ($state.Missing.Count -gt 0) {
    Write-Warn "Still missing: $($state.Missing -join ', ')"
    Write-Warn 'ONNX Runtime will fall back to the CPU, silently, and everything will take about ten times as long.'
    exit 1
}

Write-Good 'Every required library is present.'
if ($state.TensorRt) { Write-Good "TensorRT is present too: ManualForge will use it ($($state.TensorRt))." }
else { Write-Warn 'No TensorRT, so ManualForge will run on CUDA. That works; TensorRT is about twice as fast.' }

# The only verification that counts is the application's own, because it is the thing whose loader
# has to be satisfied. `gpu` prints which provider it actually got, rather than which it wanted.
$manualforge = Get-Command manualforge -ErrorAction SilentlyContinue
if (-not $manualforge) {
    $published = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe"
    if (Test-Path $published) { $manualforge = $published }
}

if ($manualforge) {
    Write-Host ''
    & $manualforge gpu
}
else {
    Write-Warn 'manualforge is not installed yet, so the last check was not run. After publishing it:'
    Write-Warn '  manualforge gpu     # expect "Active provider : TensorRt" (or "Cuda" without TensorRT)'
}
