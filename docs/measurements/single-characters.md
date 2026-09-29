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

## Cutting stacks back into rows

919 of Acrobat's singles sat under a **stack**: a column of short aligned entries - designator
prefixes, check digits - boxed by the detector as one tall region and read on its side as
`NNNNN`, `mmmmm`, `MONMM`. Cut at the blank rows of its own ink and read row by row, it gives the
rows back. In the harness, cutting the detector's regions before recognition read 1,780 more of
Acrobat's tokens than the same path uncut.

In the engine it took three attempts:

| | tables: singles read | tables: all tokens read | typical: all tokens read | typical: ours on no Acrobat token |
|---|---|---|---|---|
| rescue only | 4,065 | 32,623 | 17,470 | 11,107 |
| cut the engine's own tall words | 4,377 | 32,939 | 17,477 | 11,125 |
| cut the detector's tall regions | 4,920 | 33,484 | 17,494 | 11,232 |
| ... where rows stand apart (0.3 line) and sit on text rows | 4,640 | 33,200 | 17,472 | 11,106 |
| **... gap 0.15 line, at most 2.5 lines wide, replaced only if read** | **4,916** | **33,480** | **17,482** | **11,108** |

- **The engine's own words are the wrong unit.** Once a stack has been read it is several words, each
  claiming part of the column - `R5` and `A2` with boxes 20-35 pt tall - and only 71 of them could be
  cut. On a page that shows a tall, narrow word, the detector is asked for its regions again, and
  the words whose middles fall inside a cut region are replaced by its rows.
- **Words set sideways look the same.** Unguarded, on typical pages the cut went mostly into labels
  written up the side of drawings - `FUER`, `R934`, `X6` - which the recogniser had read correctly,
  and turned them into loose letters; it removed 112 words there, a rotated table caption among them.
  A region is now cut only where at least half its rows line up with horizontal words of ordinary
  height, and the median gap between its rows is at least 0.15 of a line.
- **Gap size alone does not separate them.** At 0.3 of a line the gap test refused 117 real table
  stacks, tightly set tables whose rows stand 0.2-0.3 of a line apart - a third of the gain - while
  sideways labels on typical pages run up to 0.25. The row test separates them: it refused no table
  stack and 44 typical-page regions. The gap test is kept at 0.15 for the words it catches first.
- **A block is not a column.** On a component layout a region 6 lines wide holding `Q14`, `U6`,
  `Q16` and `R27` passed both tests, was cut into two rows that read as nothing, and took eight
  correct words with it. Every table stack was under 2 lines wide, so stacks are capped at 2.5; and
  a region is replaced only when at least half its rows come back readable.

The engine logs a verdict for every tall region at debug level (`cut`, `no gap to cut at`, `rows too
close`, `off the text rows`). On the table pages 319 of 335 are cut. The words the cut removes there
are the stacks' own readings and single digits the rescue had already found, now read again as rows.
The extra detector pass runs only on pages with a tall, narrow word: 100 table pages took 175-181 s,
against 172 s without it.

`SplitTallStacks` is on by default; `--no-stack-split` leaves stacks as they were read.

## After deskew: measuring the line, and the rows' own words

With deskew off (`deskew-offset.md`) the table harness stopped following our shift, and what was
left of the "nothing there" singles could be looked at one by one. The rescue now logs every
candidate the row and column tests drop, and every reading it rejects.

- **Most of the rest never reached those tests.** Of 826 singles still showing nothing, 736 were
  lost before them - and every one looked at was a clean glyph of its own, 12 x 20 px, not joined
  to a rule. They were **blocked by uncut stacks**: on page 75 words read `9000` and `UNUNE`, 19-25 pt
  tall, still sat over the check-digit column, and the clearance gap, sized by the word's height,
  took the quantity column beside it too.
- **Those stacks were never cut because the line height was measured on lines.** A line chains a
  table row together with the tall stack beside it, and the median swelled until a stack 2.7 lines
  tall no longer looked tall. The line height is now the median of words of three or more
  characters that are wider than tall; a word taller than wide gets only the ordinary clearance.
- **That let drawing symbols through on typical pages** - terminal circles as `O`, strokes as `1` -
  about half of what it added there. A table's orphan sits *between* words on its row, part
  number to the left and description to the right; a rescued glyph must now have both.
- **A split row with one word is placed on the row's ink**, and several words share the row out at
  the widest blank gaps between the recogniser's word centres. Its own boxes for a check digit
  came back a point wide, beside the ink.

| | tables: tokens read | tables: singles read | tables: singles, nothing there | typical: tokens read | typical: ours on no Acrobat token |
|---|---|---|---|---|---|
| deskew off | 33,587 | 5,047 | 835 | 17,493 | 11,135 |
| rows' words on their ink | 33,594 | 5,054 | 826 | 17,493 | 11,135 |
| line height from words | 34,089 | 5,445 | 662 | 17,600 | 11,244 |
| **... and words on both sides** | **34,067** | **5,434** | **674** | **17,555** | **11,167** |

The last step gives back 22 tokens on the tables and 45 on typical pages for 87 fewer stray
tokens; the loss is mostly a column at the right edge of a page, which has no word beyond it
(page 99 of the typical book, 24 check digits). Kept, because in a text layer stray characters
from drawings cost more than a missing check digit. On the 100 tables that is 34,067 of Acrobat's
43,944 content tokens, from 32,194 before any of this.

## Two settings that stay as they are

Both against the engine above, over both books:

| | tables: tokens read | tables: ours on no Acrobat token | typical: tokens read | typical: ours on no Acrobat token | seconds (tables / typical) |
|---|---|---|---|---|---|
| as shipped | 34,067 | 5,583 | 17,555 | 11,167 | 165 / 163 |
| rescued reads kept from 0.8 | 34,072 | 5,584 | 17,563 | 11,174 | 162 / 157 |
| denoise off | 34,039 | 5,652 | 17,666 | 11,642 | 151 / 155 |

- **Keeping rescued readings from 0.8** changes 5 and 8 tokens. The 0.9 bar stays; it is now
  `OcrEngineOptions.OrphanConfidence`, so it can be measured without a rebuild.
- **Denoise off** is 7% faster, and on typical pages reads 111 more of Acrobat's tokens and 475
  more that land on none of them; on tables it is slightly worse. Compared as text, it changes
  which words come out right in both directions - on typical pages 4,436 token texts gained and
  3,773 lost, each list a mix of right and wrong (`CAPACITOR-FXD`, `RESISTOA-THMR` gained;
  `TRANSFORMER`, `RESI5TOR` lost). Not a clear improvement, so it stays on.

## What this does not settle

- **77 singles are still under stacks; 674 still have nothing there.** About half of a sample of
  the latter are Acrobat splitting a word we read whole (`1%`, `0±100`), which the metric scores
  as a miss.
- **A table's right-hand column of single characters is not rescued**: nothing lies beyond it.
- **Sideways labels on drawings are still sometimes cut as stacks** (`CABLE` read on its side,
  then cut and read as `CAAE`). The both-sides test cannot guard the cut: a table's leftmost
  designator column has no word to its left.
- **Readings between 0.8 and 0.9 are dropped** - 46 of the misses, most of them right (`8` at
  0.82-0.89). Keeping them was tried and gains 5 tokens; not worth it.
- **Schematic pin numbers** standing alone are mostly not rescued: the column test drops them with
  the drawing strokes they look like. Pin numbers that happen to line up are kept.
- **Nothing already in the library is re-read** by this change.
