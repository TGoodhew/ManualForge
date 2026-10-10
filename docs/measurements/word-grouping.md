# Words broken into letters, and why (#47)

Found while measuring #16: 9,935 of the library's 112,757 indexed pages held runs of five or more
one-letter "words". In the worst born-digital manuals whole paragraphs were indexed like this:

```
A g ile nt Te chn o log i e s m a k e s no wa rr a nty of a n y k i nd
```

`pdftotext` reads the same page cleanly, as "Agilent Technologies makes no warranty of any kind". A
search for those words could not find the page.

10 Oct 2026, PdfPig 0.1.16.

## The cause

Not the gaps between letters. On E4418-90066 page 5 they run from -0.34 to +0.24 pt, the ordinary
tracking of a FrameMaker page, and every space is a real space character. The cause is the glyph
boxes. PdfPig cannot read heights from this embedded New Century Schoolbook, so every letter's box
is flat: top and bottom are both 488.406. PdfPig's default word grouping scales its gap tolerance by
the glyph box, so a flat box leaves no tolerance at all. Every gap above nothing, 0.02 pt included,
became a word break, and every overlap did not. That explains the random-looking cuts, and why
whole manuals were affected while others set the same way were not: it depends on the font.

## The fix

`PageWords` groups letters on space characters and on gaps wider than 0.15 of the type size,
roughly as `pdftotext` does. The type size is known even when the glyph box is not. The gap is
measured along the letter's own baseline, so a table set sideways reads the same as an upright one.
The first version measured horizontal gaps only and scored 0% on a rotated hex table that
nearest-neighbour grouping read perfectly.

It replaces `GetWords()` in the indexer, the classifier, the doctor's reading-order explainer and the
baseline probe.

## Against pdftotext

`tools/measure-word-grouping.cs`, on 150 pages drawn at random (seed 47) from those with
letter-spaced runs and 150 from those without:

| Sample | Grouping | Recall | Precision | ms/page |
|---|---|---|---|---|
| affected | PdfPig default | 70.1% | 94.5% | 2.4 |
| affected | nearest neighbour | 99.1% | 98.3% | 5.1 |
| affected | **PageWords** | **99.9%** | **99.6%** | 0.3 |
| ordinary | PdfPig default | 99.4% | 99.4% | 0.6 |
| ordinary | nearest neighbour | 98.8% | 99.1% | 1.5 |
| ordinary | **PageWords** | **99.7%** | **99.8%** | 0.2 |

A gap share of 0.10 and of 0.20 both read within half a per cent of 0.15, which was best on both
samples. Nearest-neighbour grouping fixes the affected pages too, but is worse than today on ordinary
ones and twice the cost.

## Across the library

Re-indexed into a scratch file with the fix:

| | Pages with letter-spaced runs |
|---|---|
| Before | 9,935 (433 documents) |
| After | **5,702** (386 documents) |

The worst born-digital cases are gone from the list: VEE6 Advanced Topics (632 pages), E4418-90066
(467), the E4406A Programmer's Guide, the TDS784D manuals, the 53131A Service Guide and E4436B User.
What remains is not word grouping:

* **Scans whose own OCR layer is letter-spaced.** 3585B, 8901A, 8656A, 5343A and the 8902 manuals:
  their text layers put real gaps between letters, and `pdftotext` reads them the same way.
* **Headings set with wide letter-spacing.** `C A U T I O N`, `G E T T I N G  S T A R T E D`,
  `T R A C E`: typographic tracking far wider than a fifth of an em.

Both could be addressed by joining runs of single letters that sit at an even pitch. That is a
different change, with its own risk of gluing scan noise (`W T I C U E`) into words, and is left for
later.

## Search, before and after

The same queries against today's index and the scratch one:

| | Today | With the fix |
|---|---|---|
| Ground truth (33): found / first 25 / first ten | 33 / 33 / 32 | 33 / 33 / 32 |
| Ordinary pages (40): first ten / found | 36 / 38 | **38 / 40** |
| Bare terms (60): first ten / found | 12 / 25 | **13 / 26** |
| Command pages (60): first / first ten / found | 21 / 32 / 44 | **23 / 37 / 48** |

The phrases are drawn from the new text, which can only favour the new index. Row by row:

* **Ordinary pages:** 7 rose and 2 slipped one place (1 to 2, 4 to 5).
* **Bare terms:** 3 rose (one from not found to first) and 12 slipped, almost all by a few places
  far down the list (121 to 123, 141 to 149). That is the expected cost of mending the words: more
  pages now really contain each one.

## The classifier

On the classifier's own sample of pages, 631 of 632 documents keep their class. The one that moves,
the HP 83592A replaceable-parts list, goes from GoodText to ProbablyGood (plausible tokens 90% to
71%). That is the honest figure. The classifier ignores one-letter tokens, so the old grouping,
by chopping the scan's junk OCR layer into single letters, hid most of it: page 3 measured 29 tokens,
all plausible, where there are 448. Both classes are skipped, so nothing is done differently to the
file.
