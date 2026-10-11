# Pages in flight tuned on any card

#31 asked for the GPU settings to suit the card they run on. The first answer
([gpu-concurrency-5070ti.md](gpu-concurrency-5070ti.md)) re-measured one card and wrote what it
found into the code: three pages in flight from 16 GB, two below, 3,000 MiB budgeted per extra
page, 151.7 pages a minute in every estimate. That is right for the RTX 5070 Ti and a guess
everywhere else. It was the same mistake the 3060 Ti's figures had made: they went on being used
after that card was replaced.

**Now no card's figures are built in.** The card is tuned one run at a time: each run keeps one
setting and backs off only if the card spills, a clean run lets the next try one more, and the first
setting that spills is remembered as the card's limit. Tuning within a run was tried twice and lost
to a fixed setting both times; the sections after "What is remembered" say why.

## What is tuned, and what is not

| Setting | Tuned? | Why |
|---|---|---|
| Pages in flight | **yes** | The output is identical at every setting (36,001 words at one to four pages), and the best value depends on the card and on what else is using it. |
| Batch size | **no** | It changes the words read: 58 of 100 pages differ between batch 8 and 16. A tuner chasing speed would quietly change the text, so it stays at 8. |
| Rasterisers | no | Two, four and six give the same speed. Rasterising is not the limit. |
| cuDNN algorithm search | no | Exhaustive and heuristic were the same within noise. |

## How a card is tuned

**Across runs.** A card never seen starts at two pages in flight: the most the smallest card measured
(8 GB) held, and about half again the speed of one. After that, each run starts at one more than the
most the card has held cleanly, unless that is the setting known to spill:

- A run that reads at least 100 pages **without spilling** vouches for its setting. The next run
  tries one more.
- A run that **spills** at a setting being tried for the first time marks that setting unsafe for the
  card. The next run goes back to the one below, and the setting isn't tried again until the
  display driver changes.
- A run that spills at a setting the card **has held before** may have been pushed over by something
  else (a game, a browser). The next run starts one lower and climbs back. The setting isn't ruled out.
- A run too short to say, or one where the counter could not be read, learns nothing about the card.

So the card settles on the most pages in flight it holds. On every card measured, that was also the
fastest: each page added was faster until the card spilled (50, 75, 87, 91 pages a minute at one to
four on the 5070 Ti), and spilling was slower than any of them. Eight is the most any run will try.
That isn't a figure for any card: it's above anything one has been seen to want.

**Within a run, only down.** Every 20 seconds and `max(12, 4 × pages in flight)` pages, the run reads
how much of its own GPU memory Windows has moved out to system RAM (the
`GPU Process Memory\Shared Usage` performance counter for the process). If that has risen above what
the process held before its first page by more than the engine's margin, the card is over the edge,
and the run steps down one at once. The margin is 128 MiB on CUDA and 512 on TensorRT, which moves
some memory out while it warms up and brings it back (see "TensorRT on the card" below). After a step down, the mark moves up to what is out now, because the memory
pool doesn't hand back what was moved. What counts after that is whether more goes. A step down
takes effect as pages finish; nothing in flight is interrupted.

## What is remembered

