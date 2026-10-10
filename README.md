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

It works on any NVIDIA GPU with CUDA 13, at about 85–90 pages a minute on an RTX 5070 Ti. It also
runs on the CPU alone, about 15 pages a minute.

A note on accuracy: what ManualForge measures is how *much* text it recovers. How *correctly* it
reads a scanned page has not been measured. The details, and the full story of how ManualForge was
built and measured, are in [docs/DEVELOPMENT-HISTORY.md](docs/DEVELOPMENT-HISTORY.md).

## Install

There is no installer yet ([#26](https://github.com/TGoodhew/ManualForge/issues/26)), so
ManualForge is built from source and published into place.

### Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For the GPU, which is optional but about seven times faster: an NVIDIA card and a current driver

### 1. Build

```powershell
git clone https://github.com/TGoodhew/ManualForge.git
cd ManualForge
dotnet build
dotnet test        # optional: about 500 tests, a few seconds, no GPU or network needed
```

### 2. Set up the GPU (optional)

ManualForge needs **CUDA 13** and **cuDNN 9**. CUDA 12 will not load. A script installs both and
checks the result:

```powershell
./tools/setup-cuda.ps1 -CheckOnly   # what this machine already has; changes nothing
./tools/setup-cuda.ps1 -WhatIf      # what it would download and install, and how large
./tools/setup-cuda.ps1              # install what is missing
```

It installs the CUDA toolkit, extracts cuDNN under `C:\Tools`, and adds both to your user `PATH`.
It never replaces a newer display driver. Skip this step to run on the CPU.

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

Open a new terminal, then check the install:

```powershell
manualforge version
manualforge gpu        # "Active provider : Cuda" on a GPU; if it says Cpu, the hint names the missing DLL
```

The OCR models (about 20–40 MB) download on first use into `%LOCALAPPDATA%\ManualForge\models`.
After that, everything runs offline.

### 4. Connect Claude (optional)

The MCP server lets Claude Desktop and Claude Code search the library and read pages. Point
`MANUALFORGE_LIBRARY` at your library folder.

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

Pull the new source and run the three publish commands again. Publishing fails while the MCP server
is running, so first quit Claude Desktop and any Claude Code session using it, or stop it directly:

```powershell
Stop-Process -Name ManualForge.Mcp -ErrorAction SilentlyContinue
```

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
  before. Uninstall step 5 shows how to put the originals back.

`_Originals` is also where ManualForge keeps its working files:

| Path | Contents |
|---|---|
| `_Originals\<same relative path>` | the untouched original of every file that was replaced |
| `_Originals\_superseded\` | the previous searchable copy, kept whenever a file is read again from its original |
| `_Originals\manualforge.db` | progress, and recognition cached so an interrupted run resumes |
| `_Originals\manualforge-doctor.db` | what the audit found, and the text `repair` recovered |
| `_Originals\manualforge-index.db` | the search index |

Leave `_Originals` where it is and don't edit what's in it. It is the only copy of every original
that was replaced.

`doctor`, `repair`, `index` and search never change a PDF. Text that `repair` recovers is kept in
`manualforge-doctor.db` and added to the search index, not written into the file. Folders named
`_Originals`, `BASELINE` and `_GroundTruth` are never processed or indexed.

### Process a library

The usual sequence, each step safe to stop with Ctrl+C and run again:

```powershell
manualforge survey D:\Manuals     # classify every file and show what would be done; changes nothing
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

```powershell
& "$env:LOCALAPPDATA\Programs\ManualForge\app\ManualForge.App.exe" [D:\Manuals]
```

The app does the same work with live progress:
- survey a library, adjust what will be done per class, and run it, with throughput, GPU use and
  problems shown as they happen, and a report to export
- in the Doctor tab, review what the audit found, see the picture the detector worked from for any
  page, and repair the documents you tick

Cancel keeps every page already recognised, so running again carries on.

### Asking Claude

Once connected (Install, step 4), Claude has three tools:

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
| `manualforge index <library> --reindex --compact` | Rebuild the index from scratch and shrink it; worth doing after a large repair. |
| `manualforge index <library> --sidecars <folder>` | Also write a plain-text file per manual. |
| `manualforge doctor <file.pdf> --explain <page> --dump page.png` | Show why one page was or wasn't flagged. |
| `manualforge survey <library> --trim-missing` | Forget records of files you have deleted or moved. |
| `manualforge doctor <library> --trim-missing` | The same for the audit: forget findings and repairs of files that are gone. Files `run` renames are forgotten under their old names automatically, in the audit and the index. |
| `manualforge gpu` / `manualforge version` | Which execution provider is active; which build is installed. |

`manualforge` with no arguments lists every command and option.

### Logs

Each run writes a log to `%LOCALAPPDATA%\ManualForge\logs\manualforge-YYYYMMDD.jsonl`, one JSON
object per line. A new file starts each day and files are kept for 30 days. Every run also ends with
a summary on screen.

## Uninstall

Uninstalling removes the program. **Your library is never touched.** The searchable PDFs stay
searchable in any reader, and your originals stay in `_Originals` until you decide what to do with
them (step 5).

**1. Stop everything.** Quit Claude Desktop, close any Claude Code session that uses ManualForge,
and close the app. This should print nothing:

```powershell
Get-Process ManualForge*, manualforge -ErrorAction SilentlyContinue
```

**2. Disconnect Claude.**

```powershell
claude mcp remove manualforge --scope user
```

For Claude Desktop, delete the `"manualforge"` entry from `claude_desktop_config.json`.

**3. Remove the program**, and its `PATH` entry if you added one:

```powershell
$target = "$env:LOCALAPPDATA\Programs\ManualForge"
Remove-Item -LiteralPath $target -Recurse -Force
$path = ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ -and $_ -ne $target }) -join ';'
[Environment]::SetEnvironmentVariable('Path', $path, 'User')
```

**4. Remove the models and logs (optional):**

```powershell
Remove-Item -LiteralPath "$env:LOCALAPPDATA\ManualForge" -Recurse -Force
```

**5. Decide what to do with your library.** You have two choices:

- **Keep the searchable copies.** Delete `<library>\_Originals` once you are sure you no longer
  want the originals. It holds the only untouched copy of every file ManualForge replaced.
- **Put the originals back.** Copy them over the searchable copies, check them, and only then
  delete `_Originals`:

  ```powershell
  $lib  = 'D:\Manuals'
  $orig = Join-Path $lib '_Originals'
  $skip = (Join-Path $orig '_superseded'), (Join-Path $orig 'broken')

  # List what would be copied back; /L copies nothing.
  robocopy $orig $lib *.pdf /S /COPY:DAT /R:1 /W:1 /L /XD $skip

  # Copy the originals back over the searchable copies.
  robocopy $orig $lib *.pdf /S /COPY:DAT /R:1 /W:1 /XD $skip

  # Check every original now matches the library byte for byte. This must print nothing.
  Get-ChildItem $orig -Recurse -File -Filter *.pdf |
      Where-Object { $_.FullName -notmatch '\\_Originals\\(_superseded|broken)\\' } |
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

**6. Remove CUDA and cuDNN (optional).** Only do this if nothing else on the machine uses them:
- uninstall the *NVIDIA CUDA* entries in Settings › Apps
- delete the `cudnn-*` folder under `C:\Tools`
- remove the two `...\bin\x64` entries that `setup-cuda.ps1` added to your user `PATH`, and its
  `CUDA_PATH` user variable

## More

- **[docs/DEVELOPMENT-HISTORY.md](docs/DEVELOPMENT-HISTORY.md):** how it was designed, built and
  measured, including what is and isn't measured, the safety guarantees in detail, the project
  layout and the tests.
- **[docs/measurements/](docs/measurements/):** the dated measurement reports.
- **[docs/UNDER-EXTRACTION.md](docs/UNDER-EXTRACTION.md):** how the audit finds incomplete pages.
