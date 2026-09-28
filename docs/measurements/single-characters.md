# Lone single characters: reading the ink the detector never boxed

After the orientation fix, lone single characters were still the largest loss against Acrobat on
table pages: the check-digit and quantity columns of HP parts lists. This round found why, ruled out
the detector's own settings, and added a pass that reads those characters directly.

All numbers are from `tools/measure-single-characters.cs` over the 100 captioned table pages
(`_compare/tableonly-*.pdf`) and the 100 typical pages (`_compare/typical-*.pdf`), rendered at
300 dpi. The content-token rule is written in the file: strip `.,;:()[]{}'"*`, a token counts if it
matches `^[A-Za-z0-9][A-Za-z0-9\-./]*$`, and it is a single if what is left is one letter or digit.
**Compare within this file, not with earlier ones**, which used rules that were not recorded.

## What happens to a lone character

Every one of Acrobat's 8,100 single characters on the table pages, classified by what we had at the
same place (production settings, orientation classifier off):

| | count | share |
|---|---|---|
| read, same character | 3,653 | 45% |
| nothing of ours there at all | 1,621 | 20% |
| inside a longer word of ours (`1UF`, `14`) | 1,229 | 15% |
| under a tall stack (`mmmmm`, `MONMM`) | 919 | 11% |
| a different character | 678 | 8% |

The "inside a longer word" row is mostly a scoring artefact: Acrobat split `1 UF` where we wrote
`1UF`. The loss worth chasing is the 20% with nothing there.

Page 49 shows what that is. The check-digit column of a clean parts list, every digit sharp and
legible, each alone in a ruled cell. Asked for its regions, the detector boxes 2 of 17. The two it
finds read `2` and `8` at 0.96-1.00. The quantity column's `1` gets a box 2.9 pt wide, which the
recogniser reads as `-` at 0.15.

**The detector answers weakly to a single glyph with nothing beside it on its line.** Recognition is
not the problem.

## No detector setting helps

One setting changed at a time, 100 table pages:

| setting | singles read | nothing there |
|---|---|---|
| baseline | 3,653 | 1,621 |
| `DetThreshold` 0.2 / 0.15 | 3,608 / 3,593 | 1,679 / 1,670 |
| `BoxThreshold` 0.5 / 0.4 / 0.3 | 3,683 / 3,684 / 3,685 | 1,586 / 1,573 / 1,576 |
| `UnclipRatio` 1.2 / 2.0 | 3,631 / 3,688 | 1,581 / 1,652 |
| `UseDilation` | 3,537 | 1,817 |
| `ScoreMode.Slow` | 3,686 | 1,569 |
| `MinTextHeight` 32 | 3,697 | 1,567 |
| `EnhanceContrast` | 3,610 | 1,653 |

All within 1%. Looser thresholds find fragments of glyphs - boxes 2 pt tall over part of a digit,
read as `.` or `P` - not whole glyphs. `UnclipRatio` 2.0 gains 35 singles and loses about 300 other
words. `MinTextHeight` hardly fires: table text is already above 32 px.

## Reading the orphans directly

`OrphanGlyphs` looks for ink that no recognised word covers and reads each piece from a crop of its
own, through `RecognizeRegionsAsync` (detection skipped). What qualifies, and why, each condition
added because a sample of the output, checked by eye, showed what it was letting through:

- **A connected run of ink shaped like a character** at the page's line height: 0.4-1.2 lines tall,
  at most 1.5 wide. A table's rules fail this.
- **Clear of every word by 0.4 of the larger of the line height and the word's own.** Word boxes
  come from the recogniser's timesteps and run narrower than the ink; without the gap, the first
  `C` of `CAPACITOR` was re-read on its own and `CAPACITOR-FXD C` went into the layer. That
  version added 3,068 tokens to the table book, 2,505 of them on no Acrobat token. With the gap it
  added 479.
- **Against words, not lines.** A line box spans the whole row of a table, orphan cells included.
- **On a row of text:** its middle inside a word of about its size, along the same line.
- **In a column:** at least two other candidates directly above or below. A table's orphans line up;
  strokes of a schematic do not. This and the row test cut the additions on typical pages from
  314 to 102 and raised the share read correctly from about 28 in 40 to 34 in 40.

How the crop is cut mattered more than anything else:

| crop around page 49's check digits | reads | mean confidence |
|---|---|---|
| 0.1 line, no padding | `779845898888797995699` - all right | 0.98 |
| 0.25 line + 8 px padding | one `8` as `A` | 0.80 |
| 0.5 line | the rules come in: `?|?|il:l|5|5|_||...` | 0.55 |

And a lone `1` a few pixels wide reads as `4` at 0.18-0.34. Widening narrow crops **sideways only**,
to 0.8 of a line, reads it as `1` at 1.00; padding on every side shrinks the glyph once the crop is
scaled to line height. Readings are kept at 0.9 and above, letters and digits only.

## End to end

The shipped `PaddleOcrEngine`, production settings, `RescueOrphanGlyphs` off and on:

| | tables, off | tables, **on** | typical, off | typical, **on** |
|---|---|---|---|---|
| Acrobat singles read | 3,639 | **4,065 (+426)** | 2,574 | **2,629** |
| singles with nothing there | 1,621 | **1,130 (-30%)** | 2,002 | 1,939 |
| all Acrobat tokens read | 32,194 | **32,623** | 17,415 | 17,470 |
| tokens added | | 554 | | 89 |
| ... of them on no Acrobat token | | 59 | | 26 |
| 100 pages, seconds | 157 | 166 | 177 | 216 |

Checked by eye on random samples of 40 additions: about 36 in 40 right on the table pages, 34 in 40
on typical pages. The wrong ones are thin rules and switch contacts read as `1`, a hatched bar, a
circle read as `0`. Many of the additions that land on no Acrobat token are right and Acrobat's
wrong: it reads this font's `7` as `1` and `8` as `e` or `A`, so agreement with Acrobat undercounts
single characters and the harness also reports presence.

**Cost:** a second decode of the page, a pass over its ink, and one batched recognition of the
candidates. +6% on the table book; +22% on the typical book in a single run, not investigated.

`RescueOrphanGlyphs` is on by default; `--no-orphan-rescue` restores the old behaviour. It is skipped
on a page the orientation classifier turned and was believed, whose boxes do not sit on the ink as
it stands.

## What this does not settle

- **919 singles are still under tall stacks**, a column read as one word. Splitting tall boxes at
  the blank rows of their own ink is written (`split` in the harness) but not measured.
- **1,130 singles still have nothing there.** Not diagnosed individually.
- **Schematic pin numbers** standing alone are mostly not rescued: the column test drops them with
  the drawing strokes they look like. Pin numbers that happen to line up are kept.
- **Nothing already in the library is re-read** by this change.
