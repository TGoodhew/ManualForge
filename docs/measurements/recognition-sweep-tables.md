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
