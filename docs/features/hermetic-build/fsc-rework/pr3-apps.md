# PR 3: applications on the recorded lock (2026-10-16)

Summary. The full report with the trial-project numbers is kept outside this repository.

## Design

- An entry whose project has runtime output (exe, winexe, .NET test project) gets an `App`
  section, every value from msbuild's design-time build: `OutputType`, `Host` (RID, apphost pack),
  `Files` (runtimeconfig.json, deps.json, the app's own config, recorded beside the lock as LF
  text with sha256), `Copy` (`ReferenceCopyLocalPaths` in msbuild's order), `Related`,
  `BindingRedirects` (a recorded fact; generation is open).
- deps.json of referenced projects: a second msbuild pass over the applications sees stand-in
  outputs with a marker version; the marker becomes `$(AssemblyVersion:<file>)` /
  `$(FileVersion:<file>)` tokens resolved by the build from the copied file. A marker left in an
  unknown layout fails the import, naming the file.
- Apphost rule: no RID -> the pinned SDK's own RID and pack, identified by the SDK version;
  explicit RID -> `pack:` identity with sha256. Written by the SDK's `HostWriter` (one per
  process), else a byte-patch fallback with a warning (GUI subsystem set for WinExe).
- An application's `App.config` and `runtimeconfig.template.json` join `Evaluation.Imports`: an
  edit fails the build with "run the import again". `FileHash.sha256` memoises hashing per process.
- Not yet: binding redirects, satellites, copy items (PR 4), self-contained publishing.

## Tests

hermetic.tests 263 (+13 in `src/hermetic.tests/AppTests.fs` and fixes): stand-in tokens, host
strings, App round trip; e2e import of a library and four apps (console, net472 WinExe, explicit
RID, Test.Sdk): import after Xake's and msbuild's builds records the same locks, file sets and
runtime files equal msbuild's, the apps run.

## Verification

Verified on a large customer solution (94 projects, 4 build variants): metadata parity with
msbuild for every assembly, import -> build -> import -> `dotnet build` -> import stable,
runtimeconfig equal everywhere; apphost byte-identical with the SDK's HostWriter.
