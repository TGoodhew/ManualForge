# Flattening keeps bookmarks, page labels and links

Files that refuse modification are rebuilt by importing their pages into a new document. Page import
copies each page and nothing at document level, so 7 flattened manuals lost their bookmarks and 5
their printed page numbers, with nothing to say so (#28). Checking the fix turned up a second bug,
which turned out to be worse: **every internal link in a flattened file led to a detached copy of
its target page**, outside the document.

9 Oct 2026, PDFsharp 6.2.4.

## The fix

`CatalogCarrier` deep-copies the catalog entries that make a manual navigable: outlines, named
destinations, page labels, page mode and layout, open action, viewer preferences, optional-content
properties, language, metadata and threads. Every reference is remapped: to the imported page, to
the copy the import already made of anything a page reaches (found by walking each source page
alongside its import), or to a fresh copy of anything else.

The pairing walk is also where the link bug showed. When the import meets a reference from one page
to another (a link's destination, an annotation's own page), it imports the other page again as a
detached copy. Each such reference is now pointed at the real imported page. 461A Mil Manual is how
it was found: its bookmarks share their GoTo actions with link annotations, so the carried bookmarks
landed on detached copies, and the new check refused the flatten.

Verification now also requires every bookmark's title, depth and page, and every link's destination
page, to match the source. The catalog entries that don't survive are listed in
`FlattenResult.Dropped`, and the library log reports them. Some are left behind on purpose, with the
reason given: `/AcroForm`, `/StructTreeRoot`, `/MarkInfo` and the signature entries. Anything else
is reported as lost.

## Over every original

All 170 files in `_Originals`, flattened into a scratch folder (sources only read):

| | Files |
|---|---|
| Flattened and verified | 169 |
| Unreadable, before the fix as well (`08340-90020-serv-v2-4.pdf`, a corrupt stream keyword) | 1 |
| With bookmarks, every one kept on its page | 25 of 25 |
| Left behind: `/AcroForm` / `/StructTreeRoot` / `/MarkInfo` | 58 / 33 / 3 |
| Lost, with no reason given: `/FICL:Enfocus` (PitStop's private data), `/DefaultGray`, `/DefaultRGB` (misplaced in one catalog) | 2 / 1 / 1 |

2235_lg's 141 links all reach their pages. Its existing library copy had all 141 pointing outside the
document, and carried 226 detached page copies.

## The seven files re-read

`run --redo-completed --documents` with the fix, then `doctor`, `repair --documents --include-scans`
(93 pages, 1,633 words) and `index`. Every one of the seven now matches its original on bookmarks
and page labels, and 2235_lg on all 141 links: 194, 8, 125, 16, 27, 9 and 7 bookmarks. The copies
they replaced are in `_Originals\_superseded`.

## The rendering difference on 461A-462A-OSM

Rendered at 72 dpi, pages 1, 18, 19, 21, 24, 25 and 28 differ from the original by 1.0–3.6% of
pixels. The copies made on 16 Sep, 1 Oct and today all do so identically.

- **Ruled out:** flattening, catalog entries (including `/OCProperties`), the `q`/`Q` wrap, the
  `/MFInvisible` font resource, the text block itself (each page's real one), the transparency group,
  stripping, saving in Modify mode, and the PDF version. A fresh flatten carrying each page's real
  text renders identically to the original.
- **What is left:** the real `ocr` output of that same fresh flatten differs by the same 3.59% on
  page 21. Everything the page reaches is identical between the two except the embedded invisible
  font program: 2,672 bytes holding every glyph used, against 912.
- **What the difference is:** in parts of the page, the scan is drawn about one pixel further over.
  Each glyph gets a red fringe on one edge and blue on the other in a difference map, and the page
  looks the same. The image bytes and the matrix that places them are identical. The page draws a
  2000×1515 JPEG through a 90° rotation matrix.

So it is a PDFium resampling effect, set off by the font loaded on the page, and **no data in the
file changes**. It is documented, not fixed. 3585A-SAT shows the same symptom on 16 of 17 pages
(0.8–4.3%), and its 30 Sep copy did identically. Its pages are upright 1-bit CCITT scans whose
original carried an old hidden text layer, and the cause there has not been isolated. The other five
re-read files render identically to their originals. Whether another renderer (Acrobat, a browser)
shows any of this has not been checked.
