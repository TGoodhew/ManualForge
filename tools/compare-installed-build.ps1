<#
.SYNOPSIS
    Reads the _compare books with the installed ManualForge and with a build from this repository,
    and says whether every word is the same (#26).

.DESCRIPTION
    The installer publishes the program into one framework-dependent folder, and loads NVIDIA's
    libraries from %LOCALAPPDATA%\ManualForge\gpu. This reads each book with that installed
    manualforge.exe and with this repository's own build of the same commit, on each engine asked
    for, and compares the text each wrote, word for word. Both find the same NVIDIA libraries, so
    what differs is only how the program was built and laid out.

.EXAMPLE
    ./tools/compare-installed-build.ps1
    ./tools/compare-installed-build.ps1 -Engines tensorrt -Books typical-test.pdf
#>
[CmdletBinding()]
param(
    [string[]] $Engines = @('tensorrt', 'cuda'),
    [string[]] $Books,
    [string] $Installed = (Join-Path $env:LOCALAPPDATA 'Programs\ManualForge\manualforge.exe'),
    [string] $Work = (Join-Path $env:TEMP 'manualforge-installed-build')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$compare = Join-Path $repo '_compare'
if (-not $Books) {
    $Books = @(Get-ChildItem $compare -Filter '*-test.pdf' | ForEach-Object Name) + '431C-OSM.pdf'
}
if (-not (Test-Path $Installed)) { throw "ManualForge is not installed at $Installed." }

Write-Host 'Building this repository (Release)...'
dotnet build (Join-Path $repo 'src\ManualForge.Cli\ManualForge.Cli.csproj') -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
$built = Get-ChildItem (Join-Path $repo 'src\ManualForge.Cli\bin\Release') -Recurse -Filter 'manualforge.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName

$builds = [ordered]@{ installed = $Installed; repository = $built }
foreach ($name in $builds.Keys) {
    Write-Host ("{0,-11} {1}  ({2})" -f $name, (& $builds[$name] version | Select-Object -First 1), $builds[$name])
}
$commits = @($builds.Values | ForEach-Object { (& $_ version | Select-Object -First 1) -replace '^.*\+|\.dirty$', '' } | Sort-Object -Unique)
if ($commits.Count -ne 1 -and (git -C $repo diff --stat $commits[0] $commits[1] -- src)) {
    Write-Warning "The two builds are of commits whose code differs: $($commits -join ', ')"
}

New-Item -ItemType Directory -Force $Work | Out-Null
$rows = foreach ($engine in $Engines) {
    foreach ($book in $Books) {
        $source = Join-Path $compare $book
        $words = @{}
        $seconds = @{}
        foreach ($name in $builds.Keys) {
            $stem = Join-Path $Work ("{0}-{1}-{2}" -f [IO.Path]::GetFileNameWithoutExtension($book), $engine, $name)
            $started = Get-Date
            $output = & $builds[$name] ocr $source --engine $engine --out "$stem.pdf" --overwrite --text "$stem.txt" 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0 -and $output -match 'owner password') {
                # `ocr` leaves a locked file alone; a library run flattens it first, so read it that way,
                # in a library of its own, and take the words from the searchable copy it leaves. Its text
                # layer is good, which a run would skip, so it is told to read it again.
                $lib = "$stem-library"
                if (Test-Path $lib) { Remove-Item -LiteralPath $lib -Recurse -Force }
                New-Item -ItemType Directory -Force $lib | Out-Null
                Copy-Item -LiteralPath $source $lib
                $output = & $builds[$name] run $lib --engine $engine --policy 'GoodText=redo' 2>&1 | Out-String
                if ($LASTEXITCODE -ne 0 -or $output -notmatch 'Completed\s*:\s*1') { throw "$name failed on $book ($engine):`n$output" }
                dotnet run (Join-Path $PSScriptRoot 'pdf-words.cs') -- (Join-Path $lib $book) | Set-Content "$stem.txt" -Encoding UTF8
            }
            elseif ($LASTEXITCODE -ne 0) { throw "$name failed on $book ($engine):`n$output" }
            if ($output -notmatch "on $engine") { Write-Warning "$name did not run on $engine for ${book}" }
            $seconds[$name] = ((Get-Date) - $started).TotalSeconds
            $words[$name] = @((Get-Content "$stem.txt" -Raw) -split '\s+' | Where-Object { $_ })
        }
        $differences = @(Compare-Object $words.installed $words.repository -SyncWindow 0).Count
        $row = [pscustomobject]@{
            Engine     = $engine
            Book       = $book
            Installed  = $words.installed.Count
            Repository = $words.repository.Count
            Identical  = ($differences -eq 0)
            Seconds    = '{0:N0} / {1:N0}' -f $seconds.installed, $seconds.repository
        }
        $row | Format-Table -HideTableHeaders | Out-String | ForEach-Object { Write-Host $_.Trim() }
        $row
    }
}

Write-Host ''
$rows | Format-Table -AutoSize
$same = @($rows | Where-Object Identical).Count
Write-Host ("{0} of {1} identical word for word." -f $same, $rows.Count)
