# Conceptual review of slice 1

Written 2026-09-23, requested in tracker.md ("Conceptual review"). Read-only audit of what
slice 1 left in `src/dotnet` and in the scripts; facts are from the code on
`feature/hermetic-build` at commit `f2c5a2c`, function names as they are in the source.

## 0. Status 2026-09-23 (audit)

Re-checked claim by claim against the code on `feature/hermetic-build` at `dcf131b` (stages
A1/A2, the lock split, `csc { lock }`, `Restore`, one lock per variant all landed after this
review was written). The verdict of §1 held: both seams are closed, and the way they were
closed is the shape this review asked for.

| Claim / recommendation | Status | Evidence |
|---|---|---|
| §1, §2.8 `src/core` changed in exactly two files | STALE-WRONG | four now: `ExecCore.fs`, `WorkerPool.fs`, `Path.fs` (`impl.normalizeLiteralPrefix`, 4e74f69), `Database.fs` (`Storage.openWithRetry`, 2d10b52). Both new ones are engine bug fixes too, so the conclusion "no drift" stands |
| §2.1 `Lock.Project` mixes three notions | DONE | `Lock.Entry = { Name; Framework; Evaluation; Compilation; Dependencies }`, `Project.fs:498` |
| §2.1 `Args` duplicates the reference paths | DONE | `Entry.Args` is a member rebuilt from `Compilation.Options` + `Dependencies` (`Project.fs:515`, `Lock.Compilation.args`/`ofArgs`); import and `resolve` both verify the rebuilt line equals the original (`Dotnet.csc.fs:490`) |
| §2.1 `mapPaths` rewrites five fields in step | DONE | `Lock.mapPaths` (`Project.fs:584`) walks `Options` (skipping the section markers), `Sources` and the reference/analyzer entries |
| §2.1 `Compiler.Sdk` means two things | DONE | `Compiler.Version` (from the file version resource, `Lock.compilerVersion`) vs `Evaluation.Sdk` (`NETCoreSdkVersion`) |
| §2.1 `resolve` fills placeholder fields | DONE by contract | `resolve` writes `Evaluation = { Project=""; ProjectRefs=[]; Imports=[]; Sdk=""; SdkPin=None; Properties=Map.empty }` and the type documents "empty when composed" (`Dotnet.csc.fs:482`) |
| §2.2 `FromLock` is a mode flag in the settings | DONE, and more | `CscSettingsType.FromLock` and the `fromlock` operation are gone; `CscLock.compile`/`compileWith` (`Dotnet.csc.fs:563`), `RunOptions` (`Dotnet.csc.fs:74`). `Csc settings` is one path: resolve, then run |
| §2.2 `run` takes `RunOptions = { FailOnError; CscPath }` | DONE, superseded | the record also carries `Restore: Restore.Options` (`Dotnet.csc.fs:74`) |
| §2.2 "optionally a small `cscLock {}` builder" | STALE | the need was met from the other side: `csc { lock "path" }` with strict semantics, plus `CscLock.record`/`verify` (`Dotnet.csc.fs:685`, `CscLockTests.fs`) |
| §2.3 the `.resources` timestamp test is a second rebuilder | DONE | `run` regenerates only when the output is missing (`Dotnet.csc.fs:260-264`), the comment names this review |
| §2.3 `Generated`/`.resources` as targets via a rule factory | STILL HOLDS (open) | no `Lock.rules`; `run` still writes `Generated` and compiles resx inline (`Dotnet.csc.fs:240-264`) |
| §2.4 the compiler is hashed but not a tracked dependency | DONE | `do! needFiles (Filelist [File.make compiler.Path])` (`Dotnet.csc.fs:204`) |
| §2.4 gate semantics must be written down | DONE | `csc-syntax.md`, "they never trigger one" (line 184) |
| §2.4 the import rule must not `needFiles` what it hashes | STILL HOLDS | `Project.import` `needFiles` only the project files, `Evaluation.Imports` and `.git/HEAD` (`Project.fs:1330,1430,1447`) |
| §2.4 `resolve`'s `needFiles (src @ refs @ resfiles)` is redundant | STILL HOLDS | still there, `Dotnet.csc.fs:404`, duplicated by `CscArgs.inputs` at `Dotnet.csc.fs:291` |
| §2.5 `$(ProjectRoot)` from the process cwd | DONE | `Roots.builtin projectRoot`, `Roots.current`/`currentWith` read `ExecOptions.ProjectRoot` (`Roots.fs:43,110`); the cwd survives only as a fallback for an empty root |
| §2.5 move `Json` and the roots functions out of `Fsproj` | DONE | `src/dotnet/Json.fs`, `src/dotnet/Roots.fs`, compiled ahead of `Fsproj.fs` (`Xake.Dotnet.fsproj`) |
| §2.6 `Fsproj.evaluate` cannot be retired yet | STILL HOLDS | `Dotnet.fsc.fs` mentions neither `Lock` nor `RunOptions`; `Fsproj.evaluate`/`load` have one caller, `build.fsc.fsx:107,127` |
| §2.7 `Lock.Project` -> `Lock.Entry`, `Lock.File` -> `Lock.Document` | DONE | `Project.fs:498,522` |
| §2.7 `SdkPin`/`NETCoreSdkVersion` out of the `Properties` bag | DONE | `Evaluation.Sdk: string`, `Evaluation.SdkPin: SdkPin option` over a DU (`Project.fs:361,337`) |
| §2.7 keep `Variant` | STILL HOLDS | `ImportOptions.Variant` (`Project.fs:988`) |
| §3 "env vars *and temp files* as `run` parameters" | PARTIALLY STALE | `run` takes `envVars` only; `resolve` produces no temp files -- a composed `.resx` becomes a permanent `(resx, .resources)` pair under `obj/xake/<name>/` (`Dotnet.csc.fs:391`) |
| §3 one runner, two producers | STILL HOLDS, widened | `run` is still the only thing that shells out to csc; the producers are now `Project.parseImport`, `CscLock.resolve`, and a lock read off disk |
| §3 the lock is a gate, not a rebuilder | STILL HOLDS, with a caveat | `run` now restores missing packages before the hash check (`Restore.ensure`, `Dotnet.csc.fs:190`), so a *missing* dependency is repaired rather than failed; a dependency whose bytes differ still fails |
| §4 items 1-7 | DONE | as already marked in §4 |

