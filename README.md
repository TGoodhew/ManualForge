# ManualForge

Searchable PDFs from scanned technical manuals, and a searchable index over them.

Built for vintage test-equipment documentation — 1950s-80s scans at 200-400 dpi, mostly CCITT G4
bitonal, where roughly a quarter of files have no text layer and many of the rest carry poor
2000s-era OCR. The figures throughout are measured on a corpus of that shape: about 576 documents
and 100,000 pages.

**Status: phase 5 - the full-text index and search.** It OCRs a PDF end to end with an invisible
text layer whose alignment is measured rather than assumed, classifies a whole library to decide
what is worth re-OCRing, rebuilds files that refuse modification, processes a library resumably
without ever overwriting a source at 104 pages/min, has a desktop application over all of it, and
indexes every page so a question returns a manual, a page and a snippet - from the command line, the
application, or Claude through MCP. A VLM sidecar and benchmark mode are phases 6-7.

## What it does

```
manualforge survey <folder>                classify a library; changes nothing
manualforge run <folder>                   classify, flatten, OCR and replace, resumably
manualforge ocr <input.pdf> [options]      rasterise → recognise → overlay → verify
manualforge inspect <input.pdf>            page count, sizes, landscape pages, existing text
manualforge index <folder>                 build the full-text index over a library
manualforge search <query> --library <f>   ask it: manual, page and snippet
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

Measured on a 100-page instrument manual including its two `/Rotate 90` pages: 749 of 749 words
within 0.000 pt, renders byte-identical, source MD5 unchanged.

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

Serilog writes them: rolled daily, kept for 30 days, opened shared so a run holding the log for
hours does not stop a second command starting, and flushed as written so a run that is killed still
ends with the last thing that happened. `--log <path>` writes to exactly that file instead, with no
rolling.

A first run, on three pages, writing nothing over the source:

```
manualforge ocr "C:\Manuals\service-manual.pdf" --out out.pdf --pages 6,29,31 --verify-ink
```

## GPU: CUDA

Measured on 20 pages at 300 dpi, on an RTX 3060 Ti against a 24-thread CPU:

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

Peak VRAM is **7,556 MiB of 8,192**, and the desktop and anything else running already hold
2.6–2.9 GB before work starts — a few hundred megabytes of headroom at batch size 8. That is why
concurrency is sized against *free* VRAM at startup rather than the card's nominal 8 GB, and why
going past it is a cliff rather than a slope. See "The pipeline" below for what happens when it is
exceeded, and what it is worth when it is not.

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

## What a real corpus looks like

These are the numbers from the corpus this was developed against, reported by
`manualforge survey`. They are here because the design decisions further down only make sense
against a distribution like this one.

```
576 files, 101,733 pages

  Class            Files     Pages  Action
  ------------------------------------------------
  ImageOnly           95     9,325  Ocr
  SuspectText          5       924  Skip
  GoodText           475    91,484  Skip
  Unreadable           1         0  Skip

  147 file(s) cannot be written to directly:
    OwnerPassword      142   cleared by flattening
    Signature            2   flattening would drop the signature
    Unknown              2   unknown cause
    Corrupt              1   needs manual attention
