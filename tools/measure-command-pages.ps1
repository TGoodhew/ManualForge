<#
.SYNOPSIS
    Asks for a SCPI command printed on a page of some other manual, and reports where that page
    ranked. The control for any change that treats command syntax differently.

.DESCRIPTION
    The 33 ground-truth strings are commands from one manual, the 54845A, and were chosen because
    they failed, so a change aimed at command syntax will flatter itself on them. The other two
    controls cannot see such a change: `measure-ordinary-pages.ps1` asks eight-word phrases of
    prose and `measure-bare-terms.ps1` one long word, and neither ever holds a mnemonic written
    the SCPI way (`SOURce`). Both reported nothing changed for the notation bias (#3) because
    nothing they asked could change.

    This asks what such a change touches: a colon-delimited command of two levels or more, written
    in mnemonic case, taken from a page drawn at random from every manual but the 54845A, and asked
    exactly as printed. Two ranks are kept: that page's own, and the first page of the same manual,
    since a command is often printed on its summary table and its index as well as where it is
    defined, and any of those is a fair answer.

    As with the bare-term control, the absolute score is not the point. The same seed draws the same
    pages and commands, so two runs with and without a change measure that change and nothing else.

.EXAMPLE
    ./tools/measure-command-pages.ps1 -Sidecars .\dump -Sample 60
    ./tools/measure-command-pages.ps1 -Sidecars .\dump -Sample 60 -Extra @('--rank-notation','1.4')
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

    # The same seed draws the same pages and the same commands, which is what makes two runs comparable.
    [int] $Seed = 1,

    [int] $Limit = 200,

    # The manual the ground truth is drawn from, left out so the two do not overlap.
    [string] $Exclude = '54845A Programmer',

    [string[]] $Extra = @(),

    [string] $Out,

    [string] $Label = 'unlabelled'
)

$ErrorActionPreference = 'Stop'

foreach ($p in @($Sidecars, $Index, $Exe)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}

# Two levels or more, each a run of capitals optionally followed by lower case, and at least one
# level in mnemonic case - so `:TRIGger:EDGE:SLOPe` counts, and a time such as `10:30` does not.
$command = [regex]'(?<![A-Za-z0-9:]):?(?:[A-Z]{2,}[a-z]*)(?::[A-Z]{2,}[a-z]*)+'
$mnemonic = [regex]'[A-Z]{2,}[a-z]+'

$rng = [System.Random]::new($Seed)
$files = Get-ChildItem $Sidecars -Recurse -Filter *.txt | Sort-Object FullName | Sort-Object { $rng.Next() }

$results = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    if ($results.Count -ge $Sample) { break }

    $document = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($Exclude -and $document -eq $Exclude) { continue }

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

    $candidates = $pages.Keys |
        Where-Object { $command.Matches($pages[$_]) | Where-Object { $mnemonic.IsMatch($_.Value) } } |
        Sort-Object
    if (-not $candidates) { continue }
    $page = $candidates | Get-Random -Count 1 -SetSeed ($Seed + $results.Count)

    # The first such command on the page: deterministic given the page.
    $query = ($command.Matches($pages[$page]) | Where-Object { $mnemonic.IsMatch($_.Value) } |
        Select-Object -First 1).Value

    $arguments = @('search', $query, '--index', $Index, '--limit', $Limit, '--show-duplicates') + $Extra
    $output = & $Exe @arguments 2>&1 | Out-String

    $rank = 0
    $documentRank = 0
    $seen = 0
    foreach ($line in ($output -split "`r?`n")) {
        if ($line -match '^\s{2}(?<title>\S.*?)\s\s+page\s(?<page>[\d,]+)') {
            $seen++
            if ($Matches.title -like "*$document*") {
                if (-not $documentRank) { $documentRank = $seen }
                if ([int](($Matches.page) -replace ',', '') -eq $page) { $rank = $seen; break }
            }
        }
    }

    $results.Add([pscustomobject]@{ Document = $document; Page = $page; Command = $query; Rank = $rank; DocumentRank = $documentRank })
}

$total = $results.Count
$first = ($results | Where-Object Rank -eq 1).Count
$topTen = ($results | Where-Object { $_.Rank -gt 0 -and $_.Rank -le 10 }).Count
$found = ($results | Where-Object Rank -gt 0).Count
$docFirst = ($results | Where-Object DocumentRank -eq 1).Count
$docTopTen = ($results | Where-Object { $_.DocumentRank -gt 0 -and $_.DocumentRank -le 10 }).Count

Write-Host ""
Write-Host "Sampled          : $total commands from other manuals, seed $Seed"
Write-Host "Page first       : $first      manual first       : $docFirst"
Write-Host "Page in first ten: $topTen      manual in first ten: $docTopTen"
Write-Host "Page found at all: $found"

if ($Out) {
    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("# Command-page control — $Label")
    $report.Add("")
    $report.Add("One SCPI command per page, as printed, from pages drawn at random from every manual but")
    $report.Add("$Exclude. The population a ranking change aimed at command syntax affects, and one neither")
    $report.Add("``measure-ordinary-pages.ps1`` nor ``measure-bare-terms.ps1`` ever asks.")
    $report.Add("")
    $report.Add("| | |")
    $report.Add("|---|---|")
    $report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
    $report.Add("| Index | $Index |")
    $report.Add("| Sample | $total pages, seed $Seed |")
    $report.Add("| Search arguments | ``$($Extra -join ' ')`` |")
    $report.Add("")
    $report.Add("| | that page | its manual |")
    $report.Add("|---|---|---|")
    $report.Add("| First | **$first** | $docFirst |")
    $report.Add("| In the first ten | **$topTen** | $docTopTen |")
    $report.Add("| Found at all | $found | |")
    $report.Add("")
    $report.Add("| Document | Page | Command | Page rank | Manual rank |")
    $report.Add("|---|---|---|---|---|")
    foreach ($r in $results) {
        $report.Add("| $($r.Document) | $($r.Page) | ``$($r.Command)`` | $(if ($r.Rank) { $r.Rank } else { 'not found' }) | $(if ($r.DocumentRank) { $r.DocumentRank } else { 'not found' }) |")
    }

    $report | Set-Content $Out -Encoding utf8
    Write-Host "Written to $Out"
}
