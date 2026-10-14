<#
.SYNOPSIS
    Runs the installer's Windows Sandbox test, waits for it, prints its summary, and makes sure the sandbox
    is closed afterwards (#84).

.DESCRIPTION
    Starts artifacts\installer\sandbox\test-in-sandbox.wsb, which tools\build-installer.ps1 writes, and
    follows the test's steps as it runs installer\test-in-sandbox.ps1. The sandbox shuts itself down when
    the test ends; if it is still open a minute after the summary - or the test has not finished within
    -TimeoutMinutes - this closes it, so no sandbox is left behind to block the next run.

    Exits 0 when every check passed, and 1 when one failed, the test never finished, or its summary can't
    be read (a host crash while the sandbox is writing can leave it full of null bytes).

.EXAMPLE
    ./tools/invoke-sandbox-test.ps1
    ./tools/invoke-sandbox-test.ps1 -TimeoutMinutes 60
#>
[CmdletBinding()]
param(
    [int] $TimeoutMinutes = 30,
    [string] $SandboxDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\installer\sandbox')
)

$ErrorActionPreference = 'Stop'
$wsb = Join-Path $SandboxDir 'test-in-sandbox.wsb'
$results = Join-Path $SandboxDir 'results'
$summary = Join-Path $results 'summary.txt'
$transcript = Join-Path $results 'transcript.txt'

function Get-Sandboxes {
    $listed = try { (& wsb.exe list --raw 2>$null | Out-String | ConvertFrom-Json).WindowsSandboxEnvironments } catch { @() }
    @($listed | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.Id } } | Where-Object { $_ })
}

function Test-SandboxOpen { (Get-Sandboxes) -or (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) }

function Close-Sandbox {
    foreach ($id in Get-Sandboxes) { & wsb.exe stop --id $id | Out-Null }
    # The window can outlive the sandbox it showed.
    for ($i = 0; $i -lt 30 -and (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue); $i++) {
        Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

function Test-Fresh([string] $path) { (Test-Path $path) -and (Get-Item $path).LastWriteTime -gt $started }

if (-not (Test-Path $wsb)) { throw "No $wsb. Run tools\build-installer.ps1 first." }
# Windows runs one sandbox at a time, and one already open may be someone's own.
if (Test-SandboxOpen) { throw 'A Windows Sandbox is already open. Close it first.' }

# What the last run left would otherwise read as this one's.
New-Item -ItemType Directory -Force $results | Out-Null
Get-ChildItem $results -File | Remove-Item -Force

$started = Get-Date
Write-Host "Started $(Split-Path $wsb -Leaf) at $($started.ToString('HH:mm:ss')); waiting up to $TimeoutMinutes min."
Start-Process $wsb

$deadline = $started.AddMinutes($TimeoutMinutes)
$shown = 0
while ((Get-Date) -lt $deadline -and -not (Test-Fresh $summary)) {
    # Progress: each step the test starts, and each check, as they happen.
    if (Test-Fresh $transcript) {
        $steps = @(Get-Content $transcript -ErrorAction SilentlyContinue | Where-Object { $_ -like '== *' -or $_ -match '^(PASS|FAIL)  ' })
        $steps | Select-Object -Skip $shown | ForEach-Object { Write-Host ("  {0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), ($_ -replace '^== ', '').Trim()) }
        $shown = [Math]::Max($shown, $steps.Count)
    }
    Start-Sleep -Seconds 5
}
$finished = Test-Fresh $summary

# The sandbox shuts itself down after writing the summary; give it a minute, then close what is left.
for ($i = 0; $i -lt 60 -and $finished -and (Test-SandboxOpen); $i++) { Start-Sleep -Seconds 1 }
if (Test-SandboxOpen) {
    Write-Host $(if ($finished) { 'The sandbox did not close itself; closing it.' } else { "No summary after $TimeoutMinutes min; closing the sandbox." }) -ForegroundColor Yellow
    foreach ($said in 'exited.txt', 'shutdown.txt') {
        $path = Join-Path $results $said
        Write-Host ("  {0}: {1}" -f $said, $(if (Test-Path $path) { (Get-Content $path -Raw).Trim() } else { 'not written' }))
    }
    Close-Sandbox
    if (Test-SandboxOpen) { Write-Host 'The sandbox is still open; close it by hand.' -ForegroundColor Red }
}
else { Write-Host 'The sandbox closed itself.' }

if (-not $finished) {
    Write-Host $(if (Test-Fresh $transcript) { "The test did not finish. What it got to is in $transcript." }
                 else { 'The test never started: the sandbox wrote nothing. Run it again.' }) -ForegroundColor Red
    exit 1
}

$text = try { Get-Content $summary -Raw -Encoding UTF8 } catch { '' }
if ($text -match "`0" -or $text -notmatch '(?m)^\d+ checks, \d+ failed') {
    Write-Host "The summary can't be read, so the run counts as failed: $summary" -ForegroundColor Red
    exit 1
}
Write-Host ("Finished in {0:N1} min. Results: {1}" -f ((Get-Item $summary).LastWriteTime - $started).TotalMinutes, $results)
Write-Host ''
$lines = $text -split '\r?\n'
$lines | ForEach-Object {
    Write-Host $_ -ForegroundColor $(if ($_ -like 'FAIL*') { 'Red' } elseif ($_ -like 'PASS*') { 'Green' } else { 'Gray' })
}
if ($lines | Where-Object { $_ -like 'FAIL*' }) { exit 1 }
exit 0
