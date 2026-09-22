# Measurements

One file per run of `tools/measure-ground-truth.ps1`, kept so that a claim about search quality can
be checked against the run that produced it rather than against somebody's recollection of it.

Nothing in here is hand-edited. If a number looks wrong, re-run the harness and commit what it says.

| File | What it measures |
|---|---|
| `ground-truth-before-full-repair.md` | The index as it stood on 2026-09-22, after the first 20-document repair and before the corpus-wide one. 27 of 33 at rank 25, 21 in the first ten. |

## Adding one

```
./tools/measure-ground-truth.ps1 -Label "after the full repair" \
    -Out docs/measurements/ground-truth-after-full-repair.md
```

Each report records the index file it read, when that index was written, its size and the repo
commit, because a score without those is not a measurement of anything.

To measure an index that has since been overwritten, pass `-Index` a copy. Copying
`_Originals/manualforge-index.db` aside before a repair costs 300 MB and is the difference between
having a control and arguing from memory.
