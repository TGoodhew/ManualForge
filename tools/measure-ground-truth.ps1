<#
.SYNOPSIS
    Runs the 33 hand-read ground-truth strings against a search index and records where the
    correct page ranked.

.DESCRIPTION
    docs/GROUND-TRUTH-54845A.md carries the scores; this produces them. It exists so that a
    before-and-after comparison is the same measurement twice rather than two afternoons of
    typing queries, and so the "before" survives the index being rebuilt over the top of it.

    Every query is asked at --limit 200, well past the ten results `search` prints by default,
    because a correct answer at rank 16 is a ranking result and not a miss. The report scores
    both: found at all, and found in the first ten and twenty-five.

    The index is not modified. Point --Index at a copied database to measure an old one.

.EXAMPLE
    ./tools/measure-ground-truth.ps1 -Label before -Out baseline/ground-truth-before.md
#>
[CmdletBinding()]
param(
    # The library folder. Its default index is used unless -Index says otherwise.
    [string] $Library = $(if ($env:MANUALFORGE_LIBRARY) { $env:MANUALFORGE_LIBRARY }
                          else { "$env:USERPROFILE\OneDrive\Documents\Manuals" }),

    # A specific index database, for measuring a snapshot taken before some change.
    [string] $Index,

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    [string] $Queries = (Join-Path $PSScriptRoot 'ground-truth-54845A.tsv'),

    # How deep to look. 10 is what `search` shows; 200 is deep enough to tell a ranking
    # problem from a retrieval one.
    [int] $Limit = 200,

    # Written as a markdown report. Printed to the console as well either way.
    [string] $Out,

    # Goes in the report header: "before", "after full repair", and so on.
    [string] $Label = 'unlabelled',

    # Pass an instrument model the way a caller who knows it would. Every string in the table comes
    # from one manual, so this measures what the hint is worth to somebody holding the instrument.
    [string] $Model,

    # Extra arguments handed to every search, for trying a ranking change without editing this file.
    # e.g. -Extra @('--rank-labels','1.4')
    [string[]] $Extra = @()
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "No manualforge.exe at $Exe. Publish it, or pass -Exe." }
if (-not (Test-Path $Queries)) { throw "No query file at $Queries." }

$indexPath = if ($Index) { $Index } else { Join-Path $Library '_Originals\manualforge-index.db' }
if (-not (Test-Path $indexPath)) { throw "No index at $indexPath." }

$indexFile = Get-Item $indexPath
$commit = (& git -C $PSScriptRoot rev-parse --short HEAD 2>$null)

$cases = Get-Content $Queries |
    Where-Object { $_ -notmatch '^\s*#' -and $_.Trim() } |
    ForEach-Object {
        $f = $_ -split "`t"
        [pscustomobject]@{ Page = [int]$f[0]; Document = $f[1]; Query = $f[2] }
    }

Write-Host "Index   : $indexPath ($([math]::Round($indexFile.Length / 1MB, 1)) MB, written $($indexFile.LastWriteTime))"
Write-Host "Queries : $($cases.Count) from $Queries, asked at --limit $Limit"
Write-Host ""

$results = foreach ($case in $cases) {
    # Results print as "  <title>  page <n>" followed by snippet and path lines. Rank is the
    # order they come back in, so count title lines and stop at the expected page.
    $arguments = @('search', $case.Query, '--index', $indexPath, '--limit', $Limit)
    if ($Model) { $arguments += @('--model', $Model) }
    if ($Extra) { $arguments += $Extra }

    $output = & $Exe @arguments 2>&1 | Out-String

    $rank = 0
    $found = 0
    $source = ''

    foreach ($line in ($output -split "`r?`n")) {
        if ($line -match '^\s{2}(?<title>\S.*?)\s\s+page\s(?<page>[\d,]+)(?<marker>\s+\[[^\]]+\])?\s*$') {
            $rank++
            $page = [int](($Matches.page) -replace ',', '')
            if ($page -eq $case.Page -and $Matches.title -like "*$($case.Document)*") {
                $found = $rank
                $source = ($Matches.marker ?? '').Trim()
                break
            }
        }
    }

    $verdict = if ($found -eq 0) { 'not found' }
               elseif ($found -le 10) { 'top ten' }
               elseif ($found -le 25) { 'top 25' }
               else { 'ranked low' }

    Write-Host ("  {0,-8} {1,-52} {2}" -f
        $(if ($found) { "rank $found" } else { 'MISS' }), $case.Query, $verdict)

    [pscustomobject]@{
        Query = $case.Query; Page = $case.Page; Rank = $found; Source = $source; Verdict = $verdict
    }
}

$total = $results.Count
$anywhere = ($results | Where-Object Rank -gt 0).Count
$topTen = ($results | Where-Object { $_.Rank -gt 0 -and $_.Rank -le 10 }).Count
$topTwentyFive = ($results | Where-Object { $_.Rank -gt 0 -and $_.Rank -le 25 }).Count

Write-Host ""
Write-Host "Found at all      : $anywhere of $total   (--limit $Limit)"
Write-Host "In the first 25   : $topTwentyFive of $total   <- the figure the docs quote"
Write-Host "In the first ten  : $topTen of $total   <- what a user actually sees"

if (-not $Out) { return }

$report = New-Object System.Collections.Generic.List[string]
$report.Add("# Ground truth, 54845A Programmer: $Label")
$report.Add('')
$report.Add("Produced by ``tools/measure-ground-truth.ps1``. Do not hand-edit.")
$report.Add('')
$report.Add("| | |")
$report.Add("|---|---|")
$report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
$report.Add("| Index | ``$indexPath`` |")
$report.Add("| Index written | $($indexFile.LastWriteTime.ToString('yyyy-MM-dd HH:mm')), $([math]::Round($indexFile.Length / 1MB, 1)) MB |")
$report.Add("| Repo commit | $commit |")
$asked = if ($Model) { "``--limit $Limit`` with ``--model $Model``" } else { "``--limit $Limit``" }
$report.Add("| Asked at | $asked |")
$report.Add('')
$report.Add("| | of $total |")
$report.Add('|---|---|')
$report.Add("| Correct page returned at all | **$anywhere** |")
$report.Add("| In the first 25 | **$topTwentyFive** |")
$report.Add("| In the first ten | **$topTen** |")
$report.Add('')
$report.Add('| Query | Physical page | Rank | Source |')
$report.Add('|---|---|---|---|')
foreach ($r in $results) {
    $rank = if ($r.Rank -eq 0) { "not in $Limit" } else { $r.Rank }
    $report.Add("| ``$($r.Query)`` | $($r.Page) | $rank | $($r.Source) |")
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$report -join "`n" | Set-Content -Path $Out -Encoding utf8

Write-Host ""
Write-Host "Written to $Out"
