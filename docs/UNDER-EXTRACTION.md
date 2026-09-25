# Under-extracted PDFs: finding them, repairing them, and saying so

A PDF can carry a text layer that is **present but incomplete**, and every "does this file need
OCR?" check that asks "is there a text layer?" answers no and is wrong.

This document describes the check that asks a better question, the numbers it uses and where those
numbers came from, what the repair does and does not touch, and why a search result that finds
nothing no longer claims that nothing is there.

---

## 1. The failure

Two generations of documentation sit in this library, and they fail in opposite directions.

| Generation | Structure | Before this work |
|---|---|---|
| Scanned paper | Page images, no text layer | Correct — detected by the classifier, OCR'd |
| Desktop-published (FrameMaker, Interleaf, Ventura → Distiller) | Real text for prose; **figures, syntax diagrams, pin-outs and some tables drawn as vector graphics** | **Broken — passed the "has text" check and was indexed as complete** |

In the second case the prose extracts perfectly and the *technical content* does not. Command
syntax diagrams, schematic labels, connector pin-outs and timing diagrams are exactly the material
that gets drawn rather than typeset, and exactly what anybody searches a service manual for.

The failure is silent and it inverts into a false negative. A search returns nothing, and the
person searching concludes the manual does not cover the topic.

### The case that started it

`54845A Programmer.pdf` — Agilent Infiniium Programmer's Quick Reference Guide, pub. 54810-97065.

- 110 pages, 3.1 MB, `Creator: FrameMaker+SGML 5.5P4f`, `Producer: Acrobat Distiller 4.05`.
- 50,332 extractable characters over 110 pages — **458 per page**, against 1,500–3,000 for a normal
  typeset page in this corpus.
- `pdfimages -list` returns **no images at all**, so every scan-detection heuristic waves it through.
- The extracted text is front matter and parameter tables. Chapter 2 — the entire command
  reference, the reason the document exists — is vector diagrams and extracts as nothing.
- Rendered at 110 dpi the same pages are cleanly legible, small annotation included.

---

## 2. Detection: `manualforge doctor`

```
manualforge doctor <folder>              audit a library; changes nothing
manualforge doctor <file.pdf> --detail   per-page numbers
manualforge doctor <file.pdf> --explain <page> --dump out.png
manualforge doctor <folder> --review 20 --into <folder>
```

The audit works **per page**, not per document. Most affected documents are mixed — prose fine,
figures empty — and a per-document verdict either re-OCRs good prose or skips bad figures.

### Two stages, because rendering is the expensive part

**Stage one** reads the page and costs nothing beyond the parse that text extraction needs anyway.
It counts glyphs drawn, characters decoded, path-painting operations against text-showing
operations, images and their coverage, and looks for Type 3 fonts and embedded fonts with no
`/ToUnicode`. It decides whether the page is worth rendering.

**Stage two** renders the page and compares the ink against the extracted glyph boxes. This is the
measurement that settles it, and keeping it behind a gate is what makes a hundred-thousand-page
audit finish in minutes rather than hours.

The gate has three limbs, and a page passing any of them is rendered:

| Limb | Threshold | What it catches |
|---|---|---|
| Few characters | under 900 extracted | a page that is mostly drawing |
| Many paths | 40 or more painting operations | a syntax diagram, schematic or pin-out |
| A large image | covering 10% or more of the page | lettering inside a photograph or screenshot |

**The third limb was missing until 24 September 2026, and its absence was the larger half of the
recall problem.** A page with plenty of prose and a raster figure whose labels do not extract
satisfies neither of the first two — too many characters for one, too few paths for the other — so
it was never rendered and could not be flagged whatever the thresholds said. That is an extremely
common shape in illustrated manuals, and it is `clean-33` in `docs/UNDER-EXTRACTION-SAMPLE.md`:
1,974 characters of prose, 2 path operations, and `Receptacle`, `Socket` and `G6.35 Bulb` sitting
unextracted in the figure. With the limb it is flagged.

