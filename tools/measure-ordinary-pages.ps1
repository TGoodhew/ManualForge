<#
.SYNOPSIS
    Asks for a phrase from an ordinary page of an ordinary manual, and reports where that page
    ranked. The test a ranking change has to pass before it is turned on for everybody.

.DESCRIPTION
    The 33 hand-read strings measure one document and were chosen because they failed. A change
    tuned against them will flatter itself. This samples pages at random from the whole library,
    quotes a phrase out of each page's own text, and asks for it — so the queries are ordinary,
    numerous, and had no hand in designing whatever is being tested.

    It answers "did this make normal search worse", which is the question that decides whether a
    ranking idea ships. A change that gains four places on the ground truth and loses ten pages
    here is not an improvement.

    The phrase is taken from text the index already holds, so the correct page must be findable:
    anything not returned is a ranking result, never a retrieval one.

.EXAMPLE
    ./tools/measure-ordinary-pages.ps1 -Sidecars .\dump -Sample 40
    ./tools/measure-ordinary-pages.ps1 -Sidecars .\dump -Sample 40 -Extra @('--rank-labels','1.4')
#>
[CmdletBinding()]
param(
    # A text dump of the library, from `index --sidecars <folder>`.
    [Parameter(Mandatory)] [string] $Sidecars,

    [string] $Index = $(if ($env:MANUALFORGE_LIBRARY)
                        { "$env:MANUALFORGE_LIBRARY\_Originals\manualforge-index.db" }
                        else { "$env:USERPROFILE\OneDrive\Documents\Manuals\_Originals\manualforge-index.db" }),

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    [int] $Sample = 40,
    [int] $Seed = 1,
    [int] $Limit = 25,

    # Extra search arguments, e.g. -Extra @('--rank-labels','1.4').
    [string[]] $Extra = @(),

    [string] $Out
)

$ErrorActionPreference = 'Stop'
foreach ($p in @($Sidecars, $Index, $Exe)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}

$rng = [System.Random]::new($Seed)
$files = Get-ChildItem $Sidecars -Recurse -Filter *.txt | Sort-Object FullName | Sort-Object { $rng.Next() }

$results = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    if ($results.Count -ge $Sample) { break }

    $pages = @{}
    $current = 0
    $buffer = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        if ($line -match '^--- page (\d+) ---$') {
            if ($current) { $pages[$current] = ($buffer -join "`n") }
            $current = [int]$Matches[1]
            $buffer.Clear()
        }
        elseif ($current) { $buffer.Add($line) }
    }
    if ($current) { $pages[$current] = ($buffer -join "`n") }

    # A page with real prose on it, chosen by the same seeded draw, so the sample is re-drawable.
    $candidates = $pages.Keys | Where-Object { $pages[$_].Length -gt 600 } | Sort-Object
    if (-not $candidates) { continue }
    $page = $candidates | Get-Random -Count 1 -SetSeed ($Seed + $results.Count)

    # Eight consecutive ordinary words from the middle of the page: long enough to identify one
    # page, short enough that a person might plausibly type it.
    $words = ($pages[$page] -replace '[^\w\s\.\-/]', ' ') -split '\s+' |
        Where-Object { $_.Length -ge 3 -and $_ -cnotin @('AND', 'OR', 'NOT') }
    if ($words.Count -lt 30) { continue }

    $start = [int]($words.Count / 3)
    $phrase = ($words[$start..($start + 7)] -join ' ')
    $document = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

    $arguments = @('search', $phrase, '--index', $Index, '--limit', $Limit, '--show-duplicates') + $Extra
    $output = & $Exe @arguments 2>&1 | Out-String

    $rank = 0
    $seen = 0
    foreach ($line in ($output -split "`r?`n")) {
        if ($line -match '^\s{2}(?<title>\S.*?)\s\s+page\s(?<page>[\d,]+)') {
            $seen++
            if ([int](($Matches.page) -replace ',', '') -eq $page -and $Matches.title -like "*$document*") {
                $rank = $seen
                break
            }
        }
    }

    $results.Add([pscustomobject]@{ Document = $document; Page = $page; Phrase = $phrase; Rank = $rank })
}

$total = $results.Count
$first = ($results | Where-Object Rank -eq 1).Count
$topTen = ($results | Where-Object { $_.Rank -gt 0 -and $_.Rank -le 10 }).Count
$found = ($results | Where-Object Rank -gt 0).Count

Write-Host ""
Write-Host "Sampled          : $total ordinary pages"
Write-Host "Correct page 1st : $first"
Write-Host "In the first ten : $topTen"
Write-Host "Found at all     : $found   (anything missing is ranking, not retrieval)"

if (-not $Out) { return }

$report = New-Object System.Collections.Generic.List[string]
$report.Add('# Ordinary pages, ordinary queries')
$report.Add('')
$report.Add('Produced by `tools/measure-ordinary-pages.ps1`. Do not hand-edit.')
$report.Add('')
$report.Add("| | |")
$report.Add('|---|---|')
$report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
$report.Add("| Sample | $total pages, seed $Seed |")
$report.Add("| Search arguments | ``$($Extra -join ' ')`` |")
$report.Add('')
$report.Add("| | of $total |")
$report.Add('|---|---|')
$report.Add("| Correct page first | **$first** |")
$report.Add("| In the first ten | **$topTen** |")
$report.Add("| Found at all | $found |")
$report.Add('')
$report.Add('| Document | Page | Rank |')
$report.Add('|---|---|---|')
foreach ($r in $results) {
    $report.Add("| $($r.Document) | $($r.Page) | $(if ($r.Rank) { $r.Rank } else { "not in $Limit" }) |")
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$report -join "`n" | Set-Content -Path $Out -Encoding utf8
Write-Host "Written to $Out"
