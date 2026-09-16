# ManualForge

Searchable PDFs from scanned technical manuals, and a searchable index over them.

**Status: phase 1 — console prototype.** It OCRs a PDF end to end and overlays an invisible text
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

## CUDA vs DirectML

**On this machine, neither GPU path is active yet, and OCR runs on CPU at roughly 22 pages/min.**
Both need a decision from you before phase 3, where throughput starts to matter.

### CUDA

`PaddleOcrNet.Gpu` brings `Microsoft.ML.OnnxRuntime.Gpu` 1.30.0, whose CUDA provider is built
against **CUDA 13**, not CUDA 12:

```
Error loading "onnxruntime_providers_cuda.dll" which depends on "cublasLt64_13.dll" which is missing.
```

The RTX 3060 Ti and its 616.56 driver (CUDA 13.4 UMD) are fine. What is missing is the user-mode
runtime. To enable it, install the **CUDA 13.x runtime** and **cuDNN 9** and make sure their `bin`
directories are on `PATH`. Nothing in the project needs to change; `manualforge gpu` will then
report `Cuda`.

### DirectML

`--engine directml` currently falls back to CPU:

```
Unable to find an entry point named 'OrtSessionOptionsAppendExecutionProvider_DML' in DLL 'onnxruntime'.
```

DirectML lives in a **separate package, `Microsoft.ML.OnnxRuntime.DirectML`, which is not on the
agreed dependency list**, so it has not been added. Note that it and `Microsoft.ML.OnnxRuntime.Gpu`
both ship their own `onnxruntime.dll` and cannot sit in the same output folder, so supporting both
means a build configuration switch rather than a runtime setting. See "Open questions" below.

### Degrading gracefully

Whatever is missing, the engine reports what it resolved to and carries on:

```
Active provider       : Cpu
Using GPU             : False
Hint                  : CUDA execution provider unavailable; OCR will run on CPU. ...
```

`manualforge gpu` is the first thing to check when throughput looks wrong — a silent fall back to
CPU costs roughly an order of magnitude.

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

1. **DirectML** — add `Microsoft.ML.OnnxRuntime.DirectML`? It is Microsoft's own package, but it is
   not on the agreed list and it conflicts with the CUDA package in the same output folder.
2. **CUDA 13 runtime + cuDNN 9** — worth installing on this machine? It is the fastest path and
   needs no project change.
3. **Serilog** — file logging is currently a 150-line hand-rolled JSON-lines provider to avoid an
   unapproved dependency. Fine to keep, or would you rather have Serilog?
4. **xunit** — the test projects use xunit, the `dotnet new` default. Called out for completeness
   since it was not on the list.
5. **Baseline offset** — `TextLayerOptions.BaselineOffsetFraction` currently defaults to 0, putting
   the baseline on the bottom edge of the detected ink box. Worth tuning against ground truth in
   phase 7 rather than guessing now.