The cost is smaller than it sounds. Pages with no text layer at all are decided before the gate, so
a library of scans does not suddenly render end to end. Measured on two folders of this corpus, the
limb added 7% and 12% more flagged pages for no meaningful change in audit time.

### The deciding signal

Render the page, count the pixels carrying a mark, and subtract the ones an extracted glyph
accounts for. A page with substantial ink and little text coverage is the signature. It does not
fire on a legitimately sparse page — a chapter opener — because a sparse page has little ink either.

Ink alone would be far too blunt. A schematic, an exploded parts diagram or a full-page waveform is
mostly ink and mostly has nothing to recover, and flagging all of those would trigger a
hundred-thousand-page re-OCR and teach everybody to ignore the report. So the unaccounted-for ink
is then sorted by **shape**, and only clusters the size, aspect and density of lettering are
counted. A page is flagged only when both agree: enough unaccounted-for ink, **and** enough of it
shaped like text.

### Every threshold, and where it came from

All of these are fields on `DoctorOptions`, all are overridable from the command line, and all
carry the same reasoning in their XML docs.

| Threshold | Default | Flag | Why this number |
|---|---|---|---|
| Render below characters/page | 900 | `--render-below-chars` | Below the 1,500–3,000 a normal typeset page yields here, well above the 458/page the 54845A guide manages. Only decides what gets *looked at*; a page over it is still rendered by the rule below. |
| Render at/above path ops | 40 | `--render-above-paths` | Page furniture — a header rule, a footer rule, a table's ruling, a logo — costs well under 40 painting operations. A syntax diagram costs hundreds; page 40 of the 54845A guide paints 183. |
| Render at/above image coverage | 0.10 | `--render-above-image` | The third limb, added 24 Sep 2026. About a quarter-page figure: below that there is not room for enough lettering to be worth a render, above it one sits comfortably. Set it to 1.0 for the two-limb gate the 17 September audit was made with. |
| Audit render resolution | 150 dpi | `--audit-dpi` | Not the OCR resolution. It only has to make ink countable and glyph-sized blobs separable; at 150 dpi a 6 pt annotation is still twelve pixels tall and does not merge with its neighbour. |
| Ink level | 200 of 255 | — | PDFium antialiases vector strokes, so a hairline lands as a band of greys. Counting every off-white pixel would let antialiasing dominate the ink fraction on a page whose only mark is a header rule. |
| Text box padding | 1.5 pt | — | Absorbs antialiasing, hinting and descenders without letting one line of text claim the ink of the line 10–12 pt below it. |
| Text box padding | 0.6 em sideways | — | **Measured, see below.** |
| Unaccounted-for ink | 0.002 of the page | `--uncovered-ink` | Two pixels in a thousand. Clean prose leaves well under that once its glyph boxes are credited; the residue is rules and page furniture. A drawn figure leaves several times it. |
| Glyph-like clusters | 40 | `--min-blobs` | The precision half, and the reason a plain schematic does not fire. Roughly six or seven short words: below that the recoverable text is a figure number; above it there is a caption, a pin-out or a diagram's worth of labels. |
| Blob height | 2.5–30 pt | — | Below, a speck; above, a bubble, a box or a rule. Display headings in this corpus top out around 24 pt. |
| Blob width | ≤ 30 pt | — | Wider is a connector line, an arrow run or a box edge. |
| Blob fill | ≥ 0.18 of its box | — | A letter fills 25–60% of its bounding box; a diagonal connector or an L-shaped corner fills far less. |
| Blob aspect | ≤ 8:1 either way | — | An `l` is about 1:6 and an `m` about 1.2:1. A long rule fragment is 40:1. |
| Rule-segment run | 3 | `--rule-run` | A *short* rule fragment passes every shape test above — between two close table rows it is a few pixels each way, solid, aspect near one. Position gives it away: three or more hairline blobs of identical width sharing one x is a ruled line with gaps in it, not text, because glyphs differ in width even in a left-aligned column. Only ever removes flags. |
| Rule-segment width | ≤ 1.5 pt | — | Needed alongside the run test, which alone would take a column of left-aligned text with it. Table rules here are half a point to one point; the narrowest glyph that survives the height and fill thresholds is several points of ink wide at 150 dpi. |
| Characters per blob | 0.85 | — | For the recoverable-text estimate only. Slightly below one because touching characters merge and some blobs are arrowheads. Labelled as an estimate everywhere it is shown. |
| Raster page image coverage | 0.2 | — | A flagged page whose missing lettering sits under an image this large is a scan or a screenshot, not a drawing. Nowhere near a boundary: a scanned page is one image covering 90%+, a vector diagram has none. |
| Document flagged share | 0.10 | — | Decides whether the *report* calls a document broken, scanned-and-gappy, or merely illustrated. See below. |

