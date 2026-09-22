# Conceptual review of slice 1

Written 2026-09-23, requested in tracker.md ("Conceptual review"). Read-only audit of what
slice 1 left in `src/dotnet` and in the scripts; facts are from the code on
`feature/hermetic-build` at commit `f2c5a2c`, function names as they are in the source.

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

Duplication that is only cosmetic: `resolve` calls `needFiles (src @ refs @ resfiles)` and
`run` then `needFiles` the same paths again via `CscArgs.inputs`; the pool dedups by target,
so it costs nothing, but the first call can go.

### 2.5 A second project root, and shared infrastructure under `Fsproj` -- debt

`Fsproj.roots ()` defines `$(ProjectRoot)` as `Directory.GetCurrentDirectory()`. The engine
has a project root: `ExecOptions.ProjectRoot`, reachable in any recipe via `getCtxOptions()`,
and it is what `need`, `getFiles` and rule matching resolve against. The two agree only when
the script is run from its own directory; the import scripts' headers already carry "run it
from the repository being imported" because of this. Cleaner: `roots` takes the project root
as a parameter and `import`/`write`/`read` pass `options.ProjectRoot`. Cost: S.

Placement: `Json`, `roots`, `withRoots`, `tokenize`, `expand` live in `Fsproj` and are used
15+ times from `Project.fs`, 3 times from `import-page.fsx` and the tests -- while
`Fsproj.evaluate` itself has one caller (`build.fsc.fsx`). The module name now describes a
tenth of what it holds. Move them to two internal modules compiled before `Fsproj` (`Json`;
`Roots` or `Lock.Roots`) and leave `Fsproj` as the F# evaluation it is named for. Cost: S, a
rename with the tests' `Fsproj.withRoots`/`Fsproj.roots` following.

### 2.6 `Fsproj.evaluate` is now the special case -- debt, deferred

Two evaluators: `Fsproj.evaluate` (items and properties, `ProjectInfo`, fed into the composed
`fsc {}`), `Project.import` (the command line, `Lock.Project`, fed into `run`). Brief §11 said
the first "becomes the F# case of this or is retired". It cannot be retired yet: `fsc` has no
runner over a `Lock.Project` (`Dotnet.fsc.fs` composes its own args and `needFiles` its own
list), so `build.fsc.fsx` has nothing else to call. Leave it, do not extend it; retire it when
`fsc` gets `run`'s equivalent (`FscCommandLineArgs` exists) and `build.fsc.fsx` imports its
two projects the same way `import.fsx` does.

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

## 4. Order

Now, cheap, before more code depends on it:

1. `Csc.fromLock` and `RunOptions`; drop `FromLock`/`fromlock` from the settings (2.2).
2. `needFiles` the compiler in `run` (2.4); drop `resolve`'s duplicate `needFiles`.
3. Drop the `.resources` timestamp test in `run` -- regenerate when the recipe runs (2.3).
4. `roots` from `ExecOptions.ProjectRoot`, not cwd; move `Json` and the roots functions out
   of `Fsproj` (2.5).
5. State the gate semantics in `csc-syntax.md`: the lock fails builds, the engine triggers them.

With the planned lock split:

6. Three sections (evaluation, compilation, dependencies); `Args` built from the dependency
   entries, not duplicating their paths; `Compiler.Version` distinct from the SDK; `SdkPin`
   typed (2.1, 2.7).
7. Rename `Lock.Project`/`Lock.File` (2.7) in the same change -- one migration of the fixtures.
8. Decide then whether `Generated` and `.resources` become targets via a rule factory (2.3);
   yes if anything besides the one csc call will consume them.

Leave:

9. `Project.import` shelling to msbuild inside a file rule, and its per-project loop.
10. The import rule not depending on the packages it hashes.
11. `Fsproj.evaluate` until `fsc` has a `Lock`-based runner (2.6).
12. The scripts' wiring pattern (`need` the lock, `Lock.read`, `mapPaths` the unbuilt
    references, `need` them, compile) -- it is what the library should keep making easy.
