# Genuine tables against Acrobat — we lose, and the population is 6,206 pages

Tony looked at the previous two table books and said they were not tables. He was right, and the
correction changes the conclusion twice over.

## Why the earlier rounds were not measuring tables

Both previous selections filtered on **token shape** — mostly short tokens, plenty of digits, almost
no ordinary English. A component-locator drawing has exactly that profile: `A2R37`, `MOD LEVEL ADJ`,
`-C4-`, `TP6 TP3 ADJ TP1 TP4`. Of six pages sampled from the last book, **one was a table**; the rest
were schematics and locator diagrams.

Line structure did not rescue it. `3335A-OSM` p85, a drawing, scores 77% of lines carrying four or
more tokens — higher than several real tables — because the reading order merges scattered labels
into long lines.

What separates them is that **these manuals caption their tables and their drawings do not**.
Requiring `Table N-N`, `Replaceable Parts`, `Parts List` or `Stock Number` within the first six
lines caught 4 of 6 known tables and **0 of 5 known drawings**: poor recall, perfect precision,
which is the right trade for building a test set. With a rows check and a digit floor, that is
**6,206 pages across 243 documents — 5.5% of the library**.

## On genuine tables, Acrobat wins clearly

100 captioned table pages from 68 documents, at most two each, text layer removed by rasterisation.

| | Acrobat | ManualForge |
|---|---|---|
| Raw words | **58,178** | 48,029  (−17.4%) |
| **Content-bearing tokens** | **28,418** | 22,294  (**−21.5%**) |
| Pages led on | **49** | 5  (46 level) |

This is not a junk artefact. Acrobat's unique output is 28.0% one- and two-character tokens against
our 12.5% — its lowest junk rate of any round — and it still leads by a fifth on content.

## What we do find that they miss, and what that costs

Our unique tokens are **9.1% part numbers** against Acrobat's 1.4%: `0683-1035`, `1251-2035`,
`5041-0285`, `ct4-1/8-t0-1001-f`. Real content, genuinely recovered.

But the same sample shows what the metric cannot:

```
resistur-trmr    opto-isolatnr    besi5tor    i00opf    1bov
```

Those are `resistor-trimmer`, `opto-isolator`, `resistor`, `1000pF`, `160V`. Each one scores as
content under this measurement and each one is wrong. **So −21.5% is the optimistic reading.**

## The sequence of wrong conclusions, kept because the pattern matters

| Round | Sample | Result | What I concluded | Why it was wrong |
|---|---|---|---|---|
| 4 | 20 most digit-dense pages | Acrobat +8.2% | "tables are a class we lose" | Sampled 0.7% of the population, from the tail |
| — | sizing | 11 pages above 63% digits | "not a class, a dozen pages" | Right about the tail, wrong that it was the whole story |
| 5 | 100 stratified by digit share | ManualForge +6.7% | "loss confined to the tail" | **Also not tables** — same shape filter, mostly drawings |
| 6 | 100 captioned tables | **Acrobat −21.5%** | tables are a real class we lose | — |

Three selections in a row were built on the same unexamined assumption: that a page whose
*extracted text* looks like a table is a table. Each time the measurement was clean and the
population was wrong. The fix came from someone looking at the pages.

## What this means

**6,206 pages — 5.5% of the library, across 243 of 632 documents — are recognised materially worse
than Acrobat manages on the same pixels.** These are replaceable-parts lists, performance-test
tables and service sheets: the pages somebody consults a service manual *for*.

That is large enough to change the decision on #19. A second recognition path for table-shaped
pages was not worth arguing for eleven pages. For 6,206 it is.

Still unmeasured, and no round of this will settle it: whether either engine's text is *correct*.
Our own visible errors above are the argument for #6, which does not exist.