### Two thresholds that came from being wrong

Both of these were found by looking at the diagnostic image rather than by reasoning, and both were
firing on hundreds of perfectly good pages.

**The vertical text box is built from the baseline and point size, not from the font's metrics.**
The first run flagged 43 of the 418 pages of the negative control. The diagnostic image showed the
unaccounted-for ink was the *tops of the bold headings*: those fonts report a glyph rectangle that
stops below their own ascenders. A box running from 0.30 em below the baseline to 0.95 em above it
does not depend on the metrics being right, and is about one line of text, so a line still cannot
claim the ink of the line below. 43 pages → 6.

**Text boxes are padded 0.6 em sideways.** The library's own OCR'd scans put their invisible text
layer on the box the recogniser detected, and a CRNN's per-character timesteps consistently start
about one character late — so the run begins a character to the right of the ink it describes, and
the first letter of every word had no glyph over it. On `200CD.pdf` that alone accounted for most
of the unexplained ink on seven pages, and would have done the same on every OCR'd scan in the
corpus. 0.6 em is about one character's advance; growing a text box sideways costs almost nothing
in precision, because what lies left and right of a word on its own line is almost always more of
the same line.

### Drawn, or photographed

A flagged page is one thing; *why* its content is missing is another, and the two have different
remedies and wildly different scale. Every flagged page is therefore also classified:

- **`Drawn`** — the missing content is vector graphics on the page: a syntax diagram, a pin-out, a
  schematic label. It never had a text layer and no amount of re-OCRing the document as a whole
  would have found it. **This is the failure the audit exists for.**
- **`Raster`** — the missing content is lettering inside an image: a scanned page whose OCR missed
  it, or a screenshot pasted into a digital document. Real, and worth recovering, but the existing
  classify-and-OCR path is already the right tool.

The test is whether the page carries an image covering at least 20% of it. That number does not
need to be near a boundary: a scanned page is a single image covering upwards of 90%, and a vector
syntax diagram has no image on the page at all.

This distinction was added after the first corpus-wide run, which flagged 220 of the first 241
documents. Almost all of them were scanned service manuals whose 1990s OCR had missed the lettering
on their schematics and tables — a true finding, and the wrong one to put at the top of a report
that exists to surface the dozen documents whose content was never text at all. Reported as one
list, the thing being looked for was buried under two orders of magnitude of something else.

### Pages that are not this finding

Two verdicts are reported separately and are **not** counted as flagged, because they are not news
and burying the finding under tens of thousands of them would make the report useless:

- **`NoTextLayer`** — no text at all and a scanned image where the text should be. The existing
  classify-and-OCR path already owns this.
- **`Undecodable`** — glyphs drawn that almost nothing decodes: a custom encoding with no
  `/ToUnicode`, or text converted to outlines. The classifier already calls this
  `UnreadableTextLayer`, and it needs a different repair.

### Document verdicts

Almost every illustrated manual has a handful of pages whose figure labels are drawn rather than
set. Saying so about all of them would drown the finding that matters, so the report separates:

- **`UnderExtracted`** — at least 10% of its pages are flagged **and drawn**. This is the list to
  act on, and the one `repair` works through by default.
- **`ScannedGaps`** — at least 10% of its pages are flagged and raster. A scanned document whose
  existing OCR missed a substantial part of its lettering. A real gap, a different one, and on this
  corpus a much larger one; `repair --include-scans` does these.
- **`Figures`** — some flagged pages, neither share reaching 10%. Real findings, and small ones.
- **`Sound`** — nothing flagged.

