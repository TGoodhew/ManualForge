# The PaddleOcrNet fork

ManualForge builds against a fork of [PaddleOcrNet](https://github.com/FarhanLodi/PaddleOcrNet), not
the package on nuget.org. The fork adds one thing: an opt-in TensorRT execution provider, which doubled
recognition speed here (#34, `docs/measurements/recognition-shape-changes.md`). It's a stopgap until
upstream releases TensorRT support. The fork is kept in a state upstream can take as a pull request,
and upstream's own changes are taken in as they come.

| | |
|---|---|
| Fork | [TGoodhew/PaddleOcrNet](https://github.com/TGoodhew/PaddleOcrNet), branch `feature/tensorrt` |
| Based on | upstream `main` at 2.2.1 |
| Upstream request | [FarhanLodi/PaddleOcrNet#11](https://github.com/FarhanLodi/PaddleOcrNet/issues/11) |
| ManualForge issue | #53 |
| Packages | `packages/PaddleOcrNet.<version>-tensorrt.<n>.nupkg` and `PaddleOcrNet.Gpu.…`, committed |
| Source mapping | `nuget.config`: PaddleOcrNet comes only from `packages/`, never from nuget.org |

## What the fork changes

- `OcrExecutionProvider.TensorRt`, which `Auto` never selects, with `PaddleOcrServiceOptions.TensorRt`
  (`TensorRtOptions`: engine cache, FP16, and the shape limits).
- The detector, line classifier and recognizer each get a TensorRT engine with its own shape profile.
  This has to be per model, because all three name their input `x`. Everything else stays on CUDA.
- If TensorRT can't be loaded, OCR runs on CUDA, not CPU. If one model's engine can't be built, that
  model alone falls back to CUDA.
- Unit tests, README and CHANGELOG, following upstream's `CONTRIBUTING.md`: no API breaks, no changed
  defaults, documented public members, no new dependencies.

## Taking upstream's changes

```powershell
./tools/build-paddleocrnet.ps1           # fetch upstream, merge, build, test, repack, repin
dotnet build ManualForge.slnx; dotnet test ManualForge.slnx
git add packages Directory.Packages.props
git commit -m "Take PaddleOcrNet <version> (fork <commit>)"
```

What the script does:

1. Merges `upstream/main` into the branch. It merges rather than rebases, so the branch stays usable
   as a pull request.
2. Builds the fork with warnings as errors, and runs its unit tests.
3. Packs the next `-tensorrt.<n>` version. NuGet caches a package by its version, so re-using a
   version restores the old build.
4. Replaces `packages/` and pins `Directory.Packages.props` to the new version.

On a merge conflict it stops. Resolve the conflict in the fork, commit, and run it again with
`-NoMerge`. `-Push` also pushes the fork's branch.

If you change the fork yourself, commit in the fork, then run with `-NoMerge`.

Upstream's integration tests need models and a GPU, and the script doesn't run them. Run them by
hand before offering anything upstream:

```powershell
$env:PADDLEOCRNET_RUN_INTEGRATION = 1
dotnet test ..\PaddleOcrNet\test\PaddleOcrNet.Tests\PaddleOcrNet.Tests.csproj -c Release
```

## Offering it upstream

The plan is a pull request from `feature/tensorrt` to FarhanLodi/PaddleOcrNet, opened only once
ManualForge has proved it against the baselines. It should open by saying an AI wrote it, link #11,
include the before/after measurements and the integration-suite result, and offer logs.

## Retiring the fork

When a PaddleOcrNet release carries TensorRT support (the script warns once upstream `main` has it):

1. Delete `packages/`, and remove the `local` source and its mapping from `nuget.config`.
2. Pin the released version in `Directory.Packages.props`.
3. Check that the released API matches `OcrExecutionProvider.TensorRt` and `TensorRtOptions`, and
   adjust `Ocr/PaddleOcrEngine.cs` if it doesn't.
4. Re-measure against the baselines: a different build of the same provider can still read slightly
   differently.
