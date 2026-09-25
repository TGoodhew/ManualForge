# Error rate: 20 flagged and 20 unflagged pages, checked by eye

A detector whose error rate is unknown is not finished, and a rate quoted from a sample nobody else
can re-draw is not much better. This is the sample, how to re-draw it, what each page turned out to
be, and what the numbers are — including the one that is not good.

## Drawing the sample

```
manualforge doctor "<library>" --review 20 --into <folder> --seed 1
```

Seeded, so the same 40 pages come back for anybody who wants to disagree. Each page is written as
the picture the detector worked from: pale grey for ink an extracted glyph accounts for, black for
ink nothing accounts for, a red box round every cluster counted as lettering.

The judgement applied to each page was: **is there readable text here that somebody might search
for, which the text layer does not hold?** Not "is there any unaccounted-for ink" — a page whose
only missing word is `CAUTION` is not worth a re-OCR, and counting it as a success would flatter the
numbers.

## The corpus this was drawn from

579 documents, 100,830 pages, audited in full.

| | |
|---|---|
| Documents flagged | 511 |
| Pages flagged | 11,008 |
| — of those, **drawn** (content that never had a text layer) | 3,434 |
| — of those, **raster** (lettering inside an image) | 7,574 |
| Documents whose *drawn* share crosses 10% — the finding | **104**, 2,847 pages |
| Documents with isolated figure pages | 226, 2,070 pages |
| Scanned documents with OCR gaps | 180, 5,735 pages |

## Flagged pages: precision

**16 of 20 correct — precision 80%.**

| # | Page | Verdict | What is actually there |
|---|---|---|---|
| 01 | 8720C Service p5 | TP | German noise-declaration paragraph, wholly unextracted |
| 02 | 7090A OM p75 | TP | `POSITIVE SLOPE`, `TRIGGER OCCURS` diagram labels |
| 03 | DPO3034 User p110 | TP | Screen readout `Waveform Intensity: 35%` |
| 04 | 20090826151300694 p30 | **FP** | German schematic; text extracts. Blobs are resistor bodies and capacitor plates |
| 05 | TDS784D Service p316 | **FP** | Exploded parts diagram; callouts extract. Blobs are screws and connectors |
| 06 | 54845A Programmer p106 | TP | `WAVeform` command syntax diagram |
| 07 | 8901B Service v3 p74 | TP | Entire component-locator table and every board designator |
| 08 | AN/USM-488 Service p238 | TP | Every adjustment-point label; 48 characters extracted from the whole page |
| 09 | 8560E p167 | TP | Whole `AUTO COUPLE` softkey menu tree |
| 10 | 8656B Service v2/3 p354 | TP | Schematic; block titles extract, component designators do not |
| 11 | 3585B-SM-V1 p289 | TP | Scope setup: `A21J1 & A21J2`, `.05 usec/DIV`, `10:1 probe (ac coupled)` |
| 12 | E4438C User p624 | TP | ESG display: `4.000 000 000 00 GHz`, `Chip Rate: 3.840000 Mcps` |
| 13 | 8902 Service 2 p36 | TP | Full schematic, every designator and voltage annotation |
| 14 | TDS 784D User p288 | TP | Scope screen text and soft-menu labels |
| 15 | 54845A user p52 | TP | Front-panel labels `DC`, `AC`, `1MΩ`, `50Ω`, `Coupling`, `Input` |
| 16 | 13f12om3e p419 | TP | Scanned inspection form, entire text |
| 17 | 7090A-SM p18 | **FP** | Prose extracts; only `CAUTION` missing. Blobs are halftone photographs |
| 18 | 53131A Service p42 | **FP** | Everything extracts. 46 blobs (threshold 40) are drawn front-panel buttons |
| 19 | 8902A Service p261 | TP | Block-diagram labels `LF VCXO`, `SAMPLER`, `TRACK LOOP AMPLIFIER` |
| 20 | 5440A-SM p194 | TP | Component-locator PCA, most designators unextracted |

**Every false positive in this sample has the same cause**: drawn hardware — resistor bodies,
capacitor plates, screws, connector outlines, front-panel buttons, halftone dots — is the size,
aspect and density of lettering, and the shape filter cannot tell it from a character. Three of the
four sit close to the 40-blob threshold.

**A second cause, found later and probably commoner: ruled tables.** Where a row line crosses a
column line it cuts the rule into short segments, and a segment of rule between two closely spaced
rows is the size, aspect and fill of a character. It did not appear in these twenty pages; it
appeared when `8591e Calibration Guide.pdf` was examined page by page, where 40 of 141 flagged pages
are performance-test record forms whose text extracts perfectly and whose only unaccounted ink is
the table borders. This corpus is full of such forms. Issue #11, which also proposes the
discriminator: rule fragments sit in perfect vertical columns at one x, repeated down the page at
the row pitch, and lettering does not.

The mitigation that already exists is that these pages are cheap to repair and the repair is
harmless: OCR of a page with no recoverable text returns almost nothing, and the merge adds nothing
to the index. A false positive here costs GPU time, not correctness.

