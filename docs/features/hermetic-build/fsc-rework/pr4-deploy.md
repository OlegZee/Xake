# PR 4: run folders driven by the lock (2026-10-16)

Branch `pr/deploy` (worktree `.claude/worktrees/pr/deploy`), stacked on `pr/apps` (073603f).
Binding decisions: interview.md, the two 2026-10-15 sections (no emulation: everything deployed
comes from recorded msbuild items). Spec: xake-ar `layout-proto` `tests/gaps.md` S59, S60,
S63, S64, S65 and `tests/results/2026-10-14-layout-proto/results-2.md`; reference
implementation: `layout-proto:build.fsx` `runFolder` (moved into `Locks.runFolderWith` and the
new `RunFolder` module).

`Locks.deployWith` does not exist on this line (it was a night-build function): nothing to
alias. The release note says that `runFolderWith` replaces it.

## 1. What the import records now

| Lock field | From msbuild | Used by |
|---|---|---|
| `App.Content` (new) | `_GetCopyToOutputDirectoryItemsFromThisProject` (`None`, `Content`, `EmbeddedResource`, `Compile` with `CopyToOutputDirectory`), each at `TargetPath`; a repository file by path, unhashed (an input, as a source); a package's file by identity with its hash | run folder of the project and of every project referencing it (msbuild's transitive `GetCopyToOutputDirectoryItems`) |
| `App` of a library (new) | `ReferenceCopyLocalPaths` and `Content` of a project without runtime output, when not empty; `OutputType` `Library`, no `Host`, no `Files` | S59: a .NET Framework test library's package runtime assets |
| `Evaluation.Properties.IsTestProject` (new, only where set) | `IsTestProject` | `Locks.runs`: which entries get a run folder |
| `App.Related` (PR 3) | `AllowedReferenceRelatedFileExtensions` | the files beside a referenced project's output (`.pdb`, `.xml`, ...) |

`_ReferenceRelatedPaths` needs no field of its own: the related files msbuild copies are in
`ReferenceCopyLocalPaths` already; a referenced project's are not there at import time (it is
not built), hence `Related`.

