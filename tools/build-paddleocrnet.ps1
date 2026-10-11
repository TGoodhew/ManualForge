<#
.SYNOPSIS
    Brings upstream PaddleOcrNet's changes into our fork, rebuilds it, and refreshes the packages
    ManualForge builds against.

.DESCRIPTION
    ManualForge uses a fork of PaddleOcrNet (github.com/TGoodhew/PaddleOcrNet, branch
    feature/tensorrt) because the TensorRT execution provider it adds has not been released upstream
    yet (FarhanLodi/PaddleOcrNet#11; #53 here). The fork is kept so that upstream can take it as a pull
    request, and so that what upstream publishes can be taken in here. This script is the second half:

      1. Fetches upstream and merges upstream/main into the fork's branch. A merge, not a rebase, so
         the branch's history stays intact for the pull request. On a conflict it stops and says so;
         resolve it in the fork, commit, and run this again.
      2. Builds the fork and runs its unit tests (the integration suite needs models and a GPU, and is
         run by hand).
      3. Packs PaddleOcrNet and PaddleOcrNet.Gpu as <upstream version>-tensorrt.<n>, with n one past the
         last one in packages/. Every pack gets a new version because NuGet caches a package by its
         version: re-using one means the old build is what gets restored.
      4. Replaces packages/ with the new pair and pins Directory.Packages.props to it.

    Then build and test ManualForge, and commit packages/ and Directory.Packages.props together.

    When upstream releases TensorRT support, stop using this: delete packages/ and the "local" source
    in nuget.config, and pin the released version.

.PARAMETER ForkPath
    The fork's clone. Default: PaddleOcrNet beside this repository.

.PARAMETER NoMerge
    Rebuild and repack the fork as it stands, without fetching upstream.

.PARAMETER Push
    Push the fork's branch to origin (the fork) after a successful merge and test run.

.EXAMPLE
    ./tools/build-paddleocrnet.ps1

.EXAMPLE
    ./tools/build-paddleocrnet.ps1 -NoMerge     # after changing the fork yourself
#>
[CmdletBinding()]
param(
    [string] $ForkPath = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\PaddleOcrNet'),
    [string] $Branch = 'feature/tensorrt',
    [switch] $NoMerge,
    [switch] $Push
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$packages = Join-Path $repo 'packages'
$props = Join-Path $repo 'Directory.Packages.props'
$fork = (Resolve-Path $ForkPath).Path

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Good([string] $text) { Write-Host "   $text" -ForegroundColor Green }
function Write-Warn([string] $text) { Write-Host "   $text" -ForegroundColor Yellow }
function Invoke-Git { & git -C $fork @args; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." } }

Write-Step "Fork: $fork ($Branch)"

if (& git -C $fork status --porcelain) { throw 'The fork has uncommitted changes. Commit or stash them first.' }
if ((& git -C $fork rev-parse --abbrev-ref HEAD) -ne $Branch) { Invoke-Git checkout $Branch }

# The upstream remote must never be pushed to by accident.
if ((& git -C $fork remote get-url --push upstream) -ne 'DISABLED') { Invoke-Git remote set-url --push upstream DISABLED }

if (-not $NoMerge) {
    Write-Step 'Upstream'
    Invoke-Git fetch upstream --tags
    $behind = [int](& git -C $fork rev-list --count "$Branch..upstream/main")
    if ($behind -eq 0) {
        Write-Good 'Up to date with upstream/main.'
    }
    else {
        Write-Host "   upstream/main has $behind commit(s) the fork does not:"
        & git -C $fork log --oneline "$Branch..upstream/main" | ForEach-Object { Write-Host "     $_" }
        & git -C $fork merge --no-edit upstream/main
        if ($LASTEXITCODE -ne 0) {
            throw 'The merge conflicted. Resolve it in the fork, commit, and run this again with -NoMerge.'
        }
        Write-Good 'Merged.'
    }

    # Once upstream has its own TensorRT provider, the fork has done its job.
    if (& git -C $fork grep -l 'TensorRt = 5' upstream/main -- 'src/PaddleOcrNet/Services/OcrExecutionProvider.cs' 2>$null) {
        Write-Warn 'upstream/main now has OcrExecutionProvider.TensorRt. Check whether a release carries it, and if so switch ManualForge back to the nuget.org package.'
    }
}

Write-Step 'Build and unit tests'
& dotnet build (Join-Path $fork 'PaddleOcrNet.sln') -c Release -warnaserror | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw 'The fork does not build cleanly (warnings are errors here, as upstream asks).' }
& dotnet test (Join-Path $fork 'test\PaddleOcrNet.Tests\PaddleOcrNet.Tests.csproj') -c Release --no-build --filter 'Category!=Integration' | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw 'The fork''s unit tests failed.' }

if ($Push) {
    Write-Step 'Push'
    Invoke-Git push origin $Branch
}

Write-Step 'Pack'
$csproj = [xml](Get-Content (Join-Path $fork 'src\PaddleOcrNet\PaddleOcrNet.csproj') -Raw)
$base = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $base) { throw 'Could not read <Version> from the fork''s PaddleOcrNet.csproj.' }

$pattern = '^PaddleOcrNet\.' + [regex]::Escape($base) + '-tensorrt\.(\d+)\.nupkg$'
$last = Get-ChildItem $packages -Filter '*.nupkg' -ErrorAction SilentlyContinue |
    ForEach-Object { if ($_.Name -match $pattern) { [int]$Matches[1] } } |
    Measure-Object -Maximum
$version = "$base-tensorrt.$([int]$last.Maximum + 1)"
$commit = (& git -C $fork rev-parse --short HEAD)

$staging = Join-Path ([IO.Path]::GetTempPath()) "paddleocrnet-pack-$version"
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
foreach ($project in 'src\PaddleOcrNet\PaddleOcrNet.csproj', 'src\PaddleOcrNet.Gpu\PaddleOcrNet.Gpu.csproj') {
    & dotnet pack (Join-Path $fork $project) -c Release --no-build -p:Version=$version -o $staging | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}

New-Item -ItemType Directory -Force -Path $packages | Out-Null
Get-ChildItem $packages -Filter 'PaddleOcrNet*.nupkg' | Remove-Item
Copy-Item (Join-Path $staging '*.nupkg') $packages
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Good "Packed $version from fork commit $commit into packages/."

$text = Get-Content $props -Raw
$text = $text -replace '(<PackageVersion Include="PaddleOcrNet(\.Gpu)?" Version=")[^"]+(")', "`${1}$version`${3}"
Set-Content -Path $props -Value $text -NoNewline
Write-Good "Directory.Packages.props now pins $version."

Write-Step 'Next'
Write-Host '   dotnet build ManualForge.slnx; dotnet test ManualForge.slnx'
Write-Host "   git add packages Directory.Packages.props; git commit -m 'Take PaddleOcrNet $version (fork $commit)'"
