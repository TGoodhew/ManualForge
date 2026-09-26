# Recall, re-measured after the gate grew a third limb

The detector's recall was measured once, against the two-limb gate, at **2 misses in 20 unflagged
pages** — a point estimate near 50% with a range from 20% to 90%. One of those two misses was the
render gate never looking at a prose page that carried a raster figure, which is now fixed.

Re-drawn the same way, against the same seed, on the corpus as re-audited on 24 September:

**1 miss in 20.**

## The sample

```
manualforge doctor <library> --review 20 --into <folder> --seed 1
```

Twenty pages the detector left alone, dumped as the picture it worked from: pale grey for ink an
extracted glyph accounts for, black for ink nothing accounts for, a red box round every cluster
counted as lettering. The judgement is the one this repository has used throughout — **is there
readable text here that somebody might search for, which the text layer does not hold?**

Seventeen were judged by eye. Three were settled by measurement instead: `clean-26`, `clean-31` and
`clean-38` have no glyph-shaped clusters and 0.14% or less of the page in unaccounted ink, and
unaccounted ink is the only place missing text could be.

## The one that was missed

**`clean-37`, a camcorder manual, page 119.** Every word of prose is accounted for. The `AVCHD`,
`MP4` and `AUTO` badges are not — a reader can see them, and a search for `AVCHD` does not find this
page.

**The cause was misdiagnosed here, and the correction is worth reading.** This was first written up
as reverse video: white letters on a black field, which a detector looking for ink cannot see.
Building that detector (issue #12) and pointing it at this page found nothing, at any resolution or
threshold — because the page is not reverse video. Its text layer *does* carry glyphs for those
badges, from an icon font, and they decode to `N ƒ ' † y }`. The ink is accounted for; the
characters are simply not the letters on the page.

So the miss stands — the text a reader sees is not the text the layer holds — but its cause is an
icon font, which is issue #16, and no amount of looking at ink would ever have caught it.
`docs/measurements/reverse-video.md` has the whole investigation.

## What that makes recall

| | Two-limb gate, 17 Sep | Three limbs, 24 Sep |
|---|---|---|
| Misses in 20 unflagged pages | 2 | **1** |
| Flagged pages | 11,008 | 19,351 |
| Precision on those flags | 80% | 80% on the old ones, 92% on the 8,343 new |
| Genuine flagged pages, estimated | ~8,800 | ~16,500 |
| Unflagged pages | ~89,800 | ~86,000 |
| Missed pages, estimated | ~9,000 | ~4,300 |
| **Recall, point estimate** | **~49%** | **~79%** |

**And the interval is still wide.** One miss in twenty puts the true miss rate somewhere between
0.1% and 25% at 95% confidence, which puts recall between about 43% and 99%. The direction is
supported — the fix was aimed at a cause that was diagnosed rather than guessed, and the sample
moved the way it should — but anybody quoting 79% as a number should quote the interval with it.

Narrowing it means a few hundred unflagged pages rather than twenty, which is a day of looking at
pictures. That has not been done and is not pretended.

## What is left, in order of how much it costs

1. **Icon fonts** — the miss above. A page whose badges are glyphs from a symbol font is sound by
   every test this detector applies, and unsearchable to a reader. Issue #16.
2. **Reverse video** — not the cause of the miss above, but real elsewhere: analyser screenshots
   whose readouts are white on black. Implemented and left off by default, because it finds
   photographs at about the same rate. Issue #12, `docs/measurements/reverse-video.md`.
3. **The threshold miss** — the older sample's other miss was three blobs short of 40. Still
   possible, and it did not recur here.
4. Everything else the sample did not happen to contain.
