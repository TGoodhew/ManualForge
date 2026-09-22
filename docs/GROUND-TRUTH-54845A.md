# Ground truth: `54845A Programmer.pdf`

Agilent Infiniium Programmer's Quick Reference Guide, pub. 54810-97065. 110 pages, 3.1 MB,
FrameMaker+SGML 5.5P4f through Acrobat Distiller 4.05. No images anywhere in the file, 458
extractable characters per page, and chapter 2 — the entire command reference — drawn as vector
graphics.

Every string below was read off a rendered page by eye. Before the repair, none of them could be
found in the library that contained them.

The document's own page numbers are chapter-relative. **Physical page = document page + 26** for
chapter 2, confirmed against the printed folio on each page.

## Result

| | Ground-truth strings returning the correct page |
|---|---|
| Before the repair | **2 of 33** — and only because command syntax is now read as notation, which lets `:CHANnel<N>:RANGe` match the typeset heading `CHANnel Commands`. The diagrams themselves matched nothing. |
| After the repair, development library | **33 of 33** |
| After the repair, **full 584-document library** | **27 of 33**, 21 of them in the first ten |

### The two figures differ because the corpus does, and the smaller one flatters

The 33 of 33 was measured on the library this was developed against. Repeated on the full
584-document, 104,468-page library on 18 September 2026 — same code, same repair, same queries —
the score is **27 of 33** at `--limit 25`.

Nothing regressed. Every one of the six that drop out is **ranking, not retrieval**: the recovered
text is in the index and it matches. Asked for more results, the correct page comes back for five of
them, and the sixth is the one this document already singles out.

| Query | Physical page | Rank in the full library |
|---|---|---|
| `:CHANnel<N>:DISPlay` | 40 | 39 |
| `:CHANnel<N>:INPut` | 40 | 40 |
| `ATTenuation` | 41 | 41 |
| `:CHANnel<N>:PROBe` | 40 | 47 |
| `PROTection` | 42 | 108 |
| `:WAVeform:SOURce` | 106 | beyond 200 |

The mechanism is the one described under "The two that rank low" below, and corpus size is what
sharpens it: bm25 prefers a prose page that uses the query's words many times over the syntax
diagram that *defines* the command, and a larger library simply holds more such prose pages. The
single-token queries — `ATTenuation`, `PROTection` — are worst hit, because a bare instrument term
appears in hundreds of manuals.

**So quote 27 of 33 for this library.** The honest summary is that the repair solved retrieval and
did not solve ranking, and that any score from this table is a property of the corpus it was
measured on as much as of the code.

### Measuring it yourself

```
./tools/measure-ground-truth.ps1 -Label "<what changed>" -Out docs/measurements/<name>.md
```

That runs all 33 strings, scores where the correct page ranked, and writes a report. The strings
live in `tools/ground-truth-54845A.tsv`; the reports are in [`docs/measurements/`](measurements/),
one per run, dated, and not hand-edited. Run against the index as it stands on 2026-09-22 it
reproduces the table below exactly — 27 of 33 at 25, 21 in the first ten — which is the only reason
to trust it with an "after".

Two things to know before reading any number it prints:

* `search` prints ten results by default, so a correct answer at rank 16 — which this table
  documents — scores as a miss unless `--limit` is raised. The harness asks at 200 and scores the
  first ten, the first 25 and found-at-all separately, because those three answer different
  questions: what a user sees, what the docs quote, and whether retrieval worked at all.
* An index rebuild overwrites the control. Copy `_Originals/manualforge-index.db` aside before a
  repair and pass `-Index <that copy>` afterwards, and the "before" is still measurable when the
  argument starts.

`--no-repairs` cannot be used for a before/after here: it applies when the index is **built**, not
when it is queried, so passing it to `search` changes nothing. The "before" figure above comes from
running the queries against the index as it stood before the repair was merged.

## The table

`[OCR]` means every word that matched was recovered from the rendered page; `[part OCR]` means the
query matched both the PDF's own text and the recovered text.

