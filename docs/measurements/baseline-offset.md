# Where a word's baseline really sits

`TextLayerOptions.BaselineOffsetFraction` decides where the invisible text layer is written relative
to the box the recogniser returns. It was **0.0**, with a comment guessing that "a small positive
value helps when the corpus is mostly mixed case", and the README's open question 6 said to tune it
against ground truth in phase 7 rather than guess.

The guess was wrong in its **sign**. It is now **-0.04**, measured.

## Why a constant is needed at all

The recogniser boxes a word's *ink*. The text layer has to sit on the word's *baseline*. For
`hello` those are the same line; for `happy` the ink runs a fifth of the way past it. One number has
to serve every word, so the only sensible way to choose it is to measure the distribution it is
approximating.

## How it was measured

A born-digital page records each word's true baseline in its own content stream, and rendering it
produces the same ink a detector would box. So the difference between the two is directly
observable, for thousands of real words in the typefaces this corpus actually uses — with no
recogniser, no GPU and nobody transcribing anything.

```
manualforge baselines --library <folder> --pages 12
```

Twelve born-digital pages across eight manuals, 2,925 words, at three resolutions:

| dpi | with a descender | without one | best constant | mean error at that constant | at 0.0 |
|---|---|---|---|---|---|
| 200 | -0.240 | -0.039 | -0.06 | 0.70 pt | 0.90 pt |
| 300 | -0.243 | -0.037 | **-0.04** | **0.66 pt** | 0.86 pt |
| 400 | -0.236 | -0.036 | -0.04 | 0.63 pt | 0.83 pt |

## What the numbers mean

**-0.24 for a word with a descender** is the descender itself: `g`, `j`, `p`, `q` and `y` drop about
a fifth of the way below the line, and the figure is the same at every resolution because it is a
fact about type rather than about sampling.

**-0.037 for a word without one** is the surprise, and it is not noise. A word of `them` should sit
exactly on its ink. It does not, at any resolution, and the reason is **optical overshoot**: round
letters are drawn fractionally below the baseline so that they look aligned with the flat ones. The
measurement recovered a typographic convention nobody told it about, which is the best evidence it
is measuring what it claims to.

**-0.04 is the constant that costs least.** Mean baseline error falls from 0.86 pt to 0.66 pt, about
a quarter. It is a compromise: three quarters of words want roughly -0.037 and the rest want -0.24,
and no single number satisfies both.

## What it does not settle

The pages are clean digital type. A scan's ink carries speckle and bleed-through that can extend a
detected box further than a rendered glyph's, so the constant may want to be slightly different
there — that needs hand-corrected scanned pages, which is issue #6 and still open.

The measurement also uses the *rendered* ink rather than a recogniser's box. Those should agree,
since the recogniser reads the same render, but "should" is doing work in that sentence.

## One thing this changed beyond the number

The verifier has its own default for this fraction, and it was 0.0 in two places that stayed in step
by luck. A verifier checking placement against a different intention from the writer's reports the
difference as error — which is exactly what happened: ten round-trip tests failed the moment the
writer's default moved. Both now read one constant,
`TextLayerOptions.DefaultBaselineOffsetFraction`, and the tests pass because they are checking the
same thing the writer was trying to do.
