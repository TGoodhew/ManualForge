# Pages in flight tuned on any card

#31 asked for the GPU settings to suit the card they run on. The first answer
([gpu-concurrency-5070ti.md](gpu-concurrency-5070ti.md)) re-measured one card and wrote what it
found into the code: three pages in flight from 16 GB, two below, 3,000 MiB budgeted per extra
page, 151.7 pages a minute in every estimate. That is right for the RTX 5070 Ti and a guess
everywhere else. It was the same mistake the 3060 Ti's figures had made: they went on being used
after that card was replaced.

**Now no card's figures are built in.** A run finds its own pages in flight as it goes, and the
machine remembers what it found for the next run on the same card.

## What is tuned, and what is not

| Setting | Tuned? | Why |
|---|---|---|
| Pages in flight | **yes** | The output is identical at every setting (36,001 words at one to four pages), and the best value depends on the card and on what else is using it. |
| Batch size | **no** | It changes the words read: 58 of 100 pages differ between batch 8 and 16. A tuner chasing speed would quietly change the text, so it stays at 8. |
| Rasterisers | no | Two, four and six give the same speed. Rasterising is not the limit. |
| cuDNN algorithm search | no | Exhaustive and heuristic were the same within noise. |

## How a run tunes

The run judges itself in windows of at least 20 seconds and at least `max(12, 4 × pages in flight)`
pages. The first window is thrown away, because it pays for loading the models and, on a card's first
run, compiling its kernels (13.8 s for the first page against 4.9 s after).

After each window it reads how much of its own GPU memory Windows has moved out to system RAM (the
`GPU Process Memory\Shared Usage` performance counter for the process), and decides:

1. **Memory going out.** If that has risen more than 128 MiB above the least the run has held
   there, the card is over the edge and it steps down at once.
2. **Collapse.** Under half the speed of a lower setting, it steps down. This catches the edge on a
   machine where the counter can't be read.
3. **Judging a step up.** After two windows at the new setting, it keeps the step if it was at least
   5% faster. Otherwise it goes back down and stops climbing. Identical whole runs differ by about
   5%, so a smaller gain can't be told from noise.
4. **Otherwise it tries one more.**

A setting that spills or collapses *while being tried* is marked unsafe for the card. One that spills
later, after it had been fine, may have been pushed over by something else (a game, a browser). It
only lowers where the next run starts, and that run may climb again.

After a step down, the mark for "gone out" moves up to what is out now, because the memory pool does
not hand back what was moved. What counts after that is whether more goes.

Lowering the number takes effect as pages finish; nothing in flight is interrupted. The run starts
up to eight workers. A gate decides how many may hold a page, so the tuner moves a limit and never
starts or stops a thread. Eight is a cap on workers, not a figure for any card: four was fastest on
the 16 GB card and six had collapsed.

## What is remembered

`%LOCALAPPDATA%\ManualForge\gpu-profiles.json`, one entry per card, keyed by name and total memory.
The driver version is recorded but not part of the key. A driver update moves speed by a few per
cent, which isn't worth re-tuning from nothing.

| Field | Learned from | Used for |
|---|---|---|
| `BestConcurrency` | the last step up that paid, less any that later spilled | where the next run starts (a new card starts at 1) |
| `UnsafeConcurrency` | a spill or collapse on a step up | never started at or climbed to; only ever lowered |
| `RunPagesPerMinute` | a run of 100 pages or more, end to end, averaged with the last | `survey`, `status` and `run` estimates |
| `RepairPagesPerMinute` | per page kind and resolution, 30 pages or more, first page excluded, averaged with the last | `repair`'s estimate |

Until a card has a figure, the estimates use the 5070 Ti's measurements and say so ("as measured on
an RTX 5070 Ti; this <card> has not been timed yet"). With no NVIDIA card they use the CPU figure.
`--gpu-concurrency <n>` fixes pages in flight and turns tuning off. A fixed run still records its
speed and never clears a known cliff.

Each run's log records the card, its driver, what the tuner started from, every change it made with
the reason, and where it settled.

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

## Evidence so far

Simulated cards (`ConcurrencyControllerTests`), on the curves measured above:

- **Plenty of room** (50.4, 74.9, 86.8, 91.4, 92): climbs to 4, tries 5, gives it back.
- **Looks full, has not spilled:** still climbed. This is the case the first version got wrong.
- **10 Oct, 4 spills 322 MiB at 46 pages/min:** back to 3, 4 marked unsafe.
- **No counter, 8 GB collapse** (50, 78, 7.6): back to 2, 3 marked unsafe.
- **Another program pushes a settled run over:** steps down, lowers the next start, card not marked.
- **Memory that went out and stayed out** does not keep pushing it down.
- A remembered unsafe setting is never reached.

A real pipeline (`RecognitionPipelineTests.ATunedRunClimbsWhileMorePagesInFlightPay`) with a
simulated engine climbs from 1 to at least 2 and runs that many pages at once. The counter reader
gives the same figure as `Get-Counter` for a live process.
