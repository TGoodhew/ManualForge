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