```

A quarter of the corpus refuses modification, so the flatten path is load-bearing rather than an
edge case.

The ImageOnly count is what is *left*: 94 of the original 238 image-only files have been processed
and now carry a text layer, which is why they count as GoodText above. Two resist and need a look by
hand — one whose single page will not parse, and one that reports zero pages.

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

### A third measurement: is there a text layer at all?

Both of those ask how *good* the text is. Neither answers whether there is any, and the two
questions have to be kept apart, because a text layer that will not decode looks exactly like no
text layer at all.

A page draws glyphs; a reader turns them into characters. On a font with a custom encoding and no
`/ToUnicode` — 1990s manuals typeset with `cdsdvips` are full of them — those two come apart
completely. One sampled page draws **3,276 glyphs** and decodes to 68 characters of `&" '(") *++,`.
Poppler reads that page perfectly. PdfPig reads punctuation soup.

So the classifier counts glyphs as well as characters, and a document that draws glyphs while
decoding almost nothing is **`UnreadableTextLayer`**, not `ImageOnly`:

| | decodes | does not decode |
|---|---|---|
| **draws glyphs** | judged on the text | `UnreadableTextLayer` — leave it alone |
| **draws none** | — | `ImageOnly` — OCR is pure gain |

It is skipped by default, because the right answer is genuinely unclear: the text may be the
original typesetting and better than any OCR of it, and other readers can see it even when we
cannot. `--policy UnreadableTextLayer=redo` strips and re-OCRs them deliberately.

Measured against 156 pre-OCR originals: 151 correctly stay `ImageOnly`, 5 are
`UnreadableTextLayer`, and no genuine scan is flagged. A stamped page number or a signature block
does not trip it; the threshold is 100 glyphs per page, and the affected files draw 355 to 3,276.

Two bugs found by measuring rather than assuming, each of which would have sent thousands of pages
of perfectly good text back through OCR:

1. Word statistics were computed from raw extracted text. Many PDFs position words rather than
   emitting space characters, so their text arrives as one unbroken run — one 300-page user guide
   yields *zero* whitespace characters — and every word-frequency measure reads as gibberish. Fixed
   by segmenting with PdfPig's `GetWords()`. That alone moved 22 files out of SuspectText.
2. Internal separators counted as noise, so `SENSe:FREQuency:STARt` scored as garbled and every
   programming manual looked like bad OCR. Colons, hyphens, underscores, dots and slashes are now
   treated as structure.

## Recognising each document once

A collection assembled over years accumulates copies: a manual filed under two model numbers, a
folder left behind by an earlier tool, the same PDF downloaded twice. The test corpus holds **560
distinct documents across 576 files** — 13 duplicate groups, 16 redundant files, 3,236 pages between
them. Recognising those twice would cost just under an hour of GPU time at 55.9 pages/min.

As it happens all 13 groups are `GoodText` and therefore skipped, so under the default policy
deduplication spares nothing on that corpus today. It earns its keep the moment a wider policy
queues any of them for work. The honest summary is that the saving is real but contingent.

Matching is by SHA-256 of the content, not by filename, because copies rarely keep the same name.
The duplicate groups found show every variety of that:

* a part number against a description of the same manual
* URL-escaping that was never undone, so `%20` where a space should be
* a suffix naming whichever machine the file was copied off
* a space that came and went between two otherwise identical names
* the same document filed once at the top level and once in a vendor subfolder

No filename rule would group those; the content hash groups all of them.

One copy becomes the **primary** and is recognised; the rest take its finished result by copy, and
each still gets its own original preserved, so the originals tree stays a complete mirror and every
path in the library opens a searchable file.

### Choosing the primary

Since the copies are identical, the choice does not affect what any file ends up containing. It
decides which path the search index will cite for the document, which is a question about what a
person will recognise at a glance. The rule is:

1. **Shallowest path.** A manual in the collection root beats the same manual staged in a working
   subfolder, and this needs no knowledge of what any particular folder is called.
2. **Most descriptive name**, approximated by how many word-like runs it contains.
3. Alphabetical, so the result never depends on the order files were walked in.

The second criterion used to be the *shortest* name, which is the obvious choice and the wrong one:
it systematically picks part numbers over descriptions, so a document ends up cited by its
publisher's stock number rather than by a name a person would recognise. The two rules disagreed on six of
the thirteen groups in the test corpus, and the descriptive name was the better answer in all six.

`--no-dedup` recognises every copy separately.

### It does not help the index yet

Deduplication runs only over the files marked for work, because hashing the whole library to spare
effort on files nobody is touching costs more to discover than it saves. That is right for
recognition and wrong for search: a duplicate that was skipped is never grouped, so phase 5 would
still return one manual and its three identical twins as four separate results. The index needs its
own pass over content hashes, independent of whether a file needed OCR.

## Flattening files that refuse modification

In the test corpus 147 files carry owner-password permissions. PDFsharp will not open them for
modification, but it *will* import their pages, so they are rebuilt into a fresh document:

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

## The pipeline

Phase 3. Measured before it was built, because the architecture in the original design is not
aimed at where the time goes.

Over the 40,000 pages of the first full run, rasterising averages **55 ms a page against 1,034 ms
of recognition**. Overlapping those two is worth 5.1%. Per-document work outside the pages - the
flatten, the save, the verification, the moves - is another 2.2%. So the whole of the original idea
buys about 7%.

The lever is that **the GPU is only 40% busy**. Sampling the card during a real run:

```
GPU utilisation  mean 40.2%  median 39%  p10 1%  p90 89%
below 50% util   60.4% of samples
at 0% util        8.3% of samples
VRAM peak        7,694 MiB of 8,192
```

Every page alternates CPU phases - decode, deskew, crop, CTC - with GPU phases, and one page in
flight leaves those gaps empty. Feeding the engine more than one page at a time fills them.

### What it buys, measured end to end

Five manuals, 378 pages, from the same starting state each time:

| | wall clock | pages/min | |
|---|---|---|---|
| Phase 2, serial | 428.8 s | 55.9 | |
| Pipeline, one page on the GPU | 316.6 s | 72.1 | **1.35x** from overlap alone |
| Pipeline, two pages on the GPU | **219.9 s** | **104.1** | **1.95x** |

Identical output at every setting: 84,489 words, worst alignment deviation 0.001 pt. Against the
~30 hours a full pass over the test corpus took, that is about fourteen hours saved.

### VRAM is a cliff, not a slope

This is why sizing against free VRAM is load-bearing rather than tidy. Under what the card holds,
more pages is faster - 54.9, 78.1 and 83.3 pages a minute at one, two and three. **Over it,
throughput does not degrade, it collapses:** the driver spills to system memory over PCIe and the
same work runs at **7.6 pages a minute**, an order of magnitude slower than serial, with no
exception raised to say why.

So concurrency is chosen from what `nvidia-smi` reports free, less half a gigabyte of headroom for
a desktop that grows, and capped at two. Three was the fastest setting measured and also the one
that tipped an 8 GB card over; 6% of upside against a factor of ten of downside belongs behind an
explicit `--gpu-concurrency 3`.

```
GPU memory : NVIDIA GeForce RTX 3060 Ti: 6,786 MiB free of 8,192
Pipeline   : 2 page(s) on the GPU at once, 2 rasteriser(s)
```

### What it deliberately does not do

The pipeline fills the page cache. It does not open a file in the library, write a PDF, verify one,
or move anything. The replace-in-place sequence that follows is exactly the one phase 2 shipped and
tested - still one document at a time, still refusing to touch the original until the replacement
is verified. **Parallelism was kept away from the code that can lose a manual.**

The overlap that remains is between recognising one document and assembling another. A document is
announced on a channel as its last page is cached, and the assembler consumes that channel, so
writing the text layer and verifying a 639-page manual - a minute and a half of CPU that used to
leave the GPU idle - now happens while the next document is being recognised.

Documents that need flattening or stripping first are excluded from the pre-pass and take the
serial path. Pre-rasterising the original would be assuming the rebuilt copy renders identically to
it; that is true as far as anything can tell, and the flatten is verified structurally, but assuming
it costs every word box on the page if it is ever wrong. It affected eleven of 156 originals in the
test corpus.

### A CPU pipeline alongside the GPU one makes it slower

Worth writing down because the idea is a good one and the answer is not the obvious one.

During a GPU pipeline run the CPU averages **26.8% of 24 threads** — about 17 threads idle — while
the GPU sits at 40%. Neither resource is saturated. So a second OCR pipeline on the CPU looks like
free throughput: it is 15.4 pages/min on its own, which would be a 15% top-up.

It is not free. Measured on 30 pages, with both engines pulling from one shared queue so the slower
one simply takes fewer pages:

| configuration | pages/min | what the GPU alone managed | split |
|---|---|---|---|
| GPU x2 alone | **92.6** | 92.6 | gpu 30 |
| \+ CPU x1, 8 threads | 78.7 | 62.9 | cpu 6, gpu 24 |
| \+ CPU x1, 4 threads | 41.4 | 28.9 | cpu 9, gpu 21 |
| \+ CPU x2, 4 threads | 37.2 | 22.3 | cpu 12, gpu 18 |
| \+ CPU x1, uncapped | 46.6 | 32.6 | cpu 9, gpu 21 |

Every mix is worse than the GPU on its own, and capping the CPU engine's threads does not rescue
it. The third column is the reason: the GPU path's own throughput collapses as soon as anything
else wants a core.

The average was misleading. PaddleOCR's GPU path is heavily CPU-bound between its GPU phases —
decode, deskew, denoise, crop, NMS, CTC — and those phases are **latency-critical**: while one runs,
the GPU is idle waiting for it. 27% average CPU is 27% of short bursts that must happen *now*. A CPU
OCR engine is a long, dense, low-priority workload that makes every one of those bursts queue, and
it extends GPU idle time by more than the pages it contributes are worth.

Put another way: a core spent feeding the GPU pipeline produces far more pages than the same core
spent doing OCR itself, so the machine is better used by giving every core to the GPU path. An idle
core is not waste when the GPU is the limit.

What came out of it: `--cpu-threads` now caps ONNX Runtime's per-operator threads. It does not help
a hybrid, but it is the difference between a CPU-only run that takes the whole machine and one you
can work alongside.

The lever this points at instead is reducing the CPU work on the critical path — `--no-deskew` and
`--no-denoise` are both CPU-side OpenCV passes that run on every page. What they cost and what they
buy is a question for benchmark mode in phase 7, measured against the BASELINE folder rather than
guessed at.

## The desktop application

Phase 4. `src/ManualForge.App` is the window; `src/ManualForge.Shell` is everything it does.

That split is the whole point of "MVVM, no code-behind logic", which otherwise means nothing you
can check. The shell is a plain library with no XAML in it, so the test project drives it directly:
a survey that groups by class and totals its pages, an action changed in the table reaching the
policy the run obeys, a failure landing in the error list, an invalidated signature reported even
though the file succeeded, cancel offered only while something is running, and the report exporting
as CSV or JSON. Twenty tests, no window, no GPU.

What is left in code-behind is a file picker, which needs the native window handle, and a
two-second timer that asks the card how it is doing. Both belong to a window rather than to a view
model.

The window shows the folder, a class summary to review before committing to anything, live per-file
and per-page state, throughput, GPU utilisation and VRAM, a running list of problems, and the
results with an export. Cancel stops at the page in flight and says so — *"recognised pages are
kept, so running again resumes from here"* — because a user who does not know that will never dare
press it.

The search tab exists and says it is phase 5, rather than pretending.

```
manualforge-app [folder]
```

A folder on the command line is optional. It is there because a window that can only be driven by a
mouse cannot be checked after a change.

### Unpackaged, and why

MSIX packaging refuses this application outright:

```
error APPX1101: Payload contains two or more files with the same destination path 'onnxruntime.dll'
  ...microsoft.ml.onnxruntime.gpu.windows\1.30.0\runtimes\win-x64\native\onnxruntime.dll
  ...microsoft.ml.onnxruntime\1.30.0\runtimes\win-x64\native\onnxruntime.dll
