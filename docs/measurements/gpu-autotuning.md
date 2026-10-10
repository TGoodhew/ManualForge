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
  card, for good. The next run goes back to the one below.
- A run that spills at a setting the card **has held before** may have been pushed over by something
  else (a game, a browser). The next run starts one lower and climbs back. The setting isn't ruled out.
- A run too short to say, or one where the counter could not be read, learns nothing about the card.

So the card settles on the most pages in flight it holds. On every card measured, that was also the
fastest: each page added was faster until the card spilled (50, 75, 87, 91 pages a minute at one to
four on the 5070 Ti), and spilling was slower than any of them. Eight is the most any run will try.
That isn't a figure for any card: it's above anything one has been seen to want.

**Within a run, only down.** Every 20 seconds and `max(12, 4 × pages in flight)` pages, the run reads
how much of its own GPU memory Windows has moved out to system RAM (the
`GPU Process Memory\Shared Usage` performance counter for the process). If that has risen more than
128 MiB above what the process held before its first page, the card is over the edge, and the run
steps down one at once. After a step down, the mark moves up to what is out now, because the memory
pool doesn't hand back what was moved. What counts after that is whether more goes. A step down
takes effect as pages finish; nothing in flight is interrupted.

## What is remembered

`%LOCALAPPDATA%\ManualForge\gpu-profiles.json`, one entry per card, keyed by name and total memory.
The driver version is recorded but not part of the key. A driver update moves speed by a few per
cent, which isn't worth re-tuning from nothing.

| Field | Learned from | Used for |
|---|---|---|
| `BestConcurrency` | the most pages in flight a run of 100+ pages held without spilling; one less after a spill | the next run starts one above it (a new card starts at 2) |
| `UnsafeConcurrency` | a spill at a setting being tried for the first time | never tried again; only ever lowered |
| `RunPagesPerMinute` | a run of 100 pages or more, end to end, averaged with the last | `survey`, `status` and `run` estimates |
| `RepairPagesPerMinute` | per page kind and resolution, 30 pages or more, first page excluded, averaged with the last | `repair`'s estimate |

Until a card has a figure, the estimates use the 5070 Ti's measurements and say so ("as measured on
an RTX 5070 Ti; this <card> has not been timed yet"). With no NVIDIA card they use the CPU figure.
`--gpu-concurrency <n>` fixes pages in flight and turns tuning off. A fixed run still records its
speed and never clears a known cliff.

Each run's log records the card, its driver, the setting and why it was chosen, any step down with
the reason, and what the next run will try. Deleting the file starts the card again from two.

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
pool. It never shrinks, so a run that tried four pages and went back to three kept four's pool. And
it grows in steps that double, so growing it a page at a time left it larger than growing it once:
the tuned runs peaked at 15.6–15.8 GB of the card, the fixed one at 12.9. PaddleOcrNet 2.2.1 has no
option to change how ONNX Runtime's pool grows. Within a run, trying more costs the rest of the run,
so the trying happens between runs, where every run starts with a fresh pool.

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

A real pipeline (`RecognitionPipelineTests.ARunWhoseMemoryStartsGoingOutStepsDownAndFinishes`) with
a simulated engine and counter steps from three to two and still reads every page. The counter
reader gives the same figure as `Get-Counter` for a live process.
