<#
.SYNOPSIS
    Recognises one book of pages under several settings and reports, for each, how much
    content-bearing text came back. The measurement behind issue #19.

.DESCRIPTION
    #19 rests on one page: a dense code table ManualForge reads as noise and Acrobat reads
    correctly. Four fixes were tried against that page and all four failed, which is not enough to
    conclude anything - one page cannot distinguish "the model cannot read this material" from
    "this page is unusual".

    This runs a whole book of such pages through every setting worth trying and counts tokens that
    look like real content: a component designator, a value, or a word of three or more letters.
    Raw token counts are not used, because across three Acrobat comparisons they reversed the
    apparent result twice - an engine reading the lines of a drawing as text produces enormous
    counts of nothing.

    A flat yield across every configuration says the recogniser is the ceiling on this material and
    #19 is not a misconfiguration. A configuration that stands out says otherwise.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Book,
    [Parameter(Mandatory)] [string] $Out,
    [string] $Exe = "$PSScriptRoot\..\src\ManualForge.Cli\bin\Release\net10.0-windows\win-x64\manualforge.exe",
    [string] $Work = "$env:TEMP\manualforge-sweep"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe))  { throw "No manualforge.exe at $Exe" }
if (-not (Test-Path $Book)) { throw "No book at $Book" }
New-Item -ItemType Directory -Path $Work -Force | Out-Null

# Each row is a configuration worth asking about, and why it is worth asking.
$configs = @(
    @{ Name = '300 dpi, defaults';        Args = @('--dpi','300') }
    @{ Name = '300 dpi, no deskew';       Args = @('--dpi','300','--no-deskew') }
    @{ Name = '300 dpi, no denoise';      Args = @('--dpi','300','--no-denoise') }
    @{ Name = '300 dpi, neither';         Args = @('--dpi','300','--no-deskew','--no-denoise') }
    @{ Name = '400 dpi, defaults';        Args = @('--dpi','400') }
    @{ Name = '600 dpi, defaults';        Args = @('--dpi','600') }
    @{ Name = '600 dpi, neither';         Args = @('--dpi','600','--no-deskew','--no-denoise') }
    @{ Name = '200 dpi, defaults';        Args = @('--dpi','200') }
    @{ Name = '300 dpi, keep weak words'; Args = @('--dpi','300','--min-confidence','0.05') }
)

$designator = '^[A-Za-z]{1,3}\d{1,4}[A-Za-z]?$'
$value      = '^\d+(\.\d+)?[kKmMuUpPnN]?([FfHh]|hm|ohm)?$'
$word       = '^[A-Za-z]{3,}$'

$rows = foreach ($c in $configs) {
    $tag  = ($c.Name -replace '[^A-Za-z0-9]','_')
    $text = Join-Path $Work "$tag.txt"
    $pdf  = Join-Path $Work "$tag.pdf"

    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $c.Name)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $Exe ocr $Book --out $pdf --text $text --overwrite @($c.Args) *>&1 | Out-Null
    $sw.Stop()

    if (-not (Test-Path $text)) {
        Write-Host "    produced no output"
        continue
    }

    $tokens = Get-Content $text | Where-Object { $_ -notmatch '^--- page' }
    $signal = @($tokens | Where-Object { $_ -match $designator -or $_ -match $value -or $_ -match $word })
    $junk   = @($tokens | Where-Object { $_.Length -le 2 -and $_ -notmatch '^\d+$' })

    $row = [pscustomobject]@{
        Configuration = $c.Name
        RawTokens     = $tokens.Count
        Signal        = $signal.Count
        SignalShare   = if ($tokens.Count) { [math]::Round(100 * $signal.Count / $tokens.Count, 1) } else { 0 }
        JunkShare     = if ($tokens.Count) { [math]::Round(100 * $junk.Count   / $tokens.Count, 1) } else { 0 }
        Minutes       = [math]::Round($sw.Elapsed.TotalMinutes, 1)
    }
    Write-Host ("    raw {0,6}  signal {1,6}  ({2}%)  junk {3}%  in {4} min" -f
        $row.RawTokens, $row.Signal, $row.SignalShare, $row.JunkShare, $row.Minutes)
    $row
}

$rows | Sort-Object -Descending Signal | Format-Table -AutoSize | Out-String -Width 140 | Write-Host

$report = New-Object System.Collections.Generic.List[string]
$report.Add("# Recognition sweep on dense table pages")
$report.Add("")
$report.Add("The measurement behind issue #19, which until now rested on a single page.")
$report.Add("")
$report.Add("| | |")
$report.Add("|---|---|")
$report.Add("| Measured | $(Get-Date -Format 'yyyy-MM-dd HH:mm') |")
$report.Add("| Book | $Book |")
$report.Add("")
$report.Add("Content-bearing means a component designator, a value, or a word of three or more")
$report.Add("letters. Raw counts are reported but not ranked on: across three Acrobat comparisons")
$report.Add("they reversed the apparent result twice, because an engine reading the lines of a")
$report.Add("drawing as text produces enormous counts of nothing.")
$report.Add("")
$report.Add("| Configuration | Raw tokens | Content-bearing | Signal share | Junk share | Minutes |")
$report.Add("|---|---|---|---|---|---|")
foreach ($r in ($rows | Sort-Object -Descending Signal)) {
    $report.Add("| $($r.Configuration) | $($r.RawTokens) | **$($r.Signal)** | $($r.SignalShare)% | $($r.JunkShare)% | $($r.Minutes) |")
}
$report | Set-Content $Out -Encoding utf8
Write-Host "Written to $Out"
