# The first accuracy numbers, from pages nobody had to transcribe

Phase 7 has been blocked since it was written on the same thing: a yardstick. Hand-correcting 9–12
pages is about three hours of somebody's attention, and until it happens every accuracy claim here
is a confidence score, which is the recogniser's opinion of itself.

This is a way round part of that. A born-digital page already carries a correct transcription of
itself — the characters the typesetter put in the content stream. Render it, recognise the render,
and score the recognition against the page's own text. Nobody reads anything.

```
manualforge truth --publisher --library <folder> --out <truth> --count 12
manualforge benchmark --truth <truth> --library <folder> --sweep
```

## What it says

Twelve pages, ten prose and two table, across eight manuals. Recognised on an RTX 3060 Ti.

| Configuration | CER | WER | WER unordered | Prose CER | Table CER | pages/min |
|---|---|---|---|---|---|---|
| 300 dpi, deskew + denoise | 6.3% | 7.0% | 2.8% | 5.3% | 17.5% | 65.1 |
| 300 dpi, no deskew | 6.3% | 7.0% | 2.8% | 5.3% | 17.5% | 77.3 |
| 300 dpi, no denoise | 6.3% | 6.9% | 2.7% | 5.3% | 17.1% | 80.8 |
| 300 dpi, neither | 6.3% | 6.9% | 2.7% | 5.3% | 17.1% | 77.5 |
| **200 dpi, both** | 6.3% | 6.5% | 2.7% | 5.4% | 16.9% | **96.5** |
| 400 dpi, both | 6.4% | 6.9% | 2.7% | 5.4% | 17.2% | 52.0 |

**Read the unordered column.** Ordered edit distance cannot tell "read the wrong characters" from
"read them in a different order", and a two-column page is read down one column by the extractor and
across the page by the recogniser. On one page here, 87% of the word error is ordering alone: it
scores 34.8% WER and 4.4% unordered.

### Three things it settles, for clean type

**Preprocessing earns nothing and costs a fifth of the throughput.** Deskew and denoise move the
error rate by at most 0.1 point — inside the noise of twelve pages — while costing 15 to 20% of
pages per minute. That is exactly what should happen: both exist for photographs of paper, and there
is nothing in a clean render to straighten or clean. **It says nothing about scans, which is what
they are for.**

**200 dpi is not worse than 300, and is 48% faster.** 400 dpi is no better and 20% slower. On
digital type, 200 dpi already gives the recogniser more pixels per character than it needs.

**A table is three times harder than prose.** 17% against 5.3%, which is the argument for splitting
by page kind rather than quoting one average — a table's columns, part numbers and units are where
the errors live.

## What it does not say, and must not be quoted as saying

**This is not accuracy on this library.** A clean render of digital type is a far easier read than a
1965 photocopy with halftone screening, bleed-through and a skew from the book's spine. These
numbers are the recogniser's **floor**: what it manages when the page is perfect. The real figure
for a scan can only come from hand-corrected scanned pages. That was attempted and abandoned, so it
remains unmeasured — issue #6 records why, and the README's *What is not measured* says what may
and may not be claimed from the numbers here.

**It does not answer the baseline-offset question.** `TextLayerOptions.BaselineOffsetFraction`
governs where the invisible text layer is written, not what the recogniser reads, so no benchmark of
recognised text can measure it. What it needs is a comparison of the written layer's baselines
against the page's own glyph baselines — which these pages could support, since their glyph
positions are exact, but which is a different measurement and not yet built.

**Some of the truth is imperfect, and knowingly so.** Extraction is not transcription: a
multi-column page comes back with its columns interleaved, and tight tracking produces `Vi sual`
where the page says `Visual`. Pages whose text is badly broken are filtered out — the letter-spaced
ones, the scanner boilerplate reprinted in every manual, and documents whose page sizes wander,
which is how a scan with the image dropped gives itself away. What survives still carries ordering
differences, which is the other reason to read the unordered column.

## How the pages were chosen

A page qualifies when it is in a document whose pages are all one size to within a point (a
scanner's crop wanders; a typesetter's does not), and when the page itself:

* decodes at least 900 characters, and at least half its drawn glyphs into characters;
* has no image over 2% of it — the criterion that separates type from a photograph of type;
* paints fewer than 40 paths, so no vector figure whose drawn labels the recogniser would read and
  be marked wrong for;
* uses no Type 3 font;
* passes the audit's ink comparison, which is the only test that proves nothing on the page is
  missing from its text;
* reads as written language rather than as fragments, and is not a page already taken.

A missing `/ToUnicode` is *not* disqualifying. That was the first criterion tried and it excluded
the entire library: most born-digital pages here carry a subset font without one and extract
perfectly through its standard encoding.
