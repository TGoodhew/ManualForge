# TensorRT through our PaddleOcrNet fork (#53)

#34 found that cuDNN plans every convolution afresh for each new input shape, and that ONNX Runtime's
TensorRT provider avoids it (`recognition-shape-changes.md`). PaddleOcrNet 2.2.1 can't select
TensorRT, so ManualForge now builds against a fork that can (`docs/paddleocrnet-fork.md`). This is
that build, measured against the baselines kept before #34 began
(`C:\Users\Tony\ManualForge-baselines\2026-10-10-before-34\`).

10 Oct 2026, RTX 5070 Ti 16 GB, driver 617.42, CUDA 13.4, cuDNN 9.26, TensorRT 10.16.1.11, ONNX Runtime
1.30, PaddleOcrNet 2.2.1 plus the fork's TensorRT commits (`2.2.1-tensorrt.3`).

## The fork on CUDA changes nothing

`tools/measure-single-characters.cs`, config `engine`, with the fork's packages on CUDA. Both books read
**byte-for-byte as the baselines did**: identical `ours-engine.tsv`. So `--engine cuda` gives exactly
the readings ManualForge has always given.

## TensorRT, by which models use it

| | `run`, 280 test pages, 3 at once | Words differing from CUDA, 100 typical pages | Acrobat tokens matched, typical (of 28,912) | Acrobat tokens matched, tables (of 43,944) |
|---|---|---|---|---|
| CUDA | 78.4 pages/min | – | 17,568 | 34,147 |
| TensorRT: classifier and recogniser (**default**) | **151.4** | **1.0%** (328 / 294) | 17,559 | – |
| TensorRT: all three | 165.0 | 2.3% (745 / 645) | 17,570 | 34,141 |

- "Words differing" counts, page by page, the words only one side read. The pair is CUDA's count
  then TensorRT's. Positions are left out, because TensorRT's detector moves boxes by fractions of a
  point. On the table book, all three on TensorRT changed 0.9% (425 / 419).
- **Accuracy:** against Acrobat, every setup is within noise of CUDA.
- **The detector:** most of the extra change in the all-three setup comes from it. A box a little
  different is a line cropped a little differently. Keeping it on CUDA keeps 92% of the speed-up and
  changes under half as many words.
- **The default:** Tony chose classifier and recogniser on TensorRT, with the detector on CUDA.
  `--tensorrt-models det,cls,rec` adds the detector.
- **The figure #34 quoted:** 0.6% was the recogniser alone, on 32,140 words.

## Pages in flight, tuned

Two runs of the 280 pages with `--engine auto` (TensorRT, all three models in that build), as a user
would run them, on a card never seen:

| Run | Pages in flight | Pages/min | Spilled |
|---|---|---|---|
| 1 | 2, the first try | 134.6 | no |
| 2 | 3 | 165.0 | no |

On CUDA, three pages in flight spilled in one run out of three (`gpu-autotuning.md`). On TensorRT it
held, and the next run will try four.

## Engines

The first run on a card builds the engines: about a minute for the detector (22 MB), the classifier
(7 MB) and the recogniser (9.7 MB). After that they're loaded from
`%LOCALAPPDATA%\ManualForge\models\tensorrt`. TensorRT rebuilds them when the card, the TensorRT
version or the shape limits change.

## What the library would see

These are the test books, not the library. A re-read of the library with TensorRT (#46) would change
about 1% of its OCR'd words, mostly low-confidence readings, some better and some worse. It wouldn't
change how much text is found, and it would take about half as long as on CUDA.
