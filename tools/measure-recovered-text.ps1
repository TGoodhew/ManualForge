<#
.SYNOPSIS
    Asks whether text the repair recovered actually reaches search, by sampling repaired pages and
    looking for a phrase from each in the index.

.DESCRIPTION
    The 33-query ground truth in tools/ground-truth-54845A.tsv measures one document, because one
    document is all anybody has read by eye. It cannot see a repair of the other five hundred.

    This measures those instead, and it needs no human to read anything. Two text dumps of the
    library are produced by `index --sidecars`, one with recovered text merged and one with
    `--no-repairs`; the difference, page by page, is exactly what the repair added. A phrase is
    taken from that difference and asked of both indexes. A phrase that returns the right page
    afterwards and did not before is recovered text that reached search.

    Nothing here proves the recovered text is *correct* - these are OCR readings of pictures and
    they carry recognition errors. It proves the text is present, matchable and attached to the
    right page, which is the claim the repair actually makes.

.EXAMPLE
    ./tools/measure-recovered-text.ps1 -After .\sidecars-after -Before .\sidecars-before `
        -BeforeIndex .\index-before.db -Sample 25 -Out docs/measurements/recovered-text.md
#>
[CmdletBinding()]
param(
    # Sidecars written by `index --sidecars` WITH recovered text merged.
    [Parameter(Mandatory)] [string] $After,

    # Sidecars written by `index --sidecars --no-repairs`: the same library without the repair.
    [Parameter(Mandatory)] [string] $Before,

    # The index as it stood before the repair was merged.
    [Parameter(Mandatory)] [string] $BeforeIndex,

    [string] $AfterIndex = $(if ($env:MANUALFORGE_LIBRARY)
                            { "$env:MANUALFORGE_LIBRARY\_Originals\manualforge-index.db" }
                            else { "$env:USERPROFILE\OneDrive\Documents\Manuals\_Originals\manualforge-index.db" }),

    [string] $Exe = "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe",

    # How many repaired pages to sample. One page per document, so this is also a document count.
    [int] $Sample = 25,

    # Fixed, so the same pages come back for anybody who wants to disagree.
    [int] $Seed = 1,

    [int] $Limit = 25,

    [string] $Out
)

$ErrorActionPreference = 'Stop'
foreach ($p in @($After, $Before, $BeforeIndex, $AfterIndex, $Exe)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}

# Sidecar pages are "--- page N ---" followed by that page's text.
function Get-Pages([string] $path) {
    $pages = @{}
    if (-not (Test-Path $path)) { return $pages }
    $current = 0
    $buffer = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadLines($path)) {
        if ($line -match '^--- page (\d+) ---$') {
            if ($current) { $pages[$current] = ($buffer -join "`n") }
            $current = [int]$Matches[1]
            $buffer.Clear()
        }
        elseif ($current) { $buffer.Add($line) }
    }
    if ($current) { $pages[$current] = ($buffer -join "`n") }
    return $pages
}

# A phrase worth asking for: a run of ordinary words long enough to be specific. Lines of pure
# punctuation, single tokens and figure scatter make poor queries and prove nothing either way.
function Get-Phrase([string] $recovered) {
    foreach ($line in ($recovered -split "`n")) {
        $words = ($line -replace '[^\w\s\.\-/]', ' ') -split '\s+' |
            Where-Object { $_.Length -ge 3 } |
            # FTS5 reads a bare uppercase AND, OR or NOT as an operator, and these manuals are
            # written in capitals, so a quoted phrase from one lands on that constantly. Dropping
            # the operator words measures the repair rather than issue #9.
            Where-Object { $_ -cnotin @('AND', 'OR', 'NOT') }

        if ($words.Count -ge 6) { return ($words[0..5] -join ' ') }
    }
    return $null
}

$rng = [System.Random]::new($Seed)
$candidates = Get-ChildItem -Path $After -Recurse -Filter *.txt |
    Sort-Object FullName |
    Sort-Object { $rng.Next() }

