# Precision of the pages the third gate limb added

The render gate gained a third limb on 24 September 2026 — render any page carrying an image over a
tenth of itself — and the corpus audit went from 11,008 flagged pages to 19,351. The question this
answers is whether those 8,343 new pages are real, because repairing them is about three hours of
GPU and the honest alternative was raising the threshold.

**They are: 11 of 12 sampled pages are genuine. Precision 92%.**

## How the sample was drawn

Every page flagged before this change had already been repaired, so *flagged and unrepaired* is
exactly *newly flagged*. The sample is twelve documents drawn at random with seed 1 from the
library, taking the first unrepaired flagged page of each, and dumping what the detector saw with
`doctor --explain --dump`: pale grey for ink an extracted glyph accounts for, black for ink nothing
accounts for, a red box round every cluster counted as lettering.

The judgement applied to each is the one used in `UNDER-EXTRACTION-SAMPLE.md`: **is there readable
text here that somebody might search for, which the text layer does not hold?** Not "is there any
unaccounted-for ink".

## The twelve

| # | Document | Page | Verdict | What is actually there |
|---|---|---|---|---|
| 1 | 415E-OSM-90009 | 13 | TP | Front-panel photograph. Prose extracts; `415E SWR METER`, `XTAL IMPED`, `BIASED`, `BOLOMETER`, `GAIN VERNIER`, `RANGE-DB` and the meter scale do not |
| 2 | DS1000Z Programming Guide | 244 | TP | Screenshot: `USB Test and Measurement Device (IVI)`, the radio-button labels, `Click Next to continue` |
| 3 | Tek 2465 Operators | 18 | TP | Rear-panel photograph whose CAUTION label carries the line-voltage table, fuse ratings and `POWER MAX WATTS 120` |
| 4 | 8902A Service Sheet 17 | 2 | TP | Block diagram: `DIV 1`–`DIV 6`, `U8`, `U7`, `U6A`, `U12A`, `CR8` unextracted while other labels extract |
| 5 | 3325B-IM | 10 | TP | `FUSE HOLDER`, `LINE VOLTAGE SELECTOR`, `ALIGNMENT ARROWS`, and a fuse rating table |
| 6 | HP8405A PROBE REPAIR | 1 | TP | Part numbers `08405-60011`, `08405-60049`, `08405-60056`, serial-number ranges, shipment dates |
| 7 | 85672 Spurious | 27 | TP | Test setup: `SIGNAL GENERATOR`, `POWER COMBINER`, `GOOD ISOLATION`, `DEVICE UNDER TEST` |
| 8 | DSA800 UserGuide | 184 | TP, thin | `Completing the Hardware Update Wizard` and its boilerplate. Real text, low value |
| 9 | 8901B Service Vol 2 | 3 | TP | Schematic: `R2`, `C8`, `Q1`, `U1A`, `RT1`, `±15 VDC`, `SHAPING NETWORK` |
| 10 | 431C-OSM | 9 | TP, thin | Three part labels only: `DIVIDER ASSEMBLY`, `DIVIDER LATCH`, `RETAINER` |
| 11 | 8902A Operation & Calibration | 23 | **FP** | AM waveform traces. Captions extract; the clusters are the densely packed carrier strokes themselves |
| 12 | 8672A-OSM-90063 | 22 | TP | Filter values `620µH`, `1100µH`, `0.27µF` and cable part numbers `HP 1250-1487`, `HP 1250-1420` |

## What the numbers mean, and what they do not

**Precision on the new flags is 92%** (11 of 12), against 80% measured on the flags the two-limb
gate produced. That is not evidence the detector got better at judging a page; it is evidence that
the pages this limb reaches are an easier population. A raster figure on a prose page either has
lettering in it or does not, while the old false positives were drawn hardware — resistor bodies,
screws, halftone dots — being mistaken for characters.

**The one false positive is the same old cause in a new costume.** Dense line art read as lettering:
here the carrier strokes of an AM waveform, packed tightly enough that each looks glyph-shaped. It
is the family already described in `UNDER-EXTRACTION-SAMPLE.md`, and it costs GPU time rather than
correctness — OCR of that page recovers nothing and the merge adds nothing.

**Two of the eleven are thin.** A Windows wizard's boilerplate and three part labels are both real
unextracted text, and neither is worth much. Counting them as successes is right by the rule and
flattering by the spirit, so they are marked rather than hidden.

**This measures precision on the new flags only.** It is not a new figure for the detector overall,
and it says nothing about recall — that needs a fresh sample of *unflagged* pages, which is the
larger job described in `UNDER-EXTRACTION-SAMPLE.md` and still undone. The sample is twelve pages;
92% from twelve is 62% to 100% at 95% confidence, which is enough to decide whether to spend three
hours of GPU and not enough to quote as a property of the detector.
