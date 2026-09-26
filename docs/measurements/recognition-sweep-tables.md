# Recognition sweep on dense table pages

The measurement behind issue #19, which until now rested on a single page.

| | |
|---|---|
| Measured | 2026-09-25 23:48 |
| Book | C:\Users\Tony\AppData\Local\Temp\claude\c--Users-Tony-source-ManualForge\5d252f0a-078b-4f41-b00a-46c679df0a83\scratchpad\sweep.pdf |

Content-bearing means a component designator, a value, or a word of three or more
letters. Raw counts are reported but not ranked on: across three Acrobat comparisons
they reversed the apparent result twice, because an engine reading the lines of a
drawing as text produces enormous counts of nothing.

| Configuration | Raw tokens | Content-bearing | Signal share | Junk share | Minutes |
|---|---|---|---|---|---|
| 300 dpi, no denoise | 54779 | **41446** | 75.7% | 14.6% | 3.6 |
| 300 dpi, neither | 54459 | **41273** | 75.8% | 14.3% | 3.4 |
| 200 dpi, defaults | 54682 | **41146** | 75.2% | 14.6% | 3 |
| 300 dpi, defaults | 53490 | **40351** | 75.4% | 14.2% | 3.7 |
| 300 dpi, keep weak words | 53490 | **40351** | 75.4% | 14.2% | 3.7 |
| 300 dpi, no deskew | 53303 | **40133** | 75.3% | 14.2% | 3.5 |
| 400 dpi, defaults | 51287 | **38658** | 75.4% | 14% | 4.9 |

## The confidence floor, measured separately once it could be reached

The sweep above could not test this: `--min-confidence` sets the *writer's*
threshold and never reached the recogniser's own floor, so its "keep weak words"
run returned numbers identical to the default. `--drop-score` was added in
`e118e3d` and the question asked properly.

| `--drop-score` | Raw tokens | Content-bearing | Junk share |
|---|---|---|---|
| 0.30 *(default)* | 53,490 | 40,351 | 14.2% |
| 0.15 | 54,199 | 40,652 | 14.8% |
| 0.05 | 54,270 | **40,678** | 14.8% |
| 0.01 | 54,270 | **40,678** | 14.8% |

**0.8%, and saturated below 0.05** — 0.01 returns exactly what 0.05 returns, so
there is nothing further down there to find.

On the page that started all of this, the answer is sharper still. Dropping the
floor from 0.30 to 0.01 takes it from 298 words to 317, and from **one**
bit-pattern reading to **one**. Acrobat reads fifty-six.

So the readings are not being produced and discarded as weak. They are not being
produced. That closes the configuration line of enquiry on #19: detection finds
the boxes, no threshold, grouping, resolution or confidence setting changes the
outcome, and the recogniser simply does not read these cells.
