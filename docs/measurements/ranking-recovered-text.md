# Weighting recovered text, and why only for one-word queries

**Read `ranking-label-bias.md` first.** This is the second ranking change to ship, measured the same
way, and it is narrower than it first looked.

## The idea, which was already written

`RankingBias.Recovered` had been implemented and wired to `--rank-recovered` since the ranking work
began, and had never been measured once. Issue #3 stated the hypothesis:

> Weight a hit that matches recovered OCR text differently from one that matches the PDF's own text
> layer. The index already knows which (`TextSource`), and the defining page is disproportionately
> the repaired one.

The reasoning is that the page which *defines* a term is a syntax table, a pin-out or a
component-locator list — the kind of page whose text layer was incomplete and which the repair
rescued — while the pages burying it are prose that extracted cleanly all along.

## Applied to every query, it trades one gain for two losses

Swept against the 33 hand-read strings on the 632-document library:

| `--rank-recovered` | found | first 25 | first ten |
|---|---|---|---|
| 0.70 | 31 | 28 | 24 |
| 0.85 | 33 | 29 | 25 |
| **1.00 (was the default)** | **33** | **31** | **25** |
| 1.20 | 33 | 29 | 26 |
| 1.40 | 33 | 28 | 26 |
| 1.70 | 33 | 28 | 25 |
| 2.00 | 33 | 28 | 25 |

By the rule issue #3 sets — raises the first ten without lowering found-at-all — 1.20 passes. It was
still the wrong change, because the totals hide a split.

## The split is the whole result

Every bare single word improves or holds. Every colon-delimited command gets worse.

| Query | off | 1.20 | |
|---|---|---|---|
| `ATTenuation` | 14 | **1** | ↑ |
| `PROTection` | 11 | **2** | ↑ |
| `SKEW` | 6 | **1** | ↑ |
| `XREFerence?` | 2 | **1** | ↑ |
| `YINCrement?` | 2 | **1** | ↑ |
| `YREFerence?` | 2 | **1** | ↑ |
| `:CHANnel<N>:RANGe` | 4 | 8 | ↓ |
| `:CHANnel<N>:SCALe` | 6 | 15 | ↓ |
| `:CHANnel<N>:OFFSet` | 17 | 27 | ↓ |
| `:CHANnel<N>:INPut` | 19 | 32 | ↓ |
| `:CHANnel<N>:DISPlay` | 27 | 33 | ↓ |
| `:WAVeform:SOURce` | 91 | 100 | ↓ |

That is precisely the population #3 identified as worst served:

> Single-token instrument terms — `ATTenuation`, `PROTection` — are worst hit, because a bare term
> appears in hundreds of manuals.

So the hypothesis is **confirmed for bare terms and refuted for command syntax**. A compound command
is already discriminating — the notation itself narrows the field — and adding a second thumb on the
scale only displaces the page that had it right.

## Scoped to bare terms, it gains and costs nothing

`SearchQuery.IsBareTerm` — one token, no whitespace, none of the notation characters `: { } [ ] < > |`,
no quote or wildcard — and the bias applies only then:

| `--rank-recovered` | found | first 25 | first ten |
|---|---|---|---|
| 1.0 | 33 | 31 | 25 |
| **1.2** | 33 | 31 | **27** |
| **1.4 (shipped)** | **33** | **31** | **27** |
| 1.7 | 33 | 31 | 27 |
| 2.0 | 33 | 31 | 27 |
| 3.0 | 33 | 31 | 27 |

**Two more pages in the first ten, and nothing lost anywhere.** The flat version gained one and lost
two.

The plateau matters as much as the peak. Anything from 1.2 to 3.0 scores identically, so the value
is not tuned to the measurement — it is switching a behaviour on, not finding a magic number. 1.4
ships, matching the label bias.

## The controls, and what they could not tell us

Both were run with the same seed so the two passes ask identical questions:

| Control | queries | off | 1.4 |
|---|---|---|---|
| `measure-ordinary-pages.ps1` — 8-word phrases | 40 | 23 first, 38 in ten, 40 found | **identical** |
| `measure-bare-terms.ps1` — one bare word | 60 | 6 first, 12 in ten, 24 found | **identical** |

Not a single rank moved across 100 control queries.

**That is weaker evidence than it looks, and worth saying plainly.** The phrase control *cannot*
respond — `IsBareTerm` is false for every query it asks, so the change is inert for it by
construction. It proves the scoping works and nothing else. The bare-term control can respond, and
did not, because the bias only fires when the winning match is recovered text, which is uncommon on
randomly drawn pages.

So the honest claim is: on 100 ordinary queries this change moves nothing, and on the 33 strings it
moves two into view. It has not been stressed against a case designed to break it.

`tools/measure-bare-terms.ps1` was written for this and did not exist before. Any future change
scoped to one-word queries needs it, because the ordinary-pages harness is structurally blind to
them — it was reporting "40 of 40 unchanged" for a change it was incapable of detecting.

## What is still outside the first ten

Eight became six:

| Query | rank |
|---|---|
| `:CHANnel<N>:OFFSet` | 17 |
| `:CHANnel<N>:INPut` | 19 |
| `:CHANnel<N>:PROBe` | 21 |
| `:CHANnel<N>:DISPlay` | 27 |
| `:WAVeform:SOURce` | 91 |
| `The EXTernal command is only available on the 54810/20` | 13 |

Five of the six are compound commands; the sixth is a sentence. All retrieve the right page and bury
it, and neither bias shipped so far touches them — both are scoped to shapes these do not have. The
two ideas left on #3 — page geometry, and treating command syntax as notation rather than prose —
aim squarely at the five, which is the argument for trying them next.
