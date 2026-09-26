# Tables against Acrobat — the round we lose

Three earlier rounds used schematics and ManualForge won all three, by 17.7%, 17.7% and 103.5% on
content-bearing tokens. #19 was about **tables**, and one dense code table where Acrobat read
fifty-six bit patterns to our one. This asks whether that page was an outlier.

It was not.

20 dense table pages from 14 documents, selected on shape — over 55% of tokens four characters or
shorter, over 18% of characters digits, under 2% ordinary English words, at least 25 lines — which
matches 3,946 pages across 316 documents. Text layer removed by rasterisation: 0 extractable
characters.

## Acrobat wins, and it still wins after the junk comes out

| | Acrobat | ManualForge |
|---|---|---|
| Raw words | **13,865** | 10,440  (−24.7%) |
| **Content-bearing tokens** | **8,674** | 7,963  (−8.2%) |
| Pages led on | **10** | 1  (9 level) |

Half the pages are level, so this is not a rout. But it is the first loss in four rounds, and it is
on exactly the material #19 is about.

Acrobat's raw lead is still mostly noise — 43.4% of its unique output is one or two characters
against our 14.4% — which is why the raw figure overstates it. The signal figure does not.

## The metric flatters us here, and the real gap is probably wider

On schematics, "content-bearing" separated designators from junk cleanly. On tables it does not,
because **a table is mostly numbers and the value pattern matches any digit string**. Wrong digits
score exactly as well as right ones.

Our unique tokens are **76.7% values** against Acrobat's 43.8%, which looks like an advantage until
the samples are read:

```
ManualForge only   192.857143   89900009   10111890   008090   3989   500988
Acrobat only       10-04-938-20   90-8880   875f1   -4919   01101
```

Acrobat's look like stock and part numbers. Several of ours look like digit-shaped noise that the
metric counts as content. So −8.2% is the optimistic reading of this round, not the pessimistic one.

Establishing the true gap needs correct transcriptions, which is #6 and does not exist.

## The page that started #19 is the extreme of the pattern, not an exception

| Book page | Source | Acrobat | ManualForge |
|---|---|---|---|
| 7 | `59401A-OSM` p61 | **691** | 204 |
| 1 | `8901A_MIL_MANUAL` p21 | **359** | 124 |
| 4 | `8901A_MIL_MANUAL` p22 | **346** | 88 |
| 3 | `HP_8672A` p282 | **582** | 478 |
| 8 | `461A Mil Manal` p21 | 440 | **1,231** |

`59401A-OSM` p61 was called an outlier after round two. It is not — it is the worst case of
something that happens across military parts indexes and stock-number tables generally. Both
`8901A_MIL_MANUAL` pages lose by roughly three to one.

The single page we win, we win decisively, so this is not a blanket incapacity.

## What this settles, and what it does not

**Settled:** tables are a class ManualForge loses to Acrobat, and no configuration recovers it —
thirteen settings over 120 pages moved the yield by 7% end to end, and dropping the recogniser's
confidence floor to 0.01 changed that page from one bit-pattern reading to one.
`recognition-sweep-tables.md` has that.

**Not settled:** whether either engine is *correct*. Every number in all four rounds counts how much
real-looking text is found. On schematics that was a good proxy. On tables, where most tokens are
digits, it is a weak one.

The honest options are a different recognition path for table-shaped pages, or documenting the
limit. That is a decision about what this application is for, not a bug fix.

---

## Correction: this sampled the extreme tail, not tables

Sizing the affected population the next day showed the comparison above is not a measurement of
tables. It is a measurement of the most digit-dense twenty pages in the library.

The 20 pages were chosen by sorting the table-shaped population on digit share and taking the top,
which is the same trait that predicts losing. Against the population they were drawn from:

| Digit share across the 3,946 table-shaped pages | |
|---|---|
| Median | **30%** |
| 75th percentile | 35% |
| 90th percentile | 41% |
| 99th percentile | 56% |
| Maximum | 72% |

**The sample ran 60% to 72%.** Only **26 pages of 3,946 — 0.7%** — reach 60%. The typical table
page in this library is 30% digits and was never tested.

## Where the loss actually lives

Digit share predicts the result sharply within the sample:

| | Acrobat wins | Level | ManualForge wins |
|---|---|---|---|
| ≥ 63% digits | **8** | 0 | 1 |
| < 63% digits | 2 | **9** | 0 |

Counting that band across the whole library:

**11 pages, across 5 documents, 0.01% of the corpus.**

| Document | Pages |
|---|---|
| `HP_8672A_Synthesized_Microwave_Signal_Generator_Service` | 4 |
| `8901A_MIL_MANUAL` | 3 |
| `tektronix_an-usm-488-service_manual` | 2 |
| `461A Mil Manal` | 1 |
| `59401A-OSM` | 1 |

## What that changes

"Tables are a class we lose" was too strong, and the sizing is what shows it. **Eleven pages are
demonstrably lost.** Nine of the eleven were in the sample, which is why the comparison looked so
decisive — it had found nearly every page in the library that exhibits the problem.

The honest statement is narrower: *the most numerically dense pages in this corpus — military parts
indexes and stock-number tables — are recognised materially worse than by Acrobat, and there are
about a dozen of them.*

Two things remain genuinely open, and neither is settled by more of this sample:

* **The typical table page is untested.** Nothing below 60% digits was measured, and that is 99.3%
  of the population. The trend says parity, since nine of eleven below 63% were level, but eleven
  pages is not evidence about three thousand.
* **The 63% threshold is fitted to this sample**, drawn from the extreme tail. Two of the ten
  Acrobat wins sit at 61-62%, so the boundary is soft, and it should not be quoted as though it
  were established.

A second recogniser for eleven pages is not a serious proposal. Whether one is warranted for the
other 3,935 is a question this measurement never asked.