```

PaddleOcrNet pulls in both the CPU and the GPU builds of ONNX Runtime, and each ships its own
`onnxruntime.dll` — the same collision that kept DirectML out. So the app is unpackaged and
self-contained on the Windows App SDK, which needs no install and matches how the command line is
already run.

### Two defects the view-model tests found

Both were real, and neither would have been obvious from clicking around.

**`IProgress<T>` posts rather than runs.** When a run finished there was no guarantee every report
had been applied, so the final counts were usually right — the worst kind of right. Rebuilding the
totals from what the run returned then produced the opposite bug: duplicated rows from reports that
landed *after* the rebuild. The answer was to stop using `Progress<T>`. An `IUiDispatcher` makes the
ordering explicit — the window's FIFO queue in the app, inline in a test — and the returned list
reconciles on top of it.

**A binding referenced a converter that did not exist**, which XAML compiles quite happily. It
failed at startup with exit code `0xC000027B` and nothing else: a stowed exception, somewhere. It is
now an ordinary property where the compiler can see it, and an unhandled-exception handler writes
the next one to `crash.txt`.

## Asking Claude about the library

The index is reachable from Claude Desktop and Claude Code as a local MCP server over stdio — a
"connector" in the user interface, but MCP underneath, and local because a 275 MB index has no
business going over a network.

```
%LOCALAPPDATA%\Programs\ManualForge\ManualForge.Mcp.exe
```

Three tools:

* **`library_search`** — full text of every page, ranked. The manual, the page and a snippet.
* **`read_manual_page`** — the text of one page, optionally with its neighbours. A snippet is enough
  to choose a page and never enough to answer from, so this is the second half rather than a
  convenience.
* **`library_status`** — what is indexed and how old it is. An index built before a manual was added
  will answer "not found", and a tool that cannot tell that from a real absence is not trustworthy.

Register it with Claude Desktop in `claude_desktop_config.json`, or with Claude Code:

```
claude mcp add manualforge --scope user   --env MANUALFORGE_LIBRARY="D:\Manuals"   -- "%LOCALAPPDATA%\Programs\ManualForge\ManualForge.Mcp.exe"
```

On a Microsoft Store install of Claude Desktop the config is not in `%APPDATA%` — it is virtualised
to `%LOCALAPPDATA%\Packages\Claude_<publisher>\LocalCache\Roaming\Claude\`.

## Working alongside GPIB-MCP

[GPIB-MCP](https://github.com/TGoodhew/GPIB-MCP) is an MCP server that controls GPIB/VISA test
instruments, and it has a `manual_search` tool over the same folder of manuals this indexes.

**Both applications are standalone, and neither deprecates anything in the other.**

* ManualForge does everything described in this README with GPIB-MCP absent.
* GPIB-MCP does everything described in its README with ManualForge absent, unchanged. Its
  filename-and-model search is not being replaced or demoted — it is the better tool whenever the
  instrument model is known, and that is most of the time for a server whose job is driving
  instruments.
* When both are installed, each is better for it. That happens by **detection, not dependency**:
  GPIB-MCP notices the index and uses it to choose which manuals to open, and carries on exactly as
  before if it is not there.

Tracked as [ManualForge#1](https://github.com/TGoodhew/ManualForge/issues/1) on this side and
[GPIB-MCP#134](https://github.com/TGoodhew/GPIB-MCP/issues/134) on that one.

### Why both exist

They solve opposite halves of the same problem, and the split is not arbitrary — it falls out of
what each can afford to do.

| | `gpib-mcp` `manual_search` | `manualforge` `library_search` |
|---|---|---|
| Picks candidate files by | filename and model | page content, ranked by relevance |
| Looks at | at most 12 files | every page of every manual |
| Returns | long passages, configurable context | page number and a snippet |
| Extraction | `pdftotext`, cached per file | a prebuilt FTS5 index |
| Best when | **you know the instrument model** | **you do not** |
| Cost | seconds, bounded by the file cap | milliseconds, bounded by nothing |

GPIB-MCP narrows by filename because content search was assumed to cost minutes — which was true
before an index existed. Its own source is explicit about the limitation that buys:

> it reports "searched 12 files, no match", which reads as "your library does not have this" when
> the truth is "I never looked at the right file".

That is a real blind spot, and easy to hit. A query for *"crystal oscillator troubleshooting"*
returns nothing useful if no manual has "oscillator" in its filename, however many of them discuss
it. Conversely, when the model *is* known, GPIB-MCP is the better tool by some distance: it goes
straight to that instrument's manuals and returns enough surrounding text to read properly, where a
ranked index has to be asked for the page and then asked again for its contents.

### The shared surface is a file, not a protocol

The two servers never talk to each other. The only thing between them is an index file, which one
writes and the other may read:

```
<library>/_Originals/manualforge-index.db
```

SQLite, opened read-only, safe to read while ManualForge is writing (WAL). Its schema is a
**stable contract** — it will not change shape without a version bump and a note here:

```sql
CREATE TABLE documents (
    id            INTEGER PRIMARY KEY,
    path          TEXT NOT NULL UNIQUE,   -- absolute path to the PDF
    title         TEXT NOT NULL,          -- file name without extension
    page_count    INTEGER NOT NULL,
    content_hash  TEXT NOT NULL,          -- SHA-256, so identical copies can be folded
    indexed_utc   TEXT NOT NULL
);