## 1. Verdict

The concept has not drifted, but two seams are blurred and should be straightened before
more code sits on them. The engine is still "targets, rules, tracked dependencies": the
whole of `src/core` changed for this feature in exactly two files (`ExecCore.fs`, two calls
of `Scheduler.newRun`; `WorkerPool.fs`, the `running`/`finished` maps and `NewRun`), and that
is a fix of a real engine bug (one target executed twice per run), not a hermetic-build
idea. No new `Dependency` case, no new `Rule` case, nothing in `Xake` knows what a lock is.
Everything hermetic is a recipe, a value, and a file format in `Xake.Dotnet`, and the
scripts make the lock a file target that other rules `need` -- the right shape. The two
things that matter most: (a) `Lock.Project` holds three different kinds of fact in one flat
record and its producers fill it inconsistently (`resolve` writes placeholders and gives
`Compiler.Sdk` a different meaning than `parseImport` does); (b) `csc {}` carries a mode
flag (`FromLock`) inside its settings record, so the type allows a block whose other settings
are silently ignored. Beneath those, `run` quietly does a rebuilder's and two rules' work, and
`Fsproj.roots ()` reinvents the engine's project root from the process cwd. Each is small
now; each is the kind of thing that becomes the shape of slice 2 if left alone.

> **2026-09-23:** (a) and (b) are both closed, and so are the two smaller items; only the "everything is a
target" question of 2.3 is still open. The engine count is out of date -- four files now, see
2.8.

## 2. Findings

### 2.1 `Lock.Project` is three notions in one record -- conceptual

`Lock.Project` (`src/dotnet/Project.fs`, module `Lock`) carries, side by side:

- *evidence*: `References`, `Analyzers`, `Compiler`, `Imports` as `Hashed` (path + SHA-256);
- *the compile manifest*: `Args`, `Generated`, `Resources`, `Directory`, `Name`;
- *provenance of the evaluation*: `Project`, `ProjectRefs`, `Properties` (now also carrying
  `SdkPin` and `NETCoreSdkVersion` as free-form strings), `Compiler.Sdk`.

`run` reads only the first two groups; it never touches `Imports`, `ProjectRefs`, `Project`
or `Properties`. `Imports` is in fact a dependency of the *import rule* (that is where
`needFiles` is called on it), not of the compilation at all. And `References` is derived
from `Args` at import (`CscArgs.switchValues "reference" args |> List.map Lock.hashed`) yet
stored again, so every path lives twice and `Lock.mapPaths` has to rewrite `Args`,
`References`, `Analyzers`, `Generated` and `Resources` in step to keep them consistent.

