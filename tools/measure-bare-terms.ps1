<#
.SYNOPSIS
    Asks for a single bare word taken from an ordinary page, and reports where that page ranked.
    The control for any change that treats one-word queries differently from phrases.

.DESCRIPTION
    `measure-ordinary-pages.ps1` quotes eight consecutive words, so every query it asks is a phrase.
    That makes it blind to a change scoped to single bare terms: the recovered-text bias was measured
    at 40 of 40 identical with the bias on and off, not because it was harmless but because the
    harness never asked it a question it could answer.

    This asks the shape that change actually affects. One distinctive word per page, taken from the
    page's own text, asked bare.

    A bare word is genuinely ambiguous — hundreds of manuals contain "attenuator" — so a low score
    here is normal and is not the point. The point is the *paired* comparison: the same seed draws
    the same pages and the same words, so running it twice with and without a ranking change
    measures that change and nothing else.

.EXAMPLE
    ./tools/measure-bare-terms.ps1 -Sidecars .\dump -Sample 60
    ./tools/measure-bare-terms.ps1 -Sidecars .\dump -Sample 60 -Extra @('--rank-recovered','1.4')
#>
[CmdletBinding()]
param(
    # A text dump of the library, from `index --sidecars <folder>`.
    [Parameter(Mandatory)] [string] $Sidecars,

    [string] $Index = $(if ($env:MANUALFORGE_LIBRARY)
                        { "$env:MANUALFORGE_LIBRARY\_Originals\manualforge-index.db" }
                        else { "$env:USERPROFILE\OneDrive\Documents\Manuals\_Originals\manualforge-index.db" }),

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    [int] $Sample = 60,

    # The same seed draws the same pages and the same words, which is what makes two runs comparable.
    [int] $Seed = 1,

    [int] $Limit = 200,

    [string[]] $Extra = @(),

    [string] $Out,

    [string] $Label = 'unlabelled'
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

    $candidates = $pages.Keys | Where-Object { $pages[$_].Length -gt 600 } | Sort-Object
    if (-not $candidates) { continue }
    $page = $candidates | Get-Random -Count 1 -SetSeed ($Seed + $results.Count)

    # The most distinctive word on the page: long, alphabetic, and rare within the page itself.
    # Deterministic given the page, so the pair of runs asks identical questions.
    $words = ($pages[$page] -replace '[^A-Za-z]', ' ') -split '\s+' |
        Where-Object { $_.Length -ge 7 }
    if (-not $words -or $words.Count -lt 3) { continue }

    $word = $words |
        Group-Object |
        Sort-Object @{ e = { $_.Count } }, @{ e = { -$_.Name.Length } }, @{ e = { $_.Name } } |
        Select-Object -First 1 -ExpandProperty Name

    if (-not $word) { continue }

    $document = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

    $arguments = @('search', $word, '--index', $Index, '--limit', $Limit, '--show-duplicates') + $Extra
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

    $results.Add([pscustomobject]@{ Document = $document; Page = $page; Term = $word; Rank = $rank })
}

$total = $results.Count
$first = ($results | Where-Object Rank -eq 1).Count
$topTen = ($results | Where-Object { $_.Rank -gt 0 -and $_.Rank -le 10 }).Count
$found = ($results | Where-Object Rank -gt 0).Count

Write-Host ""
Write-Host "Sampled          : $total pages, one bare word each, seed $Seed"
Write-Host "Correct page 1st : $first"
Write-Host "In the first ten : $topTen"
Write-Host "Found at all     : $found   (a bare word is ambiguous; compare runs, not absolutes)"

if ($Out) {
    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("# Bare-term control — $Label")
    $report.Add("")
    $report.Add("One distinctive word per page, asked bare. The population a single-token ranking")
    $report.Add("change affects, and the one `measure-ordinary-pages.ps1` cannot see because every")
    $report.Add("query it asks is a phrase.")
    $report.Add("")
    $report.Add("| | |")
    $report.Add("|---|---|")
    $report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
    $report.Add("| Index | $Index |")
    $report.Add("| Sample | $total pages, seed $Seed |")
    $report.Add("| Search arguments | ``$($Extra -join ' ')`` |")
    $report.Add("")
    $report.Add("| | of $total |")
    $report.Add("|---|---|")
    $report.Add("| Correct page first | **$first** |")
    $report.Add("| In the first ten | **$topTen** |")
    $report.Add("| Found at all | $found |")
    $report.Add("")
    $report.Add("| Document | Page | Term | Rank |")
    $report.Add("|---|---|---|---|")
    foreach ($r in $results) {
        $report.Add("| $($r.Document) | $($r.Page) | $($r.Term) | $(if ($r.Rank) { $r.Rank } else { 'not found' }) |")
    }

    $report | Set-Content $Out -Encoding utf8
    Write-Host "Written to $Out"
}