CREATE VIRTUAL TABLE pages USING fts5(
    text,                   -- readable page text, hyphens at line ends rejoined
    alternates,             -- the other reading of each rejoined hyphen; indexed, never displayed
    doc_id UNINDEXED,       -- UNINDEXED so a query for a part number cannot match an id
    page_number UNINDEXED,
    tokenize = 'unicode61 remove_diacritics 2'
);
```

A consumer wanting the best files for a query needs one statement:

```sql
SELECT d.path, COUNT(*) AS hits, MIN(bm25(pages)) AS best
FROM pages p JOIN documents d ON d.id = p.doc_id
WHERE pages MATCH ?           -- see "Queries must be escaped" below
GROUP BY d.id
ORDER BY best
LIMIT 12;
```

**Queries must be escaped.** FTS5's `MATCH` takes a query language, not a phrase, and technical
documentation is full of punctuation that collides with it — `HP-IB handshake` fails outright with
`no such column: IB`. Quote each term (`"HP-IB" "handshake"`) unless the caller clearly meant the
query language. ManualForge does this in `SearchQuery.Prepare`.

### Three ways they help each other

**1. The index tells GPIB-MCP which files to open.** This is the valuable one and the reason the
contract above is documented. `ManualLibrary.Candidates()` can consult the index when it exists and
choose its twelve files by content as well as by name, using filename scoring alone when there is no
index. The cap stays, the model filter stays, the passage extraction stays, the citations stay —
only the *choice* of which files to open gets better, and only when the index is there.
[GPIB-MCP#134](https://github.com/TGoodhew/GPIB-MCP/issues/134).

**2. OCR gives GPIB-MCP text that was not there before.** A quarter of a typical scanned library has
no text layer at all, and `pdftotext` returns nothing for those files. Once ManualForge has added a
text layer, they extract like any other manual — so running ManualForge over a library improves
GPIB-MCP's existing search with no code change on either side. Note that GPIB-MCP caches extracted
text under `%LOCALAPPDATA%\GpibMcp\manual-text` keyed by path, size and modification time, so newly
OCR'd files are picked up automatically as their timestamps change.

**3. Sidecars, if you want them.** `ManualText` reads a `<name>.txt` sitting beside a PDF in
preference to running `pdftotext`. `manualforge index --sidecars <folder>` writes exactly that text.
Pointing it at the library itself would let GPIB-MCP skip extraction entirely — at the cost of one
`.txt` per manual living beside it, which is why it is opt-in rather than the default.

### Both installed: which tool gets used

Two similarly-named tools over the same folder will be chosen between badly unless the descriptions
say how. So the routing is written into them rather than left to chance:

* `library_search`'s own description tells the caller to prefer `gpib-mcp`'s `manual_search` when
  the instrument model is known, and to come here when it is not or when that found nothing.
* ManualForge's server instructions, which a client reads before any tool call, say the same thing
  at the level of the whole server.

The environment variables are deliberately parallel — `GPIB_MCP_MANUALS` and `MANUALFORGE_LIBRARY`
— because the same machine points both at the same folder, and two conventions for one idea is a
trap.

```json
{
  "mcpServers": {
    "gpib-mcp": {
      "command": "%LOCALAPPDATA%\\Programs\\GpibMcp\\GpibMcp.exe",
      "env": { "GPIB_MCP_MANUALS": "D:\\Manuals" }
    },
    "manualforge": {
      "command": "%LOCALAPPDATA%\\Programs\\ManualForge\\ManualForge.Mcp.exe",
      "env": { "MANUALFORGE_LIBRARY": "D:\\Manuals" }
    }
  }
}
```

Expand the variables to real paths; Claude Desktop does not.


## Safety

The order of operations is the guarantee:

0. Refuse outright if the pages already carry text. See below — this is the one that was missing.
1. OCR into a temporary file. Nothing in the library has been touched.
2. Verify: opens cleanly, page count matches, text layer is non-empty, word alignment measured.
3. Only then move the original into `_Originals`, mirroring the source tree.
4. Then move the new file into the original's place.

Both are moves on the same volume, so each is atomic and the original exists in exactly one place
at every instant. If step 4 fails, the log names both paths.

A digitally signed file is processed like any other, but never silently: every signature invalidated
is logged as a warning and listed in the run summary, and the untouched original is kept as always,
so it stays reversible. `--refuse-signed` skips them instead. The signatures on these manuals come
from whoever scanned or redistributed them decades ago, which is why the default is the way round it
is. `--dry-run` does everything up to step 2 and stops.

A record whose file is no longer on disk is marked rather than deleted, so a file that comes back is
recognised as the one that went away. Those records are reported on every survey and every run, and
left out of every total. `--trim-missing` forgets them, along with any recognition cached against
their paths — it is the deliberate way to clean up after a library has been reorganised.

Verified on a sandbox copy before the first real run: originals byte-identical to the library,
files marked GoodText untouched, replaced files carrying full text layers, and a second run
changing nothing at all.

### Never a second text layer

Two text layers do not merge. An extractor sorts them together by position and returns them
interleaved character by character, so `Broadband` comes back as `BBrrooaaddbbaanndd` and the
document ends up **less searchable than before it was touched**.

Three files were damaged exactly that way during development, because the classifier read a font it
could not decode as no text at all. All three were restored from their preserved originals, which is
what keeping originals is for — and the run summary would have said nothing was wrong.

Two things now prevent it. The classifier tells a text layer it cannot read apart from no text
layer, as above. And independently of any classification, the processor probes whatever it is about
to recognise and refuses the file if a sampled page draws a single glyph. That second check runs
*after* stripping, so it also catches a strip that silently left a page alone.

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
  Pipeline/RecognitionPipeline.cs Channels: rasterise and recognise ahead of assembly
  Ocr/GpuMemory.cs                free VRAM, and how many pages it will hold
  Geometry/PageGeometry.cs        image pixels ↔ PDF user space; rotation, crop origin
  Text/GlyphlessTrueTypeFont.cs   generates the blank font program
  Text/InvisibleFont.cs           Type0/CIDFontType2 objects, subsetting, ToUnicode
  Text/TextLayerWriter.cs         BT/ET, Tr 3, Tz, Tm emission
  Ocr/PaddleOcrEngine.cs          PaddleOcrNet wrapper, word boxes, provider selection
  Rendering/PageRasteriser.cs     PDFium via PDFtoImage
  Verification/                   PdfPig re-extraction, baseline deviation, ink comparison
  Pipeline/SearchablePdfBuilder.cs end-to-end for one file
  Diagnostics/RunLog.cs           Serilog: JSON lines, rolled daily, shared
  Indexing/SearchIndex.cs         SQLite FTS5 over every page; the shared contract
  Indexing/Dehyphenator.cs        rejoins words broken across line ends, for the index only
  Indexing/LibraryIndexer.cs      walks the library, extracts, indexes, writes sidecars
src/ManualForge.Cli/              the command line
src/ManualForge.Shell/            view models and the services behind them - no XAML, so testable
src/ManualForge.App/              WinUI 3: XAML, a file picker, a GPU timer, nothing else
src/ManualForge.Mcp/              MCP server: library_search, read_manual_page, library_status
tests/ManualForge.Core.Tests/     250 tests, no GPU or network needed
```