The tracker's planned split (dependencies apart from compilation) matches the first two
groups; the provenance group has no home in that plan. The code's shape hinders the split
slightly: `resolve` must fill fields that mean nothing for composed settings (`Project = ""`,
`Directory = options.ProjectRoot`, `Imports = []`), and the two producers disagree on
`Compiler.Sdk` -- `parseImport` stores `NETCoreSdkVersion` (the SDK that evaluated), `resolve`
stores `fwkInfo.Version` (the *target* framework, e.g. `net-4.6.2`; its own comment says so).

Cleaner shape: a record of three records -- `Evaluation` (project file, imports, SDK, pin,
properties; empty for a composed compilation), `Compilation` (args or the structured fields
the tracker plans, generated, resources, directory), `Dependencies` (references, analyzers,
compiler, each hashed) -- with `Args` built from `Dependencies` rather than duplicating its
paths, and `Compiler` given a `Version` of its own instead of overloading `Sdk`. In memory
this is the same data with three names; `mapPaths` shrinks to one section. Cost: M, mostly
in `writeProject`/`readProject`, the fixtures under `samples/hermetic/`, and `FromLockTests`;
it is exactly the work the tracker already plans, so do it once, with the split.

> **2026-09-23:** done in this shape (`Lock.Entry` with `Evaluation`/`Compilation`/`Dependencies`, `Args` a
member rebuilt from the sections, `Compiler.Version` separate from `Evaluation.Sdk`, `SdkPin` a
DU). `Entry` also carries `Framework` next to `Name` -- identity, added later by the
one-lock-per-variant change -- and `Dependencies` gained `Packages`, the restore graph the SBOM
reads.

### 2.2 `FromLock` is a mode flag inside a settings record -- conceptual

`CscSettingsType.FromLock: Lock.Project option` (`Dotnet.csc.fs`) makes `Csc` a two-way
branch: `Some project -> run settings project [] []`, else `resolve` then `run`. In the first
branch `Src`, `Ref`, `RefGlobal`, `Resources`, `Define`, `Target`, `Platform`, `Out`,
`TargetFramework`, `CommandArgs`, `Toolset` are all ignored, and nothing in the type says so:
`csc { fromlock p; src !!"*.cs" }` compiles and does something other than it reads. Only
`FailOnError` and `CscPath` are honoured in both branches -- they are runner options, and
`run` takes the whole `settings` record just to read those two.

Cleaner shape: `Csc.fromLock : Lock.Project -> Recipe<unit>` (optionally a small
`cscLock { project p; cscpath ...; nofailonerror }` builder) next to `csc {}`, both calling
the same `run`, and `run` taking a `RunOptions = { FailOnError; CscPath }` instead of the
settings record. `FromLock` and the `fromlock` operation go. What is lost: one entry point
in the docs; "the compiler is only ever run through the `csc` recipe" stays true as long as
both live in `CscImpl` and share `run`. Cost: S -- `Csc`, the builder, `FromLockTests.fs`,
the two `import*.fsx`, `csc-syntax.md`. Do it now, before `fsc` grows the same flag.

> **2026-09-23:** done (stage A2): `CscLock.compile`/`compileWith` and `RunOptions`. The suggested `cscLock {}`
builder was not built; instead `csc {}` gained `lock "path"` (Stage C) -- a lock *binding* for
composed settings, which honours every other setting instead of ignoring it. `RunOptions` also
carries `Restore`.

### 2.3 `run` does a rebuilder's and two rules' work -- debt

Inside one recipe, before the compiler starts, `run`:

1. writes back every `Generated` file whose content differs (`File.ReadAllText path = content`);
2. creates output directories (`CscArgs.outputs`);
3. `needFiles` each resx, then regenerates the `.resources` when
   `File.GetLastWriteTimeUtc resourcesFile >= File.GetLastWriteTimeUtc resx` fails;
4. verifies hashes; 5. `needFiles` the inputs; 6. runs csc.

Step 3 is a second rebuilder: the engine already decides, from the `FileDep` on the resx,
whether this recipe runs; `run` then re-decides with its own timestamp comparison. Step 1 is
a file target's rule folded into its consumer -- the engine never sees `AssemblyInfo.cs`
being *produced*, only being read. In Xake terms each `.resources` and each generated file is
a target with one input (the resx; the lock), and the dll `need`s them.

