# The GPU libraries an installer has to fetch (#26)

The installer is to download NVIDIA's libraries rather than carry them, and to download only what
ManualForge loads. Until now every run was made on a machine with the whole CUDA toolkit installed.
Two things had to be shown before the toolkit installer could be replaced by NVIDIA's per-component
archives:

1. that the archives hold the same libraries the toolkit installs;
2. that NVRTC, which cuDNN can use to compile engines at run time and which the toolkit puts on
   `PATH`, can be left out without changing a word that is read.

## The archives are the toolkit's own files

NVIDIA publishes each CUDA component as a zip with a manifest giving its path, size and SHA-256
(`developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.2.json`). `cuda_cudart` 13.4.92
(2.7 MB) and `libcublas` 13.8.0.4 (422 MB) both matched their manifest's SHA-256. The three DLLs
ManualForge loads from them are **byte-identical** to the toolkit's in
`C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4\bin\x64`:

| DLL | Version | Same bytes as the toolkit's |
|---|---|---|
| `cudart64_13.dll` | 13.4.0 | yes |
| `cublas64_13.dll` | 13.8.0 | yes |
| `cublasLt64_13.dll` | 13.8.0 | yes |

cuDNN 9.26.0.51 for CUDA 13 comes as one archive with its own manifest
(`.../compute/cudnn/redist/redistrib_9.26.0.json`, 1.28 GB). It is the same archive
`tools/setup-cuda.ps1` already extracts into `C:\Tools`.

## NVRTC is never loaded

11 Oct 2026, RTX 5070 Ti, the single-character harness (`tools/measure-single-characters.cs`, config
`engine`) on the two 100-page books in `_compare`:

- **A:** the machine as it is, with the toolkit (and NVRTC) on `PATH`.
- **B:** the installer's layout. `cudart` and `cublas` sit in a folder of their own, found through
  `MANUALFORGE_GPU_LIBRARIES`. The toolkit and `C:\Tools` are off `PATH`, `CUDA_PATH` is unset, and
  NVRTC can't be found. cuDNN and TensorRT came from `C:\Tools` through the fallback search.

| Engine | Book | A: tokens read | B: tokens read | Word for word |
|---|---|---|---|---|
| TensorRT | typical | 17,559 | 17,559 | identical |
| TensorRT | table | 34,152 | 34,152 | identical |
| CUDA | typical | 17,568 | 17,568 | identical, and identical to `p50/typ-A` from before #34 |
| CUDA | table | 34,147 | 34,147 | identical, and identical to `p50/tab-A` |

Every run took the same time either way (107–164 s), so none fell back to the CPU.

A sampler checked every three seconds which modules the harness had loaded:
- **In B,** cuBLAS came from the installer-style folder and cuDNN from `C:\Tools`, as intended.
- **In every run, A included,** cuDNN loaded `cudnn_engines_runtime_compiled64_9.dll` but never
  NVRTC. #34 counted 93,988 runtime-compiled candidates per new input shape; none was ever compiled.

So the installer leaves NVRTC out: 320 MB less to download, and nothing read changes. If a later
cuDNN or model starts compiling engines, the same A/B shows it.

## What the installer fetches

| Component | Download | Kept |
|---|---|---|
| `cuda_cudart` | 2.7 MB | `cudart64_13.dll` |
| `libcublas` | 422 MB | `cublas64_13.dll`, `cublasLt64_13.dll` |
| cuDNN 9.26 (CUDA 13) | 1.28 GB | its DLLs |
| TensorRT 10.16 (optional) | about 1.9 GB | its DLLs |

About 1.7 GB without TensorRT and 3.6 GB with it, into `%LOCALAPPDATA%\ManualForge\gpu\{cuda,cudnn,tensorrt}`.
ManualForge now looks there before anywhere else (`CudaLibraries.ChainedLibrariesRoot`), so nothing
has to be put on `PATH`.
