# Typical tables against Acrobat — the loss does not extend past the tail

`tables-against-acrobat.md` sampled the twenty most digit-dense pages in the library and Acrobat
won. Sizing then showed that sample covered 0.7% of the table population, and that the band it
tested — 63% digits and above — is **eleven pages in the whole corpus**.

This tests the other 99.3%.

## The sample

100 pages, drawn at random from five digit-share bands, at most two per document, **71 distinct
documents**. Text layer removed by rasterisation: 0 extractable characters, so neither engine can
read one that was already there.

| Band | Pages sampled | Population it represents |
|---|---|---|
| 18-25% digits | 20 | 1,159 |
| 25-30% | 20 | 901 |
| 30-35% | 20 | 902 |
| 35-45% | 20 | 783 |
| 45-60% | 20 | 175 |

## ManualForge is ahead, and no band shows a systematic loss

| Band | Acrobat | ManualForge | Delta | Pages A / level / M |
|---|---|---|---|---|
| 18-25% | 3,453 | 3,562 | **+3.2%** | 6 / 6 / 8 |
| 25-30% | 2,802 | 4,204 | **+50.0%** | 3 / 3 / 14 |
| 30-35% | 3,980 | 4,379 | **+10.0%** | 1 / 11 / 8 |
| 35-45% | 4,897 | 4,961 | **+1.3%** | 9 / 4 / 7 |
| 45-60% | 6,953 | 6,448 | −7.3% | 4 / 7 / 9 |
| **All** | **22,085** | **23,554** | **+6.7%** | **23 / 31 / 46** |

ManualForge leads on 46 pages of 100 against 23, with 31 level.

Junk rates hold to the pattern of every previous round: 45.3% of Acrobat's unique output is one or
two characters, against 18.4% of ours.

## The one band worth a second look

**45-60% is mixed and sits directly below the known-bad zone.** Acrobat has 7.3% more
content-bearing tokens, yet ManualForge leads on more pages, 9 to 4. That shape — losing on totals
while winning on pages — means Acrobat wins a few pages by a lot, which is exactly what it does
above 63%.

So the transition is not sharp at 63%; it starts somewhere in the 45-60% range and becomes severe
above it. That band is 175 pages, and the band above it is 26. Neither is large.

## What this settles

The loss is confined to the extreme tail. Across the 3,745 table pages below 45% digits,
ManualForge is at parity or ahead in every band. The earlier conclusion — "tables are a class we
lose" — is now doubly wrong: it was drawn from 0.7% of the population, and the other 99.3% does not
behave that way.

What survives is small and specific: **about a dozen pages of military parts indexes and
stock-number tables, plus some fraction of the 175 pages between 45% and 60% digits, are recognised
materially worse than by Acrobat.**

## What it still does not settle

Whether either engine's text is *correct*. On a table most tokens are digits, the metric counts any
digit string as content, and a wrong number scores as well as a right one. Five rounds have now
measured how much real-looking text is found and none has measured accuracy. That needs
hand-corrected pages, which is #6.

The 6.7% lead should be read as "finds more plausible text", not "is more accurate".