Is folding them in a leak or a shortcut? A shortcut, and a defensible one: both writes are
idempotent and deterministic (`Resx.compile` is byte-identical to msbuild's), the inputs are
`needFiles`'d so a change still rebuilds the dll, and a rule per generated file would need
the lock's contents to *define* rules -- rules are declared before the build, the lock is
read inside a recipe. The "everything is a target" version therefore needs either a
rule-factory in `Xake.Dotnet` (`Lock.rules lock : Rule list`, the script adds them with
`rules`/`for`; precedent for rule-valued functions exists in core: `requiring`, `delegated`)
or rules the script writes by pattern (`obj/xake/(fwk:*)/(brand:*)/(name:*).resources`,
reading the lock inside). Worth it? Not for slice 1's proof; yes the day a `.resources` or a
generated file is consumed by anything other than this one csc call (satellite assemblies,
the SBOM listing inputs). Until then the cheap fix is to drop the timestamp test in step 3
(regenerate unconditionally when the recipe runs -- the engine already established that an
input changed) so `run` keeps no rebuild logic of its own. Cost: one `if`; S.

> **2026-09-23:** the cheap fix landed: `run` regenerates a `.resources` only when it is missing. The
target-per-generated-file question is still open (§4 item 8), and there is now a concrete reason
to reopen it: an imported project's `.resources` is written to msbuild's own `OutputResource`
path inside the shared `obj/`, which collides with `dotnet build` in the same tree (tracker,
"the page byte-comparison cannot be re-run in a tree Xake has built in"). `run` also gained a
step ahead of everything else -- `Restore.ensure`, fetching packages the lock names and this
machine lacks -- which is more work in the same recipe, deliberately.

### 2.4 Two dependency mechanisms, and the one they miss -- debt

The engine records: `FileDep` on project files and imports (from `Project.import`), `FileDep`
on every path `CscArgs.inputs` names plus each resx (from `run`), `ArtifactDep` on the lock
and on referenced projects' outputs -- but those last two only because the *script* writes
`need [lockFile fwk brand]` and `need (unbuilt |> ... outputOf)`. The lock records SHA-256 of
references, analyzers, compiler, imports, and the content of `Generated`.

Which wins: the engine, always. `getDepState` (`DependencyAnalysis.fs`) compares write times
within `TimeCompareToleranceMs`; the hash check lives inside the recipe and only runs when
the engine has already decided to rebuild. So the lock is a *gate*, not a rebuilder: it can
fail a build, never trigger one. That is the semantics brief §5 asked for ("a check, not a
new dependency kind") and it is coherent -- provided it is written down where `run` is
documented, because today "verifies the SHA-256 of every hashed reference" reads as if a
swapped dll would be caught, and with an unchanged timestamp it is not.

The gap: the compiler. `project.Compiler.Path` is hashed but not in `CscArgs.inputs`, so the
engine has no `FileDep` on `csc.dll`. An SDK update changes the compiler bytes, the dll stays
"up to date", and the hash that would have said otherwise is never computed. Fix: add the
compiler to the `needFiles` list in `run`. Cost: one line. (The import rule deliberately does
*not* `needFiles` the references it hashes -- a package changing under the same path must not
silently re-import; that is right and should stay.)

> **2026-09-23:** the compiler is `needFiles`'d (`Dotnet.csc.fs:204`) and the gate semantics are stated in
`csc-syntax.md`. One nuance this text predates: since `Restore`, a dependency that is *missing*
is fetched rather than failed; only one whose bytes differ still fails. The import still does not
`needFiles` what it hashes.

Duplication that is only cosmetic: `resolve` calls `needFiles (src @ refs @ resfiles)` and
`run` then `needFiles` the same paths again via `CscArgs.inputs`; the pool dedups by target,
so it costs nothing, but the first call can go.

> **2026-09-23:** still there (`Dotnet.csc.fs:404` against `:291`).

### 2.5 A second project root, and shared infrastructure under `Fsproj` -- debt

`Fsproj.roots ()` defines `$(ProjectRoot)` as `Directory.GetCurrentDirectory()`. The engine
has a project root: `ExecOptions.ProjectRoot`, reachable in any recipe via `getCtxOptions()`,
and it is what `need`, `getFiles` and rule matching resolve against. The two agree only when
the script is run from its own directory; the import scripts' headers already carry "run it
from the repository being imported" because of this. Cleaner: `roots` takes the project root
as a parameter and `import`/`write`/`read` pass `options.ProjectRoot`. Cost: S.

> **2026-09-23:** done (stage A1), and the entry points that need the root became recipes: `Roots.current`/
`currentWith`, `Lock.load`/`loadWith`/`save`/`saveWith`, `Fsproj.load`. The cwd-based ones are
gone. `Lock.load` also `needFiles` the lock itself, so the scripts' explicit `need` in front of
it went away with it.

Placement: `Json`, `roots`, `withRoots`, `tokenize`, `expand` live in `Fsproj` and are used
15+ times from `Project.fs`, 3 times from `import-page.fsx` and the tests -- while
`Fsproj.evaluate` itself has one caller (`build.fsc.fsx`). The module name now describes a
tenth of what it holds. Move them to two internal modules compiled before `Fsproj` (`Json`;
`Roots` or `Lock.Roots`) and leave `Fsproj` as the F# evaluation it is named for. Cost: S, a
rename with the tests' `Fsproj.withRoots`/`Fsproj.roots` following.

> **2026-09-23:** done: `src/dotnet/Json.fs` and `src/dotnet/Roots.fs`, both compiled before `Fsproj.fs`.

### 2.6 `Fsproj.evaluate` is now the special case -- debt, deferred

Two evaluators: `Fsproj.evaluate` (items and properties, `ProjectInfo`, fed into the composed
`fsc {}`), `Project.import` (the command line, `Lock.Project`, fed into `run`). Brief §11 said
the first "becomes the F# case of this or is retired". It cannot be retired yet: `fsc` has no
runner over a `Lock.Project` (`Dotnet.fsc.fs` composes its own args and `needFiles` its own
list), so `build.fsc.fsx` has nothing else to call. Leave it, do not extend it; retire it when
`fsc` gets `run`'s equivalent (`FscCommandLineArgs` exists) and `build.fsc.fsx` imports its
two projects the same way `import.fsx` does.

> **2026-09-23:** unchanged: `Dotnet.fsc.fs` still knows nothing of `Lock`, and `build.fsc.fsx` is still the
only caller of `Fsproj.evaluate`/`load`.

### 2.7 Names -- cosmetic

- `Lock.Project` is not a project; it is one project's compilation. `Lock.Entry` or
  `Lock.Compilation` says what it is, and stops the collision with module `Project`
  (`Project.import` returns `Lock.Project`s). `Lock.File` collides with Xake's `File` type,
  which is why `Lock` spells out `System.IO.File.Exists`; `Lock.Document` or `Lock.Contents`.
- `Compiler.Sdk` means two things (2.1). `Variant` is honest ("names the obj subtree") but
  every caller passes the brand; keep it, it is more general than `Brand`.
- `Project.import`, `resolve`, `run`, `Generated`, `Hashed`, `CscArgs.inputs`/`outputs`,
  `sdkPin` say what they are. `fromlock` goes with 2.2.
- `Properties` as a whitelist plus `SdkPin` as a formatted string ("exact 8.0.100") is an
  untyped bag growing evidence; give `SdkPin` a field in the evaluation section at the split.

> **2026-09-23:** all done. `SdkPin` is a DU (`NoGlobalJson`/`Pinned`/`RollsForward`/`NoVersion`) in
`Evaluation`, next to `Sdk`; `Properties` stays a whitelist map. `Lock` still spells out
`System.IO.File.Exists`: the collision is with Xake's `File` type, which the rename of `Lock.File`
did not change.

### 2.8 Engine drift, concretely

`git diff dev..HEAD -- src/core`: `ExecCore.fs` (+5), `WorkerPool.fs` (+31/-14). Nothing
else. In `Xake.Dotnet`, what re-implements an engine idea: the `.resources` timestamp check
(2.3, a rebuilder); `Fsproj.roots ()`'s cwd project root (2.5); the `Generated` content
compare (2.3, harmless -- an idempotent write, not a rebuild decision). `Lock.Hashed` is a
dependency record of its own, but that is the product (the lock is the deliverable), and it
stays on the right side of the line as long as it remains a gate (2.4). `Project.import`'s
per-project loop is a sequential mini-scheduler inside one target -- N msbuild runs the pool
cannot parallelize -- chosen deliberately (one lock per TFM and brand) and, given the shared
`obj/project.assets.json` race in the tracker, currently the safer choice. Not drift.

> **2026-09-23:** the diff is now `ExecCore.fs` (+5), `WorkerPool.fs` (+31/-14), `Path.fs` (+41/-2,
`normalizeLiteralPrefix` for file targets containing `..`), `Database.fs` (+23/-6, retrying a
transient sharing violation instead of discarding the database). Both additions are engine bug
fixes with tests in `src/tests`, so "no drift" still holds. The import loop has changed shape as
well: restore runs once per project without `TargetFramework`, then one design-time build per
framework, so the assets-file race is gone by construction and the `Resource` guard is the
fallback.

## 3. What is clean and should stay

- The engine untouched except for a bug fix; every hermetic piece a recipe, a value or a file.
- The lock as a file target in the scripts, `need`ed by the compile rule, msbuild only ever
  inside that rule -- the same contract `build.fsc.fsx` has with `Fsproj.evaluate`, and what
  `lock-from-settings.md` recommends (1b) for composed settings, having rejected a global
  update variable. The instinct "no engine mode, locks are targets" is the right one.
- `CscArgs` as data: one table of switch shapes, `inputs`/`outputs` deriving the dependency
  set from the command line -- exactly what a task owes the engine, computed not declared.
- One runner, two producers: `resolve` and `parseImport` both end in a `Lock.Project`, `run`
  is the only thing that shells out. Keep that even after 2.2 splits the front ends.
- Env vars and temp files as `run` parameters, not lock fields: the file format describes the
  compilation, not the machine.
- `Resx.read`/`compile`, `sdkPin`, `tokenizeAll`/`expand`, `Lock.mapPaths`: pure, tested,
  byte-identical where it matters.
- The dependency-free JSON: no `System.Text.Json` on netstandard2.0 consumers.
- Hash checks reporting every mismatch at once, "missing" spelled out.

> **2026-09-23:** all of this still holds, with two amendments. `run` no longer takes temp files:
> `resolve` records a composed `.resx` as a permanent `(resx, .resources)` pair under
> `obj/xake/<name>/`, so only the env vars travel alongside the entry. And the lock is still a
> gate rather than a rebuilder, but since `Restore` a *missing* dependency is fetched instead of
> failing the build -- only a changed one fails.

## 4. Order

Now, cheap, before more code depends on it (1-5 all **done**, stages A1/A2, 2026-09-24):

1. **Done** (stage A2): `CscLock.compile`/`compileWith` and `RunOptions`; `FromLock` and the
   `fromlock` operation are gone from the settings (2.2).
2. **Done**: `needFiles` the compiler in `run` (2.4).
3. **Done**: the `.resources` timestamp test in `run` is gone (2.3).
4. **Done** (stage A1): roots from `ExecOptions.ProjectRoot`, not cwd (`Roots.current`/
   `currentWith`, `Lock.load`/`save`, `Fsproj.load`); `Json` and the roots functions moved out
   of `Fsproj` into `Json.fs` and `Roots.fs` (2.5).
5. **Done**: the gate semantics are stated in `csc-syntax.md` -- the lock fails builds, the
   engine triggers them.

With the lock split (Stage B, **done** 2026-09-24, commits aa5ddc8/94f8087):

6. **Done**: three sections (`Evaluation`, `Compilation`, `Dependencies`); `Args` is a member
   rebuilt from `Compilation.Options` (with section markers) and the dependency entries, no
   path stored twice; `Compiler.Version` distinct from `Evaluation.Sdk`; `SdkPin` typed (2.1,
   2.7). The import verifies the rebuilt command line equals msbuild's.
7. **Done**: `Lock.Project` -> `Lock.Entry`, `Lock.File` -> `Lock.Document`, `Lock.project` ->
   `Lock.entry` (2.7), fixtures migrated in the same change.
8. Open: whether `Generated` and `.resources` become targets via a rule factory (2.3);
   yes if anything besides the one csc call will consume them.

Leave:

9. `Project.import` shelling to msbuild inside a file rule, and its per-project loop.
10. The import rule not depending on the packages it hashes.
11. `Fsproj.evaluate` until `fsc` has a `Lock`-based runner (2.6).
12. The scripts' wiring pattern (`need` the lock, `Lock.read`, `mapPaths` the unbuilt
    references, `need` them, compile) -- it is what the library should keep making easy.