$results = New-Object System.Collections.Generic.List[object]

foreach ($file in $candidates) {
    if ($results.Count -ge $Sample) { break }

    $relative = $file.FullName.Substring((Resolve-Path $After).Path.Length).TrimStart('\')
    $beforeFile = Join-Path $Before $relative
    if (-not (Test-Path $beforeFile)) { continue }

    $afterPages = Get-Pages $file.FullName
    $beforePages = Get-Pages $beforeFile

    # The first page of this document whose text grew, with enough new text to quote.
    $page = $afterPages.Keys | Sort-Object | Where-Object {
        $a = $afterPages[$_]; $b = $beforePages[$_]
        $a -and $a.Length -gt ($b.Length + 200) -and $a.StartsWith($b)
    } | Select-Object -First 1

    if (-not $page) { continue }

    $recovered = $afterPages[$page].Substring($beforePages[$page].Length)
    $phrase = Get-Phrase $recovered
    if (-not $phrase) { continue }

    $document = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

    $rank = {
        param($index)
        $output = & $Exe search $phrase --index $index --limit $Limit 2>&1 | Out-String
        $r = 0
        foreach ($line in ($output -split "`r?`n")) {
            if ($line -match '^\s{2}(?<title>\S.*?)\s\s+page\s(?<page>[\d,]+)') {
                $r++
                if ([int](($Matches.page) -replace ',', '') -eq $page -and
                    $Matches.title -like "*$document*") { return $r }
            }
        }
        return 0
    }

    $was = & $rank $BeforeIndex
    $now = & $rank $AfterIndex

    Write-Host ("  {0,-42} p{1,-5} before {2,-9} after {3}" -f
        $document.Substring(0, [Math]::Min(42, $document.Length)), $page,
        $(if ($was) { "rank $was" } else { 'not found' }),
        $(if ($now) { "rank $now" } else { 'NOT FOUND' }))

    $results.Add([pscustomobject]@{
        Document = $document; Page = $page; Phrase = $phrase; Before = $was; After = $now
    })
}

$total = $results.Count
$newlyFound = ($results | Where-Object { $_.Before -eq 0 -and $_.After -gt 0 }).Count
$foundBefore = ($results | Where-Object Before -gt 0).Count
$stillMissing = ($results | Where-Object After -eq 0).Count

Write-Host ""
Write-Host "Sampled            : $total repaired pages, one per document"
Write-Host "Newly findable     : $newlyFound"
Write-Host "Findable before too: $foundBefore"
Write-Host "Still not findable : $stillMissing"

if (-not $Out) { return }

$report = New-Object System.Collections.Generic.List[string]
$report.Add('# Does recovered text reach search?')
$report.Add('')
$report.Add('Produced by `tools/measure-recovered-text.ps1`. Do not hand-edit.')
$report.Add('')
$report.Add("| | |")
$report.Add('|---|---|')
$report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
$report.Add("| Sample | $total repaired pages, one per document, seed $Seed |")
$report.Add("| Asked at | ``--limit $Limit`` |")
$report.Add('')
$report.Add("| | of $total |")
$report.Add('|---|---|')
$report.Add("| Phrase newly findable after the repair | **$newlyFound** |")
$report.Add("| Already findable before it | $foundBefore |")
$report.Add("| Still not findable | $stillMissing |")
$report.Add('')
$report.Add('| Document | Page | Phrase from the recovered text | Before | After |')
$report.Add('|---|---|---|---|---|')
foreach ($r in $results) {
    $b = if ($r.Before -eq 0) { 'not found' } else { $r.Before }
    $a = if ($r.After -eq 0) { 'not found' } else { $r.After }
    $report.Add("| $($r.Document) | $($r.Page) | ``$($r.Phrase)`` | $b | $a |")
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$report -join "`n" | Set-Content -Path $Out -Encoding utf8
Write-Host ""
Write-Host "Written to $Out"
