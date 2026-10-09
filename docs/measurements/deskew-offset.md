# Deskew puts the text layer beside the ink

Deskew was on by default. On every page it straightens, PaddleOcrNet 2.2.0 hands back positions
3-5 pt right and 1-3 pt down of where the text really is. Search still finds the words; selecting
them highlights the space beside them. It is now off by default.

## How it was found

Chasing single characters the table harness scored as "nothing there" (#22), page 75's check digits
turned out to be read and placed correctly - and scored as missing because the harness aligns each
page by its long words, and on page 75 those sat 3.25 pt right of Acrobat's. Drawing our word boxes
over the page image settled whose layer had moved: `08503-80001`'s box began at the `5`, two
characters to the right of the ink. Acrobat's sat on it.

That was the "seven table pages 3-8 pt apart" left unexplained in `page-orientation.md`. With a
recorded token rule and per-page alignment it is 25 of the 100 captioned table pages over 1 pt, the
worst 6.5 pt.

## The probe

Eight table pages read twice, deskew on and off, everything else as shipped. Words matched by text
(four characters or more, unique on the page) and their centres compared:

| page | words move, on against off | words outside their own line |
|---|---|---|
| 75 | +3.08 pt right, +1.01 down | 0 |
| 62 | +5.17, +3.30 | 5 |
| 59 | +4.46, +1.44 | 6 |
| 83 | +4.05, +3.37 | 4 |
| 66 | +3.02, +0.96 | 0 |
| 26 | +3.59, +0.72 | 0 |
| 1, 49 (not skewed) | 0.00, 0.00 | 0 |

Lines and words move together, so it is not the word boxes alone: the whole mapping back from the
rotated, enlarged canvas lands out of place. On a page with no skew worth correcting deskew does not
fire and nothing moves. The reading with deskew off is the one on the ink.

`OcrResult` does not report the angle deskew applied, so the shift cannot be undone on this side.

## What deskew was buying

Nothing. The shipped engine, deskew on and off, over both 100-page books:

| | deskew on | **deskew off** |
|---|---|---|
| tables: Acrobat tokens read | 33,480 | **33,587** |
| tables: single characters read | 4,916 | **5,047** |
| tables: ours on no Acrobat token | 5,766 | **5,542** |
| tables: pages more than 1 pt from Acrobat | 25 (worst 6.5 pt) | **9 (worst 2.3 pt)** |
| typical pages: Acrobat tokens read | 17,483 | **17,493** |
| 100 pages, seconds (tables / typical) | 181 / 179 | **160 / 157** |

`publisher-truth-benchmark.md` found the same on born-digital pages: deskew and denoise earn nothing
on clean type and cost a fifth of the throughput. Repair already ran with both off.

`--deskew` turns it back on; `--no-deskew` is still accepted and now changes nothing.

## What this does not settle

- **Every page already in the library that deskew straightened carries a shifted text layer.** A
  quarter of the table pages here; the share across the library is not known. Re-reading them is
  the only fix.
- **Nine table pages are still more than 1 pt from Acrobat**, at most 2.3 pt. Whose layer that is
  has not been looked at.
- **Denoise is still on.** Not measured here.
- **Reported upstream** on 28 Sep 2026 as
  [FarhanLodi/PaddleOcrNet#9](https://github.com/FarhanLodi/PaddleOcrNet/issues/9), with the probe
  above as the reproduction. If a fixed release lands, re-run the probe before turning deskew back
  on: it has to earn its keep on recognition as well as stop moving the text.

## After the upstream fix (PaddleOcrNet 2.2.1, 9 Oct 2026)

2.2.1 shipped a fix for #9. Re-measured on all 100 table pages with the probe's method: words of 4+
characters unique on the page, matched by text, and the mean movement of their centres with
`--deskew` against the same build with deskew off. RTX 5070 Ti, serial `ocr`.

| | Pages moved over 1 pt | Worst | Direction |
|---|---|---|---|
| 2.2.0 | 22 | 6.58 pt | right and down, e.g. p62 5.63 right 3.40 down, p75 2.99 right 1.41 down |
| 2.2.1 | 3 (p21, p44, p92) | 1.53 pt | down only, 1.26-1.53 pt, under 0.15 pt sideways |

The 2.2.0 row matches the table above, so the method holds. The sideways shift is gone; a smaller
downward one remains on three pages and has not been explained. Deskew stays off: the fix only
removes the reason it was harmful, not the finding that it reads nothing more. Turning it on would need
the Acrobat-token comparison re-run first. Serial on these pages, deskew cost 166.6 s against
162.2 s off.

With deskew off, 2.2.1 reads exactly what 2.2.0 did: on the typical and table books, 86,105
words each, the same words on every page, and not one box moved by more than 0.01 pt.
