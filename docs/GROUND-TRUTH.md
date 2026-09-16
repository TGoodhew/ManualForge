# Hand-correcting ground truth

**What this is for:** producing the small set of perfectly-correct pages that `manualforge benchmark`
measures against, so the question *"is this OCR better than Acrobat's?"* gets a number instead of an
opinion.

**Why it cannot be automated:** the thing being measured is how well machines read these pages. Any
machine-produced answer is the thing under test, not a yardstick for it. A person reading the page
image is the only source of truth there is.

**How long:** about **15 minutes a page**, and you want **9 to 12 pages**. Call it two to three
hours, and it does not have to be done in one sitting — the files are plain text and half-finished
work is fine to leave.

**Do it once.** The corrected pages stay valid forever. Every future change — a new engine, a
different resolution, turning deskew off — is measured against the same set for free.

---

## Read this first: most of what looks like work isn't

The scorer normalises before comparing. **These differences are invisible to it**, so do not spend a
second on them:

| Ignore completely | Why |
|---|---|
| **Line breaks** — wrap wherever you like | Where text wraps is a property of the page, not the text |
| **Capitalisation** — `VOLTAGE` and `voltage` score identical | Case is not what OCR quality means |
| **Multiple spaces, tabs, indentation** | All whitespace collapses to one space |
| **Blank lines** | Same |
| **Column alignment in tables** | You are transcribing words, not rebuilding layout |
| **Trailing spaces** | Trimmed |

**These are compared exactly**, so they are where your attention goes:

| Get right | Example |
|---|---|
| **Every character of every word** | `attenuator`, not `a.Uenua.tor` |
| **Digits and letters that look alike** | `1N21` not `1N2l`; `0` not `O`; `5` not `S` |
| **Punctuation** | `±0.5` not `+0.5`; `A1R3` not `A1-R3` |
| **Symbols** | `Ω µ ° ± √ ×` — type the real character |
| **Part and model numbers** | `08340-60019`, hyphen included |

So the job is: **read the words off the page image, fix the ones the machine got wrong, ignore how
it looks.** That is much less work than it sounds.

---

## Before you start

You need:

* A PDF viewer that shows a page at a readable zoom — anything will do.
* A plain-text editor. **Notepad is fine.** Use UTF-8 so `Ω` and `±` survive.
* The `manualforge` command line, from `src\ManualForge.Cli\bin\...\manualforge.exe`, or build with
  `dotnet build`.

Set these once in a PowerShell window and keep it open — every command below uses them:

```powershell
$Library  = 'D:\Manuals'                     # ← your manual library
$Baseline = "$Library\BASELINE"
$Truth    = "$Library\_GroundTruth"          # corrected pages live here
$mf       = 'C:\path\to\manualforge.exe'     # ← the built executable
```

`_GroundTruth` sits inside the library on purpose: it gets backed up and synced along with
everything else, and this is the one folder here whose contents cannot be regenerated. ManualForge
excludes it from processing and indexing, so nothing will touch it.

---

## Step 1 — Find the two files you are comparing

**This is the part that is easy to get wrong**, because the two copies of a manual do not always
have the same path.

