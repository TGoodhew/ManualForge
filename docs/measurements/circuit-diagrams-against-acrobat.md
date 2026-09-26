# Circuit diagrams against Acrobat — the harder round

Round one (`schematics-against-acrobat.md`) chose pages by counting component designators in the
existing text, which can only find pages whose recognition already worked. Tony asked for more
actual circuit diagrams. `manualforge pages --shape drawn` selects on the audit's geometry instead,
and the set it produces is visibly a different test.

| | round 1 | round 2 |
|---|---|---|
| Landscape pages | 2 of 24 | **12 of 24** |
| Largest page | 17 × 11 in | **34 × 24 in** |
| ManualForge characters | 19,942 | 50,345 |

16 vector circuit diagrams — ranked by path-painting operations per glyph, so the drawing outweighs
the labelling — and 8 photographed schematic sheets. Text layer removed by construction: 0
extractable characters.

## Acrobat produces half as much again, and most of it is not there

| | Acrobat | ManualForge |
|---|---|---|
| Characters | **77,588** | 50,345 |
| Words | **18,306** | 12,425 |

On raw volume Acrobat wins comfortably, which is the opposite of round one. It does not survive
looking at.

| Unique words | designators | values | words 3+ letters | junk (≤2 chars) |
|---|---|---|---|---|
| Acrobat, n=11,497 | 3.5% | 9.7% | 10.5% | **66.5%** |
| ManualForge, n=5,616 | **24.3%** | **25.5%** | 22.4% | 16.0% |

Two thirds of what Acrobat finds and ManualForge does not is single characters. The samples:

```
Acrobat only      i  h  i  l  -  dou  ra  ...-  c9  -  1u  -  00  n  j  .  itude  l.  w  -  .  -  -
ManualForge only  c40  r67  c906  1826-2403  r6  mhz  c552  q7  u1a  c222  q13  c30  hizout2  c1b0
```

Book page 20 is the clearest case. Acrobat emits **2,770** tokens against our 348 — an eightfold
lead — and a slice of them reads:

```
1  u  C  11101  OJ..iDl  .101.  J  i  i  .I.  l  l  J  i  J  J  t  1  .  .  1.  .  .  -  .  o  1  1
```

That is a recogniser finding text in the *lines of the schematic*.

## Counting only what carries meaning reverses the result

Tokens that are a component designator, a value, or a word of three or more letters — the same rule
applied to both:

| | Acrobat | ManualForge |
|---|---|---|
| Content-bearing tokens | 7,566 | **8,905  (+17.7%)** |
| Pages led on | 4 | **15**  (5 level) |

So ManualForge is ahead on this set too, and by a wider margin in pages than in tokens.

**What this metric cannot do** is notice text that is confidently wrong. An engine that reads `R13`
as `R18` scores exactly as well as one that reads it correctly, because both produce a designator.
Everything here measures *how much real text is found*, never whether it is right. Only
hand-corrected pages would settle that, and that is what #6 is for.

## The four pages Acrobat genuinely wins

| Book page | Source | Kind |
|---|---|---|
| 3 | `LC574AL.pdf` p21 | vector |
| 13 | `DG1000Z User's Guide.pdf` p22 | vector |
| 20 | `59401A-OSM.pdf` p61 | scanned sheet |
| 21 | `3335A-OSM.pdf` p91 | scanned sheet |

Page 20 is a real deficit and not a scoring artefact: 691 content-bearing tokens against our 204,
after the junk is stripped from both. Two of the four are photographed sheets from 1970s manuals,
which is worth noting as a pattern rather than a conclusion — 8 such pages is too few to claim one,
and we lead on `83522A-OSM` p319, which is the same kind of page.

## Where this leaves the comparison

Past parity on both rounds, and by the more demanding measure on the harder set. The remaining work
is not "catch up with Acrobat" but two specific defects it exposed:

* **#19** — wide letter-spacing read as word breaks, which loses code and truth tables.
* **Page 20 of this book** — a scanned schematic sheet where Acrobat finds three times the real
  text. `_compare/circuit-test.pdf` p20 is the regression case, and Acrobat's output beside it says
  what a good answer looks like.

---

# Round three: Tony's own selection, and the scanned-sheet hypothesis dies

Round two ended by noting that two of Acrobat's four winning pages were photographed sheets from
1970s manuals, and declined to call it a pattern on eight pages. Tony then hand-picked twelve pages
— almost all scanned fold-outs, the exact type in question — which is the right way to settle it.

`11713A-OSM` 71, 74 · `651B_OSM` 31, 32 · `400F_OSM` 28 · `3335A-OSM` 87, 88 ·
`8902A Service Manual Full - Searchable` 561 · `8903B Service Manual` 213 ·
`5200A-OSM` 217, 222, 238

Eleven of the twelve pages are landscape, four of them 34 inches wide. Text layer removed by
construction: 0 extractable characters.

## The pattern does not survive contact with more pages

| | Acrobat | ManualForge |
|---|---|---|
| Raw words | **7,431** | 6,438 |
| **Content-bearing tokens** | 1,961 | **3,991  (+103.5%)** |
| Pages led on | **0** | **10**  (2 level) |

Acrobat produces more words and **less than half the real content**, and does not lead a single page.
This is the widest margin of the three rounds, on the set chosen to be hardest for us.

| Unique words | designators | values | junk (≤2 chars) |
|---|---|---|---|
| Acrobat, n=5,566 | 3.0% | 6.1% | **67.9%** |
| ManualForge, n=4,573 | **21.7%** | **23.0%** | 24.8% |

```
Acrobat only      /  q  ----  -  i  73  -  -  r52.  -  oti-i  j  .  -  2104  u  p/0  -c  000  -  --
ManualForge only  loop  amptd  nrfd  +15v2  timing  pulse  5200a.4165  cr2o  l2  u1-5  r24  cr333  08901-60130
```

Book page 8 is round two's story in miniature: `8902A` p561 gives Acrobat 1,692 raw words to our
924, an 83% lead, which becomes 509 against 548 once only meaningful tokens are counted. The lead
was junk.

## So what was page 20 of round two?

An outlier, not a class. `59401A-OSM` p61 remains a genuine deficit — 691 content-bearing tokens to
our 204 — but twelve more pages of the same kind produce no Acrobat advantage at all. Whatever is
wrong on that page is specific to it, and guessing from one example is what the extra twelve pages
were for.

## What all three rounds cannot tell us

Every number here counts *how much real text is found*, never whether it is right. `R13` read as
`R18` scores identically to `R13` read correctly. Three rounds have measured recall and nothing
else, and no amount of further rounds changes that — it needs pages corrected by hand, which is #6.

That is worth stating plainly next to a 103% margin, because a margin that large invites the
conclusion that the recognition is good. What has been shown is that it finds far more of the text
than Acrobat does. How much of what it finds is correct remains unmeasured.
