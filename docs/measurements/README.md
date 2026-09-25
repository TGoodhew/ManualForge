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