Every manual in `BASELINE\` is **Adobe Acrobat's OCR** of a scan. Somewhere in the library there is
a second copy of the *same scan* carrying **ManualForge's OCR** instead. Same page images, two
different text layers. That pairing is what the benchmark compares.

The pairing is recorded in **`BASELINE\BASELINE-manifest.csv`**, which has these columns:

```
baselineFile,manualForgeFile,pages,category,acrobatCharsSampled,commonWordShare
```

* `baselineFile` — path **relative to `BASELINE\`** → Acrobat's copy
* `manualForgeFile` — path **relative to the library root** → ManualForge's copy
* `pages` — how many pages it has, for checking you have the right pair
* `category` — `prose` or `tables`, which is how the set was chosen

**The two are not always the same name.** Most are, but at least one BASELINE file's counterpart
lives in a model subfolder rather than at the top level. Read the manifest; do not assume.

Print the pairs with full paths:

```powershell
Import-Csv "$Baseline\BASELINE-manifest.csv" | ForEach-Object {
    [pscustomobject]@{
        Category = $_.category
        Pages    = $_.pages
        Acrobat  = Join-Path $Baseline $_.baselineFile
        ForgeOcr = Join-Path $Library  $_.manualForgeFile
    }
} | Format-Table -AutoSize -Wrap
```

Confirm you have a true pair before correcting anything — same page count, and page *n* of one is
the same image as page *n* of the other:

```powershell
$pair = Import-Csv "$Baseline\BASELINE-manifest.csv" | Where-Object baselineFile -eq '438A.pdf'
$a = Join-Path $Baseline $pair.baselineFile
$b = Join-Path $Library  $pair.manualForgeFile
"Acrobat : $a"
"Forge   : $b"
(pdfinfo $a | Select-String '^Pages').ToString().Trim()
(pdfinfo $b | Select-String '^Pages').ToString().Trim()
```

Both lines must show the same number. If they differ you have the wrong pair — stop and re-read the
manifest.

`pdfinfo` and `pdftotext` come with [Poppler](https://poppler.freedesktop.org/); if they are not on
your `PATH`, any PDF viewer will tell you the page count just as well.

> **Which file do I open to read the page?**
> Either. They are the same scan, so the *images* are identical; only the invisible text differs.
> Open whichever you like at the page you are correcting.

---

## Step 2 — Choose which pages to correct

Aim for **9–12 pages**, spread across three kinds. The kinds are reported separately because an
engine that reads prose beautifully can still make a mess of a parts table, and a single average
hides exactly that.

| Kind | What to look for | How many |
|---|---|---|
| `prose` | Solid paragraphs — theory of operation, installation, a description section | 3–4 |
| `table` | A parts list, a specifications table, reference designations | 3–4 |
| `schematic` | A circuit diagram with reference designators and value labels | 3–4 |

**Pick dense pages.** A page with 30 words gives a rate with no resolution; a page with 600 gives a
meaningful one. Avoid blank pages, title pages, and *"This Page Intentionally Left Blank"*.

Find the densest pages in a manual:

```powershell
$f   = Join-Path $Baseline '438A.pdf'
$tmp = Join-Path $env:TEMP 'density.txt'
& pdftotext -q $f $tmp                       # to a file, not the pipeline - see note below
$page = 1
(Get-Content $tmp -Raw) -split "`f" | ForEach-Object {
    [pscustomobject]@{ Page = $page++; Words = ($_ -split '\s+' | Where-Object { $_ }).Count }
} | Sort-Object Words -Descending | Select-Object -First 15
Remove-Item $tmp
```

> Extract to a file and read it with `-Raw`. Piping `pdftotext` straight into PowerShell gives you
> an array of *lines*, so splitting on the form-feed page separator counts every line as a page and
> reports nonsense like "page 22177" of a 306-page manual.

Then open those pages and check they are the *kind* you want. Note the page numbers — you need them
in step 3.

> **A better way to choose, if you want one.** Pages where ManualForge and Acrobat *agree* are
> probably both right, so correcting them teaches you little. Pages where they *disagree* are where
> truth actually decides something. Seed a throwaway set (step 3), run the benchmark (step 5), and
> correct the pages with the highest **unordered** word error. Optional — picking by page type is
> perfectly good.

---

## Step 3 — Seed the files

This creates one `.txt` per page, pre-filled, for you to correct. **Correcting is far faster than
typing from scratch**, and it biases nothing: what gets scored later is a fresh run measured against
your corrected file.

Run this once per *kind*, because `--kind` applies to all pages in the command:

```powershell
# Prose pages
& $mf truth (Join-Path $Baseline '438A.pdf') `
      --seed-from (Join-Path $Library '438A.pdf') `
      --from-text-layer `
      --pages 35,161,225 `
      --kind prose `
      --out $Truth `
      --library $Baseline