Left out at import, by design: a copy item under the intermediate or the output folder (written
by a build target: the apphost's `None` item, coverage mapping files) and a missing one.

## 2. The compile writes less

`Apps.write` (called by `Locks.compileAs`) writes only what is named after the assembly:
`Files` (runtimeconfig, deps.json, config) and the apphost. The copy-local files went to the run
folder: in a flat output two applications with different versions of one package would
overwrite each other's file on every compile (S60, S61). A `deps.json` token
`$(AssemblyVersion:<file>)` is resolved from the copy-local source of that file. An apphost is
rewritten only when its bytes change, so a recompile does not break every hard link to it.

## 3. `Locks.runFolderWith`

`Locks.runFolderWith extraRoots mode lockOf outOf cfg fwk name` (`runFolder` = no extra roots,
`RunFolder.Link`). The target is the manifest `.xake-run.txt`; its folder is the run folder.
`Locks.flatOutOf` (`out/<cfg>/<fwk>`) and `Locks.runOf` (`run/<cfg>/<fwk>/<name>`) are the
prototype's defaults; the layout stays the script's. `Locks.runFoldersWith` lists the manifests
of every entry that runs (`Locks.runs`: Exe/WinExe, generated runtime files, or
`IsTestProject`).

- Inputs: the root's outputs (assembly, pdb, xml when the lock asks for doc, `Files`, apphost),
  its `Content`, its `Copy`; then the closure breadth first (`Recorded.Projects` with msbuild's
  configuration and framework, else `refConfig`/`nearestFramework`): each assembly with the
  root's `Related` extensions, a referenced application's runtimeconfig/deps.json/apphost (the
  SDK's items for referencing projects), each project's `Content`, and its `Copy` files that are
  not package files.
- **Package files come from the root's restore only.** The prototype (and a first version here)
  took the closure's `Copy` package files too; on AR that put 206 netstandard assets msbuild
  drops for net8.0 (System.Memory, System.Text.Json, ...) into 30 folders and caused every
  conflict. msbuild never copies a referenced project's package files; the root's
  `ReferenceCopyLocalPaths` already holds its whole graph at its own versions. This also is S65:
  a root's own version can no longer be beaten.
- Precedence (`RunFolder.plan`): the root's own first and never replaced; in the closure the
  first source wins, a higher **assembly** version replaces a lower one; every dropped source
  with other content goes to `.xake-run.conflicts.txt` (a warning).
- Placing (`RunFolder.apply`): hard link, else clone (APFS `clonefile`, Linux `FICLONE`), else
  copy; `RunFolder.Mode` picks where to start. A placed file is never written into (unlinked,
  placed again); a file at a planned path that no run placed and that has other content is kept
  and reported. An unchanged entry: same file identity for a link (`stat` dev/ino, Windows
  volume serial + file index), same size and write time for a clone/copy. Stale paths of the old
  manifest are removed, with folders left empty. The manifest is written only when its text
  changes.
- Dependencies: the locks read; the closure's assemblies as rule targets **and** their write
  times as `FileDep`s; package files and items. Side outputs are not needed through the compile
  rule (S64).
- **S66 on the published engine.** Xake.Hermetic.Dotnet builds on the published Xake 3.7.0.26
  (`[3.7.0.26, 3.8)`), whose `ArtifactDep` has no change stamp; `ArtifactDep2` (PR #54) is in
  Xake 3.8, which this package does not reference yet. The first test run proved it: an edit,
  `build`, then the copy-mode run folder in a run of its own stayed stale. The rule therefore also
  records each closure assembly's write time as a plain `FileDep` (the prototype depended on the
  pdb for the same reason). With Xake 3.8 that is redundant, not wrong.
- S64: `docOf` already names the dll by `outputName` (TargetFileName) on this line; a test now
  needs `desk.xml` of a net472 WinExe in a fresh run and gets `desk.exe`.

## 4. Tests

`dotnet fsi build.fsx -- -- build test`: green. tests.dll **341 passed + 1 skipped**
(unchanged); hermetic.tests **267 passed** (258 before; +9 in
`src/hermetic.tests/RunFolderTests.fs`):

- `Run folder placing` (3, scratch files): precedence (root wins, higher assembly version in the
  closure, case-insensitive paths, conflicts and their reasons, same content no conflict); links
  by inode, no-op (manifest not written, identities kept), a replaced source re-linked, stale file
  and empty folder removed; copy mode keeps the source's time and re-places on change, a foreign
  file is never overwritten and is taken over once it has the planned content.
- `Run folders` (6, e2e, SDK 10.0.401, flat `out/Release/<fwk>/`, one import of lib
  (netstandard2.0;net8.0, Newtonsoft 13.0.3, doc), console (net8.0, a `None` and a `Content`
  item), console2 (Newtonsoft 12.0.3), tests (Test.Sdk + NUnit adapter, references lib and
  console), desk (net472 WinExe, doc)):
  - the run folder's file set **equals** `dotnet build`'s bin for console, console2, tests and
    desk (no exception in this fixture: no satellites; desk's config needs no redirects and is
    equal); the flat output holds no package file and no item; `dotnet console.dll` from the
    run folder prints;
  - `dotnet test tests.dll` from the run folder: **Passed**;
  - console has Newtonsoft 13.0.0.0, console2 12.0.0.0, each a hard link to its package file
    (inode), the referenced assembly a link to the flat output's, items a link to the
    repository file, no clone/copy;
  - no-op: a second deploy runs no run-folder rule; a rule re-run by a moved input time places
    nothing (`0 placed`), manifests' times and every file identity unchanged;
  - S66 at the deploy level: copy mode, deploy, edit `lib/C.cs`, `build`, deploy in separate
    runs: the run folder's `Lib.Core.dll` has the new bytes;
  - S64 as above.
- `Import record applications` (PR 3) now compares run folders (per-project output layout, the
  same rule): the Test.Sdk project's file set is now **equal** to msbuild's (the six adapter
  files come from `Content`); console, winexe, ridapp as before; the apphost runs from the run
  folder.
- `App recording`: the App section round trip with `Content`.

## 5. ActiveReports (`/Users/olegz/.claude/jobs/7147ff60/tmp/pr4/ar`)

Setup cloned from pr3 (APFS clone; msbuild's reference `bin/` folders from pr3's `dotnet
build` kept), `build.fsx`: flat `out/<b>/<cfg>/<tfm>/` (compile rule finds the project by the
assembly name, S62 at script level), rule `run/(b)/(cfg)/(fwk)/(name)/.xake-run.txt` over
`Locks.runFolderWith ... RunFolder.Link`, `deploy::<b>::<cfg>` over `Locks.runFoldersWith`.
mescius Release, SDK 8.0.425, Xake 3.7.10-night, local dll then the packed 0.4.2-night (the same
locks). Compile and deploy only, no test run. Scripts: `pr4/scripts/ar-xake.sh`, `ar-run.py`;
comparison `pr4/ar/logs/run-compare.txt`. The machine's load was 20-35 during the runs (other
jobs).

| | mescius Release |
|---|---|
| import / clean build / build no-op | 37-39 s / 34.6-35.2 s / 1.6 s |
| run folders | **77** (72 of the 36 test projects: 36 net472 + 25 net8.0 + 11 net8.0-windows; 5 of the 4 dev apps: 4 net472 + ThemeEditor net8.0-windows) |
| deploy clean / no-op | **3.7-4.6 s / 2.0-2.1 s** |
| entries / inodes / own files | 16,739 / **1,720** / 77 (the manifests) |
| placed by | 16,662 hard links, 0 clones, 0 copies |
| logical size / `du run` alone / `du` after out, .packages, meta | 3.95 GiB / 767 MiB / **3.4 MiB** |
| conflicts | **0** (first version, closure package files taken: 29 folders) |
| stale removal | redeploy after the closure fix: 228 files removed, 0 placed, 16,662 kept |
| locks | 81,182 lines (PR 3: 72,035); 167 of 177 entries with `App` (126 libraries), 808 `Content` items (432 from packages), 13,638 `Copy` items; 72 `IsTestProject` |

File sets against msbuild's `bin/` (72 test folders, 15,914 msbuild files, 15,730 in the run
folders): **extra 0**; missing, all known:

| missing | count | why |
|---|---|---|
| satellites (`ja/`, `zh-CN/`) | 68 | not recorded (open since PR 2) |
| `.msCoverageSourceRootsMapping_*`, `CoverletSourceRootsMapping_*` | 72 | written into the output by the coverage packages' build targets |
| `*.Tests.dll.config` (net472 test libraries, and a referenced test project's) | 38 | binding redirects: msbuild generates the file (S55) |
| `Microsoft.Data.SqlClient.SNI.{arm64,x64,x86}.{dll,pdb}` (Tests net472) | 6 | the SNI package's `CopySNIFiles` target (a `Copy` task, not an item) |

Dev apps (shared msbuild folders): net472 `dev-apps/bin/Release/net472` 260 files, union of the
four run folders 256, missing 4 (satellites), extra 0; ThemeEditor net8.0-windows 144 vs 142,
missing 2 (satellites), extra 0. PR 3's 11 missing `None`/`Content` items of the dev apps and
the test projects' adapter, `TestFiles/` and referenced-app files are all present now.

Feed: `Xake.Hermetic.Dotnet.0.4.2-night.nupkg` added to `~/Projects-work/ar/xake-ar/feed/` (and
`pr4/feed`). gccn not run.

## 6. Commits

83b1cb4 lock `App.Content`; 5611bcc import records copy items, library `App`, `IsTestProject`;
7c25fe4 compile writes only files named after the assembly; 7dd26c6 `RunFolder` (placing,
plan); b640a2a `Locks.runFolderWith`/`runFoldersWith`/`flatOutOf`/`runOf`/`runs`, assembly
`FileDep`s, apphost unchanged when equal; 11d1b67 tests; 6b7be65 closure package files from the
root's restore only; docs (this file, lock.md "Run folders", README release note).

## 7. Open

- Satellites (own and referenced) and binding redirects (S55) -- the two remaining file-set
  differences.
- Windows and Linux are implemented but not run: `CreateHardLink`, `GetFileInformationByHandle`,
  `FICLONE`, glibc `__xstat` fallback; ReFS clone relies on `CopyFile`.
- With hard links a test that writes into a file of its run folder writes into the source
  (`RunFolder.Copy` for such a folder; the AR prototype saw none).
- The `FileDep`s on the closure's assemblies can go when the package moves to Xake 3.8.
- A package's build target that copies with a `Copy` task (SNI) is not an item: by the
  no-emulation decision it stays missing; a script can add such files itself.
