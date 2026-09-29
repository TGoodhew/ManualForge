<#
.SYNOPSIS
    Reads the whole library again with the current recogniser, overnight, unattended.

.DESCRIPTION
    The four steps in the README's "Reading the library again when the recogniser improves", in
    order, each only after the one before it succeeded:

      1. run --redo-completed    the files ManualForge recognised end to end, from their originals
      2. doctor                  re-audit the files step 1 changed
      3. repair --redo-before    every repaired page, again
      4. index                   so search sees all of it

    About 13 hours as timed on 28 September 2026 (10½ to 15).

    It is safe to start again. Progress is kept in <library>\_Originals\reread-progress.json: a
    step that finished is not repeated, step 1 carries on with the files it had not reached, and
    step 3 reuses the moment it first began as its --redo-before, so pages already read again are
    skipped. Delete that file to start a fresh pass from the beginning.

    While it runs the PC is kept from sleeping (the screen may still turn off). Everything the
    steps print goes to a log under %LOCALAPPDATA%\ManualForge\reread.

.EXAMPLE
    ./tools/reread-library.ps1 -Plan
    Shows what would run and when it should finish. Runs nothing.

.EXAMPLE
    ./tools/reread-library.ps1
    Starts, or carries on with, the re-read.
#>
[CmdletBinding()]
param(
    [string] $Library = $(if ($env:MANUALFORGE_LIBRARY) { $env:MANUALFORGE_LIBRARY }
                          else { "$env:USERPROFILE\OneDrive\Documents\Manuals" }),

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    # Show the steps, what is already done and the expected finish time, and stop.
    [switch] $Plan
)

$ErrorActionPreference = 'Stop'

# Minutes per step, from the timed samples in the README. Only used to say when it should finish.
$steps = @(
    [pscustomobject]@{ Name = 'redo';   Title = 'Read the 93 finished files again from their originals'; Minutes = 145 }
    [pscustomobject]@{ Name = 'doctor'; Title = 'Re-audit the files that changed';                     Minutes = 5 }
    [pscustomobject]@{ Name = 'repair'; Title = 'Read every repaired page again';                      Minutes = 620 }
    [pscustomobject]@{ Name = 'index';  Title = 'Rebuild the search index';                            Minutes = 10 }
)

# ---------------------------------------------------------------- where things are

if (-not (Test-Path -LiteralPath $Exe)) { throw "manualforge.exe is not at $Exe. Publish it first (README, 'Installing it')." }
if (-not (Test-Path -LiteralPath $Library -PathType Container)) { throw "No library folder at $Library." }

$originals = Join-Path $Library '_Originals'
$progressPath = Join-Path $originals 'reread-progress.json'
$logFolder = Join-Path $env:LOCALAPPDATA 'ManualForge\reread'
New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
$log = Join-Path $logFolder ("reread-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

# The options this script relies on arrived in 108df12. An older build would reject them, or worse,
# run plain --redo and lose the ability to resume.
$help = (& $Exe 2>&1) -join "`n"
foreach ($option in '--redo-completed', '--redo-before') {
    if ($help -notmatch [regex]::Escape($option)) {
        throw "The installed manualforge does not know $option. Publish a build from 108df12 or later."
    }
}
$version = (& $Exe version 2>&1 | Select-Object -First 1)

# ---------------------------------------------------------------- progress, kept with the library

function Read-Progress {
    if (Test-Path -LiteralPath $progressPath) {
        $p = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
        # PowerShell 7 turns an ISO date in JSON into a local DateTime on the way in; put it back
        # into the exact round-trip form, so the cutoff a restart passes is the one first saved
        # and never depends on the machine's date format.
        $exact = { param($v) if ($v -is [datetime]) { $v.ToString('o') } else { $v } }
        return @{
            Done         = @($p.Done | Where-Object { $_ })
            RedoStarted  = [bool]$p.RedoStarted
            RepairCutoff = & $exact $p.RepairCutoff
            Started      = & $exact $p.Started
        }
    }
    return @{ Done = @(); RedoStarted = $false; RepairCutoff = $null; Started = $null }
}

function Save-Progress($progress) {
    [pscustomobject]$progress | ConvertTo-Json | Set-Content -LiteralPath $progressPath -Encoding utf8
}

$progress = Read-Progress

function Write-Line([string] $text) {
    Write-Host $text
    Add-Content -LiteralPath $log -Value $text
}

# ---------------------------------------------------------------- the plan

$remaining = $steps | Where-Object { $progress.Done -notcontains $_.Name }
$finish = (Get-Date).AddMinutes(($remaining | Measure-Object Minutes -Sum).Sum)

Write-Line "ManualForge library re-read"
Write-Line "  Library : $Library"
Write-Line "  Program : $version"
Write-Line "  Log     : $log"
Write-Line "  Progress: $progressPath$(if ($progress.Started) { " (pass begun $($progress.Started))" })"
Write-Line ""
foreach ($step in $steps) {
    $state = if ($progress.Done -contains $step.Name) { 'done' }
             elseif ($step.Name -eq 'redo' -and $progress.RedoStarted) { 'carry on' }
             elseif ($step.Name -eq 'repair' -and $progress.RepairCutoff) { 'carry on' }
             else { 'to do' }
    Write-Line ("  {0,-9} {1,-55} ~{2,4} min" -f $state, $step.Title, $step.Minutes)
}
Write-Line ""
if (-not $remaining) {
    Write-Line "Every step is done. Delete $progressPath to start a fresh pass."
    return
}
Write-Line ("Expected to finish around {0:ddd HH:mm} (10½-15 hours for a whole pass; the repair estimate is +/- a fifth)." -f $finish)

if ($Plan) {
    Write-Line "-Plan given: nothing was run."
    return
}

# ---------------------------------------------------------------- keep the PC awake

# ES_CONTINUOUS | ES_SYSTEM_REQUIRED: no sleep while this runs; the display may still turn off.
# Cleared on the way out, however the script ends.
if (-not ('ManualForge.Power' -as [type])) {
    Add-Type -Namespace ManualForge -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint flags);
'@
}
# Written out as unsigned: PowerShell reads the hex literal 0x80000001 as a negative Int32.
$keepAwake = [Convert]::ToUInt32('80000001', 16)
$allowSleep = [Convert]::ToUInt32('80000000', 16)
[void][ManualForge.Power]::SetThreadExecutionState($keepAwake)

