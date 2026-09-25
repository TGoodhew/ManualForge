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
| `ground-truth-after-reaudit.md` | The 33 strings after the re-audit and the third repair. Still 27 of 33, 21 in the first ten: 700k more words of competition changed nothing by more than a rank or three. |
| `recovered-text-after-reaudit.md` | 25 pages from the third repair. 21 newly findable, 2 already findable, 2 whose *document* returns at ranks 4 and 2 but whose page sits below 25. |
| **`ranking-label-bias.md`** | **Read this one first about ranking.** Why a term on a line of its own is boosted, measured three ways, and what was tried and rejected beside it. |
| `ground-truth-with-label-bias.md` | The 33 strings with the bias that is now the default: 31 of 33 in the first 25, 25 in the first ten. |
| `ordinary-pages-without-label-bias.md` / `ordinary-pages-with-label-bias.md` | The control that decides whether a ranking change ships: 40 pages quoted at random from the library. 30 first against 29, nothing leaving the first ten. |

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
against them will flatter itself. Anything that alters ranking is measured against two sets of
queries that had no hand in its design as well:

```
./tools/measure-ground-truth.ps1    -Extra @('--rank-labels','1.4')   # the target
./tools/measure-recovered-text.ps1  -Extra @('--rank-labels','1.4')   # other documents
./tools/measure-ordinary-pages.ps1  -Extra @('--rank-labels','1.4')   # ordinary prose, does it harm?
```

The third is the one that decides. A change that gains four places on the ground truth and costs ten
ordinary pages their first place is not an improvement, and only that script will say so.
