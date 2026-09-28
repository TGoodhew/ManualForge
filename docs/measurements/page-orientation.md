# The page-orientation classifier, and why its verdict is now checked

Before detecting anything, PaddleOcrNet runs a whole-page classifier (PP-LCNet doc-ori) and turns
the page by whatever it says: 0, 90, 180 or 270 degrees. ManualForge never set
`UseDocOrientation`, so the package default - on - applied to every page this library has had
recognised. Nothing reported what it decided.

On these scans it is wrong about a third of the time.

## How it was found

The next step planned for #22 was the table-structure models, `PaddleModelSet.TableSlaNeXt`. The
package's own documentation rules that out before any code is written: `ITableRecognizer.Recognize`
takes the table crop **and OCR lines already recognised**, and distributes them into predicted
cells. It recovers structure over the text we already have. It cannot find a word we missed.

So the question became where the missing words are. Both text layers of the 100 captioned table
pages - Acrobat's and ours - were dumped with word positions, and every content-bearing token
Acrobat found was classified by what ManualForge had at the same place: the same text (found),
different text (misread - a recognition loss), or nothing at all (missing - a detection loss).

| Acrobat's tokens | found | misread | missing |
|---|---|---|---|
| words, 3+ letters | 80.4% | 16.0% | 3.5% |
| values | 83.7% | 13.7% | 2.6% |
| designators | 74.0% | 16.7% | 9.3% |
| **single characters** | 44.8% | 3.8% | **51.4%** |

Many of the misreads are Acrobat's errors and our correct readings - `IUF` against `1UF`, `3900fl`
against `3900Ω`. The detection loss is not spread evenly: lone single characters, the check-digit
and quantity columns of HP parts lists, are 74% of everything missing, and the next largest piece
was one page.

That page, 60, is a clean, sharp 8116A parts list, and ManualForge read 273 of its 819 words. Its
recognition came back as **two lines**, each a box 2,900 pixels tall. Asked for ungrouped boxes it
returned nothing at all from the left 1,500 pixels of the page - yet the left half, cropped and
read on its own, came back perfectly. Something looked at the whole page and got it wrong. With
`UseDocOrientation` off, the whole page is detected.

## The classifier's verdicts

Logged over both 100-page books, every flagged page then looked at by eye:

| | pages called rotated | actually rotated |
|---|---|---|
| captioned table pages | **26 of 100** (21 at 90, 4 at 180, 1 at 270) | 0 |
| typical pages | **39 of 100** (20 at 180) | 2, both fold-out drawings |

A colour render makes no difference - the same 26 fire - so the greyscale rasteriser is not the
cause.

A page turned 90 degrees before detection presents every row of text as a column. What the detector
can still find are characters that happen to stack vertically in the original: the `G` of five
consecutive `PRL-G` rows, a column of `5`s. Page 60's garbage was exactly that - `55555`, `GGGGG`,
`SSSSS`. The recogniser turns tall crops upright and the text-line classifier flips upside-down
ones, which is why most misfired pages still read reasonably and why the damage went unseen. On
page 60 and page 81 that rescue failed.

## But it is right when it matters

Three clean pages turned deliberately and read both ways, as a share of the words read upright:

| | 0 | 90 | 180 |
|---|---|---|---|
| classifier on | 92-100% | **99%** | **99%** |
| classifier off | 100% | 82-85% | 57-74% |

The library has sideways fold-outs that `/Rotate` does not correct. Switching the classifier off
would cost them up to 43% of their text.

## The fix: check it when it fires

When the classifier claims a rotation, the page is read again as it stands, and whichever reading
has more letters and digits in words recognised at 0.8 or above is kept. A wrong turn produces
text that is long but unsure, so length alone would favour it.

On the nine deliberately rotated cases it kept the classifier's reading nine times. On the table
book it overruled the classifier on 24 of the 26 pages it fired on; the other two were 180-degree
calls, where turning back line by line leaves the two readings within a few characters of each
other.

| missing Acrobat tokens | classifier trusted | classifier off | **checked** |
|---|---|---|---|
| table pages | 3,878 | 3,065 | **3,074** |
| typical pages | 6,266 | 4,892 | **4,962** |

Checking recovers everything switching it off does, to within 0.3%, and keeps the fold-outs.

End to end, the real `ocr` command over the table book, against what it produced before:

| | before | after |
|---|---|---|
| Acrobat tokens missing | 3,801 | **3,059 (-19.5%)** |
| single characters found | 44.8% | **51.0%** |
| designators found | 74.0% | **77.0%** |
| page 60, words | 273 | **491** |
| page 81, words | 425 | **730** |
| 100 pages, including model load | - | 3 min 13 s |

The text layer verified to 0.000 pt on every page.

**Cost:** a second recognition pass on the pages the classifier fires on - a quarter to two fifths
of them. In the sweep harness throughput fell from 47 to 39 pages a minute.

`VerifyPageOrientation` is on by default; `--trust-orientation` restores the old behaviour.

## What this does not settle

- **The numbers here are not comparable with the earlier rounds.** The regex behind "content-bearing
  tokens" in `real-tables-against-acrobat.md` was never written down, and the one used here counts
  32,103 for Acrobat where that round counted 28,418. Compare the columns within this file, not
  across files.
- **Single characters are still the largest loss.** Even with orientation fixed, 45.6% of Acrobat's
  lone characters are missing. Part of that is the same vertical stacking with no rotation
  involved: a column of `A2` prefixes or check digits detected as one tall box and read as `AAAAA`.
  Page 60 has 55 such boxes out of 450, page 75 has 15, page 13 none. The rest is not diagnosed.
- **Seven pages sit 3-8 pt apart** between our text layer and Acrobat's, a consistent translation
  on each. Whose layer moved is not known. Orientation does not change it.
- **Every page already in the library was recognised with the classifier trusted.** Nothing is
  re-read by this change.
