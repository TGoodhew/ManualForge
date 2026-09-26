# Schematic pages: ManualForge against Adobe Acrobat

The first head-to-head against another engine, and the first answer to whether the schematic pages
this corpus is full of are recognised at all.

## The test was built so neither engine could cheat

24 pages from **24 different documents**, collected into one image-only PDF with
`manualforge assemble`. Every page is rasterised at 300 dpi and re-embedded as an image, so the text
layer the sources carried is gone by construction rather than by a filter that might miss some —
`inspect` confirms **0 extractable characters**. Both engines then recognise the same pixels.

Pages were chosen by a designator-density classifier over the existing text: a page counts as
schematic-looking when over 10% of its tokens match a component designator (`R13`, `C4`, `A2R7`) and
under 3% are common English words. That found 2,775 candidate pages across 243 documents. One page
per document was taken, then 8 each from the top, middle and bottom of the range, so the set is not
the 24 easiest pages.

Acrobat's pass was run by Tony; ManualForge's by `manualforge ocr` at defaults.

## ManualForge recovers about half as much again, and it is real

| | Acrobat | ManualForge |
|---|---|---|
| Characters | 16,779 | **19,942** |
| Words | 3,441 | **4,999**  (+45.3%) |
| Pages clearly ahead | 3 | **14** |
| Pages within 10% | 7 | 7 |

Volume alone would reward an engine that invents text, so the words each engine found *and the
other did not* were classified:

| Unique words | look like designators | look like values | junk (≤2 chars) |
|---|---|---|---|
| Acrobat, n=1,552 | 4.6% | 15.9% | **50.6%** |
| ManualForge, n=3,110 | **46.9%** | 20.5% | 16.8% |

The samples say it plainly. ManualForge's extra words are `r65 u4 c42 r637 c718 mp78 ds31 r461` —
component designators, which is exactly what a schematic is made of. Acrobat's extra words are
`. i - ll j---- ckfe uerte nehdfruhcs schailtellliste` — mostly single characters and noise.

So the +45% is not padding. **ManualForge is ahead on this corpus by a wide margin, and the
comparison is done.**

## Where Acrobat genuinely wins, which is the useful part

Two of its three winning pages are hollow: book page 10 is 79% junk in its lead, page 18 is 43%. But
they are not all noise, and one is a real defect.

**Binary and bit-pattern tables — book page 21, `HP 8340B, 41B Operating Information` p93.** Acrobat
finds `x1101101`, `x1011001`, `x1110000`, `x1001110`; ManualForge does not. These are rows of a
truth table, and a service engineer looking one up would find it in Acrobat's text and not in ours.
Acrobat leads 360 words to 220 on that page, and unlike the other two the lead is mostly content.

**Dotted part numbers — book page 18.** `0007.0793.00` and `ohm+-1` are in Acrobat's output and not
in ours.

Both are narrow and specific, which makes them worth filing rather than filing away.

## What to do differently next time

The classifier selects on **designator density in the existing text layer**, which has two biases
worth fixing before a second round:

* It favours pages whose recognition already worked, because a page whose text layer is empty has no
  designators to count. The pages most worth testing are the ones it is least likely to pick.
* It does not distinguish a circuit diagram from a component-locator drawing or a parts list, all of
  which are dense in designators. Tony's verdict on the set was that he wanted **more actual circuit
  diagrams**.

Both point the same way: add a geometry signal. A circuit diagram is drawn — many path operations,
sparse text, and frequently a landscape or oversized fold-out. The audit already computes path
operations per page, so selecting on `paths >> characters` rather than on the text alone would pick
schematic *sheets* and would not care whether their text layer was any good.
