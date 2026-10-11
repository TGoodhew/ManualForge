# Why neither the GPU nor the CPU is busy

On an RTX 5070 Ti, `run` levels off around 85–95 pages a minute with the GPU busy about 40% of the
time and the CPU at about a quarter, whatever the pages in flight, batch size or rasterisers (#31).
This is the investigation for #32. **The cause is ONNX Runtime's CUDA convolutions: each change of
input shape costs about 9–18 ms that can't be overlapped, and OCR changes shape on nearly every
call.** Nothing within reach fixes it without changing what is read. Taking it further is #34.

9 Oct 2026. RTX 5070 Ti, ONNX Runtime 1.30, cuDNN 9.26, PaddleOcrNet 2.2.0 (and 2.2.1 where stated),
`_compare/typical-test.pdf` (100 pages) at 3 pages in flight unless stated.

## Where the time goes

A `dotnet-trace` thread-time capture of `run` (73 s wall), broken down by `tools/trace-stages.cs`:

| Stage | Thread-seconds |
|---|---|
| Recogniser `InferenceSession.Run` | 115.2 |
| Text-line classifier `Run` | 38.4 |
| Detector `Run` | 20.8 |
| Orphan glyphs (ours) | 3.8 |
| Text layer, verification, page cache (ours) | 1.6 |
| Tall stacks (ours) | ≈0 |

The three GPU workers spent 81% of their time inside `Run`, with the card 40% busy. ManualForge's
own steps are not the problem.

## Ruled out

| Suspect | Result |
|---|---|
| GC pauses | 4% of the time (`dotnet-counters`). Allocation is heavy, 2.1 GB/s with up to 6.8 GB of large-object heap, but it is not the limit. |
| Thread-pool starvation | The thread pool always had work ready. |
| Locks in PaddleOcrNet | Its semaphores only guard loading each model the first time. |
| Pages sharing one session | One engine per page in flight: 76.8 against 74.9 pages/min at 2, 89.5 against 83–87 at 3. Noise. |
| Operations falling back to the CPU | The recogniser's profile is 93% CUDA node time, 0.2% CPU. |
| Switching between models | With fixed shapes, the classifier takes 1.2 ms and the recogniser 4.1 ms, alternated or not. |
| One model stalling the others | The classifier stays at 2.0 ms while the recogniser changes shape on another thread. |
| Provider and session options | `cudnn_conv_algo_search` HEURISTIC or EXHAUSTIVE, `cudnn_conv_use_max_workspace=0`, `prefer_nhwc=1`, `EnableMemoryPattern=false` and `arena_extend_strategy=kSameAsRequested` all leave the per-change cost where it is. HEURISTIC end to end, back to back: same speed, same memory, same words (an earlier "2 GB lighter" was the desktop's share of the card drifting between runs). `DEFAULT` took over 9 minutes for what takes 1. |

## The cause

`tools/probe-shape-changes.cs`, recogniser, batch 8, height 48, profiling off:

| Calls | 1 thread | 3 threads | 6 threads |
|---|---|---|---|
| Fixed width 640 | 4.2 ms/call | 4.7 ms/call | – |
| Widths varying 96–1,392 | 22.8 ms/call | 14.5 ms/call | 13.3 ms/call |

- With each width repeated four times in a row, the median falls back to about 9 ms, and only the
  call where the width changes is slow.
- Widths seen before are no faster.
- The cost sits inside the Conv nodes.
- Extra threads reduce it by about 40%, then it flattens.

PaddleOcrNet pads each batch of lines to its own widest line, so nearly every batch is a new width,
and pages in flight interleave on the same session. That is why more pages stop helping well before
the card is busy.

## The fix that was tried

`tools/probe-width-buckets.cs`, realistic widths:

| Recogniser | 1 thread | 3 threads |
|---|---|---|
| As now | 23.3 ms/call | 14.3 ms/call |
| One session, widths rounded to 320/480/640/960/1280/1920/2560/3200 | 23.7 | 15.4 |
| Session per bucket, exact batch counts | 14.7 | 11.1 |
| Session per bucket, batches padded to 8 | 7.7 | 8.9 |

The last row was built into a local copy of PaddleOcrNet 2.2.1 and run end to end on the typical and
table books (200 pages):

| | Pages/min | GPU busy | ManualForge's own peak | Words |
|---|---|---|---|---|
| 2.2.1 as shipped | 85.2 | 40% | 11.8 GB | 86,105 |
| With buckets | 90.2 (+6%) | 66% | 14.8 GB | 86,052 |

The gain was +6% (+8% on the 100 typical pages alone), and the readings changed: 61 of 100 typical
pages differ, and part numbers run into their makers' names (`0007.5789.00ROEDERSTEI` ×3,
`0099.8780.00MURATA` ×3). The extra padding moves where the recogniser puts word gaps, the same
effect as batch 16 in `gpu-concurrency-5070ti.md`. **Rejected.**

PaddleOcrNet 2.2.1's `SpaceRecoveryThreshold` (0.15), tried as a way to put those gaps back, adds
1,607 words (+1.9%). On these manuals they are mostly false splits of part numbers and values:
`+-5PF` → `+ -5PF`, `PD-500MW` → `PD- 500MW`, `100PF` → `100 PF`, `TC-0±100` → `TC -0 ±`,
`LED` → `LE D`. **Rejected.**

With deskew and space recovery off, PaddleOcrNet 2.2.1 read the same words as 2.2.0 on all 100 typical
pages.

## Our own orientation re-read

Serial `ocr` with `--trust-orientation` took 135.7 s against 165.6 s, so verifying the page classifier
costs about 18%. It saves 1,235 of 36,001 words (`page-orientation.md`), so it stays. Making it cheaper
is a separate question.

## What the cost is, at the native level (#34)

10 Oct 2026, same card and versions, the same probe (`tools/probe-shape-changes.cs`) with production's
`EXHAUSTIVE` search. Evidence in `C:\Users\Tony\ManualForge-baselines\2026-10-10-issue-34-evidence\`.

**Not the ONNX Runtime version.** 1.31.0, the release after the one shipped, gives the same figures:

| | 1.30.0 | 1.31.0 |
|---|---|---|
| Fixed width, 1 thread | 4.55 ms/call | 4.40 |
| Varying widths, 1 thread | 24.55 | 24.97 |
| Varying widths, 3 threads | 15.00 | 15.97 |

**Not CUDA's lazy module loading.** With `CUDA_MODULE_LOADING=EAGER` the varying-width calls took
24.59 ms.

**Code libraries loaded and thrown away.** An Nsight Systems 2026.3.2 trace of the probe
(`shapes130.nsys-rep`) shows `cuLibraryLoadData` and `cuLibraryUnload` in pairs, about eight per call,
but only in the phases where the width changes. In the fixed-width phases there are none at all. In
half-second bins, the varying phases show 160 to 300 load/unload pairs and about 5,000 kernel
launches; the fixed phases show none, and about 30,000 launches. The 5,068 loads and 5,037 unloads
took 2.4 s of API time on their own. Unloading straight away is why a width seen before is no faster.

**cuDNN rebuilding its plans, with kernels compiled at run time.** cuDNN 9.26's own log
(`CUDNN_LOGLEVEL_DBG=3`; 6.4 GB for 40 calls of each kind) shows what happens on each new shape.
ONNX Runtime's convolutions go through cuDNN's graph ("backend") API, and for every Conv node:

- an operation graph is built and finalized (6,632 of them);
- the heuristics are run in both modes A and B (6,632 each);
- candidate engine configurations are finalized: 278,245 finalize calls, 96,152 engine configs
  logged, 80,387 `NOT_SUPPORTED` and 10,114 `NOT_INITIALIZED` replies along the way.

**93,988 of those candidates carry `CUDNN_BEHAVIOR_NOTE_RUNTIME_COMPILATION`**: their kernels are
compiled at run time for the exact shape, then loaded. That is the per-shape cost: building plans and
compiling kernels for a shape, using them, and keeping nothing. (With the log on, a changing call took
about 490 ms. The logging is slow; the counts are what matter.)

So the cost is not a shortcoming of ONNX Runtime's options or of how ManualForge calls it. It is
cuDNN 9 planning every convolution from scratch for each new input shape, and ONNX Runtime not keeping
those plans by shape. cuDNN has features meant for this, a cache for runtime-compiled kernels and
dynamic-shape graphs, but they have to be used by the caller, which here is ONNX Runtime's CUDA
provider.

## Ways out, and what each costs

| Route | What it would take | Expected effect on words |
|---|---|---|
| ONNX Runtime keeps cuDNN plans per shape, or uses cuDNN's dynamic-shape kernel cache | An upstream change to ONNX Runtime's CUDA provider; report with the probe as a repro | None: same kernels chosen per shape |
| TensorRT execution provider with dynamic-shape profiles and its engine cache | TensorRT installed alongside CUDA and cuDNN (#26's installer would have to carry it); a build of PaddleOcrNet that can select it | Must be measured: different kernels, possibly different arithmetic |
| A different cuDNN 9.x | Only if an older or newer release has precompiled kernels for these shapes on Blackwell; needs downloads to test | None expected |
| Fewer shapes (width buckets) | Already tried in #32 | Changed 61 of 100 pages: rejected |

