<#
.SYNOPSIS
    Times `manualforge run` at several GPU settings on test PDFs, recording pages a minute, peak
    VRAM, GPU busy, CPU load and memory spilled to system RAM for each. Written for #31, when the
    card changed; the spill trace was added for #56.

.DESCRIPTION
    Each setting gets its own fresh copy of the PDFs and its own state database, so nothing is served
    from a page cache and nothing in the library is touched. Uses the GPU for every setting: ask
    before running it on a shared machine.

    Pages in flight are the setting with the cliff: past what VRAM holds, throughput does not fall
    gently but collapses by an order of magnitude. Watch the peak-memory column against the card's
    total, and stop when a setting comes back an order of magnitude slower than its neighbour.

    The spill columns are what the run's own tuning watches (#31): this process's
    `\GPU Process Memory(pid_*)\Shared Usage`, sampled about once a second. A healthy run holds a
    steady few dozen MiB there, so the rise from its least to its most is the figure to read; each
    run's samples are kept in spill.csv. ManualForge's tuner counts a rise of more than 128 MiB as
    spilling.

    A run at a fixed setting still averages its pages a minute into the card's profile in
    %LOCALAPPDATA%\ManualForge\gpu-profiles.json. Copy that file aside first if its figures matter.

    Batch size changes the words read, not only the speed (docs/measurements/gpu-concurrency-5070ti.md).
    Compare the words column, and diff the outputs, before adopting a different batch.

.EXAMPLE
    ./tools/measure-gpu-settings.ps1 -Out $env:TEMP\gpu-sweep -Concurrency 1,2,3,4 -Batch 8,16
    ./tools/measure-gpu-settings.ps1 -Out $env:TEMP\gpu-sweep -Concurrency 1,2,3 -Repeat 2
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    # The seven test books in _compare, 345 pages, unless told otherwise.
    [string[]] $Pdf,
    [string] $Exe = 'manualforge',
    [int[]] $Concurrency = @(1, 2, 3, 4),
    [int[]] $Batch = @(8),
    [int[]] $RasterWorkers = @(2),
    # Runs of each setting, one after another, each with a fresh copy. The same setting can spill in
    # one run and not the next (docs/measurements/gpu-autotuning.md), so one run says little.
    [int] $Repeat = 1
)

$ErrorActionPreference = 'Stop'
if (-not $Pdf) {
    # Here rather than as the parameter's default, where Windows PowerShell leaves $PSScriptRoot empty.
    $Pdf = @(Get-ChildItem (Join-Path $PSScriptRoot '..\_compare') -File |
        Where-Object { $_.Name -like '*-test.pdf' -or $_.Name -eq '431C-OSM.pdf' } |
        ForEach-Object FullName)
}
New-Item -ItemType Directory -Force $Out | Out-Null
$results = Join-Path $Out 'results.csv'
if (-not (Test-Path $results)) {
    'name,seconds,pages,ppm,peakMemMiB,meanGpuUtil,meanCpu,provider,words,spillLeastMiB,spillMostMiB,spillRiseMiB' |
        Set-Content $results
}

$pages = 0
foreach ($p in $Pdf) {
    $pages += [int]((& $Exe inspect $p | Select-String '^Pages\s+:\s+(\d+)').Matches[0].Groups[1].Value)
}
$exeName = [IO.Path]::GetFileNameWithoutExtension($Exe)

foreach ($c in $Concurrency) { foreach ($b in $Batch) { foreach ($r in $RasterWorkers) { foreach ($n in 1..$Repeat) {
    $name = "conc$c-batch$b-raster$r" + $(if ($Repeat -gt 1) { "-run$n" } else { '' })
    $dir = Join-Path $Out $name
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force "$dir\lib" | Out-Null
    Copy-Item $Pdf "$dir\lib\"

    $smi = Start-Process nvidia-smi -NoNewWindow -PassThru -RedirectStandardOutput "$dir\smi.csv" `
        -ArgumentList '--query-gpu=memory.used,utilization.gpu', '--format=csv,noheader,nounits', '-l', '1'
    $cpuJob = Start-Job { while ($true) { (Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples[0].CookedValue } }
    $spillJob = Start-Job -ArgumentList $exeName {
        param($process)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $run = Get-Process $process -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($run) {
                $samples = (Get-Counter "\GPU Process Memory(pid_$($run.Id)_*)\Shared Usage" -ErrorAction SilentlyContinue).CounterSamples
                if ($samples) {
                    '{0:F1},{1}' -f $clock.Elapsed.TotalSeconds, [int](($samples | Measure-Object CookedValue -Sum).Sum / 1MB)
                }
            }
            else {
                Start-Sleep -Milliseconds 200
            }
        }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Exe run "$dir\lib" --state "$dir\state.db" --gpu-concurrency $c --batch $b --raster-workers $r 2>&1 |
        Out-String -Width 300
    $watch.Stop()
    Stop-Process -Id $smi.Id
    $cpu = Receive-Job $cpuJob; Stop-Job $cpuJob; Remove-Job $cpuJob -Force
    $spill = Receive-Job $spillJob; Stop-Job $spillJob; Remove-Job $spillJob -Force
    $output | Set-Content "$dir\output.txt"
    (@('seconds,sharedMiB') + $spill) | Set-Content "$dir\spill.csv"

    $samples = Get-Content "$dir\smi.csv" | Where-Object { $_ -match '^\d' } |
        ForEach-Object { $p = $_ -split ',\s*'; [pscustomobject]@{ Mem = [int]$p[0]; Util = [int]$p[1] } }
    $peak = ($samples.Mem | Measure-Object -Maximum).Maximum
    $util = [math]::Round(($samples.Util | Measure-Object -Average).Average)
    $cpuMean = [math]::Round(($cpu | Measure-Object -Average).Average)
    $shared = $spill | ForEach-Object { [int]($_ -split ',')[1] }
    $least = ($shared | Measure-Object -Minimum).Minimum
    $most = ($shared | Measure-Object -Maximum).Maximum
    $rise = if ($null -ne $least) { $most - $least } else { '' }
    $provider = if ($output -match 'ready on (\w+)') { $Matches[1] } else { '?' }
    $words = if ($output -match '(?m)^Words\s+:\s+([\d,]+)') { $Matches[1] -replace ',', '' } else { '' }
    $seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    $ppm = [math]::Round($pages / $watch.Elapsed.TotalMinutes, 1)

    "$name,$seconds,$pages,$ppm,$peak,$util,$cpuMean,$provider,$words,$least,$most,$rise" | Add-Content $results
    Write-Host ("$(Get-Date -Format HH:mm:ss) $name : $ppm pages/min, peak $peak MiB, GPU $util%, CPU $cpuMean%, " +
        "$words words, spilled $least -> $most MiB")
} } } }
