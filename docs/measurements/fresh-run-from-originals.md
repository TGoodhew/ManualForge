# The library read again from its originals (#25)

On the night of 9 to 10 October 2026 every original was put back, `_Originals` and `BASELINE` were
deleted, and the whole library was read again from scratch with one build:
`tools/read-library-from-scratch.ps1`, program `0.1.0+03a8777`, policy
`ImageOnly=ocr,UnreadableTextLayer=redo,SuspectText=redo`, on an RTX 5070 Ti. Reviewing it found
two bugs and one damaged file. All three were fixed that morning (`0.1.0+955fa65`), and the files
they affected were read again.

The "before" for every comparison is the index in the snapshot the script took first:
`ManualForge-baselines\2026-10-09-before-fresh-run\_Originals\manualforge-index.db`.

## The night

Started 20:34, finished 07:14: **10 h 40 min** against an estimate of 15 h.

| Step | Took | What it did |
|---|---|---|
| snapshot, restore, verify, clean, remove | 7 min | 170 originals put back and checked byte for byte; no output left behind; 632 PDFs before and after |
| survey | 1 min | 166 ImageOnly (22,907 pp), 5 UnreadableTextLayer, 3 SuspectText, 458 GoodText |
| run | 2 h 32 min | 172 of 172 files, none failed, 23,045 pages, **151.7 pages/min**; 11 flattened, 6 signatures invalidated (originals kept) |
| doctor | 1 h 09 min | 632 documents, 113,623 pages; 19,529 pages flagged in 575 documents |
| repair | 6 h 47 min | all 19,529 pages, 1.72 M words, 48 pages/min (estimate 8.6 h) |
| index | 3 min | 631 of 632 documents. One failed, see below |

**The estimates were well off, and have been re-taken from this run.** `PagesPerMinuteOnGpu` was
87.3, measured on 100 dense benchmark pages. Across the whole library the run averaged 151.7, which
is now the figure; later re-reads ran at 141 to 247. Repair ran at 48 pages/min. Re-measured by page
kind and resolution, the repair table (`RepairThroughput`) is 5 to 25 per cent faster than the RTX
3060 Ti's, and estimates this run at 406 minutes against the 519 the old table gave. It took 407.

## What reviewing it found

**1. One bad page dropped a whole document from the index** (#39). Page 31 of
`08340-90020-serv-v2-4.pdf` draws an image its resources don't hold. `LibraryIndexer.ExtractPages`
was meant to cost only that page. But PdfPig builds each page inside `GetPages()`'s enumerator, which
was outside the try, so the exception escaped and all 992 pages were left out. Pages are now fetched
by number inside the try.

**2. `redo` did almost nothing for unreadable printed text** (#40). Of the 8 files the policy sent to
strip-and-redo, 6 carry their text in fonts that number their glyphs their own way: `oven.pdf`
extracts `*($SSOLDQFHV` for "GE Appliances", and the 3458A guide `5IFQSJOUJOH` for "The printing".
That text is the ink on the page, so since #24 the strip keeps it, and the recognised words that
landed on it were left out. Those fonts now get a `/ToUnicode` that sends every code to a space. The
pages render the same: byte for byte on every page compared, the first three and every seventh of
nine files. The garbage stops extracting, and the OCR layer covers the page. Each such file is renamed `name_repaired.pdf`, and its original keeps the old name in
`_Originals`.

| File | Pages | Words written, 9 Oct | Words written, 10 Oct |
|---|---|---|---|
| 3458A Quick reference guide | 54 | 7 | 6,343 |
| 8714 IBASIC | 152 | 648 | 36,828 |
| 8714 Service Guide | 182 | 1,083 | 44,998 |
| 8757D Operating | 78 | 7,008 | 14,229 |
| oven | 88 | 1,142 | 40,980 |
| 83620A User | 522 | 8,136 | 100,315 |

The other two strip-and-redo files, `461A-462A-OSM` and the 1966 HP 419A, carry readable stamps over
a scan. Once their hidden layer is gone they classify as image-only, so their stamps are kept and
their names are unchanged. `HP419Mod.pdf`, whose only text is a readable printed footer, had been
called an unreadable layer because a page holds so little text; it now counts as image-only.

**3. One manual was damaged, and had been since 2017.** `08340-90020-serv-v2-4.pdf` (HP 8340B
service manual, volume 2, 992 pages) has three flipped bits in the dictionary of one image object:
`/XObjUct` at byte 990,611 (0x55 for 0x65) and `struam` at byte 990,619 (0x75 for 0x65). All three
copies on the machine are byte-identical and dated 30 Jan 2017, so it arrived this way. PDFsharp
refuses the whole file, so the run classified it as unopenable and skipped it. The library had held
an OCR'd copy from earlier, but restoring the originals replaced it, leaving 991 pages unsearchable.

With Tony's agreement the two bytes were put back in a copy, and that copy was read as
`08340-90020-serv-v2-4_repaired.pdf`: 301,887 words, plus 20,071 characters the repair recovered
from 181 pages. The file as received is in `_Originals\HP8340B\08340-90020-serv-v2-4.pdf`, and
the job record points there. Page 31's image data is damaged too (fax-coded rows smeared across the
page), so that page can't be recovered.

## Before and after

| | Documents | Pages | Search text | Recovered by repair |
|---|---|---|---|---|
| Before (snapshot) | 632 | 113,623 | 192,277,021 | 7,461,350 |
| After | 632 | 113,623 | **193,276,696** | 7,440,790 |

260 documents gained text, 291 lost some, 74 are unchanged. **The losses are duplicates going
away.** The biggest, HP 8672A (-28,655 characters), 8663 User-Calibration (-6,736) and 438A
(-1,963), carried two interleaved readings in their old copies: `SECTION I SECTION I`, `CONTENTS
CONTENTS`, `Errata Errata Title & Document Type: Title & Document Type:`. The fresh copies carry one.

| Harness | Before | After |
|---|---|---|
| Ground truth, 33 strings ([before](ground-truth-before-fresh-run.md), [after](ground-truth-after-fresh-run.md)) | 33 found, 31 in 25, 27 in 10 | the same; one rank 91 → 92 |
| Ordinary pages, 40 phrases ([before](ordinary-pages-before-fresh-run.md), [after](ordinary-pages-after-fresh-run.md)) | 39 found, 37 in 10, 25 first | 40 found, 38 in 10, 26 first |
| Recovered text, 25 pages ([report](recovered-text-after-fresh-run.md)) | | 22 already findable, 1 newly findable, 2 not findable before or after |

The ordinary-pages phrases are drawn from the new text, which can only favour the new index. Two
pages moved: HP 3580A page 112, a manual that was read again, from outside 25 to first, and an 8657B
page from 21 to 22. The ground-truth manual (54845A) was not read again, so those 33 strings show
only that the rest of the library did not crowd it out.

## Stamps and headers

#24 found that a re-read could take printed text off a page. Every file whose original is kept under
the same name was checked page by page: the visible text (anything not drawn in render mode 3 or 7)
in the library copy against the original. The 7 renamed files were left out, because their printed
text is silenced on purpose.

| | |
|---|---|
| Files compared | 166 |
| With printed text in the original | 56, 57,551 characters |
| Pages whose visible text differs | **0** |

## Left over

- The doctor database keeps rows for the six old names; repair skips them because the files are
  gone. Harmless, but `doctor`'s standing totals count them.
- After a run that renames files, the old names stay in the index until
  `manualforge reconcile <library> --trim-missing` is run.
- The estimates above.
