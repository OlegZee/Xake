# PR 1: dotnet resx, csc crash diagnostics, platform monikers

Branch `pr/dotnet-resx` from `origin/dev` (16cf289). Source: `git diff 74bf4d9..pr/apphost-resx`
(night branches), taken by hunks, not by commits (the night commits mix hermetic changes).

## Taken

- `src/dotnet/ResxFormat.fs` (new): `.resx` -> `.resources` as the .NET SDK's `GenerateResource`
  does it (`MSBuildResXReader` + `PreserializedResourceWriter`): strings, `ResXNullRef`,
  `ResXFileRef` (text in the named encoding, code pages through one reflective
  `CodePagesEncodingProvider` registration (S34); `byte[]`, `MemoryStream`, other types as
  activator streams: images), base64 `byte[]`, string-converted values, `bytearray.base64`,
  `binary.base64`, aliases, first-wins for repeated names (S16). `readerTypeOf` /
  `needsExtensions` (detect the preserialized format), `fileRefs`, `refresh`.
  Changed from the night copy: the `#if HERMETIC` namespace switch and its header comment
  dropped (on dev `src/hermetic` does not link this file).
- `Resx.compile` / `Resx.refresh` delegate to `ResxFormat`; `Resx.read` stays the strings view.
- `Impl.compileResx`: one body for both TFMs (`Resx.compile`). New vs the night: the night kept
  the net462 `ResXResourceReader` body (classic BinaryFormatter format); here net462 writes the
  same bytes as netstandard2.0. The doc comment describes the new state.
- `System.Resources.Extensions` 8.0.0 package (fsproj, `xake.project.json` via `import`),
  `locks/Xake.Dotnet/Release.json` and `xake.fingerprints` regenerated (`import`,
  `update-locks`; the night branch had not updated the lock), the hermetic.tests fixture
  `fixtures/src/dotnet/xake.project.json` copied from the new description.
- `CompilerRunner`: a compiler exiting non-zero with no error line logs the exit code (134/-6
  named as an abort), the last 200 output lines, and keeps the response file (S17).
- `DotNetFwk.netcoreMoniker`: a platform framework (`net8.0-windows`,
  `net8.0-windows10.0.19041`) is its base (`net8.0`).
- Tests: `ResxTests` (msbuild byte-compare for typed and file-ref entries, Integration; parseFileRef,
  code-page ResXFileRef, classic vs preserialized + `refresh`, the latter three ported from
  hermetic.tests into src/tests and written beside the fixture folder, which the msbuild tests
  glob), `CompilerCrashTests` (fake csc exiting 134, non-Windows), `FscTests` moniker asserts.
- `docs/csc-syntax.md` "Not yet": the resx bullet updated.

## Left on the night branches

- Everything in `src/hermetic` (Described/Desktop, the S.R.E guard for .NET Framework preserialized
  resx, linking `ResxFormat.fs` with `HERMETIC`, apphost, WPF/WinForms import) and its tests.
- `src/core` night changes (ArtifactDep2 stamp, database 0.5): already on dev as #54.
- `Dotnet.resgen.fs` keeps its net462 `ResXResourceReader` body (`UseSourcePath` semantics differ;
  not in the night diff).
- The `System.Windows.Forms` reference on net462 stays (still used by `Dotnet.resgen.fs`).

## Verified

`dotnet fsi build.fsx -- -- build test`: tests 338 passed / 1 skipped (339), hermetic.tests 206/206.
`dotnet fsi build.fsc.fsx -- -- build check -d NUGET_FETCH=on`: green after the lock update.
