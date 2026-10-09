<#
.SYNOPSIS
    Puts every original back, removes the old run's state, and reads the whole library again from
    scratch, unattended (#25).

.DESCRIPTION
    The cleanup and the run from issue #25, in order, each only after the one before it succeeded:

      1. preflight   nothing that holds the library open may be running
      2. snapshot    copy <library>\_Originals out to -Snapshot, and check the copy
      3. restore     write every kept original back over its library file
      4. verify      every restored file matches its original byte for byte
      5. clean       no ManualForge output is left in the library (bar the known exception)
      6. remove      delete _Originals and BASELINE, then check the end state
      7. survey      record the class table under the policy
      8. run         every file the policy marks, from the original
      9. doctor      audit the whole library
     10. repair      every flagged page, scans included
     11. index       a fresh search index

    Steps 2-6 change nothing irreversibly until step 6, and step 6 runs only when the snapshot was
    verified and steps 4 and 5 came back clean. Any surprise stops the script with the reason; the
    library is then exactly as the last finished step left it.

    It is safe to start again. Progress is kept OUTSIDE the library, in
    %LOCALAPPDATA%\ManualForge\reread\from-scratch-progress.json, because step 6 deletes the folder
    the re-read script keeps its progress in. A finished step is not repeated, and `run` carries on
    with what is still queued. Delete that file only to begin a new cleanup from the start.

    While it runs the PC is kept from sleeping. Everything the steps print goes to a log under
    %LOCALAPPDATA%\ManualForge\reread.

.EXAMPLE
    ./tools/read-library-from-scratch.ps1 -Plan
    Shows the steps, what is done, and when it should finish. Changes nothing.

.EXAMPLE
    ./tools/read-library-from-scratch.ps1
    Starts, or carries on with, the cleanup and the run.
#>
[CmdletBinding()]
param(
    [string] $Library = $(if ($env:MANUALFORGE_LIBRARY) { $env:MANUALFORGE_LIBRARY }
                          else { "$env:USERPROFILE\OneDrive\Documents\Manuals" }),

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    # Where _Originals is copied before anything is put back. Fixed the first time the script starts.
    [string] $Snapshot,

    # Decided on #25: the default (ImageOnly=ocr) plus the two classes the default skips.
    [string] $Policy = 'ImageOnly=ocr,UnreadableTextLayer=redo,SuspectText=redo',

    # Library files that carry a PDFsharp marker but are originals as received (#25).
    [string[]] $KnownPdfsharpOriginals = @('HP_8340A_OptionH02_Supplement.pdf'),

    # Where progress and logs go. A test points this somewhere else.
    [string] $StateFolder = (Join-Path $env:LOCALAPPDATA 'ManualForge\reread'),

    # Show the steps, what is already done and the expected finish time, and stop.
    [switch] $Plan
)

$ErrorActionPreference = 'Stop'

# Minutes per step. run: about 23,000 pages at 85-90 a minute on the RTX 5070 Ti (#31). doctor: the
# whole library at about 1,400 pages a minute (9 Oct). repair: about 20,000 flagged pages at 38 a
# minute (9 Oct, scans). Only used to say when it should finish.
$steps = @(
    [pscustomobject]@{ Name = 'preflight'; Title = 'Check nothing holds the library open';           Minutes = 0 }
    [pscustomobject]@{ Name = 'snapshot';  Title = 'Copy _Originals out, and check the copy';         Minutes = 5 }
    [pscustomobject]@{ Name = 'restore';   Title = 'Put every original back over its library file';   Minutes = 3 }
    [pscustomobject]@{ Name = 'verify';    Title = 'Check each restored file byte for byte';          Minutes = 3 }
    [pscustomobject]@{ Name = 'clean';     Title = 'Check no ManualForge output is left';             Minutes = 5 }
    [pscustomobject]@{ Name = 'remove';    Title = 'Delete _Originals and BASELINE, check the result'; Minutes = 1 }
    [pscustomobject]@{ Name = 'survey';    Title = 'Classify the library under the policy';           Minutes = 5 }
    [pscustomobject]@{ Name = 'run';       Title = 'Read every marked file from its original';        Minutes = 270 }
    [pscustomobject]@{ Name = 'doctor';    Title = 'Audit the whole library';                         Minutes = 80 }
    [pscustomobject]@{ Name = 'repair';    Title = 'Read every flagged page, scans included';          Minutes = 540 }
    [pscustomobject]@{ Name = 'index';     Title = 'Build the search index';                          Minutes = 10 }
)

# ---------------------------------------------------------------- where things are

if (-not (Test-Path -LiteralPath $Exe)) { throw "manualforge.exe is not at $Exe. Publish it first (README, 'Install')." }
if (-not (Test-Path -LiteralPath $Library -PathType Container)) { throw "No library folder at $Library." }

$Library = (Resolve-Path -LiteralPath $Library).Path.TrimEnd('\')
$originals = Join-Path $Library '_Originals'
$baseline = Join-Path $Library 'BASELINE'
New-Item -ItemType Directory -Force -Path $StateFolder | Out-Null
$progressPath = Join-Path $StateFolder 'from-scratch-progress.json'
$log = Join-Path $StateFolder ("from-scratch-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$version = (& $Exe version 2>&1 | Select-Object -First 1)

function Write-Line([string] $text) {
    Write-Host $text
    Add-Content -LiteralPath $log -Value $text
}

# ---------------------------------------------------------------- progress, kept outside the library

function Read-Progress {
    if (Test-Path -LiteralPath $progressPath) {
        $p = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
        return @{
            Done        = @($p.Done | Where-Object { $_ })
            Library     = $p.Library
            Snapshot    = $p.Snapshot
            LibraryPdfs = $p.LibraryPdfs
            Started     = if ($p.Started -is [datetime]) { $p.Started.ToString('o') } else { $p.Started }
        }
    }
    return @{ Done = @(); Library = $null; Snapshot = $null; LibraryPdfs = $null; Started = $null }
}

function Save-Progress($progress) {
    [pscustomobject]$progress | ConvertTo-Json | Set-Content -LiteralPath $progressPath -Encoding utf8
}

$progress = Read-Progress
if ($progress.Library -and $progress.Library -ne $Library) {
    throw "The progress file is for $($progress.Library), not $Library. Delete $progressPath to start over."
}
if (-not $progress.Snapshot) {
    $progress.Snapshot = if ($Snapshot) { $Snapshot }
                         else { "$env:USERPROFILE\ManualForge-baselines\$(Get-Date -Format yyyy-MM-dd)-before-fresh-run" }
}
$snapshotOriginals = Join-Path $progress.Snapshot '_Originals'

# ---------------------------------------------------------------- the plan

$remaining = $steps | Where-Object { $progress.Done -notcontains $_.Name }
$finish = (Get-Date).AddMinutes(($remaining | Measure-Object Minutes -Sum).Sum)

Write-Line "ManualForge: read the library again from scratch (#25)"
Write-Line "  Library : $Library"
Write-Line "  Program : $version"
Write-Line "  Policy  : $Policy"
Write-Line "  Snapshot: $($progress.Snapshot)"
Write-Line "  Log     : $log"
Write-Line "  Progress: $progressPath$(if ($progress.Started) { " (begun $($progress.Started))" })"
Write-Line ""
foreach ($step in $steps) {
    $state = if ($progress.Done -contains $step.Name) { 'done' } else { 'to do' }
    Write-Line ("  {0,-6} {1,-10} {2,-50} ~{3,4} min" -f $state, $step.Name, $step.Title, $step.Minutes)
}
Write-Line ""
if (-not $remaining) {
    Write-Line "Every step is done. Delete $progressPath only to begin another cleanup from the start."
    return
}
Write-Line ("Expected to finish around {0:ddd HH:mm}. The repair estimate is the least certain: +/- a fifth." -f $finish)

if ($Plan) {
    Write-Line "-Plan given: nothing was run."
    return
}

# ---------------------------------------------------------------- helpers

# Every PDF in the library itself: not in _Originals, not in BASELINE, and not the folder that is
# named like a PDF (Get-ChildItem -File).
function Get-LibraryPdfs {
    Get-ChildItem -LiteralPath $Library -Recurse -File -Filter *.pdf |
        Where-Object { -not $_.FullName.StartsWith("$originals\", [StringComparison]::OrdinalIgnoreCase) -and
                       -not $_.FullName.StartsWith("$baseline\", [StringComparison]::OrdinalIgnoreCase) }
}

# The originals kept in _Originals, minus the superseded copies and the broken file.
function Get-KeptOriginals {
    Get-ChildItem -LiteralPath $originals -Recurse -File -Filter *.pdf |
        Where-Object { $_.FullName -notmatch '\\_Originals\\(_superseded|broken)\\' }
}

function Stop-Night([string] $reason) {
    throw "$reason Nothing after this step was run, and the library is as the last finished step left it."
}

# robocopy: 0-7 is success (files copied, extra files present and so on), 8 and above is a failure.
function Invoke-Robocopy([string[]] $arguments) {
    Write-Line ("    robocopy {0}" -f ($arguments -join ' '))
    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) { Stop-Night "robocopy failed with exit code $LASTEXITCODE." }
}

# ManualForge output carries "PDFsharp" in its bytes. Originals as received do not, bar the known few.
function Get-PdfsharpFiles {
    Get-LibraryPdfs | Where-Object { Select-String -LiteralPath $_.FullName -Pattern 'PDFsharp' -SimpleMatch -Quiet }
}

$timings = [ordered]@{}
$partial = [System.Collections.Generic.List[string]]::new()

function Complete-Step([string] $name, [datetime] $started) {
    $timings[$name] = (Get-Date) - $started
    $progress.Done += $name
    Save-Progress $progress
    Write-Line ("=== {0:HH:mm:ss}  {1} done, took {2:hh\:mm\:ss}" -f (Get-Date), $name, $timings[$name])
}

# As in reread-library.ps1: exit 1 without an "error: " line is a finished step with some files or
# pages failed, reported at the end; anything else stops the night.
function Invoke-Manualforge([string] $name, [string[]] $arguments) {
    $started = Get-Date
    Write-Line ""
    Write-Line ("=== {0:HH:mm:ss}  manualforge {1}" -f $started, ($arguments -join ' '))
    $lines = & $Exe @arguments 2>&1 | ForEach-Object {
        $line = "$_"
        Write-Host $line
        Add-Content -LiteralPath $log -Value $line
        $line
    }
    $code = $LASTEXITCODE
    $crashed = [bool](@($lines) -match '^error: ')
    if ($code -eq 1 -and -not $crashed) {
        $partial.Add($name)
        Write-Line "    Finished, but some files or pages failed - see the log. Carrying on."
    }
    elseif ($code -ne 0) {
        Stop-Night "Step '$name' stopped (exit code $code). Fix the cause and start the script again: it carries on from here."
    }
    Complete-Step $name $started
}

# ---------------------------------------------------------------- keep the PC awake

if (-not ('ManualForge.Power' -as [type])) {
    Add-Type -Namespace ManualForge -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint flags);
'@
}
$keepAwake = [Convert]::ToUInt32('80000001', 16)
$allowSleep = [Convert]::ToUInt32('80000000', 16)
[void][ManualForge.Power]::SetThreadExecutionState($keepAwake)

try {
    if (-not $progress.Started) {
        $progress.Started = (Get-Date).ToString('o')
        $progress.Library = $Library
        Save-Progress $progress
    }

    # 1. Anything holding the library open would make the deletes fail half way, or keep reading an
    #    index that is about to disappear. Checked on every start, done or not.
    $running = @(Get-Process -Name 'ManualForge*', 'manualforge' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Stop-Night ("Close these first (Claude Desktop, VS Code's Claude Code session, the ManualForge app): " +
            (($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ') + '.')
    }
    if ($progress.Done -notcontains 'preflight') { Complete-Step 'preflight' (Get-Date) }

    # 2. The way back if anything after this goes wrong. Its index copy is also the "before" for the
    #    measuring harnesses.
    if ($progress.Done -notcontains 'snapshot') {
        $started = Get-Date
        Write-Line ""
        Write-Line ("=== {0:HH:mm:ss}  snapshot to {1}" -f $started, $snapshotOriginals)
        if (-not (Test-Path -LiteralPath $originals)) { Stop-Night "There is no $originals to snapshot." }
        Invoke-Robocopy @($originals, $snapshotOriginals, '/E', '/COPY:DAT', '/DCOPY:T', '/R:1', '/W:1', '/NP', '/NFL', '/NDL')
        $source = Get-ChildItem -LiteralPath $originals -Recurse -File
        $copy = Get-ChildItem -LiteralPath $snapshotOriginals -Recurse -File
        $sourceBytes = ($source | Measure-Object Length -Sum).Sum
        $copyBytes = ($copy | Measure-Object Length -Sum).Sum
        Write-Line ("    {0:N0} files, {1:N0} bytes copied ({2:N0} files, {3:N0} bytes in _Originals)" -f $copy.Count, $copyBytes, $source.Count, $sourceBytes)
        if ($copy.Count -ne $source.Count -or $copyBytes -ne $sourceBytes) { Stop-Night "The snapshot does not match _Originals." }
        $progress.LibraryPdfs = @(Get-LibraryPdfs).Count
        Write-Line ("    {0:N0} PDFs in the library itself, to be checked again after the cleanup" -f $progress.LibraryPdfs)
        Complete-Step 'snapshot' $started
    }

    # 3. Each original over the library file at the same relative path, with its own timestamps.
    if ($progress.Done -notcontains 'restore') {
        $started = Get-Date
        Write-Line ""
        Write-Line ("=== {0:HH:mm:ss}  restore originals" -f $started)
        Invoke-Robocopy @($originals, $Library, '*.pdf', '/S', '/COPY:DAT', '/R:1', '/W:1', '/NP', '/NDL',
            '/XD', (Join-Path $originals '_superseded'), (Join-Path $originals 'broken'))
        Complete-Step 'restore' $started
    }

    # 4. Byte for byte.
    if ($progress.Done -notcontains 'verify') {
        $started = Get-Date
        Write-Line ""
        Write-Line ("=== {0:HH:mm:ss}  verify every restored file" -f $started)
        $checked = 0
        $differs = foreach ($kept in Get-KeptOriginals) {
            $checked++
            $target = Join-Path $Library $kept.FullName.Substring($originals.Length + 1)
            if (-not (Test-Path -LiteralPath $target) -or
                (Get-FileHash -LiteralPath $kept.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { $target }
        }
        Write-Line ("    {0:N0} originals checked" -f $checked)
        if ($differs) {
            $differs | ForEach-Object { Write-Line "    DIFFERS: $_" }
            Stop-Night "Some library files do not match their originals."
        }
        Complete-Step 'verify' $started
    }

    # 5. Nothing ManualForge wrote may be left, or it would be taken for an original.
    if ($progress.Done -notcontains 'clean') {
        $started = Get-Date
        Write-Line ""
        Write-Line ("=== {0:HH:mm:ss}  look for ManualForge output left in the library" -f $started)
        $left = @(Get-PdfsharpFiles | Where-Object { $KnownPdfsharpOriginals -notcontains $_.Name })
        if ($left.Count -gt 0) {
            $left | ForEach-Object { Write-Line "    STILL OURS: $($_.FullName)" }
            Stop-Night "These files carry a PDFsharp marker and have no original to put back."
        }
        Write-Line "    none, bar the known originals: $($KnownPdfsharpOriginals -join ', ')"
        Complete-Step 'clean' $started
    }

    # 6. Only now, with the snapshot checked and the library shown to hold originals only.
    if ($progress.Done -notcontains 'remove') {
        $started = Get-Date
        Write-Line ""
        Write-Line ("=== {0:HH:mm:ss}  delete _Originals and BASELINE" -f $started)
        foreach ($required in 'snapshot', 'verify', 'clean') {
            if ($progress.Done -notcontains $required) { Stop-Night "Refusing to delete anything: '$required' has not passed." }
        }
        foreach ($folder in $originals, $baseline) {
            if (Test-Path -LiteralPath $folder) {
                Remove-Item -LiteralPath $folder -Recurse -Force
                Write-Line "    deleted $folder"
            }
        }
        if ((Test-Path -LiteralPath $originals) -or (Test-Path -LiteralPath $baseline)) { Stop-Night "A folder could not be deleted." }
        $now = @(Get-LibraryPdfs).Count
        Write-Line ("    {0:N0} PDFs in the library ({1:N0} before the cleanup)" -f $now, $progress.LibraryPdfs)
        if ($now -ne $progress.LibraryPdfs) { Stop-Night "The library holds a different number of PDFs than before the cleanup." }
        $left = @(Get-PdfsharpFiles | Where-Object { $KnownPdfsharpOriginals -notcontains $_.Name })
        if ($left.Count -gt 0) { Stop-Night "ManualForge output is still in the library: $($left.FullName -join ', ')." }
        Complete-Step 'remove' $started
    }

    # 7-11. The run itself. `run` surveys again on every start, so a restart carries on with what
    #       is still queued.
    if ($progress.Done -notcontains 'survey') { Invoke-Manualforge 'survey' @('survey', $Library, '--policy', $Policy) }
    if ($progress.Done -notcontains 'run')    { Invoke-Manualforge 'run'    @('run', $Library, '--policy', $Policy) }
    if ($progress.Done -notcontains 'doctor') { Invoke-Manualforge 'doctor' @('doctor', $Library) }
    if ($progress.Done -notcontains 'repair') { Invoke-Manualforge 'repair' @('repair', $Library, '--include-scans') }
    if ($progress.Done -notcontains 'index')  { Invoke-Manualforge 'index'  @('index', $Library) }

    Write-Line ""
    Write-Line "Finished. Step times this session:"
    foreach ($entry in $timings.GetEnumerator()) {
        Write-Line ("  {0,-9} {1:hh\:mm\:ss}" -f $entry.Key, $entry.Value)
    }
    if ($partial.Count -gt 0) {
        Write-Line ""
        Write-Line "Some failures, all in the log: $($partial -join ', ')."
        Write-Line "  Files that failed in 'run' stay queued; 'manualforge run `"$Library`" --policy $Policy' tries them again."
    }
    Write-Line "The snapshot of the old _Originals is at $($progress.Snapshot); delete it once you are happy (#25, 'Afterwards')."
}
finally {
    [void][ManualForge.Power]::SetThreadExecutionState($allowSleep)
}
