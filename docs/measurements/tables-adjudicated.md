# Table pages against Acrobat, adjudicated (#22)

#22 said table pages are recognised "a fifth worse than Acrobat". That figure counted every
disagreement with Acrobat as our error. Looking at what the two engines read differently says
otherwise: on parts-list tokens, where a reading can be checked against the format it must have,
the disagreements are mostly Acrobat's mistakes.

10 Oct 2026, the shipped engine (PP-OCRv5 mobile, orphan-glyph rescue and stack splitting on), RTX
5070 Ti. The same 100 captioned table pages as before (`_compare/tableonly-test.pdf` against
`_compare/tableonly-acrobat.pdf`).

## The raw agreement

`tools/measure-single-characters.cs`, config `engine`:

| | Acrobat tokens |
|---|---|
| Matched by a word of ours at the same place | 34,047 (77.5%) |
| A word of ours is there, but reads differently | 8,188 |
| Nothing of ours there | 1,709 |

That is where 28 September left it (34,050). Most of the shortfall is disagreement about text both
engines found, not text we missed.

## Who is right when they disagree

The harness now writes every token with what lies under it (`tokens-NAME.tsv`). Parts lists are
made of tokens with strict shapes, so a disagreement can be judged by which reading has the shape:
an HP part number (`0698-7230`), a reference designator (`A6CR6`), a manufacturer code (`28480`) or
a value (`100VDC`). Of the 2,827 disagreements where at least one side has such a shape:

| | |
|---|---|
| Only ours well-formed | **1,916** (67.8%) |
| Both well-formed | 639 (22.6%) |
| Only Acrobat's well-formed | 272 (9.6%) |

It holds in every category: values 626 to 30, part numbers 436 to 53, designators 546 to 144,
manufacturer codes 308 to 45.

A shape is not proof, so a seeded sample of each group was looked at on the page:

* **Only ours well-formed:** ours right on 12 of 12. Acrobat's readings were `01eo-0JoJ` for
  `0180-0303`, `1qo1-002s` for `1901-0025`, `2IM80` for `28480`, `SOVDC` for `50VDC`.
* **Both well-formed:** ours right on 11 of 12, and one too faint to call. Acrobat read `28460` for
  HP's own `28480`, `0888-7252` for `0698-7252`, `A10CA3` for `A10CR3`.
* **Only Acrobat's well-formed:** this is where our real errors are. Six of the twelve are a column
  of repeated entries read as one word (`A2 A2 A2` as `NNNNN`, `A4` as `AAAAA` or `444AA`). Four
  are character confusions (`A8C8` as `ABC8`, `A51L7` as `451L7`, `A51Q2` as `A5102`, `MP23` cut
  to `MP`). In one, ours was right (`PD`, read by Acrobat as `P0`).

## What that leaves

* **Columns of repeated entries:** 121 tokens (0.3%), on 21 pages and mostly one column of `A2`s.
  The stack splitter (`TallStacks`) does not catch them, because the merged word is not tall
  enough to be taken for a stack.
* **Character confusions:** `8`/`B`, `A`/`4`, `Q`/`0` in designators. These are the recogniser's
  own errors, and no setting or model size moved them (`recognition-sweep-tables.md`,
  `server-models-on-tables.md`).
* **Nothing of ours there:** 1,709 tokens, most of them single characters (`l`, `1`, `b`), some of
  them Acrobat's own noise.

## Table-structure models

#22's leading idea was PaddleOcrNet's table-structure models (SLANet-plus, SLANeXt). Their
documentation settles it without a run. The structure model predicts the cell grid and
"distributes the supplied OCR lines into the cells (by box overlap)": the characters come from the
same detector and recogniser we already use. The per-cell detection model that would read cells on
their own is "not currently hosted … and not consumed". So a table model can rearrange what we read
into rows and columns, but it cannot read more of it.

## Conclusion

Against Acrobat on these pages, the deficit #22 was opened for is mostly Acrobat's. On the tokens
somebody searches a parts list for, our readings are well-formed about seven times as often when the
two disagree, and better by eye in every group but the one chosen to favour Acrobat. The residual
errors are real but small and specific.