| Doc page | Physical | Query | Rank | Source |
|---|---|---|---|---|
| 2-14 | 40 | `:CHANnel<N>:INPut` | 2 | part OCR |
| 2-14 | 40 | `:CHANnel<N>:INPut AC DC LFR1 LFR2` | 1 | part OCR |
| 2-14 | 40 | `DC50\|DCFifty` | 2 | OCR |
| 2-14 | 40 | `BWLimit is not available on 54846A, 54845A, and 54835A` | 1 | part OCR |
| 2-14 | 40 | `:CHANnel<N>:DISPlay` | 5 | part OCR |
| 2-14 | 40 | `:CHANnel<N>:OFFSet` | 2 | part OCR |
| 2-14 | 40 | `:CHANnel<N>:PROBe` | 5 | part OCR |
| 2-15 | 41 | `EADapter` | 1 | OCR |
| 2-15 | 41 | `ECoupling` | 1 | OCR |
| 2-15 | 41 | `EGAin` | 1 | OCR |
| 2-15 | 41 | `EOFFset` | 1 | OCR |
| 2-15 | 41 | `ATTenuation` | 1 | OCR |
| 2-15 | 41 | `SKEW` | 2 | OCR |
| 2-16 | 42 | `:CHANnel<N>:RANGe` | 1 | part OCR |
| 2-16 | 42 | `:CHANnel<N>:SCALe` | 2 | part OCR |
| 2-16 | 42 | `:CHANnel<N>:UNITs` | 3 | part OCR |
| 2-16 | 42 | `PROTection` | 2 | OCR |
| 2-57 | 83 | `:TIMebase:SCALe` | 1 | part OCR |
| 2-57 | 83 | `:TIMebase:RANGe` | 1 | part OCR |
| 2-57 | 83 | `:TIMebase:REFerence {LEFT\|CENTer\|RIGHt}` | 1 | part OCR |
| 2-59 | 85 | `:TRIGger:MODE {EDGE\|GLITch\|ADVanced}` | 1 | part OCR |
| 2-60 | 86 | `:TRIGger:EDGE:SOURce {CHANnel\|AUX\|LINE\|EXTernal}` | 1 | part OCR |
| 2-60 | 86 | `The AUX command is only available on the 54815/25/35/45/46` | 1 | part OCR |
| 2-60 | 86 | `The EXTernal command is only available on the 54810/20` | **12** | part OCR |
| 2-79 | 105 | `:WAVeform:FORMat {ASCii\|BYTE\|WORD\|LONG}` | 1 | part OCR |
| 2-79 | 105 | `:WAVeform:BYTeorder {MSBFirst\|LSBFirst}` | 1 | part OCR |
| 2-80 | 106 | `:WAVeform:SOURce` | **16** | part OCR |
| 2-80 | 106 | `XINCrement?` | 1 | OCR |
| 2-80 | 106 | `XORigin?` | 1 | OCR |
| 2-80 | 106 | `XREFerence?` | 1 | OCR |
| 2-80 | 106 | `YINCrement?` | 1 | OCR |
| 2-80 | 106 | `YORigin?` | 1 | OCR |
| 2-80 | 106 | `YREFerence?` | 1 | OCR |

## The two that rank low, and why

These are the two that ranked low on the development library. On the full library six do, for the
same reason at greater strength — see "The two figures differ" above. The analysis below is the
mechanism; the larger corpus only supplies more of what it describes.

**`The EXTernal command is only available on the 54810/20` — rank 12.** The note is printed
verbatim on every continuation page of the TRIGger syntax diagram, so pages 88 and 100–103 are
equally correct answers and bm25 prefers them. Page 86 is returned, twelfth.

**`:WAVeform:SOURce` — rank 16.** The measuring library for this check holds the TDS3014B
programmer's manual as well, 418 pages that use the words "waveform" and "source" in prose a great
many times. bm25 scores on term frequency and page length, and neither question is "are these two
words about each other". The syntax diagram that defines the command, where they sit one above the
other, is returned sixteenth.

A proximity re-rank was written and measured against this table to see whether it would fix that.
It made things worse — 31 correct pages in the first ten became 26 — because on a syntax diagram
the levels of a command are *not* adjacent; they are separated by the argument labels between them,
while a prose page that mentions both words in one sentence scores highly. It was removed. The
measurement is recorded here so the next person does not spend the afternoon rediscovering it.

## Reproducing this

```
manualforge doctor  <library>      # flags 75 of the 110 pages
manualforge repair  <library>      # OCRs those 75 and keeps what the text layer missed
manualforge index   <library>      # merges the result in - hours, not minutes, on a large library
manualforge search  ":WAVeform:BYTeorder {MSBFirst|LSBFirst}" --library <library> --limit 25
```

