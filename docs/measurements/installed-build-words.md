# The installed build reads every word as a build from source does (#26)

11 Oct 2026, RTX 5070 Ti, driver 617.42, Windows 11 Pro 26H2.

#26 asked that the installed build read the seven `_compare` books exactly as a run on the old
`C:\Tools` setup did. That setup has since gone from this PC, and so has the CUDA toolkit. But
`installer-gpu-libraries.md` already showed that NVIDIA's archives hold the same DLLs as the toolkit,
byte for byte, and that NVRTC is never loaded. What was left to show was that the installer's own way
of building and laying out the program changes nothing read. The installer's build is one
framework-dependent folder, using the shared .NET and Windows App runtimes, with no `.pdb`.

## How

`tools/compare-installed-build.ps1` reads each book twice per engine:

- **installed:** `%LOCALAPPDATA%\Programs\ManualForge\manualforge.exe`, from
  `ManualForge-Setup-0.1.0+76f9409.exe` (draft release `installer-test-76f9409`), installed with
  CUDA, cuDNN and TensorRT;
- **repository:** this repository's Release build. That was `9a6adef`, whose `src` is the same as
  `76f9409`; only the README and tools differ.

Both load NVIDIA's libraries from `%LOCALAPPDATA%\ManualForge\gpu`, which the installer downloaded, and
share one TensorRT engine cache.

Each book is read whole with `manualforge ocr --engine <engine> --text`, and the text written is
compared word by word, in order. `431C-OSM.pdf` has an owner password, which `ocr` refuses. It was
read instead with `manualforge run --policy GoodText=redo` in a library of its own, which flattens it
first, and its words were taken from the searchable copy (`tools/pdf-words.cs`).

## Result

| Book | TensorRT words | Same | CUDA words | Same |
|---|---|---|---|---|
| circuit | 13,264 | yes | 13,263 | yes |
| picked | 6,934 | yes | 6,947 | yes |
| schematic | 5,455 | yes | 5,458 | yes |
| table | 11,803 | yes | 11,812 | yes |
| tableonly | 50,995 | yes | 50,999 | yes |
| typical | 36,846 | yes | 36,867 | yes |
| 431C-OSM | 27,586 | yes | 27,592 | yes |

All 14 pairs are identical, word for word. The two engines differ from each other by a few words a
book. That is expected: with the classifier and recogniser on TensorRT, the default, about 1.0% of
words are read differently from CUDA (`tensorrt.md`). The 0.6% that #34 measured was the recogniser
alone. Each pair took the same time within a few seconds,
so neither build fell back to another provider.

Together with `installer-gpu-libraries.md`, this means the installer's layout reads exactly as the
setup every earlier measurement was made with.