# Table pages — same file, different pages and kind
& $mf truth (Join-Path $Baseline '438A.pdf') `
      --seed-from (Join-Path $Library '438A.pdf') `
      --from-text-layer `
      --pages 148,149 `
      --kind table `
      --out $Truth `
      --library $Baseline
```

What each argument does, because these two matter and are easy to swap by mistake:

| Argument | Meaning |
|---|---|
| *(first argument)* | **The file whose text layer gets scored.** Point it at the **BASELINE / Acrobat** copy, so `--score-existing` later reports Acrobat's numbers. |
| `--seed-from` | **Where the starting text is copied from.** Point it at the **library / ManualForge** copy, because its text is usually the better thing to correct. Same scan either way, so this changes only your typing, never what is measured. |
| `--from-text-layer` | Take the seed from text already in the PDF rather than running OCR. Much faster, needs no GPU. |
| `--library $Baseline` | The root the manifest's paths are recorded relative to. Keep it `$Baseline`, matching the first argument. |

Running it again later **never overwrites a file you have already corrected**, so it is safe to add
more pages at any time.

You now have:

```
_GroundTruth\
    manifest.csv            ← which page each file is, and its kind
    438A.p0035.txt
    438A.p0148.txt
    ...
```

---

## Step 4 — Correct them

For each `.txt` file: open the PDF at that page, open the text file beside it, and make the text
match what is printed on the page.

### The rules

1. **Transcribe what is printed, not what it should say.** If the manual has a typo, keep the typo.
   You are measuring whether the machine read the page, not whether the page is correct.

2. **Read in natural reading order.** Down a column, then the next column. Left page region before
   right. This is the order a person would read it aloud.

3. **Include** body text, headings, table cells, figure captions, reference designators and value
   labels on schematics, and running headers and footers if they are printed on the page.

4. **Include the page number** if it is printed on the page. It is text, and the machine will read
   it.

5. **Separate table cells with a single space.** Do not try to preserve columns — alignment is
   normalised away. A row reading `R12 │ 1.5 kΩ │ 0757-0442` becomes:
   ```
   R12 1.5 kΩ 0757-0442
   ```

6. **Type real symbols.** `Ω` not `ohm`, `µ` not `u`, `±` not `+/-`, `°` not `deg`. Save as UTF-8.

7. **Join words broken across a line end.** If the page prints `fre-` at the end of one line and
   `quency` at the start of the next, write `frequency`. If it is a genuine hyphenated compound like
   `frequency-controlling`, keep the hyphen.

8. **Illegible text: write `[illegible]`.** If you cannot read it, no machine can be scored on it
   fairly. Use it sparingly — if half a page needs it, pick a different page.

9. **Rotated text** (a label turned 90° on a schematic): transcribe it in place, in the reading order
   you would reach it.

10. **Handwriting, stamps and annotations:** include them if they are legible printed-looking text;
    mark them `[illegible]` if not. Be consistent across your pages.

11. **Do not add anything that is not on the page.** No explanatory notes, no `--- page 148 ---`
    separators, no commentary. The file is the page's text and nothing else.

### What a correction looks like

Page 148 of a power-meter service manual — a two-column reference-designations table. This is what
Acrobat produced:

```
A a .e mbly miscellaneous electrical pa.rt E P electrical connector (movable V electron tube
fuse AT a.Uenua.tor; isola.torj F VR voltage regulator; portion); plug
filter termination FL Q transistorj SCRi triode breakdown diode
```

The page actually reads, in column order:

```
A assembly
AT attenuator; isolator; fixed or variable
B fan; motor
E miscellaneous electrical part
F fuse
FL filter
H hardware
P electrical connector (movable portion); plug
Q transistor; SCR; triode thyristor
V electron tube
VR voltage regulator; breakdown diode
W cable; transmission path; wire
```

Note what changed and what did not: every mangled word is fixed, the columns are gone because
alignment does not matter, and the line breaks are wherever they fall.

### Practical advice

* Do one page, then run step 5 on it alone. Seeing a number come out tells you the plumbing works
  before you have spent three hours.
* Work at a zoom where you can read without squinting. Most of the errors you are hunting are single
  characters.
* If you are unsure whether something is `0` or `O`, look at other digits in the same font on the
  same page.
* Save often. These files are the expensive part.

---

## Step 5 — Run the benchmark

```powershell
& $mf benchmark --truth $Truth --library $Baseline --score-existing --existing-name "Acrobat" `
      --engine cuda --csv "$Truth\results.csv"
```