## Tests

```
dotnet test
```

250 tests, a few seconds, no models and no network required:

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
- **`RecognitionPipelineTests`** - the invariants rather than the plumbing: every page reaches the
  cache, cached pages are not redone, a document is announced only once its last page is genuinely
  there, one page at a time means one, the rasteriser cannot run ahead of a bounded queue, and
  cancelling does not leave a consumer waiting on a channel nobody will complete. Every wait is
  bounded, because a pipeline defect that hangs the suite is worse than one that fails it.
- **`GpuMemoryTests`** - the arithmetic that decides concurrency, which errs downwards on purpose.
- **`LibraryViewModelTests`** - the shell driven with no window: survey, policy edits reaching the
  run, the error list, cancel and resume, and the exported report.
- **`LibraryProcessorTests`** - the replace-in-place sequence end to end, against a fake OCR
  engine. Everything downstream of recognition is real: PDFium rasterises, PDFsharp writes, PdfPig
  reads back. Most of these assert what happened to the bytes on disk rather than what the code
  returned - that the original is kept byte for byte, that a document that fails verification
  leaves its source untouched, that an owner password is flattened first, that identical copies are
  recognised once, and that an interrupted document resumes from the recognition it already has.

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

### Throughput estimates still quote the serial rate

`status` and `survey` estimate remaining time at 55.9 pages/min, which was the measured serial rate
before the pipeline. With two pages on the GPU the real figure is 104, so those estimates are now
pessimistic by about half. They are also a library-wide average over work ordered smallest-first, so
the tail of a run is the densest material. Both want fixing together, against a fresh full-library
run rather than a five-manual sample.

## Open questions

1. ~~**DirectML**~~ — resolved: not added, see "DirectML" above.
2. ~~**CUDA 13 runtime + cuDNN 9**~~ — resolved: installed and active, 3.7x faster.
3. ~~**Serilog**~~ — resolved: adopted, replacing the hand-rolled provider. Adds `Serilog`,
   `Serilog.Extensions.Logging` and `Serilog.Sinks.File`; the JSON formatter is in Serilog itself,
   so writing JSON lines needed no formatting package on top.
4. ~~**xunit**~~ — resolved: kept.
5. **Locating CUDA without `PATH`** — the app relies on the user's `PATH` to find the CUDA and
   cuDNN DLLs, which is fragile for something launched by double-clicking. Phase 3 should locate
   them at startup and add them to the process DLL search path, reporting clearly when it cannot.
6. **Baseline offset** — `TextLayerOptions.BaselineOffsetFraction` currently defaults to 0, putting
   the baseline on the bottom edge of the detected ink box. Worth tuning against ground truth in
   phase 7 rather than guessing now.
