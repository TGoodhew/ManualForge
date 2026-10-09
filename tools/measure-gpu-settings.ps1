<#
.SYNOPSIS
    Times `manualforge run` at several GPU settings on one test PDF, recording pages a minute, peak
    VRAM, GPU busy and CPU load for each. Written for #31, when the card changed.

.DESCRIPTION
    Each setting gets its own fresh copy of the PDF and its own state database, so nothing is served
    from a page cache and nothing in the library is touched. Uses the GPU for every setting: ask
    before running it on a shared machine.

    Pages in flight are the setting with the cliff: past what VRAM holds, throughput does not fall
    gently but collapses by an order of magnitude. Watch the peak-memory column against the card's
    total, and stop when a setting comes back an order of magnitude slower than its neighbour.

    Batch size changes the words read, not only the speed (docs/measurements/gpu-concurrency-5070ti.md).
    Compare the words column, and diff the outputs, before adopting a different batch.

.EXAMPLE
    ./tools/measure-gpu-settings.ps1 -Out $env:TEMP\gpu-sweep -Concurrency 1,2,3,4 -Batch 8,16
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Pdf = "$PSScriptRoot\..\_compare\typical-test.pdf",
    [string] $Exe = 'manualforge',
    [int[]] $Concurrency = @(1, 2, 3, 4),
    [int[]] $Batch = @(8),
    [int[]] $RasterWorkers = @(2)
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$results = Join-Path $Out 'results.csv'
if (-not (Test-Path $results)) {
    'name,seconds,pages,ppm,peakMemMiB,meanGpuUtil,meanCpu,provider,words' | Set-Content $results
}

$pages = [int]((& $Exe inspect $Pdf | Select-String '^Pages\s+:\s+(\d+)').Matches[0].Groups[1].Value)

foreach ($c in $Concurrency) { foreach ($b in $Batch) { foreach ($r in $RasterWorkers) {
    $name = "conc$c-batch$b-raster$r"
    $dir = Join-Path $Out $name
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force "$dir\lib" | Out-Null
    Copy-Item $Pdf "$dir\lib\"

    $smi = Start-Process nvidia-smi -NoNewWindow -PassThru -RedirectStandardOutput "$dir\smi.csv" `
        -ArgumentList '--query-gpu=memory.used,utilization.gpu', '--format=csv,noheader,nounits', '-l', '1'
    $cpuJob = Start-Job { while ($true) { (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples[0].CookedValue } }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Exe run "$dir\lib" --state "$dir\state.db" --gpu-concurrency $c --batch $b --raster-workers $r 2>&1 |
        Out-String -Width 300
    $watch.Stop()
    Stop-Process -Id $smi.Id
    $cpu = Receive-Job $cpuJob; Stop-Job $cpuJob; Remove-Job $cpuJob -Force
    $output | Set-Content "$dir\output.txt"

    $samples = Get-Content "$dir\smi.csv" | Where-Object { $_ -match '^\d' } |
        ForEach-Object { $p = $_ -split ',\s*'; [pscustomobject]@{ Mem = [int]$p[0]; Util = [int]$p[1] } }
    $peak = ($samples.Mem | Measure-Object -Maximum).Maximum
    $util = [math]::Round(($samples.Util | Measure-Object -Average).Average)
    $cpuMean = [math]::Round(($cpu | Measure-Object -Average).Average)
    $provider = if ($output -match 'ready on (\w+)') { $Matches[1] } else { '?' }
    $words = if ($output -match '(?m)^Words\s+:\s+([\d,]+)') { $Matches[1] -replace ',', '' } else { '' }
    $seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    $ppm = [math]::Round($pages / $watch.Elapsed.TotalMinutes, 1)

    "$name,$seconds,$pages,$ppm,$peak,$util,$cpuMean,$provider,$words" | Add-Content $results
    Write-Host "$(Get-Date -Format HH:mm:ss) $name : $ppm pages/min, peak $peak MiB, GPU $util%, CPU $cpuMean%, $words words"
} } }
