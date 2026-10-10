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

After each window it reads the card's free memory from `nvidia-smi` and decides:

1. **Too little memory free.** Below the reserve (5% of the card, at least 512 MiB) it steps down at
   once. This is the cliff, approached from the side that can be seen coming. If it happened during
   a step up, that number of pages is marked unsafe for the card. If it happened while settled,
   something else took the memory (a game, a browser), so the card is not blamed.
2. **Collapse.** Under half the speed of a lower setting it steps down and marks the setting unsafe.
   This is the cliff when memory reads fine, which is how it looked on the 8 GB card: memory sat
   flat at the top, because the memory pool had nowhere further to grow, and then the speed fell
   by ten times.
3. **Judging a step up.** After two windows at the new setting, it keeps the step if it was at least
   5% faster. Otherwise it goes back down and stops climbing. Identical whole runs differ by about
   5%, so a smaller gain can't be told from noise.
4. **Otherwise it tries one more**, if the memory free after the largest rise seen so far between
   settings would still clear the reserve. Before any rise has been seen, it allows a tenth of
   the card.

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
| `BestConcurrency` | the last step up that paid | where the next run starts (a new card starts at 1) |
| `UnsafeConcurrency` | a collapse, or a memory breach while climbing | never started at or climbed to; only ever lowered |
| `RunPagesPerMinute` | a run of 100 pages or more, end to end, averaged with the last | `survey`, `status` and `run` estimates |
| `RepairPagesPerMinute` | per page kind and resolution, 30 pages or more, first page excluded, averaged with the last | `repair`'s estimate |

Until a card has a figure, the estimates use the 5070 Ti's measurements and say so ("as measured on
an RTX 5070 Ti; this <card> has not been timed yet"). With no NVIDIA card they use the CPU figure.
`--gpu-concurrency <n>` fixes pages in flight and turns tuning off. A fixed run still records its
speed and never clears a known cliff.

Each run's log records the card, its driver, what the tuner started from, every change it made with
the reason, and where it settled.

## Evidence so far

Simulated cards (`ConcurrencyControllerTests`), shaped like the two measured:

- **16 GB plateau** (50, 75, 87, 91, 92 pages a minute): climbs and settles at 3. The step to 4 gains
  under 5% in the simulation.
- **8 GB cliff** with memory flat at the top, as measured: settles at 2 and marks 3 unsafe.
- A memory breach on a step up marks that step unsafe. A desktop taking memory while settled steps
  down without blaming the card.
- A remembered unsafe setting is never reached. With no memory reading at all it still climbs on
  speed alone. A card already nearly full is not climbed.

A real pipeline (`RecognitionPipelineTests.ATunedRunClimbsWhileMorePagesInFlightPay`) with a
simulated engine climbs from 1 to at least 2 and runs that many pages at once.

**Not yet measured on the card.** The design is to be checked against the fixed settings on
`_compare/typical-test.pdf`: where it settles, how fast, and that the words are unchanged.
