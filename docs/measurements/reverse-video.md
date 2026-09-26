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