This reports two things side by side:

* **Acrobat** — the text already in the BASELINE files, scored against your corrections.
* **300 dpi** — ManualForge re-OCRing the same page images now, scored against the same corrections.

Then compare configurations to answer the open questions:

```powershell
& $mf benchmark --truth $Truth --library $Baseline --sweep --engine cuda --csv "$Truth\sweep.csv"
```

`--sweep` measures deskew on/off, denoise on/off, and 200/300/400 dpi against each other.

### Reading the numbers

| Column | Means |
|---|---|
| **CER** | Character error rate. Insertions, deletions and substitutions ÷ characters in your truth. Lower is better; 0 is perfect. |
| **WER** | The same over whole words, in order. |
| **WER unord** | Word errors **ignoring the order they came out in**. |
| ***Kind* CER** | The same, for just the prose / table / schematic pages. |

**The gap between WER and WER unord is reading order, not recognition.** A page read across the
columns instead of down them scores terribly on WER while every word is correct. If `WER unord` is
much lower than `WER`, the problem is layout analysis and a better recogniser would not help at all.
The per-page output says so directly when it is a big share.

**Expect this gap to be large.** On a trial run of two prose pages, comparing ManualForge against
Acrobat's text:

```
438A p35     CER 75.8%   WER 95.8%   unordered  7.2%   (93% of the word error is reading order)
438A p161    CER 47.1%   WER 56.0%   unordered  3.7%   (93% of the word error is reading order)
```

Ninety-six per cent word error reads as a catastrophe. It is not: the two engines agreed on
roughly 93% of the words and emitted them in different sequences. **Look at `WER unord` first**, and
treat `CER` and `WER` as secondary until the ordering question is settled. Judging these pages on
the ordered numbers would send you chasing a recognition problem that is not there.

(Those figures compare two OCR engines against each other, not against truth — which is exactly why
the corrected pages are worth making.)

A rate above 100% is possible and means the engine invented more text than it got right — what a
badly deskewed page does.

---

## Checklist

- [ ] Pairs identified from `BASELINE-manifest.csv`, page counts match
- [ ] 3–4 dense `prose` pages chosen
- [ ] 3–4 dense `table` pages chosen
- [ ] 3–4 `schematic` pages chosen
- [ ] Seeded with `manualforge truth` (BASELINE file first, `--seed-from` the library copy)
- [ ] One page corrected and benchmarked, to prove the loop works
- [ ] The rest corrected
- [ ] `--score-existing` run: Acrobat versus ManualForge on the same pages
- [ ] `--sweep` run: deskew, denoise and resolution compared
- [ ] `results.csv` and `sweep.csv` kept beside the corrected pages

---

## Troubleshooting

**"No manifest.csv in …"** — the folder has no seeded set. Run step 3.

**A page is missing from the benchmark** — its `.txt` file was deleted or renamed. Rows whose text
file has gone are skipped silently so one missing file cannot stop a run; re-seed that page.

**Everything scores 0%** — you are scoring a file against truth seeded from that same file. Check
the first argument of `truth` and `--seed-from` are different copies.

**Everything scores near 100%** — the manifest is probably pointing at the wrong pages, or at a file
whose page numbering differs from the copy you corrected against. Check the pair's page counts match.

**Symbols come out wrong** — the text file is not UTF-8. Re-save it as UTF-8 in your editor.

**The OCR run says it is on CPU** — the CUDA runtime is not on `PATH` for that shell. It still works,
just slower; see the CUDA section of the README.
