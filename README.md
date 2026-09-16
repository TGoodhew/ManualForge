# ManualForge

Searchable PDFs from scanned technical manuals, and a searchable index over them.

**Status: phase 1 — console prototype, running on CUDA.** It OCRs a PDF end to end and overlays an invisible text
layer whose alignment is measured rather than assumed. The classifier, flatten path, parallel
pipeline, WinUI shell, FTS5 index, VLM sidecar and benchmark mode are phases 2–7 and are not built
yet.

## What phase 1 does

```
manualforge ocr <input.pdf> [options]      rasterise → recognise → overlay → verify
manualforge inspect <input.pdf>            page count, sizes, landscape pages, existing text
manualforge gpu                            which execution provider is actually active
```

The source file is opened read-only and is never written to. Output goes to a separate file,
written to `<name>.partial` first and moved into place only once it is complete.

### Verified, not assumed

Every run re-opens the file it just wrote with **PdfPig** — a different library from the PDFsharp
one that wrote it, so a shared bug cannot cancel itself out — and reports how far each word's
baseline is from the box the recogniser detected:

```
  page   29   1,094 characters  266/266 words matched  deviation mean 0.000 pt, worst 0.000 pt
```

`--verify-ink` goes further and re-renders both files at 150 dpi to prove the page image did not
change and the text layer added no ink:

```
  page   29  identical (2,097,528 pixels)
Page images are byte-identical: the text layer adds no ink.
```

Measured on `11683A.pdf`, including its two `/Rotate 90` pages: 749 of 749 words within 0.000 pt,
renders byte-identical, source MD5 unchanged.

## Requirements

- Windows 10/11, x64
- .NET 10 SDK (built against 10.0.303)
- Visual Studio 2026, or just `dotnet build`

```
git clone <this repo>
cd ManualForge
dotnet build
dotnet test
```

The solution is a classic `.sln` with three projects and central package management
(`Directory.Packages.props`), so VS 2026 opens it directly.

## First run

The OCR models are not in the repo. On first use PaddleOcrNet downloads the ONNX detection,
recognition and orientation models and caches them under:

```
%LOCALAPPDATA%\ManualForge\models
```

Roughly 20–40 MB, fetched once. Override with `--models <path>` to share a cache between machines
or to keep it off the system drive. Once cached, runs are fully offline.

Structured logs, one JSON object per line, go to:

```
%LOCALAPPDATA%\ManualForge\logs\manualforge-YYYYMMDD.jsonl
```

A first run, on three pages, writing nothing over the source:

```
manualforge ocr "C:\...\Manuals\11683A.pdf" --out out.pdf --pages 6,29,31 --verify-ink
```

## GPU: CUDA

**CUDA is active on this machine.** Measured on 20 pages of `11683A.pdf` at 300 dpi:

| Provider | 20 pages | Throughput | ~100k pages |
|---|---|---|---|
| CPU (24 threads) | 78.8 s | 15.2 pages/min | ~110 hours |
| CUDA (RTX 3060 Ti) | 21.5 s | **55.9 pages/min** | ~30 hours |

A 3.7x speedup, before phase 3 overlaps rasterisation and PDF assembly with inference.

### What is installed

- **CUDA Toolkit 13.4** at `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4`.
  ONNX Runtime 1.30 hard-imports `cublas64_13.dll` and `cublasLt64_13.dll`, so CUDA **13** is
  required — a CUDA 12 install will not load.
- **cuDNN 9.26.0.51 for CUDA 13** at
  `C:\Tools\cudnn-9.26.0.51\cudnn-windows-x86_64-9.26.0.51_cuda13-archive\bin\x64`.

Note the trap: the cuDNN zip extracts into a nested folder, so the directory holding the DLLs sits
three levels below where the zip lands. Putting the extract root on `PATH` instead of the inner
`bin\x64` silently falls back to CPU.

To check the whole chain at once:

```
manualforge gpu     ->   Active provider : Cuda
                         Using GPU       : True
```

If it reports `Cpu`, the hint line names the exact DLL that failed to load.

### VRAM is the binding constraint, not compute

Peak VRAM during that run was **7,556 MiB of 8,192**, and the desktop, Copilot and Claude already
held 2,865 MiB before it started — roughly **640 MiB of headroom** at batch size 8, one page at a
time. Phase 3 must therefore size batches against *free* VRAM measured at startup rather than
against the card's nominal 8 GB, and must not assume it can raise the batch size or run pages
concurrently without checking first.

### Results are not bit-identical across providers

The same 20 pages produced 6,498 words on CUDA and 6,495 on CPU: three words in about 6,500, or
0.05%. Different kernels reduce in different orders, which moves a handful of borderline detections
across the confidence threshold. Nothing is wrong, but benchmark mode in phase 7 has to record
which provider produced a result rather than treating the two as interchangeable.

### DirectML