The index step is the slow one and it is easy to underestimate. Merging a 20-document repair meant
re-extracting 29 documents and 15,146 pages at 34 pages/min: **7.5 hours**. The indexer skips
documents whose content and supplement hashes are unchanged, so only the repaired ones are redone —
but repaired documents tend to be the large ones, which is exactly why the total is not small.

To see what the audit saw on any one page:

```
manualforge doctor "54845A Programmer.pdf" --explain 40 --dump page40.png
```

## Two named regressions

Both were raised by the caller who hit the original miss — the one that read these pages off a
110 dpi render and wrote a working instrument driver from them — rather than by whoever wrote the
detector. Both are load bearing and both are easy to get subtly wrong.

### The two notes on 2-60 must not be conflated

Page 2-60 carries two sibling notes, side by side:

```
The AUX                                     The EXTernal
command is only                             command is only
available on the                            available on the
54815/25/35/45/46.                          54810/20.
```

Recognisers return lines in detection order, which runs across the page, so the first repair wove
them together:

```
The AUX CHANnel channel_number
command is
only available AUX
on the The EXTernal
54815/25/35/45/46. LINE command is only
available on the
54800s61 EXTernal 54810/20.
```

Every token is present, so search never noticed. But `read_manual_page` hands that text to something
that will answer from it, and the answer names the wrong trigger source for the instrument — the
same class of failure as the original bug, reached by a different route.

Fixed — at the second attempt. Ordering the recognised *lines* changed nothing, because the
recogniser had already merged the two notes into single detected lines: `The AUX CHANnel
channel_number` arrives as one box. Each detected line is now broken into runs at internal word gaps
wider than two line heights, and the reading order is applied to those; see `ReadingOrder` and
`PageRepairer.TextRuns`. The note pair is a literal test fixture in `ReadingOrderTests`.

What comes out now:

```
The AUX                     The EXTernal
command is                  command is only
only available              available on the
on the                      EXTernal
54815/25/35/45/46.          54810/20.
```

Each note is contiguous and ends with its own model list, which is the fact that was inverted before
and the one an instrument driver depends on.

**What remains is not cosmetic, and is recorded as what it is.** One diagram label, `EXTernal`, still
falls inside the second note, because that bubble sits in the note's own column band. *That* instance
is harmless only because the label repeats a word the sentence is already about — which is luck about
which bubble sits where, not a property of the method. The bubble beside it says `AUX`, and had that
one landed there the text would have read as a relationship the page does not state: precisely the
failure class this work exists to remove, reintroduced by geometry rather than by logic.

Two fixes were tried and both cost more than they bought:

- **Narrowing the column gutter** separates the label, and starts reading close-set tables down their
  columns — mis-pairing every row, silently and plausibly. See the threshold check below.
- **Grouping lines into paragraphs before ordering** fixes the label and does the same thing to
  tables, for the same reason: a table's columns are left-aligned and consecutively led, which is
  indistinguishable from a paragraph by alignment alone. The synthetic table fixture caught it
  immediately.

The fix at the right level is to tell a figure label from prose structurally — by whether a drawn
path encloses it — which the page's vector paths would support and which nothing here does yet. That
is a scoped piece of work, not a tuning change, and it is not done. Until it is, recovered text on a
figure page can contain a stray label mid-note; the mitigation in practice is that runs are emitted
one per line, so what a reader sees is a fragment list rather than a flowing sentence.

### The `:CHANnel<N>:INPut` arguments must survive intact

`AC | DC | LFR1 | LFR2 | DC50|DCFifty`. `LFR1`, `LFR2` and `DCFifty` are exactly what a
dictionary-assisted recogniser would "correct" into words, and `DC50` is where an `0`/`O` or `5`/`S`
confusion lands. All five come back exactly, at 99% confidence on that page — there is no language
model in the recognition path to disable, which was checked rather than assumed.

## Case

Searching `BYTeorder`, `byteorder` and `BYTEORDER` all return page 105. The stored text reads
`BYTeorder`, and no lower-cased form of it exists anywhere in the index. That distinction is load
bearing: SCPI documents its accepted short forms by capitalisation, so `BYTeorder` says `BYT` is
accepted and `byteorder` says nothing at all.
