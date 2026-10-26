# Import and msbuild: the version floor and errors in the log (analysis, 2026-10-26)

Two roadmap items (`docs/roadmap.md:190-204`): "The MSBuild an import needs, checked up front" and
"msbuild errors of an import in the log". Read-only analysis of `feature/hermetic-windows`; line
numbers are of this branch. Facts marked *probed* were checked with SDK 8.0.426 (MSBuild 17.11) on macOS.

## 1. The MSBuild an import needs

### Root cause: confirmed, but the version in the roadmap is wrong

Where `-getResultOutputFile` is passed (every import entry point):

| Entry point | Call | Switches |
|---|---|---|
| `Import.record` / `Import.descriptions` -> `evaluate` | `src/hermetic/Import.fs:649-652` | `-getProperty`, `-getItem`, `-getResultOutputFile` (plus `-pp` at :653) |
| `Project.import`, restore | `src/hermetic/Project.fs:438-442` | `-t:Restore -getProperty -getResultOutputFile` |
| `Project.import`, design-time build | `src/hermetic/Project.fs:467-472` | `-t:PrepareResources;Compile -getItem -getProperty -getResultOutputFile` |

`Import.record`'s restore of `import.sln` (`Import.fs:1706-1712`) and its traversal design-time
build (`Import.fs:1789-1791`, a dump target writes files) pass no `-get*` switch.

