# ManualForge

Searchable PDFs from scanned technical manuals, and a search index over all of them.

ManualForge is built for libraries of vintage test-equipment documentation: 1950s–80s service
manuals scanned at 200–400 dpi, where about a quarter of the files have no text at all and many of
the rest carry poor OCR from the 2000s. It:

- **adds an invisible, accurately placed text layer to scanned PDFs.** The page image is left
  untouched, so the manual looks exactly as before, but you can now search, select and copy its
  text in any PDF reader.
- **decides which files need it.** A whole library is classified first. Files whose text is already
  good are left alone.
- **finds pages whose text is incomplete.** Schematic labels, drawn tables and syntax diagrams that
  a manual's existing text layer missed are read from the rendered page.
- **indexes every page of every manual**, so a question returns the manual, the page and a snippet:
  from the command line, the desktop app, or Claude through MCP.

It never loses a manual. A file is only replaced after its searchable copy has been written and
verified, and the original is then moved, unchanged, into an `_Originals` folder inside your
library. Files that need no work are never touched. See
[What happens to your library folder](#what-happens-to-your-library-folder).

It works on any NVIDIA GPU whose driver supports CUDA 13 (in practice a GTX 16 or RTX 20 series
card, or newer). With CUDA on an RTX 5070 Ti it read a whole library at about 150 pages a minute,
and dense pages such as tables at about 85–90. TensorRT is faster still: nearly twice as fast on
dense pages, and about 164 pages a minute when the whole library was read again with it. It also
runs on the CPU alone, at about 15 pages a minute on dense pages.

A note on accuracy: what ManualForge measures is how *much* text it recovers. How *correctly* it
reads a scanned page has not been measured. The details, and the full story of how ManualForge was
built and measured, are in [docs/DEVELOPMENT-HISTORY.md](docs/DEVELOPMENT-HISTORY.md).

## An example: how does the chopper work in the HP 432A?

The HP 432A is a thermistor-bridge RF power meter, and its operating and service manual is one of
632 in the library ManualForge was built on. The scan already has a text layer, so a PDF reader can search it.
But its most useful page for this question is Figure 4-2, the block diagram, and the text layer has
none of the words drawn on it. Search the PDF for "chopper" and you get schematic pages; the block
diagram, which shows where the chopper sits in the signal path, never comes up.

ManualForge's audit found that page 23 draws far more than its text layer holds, read the picture of
the page, and added what it found to the index. Asked across the whole library,
the block diagram now comes first:

```
> manualforge search chopper --library D:\Manuals

  432A-OSM  page 23  [OCR]
    … Model 432A Block Diagram … RANGE I [CHOPPER] AMPLIFIER AMPL RANGE CALIBRATION …
    (recovered by OCR at 82% mean confidence; the file's own text layer does not hold this)
```

Ask Claude "How does the chopper work in the 432A?" and it does the same: `library_search` finds
page 23, and `read_manual_page` returns the page's own text plus what ManualForge recovered from
the drawing, marked as such:

```
--- recovered by OCR from the rendered page, 82% mean confidence at 300 dpi; the PDF's own text layer does not hold this ---
...
CHOPPER
AMPLIFIER
...
5KHz
MULTI-
VIBRATOR
...
VOLTAGE-TO-TIME CONVERTER
COMPENSATION BRIDGE
...
LOW-PASS
FILTER
```

With that and the Principles of Operation two pages earlier (page 21, paragraph 4-4), the answer is
in the manual's own words. The two thermistor bridges produce dc voltages, VRF and VCOMP. Their
difference, VCOMP − VRF, "is chopped, amplified and fed to an electronic switch actuated by the
controlled width pulses": a 5 kHz multivibrator and voltage-to-time converter set the width of each
pulse from VCOMP + VRF, and the chopper amplifier sets its height from VCOMP − VRF. The meter
averages those pulses, so it reads the product of the two, which paragraphs 4-10 and 4-11 show is
proportional to the RF power. Without the recovered text, nothing would point a search, or Claude,
at the diagram that ties it together.

## Install

Download `ManualForge-Setup-<version>.exe` from the
[latest release](https://github.com/TGoodhew/ManualForge/releases/latest) and run it. It installs for
you alone, into `%LOCALAPPDATA%\Programs\ManualForge`. Windows asks for permission only if a runtime
the PC is missing has to be installed for every user.

**First, if the PC has an NVIDIA card,** install the current NVIDIA driver. The installer offers the GPU
libraries only when the driver is there.

The installer is not signed yet, so Windows may say "Windows protected your PC". Choose **More info**,
then **Run anyway**. The release page gives the installer's SHA-256, if you want to check it.

The installer asks for:

1. **Your library folder:** the folder of PDF manuals that Claude and other AI apps search through
   ManualForge. It is also set as the `MANUALFORGE_LIBRARY` variable, unless that is already set, and
   the desktop app opens on it. The command line is given a folder each time.
2. **What to set up:**
   - **NVIDIA's libraries for the GPU:** CUDA and cuDNN (about 1.7 GB), and optionally TensorRT
     (another 1.9 GB, which reads about twice as fast). These are offered only on a PC with an NVIDIA
     card. They come from NVIDIA, under NVIDIA's licences, which the installer shows and asks you to
     accept. Without them ManualForge runs on the CPU, about ten times slower.
   - **Connect ManualForge to the AI apps on this PC,** such as Claude Desktop, Claude Code and VS Code.
     See [Connect Claude and other apps](#connect-claude-and-other-apps).
   - **Let a terminal run `manualforge` from any folder.** This adds the program folder to your
     `PATH`. It is off by default.

It then:
- installs the .NET 10 Desktop Runtime, the Windows App Runtime 1.8 and the Visual C++
  Redistributable, but only those the PC doesn't already have, or has in a version too old;
- downloads the GPU libraries you chose, and the OCR models (about 25 MB);
- adds **ManualForge** to the Start menu.

Everything it downloads comes from its publisher and is checked against a SHA-256 before it is used.
The GPU libraries go into `%LOCALAPPDATA%\ManualForge\gpu`. They are not put on `PATH`, and the
display driver is never touched.

The last page says which version was installed and what it runs on: `TensorRt`, `Cuda` or `Cpu`. From a
terminal, `manualforge version` and `manualforge gpu` say the same.

The commands in this README are written as `manualforge`. Unless you ticked the `PATH` option, use
the program's full path instead: `& "$env:LOCALAPPDATA\Programs\ManualForge\manualforge.exe"`.

To build ManualForge yourself instead, see [Build from source](#build-from-source).

### Connect Claude and other apps

The MCP server lets Claude and other AI apps search the library and read pages. If you ticked
**Connect ManualForge to the AI apps**, the installer has already registered it with every app on the
PC that can launch a local MCP server. Restart Claude Desktop to pick it up. In Claude Desktop,
**Settings › Developer** should then list `manualforge` as running.

It knows the following apps:
- Claude Desktop (Store and classic) and Claude Code;
- VS Code (and Insiders) and Visual Studio;
- Cursor and Windsurf;
- Gemini CLI, OpenAI Codex CLI, LM Studio and Continue.

Each app's settings file is copied aside before it is first changed. ManualForge's entry is merged in,
leaving everything else in the file as it was, and ManualForge records what it changed. Uninstalling
then undoes exactly that.

To register later, with an app installed since, or for another library:

```powershell
manualforge setup mcp --list                       # which apps are here, and which already have it
manualforge setup mcp --library "D:\Manuals"       # register it with all of them
```

`--client` limits it to some apps, and `--plan` shows what would change without changing it.

To do it by hand instead, point `MANUALFORGE_LIBRARY` at your library folder, as follows.

**Claude Code:**

```powershell
claude mcp add manualforge --scope user --env MANUALFORGE_LIBRARY="D:\Manuals" -- "$env:LOCALAPPDATA\Programs\ManualForge\ManualForge.Mcp.exe"
```

**Claude Desktop:** quit it, then add this to `claude_desktop_config.json`, using real paths
(Claude Desktop does not expand `%LOCALAPPDATA%`):

```json
{
  "mcpServers": {
    "manualforge": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\ManualForge\\ManualForge.Mcp.exe",
      "env": { "MANUALFORGE_LIBRARY": "D:\\Manuals" }
    }
  }
}
```

The config file is at `%APPDATA%\Claude\claude_desktop_config.json`, unless Claude Desktop came from
the Microsoft Store. Then it is at
`%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Roaming\Claude\claude_desktop_config.json`.

### Updating

Run the new installer over the old one. You don't need to close anything first.

If ManualForge is running, Setup lists what is running and stops it. That includes the app, and any
MCP server that Claude Desktop, Claude Code or another app started; Setup names which app started
each. Those apps start the new server the next time they need it. If one shows an error for
ManualForge, restart that app.

An update keeps your choices, the GPU libraries, the models, and what ManualForge has learned about
your GPU.

## Use

### What happens to your library folder

A library is just a folder of PDFs, with any subfolders you like. `run` changes it in place: each
file is either replaced with a searchable copy, or left exactly as it is. **You can always get back
to the library you started with.**

**A file ManualForge processes** (a scan with no text, for example):

1. A searchable copy is made in a temporary folder. Nothing in your library has changed yet.
2. The copy is checked: it opens, has the same pages, carries text in the right places, and its
   page images are unchanged.
3. Only then is your **original moved, byte for byte, into `<library>\_Originals\`, at the same
   relative path**. For example, `D:\Manuals\HP\8340B.pdf` goes to
   `D:\Manuals\_Originals\HP\8340B.pdf`.
4. The searchable copy takes its place at `D:\Manuals\HP\8340B.pdf`.

If any step fails, the original stays where it was and nothing is replaced.

Two exceptions keep one bad page from costing a whole manual:
- **Oversized pages.** A page too large to read at the chosen resolution, such as a big fold-out,
  is read at the highest resolution that fits.
- **Pages that can't be read at all.** A page that can't be read because of something in the page
  itself is left as it was in the scan, without text, and the rest of the manual goes ahead. The
  run summary lists those pages, and the file's record notes them. If the file is being read
  again and its current copy has text on such a page, the current copy is kept instead, so a
  re-read never makes a page worse.

A failure that isn't about one page, such as the GPU running out of memory, still fails the whole
file and leaves the original untouched, so running again retries it.

**A file ManualForge leaves alone**, because it already has good text, stays exactly where it is,
untouched. It needs no backup, so it is not copied into `_Originals`.

So, after a run:
- **The PDFs in your library are what you use.** Searchable copies where a file was processed,
  your own files everywhere else, all at the paths they always had, with one exception: a file
  whose printed text could not be read gets `_repaired` added to its name (see below).
- **`_Originals` holds the untouched original of every file that was replaced, and nothing else of
  yours.** Together with the files that were left alone, that is your whole library as it was
  before. [Uninstall](#uninstall) shows how to put the originals back.

`_Originals` is also where ManualForge keeps its working files:

| Path | Contents |
|---|---|
| `_Originals\<same relative path>` | the untouched original of every file that was replaced |
| `_Originals\_superseded\` | the previous searchable copy, kept whenever a file is read again from its original |
| `_Originals\_replaced\<when>\` | an earlier original, kept when a different file of the same name replaced a finished one in the library |
| `_Originals\manualforge.db` | progress, and recognition cached so an interrupted run resumes |
| `_Originals\manualforge-doctor.db` | what the audit found, and the text `repair` recovered |
| `_Originals\manualforge-index.db` | the search index |
| `_Originals\reread-progress.json` | how far `tools/reread-library.ps1` has got, so it can carry on; only there if you have used it |

Leave `_Originals` where it is and don't edit what's in it. It is the only copy of every original
that was replaced.

`doctor`, `repair`, `index` and search never change a PDF. Text that `repair` recovers is kept in
`manualforge-doctor.db` and added to the search index, not written into the file. Folders named
`_Originals`, `BASELINE` and `_GroundTruth` at the top of the library are never processed or indexed.

### Process a library

The usual sequence, each step safe to stop with Ctrl+C and run again:

```powershell
manualforge survey D:\Manuals     # classify every file and show what would be done; changes no PDF
manualforge run    D:\Manuals     # OCR the files that need it, replacing each only once verified
manualforge doctor D:\Manuals     # find pages whose text layer is incomplete
manualforge repair D:\Manuals     # read those pages and keep what they were missing
manualforge index  D:\Manuals     # build or update the search index
```

- **What `run` does:** by default it OCRs only scans with no text layer. Files that already have
  good text are skipped. `--policy` changes that per class; for example,
  `--policy UnreadableTextLayer=redo,SuspectText=redo` replaces text layers that cannot be decoded
  or are garbled.
- **Printed text that can't be read:** some manuals are typeset in fonts whose letters extract as
  nonsense (`*($SSOLDQFHV` for "GE Appliances"). With `redo`, that text is kept on the page exactly
  as it looks, but made to extract as blanks, and the recognised text is written over it. The file
  is then renamed with `_repaired`, for example `oven.pdf` becomes `oven_repaired.pdf`, so you know
  its searchable text came from recognition and may have mistakes. The original keeps its old name
  in `_Originals`. The run summary lists every file renamed.
- **Files you can't normally write to:** password-protected files (an owner password that forbids
  editing) are rebuilt losslessly first.
- **Signed files:** digitally signed files are processed, which invalidates the signature, and are
  listed in the summary. `--refuse-signed` skips them instead.
- **Printed text:** text printed on a scan, such as a seller's stamp or a header, is kept, and the
  new text layer is written around it.
- **Trial runs:** `--dry-run` does all the work except replacing files, and `--limit <n>` stops
  after n files.
- **`repair`:** it never writes to a PDF. Its text goes into the audit database and reaches search
  at the next `index`. By default it does pages whose content was drawn as graphics.
  `--include-scans` also does scanned pages whose existing OCR missed lettering, which on a large
  library is hours of work. Before it starts, it says how many pages it will read and how long that
  should take. `repair <library> --plan` prints just that and stops, without using the GPU.
- **`manualforge status D:\Manuals`:** shows the work queue and an estimate at any time, without
  changing anything.
- **Speed on your GPU:** nothing needs setting for your card. ManualForge learns how many pages
  your card can read at once, one run at a time. The first run on a new card reads two at once.
  After any run of 100 pages or more that fits on the card, the next run tries one more, until one
  overfills it. That number is remembered and not tried again until your display driver changes, so
  after a few runs every run uses the most your card holds. If a run overfills the card, for example because you open a game partway
  through, it drops back by one straight away and carries on. The estimates from `survey`, `status` and
  `repair` use the speeds measured on your card once it has done a run or repair big enough to
  time, and say when they don't have one yet. `--gpu-concurrency <n>` fixes the number instead.
  What was learned is kept in `%LOCALAPPDATA%\ManualForge\gpu-profiles.json`, separately for
  TensorRT and CUDA. `manualforge gpu` shows it, and `manualforge gpu --retune` makes the next run
  learn it again, keeping the measured speeds. That applies to the engine in use; add `--engine cuda`
  to retune CUDA on a PC that has TensorRT. Deleting that file forgets the speeds too.
- **TensorRT:** when it's installed, ManualForge uses it automatically, and reads pages almost twice as
  fast. The first run on a card spends about a minute preparing it; later runs reuse that preparation,
  which is kept under `%LOCALAPPDATA%\ManualForge\models\tensorrt`. TensorRT reads about 1 word in 100
  differently from plain CUDA, mostly words the reading was unsure of, some better and some worse.
  `--engine cuda` turns it off. `manualforge gpu` shows which one is in use.

### One file

```powershell
manualforge ocr "service-manual.pdf" --out searchable.pdf
manualforge ocr "service-manual.pdf" --out test.pdf --pages 6,29,31 --verify-ink
```

The source is never written to. `--verify-ink` re-renders both files to prove the page images are
unchanged. `manualforge inspect <file.pdf>` reports page count, sizes and any existing text.

### Search

```powershell
manualforge search "crystal oscillator troubleshooting" --library D:\Manuals
manualforge search ":WAVeform:SOURce" --library D:\Manuals --model 54845A
```

- **`--model`:** ranks that instrument's manuals higher without hiding the others.
- **`--limit <n>`:** shows more than the default ten results.
- **Command syntax:** command syntax pasted from a manual is understood as notation. `:` means all
  of these, `{a|b}` means either, and `<N>` is a placeholder.
- **`[OCR]` hits:** a hit marked `[OCR]` matched text that ManualForge's recogniser read off the
  page, and shows its confidence. Check those against the page image before sending the string to
  an instrument: `0`/`O`, `1`/`l`/`I`, `5`/`S` and `8`/`B` are easy to confuse.
- **No results:** a "not found" says whether the library has been fully audited. Until it has, a
  miss is not proof the text is absent.

For programs, `--json` returns one object per hit plus caveats, and `--files-only` returns ranked
paths one per line. The exit code is `0` with results, `2` when nothing matched, and `1` when it
could not search, for example because there is no index.

### The desktop app

Start **ManualForge** from the Start menu. It opens on the folder it was last closed on, or the
first time, on the library the installer recorded. To open it on another folder from a terminal:

```powershell
& "$env:LOCALAPPDATA\Programs\ManualForge\ManualForge.App.exe" D:\Manuals
```

A build from source puts the app in `app\ManualForge.App.exe` instead. The bottom of the window says
which version is running.

The app does the same work with live progress:
- survey a library, adjust what will be done per class, and run it, with throughput, GPU use and
  problems shown as they happen, and a report to export. The survey's table says, for each class,
  how many files are already done; a run leaves those alone
- in the Doctor tab, review what the audit found, see the picture the detector worked from for any
  page, and repair the documents you tick
- in the Search tab, build or update the index and search it

Cancel keeps every page already recognised, so running again carries on.

### Asking Claude

Once connected (see [Connect Claude and other apps](#connect-claude-and-other-apps)), Claude has three tools:

| Tool | What it does |
|---|---|
| `library_search` | Every page, ranked: the manual, the page and a snippet. |
| `read_manual_page` | The text of a page and, optionally, its neighbours. |
| `library_status` | What is indexed and how old it is, which PDFs are missing from the index and why, and whether the library has been audited. |

ManualForge also works alongside [GPIB-MCP](https://github.com/TGoodhew/GPIB-MCP), whose
`manual_search` is better when the instrument model is known. Each works without the other.

### Reading files again after an update

When a new version reads better, finished files can be read again from their kept originals:

```powershell
./tools/reread-library.ps1 -Plan    # what it would do and when it should finish; runs nothing
./tools/reread-library.ps1          # read everything again, then audit, repair and re-index
```

It reads the library named by `MANUALFORGE_LIBRARY`, which the installer sets. Add
`-Library D:\Manuals` to name another, or if that variable is not set.

The script runs unattended, which suits an overnight run:
- it keeps the PC awake and logs to `%LOCALAPPDATA%\ManualForge\reread`
- if it is interrupted, running it again carries on from where it stopped

Each replaced copy is kept in `_Originals\_superseded\`.

To read just some files again, list their paths in a text file, one per line:

```powershell
manualforge run D:\Manuals --redo-completed --documents files.txt
manualforge doctor D:\Manuals
manualforge repair D:\Manuals --include-scans --documents files.txt
manualforge index D:\Manuals
```

### Other commands

| Command | What it does |
|---|---|
| `manualforge reconcile <library>` | Which PDFs are not in the index, and why. `--trim-missing` also removes indexed files that are gone. |
| `manualforge index <library> --reindex --compact` | Read every document into the index again and shrink it; worth doing after a large repair. Files that are gone stay in the index until `reconcile --trim-missing`. |
| `manualforge index <library> --sidecars <folder>` | Also write a plain-text file per manual. |
| `manualforge doctor <file.pdf> --explain <page> --dump page.png` | Show why one page was or wasn't flagged. |
| `manualforge survey <library> --trim-missing` | Forget records of files you have deleted or moved. |
| `manualforge doctor <library> --trim-missing` | The same for the audit: forget findings and repairs of files that are gone. Files `run` renames are forgotten under their old names automatically, in the audit and the index. |
| `manualforge gpu` / `manualforge version` | Which execution provider is active, and how many pages it reads at once on your card; which build is installed. |
| `manualforge gpu --retune` | Forget how many pages your card reads at once, so the next run learns it again. Measured speeds are kept. |
| `manualforge setup gpu --accept-nvidia-licences` / `--remove` | Download CUDA, cuDNN and TensorRT from NVIDIA into ManualForge's own folder, without administrator rights; or take them away again. Without `--accept-nvidia-licences` it lists what it would download, and the licences, and stops. |
| `manualforge setup mcp` / `--list` / `--remove` | Register the MCP server with every app on the machine that can use it; see where it is registered; or take it out again. |

`manualforge` with no arguments lists every command and option.

### Logs

Each run writes a log to `%LOCALAPPDATA%\ManualForge\logs\manualforge-YYYYMMDD.jsonl`, one JSON
object per line. A new file starts each day and files are kept for 30 days. Every run also ends with
a summary on screen.

## Uninstall

Open **Settings › Apps › Installed apps**, find **ManualForge**, and choose **Uninstall**. **Your
library is never touched.** The searchable PDFs stay searchable in any reader, and your originals stay
in `_Originals` until you decide what to do with them (below).

The uninstaller:
- stops ManualForge if it is running, and says which app started each MCP server;
- takes ManualForge out of every AI app it was added to, and puts back any entry it replaced. An app's
  settings that nothing else has changed since go back exactly as they were;
- removes the GPU libraries ManualForge downloaded, the program, and the Start menu entry. It also
  removes the `PATH` entry and the `MANUALFORGE_LIBRARY` variable, if it was the one that set them;
- asks whether to remove the models, logs and what ManualForge learned about your GPU
  (`%LOCALAPPDATA%\ManualForge`);
- leaves the runtimes in place, because other apps may use them. Its last message lists the ones it
  installed, so you can remove them from Settings › Apps if nothing else needs them.

**Then decide what to do with your library.** You have two choices:

- **Keep the searchable copies.** Delete `<library>\_Originals` once you are sure you no longer
  want the originals. It holds the only untouched copy of every file ManualForge replaced.
- **Put the originals back.** Copy them over the searchable copies, check them, and only then
  delete `_Originals`:

  ```powershell
  $lib  = 'D:\Manuals'
  $orig = Join-Path $lib '_Originals'
  $skip = (Join-Path $orig '_superseded'), (Join-Path $orig '_replaced')

  # List what would be copied back; /L copies nothing.
  robocopy $orig $lib *.pdf /S /COPY:DAT /R:1 /W:1 /L /XD $skip

  # Copy the originals back over the searchable copies.
  robocopy $orig $lib *.pdf /S /COPY:DAT /R:1 /W:1 /XD $skip

  # Check every original now matches the library byte for byte. This must print nothing.
  Get-ChildItem $orig -Recurse -File -Filter *.pdf |
      Where-Object { $_.FullName -notmatch '\\_Originals\\(_superseded|_replaced)\\' } |
      ForEach-Object {
          $copy = Join-Path $lib $_.FullName.Substring($orig.Length + 1)
          if ((Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { "DIFFERS: $copy" }
      }

  # Remove the renamed copies whose originals are now back under the old name.
  Get-ChildItem $lib -Recurse -File -Filter *_repaired.pdf |
      Where-Object { -not $_.FullName.StartsWith("$orig\") } |
      Where-Object { Test-Path -LiteralPath ($_.FullName -replace '_repaired\.pdf$', '.pdf') } |
      Remove-Item -WhatIf    # drop -WhatIf once the list looks right

  # Only once the check printed nothing:
  Remove-Item -LiteralPath $orig -Recurse -Force
  ```

  Never use robocopy's `/MIR` or `/PURGE` here: `_Originals` sits inside the library, and either
  option would delete files.

## Build from source

For working on ManualForge, or installing it without the installer.

### Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- The [Visual C++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)
  14.40 or later, which ONNX Runtime needs
- For the GPU, which is optional but about ten times faster: an NVIDIA card and a current driver
- To build the installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`)

### 1. Build

```powershell
git clone https://github.com/TGoodhew/ManualForge.git
cd ManualForge
dotnet build
dotnet test        # optional: about 680 tests, under a minute, no GPU or network needed
```

To build the installer, run `./tools/build-installer.ps1`. It publishes the program into one folder,
pins the runtimes it chains in, and writes `artifacts\installer\package\ManualForge-Setup-<version>.exe`.
`./tools/fresh-install-test.ps1` tests an installer on a PC, end to end.

### 2. Set up the GPU (optional)

ManualForge needs **CUDA 13** and **cuDNN 9**. CUDA 12 will not load. **TensorRT 10** is optional, and
makes recognition about twice as fast. Once ManualForge is published (step 3), it can fetch only the
libraries it loads, straight from NVIDIA, into a folder of its own:

```powershell
manualforge setup gpu --plan                     # what it would download, and NVIDIA's licences
manualforge setup gpu --accept-nvidia-licences   # download, check each SHA-256, keep only the DLLs
```

That is about 1.7 GB, or 3.6 GB with TensorRT (`--no-tensorrt` leaves it out), into
`%LOCALAPPDATA%\ManualForge\gpu`. Nothing goes on `PATH` and the display driver is never touched.
ManualForge uses these copies ahead of any CUDA install on the machine. On a machine that already has
CUDA, cuDNN and TensorRT, it downloads nothing.

**Or, for development,** a script installs the CUDA toolkit and extracts cuDNN and TensorRT under
`C:\Tools`, then adds them to your user `PATH`. That needs administrator rights:

```powershell
./tools/setup-cuda.ps1 -CheckOnly   # what this machine already has; changes nothing
./tools/setup-cuda.ps1 -WhatIf      # what it would download and install, and how large
./tools/setup-cuda.ps1              # install what is missing
```

It never replaces a newer display driver. `-SkipTensorRt` leaves TensorRT out.

### 3. Publish

```powershell
$target = "$env:LOCALAPPDATA\Programs\ManualForge"
dotnet publish src/ManualForge.Cli/ManualForge.Cli.csproj -c Release -o $target
dotnet publish src/ManualForge.Mcp/ManualForge.Mcp.csproj -c Release -o $target
dotnet publish src/ManualForge.App/ManualForge.App.csproj -c Release -o "$target\app"
```

That gives you:
- **the command line**, `manualforge.exe`
- **the MCP server**, `ManualForge.Mcp.exe`
- **the desktop app**, `app\ManualForge.App.exe`

To run `manualforge` from any folder, add the program folder to your user `PATH` once:

```powershell
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($path -notlike "*$target*") { [Environment]::SetEnvironmentVariable('Path', "$path;$target", 'User') }
```

Open a new terminal, then fetch the OCR models and check the install:

```powershell
manualforge setup models
manualforge version
manualforge gpu        # "Active provider" says TensorRt or Cuda on a GPU; if it says Cpu, it says why
```

Then connect the AI apps with `manualforge setup mcp` (see
[Connect Claude and other apps](#connect-claude-and-other-apps)).

To update, pull the new source and run the three publish commands again. Publishing fails while the
MCP server is running, so first quit Claude Desktop and any Claude Code session using it, or stop it
directly with `Stop-Process -Name ManualForge.Mcp`.

### Removing a build from source

1. **Stop everything.** Quit Claude Desktop, close any Claude Code session that uses ManualForge,
   and close the app. `Get-Process ManualForge*, manualforge -ErrorAction SilentlyContinue` should
   print nothing.
2. **Disconnect the AI apps:** `manualforge setup mcp --remove` undoes what `setup mcp` recorded.
   `--remove --all` takes out every entry named `manualforge`, whoever added it. By hand instead:
   `claude mcp remove manualforge --scope user` for Claude Code, and delete the `"manualforge"` entry
   from `claude_desktop_config.json` for Claude Desktop.
3. **Remove the GPU libraries** `setup gpu` downloaded: `manualforge setup gpu --remove`. A CUDA
   toolkit or anything else on the machine is left alone.
4. **Remove the program**, and its `PATH` entry if you added one:

   ```powershell
   $target = "$env:LOCALAPPDATA\Programs\ManualForge"
   Remove-Item -LiteralPath $target -Recurse -Force
   $path = ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ -and $_ -ne $target }) -join ';'
   [Environment]::SetEnvironmentVariable('Path', $path, 'User')
   ```

5. **Remove the models, logs and what was learned about your GPU (optional):**
   `Remove-Item -LiteralPath "$env:LOCALAPPDATA\ManualForge" -Recurse -Force`
6. **Decide what to do with your library**, as in [Uninstall](#uninstall).
7. **Remove CUDA, cuDNN and TensorRT (optional)**, if `setup-cuda.ps1` installed them and nothing
   else uses them: uninstall the *NVIDIA CUDA* entries in Settings › Apps, delete the `cudnn-*` and
   `tensorrt-*` folders under `C:\Tools`, and remove the entries `setup-cuda.ps1` added to your user
   `PATH` (CUDA's and cuDNN's `...\bin\x64`, and TensorRT's `...\tensorrt-*\bin`), and its `CUDA_PATH`
   user variable.

## More

- **[docs/DEVELOPMENT-HISTORY.md](docs/DEVELOPMENT-HISTORY.md):** how it was designed, built and
  measured, including what is and isn't measured, the safety guarantees in detail, the project
  layout and the tests.
- **[docs/measurements/](docs/measurements/):** the dated measurement reports.
- **[docs/UNDER-EXTRACTION.md](docs/UNDER-EXTRACTION.md):** how the audit finds incomplete pages.
