# ManualForge

Searchable PDFs from scanned technical manuals, and a searchable index over them.

**Status: phase 2 - classifier, flatten, safety and resume, running on CUDA.** It OCRs a PDF
end to end with an invisible text layer whose alignment is measured rather than assumed,
classifies a whole library to decide what is worth re-OCRing, rebuilds files that refuse
modification, and processes a library resumably without ever overwriting a source. The parallel
pipeline, WinUI shell, FTS5 index, VLM sidecar and benchmark mode are phases 3-7 and are not
built yet.

## What phase 1 does

```
manualforge survey <folder>                classify a library; changes nothing
manualforge run <folder>                   classify, flatten, OCR and replace, resumably
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

## The library: what is actually in it

Measured over the real corpus with `manualforge survey`:

```
725 files, 119,803 pages

  Class            Files     Pages  Action
  ------------------------------------------------
  ImageOnly          238    27,859  Ocr
  SuspectText          3       220  Skip
  GoodText           483    91,724  Skip
  Unreadable           1         0  Skip

  152 file(s) cannot be written to directly:
    OwnerPassword      147   cleared by flattening
    Signature            2   flattening would drop the signature
    Unknown              2   unknown cause
    Corrupt              1   needs manual attention
```

A fifth of the library refuses modification, so the flatten path is load-bearing rather than an
edge case.

## Deciding what to re-OCR

Two measurements drive the classifier, and keeping them apart is the whole point:

| Metric | Detects | Does **not** detect |
|---|---|---|
| Plausible-token ratio | garbled OCR | whether the text is English |
| Common-word share | English prose | whether the text is any good |

A parts cross-reference, a SCPI command list and a multilingual manual all score near zero on
common-word share with flawless text. So **a low common-word share never condemns a file** — it can
only promote a middling token score. Both numbers, and the reasoning behind the verdict, are
reported per file.

Two bugs found by measuring rather than assuming, each of which would have sent thousands of pages
of perfectly good text back through OCR:

1. Word statistics were computed from raw extracted text. Many PDFs position words rather than
   emitting space characters, so their text arrives as one unbroken run — `DPO3034User.pdf` yields
   *zero* whitespace characters — and every word-frequency measure reads as gibberish. Fixed by
   segmenting with PdfPig's `GetWords()`. That alone moved 22 files out of SuspectText.
2. Internal separators counted as noise, so `SENSe:FREQuency:STARt` scored as garbled and every
   programming manual looked like bad OCR. Colons, hyphens, underscores, dots and slashes are now
   treated as structure.

## Flattening files that refuse modification

147 files carry owner-password permissions. PDFsharp will not open them for modification, but it
*will* import their pages, so they are rebuilt into a fresh document:

```
8350A-OSM.pdf   Modify  FAIL  owner password required
                Import  OK    pages=384
                FLATTEN OK    58.0 MB -> 57.9 MB   modifiable=yes