Versions:
- `-getProperty`, `-getItem`, `-getTargetResult`: MSBuild 17.8 (learn.microsoft.com, "Evaluate items
  and properties": "available in MSBuild 17.8 and later") = SDK 8.0.100 band.
- `-getResultOutputFile`: dotnet/msbuild PR #9640 "Permit specifying output file", merged to `main`
  2024-02-28; it is in the `v17.9.5...v17.10.4` compare, so **MSBuild 17.10 = SDK 8.0.300 band**,
  not 17.9 / 8.0.200 as the roadmap says. 8.0.2xx fails too. (Band map: 8.0.1xx=17.8, 8.0.2xx=17.9,
  8.0.3xx=17.10, 8.0.4xx=17.11.) Confidence high; one confirming run on an 8.0.2xx SDK would settle it.
- Nothing else in the import needs a newer floor: CPM is NuGet 6.2 (SDK 6.0.300), `RestoreRecursive`,
  `-nodeReuse`, the `MSBuild` task's `AdditionalProperties` and `-pp` are older. So the floor is
  **MSBuild 17.10 / .NET SDK 8.0.300**, set only by `-getResultOutputFile`.

Inaccuracy in the roadmap's story: on 8.0.1xx, `record`'s restore of `import.sln` has no `-get*`
switch, so MSB1001 cannot come from it. The first MSB1001 is in `evaluate` (`Import.fs:649`), and
`Tool.failOnExitCode` names the *project file* there (`msbuild ... projectFile`, `Import.fs:634`),
not `import.sln`. A "FailOnError set on ...import.sln" therefore means the restore itself failed,
for another reason (a feed, NU1008, ...): the case of item 2, hidden by item 2's logging gap. Only
`Project.import` fails with MSB1001 in its restore (it passes the switch there).

*Probed* MSB1001 output: `MSBUILD : error MSB1001: Unknown switch.` then `Switch: -bogusSwitch:x`,
all on stdout, exit 1. Not recognized by the classifier (section 2), so it lands at Verbose too.

Where the SDK version is already known without an extra process:
- `DotNetFwk.sdkImpl.probeSdk` (`src/dotnet/DotNetFwk.fs:432-470`) runs `dotnet --version` in the
  project root once per root (memoized, `:475-479`) and keeps `<dotnetRoot>/sdk/<version>`; `sdkDir`
  is private (`:487`). Public today: only `sdkFramework` (`:1134`), `sdkProbeWarning` (`:1127`),
  `globalJsonPin` (`:816`). A public `sdkVersion projectRoot` (= `Path.GetFileName sdkDir`) is a
  one-liner; the compile tasks probe the same root anyway, so the process is shared.
- The MSBuild version is in `<sdkDir>/MSBuild.deps.json` (`"Microsoft.Build/17.11.69"` for 8.0.426,
  *probed*): a file read, no process. Exact, where the band map is a table to maintain.
- After the fact, `NETCoreSdkVersion` comes back from every evaluation (`Import.fs:1329`, `:1460`;
  `Project.fs:516` already compares it with the pin) -- too late, the evaluation is what fails.
- Caveat: the probe runs in `ProjectRoot` (`DotNetFwk.fs:444`), `runMsbuild` sets no working
  directory (`Project.fs:94-103`), so `dotnet msbuild` resolves `global.json` from the process cwd.
  Same in the usual case (cwd = project root); a script run from elsewhere could check one SDK and run another.

### Options

- **A. Pre-check from the probe (recommended).** One function in `Project` (`requireImportMsbuild root`),
  called first in `Import.record`, `Import.descriptions` and `Project.import` (before any restore),
  memoized per root. Reads the probed SDK dir, takes the MSBuild version from `MSBuild.deps.json`
  (fallback: the SDK band), fails below 17.10. Message, by `globalJsonPin`:
  - pinned: `the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+); global.json (<file>) selects SDK 8.0.121 (MSBuild 17.8): raise the pin`
  - not pinned: `... found SDK 8.0.121 (MSBuild 17.8), the newest installed: install SDK 8.0.300 or later`
  Cost: no process beyond the shared probe. Needs a new public in `Xake.Dotnet`, so a Xake release
  and a raised lower bound of the `Xake` reference (`Xake.Hermetic.Dotnet.fsproj:82`, today `[3.11.0.40, 3.12)`);
  or do the probe-free part in the hermetic package (find `sdk/<v>` by `dotnet --version` itself).
- **B. `dotnet msbuild -version` once (the roadmap's proposal).** Exact and independent of the probe
  and of cwd differences (run it with the same cwd as the imports), one extra process (~0.3-1 s,
  msbuild startup) per import. No Xake release needed. Good fallback if A's coupling is unwanted.
- **C. No pre-check; recognize MSB1001 naming `getResultOutputFile` in the failed run** and rewrite
  the failure. Zero cost on success, but fires after the restore (minutes on a big solution) and
  only for this one switch.

Per evaluation is the wrong place: dozens of concurrent evaluations would report the same thing;
once per import call, before the restore, is enough. Docs: a "Requirements" line at the top of
`docs/hermetic/description.md` "Import that records msbuild's compile inputs" (`:103`), a row in
`docs/hermetic/workflows.md` "G. Troubleshooting" (`:703`), the release notes.

### Effort, risks, tests

S: ~0.5 day for A or B with docs. Risks: preview SDK versions (`9.0.100-rc.1...`) need a tolerant
parse; a missing `MSBuild.deps.json` (unusual layout) must fall back, not fail. Tests: pure unit
tests of the decision (`8.0.121` pinned/unpinned -> fail with the right text; `8.0.204` -> fail;
`8.0.300`, `10.0.100`, a preview -> pass; unreadable -> pass with no check). No CI run on an old SDK
is practical.

## 2. msbuild errors of an import in the log

### Root cause: confirmed, with one nuance

- `Project.runMsbuild` (`src/hermetic/Project.fs:91-104`): stdout through
  `Tool.diagnosticLevel Level.Verbose`, stderr through `Tool.diagnosticLevel Level.Error`; on a
  non-zero exit `Tool.failOnExitCode` (`src/dotnet/Tool.fs:59-63`) traces `('<name>') failed with
  exit code '1'` and throws `Exiting due to FailOnError set on '<name>'`.
- `Tool.diagnosticLevel` (`Tool.fs:45-50`; `DotnetTasks.levelFromString` forwards, `DotnetTasks.fs:114`)
  knows two shapes only: `"): error "` / `"): warning "` (a source position) and a line starting with
  `error ` / `warning `. msbuild's positionless form `<origin> : error CODE: ...` matches neither.
- *Probed* (all stdout, exit 1): restore of a project and of a solution:
  `/p/a.csproj : error NU1101: Unable to find package ... [/p/s.sln]` (the `[...]` suffix names the
  entry project; for the import it is the temp `import.sln`); NU1008/NU1605/NU1301 have the same shape.
  `MSBUILD : error MSB1001: Unknown switch.` likewise. All -> Verbose.
- `filelog ... Chatty` keeps Message/Error/Command/Warning/Info, not Verbose (`src/core/Logging.fs:46-47`).
- Nuance: positioned errors (`a.csproj(12,5): error MSB4025`) already reach Error. And
  `-getResultOutputFile` itself moves errors: without it msbuild's get* mode sends errors to stderr
  (*probed*; PR #9640 says so), which `runMsbuild` logs at Error. With the file the normal console
  logger is back and errors go to stdout. So every evaluation error is hidden by the switch the
  import needs.
- The same gap in the other runners using the same classifier: `Described.runRestore`
  (`src/hermetic/Described.fs:217-239`, both streams Verbose), `DotNetFwk.fs:953-954`, the msbuild
  task (`src/dotnet/Dotnet.Msbuild.fs:100-101`), and csc's `CSC : error CS2001` (the compiler runner
  has a fallback, `src/dotnet/CompilerRunner.fs:195-228`: last 200 lines, an error when the tool failed
  without one). `docs/hermetic/workflows.md` G already tells users to re-run restore by hand.

### Options

- **A. Classify lines.** Extend the classifier with `^\S.* : (error|warning) [A-Z]+[0-9]+:` (origin,
  space-colon, kind, code). In `Tool.diagnosticLevel` it fixes every runner at once, csc/fsc included;
  in a local `Project.msbuildLevel` it touches the import only (no Xake release).
- **B. Summarizing failure (recommended together with A).** `runMsbuild` keeps the error lines it saw
  (as the compiler runner does) and fails with
  `msbuild failed on '<name>' (exit 1): <project>: NU1008 ...; <project>: NU1605 ...` (+N more):
  deduplicated, first 5, the `[entry]` suffix stripped, the origin path shortened to the project
  name. Visible at any verbosity, the exception text is what CI shows. With no error line seen,
  keep the last ~20 lines, as `CompilerRunner` does.
- **C. Raise the whole stdout to Info.** Simple, but `-verbosity:quiet` design-time builds of the
  consumer solution still print warnings, and `-pp`/dump runs are many: the Chatty file log would hold
  pages of noise.

Warnings: as Warning level, a consumer solution's design-time builds can print many (NETSDK1138, NU1701,
NU1603, MSB3277), once per framework and concurrently, on the console at `Loud`. Recommend: errors ->
Error; warnings -> Info (in the Chatty file log, not on a Normal console), deduplicated per import
run; `NU1xxx` warnings at Warning only when `TreatWarningsAsErrors` would turn them into errors
(not known to the classifier: so simply Info). `captureOutput` is not involved: `runMsbuild` adds no
`CaptureSpecs`, and a `ToLog` capture only replaces the default log line (`src/core/Tasks/Shell.fs:96-108`),
so the change is in the level functions alone. The `dotnet restore` runs (`Described.runRestore`,
`DotNetFwk` restore) should get the same classifier and the same failure summary (their message
"restoring N package(s) ... failed" already exists: append the first errors).

### Effort, risks, tests

S-M: ~1-1.5 days (classifier, summary in `runMsbuild` and the two restore runners, docs). Risks:
a changed `Tool.diagnosticLevel` changes what the `msbuild` task and csc/fsc show (more errors,
correct ones; a few more warnings), so it is a Xake release with a release note; the signature
stays (binary compatibility, `docs/roadmap.md` "Binary compatibility between the packages"). False
positives: a message text containing ` : error X1:` is unlikely with the code anchor; `Build FAILED.`
and `0 Error(s)` must stay unmatched. Tests:
- unit, `src/tests/DotnetTasksTests.fs:470` extended: `/p/a.csproj : error NU1101: ...`, the same with
  `[/t/import.sln]`, `MSBUILD : error MSB1001: Unknown switch.`, `/p/a.csproj : warning NU1603: ...`,
  `CSC : error CS2001: ...`, negatives `Build FAILED.`, `    0 Error(s)`; the summary builder (dedupe,
  first N, suffix strip) as a pure function;
- integration (`Category("Integration")`, `RecordKit` in `src/hermetic.tests/RecordTests.fs:466-512`, which
  already writes a `FileLog` at `Loud`): a fixture whose restore fails offline and deterministically --
  **NU1008** (`Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and a
  `PackageReference` with `Version`) rather than NU1101/NU1605, which need a feed. Assert the
  exception names `NU1008` and the project, and the log file has the line at Error.

## 3. Open questions for the product owner

1. Floor check source: the probe plus `MSBuild.deps.json` (needs a Xake release) or one `dotnet msbuild -version` in the hermetic package? Default: `dotnet msbuild -version` now, move to the probe with the next Xake release.
2. Fix the classifier in `Tool.diagnosticLevel` (all runners, Xake release) or locally in the hermetic package? Default: `Tool.diagnosticLevel`, released with 3.12; a local copy only if 0.8 ships first.
3. msbuild warnings of an import: Info (file log only) or Warning (console at Loud)? Default: Info, deduplicated.
4. How many errors in the failure message? Default: 5, then "+N more, see the log".
5. Apply the same to the `dotnet restore` runners of `Restore`/`Described`? Default: yes, same PR.
6. Should `runMsbuild` run in `ProjectRoot` so the SDK it uses is the one checked? Default: yes.
7. Correct the roadmap entry (17.10 / 8.0.300; the `import.sln` failure is a restore failure, not MSB1001)? Default: yes, with this analysis. Neighbouring roadmap entries (`:179-216`) also name a consumer and its CI, which the public-repo rule forbids: reword them in the same change.