The two test cases sit either side of the line by a wide margin: 68% for the 54845A guide against
1.7% for the TDS3014B. Anywhere from about 3% to about 50% would separate them, so 10% is chosen
for having the most room either side rather than for fitting them tightly. It only decides how the
report reads — flagged pages are still listed and still counted whatever their document's verdict.

### Signals that are reported but never decide

- **Type 3 fonts and embedded fonts with no `/ToUnicode`.** Named in the report because they
  explain a page, but not used to flag: a simple font with no `/ToUnicode` is only a problem when
  its encoding is not one a reader knows.
- **The outline cross-check.** Headings the document's own bookmarks place on a page, checked
  against that page's text. The PDF outline names a page directly, so no printed folio has to be
  parsed and no chapter-relative page number has to be resolved — both of which are guesses, and a
  guess inside a detector is a false positive waiting to happen.

  A printed-table-of-contents cross-check was considered and is not implemented, because on the
  motivating document it does not fire: the TOC entry `CHANnel Commands 2-14` resolves to a page
  whose heading *does* extract. Only the diagram below it does not. The signal would have cost a
  folio parser and a page-label mapping to catch nothing the ink comparison does not already catch.

### Showing the working

```
manualforge doctor "54845A Programmer.pdf" --explain 40 --dump page40.png
```

writes the picture the detector worked from: pale grey for ink an extracted glyph accounts for,
black for ink nothing accounts for, and a red box round every cluster counted as lettering. A
detector whose reasoning cannot be looked at is one nobody will trust enough to act on, and every
threshold correction above came from looking at one of these.

---

## 3. Repair: `manualforge repair`

```
manualforge repair <folder>            OCR the flagged pages, worst documents first
manualforge index <folder>             merge the result into the search index
```

Only flagged pages, and by default only the drawn ones. The corpus is ~100,830 pages and most of
them are fine; re-recognising a page whose text layer is correct would replace correctly-spelled,
correctly-spaced typesetting with a machine's reading of a picture of it. `--include-scans` also
redoes the raster pages, which on this corpus is hours of GPU time for a different problem — worth
doing, and worth choosing to do.

### What it cost, on the whole corpus

Both passes have now run. `--include-scans` over the whole outstanding backlog on 24 September 2026:
**9,510 pages across 500 documents in 172 minutes at 55 pages/min**, recovering 868,303 words at
80.4% mean confidence, with nothing failed and nothing stale. The audit reads 11,008 flagged and
11,008 repaired.

Merging it into the index took **4.7 minutes**. That is worth saying plainly because the first
repair's re-index took 7.5 hours and the two numbers describe the same operation: the cost is the
handful of genuinely text-heavy documents, not the page count, because extracting an image-only
scan costs almost nothing. Indexing now extracts several documents at once, which measured 2.1× on
this library.

Of the 9,510 pages, 167 recovered no text at all — 1.8%, against 3.7% in the first pass.

### Asking what happened to one document

```
manualforge doctor "<file.pdf>" --report      every flagged page, and what the repair got back
```

The library report answers "what should I repair next". This answers "what happened to this one",
which is the question a page that recovered nothing leaves behind — and the counts cannot tell a
page that was rendered, recognised and came back empty from one that was never looked at.

It found what the 40 empty pages of `8591e Calibration Guide.pdf` really are. They are not
consecutive, as had been assumed: singles and runs of four from page 394 to 860. `--explain --dump`
on two of them shows ruled performance-test record forms whose every word extracts correctly, where
the only ink nothing accounts for is the table borders, chopped into glyph-sized pieces by their own
intersections. **The repair recovering nothing there is the right answer**, and the flag is the
false positive — issue #11.

### Merge, never replace

Recognised words whose box lands under the embedded text layer are dropped — that text is already
there and already right. What is kept is what the embedded layer missed. The test is geometric,
over a coarse grid of the rendered page, because "what the text layer missed" is a question about
where things are.

