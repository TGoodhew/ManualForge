# Measurements

One file per run of `tools/measure-ground-truth.ps1`, kept so that a claim about search quality can
be checked against the run that produced it rather than against somebody's recollection of it.

Nothing in here is hand-edited. If a number looks wrong, re-run the harness and commit what it says.

| File | What it measures |
|---|---|
| `ground-truth-before-full-repair.md` | The index as it stood on 2026-09-22, after the first 20-document repair and before the corpus-wide one. 27 of 33 at rank 25, 21 in the first ten. |
| `ground-truth-after-full-repair.md` | The same 33 strings after the whole library was repaired and re-indexed. Also 27 of 33, 21 in the first ten — the 54845A was already repaired, so these queries are blind to that pass. |
| `recovered-text-reaches-search.md` | The pass those 33 strings cannot see: 25 repaired pages across 25 other documents, asked for a phrase that exists only in recovered text. 21 newly findable, 4 already findable, 0 missing. |
| `ground-truth-with-model-hint.md` | The same 33 strings asked with `--model 54845A`, as a caller who knows the instrument would. 33 of 33, and 32 in the first ten against 21 without. |
| `new-flags-precision.md` | Twelve pages the new render-gate limb added, looked at by eye. 11 genuine, 1 false positive — enough to decide the repair was worth running. |
| `ground-truth-after-compact.md` | The same 33 strings after the index was rebuilt from scratch and vacuumed, 385 MB down to 306 MB. Identical scores, which is what a rebuild losing nothing looks like. |
| `ground-truth-after-reaudit.md` | The 33 strings after the re-audit and the third repair. Still 27 of 33, 21 in the first ten: 700k more words of competition changed nothing by more than a rank or three. |
| `recovered-text-after-reaudit.md` | 25 pages from the third repair. 21 newly findable, 2 already findable, 2 whose *document* returns at ranks 4 and 2 but whose page sits below 25. |
| **`circuit-diagrams-against-acrobat.md`** | The harder round, on pages chosen by geometry rather than text. Acrobat produces 47% more words and two thirds of its extras are single characters; counting only content-bearing tokens reverses it to ManualForge +17.7%, leading 15 pages to 4. Also what this metric cannot see. |
| **`schematics-against-acrobat.md`** | The first head-to-head with another engine, on 24 schematic pages from 24 documents with the text layer removed by construction. ManualForge finds 45% more words and they are real - 47% component designators against Acrobat's 4.6%. Also the two places Acrobat genuinely wins, and why the page-selection method needs a geometry signal next time. |
| **`notation-symbols.md`** | Whether electronic notation survives recognition, after a real error found by eye. The `±`-as-`v` corruption is 62 occurrences in 20 of 630 documents and concentrated in one bad scan. Also: two bigger-looking numbers that turned out to be almost entirely false positives, and why only ground truth can settle it. |
| **`ranking-recovered-text.md`** | The second ranking change: weighting matches against text the repair recovered, but **only for one-word queries**. Applied to everything it gains one place and loses two; scoped to bare terms it gains two and loses none. Also why the ordinary-pages control reported "unchanged" for a change it could not detect. |
| `ranking-recovered-1.0.md` / `ranking-recovered-1.2.md` / `ranking-recovered-text-default.md` | The runs behind that: the old default, the flat bias that was rejected, and the scoped bias that shipped. |
| **`ranking-label-bias.md`** | **Read this one first about ranking.** Why a term on a line of its own is boosted, measured three ways, and what was tried and rejected beside it. |
| `reverse-video.md` | Looking inside blocks of ink for white lettering: what it finds (analyser readouts), what it also finds (photographs), the discriminator that failed, and why it ships switched off. |
| `recall-after-the-third-limb.md` | The detector's recall re-measured by eye: 1 miss in 20 unflagged pages against 2 before, so about 79% rather than 49% — with the same wide interval, because the sample is the same size. |
| **`baseline-offset.md`** | Where a word's baseline sits relative to its ink, over 2,925 words of born-digital type. Settled open question 6: the default was 0.0 and the guess in its comment had the sign wrong. |
| **`publisher-truth-benchmark.md`** | The first accuracy numbers: 12 born-digital pages scored against their own text layers, so nobody had to transcribe anything. Deskew and denoise earn nothing on clean type and cost a fifth of the throughput; 200 dpi matches 300 and is 48% faster; a table is three times harder than prose. The floor, not accuracy on scans. |
| `ground-truth-with-label-bias.md` | The 33 strings with the bias that is now the default: 31 of 33 in the first 25, 25 in the first ten. |
| `ordinary-pages-without-label-bias.md` / `ordinary-pages-with-label-bias.md` | The control that decides whether a ranking change ships: 40 pages quoted at random from the library. 30 first against 29, nothing leaving the first ten. |
| `ground-truth-after-shared-folder-ingest.md` | The 33 strings after 31 documents and 3,742 pages arrived from the shared folder (#17). Identical: 33 found, 31 in the first 25, 25 in the first ten. |
| `ordinary-pages-after-shared-folder-ingest.md` | The harm control for that ingest. 40 of 40 retrievable, 38 in the first ten. The two below it lose to documents the library already held - a second copy of the same 8657B volume, and an 8903B manual reusing the same boilerplate - so neither was displaced by the new material. Not directly comparable with the runs above: the sample is drawn from the dump, and the dump grew. |

## Adding one

```
./tools/measure-ground-truth.ps1 -Label "after the full repair" \
    -Out docs/measurements/ground-truth-after-full-repair.md
```

The second harness needs the library dumped to text twice, which `index --sidecars` does and which
takes a few minutes each way:

```
manualforge index <library> --index <tmp>\with.db    --sidecars <tmp>\after
manualforge index <library> --index <tmp>\without.db --sidecars <tmp>\before --no-repairs

./tools/measure-recovered-text.ps1 -After <tmp>\after -Before <tmp>\before \
    -BeforeIndex <snapshot of the old index> -Out docs/measurements/<name>.md
```

Each report records the index file it read, when that index was written, its size and the repo
commit, because a score without those is not a measurement of anything.

To measure an index that has since been overwritten, pass `-Index` a copy. Copying
`_Originals/manualforge-index.db` aside before a repair costs 300 MB and is the difference between
having a control and arguing from memory.

## Measuring a ranking change

The 33 strings come from one manual and were chosen because they failed, so a change designed
against them will flatter itself. Anything that alters ranking is measured against sets of queries
that had no hand in its design as well.

**Match the control to the query shape the change touches.** The ordinary-pages harness asks only
eight-word phrases, so it reported "40 of 40 unchanged" for the recovered-text bias — not because
that bias was harmless but because every query it asks is one the bias never sees.
`measure-bare-terms.ps1` exists for that reason: one distinctive word per page, which is the shape a
single-token change actually moves. A control that cannot respond is not evidence.

```
./tools/measure-ground-truth.ps1    -Extra @('--rank-labels','1.4')   # the target
./tools/measure-recovered-text.ps1  -Extra @('--rank-labels','1.4')   # other documents
./tools/measure-ordinary-pages.ps1  -Extra @('--rank-labels','1.4')   # ordinary prose, does it harm?
./tools/measure-bare-terms.ps1      -Extra @('--rank-labels','1.4')   # one-word queries, does it harm?
```

The last two are the ones that decide. A change that gains four places on the ground truth and costs ten
ordinary pages their first place is not an improvement, and only that script will say so.
