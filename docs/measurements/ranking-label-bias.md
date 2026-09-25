# Ranking: a term on a line of its own

bm25 scores on term frequency and page length. It has no notion of what a page is *for*, so the page
that **defines** `:WAVeform:SOURce` — where the words appear once, in a diagram — loses to a page
that discusses waveform sources at length. Eleven of the 33 hand-read strings retrieved the right
page and buried it, five of them past rank 39.

`RankingBias.Label` multiplies a page's score when one of the query's terms appears on a line of its
own: a label, a heading or a table cell rather than a word inside a sentence. That is the shape of a
syntax diagram, a pin-out, a component-locator table and a front-panel legend, which is most of what
this library is asked about.

It is **on by default at 1.4**. `--rank-labels 1.0` turns it off.

## Measured three ways, because one of them is not enough

The 33 strings are a bad judge of a change designed against them: they come from one manual and they
were chosen *because they failed*. So two sets of queries that had no hand in the design were
measured alongside.

| | Ground truth (33 strings, one manual) | 25 repaired pages, other documents | 40 ordinary pages, quoted at random |
|---|---|---|---|
| **Without** the bias | 27 in the first 25, 21 in the first ten, 32 found | 23 of 25 found | 30 first, 39 in the first ten |
| **With** it | **31** in the first 25, **25** in the first ten, **33** found | 23 of 25 found | 29 first, 39 in the first ten |

A large gain where the problem is, nothing at all on one control, and one page in forty slipping off
first place on the other — with nothing leaving the first ten and nothing becoming unfindable.

`:WAVeform:SOURce`, the query this repository has been quoting as "beyond 200" since 18 September,
comes back at 88. Still not good. Better than absent.

## Why this is not just a number that happened to go up

The mechanism is a property of technical manuals rather than of the 54845A: **a term printed on a
line of its own is a label, and a page of labels is usually the page that defines the thing.** That
is why it was expected to help single-token queries worst-hit by bm25 — `ATTenuation` goes from 44
to 12, `PROTection` from 109 to 11 — and why it was expected to be nearly inert on prose, which the
40-page control confirms.

Two tests pin both halves: a page whose text is `ATTenuation` on its own line beats a page that uses
the word six times, and a query whose terms appear only inside sentences comes back in exactly
bm25's order.

## What was tried at the same time and not adopted

**A boost for hits whose text the repair recovered**, on the premise that a page whose text had to be
read off a picture is disproportionately the page that draws rather than discusses. The numbers did
not support it: the first ten improved (21 to 24) while the first 25 did not move (27), and
`:WAVeform:SOURce` went back out of reach entirely. Combined with the label bias it was better in
the first ten (26) and worse in the first 25 (30 against 31).

It stays available as `--rank-recovered`, off, and recorded here so the next person does not spend
the afternoon rediscovering that it is a wash.

**A proximity re-rank** was tried and removed in September, before any of this: 31 correct pages in
the first ten became 26, because on a syntax diagram the levels of a command are *not* adjacent.
`docs/GROUND-TRUTH-54845A.md` has the reasoning.

## What is still wrong

Six strings remain outside the first ten, and `:WAVeform:SOURce` is at 88. A user who knows the
instrument can pass `--model`, which takes the whole table to 33 of 33 and 32 in the first ten — but
that is the caller supplying knowledge, not the ranking getting better. Issue #3 stays open.
