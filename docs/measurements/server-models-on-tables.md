# The larger recognition model, measured on tables

ManualForge had never chosen a recognition model. It built the OCR service without setting
`DetectionModel` or `RecognitionModel`, so it silently ran PP-OCRv5's **mobile** pack — the compact
one, built to be deployable on constrained hardware — on a desktop with a CUDA card. "Mobile" and
"server" name the model's size, not the machine it runs on.

That also undercut an earlier conclusion. `recognition-sweep-tables.md` swept thirteen
configurations over 120 pages and found the yield flat, and I wrote that the recognition model was
the ceiling. Every one of those runs used the same small model, so what it measured was that
model's ceiling.

`--server-models` now selects the larger pack, roughly 3-5x the size. Measured on the same 100
captioned table pages where Acrobat leads.

## It closes a fifth of the gap for nine times the work

| | Raw words | Content-bearing | vs Acrobat |
|---|---|---|---|
| Acrobat | 58,178 | **28,418** | — |
| mobile *(shipped)* | 48,029 | 22,294 | **−21.5%** |
| server | 49,391 | 23,534 | **−17.2%** |

| | mobile | server |
|---|---|---|
| Throughput | **34.1 pages/min** | 3.6 pages/min |
| Time for these 100 pages | 176 s | **1,662 s** |

Per page: server is better on 28, level on 64, and **worse on 8**.

So the larger model gains **+5.6%** over the smaller one and leaves Acrobat still 17.2% ahead,
at **9.4x the runtime**.

## What that costs in practice

At 3.6 pages a minute, re-recognising the 6,206 captioned table pages would take about **29 hours**.
The whole 112,739-page library would take about **22 days**.

For a 5.6% gain on the measure, and still a 17.2% deficit against the engine it was meant to catch,
that is not a trade worth making — neither corpus-wide nor routed to table pages only.

## What it settles

**The model size is not the explanation.** The gap on tables survives a 3-5x larger detector and
recogniser, which rules out the cheapest hypothesis in #22 and makes the remaining ones more
interesting rather than less: the dedicated table-structure models, which recover cell layout
rather than flat text lines, and an engine outside this package.

It also means the sweep's conclusion stands after all, for a better reason than it was first given.
Thirteen settings and two model sizes now point the same way: **this is not a configuration
problem.**

`--server-models` stays, off by default. It is measured, it is documented here, and it is the sort
of thing somebody will otherwise try again from scratch.