Proven by diffing rather than asserted: re-indexing the two test documents with and without the
merge leaves **35 of 110 pages of the 54845A guide and 411 of 418 pages of the TDS3014B manual
byte-identical**, and every page that did change begins with exactly the bytes it had before.
Nothing was replaced; text was only added.

### Nothing is written to any PDF

The recovered text goes into `_Originals/manualforge-doctor.db` and is merged at index time. This
is the whole safety argument: a page that already carries a text layer must never be given a second
one, because an extractor sorts the two together by position and returns them interleaved character
by character — `Broadband` comes back as `BBrrooaaddbbaanndd` — leaving the document less
searchable than it was.

### OCR settings this corpus needs

- **Case is preserved exactly.** SCPI documents its abbreviations by capitalisation: `BYTeorder`
  means `BYT` is the accepted short form, `CHANnel` means `CHAN`. Nothing in this pipeline
  lower-cases, and the index stores the text as recognised. Search is case-insensitive because the
  FTS5 tokeniser folds case at query time; storage is not.
- **Punctuation is syntax.** `:`, `<`, `>`, `|`, `?`, `[`, `]`, `{`, `}` and `,` all carry meaning
  and all are in the recogniser's character dictionary. Nothing strips them.
- **There is no dictionary or language correction to disable.** PaddleOCR's recogniser is a CRNN
  with CTC decoding over a fixed character dictionary; it has no language model, and
  `RecognitionOptions` has no spell-check to turn off. `LFR1`, `DCFifty`, `XINCrement`, `MSBFirst`
  and `WMEMory` come through as written. This was checked rather than assumed.
- **Deskew and despeckle are switched off for repair.** They exist for photographs of paper. These
  pages are rendered from vector drawing instructions and are already straight and already clean;
  deskewing a straight page can only rotate it, and despeckling erodes the 4 pt annotation on a
  syntax diagram, which is the text the exercise is trying to recover.
- **Resolution is chosen per page, not globally.** From the height of the smallest lettering the
  audit found: 300 dpi where the missed text is 9 pt or more, 400 dpi below that, 600 dpi below
  7 pt. A blanket 600 dpi would quadruple the work for the pages that did not need it.
- **Colour, not greyscale.** A screenshot of an instrument display is often light text on a dark
  field, and desaturating it costs the contrast the recogniser needs.

### Reading order, and the page that needed two goes at it

Recognised text is assembled in reading order, not detection order, and the difference is
semantic rather than cosmetic.

Page 2-60 of the 54845A Programmer's Guide carries two sibling notes side by side — "The AUX command
is only available on the 54815/25/35/45/46" and "The EXTernal command is only available on the
54810/20". Taken in detection order they interleave line by line, and the result reads as though AUX
were the command restricted to the 54810/20. Every token is still present, so the index never
notices; but `read_manual_page` hands that text to something that answers from it, and the answer
names the wrong trigger source for the instrument. The same failure as the original bug, reached by
a different route.

The first attempt at a fix — a recursive XY-cut over the detected lines — did not work, and why is
worth recording. The recogniser's own line detection had already merged the two: it returns
`The AUX CHANnel channel_number` as a single line, the note's words and the diagram's label inside
one box. Reordering lines cannot unpick a line that is itself wrong.

So the assembly happens at one level lower. Each detected line is first broken into **runs** at
internal word gaps wider than two line heights — a word space is about a third of a line height, so
that is well clear of one and well under a real column gutter — and the reading order is then
applied to the runs. Splitting one word too early costs a line break nobody can see; splitting one
too late costs a sentence that says the opposite of the page.

### The threshold that had to not fire

Cutting columns apart risks a worse regression than the one being fixed: read a table down its
columns instead of along its rows and every row is mis-paired, silently and plausibly.

This project has a documented instance of exactly that. `8340b User.pdf` Table 3-2 is the HP 8340B's
HP-IB command table, two narrow columns with a freely wrapping description, and `pdftotext -layout`
drifts it by a row at the first wrapped cell — pairing `cs` with "CW frequency" and `CWdt` with
"Delta frequency" from there on. A sibling project carries a standing rule to use `-raw` and never
`-layout` because of it.

It is the hardest case in this library for a column cut to get right, so it is the one the threshold
was checked against:

```
manualforge doctor "8340b User.pdf" --reading-order 62
```

runs the real ordering over that page's real geometry and prints what comes out. It reads along the
rows, correctly paired, matching `-raw`. Two isolated glyphs — a lone `-` and a lone `=` — land on
their own lines, which is the "split one word too early" case and costs nothing.

Worth noting that the page is not flagged by the audit at all: its text layer extracts the table
correctly, so the repair never touches it. The threshold check matters for any similar table that
*does* land on a flagged page.

### From the desktop application

`manualforge repair` has a counterpart in the application's **Doctor** tab, and the split between
them follows the cost. Reading an audit is a database open, so the tab shows whatever
`manualforge doctor` last wrote without re-running anything; auditing one folder from the tab is
offered because it takes seconds, while a corpus-wide audit stays a command-line job.

Recovering the text is where a window earns its place. The tab lists the findings with a tick box
per document — drawn ones ticked, scanned ones not — runs the OCR with live per-page word counts, and
stops on request without losing what it already recovered. It will also draw the diagnostic picture
for any page, so the decision to spend twenty minutes of GPU time on a manual can be made by looking
at it rather than by trusting a number.

### Resumability

Repair is opt-in and resumable. Each page's recovered text is stored against the source file's
content hash; a page already done is skipped, and a document whose file has changed since the audit
is refused rather than repaired against stale page numbers.

---

## 4. Telling the truth in search results

### The claim that was wrong

A search that found nothing used to answer:

> The index covers 575 manuals and 100,830 pages, so this is a **real absence rather than a
> truncated search** — though a manual that was never OCR'd has no text to match.

The caveat covers the wrong case. It anticipates "never OCR'd", which is the *detectable* failure.
It does not anticipate "has a text layer, was therefore never OCR'd, and the text layer is missing
most of the content" — which is undetectable from the outside and is what actually happened. The
54845A guide was in the library the whole time its commands were being reported as absent.

The claim is now conditional on the audit:

- **Audit never run** — the result says so, explains the failure mode in one paragraph, and says a
  miss is unproven.
- **Audit run, findings outstanding** — the result gives the number of pages whose text no search
  can reach and names the worst-affected documents.
- **Audit run, everything repaired** — the claim becomes honest and is made, with the one caveat
  that remains: a scan that was never OCR'd has no text to match.

### Hits say where their text came from

A result whose match came from recovered text is marked `[OCR]`, or `[part OCR]` when the page also
carries the PDF's own text and the query matched both. The mean recogniser confidence is given, and
so is a note about the character confusions this genre suffers from — `0`/`O`, `1`/`l`/`I`, `5`/`S`,
`8`/`B` — which land directly in model numbers and command names. That matters when the retrieved
string is about to be sent to a real instrument.

Attribution is per hit, not per page: a page can be mostly typeset prose and still have matched on
a recognised diagram label.

### `library_status` accounts for the gap

It used to report "579 PDFs present, 575 indexed" and advise running the indexer. For a file that
cannot be opened at all that advice produces the same two numbers for ever while the file stays
invisible to search. It now names every unindexed file, says why, and says whether re-running the
indexer would fix it or whether it needs a person.

---

## 5. Command syntax as a query

Command syntax is routinely copied out of a manual and pasted into a search box. As a phrase it
matches nothing, because a syntax diagram draws `:TRIGger`, `EDGE` and each alternative in separate
boxes that are nowhere near each other in reading order.

Read as the notation it is, the same string is a good query:

| Written | Read as |
|---|---|
| `:` between levels | all must appear |
| `{a\|b\|c}`, `[a\|b\|c]`, bare `a\|b` | any one will do |
| `<N>`, `<NR3>`, `<value>` | a placeholder; dropped |
| `-` inside a token | part of it — `HP-IB` and `08340-60019` stay whole |

A query with none of that in it behaves exactly as before.

Two bugs in the existing query preparation were found while testing this, and both silently
returned nothing:

- **Operators were matched as substrings.** `WORD` contains `OR`, `COMMAND` contains `AND` and
  `NOTE` contains `NOT`, so any query containing one of those uppercase words was handed to FTS5 as
  a raw expression, where it is a syntax error. Operators are now matched as whole tokens.
- **Juxtaposition is not a general AND in FTS5.** It joins phrases into a phrase list and does not
  reach across a parenthesised group, so `"TRIGger" "EDGE" ("CHANnel" OR "AUX")` matched nothing at
  all. The operator is now written out.

A third was found later, by a script quoting phrases off repaired pages:

- **An operator needs something on both sides of it.** Matching whole tokens fixed `COMMAND` and
  `NOTE`, but not a phrase that genuinely ends in one. `FIT BOTTOM EDGE UNDER LUGS AND`, copied off
  a page of the E4418B CLIP, went to FTS5 as an expression with nothing to the right of the `AND`
  and came back as `fts5: syntax error near ""` — an error message where the page should have been.
  These manuals are lettered in capitals, so `AND`, `OR` and `NOT` are ordinary words in them far
  more often than they are operators. An operator is now only an operator with a term on each side,
  and if FTS5 rejects an expression anyway the query is re-read as ordinary words rather than
  failed — and says so, because a search that silently changes the question is worse than one that
  explains itself.

---

## 6. Measuring it

### The two test cases

| | `54845A Programmer.pdf` | `TDS3014B Programming Manual.pdf` |
|---|---|---|
| Pages | 110 | 418 |
| Flagged | 75 (68%), all drawn | 7 (1.7%) |
| Document verdict | **UnderExtracted** | **Figures** — not on the list to act on |
| What the flagged pages are | chapter 2, the entire command reference | six instrument screenshots and two character-set charts |

The negative control is not flagged as a document, which is what the acceptance asked for. Seven of
its pages *are* flagged individually, and that is correct: its commands all extract, but the
lettering inside its screenshots genuinely does not, and OCR recovers it.

### The ground truth

Every string in the specification's ground-truth table, searched against the repaired index. See
`docs/GROUND-TRUTH-54845A.md` for the full table and the exact queries, and `tools/` for the script
that runs them, so that a claim about search quality is a measurement somebody else can repeat.

Those 33 strings measure one document. They cannot see a repair of the other 510, which is what
`tools/measure-recovered-text.ps1` is for: it dumps the library's text with and without recovered
text merged, quotes a phrase from the difference, and asks both indexes for it. On 25 repaired pages
across 25 documents, 21 phrases are findable only after this repair, 4 were already findable from
the earlier pass, and none are unfindable. Reports are in `docs/measurements/`.

### The corpus, in full

579 documents, 100,830 pages.

| | |
|---|---|
| Documents flagged | 511 |
| Pages flagged | 11,008 — **3,434 drawn**, 7,574 raster |
| Documents whose drawn share crosses 10% — **the finding** | **104**, 2,847 pages |
| Documents with isolated figure pages | 226, 2,070 pages |
| Scanned documents with OCR gaps | 180, 5,735 pages |

### Error rate

**Precision 80%** (16 of 20 flagged pages checked by eye). **Recall about 50%, and soft** — 2 misses
in 20 unflagged pages puts it somewhere between 20% and 90% at that sample size.

Every false positive has one cause: drawn hardware — resistor bodies, screws, front-panel buttons,
halftone dots — is the size, aspect and density of lettering, and shape alone cannot separate it
from a character. A false positive costs GPU time, not correctness: OCR of a page with nothing to
recover returns nothing and the merge adds nothing.

The recall gap has a named, diagnosed cause and an undone fix: the render gate fires on
*few characters* or *many paths*, and a page with plenty of prose plus a raster figure whose labels
are unextracted satisfies neither. Adding a third limb for image coverage would probably move recall
a long way; it is not done because it invalidates the corpus run and needs its own precision
measurement afterwards. Tracked as issue #5, which records what doing it properly would cost.

The full table — every page, what it turned out to be, the arithmetic, and two blind spots found
while looking — is in `docs/UNDER-EXTRACTION-SAMPLE.md`. The sample is seeded, so anybody can
re-draw it and disagree.