```

This is a structural rebuild, not a re-render. Page content streams and image XObjects carry across
untouched, so a forty-year-old CCITT G4 scan arrives as the same compressed bytes — the size barely
moving is the tell. Rasterising and rebuilding, as Ghostscript-based approaches do, would re-encode
every page and lose detail that cannot be recovered.

Every flatten is verified before it is used: page count, per-page geometry and rotation, and the
pixel dimensions and compression filter of every image on every page must match the source.

## Safety

The order of operations is the guarantee:

1. OCR into a temporary file. Nothing in the library has been touched.
2. Verify: opens cleanly, page count matches, text layer is non-empty, word alignment measured.
3. Only then move the original into `_Originals`, mirroring the source tree.
4. Then move the new file into the original's place.

Both are moves on the same volume, so each is atomic and the original exists in exactly one place
at every instant. If step 4 fails, the log names both paths.

A digitally signed file is refused rather than silently invalidated (`--allow-signed` overrides).
`--dry-run` does everything up to step 2 and stops.

Verified on a sandbox copy before the first real run: originals byte-identical to the library,
files marked GoodText untouched, replaced files carrying full text layers, and a second run
changing nothing at all.

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
  Classification/                 text-quality metrics, classifier, per-class policy
  Pdf/PdfCapabilities.cs          what blocks modification, detected up front
  Pdf/PdfFlattener.cs             lossless rebuild plus before/after verification
  Pdf/TextLayerStripper.cs        removes an existing text layer for strip-and-redo
  State/JobStore.cs               SQLite per-file and per-page progress
  Pipeline/LibraryProcessor.cs    classify, flatten, OCR, verify, replace
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

122 tests, about 0.9 s, no models and no network required:

- **`PageGeometryTests`** — the corner mapping for all four rotations, non-zero crop origins,
  text-matrix direction, points-per-pixel, rotation normalisation.
- **`GlyphlessFontTests`** — the generated font is a well-formed TrueType: table directory sorted,
  checksum formula satisfied, four-byte alignment, uniform half-em advance, declared glyph boxes.
- **`TextLayerRoundTripTests`** — the real one. Writes a layer onto a synthetic page with PDFsharp
  and reads it back with PdfPig, for all four rotations crossed with zero and non-zero crop
  origins, asserting every word lands within 0.02 pt (four-decimal content-stream rounding).
  Also covers non-Latin-1 characters, `Tz` width, confidence filtering, and the writer surviving a
  page that leaves an unbalanced `q` behind.
- **`ClassifierTests`** - the false-positive cases this corpus is full of: parts
  cross-references, SCPI mnemonics, multilingual manuals, plus regressions for both bugs above.
- **`FlattenTests`** - an owner-password document is detected, flattened, and comes out
  modifiable with page count, geometry, rotation and every image stream unchanged. Stripping
  removes text and leaves images alone.
- **`JobStoreTests`** - resume, idempotency, and a changed source resetting its own progress.

## Resume

Per-file and per-page state lives in SQLite at `<root>/_Originals/manualforge.db`.

* **Resumable, page by page.** An interrupted document costs the page in flight, not the document.
* **Idempotent.** Re-running over a finished folder does nothing:
  `Nothing outstanding. Every file is already finished or deliberately skipped.`
* **Change-aware.** A source file that changes is detected by size, timestamp and a hash of its
  first and last 256 KB, and its recorded state is discarded.

### It caches recognition, not a half-built PDF

The obvious way to resume mid-document is to teach the writer to append a text layer to a
partly-finished file, working out which pages already have one. That is the hard way, and it is not
what happens here.

The two halves of the work cost wildly different amounts. Recognising a 639-page manual takes about
thirteen minutes on the GPU; assembling the PDF from results already in hand takes seconds. So
recognition is what gets persisted, page by page as it completes, and the document is rebuilt from
scratch on every attempt. The writer needs to know nothing about resuming.

Measured on a 78-page manual, interrupted at 75 seconds:

```
Page  1/78: ocr    0ms (reused from an earlier attempt)
...
Page 58/78: ocr    0ms (reused from an earlier attempt)
Page 59/78: ocr 2705ms
...
Resumed 58 of 78 pages from an earlier attempt
```

A full run of that manual takes about 100 seconds; the resumed run took **30**, and produced the
same 25,853 words at the same 0.001 pt worst deviation.

### Cached results are scoped to the settings that produced them

Word boxes are in image pixels at a particular resolution. Reusing boxes recognised at 300 dpi for a
run at 600 would put every word in the wrong place, and nothing downstream could detect it — the
text would extract cleanly and land in the wrong spot on every page. Entries are therefore keyed by
a fingerprint of resolution, colour mode, execution provider and confidence floor, and a run under
different settings simply finds nothing cached.

They are keyed on the document's path in the library, not on the file being read. A flattened or
stripped document is processed from a temporary copy whose name changes on every attempt; keying on
that silently disabled resume entirely, which is how the first version of this shipped and how the
end-to-end test caught it. The cache for a document is released once it completes, so it holds the
documents in flight rather than the library.

## Known gaps

### Throughput estimates use a library-wide average

`status` and `survey` estimate remaining time at the measured 55.9 pages/min. Work is ordered
smallest-first, so the tail of every run is the densest material — the HP 8340B service manuals
sustain about 51 pages/min — and the estimate is optimistic by roughly 10% by the end of a run. Good
enough for planning, wrong enough to mention.

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