`%LOCALAPPDATA%\ManualForge\gpu-profiles.json`, one entry per card and engine, keyed by name and
total memory, plus the engine when it's TensorRT. CUDA keeps the key every profile had before engines
were told apart. TensorRT reads about twice as fast and holds memory differently, so what one learns
would mislead the other (#56). The driver version is recorded but not part of the key: a driver
update moves speed by a few per cent, which isn't worth re-tuning from nothing. It does drop the unsafe
setting, because a driver changes how memory goes out to system RAM, so the next run finds the edge
again. The best setting and the speeds are kept.

| Field | Learned from | Used for |
|---|---|---|
| `BestConcurrency` | the most pages in flight a run of 100+ pages held without spilling; one less after a spill | the next run starts one above it (a new card starts at 2) |
| `UnsafeConcurrency` | a spill at a setting being tried for the first time | not tried again while the driver stays the same; only ever lowered |
| `RunPagesPerMinute` | a run that recognised 100 pages or more itself, end to end; pages reused from an earlier attempt don't count | `survey`, `status` and `run` estimates |
| `RepairPagesPerMinute` | per page kind and resolution, 30 pages or more, first page excluded | `repair`'s estimate |
| `RunPagesMeasured`, `RepairPagesMeasured` | the pages behind each figure, up to 5,000 | weighing the next figure against it |

A new figure is weighed against the one before by the pages behind each, and the older figure counts
for 5,000 pages at most, so a figure measured on a whole library can still move when the card's speed
changes (#63). Both used to be averaged half and half. On 11 October, a re-read whose 992 pages all
came from the cache reported 9,870 pages a minute, and two such averages took the card's run speed
from 164 to 2,580; two one-file repairs moved a repair rate measured over 14,679 pages from 64 to 76.
A figure written before page counts were kept counts as 5,000.

Until a card has a figure, the estimates use the 5070 Ti's measurements and say so ("as measured on
an RTX 5070 Ti; this <card> has not been timed yet"). With no NVIDIA card they use the CPU figure.
`--gpu-concurrency <n>` fixes pages in flight and turns tuning off. A fixed run still records its
speed and never clears a known cliff.

Each run's log records the card, its driver, the setting and why it was chosen, any step down with
the reason, and what the next run will try. `manualforge gpu` shows what has been learned for the
card and engine in use. `manualforge gpu --retune` forgets the pages in flight, so the next run starts
again from two, and keeps the measured speeds; deleting the file forgets the speeds as well.

## Free memory is the wrong signal

The first version kept a reserve of free memory (5% of the card, read from `nvidia-smi`): it climbed
only while there was room for another page, and stepped down when free memory fell into the reserve.
On the card, 10 Oct 2026, 200 pages (`typical-test` and `tableonly-test`), Release build, driver
617.42:

| Run | Pages in flight | Pages/min | Words |
|---|---|---|---|
| tuned, first run on the card | 1 → 2 → 1 ("731 MiB free, under the 815 MiB reserve") | 61.7 | 86,105 |
| tuned, second run | starts at 2 → 1 (656 MiB free) | 54.6 | 86,105 |
| fixed at 3 | 3 | **85.5** | 86,105 |

ONNX Runtime's memory pool grows into whatever the card has free, so a healthy run leaves the card
nearly full. Three pages ran at full speed with under a gigabyte free. Free memory says how much the
pool has taken, not whether the card is over the edge. The simulated cards in the tests had memory
rising with load, which is why the tests passed.

## Spilled memory is the right one

The same card at fixed settings on `typical-test` (100 pages), sampling every 0.7 s the process's
own GPU memory as Windows counts it, dedicated and shared:

| Pages in flight | Pages/min | Dedicated | Shared (in system RAM) | Words |
|---|---|---|---|---|
| 3 | 85.8 | 10.9 GB | 76 MiB, steady | 36,001 |
| 4 | **46.2** | 15.3 GB | 76 → **322 MiB** about 30 s in | 36,001 |
| 6 | **43.4** | 14.9 GB | 76 → **4.3 GB** within 8 s | 36,001 |

The desktop held 1.5 GB of the card before the four-page run, against 2.6 GB on 9 Oct when four ran at
91.4. So the edge moves with what else is open, and on this afternoon it was at four. The spill shows
in the counter within a window, and is unmistakable: a healthy run holds a steady few dozen MiB there.
Throughput at four was 54% of three's, not under half, so the collapse check alone would have missed
it, and the gain check would only have caught it by luck. Sampling the counters did not slow the run:
three pages ran at 85.8 with it, against 85.5 without.

Six pages here ran at 43 pages a minute, not the 13.7 measured on 9 Oct. How hard the fall is varies;
that there is one does not.

## Climbing within a run loses too

The second version climbed on throughput within the run (keeping a step if it was 5% faster) and
stepped down on the spill counter. On the card, 10 Oct 2026, 280 pages (all seven test files in
`_compare`), Release build:

| Run | Pages in flight | Spilled | Pages/min | Words |
|---|---|---|---|---|
| tuned, first run on the card | 1 → 2 → 3 → 4, not worth it → 3, spilled → 2 | 2,012 MiB | 68.6 | 122,858 |
| tuned, second run | starts at 2 → 3, spilled → 2, spilled → 1 | 652 MiB | 66.0 | 122,858 |
| fixed at 3 | 3 | none (76 MiB, steady) | **79.6** | 122,858 |

At three pages, the fixed run never spilled and the tuned runs did. The difference is the memory
pool. It never shrinks, so a run that tried four pages and went back to three kept four's pool: the
tuned runs peaked at 15.6–15.8 GB of the card, the fixed one at 12.9. (This note first blamed the pool
growing in regions that double in size as well; a probe later tested that and found otherwise, see
below.) Within a run, trying more costs the rest of the run, so the trying happens between runs,
where every run starts with a fresh pool.

## On the card, across runs

Four runs back to back, 10 Oct 2026, each on a fresh copy of the same 280 pages, starting as a card
never seen:

| Run | Pages in flight | Spilled | Peak on the card | Pages/min | Words |
|---|---|---|---|---|---|
| 1 | 2, first try | no (76 MiB) | 10.8 GB | 64.7 | 122,858 |
| 2 | 3, one more → 2 → 1 | 194 MiB at 70 s, 5.2 GB by the end | 15.9 GB | 53.2 | 122,858 |
| 3 | 2, the most the card holds | no | 11.4 GB | 71.0 | 122,858 |
| 4 | 2 | no | 11.5 GB | 69.2 | 122,858 |
| *fixed 3, half an hour earlier* | *3* | *no* | *12.9 GB* | *79.6* | *122,858* |

The tuning did what it was designed to: tried one more, caught the spill, remembered it, and settled
at two, where the next two runs did not spill. The words were identical in every run.

**But three pages on this card is a coin toss, not a property of the card.** The same pages at the
same setting peaked at 12.9 GB in one run and filled the card (15.9 GB, 24 s in) in the next, with the
desktop holding 0.7 and 1.3 GB respectively. Once the pool had spilled, the spill went on growing
after the run had stepped down to one page, so fewer pages in flight do not bring a spilled run back.

**Why, is not known.** The first explanation offered here was the pool growing in regions that double
in size, with Windows' CUDA driver satisfying the oversized request from system RAM. A probe replaying
this run's 280 page sizes against the raw ONNX Runtime (detector, line classifier and recogniser, 3
threads; #34, `ManualForge-baselines/2026-10-10-issue-34-evidence/arena*`) does not support it:

| Pool growth | Peak on the card | Spilled | Pages/min |
|---|---|---|---|
| doubling (the default), two runs | 13.35 GB | none | 346, 373 |
| by what is asked for, two runs | 14.6 GB | 204 MiB, 1.6 GB | 335, 328 |

Growing by request used more memory and spilled, and the default did neither. Nor did the probe
reproduce the 15.9 GB run, so something in the real pipeline that the probe leaves out also matters:
the page-orientation model, the re-reads of stacks and orphan glyphs, recogniser batches wider than the
probe's, or what else had been loaded on the card. A cap on the pool (`gpu_mem_limit`, 11,000 MiB) did
not cap the process either: it peaked at 12.5 GB, because cuDNN's workspaces are allocated outside the
pool.

So tuning settles on the most pages in flight that the card holds *reliably*, which here and today is
two: about 70 pages a minute, against about 80 at three when three does not spill and 53 when it does.
The driver's sysmem fallback can be turned off for ManualForge (NVIDIA Control Panel, "CUDA - Sysmem
Fallback Policy"), which would make an allocation that does not fit fail on the card instead of
spilling. Whether ONNX Runtime then recovers or the run fails is untested.

## TensorRT on the card (#56)

The library's re-read on the night of 10 October (#46) was the first tuned run with TensorRT. With no
profile it started at two pages. After 105 files the guard saw 132 MiB go out to system RAM, 4 MiB over
CUDA's margin, stepped down to one, and marked two unsafe. Every later run on the card would have
started at one.

A sweep on 11 October settled it. `tools/measure-gpu-settings.ps1`, which now traces the process's
spilled memory, ran all seven test books in `_compare` (345 pages) on TensorRT, at a fixed setting,
twice each:

| Pages in flight | Pages/min | Peak on the card | Spilled: least → most | Words |
|---|---|---|---|---|
| 1 | 96.1, 102.3 | 7.3 GB | 74 → 78 MiB | 122,925 |
| 2 | 157.9, 151.2 | 12.2 GB | 74 → 78 | 122,925 |
| 3 | **177.7, 185.8** | 15.6 GB | 74 → **206**, 74 → 142 | 122,925 |
| 4 | 125.4, 188.2 | 16.0 GB | 74 → **3,940**, 74 → **5,172** | 122,925 |

**At three, TensorRT moves memory out and brings it back.** In the first run, 206 MiB was out 11
seconds in, 142 by the 43rd, and it stayed there; in the second, 142 at 37 seconds and 76 by 72. Three
was the fastest setting both times. That is the 132 MiB the night run saw at two, and it isn't a
spill. **At four it is one:** 1.1 GB out seven seconds in, past 2 GB by the 16th, growing to the end,
with the card full. One of those runs still averaged 188 pages a minute and the other 125, which is
the coin toss #31 found at the edge on CUDA.

So TensorRT runs get their own margin, 512 MiB: well clear of the warm-up's 132, and well short of the
1.1 GB a real spill reached within seconds. CUDA keeps 128. Its smallest real spill was 246 MiB and
nearly halved the speed, and nothing like TensorRT's warm-up has been seen on it. The engine is now
part of the profile's key, so neither engine's limits or speeds set the other's.

Three pages is the setting for this card on TensorRT: 182 pages a minute against 155 at two and 99 at
one. The words were identical at every setting.

## Evidence so far

`ConcurrencyControllerTests`, on the readings measured above:

- A healthy run (76 MiB steady), and a card that looks full but hasn't spilled, are left alone.
- **Four pages, 322 MiB out:** steps down to three.
- **Six pages, 4.3 GB out before the first window:** caught, against what the process held before
  its first page.
- Memory that went out and stayed out doesn't keep pushing it down. More going out does.
- It never steps up, and without the counter it decides nothing.

`GpuProfileTests`, a card tuned over several runs:

- **The 5070 Ti's curve:** 2 → 3 → 4, four spills, back to 3, and 3 from then on.
- A spill at a setting the card has held lowers the next start but doesn't rule the setting out.
- A short clean run, or one without the counter, vouches for nothing. A short run that spills is
  still believed.
- A run with a fixed setting leaves what is known about the card alone.
- **TensorRT's warm-up (74 → 206 → 142 MiB) is not a spill.** The card climbs past three. The same
  readings on CUDA are one, as they should be. A real TensorRT spill (1.1 GB, then 2.3) is still
  caught, and four is marked unsafe.
- TensorRT and CUDA profiles are kept apart. CUDA's key is the one profiles had before.
- A new driver drops the unsafe setting and keeps the best one and the speeds. `--retune` forgets
  both pages-in-flight figures and keeps the speeds.

A real pipeline (`RecognitionPipelineTests.ARunWhoseMemoryStartsGoingOutStepsDownAndFinishes`) with
a simulated engine and counter steps from three to two and still reads every page. The counter
reader gives the same figure as `Get-Counter` for a live process.