## Unflagged pages: recall

**18 of 20 correctly left alone. Two genuine misses.**

The 18 correct ones include several that were worth checking rather than assuming:

- **clean-36** — 16% of the page is unaccounted-for ink (a solid border) and **0** glyph-shaped
  blobs, so it is not flagged. This is the shape filter doing exactly its job.
- **clean-34** — a softkey menu map, the same *genre* as flagged-09, but with every label genuinely
  set as text. 0 blobs. The detector separates the two cases on the thing that matters.
- **clean-28** — a datasheet whose schematic labels all extract; its 28 blobs are diode bodies.

The two misses:

- **clean-29** (7550A Interfacing p298) — a character-cell figure whose coordinate labels
  `START (10, 0)` and `END (28, 24)` are unextracted. **37 blobs against a threshold of 40.** A
  threshold miss, three blobs short.
- **clean-33** (GE range manual p29) — figure labels `Receptacle`, `Socket`, `G6.35 Bulb`,
  `G9 Bulb`, `Tab` unextracted. **The page was never rendered at all**: 1,974 characters of prose and
  2 path operations, so neither limb of the gate fired.

### Re-measured, 25 September 2026

Everything below describes the two-limb gate. After the third limb was added and the corpus
re-audited, the same seeded draw of 20 unflagged pages found **1 miss rather than 2**, moving recall
from a point estimate near 49% to near **79%** — with an interval no narrower, because the sample is
no larger. The surviving miss is the reverse-video blind spot described at the end of this document,
not the render gate. `docs/measurements/recall-after-the-third-limb.md` has the working.

### What recall actually is, and why the number is soft

Precision is measured on 20 flagged pages drawn from 11,008, and 16/20 is a reasonably firm 80%.

Recall is not firm, and it would be dishonest to quote it as though it were. The unflagged sample is
drawn from ~90,000 pages, and 2 misses in 20 puts the miss rate somewhere between about 1% and 32%
at 95% confidence. Propagating that:

| | |
|---|---|
| Flagged pages that are genuine (80% of 11,008) | ~8,800 |
| Unflagged pages that are misses (10% of ~89,800) | ~9,000 |
| **Recall, point estimate** | **~49%** |
| Recall, plausible range given the sample size | ~20% to ~90% |

So the honest statement is: **the detector finds roughly half of the under-extracted pages, and the
sample is far too small to say that to better than a factor of two.** Narrowing it needs a sample of
a few hundred unflagged pages, which is a day of looking at pictures, not an afternoon.

### The miss that is worth fixing, and why it has not been

`clean-33` is not a threshold problem, it is a **gate** problem, and gate problems are the ones that
cannot be recovered by tuning: a page that is never rendered cannot be flagged whatever the
thresholds say. The gate renders a page when it has under 900 characters *or* paints 40+ paths. A
page with plenty of prose and a raster figure whose labels are unextracted satisfies neither, and
that is an extremely common shape in this corpus — any manual with a photograph or a screenshot
under a paragraph of text.

The fix is to add a third limb: render a page that carries an image covering a meaningful share of
it, whatever its character count. That is a small change and it would probably move recall
substantially.

**It is done, as of 24 September 2026.** The gate has a third limb — render a page carrying an image
over 10% of itself, whatever its character count — and `GERange.pdf` page 29, the page this section
was written about, is now flagged: 97 glyph-shaped clusters and 2.61% of the page in ink nothing
accounts for. A page with no text layer at all is decided before the gate, so a library of scans
does not begin rendering end to end.

Re-audited over the whole corpus it took the flag count from 11,008 pages to **19,351**, and a
seeded sample of twelve of the new flags, judged by eye, found **eleven genuine and one false
positive**: front-panel photographs whose legends do not extract, CAUTION labels carrying fuse
ratings, schematics whose designators are missing, figures holding part numbers. The one miss is
dense line art read as lettering — the carrier strokes of an AM waveform — which is the family this
document already describes. `docs/measurements/new-flags-precision.md` has the twelve.

Everything below is why it waited, and it still describes what the change costs: it changes what the
audit looks at, so it invalidates the corpus run above and the 2.5 hours that produced it, and it
needs its own precision measurement afterwards
— adding a limb that renders every illustrated page could easily flag thousands more. Shipping the
measured detector with a known, named, diagnosed gap is more useful than shipping an unmeasured one
that might be better.

## Two blind spots found while looking

Neither is in the numbers above, because neither produced a wrong verdict on this sample — but both
are properties of the method rather than of the sample.

**Reverse video is invisible.** White text on a black button is *absence* of ink inside a block of
ink, so the blob detector cannot see it (clean-37). On that page the button labels duplicate the
adjacent prose so nothing is lost, but a page whose only content is a reverse-video screenshot would
be missed entirely.

**Scan furniture inflates blob counts.** Spiral-binding holes (flagged-02) and halftone dots
(flagged-17) produce glyph-shaped blobs in quantity. They did not change a verdict on this sample,
but they push borderline pages over the threshold and they are part of why three of the four false
positives sit near it.
