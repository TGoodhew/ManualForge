# Reverse video: implemented, measured, and left switched off

White lettering on a black button is the one shape a detector looking for ink cannot see, because
the letters are an *absence* of ink inside a block of it. `doctor --reverse-video` looks inside solid
blocks for glyph-shaped holes. It works, it finds real text nobody could search for before, and it
is **off by default**, because on this corpus it finds photographs in roughly equal measure.

## What it finds

Real, and a whole class of it: **spectrum-analyser screenshots**. A page of Keysight's *Signal
Analysis Measurement Fundamentals* carries two of them, and every readout on both — `Scale/Div
10 dB`, `Ref Level 0.00 dBm`, `Start 0.4000 GHz`, `Res BW 3.0 MHz`, `Video BW 30 MHz`, `Sweep 1.00
ms (1001 pts)`, the `SCAN` and `MEASURE` softkeys — is white on black and in no text layer. This
library is full of such screenshots.

## What it also finds

Photographs and traces, which have no text in them at all:

* `an_69` page 23 — bright highlights inside two halftone photographs of a reed relay and a circuit
  board.
* `an_183` page 26 — the lit fragments of a swept-SWR trace on a black display.

Both are the same mistake in a new place: small light patches of about the right size, in quantity.
It is the reverse of the false positives the sample document already records, where drawn hardware
was read as lettering.

## The numbers, on one folder

15 application notes, 458 pages:

| | Flagged pages |
|---|---|
| Without | 117 |
| With | **165** (+41%) |

Of the 48 pages it added, four were looked at: two genuine screenshots, two photographs. On that
sample it is a coin toss, and a coin toss is not good enough to change what the corpus audit means.

## A discriminator that was tried and did not work

Characters share a baseline; speckle does not. So: keep only holes sitting in a row of three or more
of similar height, which is exactly the argument that fixed ruled tables being counted as lettering.

It made things worse. Flagged pages fell from 165 to 153, but the two photographs stayed flagged
while a **genuine** screenshot page dropped below the blob threshold and out of the report
altogether — the readout text is small, and thinning it cost the page its flag. Removed rather than
kept, and written down here so the next person does not spend the afternoon rediscovering it.

## Where that leaves it

```
manualforge doctor <folder> --reverse-video            # on
manualforge doctor <folder> --reverse-video-area 180   # smallest block worth opening, pt²
manualforge doctor <folder> --reverse-video-fill 0.6   # how solid a block must be
```

Off by default. Turning it on is defensible for a corpus of instrument screenshots and indefensible
as a silent change to a measured audit, and the honest position is that nobody has yet found the
signal that separates a readout from a photograph. Issue #12 stays open with that as its remaining
question.

Setting `MANUALFORGE_REVERSE_VIDEO_PROBE=1` prints every cluster considered, its size, how solidly
it fills its box, and why it was accepted or rejected. That listing is what found the icon-font
correction below: the badges everybody assumed were solid black turned out to be clusters filling
4% of their boxes, which is not a block of ink at all.

## A correction this work forced

The page that motivated all of it — a camcorder manual whose `AVCHD`, `MP4` and `AUTO` badges the
recall sample counted as a miss — turns out **not to be reverse video at all**. Its text layer holds
glyphs for those badges; they are an icon font, and they decode to `N ƒ ' † y }`. The ink is
accounted for, so the detector is right to leave the page alone, and the reason a search for `AVCHD`
fails on that page is that the characters in the file are not the letters on the page.

The miss is still a miss — the text a reader can see is not the text the layer holds — but its cause
was misdiagnosed, and reverse video was never going to fix it. Issue #16 covers the real cause.

## It was never actually off — 25 Sep 2026

The CLI wired the flag backwards:

```csharp
ReverseVideo = !arguments.Has("no-reverse-video"),    // on unless you type --no-reverse-video
```

`DoctorOptions.ReverseVideo` defaults to `false`, this file says "off by default",
`docs/UNDER-EXTRACTION.md` lists the default as `off`, and the README documents
`--reverse-video` as the switch that turns it on. All four were describing an
opt-in. The CLI had built an opt-*out*, so `--reverse-video` did nothing at all —
the limb was already running — and there was no way to turn it off except a flag
nobody had written down.

Every audit run through the CLI since commit `a36bd0a` had it enabled, including
the #14 8340B ingest and the first #17 audit. Fixed to
`ReverseVideo = arguments.Has("reverse-video")`, with
`tests/ManualForge.Core.Tests/DoctorOptionWiringTests.cs` pinning the default
against `new DoctorOptions()` so the two cannot drift apart again. Asserting only
that `--reverse-video` turns it *on* would have passed against the broken wiring;
asserting the default is what discriminates.

### What it had been contributing: one page in 1,263

Worth knowing before deciding how much the mistake cost. Five documents from the
#17 material, each audited twice into scratch databases — once with the limb off,
once with `--reverse-video` — and nothing else changed:

| Document | Pages | Off | On | Delta |
|---|---|---|---|---|
| `E4400-90324` (ESG signal generator) | 259 | 38 | 38 | 0 |
| `E4406-60009-RF-clip` (VSA) | 39 | 2 | 2 | 0 |
| `HP_8657B_Service_Manual` | 438 | 69 | 69 | 0 |
| `8902A Service Manual - 526pp` | 526 | 73 | 74 | **+1** |
| `HP_83592A_A4_ALC_Block_Diagram` | 1 | 1 | 1 | 0 |
| | **1,263** | **183** | **184** | **+1** |

So at the shipped thresholds the limb is very nearly inert on this corpus: one
extra page in 1,263, about 0.08%. Two things follow.

The accidental enabling did no real harm. #14's ingest and the first #17 audit
are not meaningfully different from what they would have been, which is why the
corpus was not re-audited from scratch to correct it — the 31 #17 documents were
re-audited with `--recheck`, and the library-wide standing total came back
identical at 20,762 flagged, 20,322 repaired, 440 outstanding.

And the case for leaving it off is now better than it was. The earlier argument
was that it finds photographs about as often as readouts, from a four-page
sample. The stronger argument is that at 180 pt² and 0.6 fill it barely fires:
the ESG and VSA manuals — the two documents in the set most likely to contain
analyser screenshots — gained nothing at all. Turning it on is not a trade
between readouts and photographs so much as a trade for almost nothing either
way. Loosening the thresholds until it fires often enough to matter is what would
need measuring, and that is what #12 should ask next.

The deltas varying by document is also what confirms `--recheck` genuinely
replaces stored findings rather than merging them, which the identical totals
alone could not have shown.
