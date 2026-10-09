# Pages in flight, batch size and rasterisers on an RTX 5070 Ti

The RTX 3060 Ti (8 GB) was replaced by an RTX 5070 Ti (16 GB GDDR7, Blackwell, sm_120). Every GPU
default had been chosen against the old card, most of them by its 8 GB of memory. This is the
re-measurement for #31.

**Outcome:** three pages in flight by default on a 16 GB card (two below that), about 85 pages a
minute against 74.9 at the old default, same output. Batch stays at 8: batch 16 is 14% faster but
changes the words read. Neither processor is close to busy at any safe setting, which is now its
own issue, #32.

## Setup

9 Oct 2026. Build `0.1.0+66d5c24`, driver 617.42, CUDA 13.4, cuDNN 9.26, i9-12900K (24 threads).
The idle desktop holds about 2.6 GB of the card, which is now the only one and drives the display.

`manualforge run` on `_compare/typical-test.pdf` (100 image-only pages from varied documents), each
setting on its own fresh copy and state database so nothing came from the page cache. GPU memory and
busy % from `nvidia-smi` every second; CPU is total processor time across all 24 threads. Wall clock
includes model loading. `tools/measure-gpu-settings.ps1` is the same procedure as a script.

## CUDA on Blackwell

`manualforge gpu` reports `Active provider : Cuda`, and real runs used the GPU with every word verified.
**The first run on a new card compiles kernels for it**: page 1 took 13.8 s the first time and 4.9 s
the second, with 108 MB of compiled kernels left in `%APPDATA%\NVIDIA\ComputeCache`. A one-off cost
per machine (and per driver update), not a sign of a CPU fallback.

## Results

| Pages in flight | Batch | Rasterisers | Pages/min | Peak VRAM | GPU busy | CPU | Words |
|---|---|---|---|---|---|---|---|
| 1 | 8 | 2 | 50.4 | 8.2 GB | 23% | 14% | 36,001 |
| 2 | 8 | 2 | 74.9 | 11.8 GB | 37% | 22% | 36,001 |
| 3 | 8 | 2 | 86.8 | 13.3 GB | 37% | 26% | 36,001 |
| 3 (again) | 8 | 2 | 82.9 | 12.4 GB | 40% | 29% | 36,001 |
| 4 | 8 | 2 | 91.4 | 15.7 GB | 43% | 28% | 36,001 |
| 6 | 8 | 2 | **13.7** | 15.9 GB | 91% | 12% | – |
| 3 | 16 | 2 | 96.6 | 14.8 GB | 43% | 28% | 35,994 |
| 3 | 32 | 2 | 88.6 | 15.6 GB | 50% | 26% | 35,980 |
| 3 | 8 | 4 | 86.6 | 14.6 GB | 41% | 26% | 36,001 |
| 3 | 8 | 6 | 85.8 | 15.6 GB | 37% | 26% | 36,001 |

After the change, `run` with no flags chose three pages in flight by itself (15,033 MiB free at
startup) and read the same 100 pages at **87.3 pages a minute**, 36,001 words, peak 11.7 GB. That
is the figure `MeasuredThroughput.PagesPerMinuteOnGpu` now carries.

Serial `manualforge ocr` on the same pages: 36.2 pages a minute, GPU 18%, CPU 16%. The same three
pages-in-flight setting run twice came out 86.8 and 82.9, so differences under about 5% are noise.

## Pages in flight

**The cliff is still there, one step higher.** Six pages filled the card and ran at 13.7 pages a
minute, the GPU 91% busy moving memory over PCIe rather than recognising. Four was the fastest that
held, but at 15.7 of 16.3 GB it leaves nothing for a browser or a game opened partway through an
overnight run, and memory is read once at startup. Three peaked at 12.4-13.3 GB.

**The old per-page budget was the wrong size for this card.** Peak memory rose 1.5-3.6 GB per extra
page, against a budget of 700 MiB. ONNX Runtime's arena grows into whatever room there is, so these
are what the arena took rather than what a page strictly needs; on the 8 GB card the same settings
fitted in far less. Either way, at 700 MiB the formula allowed eighteen pages here, and only the
ceiling of two stood between the default and the cliff. The budget is now 3,000 MiB and the ceiling
depends on the card: three from 16 GB, two below. On the old card's idle desktop that still gives
two; on this card with a game holding 8 GB it gives two rather than three.

