# The library read again with TensorRT, and audited again (#46)

On the night of 10 to 11 October 2026, the whole library went through the current engine once:
`tools/reread-library.ps1 -Reaudit`, program `0.1.0+8548d0b`, on an RTX 5070 Ti with TensorRT
10.16 for the classifier and recogniser and CUDA for the detector (#53). Three changes reached the
library that night:

- **TensorRT (#53).** About twice as fast as CUDA. It reads about 1% of words differently from CUDA.
- **The table-rule fix (#50).** Columns of repeated entries are cut into rows past a table's vertical rule.
- **Reverse video in the audit (#12).** The audit now finds white lettering on black.

The "before" for every comparison is the copy of the three databases the script took first:
`ManualForge-baselines\2026-10-10-2245-before-reread\`.

## The night

Started 22:45, finished 07:13: **8 h 28 min**, against an estimate of 8.7 h.

| Step | Took | What it did |
|---|---|---|
| backup | 1 s | state, audit and index databases copied aside |
| `run --redo-completed` | 2 h 15 min | 169 of 170 files read again from their originals, 22,087 pages, **163.9 pages/min**. 11 flattened and 6 signatures invalidated, the same as #25; the originals are kept. One failed, see below |
| `doctor --recheck` | 1 h 10 min | 632 documents, 113,623 pages, every file audited again |
| `repair --redo-before <start> --include-scans` | 5 h 00 min | all 20,570 flagged pages, 1.75 M words, **69 pages/min** (estimate 7.1 h) |
| `index` | 3 min | 632 of 632 documents |

The repair ran faster than its table predicts because the table was measured on CUDA. The rates
TensorRT gave by page kind are kept in #56: drawn pages at 300 dpi went from 89.4 to 123.6 pages/min,
and scanned pages at 300 dpi from 43.9 to 64.3.

## What reviewing it found

**1. The damaged 8340B manual failed again, because its record pointed at the damaged copy.**
#25 found `08340-90020-serv-v2-4.pdf` with three flipped bits in one image's dictionary (`/XObjUct`,
`struam`). A patched copy was read into the library, and the job record pointed at the file as
received, which PDFsharp refuses. So `--redo-completed` failed on it:
`Cannot be opened: Token 'struam' was not expected.`

The library copy was untouched and still indexed, but the record was left `Failed`, so every later
`run` would have tried it again. The damage is now moved out of the way:

- `_Originals\broken\HP8340B\08340-90020-serv-v2-4.pdf` is the file as received,
  SHA-256 `1a0767…287e5f`. `read-library-from-scratch.ps1` never restores from `broken`.
- `_Originals\HP8340B\08340-90020-serv-v2-4.pdf` is the patched copy, `c9a5c4…2c7c`. It's the same
  bytes #25 read, and it differs from the file as received only at bytes 990,611 and 990,619.

The file was then read again on 11 Oct (992 pages, 142.5 pages/min at one page in flight), audited,
and its 181 flagged pages repaired.

**2. The failed file had already been recognised, from the damaged copy.** The pipeline renders
with PDFium, which tolerates the damage, so it recognised all 992 pages before PDFsharp refused to
open the file to write them. The failure path doesn't clear the page cache. The next attempt
reused every page, including page 31, whose image PDFium couldn't decode, so that page came out
empty. The cache was cleared and the file read once more from the patched copy. Page 31 holds 41
characters again; its scan is smeared, as #25 found, so that is all it has. Two smaller things
showed up along the way:

- the `Cannot be opened` failure reached the console but not the JSON log;
- a file PDFsharp will refuse is recognised in full before it is refused, which wastes GPU time.

**3. One marginal spill held the card at one page in flight for good** (#56). The run started at
two pages in flight, the first guess for a card with no profile. At file 105 the spill guard saw
132 MiB go out to system RAM, 4 MiB over its 128 MiB margin, and recorded two as unsafe. Every
later run on this card starts at one. #56 has the evidence and keeps the measured repair rates.

## Before and after

| | Under-extracted pages | in documents | Repaired | Search text | Recovered by repair |
|---|---|---|---|---|---|
| Before (backup) | 19,712 | 575 | 19,712 pages, 1.72 M words | 190,542,887 | 7,440,790 |
| After | **20,569** | 580 | 20,576 pages, 1.75 M words | **190,678,716** | 7,581,375 |

**The audit found 864 more pages and stopped flagging 7.** Reverse video was measured beforehand at
+846, so it accounts for nearly all of the gain. The drawn pages rose from 4,072 to 4,391, which is
where screenshots fall. The 7 pages no longer flagged are re-read scans whose new text layer covers
what the old one missed.

**The search text grew by 136k characters (+0.07%)** across 248 documents, and 272 lost a little.
The re-read hardly moved the scanned text. The largest loss is 793 characters in a 708-page manual
(8663 Service), and the 639-page 8340 service manuals lost about 360 each. The gains are
reverse-video screenshots: SpecAn Basics +32,053, DSA800 User Guide +9,602, LeCroy 5674 User
+8,374, SpecAn Fundamentals +8,372.

| Harness | Before | After |
|---|---|---|
| Ground truth, 33 strings ([before](ground-truth-before-reread.md), [after](ground-truth-after-reread.md)) | 33 found, 33 in 25, 32 in 10 | identical, rank for rank |
| Ordinary pages, 40 phrases ([before](ordinary-pages-before-reread.md), [after](ordinary-pages-after-reread.md)) | 40 found, 38 in 10, 26 first | identical |
| Command pages, 60 commands ([before](command-pages-before-reread.md), [after](command-pages-after-reread.md)) | 49 found, 38 in 10, 24 first | 49 found, **39** in 10, 24 first |
| Recovered text, 25 pages ([report](recovered-text-after-reread.md)) | | 21 already findable, **2 newly findable**, the same 2 as #25 not findable before or after |

The ordinary-page and command-page phrases come from the new text, which can only favour the new
index. Only two pages moved, one place each: `:SYSTem:LOCal` in the Keithley 2015 manual went from
11 to 10, and `FREQuency:RATio` in the 53131A went from 42 to 43. The two newly findable recovered
pages are 5440A-SM page 43 and 8340B User page 6. Both moved from not found to first.

## Left over

- `_Originals\_superseded` holds the 170 copies this pass replaced (1.5 GB), for Tony to delete.
- The index file is 436.7 MB with 113 MB of free pages. `manualforge index <library> --compact`
  rewrites it without them.
- #56: the GPU profile, and the TensorRT repair rates.
- The two smaller things found under item 2. Neither has an issue yet.