Not added, deliberately. `Microsoft.ML.OnnxRuntime.DirectML` is not on the agreed dependency list,
and it ships its own `onnxruntime.dll`, so it cannot coexist with `Microsoft.ML.OnnxRuntime.Gpu` in
one output folder — supporting both would mean two build configurations carried through the
remaining phases. Its value is running on non-NVIDIA GPUs, which this tool does not need. The CPU
path remains as the fallback and degrades gracefully. This is a deviation from the original spec,
which asked for a selectable DirectML fallback; it is reversible at any point.

## How the text layer is built

This is the part that separates a usable text layer from a useless one, so it is worth stating
exactly what happens.

Each recognised word becomes one text-showing operation inside a single `BT`/`ET` block:

```
3 Tr                                  text rendering mode 3: invisible
/MFInvisible 14.88 Tf                 font size = the detected box height, in points
83.7 Tz                               horizontal scaling: stretch the run to the box width
1 0 0 1 72 681.12 Tm                  text matrix: baseline origin, rotated with the page
<0001000200030004> Tj                 two-byte glyph codes
```

- **`Tz` is what makes it work.** Without it the run keeps the font's natural advance widths and
  drifts further from the scan with every character. With it, the run ends exactly at the right
  edge of the detected box.
- **The font is a generated glyphless TrueType**, built at run time, in which every glyph has zero
  contours and advances by exactly half an em. The uniform advance makes the `Tz` factor a
  closed-form number rather than a font-metrics lookup, and the absent outlines mean the text stays
  invisible even in a reader that mishandles rendering mode 3. It is embedded as a `Type0` /
  `CIDFontType2` with `Identity-H` encoding and a `ToUnicode` CMap, so `Ω`, `µ`, `°`, `±` and `√`
  survive — all of which a WinAnsi-encoded layer would lose, and all of which appear constantly in
  this corpus.
- **Page rotation and crop-box origin are both undone.** PDFium rasterises the page as a reader
  displays it; content-stream coordinates are always unrotated. For `/Rotate 90` the invisible text
  therefore runs *up* the unrotated page so that it lies along the scanned glyphs once the reader
  turns it. Non-zero crop origins are shifted back.
- **The original content is wrapped in `q`/`Q` before the overlay is appended.** Scanned pages
  routinely leave the graphics state dirty — a `cm` set for the page image and never restored —
  and without the wrapper the text would inherit it.

## Project layout

```
src/ManualForge.Core/
  Geometry/PageGeometry.cs        image pixels ↔ PDF user space; rotation, crop origin
  Text/GlyphlessTrueTypeFont.cs   generates the blank font program
  Text/InvisibleFont.cs           Type0/CIDFontType2 objects, subsetting, ToUnicode
  Text/TextLayerWriter.cs         BT/ET, Tr 3, Tz, Tm emission
  Ocr/PaddleOcrEngine.cs          PaddleOcrNet wrapper, word boxes, provider selection
  Rendering/PageRasteriser.cs     PDFium via PDFtoImage
  Verification/                   PdfPig re-extraction, baseline deviation, ink comparison
  Pipeline/SearchablePdfBuilder.cs end-to-end for one file
  Diagnostics/JsonFileLogger.cs   JSON-lines log provider
src/ManualForge.Cli/              the prototype's command line
tests/ManualForge.Core.Tests/     50 tests, no GPU or network needed
```

## Tests

```
dotnet test
```

50 tests, about 0.7 s, no models and no network required:

- **`PageGeometryTests`** — the corner mapping for all four rotations, non-zero crop origins,
  text-matrix direction, points-per-pixel, rotation normalisation.
- **`GlyphlessFontTests`** — the generated font is a well-formed TrueType: table directory sorted,
  checksum formula satisfied, four-byte alignment, uniform half-em advance, declared glyph boxes.
- **`TextLayerRoundTripTests`** — the real one. Writes a layer onto a synthetic page with PDFsharp
  and reads it back with PdfPig, for all four rotations crossed with zero and non-zero crop
  origins, asserting every word lands within 0.02 pt (four-decimal content-stream rounding).
  Also covers non-Latin-1 characters, `Tz` width, confidence filtering, and the writer surviving a
  page that leaves an unbalanced `q` behind.

## Open questions for phase 2

1. ~~**DirectML**~~ — resolved: not added, see "DirectML" above.
2. ~~**CUDA 13 runtime + cuDNN 9**~~ — resolved: installed and active, 3.7x faster.
3. **Serilog** — file logging is currently a 150-line hand-rolled JSON-lines provider to avoid an
   unapproved dependency. Fine to keep, or would you rather have Serilog?
4. **xunit** — the test projects use xunit, the `dotnet new` default. Called out for completeness
   since it was not on the list.
5. **Locating CUDA without `PATH`** — the app relies on the user's `PATH` to find the CUDA and
   cuDNN DLLs, which is fragile for something launched by double-clicking. Phase 3 should locate
   them at startup and add them to the process DLL search path, reporting clearly when it cannot.
6. **Baseline offset** — `TextLayerOptions.BaselineOffsetFraction` currently defaults to 0, putting
   the baseline on the bottom edge of the detected ink box. Worth tuning against ground truth in
   phase 7 rather than guessing now.