$timings = [ordered]@{}
$partial = [System.Collections.Generic.List[string]]::new()

# manualforge exits 1 both when a command crashed and when it finished with some files or pages
# failed - one bad PDF in 93. Only a crash prints an "error: " line, and only a crash stops the
# night; a partial failure is reported at the end and the rest carries on. 130 is Ctrl+C, 2 a
# mistyped command.
function Invoke-Step([string] $name, [string[]] $arguments) {
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
    $took = (Get-Date) - $started
    $timings[$name] = $took
    Write-Line ("=== {0:HH:mm:ss}  exit {1}, took {2:hh\:mm\:ss}" -f (Get-Date), $code, $took)

    if ($code -eq 0) { return }
    if ($code -eq 1 -and -not $crashed) {
        $partial.Add($name)
        Write-Line "    Finished, but some files or pages failed - see the log. Carrying on."
        return
    }
    throw "Step '$name' stopped (exit code $code). Nothing after it was run. Fix the cause and start the script again: it carries on from here."
}

try {
    if (-not $progress.Started) {
        $progress.Started = (Get-Date).ToString('o')
        Save-Progress $progress
    }

    # 1. The files recognised end to end. --redo-completed only on the first start: it puts every
    #    finished file back in the queue, and given twice it would queue again the ones this pass
    #    has already read. On a restart plain `run` carries on with what is still queued.
    if ($progress.Done -notcontains 'redo') {
        $arguments = @('run', $Library)
        if (-not $progress.RedoStarted) {
            $arguments += '--redo-completed'
            $progress.RedoStarted = $true
            Save-Progress $progress
        }
        Invoke-Step 'redo' $arguments
        $progress.Done += 'redo'; Save-Progress $progress
    }

    # 2. Repair will not attach text to a file that has changed since it was audited.
    if ($progress.Done -notcontains 'doctor') {
        Invoke-Step 'doctor' @('doctor', $Library)
        $progress.Done += 'doctor'; Save-Progress $progress
    }

    # 3. Every repaired page, read again. The cutoff is fixed the first time this step starts, so
    #    a restart skips the pages it has already done.
    if ($progress.Done -notcontains 'repair') {
        if (-not $progress.RepairCutoff) {
            $progress.RepairCutoff = (Get-Date).ToString('o')
            Save-Progress $progress
        }
        Invoke-Step 'repair' @('repair', $Library, '--redo-before', $progress.RepairCutoff, '--include-scans')
        $progress.Done += 'repair'; Save-Progress $progress
    }

    # 4. So search sees all of it.
    if ($progress.Done -notcontains 'index') {
        Invoke-Step 'index' @('index', $Library)
        $progress.Done += 'index'; Save-Progress $progress
    }

    Write-Line ""
    Write-Line "Finished. Step times this session:"
    foreach ($entry in $timings.GetEnumerator()) {
        Write-Line ("  {0,-7} {1:hh\:mm\:ss}" -f $entry.Key, $entry.Value)
    }
    if ($partial.Count -gt 0) {
        Write-Line ""
        Write-Line "Some failures, all in the log: $($partial -join ', ')."
        Write-Line "  Files that failed in 'redo' stay queued; 'manualforge run `"$Library`"' tries them again."
    }
    Write-Line "Earlier searchable copies are kept in $(Join-Path $originals '_superseded'); delete them once you are happy."
}
finally {
    [void][ManualForge.Power]::SetThreadExecutionState($allowSleep)
}