## Batch size changes what is read

Pages in flight and rasterisers leave the output identical: 36,001 words at every setting. Batch
size does not. Batch 16 against batch 8, word by word with PdfPig, page by page:

- **58 of 100 pages differ**, 236 words only in batch 8's output and 229 only in batch 16's.
- Some readings improve: `POWeR` → `POWER`, `TLO72ACD-B` → `TL072ACD-B`, `5OV` → `50V`,
  `ROHDE&SCHUAR` → `ROHDE&SCHWAR`.
- Some get worse: `BAR64-4` → `BAR54-4`, `10K` → `1OK`, `REF100` → `REF1OO`.
- **Batch 16 runs neighbouring words together** about a dozen times, mostly a part number into its
  maker on the parts lists: `0007.5789.00ROEDERSTEI`, `0099.8821.00MURATA`,
  `0009.5334.00DRALORIC`, `25403C280MAF/A22K`, `QTYQTY`. Both halves stop being searchable.

The likely mechanism: the recogniser pads every crop in a batch to the widest of them, and the
padding moves where word gaps fall. So batch size is a recognition setting, and the page cache's
settings fingerprint does not include it. Batch 16 is not adopted until it has been through the
Acrobat-token harness on the table and typical books (`tools/measure-single-characters.cs`). Batch 32
was slower than 16 as well as different.

## cuDNN algorithm search makes no difference

ONNX Runtime's CUDA provider defaults to `cudnn_conv_algo_search=EXHAUSTIVE`, which benchmarks
convolution algorithms for each new input shape. PaddleOcrNet's documentation recommends `HEURISTIC`
for OCR, and ManualForge has never set it. While profiling for #32, heuristic looked about 2 GB
lighter. **That was wrong.** The comparison put late-morning runs against early-morning ones, and
in between the desktop's own share of the card fell from about 2.6 GB to 1.1 GB. Run back to back,
with the card's memory read before each run and subtracted:

| Search | Pages in flight | Pages/min | ManualForge's own peak |
|---|---|---|---|
| exhaustive | 1 | 49.7 | 5.5 GB |
| heuristic | 1 | 50.3 | 5.5 GB |
| exhaustive | 3 | 83.9 / 87.5 | 13.9 / 13.1 GB |
| heuristic | 3 | 83.4 / 87.1 | 14.4 / 12.7 GB |

The words were identical every time, and so were all 100 pages compared with PdfPig. Speed and
memory are the same within noise, so the default is left alone. `DEFAULT` (no search at all) was
far slower: over 9 minutes for what takes 1, stopped.

**The same drift affects every peak in this document.** The memory pool grows into whatever room is
free, so ManualForge's peak at 3 pages was about 10 GB with the desktop holding 2.6 GB, and 12.7–14.4
GB with it holding 1.1. Peaks here are the card's total, desktop included, and show what fitted, not
what a page needs. 3 pages in flight ran a dozen times without reaching the cliff.

## Rasterisers

Two, four and six give the same throughput. Rasterising is not the limit.

## Neither processor is busy

At every setting that fits, the GPU is busy under half the time and the CPU at about a quarter,
roughly two threads per page in flight. Each added page gains less (+24, +12, +5 pages a minute from
one to four) and the curve ends at the memory cliff, not at a saturated processor. One serial step
inside each page sets the speed. #32.

## Server models on tables

Serial `ocr` on `_compare/tableonly-test.pdf`, the 100 captioned table pages from #22:

| Model | Pages/min | Peak VRAM | GPU busy | On the 3060 Ti |
|---|---|---|---|---|
| mobile (shipped) | 37.0 | 7.0 GB | 18% | 34.1 |
| server | **2.5** | **15.8 GB** | 95% | 3.6 |

The server pack filled the card from the start, even one page at a time (memory above 15 GB in 2,340
of 2,345 samples), and ran slower than on the card with half the memory. So the 3.6 pages a minute
behind #22's "29 hours" was most likely a measurement of spilling too, not of the model. Its accuracy
result is unaffected.
