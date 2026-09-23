# Getting a lock out of `csc {}` settings

Today `Lock.Project` -- exact `csc` args, hashed references/analyzers/compiler, generated inputs
-- comes from `Project.import` (msbuild design-time build), or gets replayed with
`csc { fromlock lock.project }`. The composed mode (`csc { src ...; ref ...; define ... }`)
also builds a `Lock.Project` internally now, in `resolve` (`Dotnet.csc.fs`), but `resolve` is
private, used once, then discarded after `run` compiles from it. This asks how a script gets
that value out, for which uses, and what each needs in the library.

`resolve`'s signature: `CscSettingsType -> Recipe<Lock.Project * (string * string) list * string
list>` -- the project, the framework's env vars, and resx temp files. It leaves `Sha256 = ""`
everywhere, and fills `Name`/`Project`/`Directory` with placeholders (`Project = ""`, `Directory
= options.ProjectRoot`) since there is no msbuild project behind composed settings.

## 1. Record

A build compiles with composed settings and writes what it resolved, so the next reader has
evidence -- the same role `Project.import`'s lock plays for msbuild projects.

**(a) An operation on `csc {}`.** `lock "path"` sets `CscSettingsType.LockOut: string option`;
`Csc` writes the resolved project (wrapped in a `Lock.File`) after `resolve`, alongside `run`
compiling it -- record-while-building. Effort S. Risk: written unconditionally every build (no
dependency tracking of the lock itself), and races if two `csc {}` calls share one lock path
(scenario 6).

**(b) A public `Csc.resolve` a separate lock rule calls**, mirroring `Project.import`: the lock
is its own file target, a downstream rule reads it and compiles with `fromlock`.

```fsharp
target "out/helloworld.lock.json" {
    let! project, _, temp = Csc.resolve settings
    Impl.deleteFiles temp                 // resolve-only: nothing to compile against yet
    do! writeFile "out/helloworld.lock.json"
        (Lock.write { Framework = ""; Configuration = ""; Properties = []; Projects = [project] })
}
"out/helloworld.dll" ..> csc {
    fromlock (Lock.project "helloworld" (Lock.read "out/helloworld.lock.json"))
}
```

Library change: un-`private` `resolve` and expose it as `Csc.resolve`. Effort S -- the function
already has this shape.

Two things record has to handle regardless of (a) or (b): `resolve` compiles `.resx` into temp
files as a side effect and hands their paths back for `run` to delete after compiling; a
resolve-only rule that never calls `run` must delete them itself (as above), or resgen output
leaks. And `resolve` probes the toolchain (`DotNetFwk.locateFramework`) to fill
`Compiler.Path`/`Sdk` -- that probe must succeed on the recording machine, which is a trap for
"record once, replay anywhere" if the replay machine's framework layout differs (scenario 7).
`Name`/`Project`/`Directory` staying placeholders is fine to leave as is -- there is no project
file to name, and the directory is the only true fact available.

Recommend **(b)**: smaller, gives the lock the same status as `Project.import`'s (a file target
another rule `need`s) rather than a side effect of compiling. Add `lock` (a) only if "compile and
record in one step" is wanted enough to accept scenario 6's races.

## 2. Replay

Already built. Gains: hash verification of every reference/analyzer/compiler (`run`'s mismatch
check); `needFiles` on everything the args name, not just declared `src`/`ref`; exact
reproducibility from the lock regardless of what currently matches on disk.

Loses: filesets stop being live -- a new source under the `src` mask is invisible until the lock
is regenerated, since replay reads `project.Args`, never `settings.Src`. Same trade `packages.
lock.json` makes against a floating `PackageReference` range: the lock is authoritative, and
staying current is a deliberate step (regenerate, diff, commit), not implicit.

## 3. Verify / enforce

Compile from settings but fail if the resolved compilation differs from a committed lock.

What to compare: not `Args` byte-for-byte (machine-local framework-probe paths differ before
tokenization). Compare after the same normalization import-mode locks get -- tokenize both sides
(`Lock.tokenizeAll`/roots), diff arg lists and reference/analyzer path sets. Hashes matter only
once record populates them (scenario 5). Report the diff the way `run`'s mismatch path already
does: one line per differing path/arg, "expected X, got Y".

Where it lives: `Lock.diff : Project -> Project -> string list` (pure, next to `Lock` in
`Project.fs`) is the primitive. On top: `csc { locked "path" }` (resolve, diff, fail before
compiling, else compile from the lock -- which also gets hash verification for free), or a
standalone `Lock.verify : CscSettingsType -> Lock.Project -> Recipe<unit>`. Effort M -- `diff`
builds on `mapPaths`/`tokenizeAll`, but getting normalization right (do `Directory`/`Project`
placeholders participate?) needs a test or two.

This is the same shape as brief §11's planned `Policy` module (`Policy.check : Rule list -> Lock
-> Violation list`) -- a `HashMatch`/`Declared` rule generalizes this to import-mode locks too.
Build `Lock.diff` now (scenario 3 needs it regardless); hold `csc { locked }` and any `Policy`
wiring for when `Policy` is designed (tracker rung 4) -- don't grow two enforcement mechanisms.

## 4. Update

Regenerate deliberately. Cheapest: delete the lock file and rebuild the lock target -- a missing
target already means "rebuild", no variable needed. A `-d UPDATE_LOCKS=true` var is only useful
to force regeneration even when the lock is present and its filesets say up to date (e.g. to
refresh hashes after an out-of-band package change the fileset dependency tracking would not
otherwise notice).

Record and verify coexist as different rules over the same lock path, not one recipe: the record
rule (1b) is a normal file target, rebuilding when its own dependencies change; the compile rule
verifies (3) against whatever is currently on disk. Regenerating is `need`ing the record target
after deleting the file, or forcing it; verify does not need to know an update happened. Effort S
given 1b and 3 exist.

## 5. Hashing at record time

`resolve` leaves every `Sha256` empty. `Lock.hashed : path -> Hashed` already exists; it is just
never called from the composed path.

Where: **at record time only, not inside `resolve`.** `resolve` runs on every composed `Csc`
call, including ordinary compiles with no lock intent -- hashing every reference (dozens of
assemblies, framework globals included) on every build would tax the common case for nothing.
The record rule (1b) is a file target that only reruns when its own inputs change, so the hash
cost is paid there, cached like everything else.

```fsharp
let! project, _, temp = Csc.resolve settings
let hashedProject =
    { project with
        References = project.References |> List.map (fun r -> Lock.hashed r.Path)
        Analyzers  = project.Analyzers  |> List.map (fun a -> Lock.hashed a.Path)
        Compiler   = { project.Compiler with Sha256 = Lock.sha256 project.Compiler.Path } }
```

Effort S (glue only). This is the one piece of scenario 1 that is not "just call an existing
function" -- without it, scenarios 2's mismatch check and 3's diff have nothing to check for a
composed-mode lock.

## 6. Many `csc` calls, one lock file

`Lock.File` holds several `Project`s per (framework, variant); parallel rules cannot
read-modify-write one shared file safely -- the race (a)'s unconditional write hits directly, and
(b) hits too if two projects were made to share one lock target.

1. **One lock file per target** (what 1b's snippet does). No aggregation, no race by
   construction -- each record rule owns a distinct output path. Consistent with how
   `csc { fromlock }` reads one project at a time via `Lock.project name lock` regardless of
   how many share a file.
2. **A phony aggregate target** that resolves every compilation without compiling and writes one
   file -- structurally `Project.import`'s loop over `ImportOptions.Projects`, for composed
   settings instead. Needs a new entry point: `Csc.resolveAll : CscSettingsType list ->
   Recipe<Lock.Project list>` (or a script-level fold calling `Csc.resolve` per settings value).
   Effort M, mostly in recipe sequencing and per-project error reporting, matching what
   `Project.import` already does.
3. **Aggregate later**: keep per-target files as the source of truth, merge several `Lock.File`s
   into one distribution file only when needed -- `{ f with Projects = f.Projects @
   other.Projects }` is the whole of it, no new concept.

Recommend **1** as default (zero new surface, no race by construction); build **2** only when a
script genuinely wants one lock for several composed compilations, modeled on `Project.import`'s
loop. Skip **3** until a concrete need for a merged file shows up.

## 7. Env vars and temp files

`Csc { fromlock project } = run settings project [] []` -- empty env, no temp files. The
composed mode needs `DotNetFwk`'s env vars (e.g. PATH for a full-framework or mono toolchain) at
`run` time; a replayed lock carries none.

Breaks when: the compiler needs environment beyond what its resolved path alone provides --
mono-hosted `csc`, or a full-framework toolchain resolving supporting DLLs via PATH. The
SDK-hosted `dotnet <csc.dll>` path (`run`'s own special case) is least at risk, since `dotnet`
resolves the SDK itself; `cscpath`/native-launcher branches carry the risk.

Does the lock need to record env? No -- `Lock.Project` is deliberately the file format, and env
is a property of *how* a compiler runs on *this* machine, not *what* was compiled (the same
reasoning that kept env a `run` parameter, not a lock field, in the one-resolved-form refactor).
The fix, when replay needs env, is for `fromlock` mode to re-derive it the way `resolve` does --
from `DotNetFwk.locateFramework`, not from the lock. Today it does not (`[]` unconditionally);
fine for the proven SDK/netstandard case, a real gap the day a full-framework or mono lock is
replayed.

If it comes up: let `csc { fromlock project; targetfwk "net-4.6.2" }` resolve env from
`DotNetFwk.locateFramework (Some fwk)` when both are set. Effort S once needed -- no design
blocker, just undone work; `project.Compiler.Sdk` already carries enough identity to make this
recoverable rather than guessed.

## 8. Interaction with the planned lock split

Tracker's "Lock stability" plans to split `Lock.Project` into dependencies (references,
analyzers, compiler, imports -- rare, reviewed) and compilation (args, sources, generated --
every PR), later making compilation structured fields instead of a raw arg list, round-tripped
against msbuild's own line at import.

Nothing above fights that split -- it eases it: composed settings (`Src`, `Ref`, `Define`,
`Target`, `Platform`, ...) are already structured; `resolve` is the one place that flattens them
into `Args`. Once the compilation section is structured, `resolve` builds that structure directly
from `CscSettingsType` instead of an arg list -- a more natural producer of the structured form
than `Project.import`'s parser, which reconstructs structure out of a flat list it did not
choose. The round-trip obligation the split adds for import-mode locks has no equivalent on the
composed side, since composing *is* the only source.

Decide alongside the split, not before: whether a composed-mode record belongs in the
dependencies section, the compilation section, or both. Building scenario 1 now against the
current flat `Lock.Project`, letting `write`/`parse` absorb the split later (as already planned
for import-mode locks), avoids designing the split twice.

## 9. Migration: an existing, carefully tuned `csc { }` (raised 2026-09-22)

Not covered above. Scenario 1b needs the settings as a *value* -- and `csc { src ...; ref ... }`
is not one: the builder's `Run` returns the compile recipe. A tuned block would have to be
rewritten in record syntax (`{ CscSettings with Src = ...; Ref = ... }`) to be shared between a
lock rule and a build rule. Two paths that leave the block in place:

**A. `lock "path"` inside the same `csc { }`**, with `packages.lock.json` semantics: `resolve`
runs as always, the settings stay the source of truth. No lock file: hash, write, compile. Lock
present: `Lock.diff` the resolved compilation against it (args and file set); a difference means
the settings or filesets moved after the lock was recorded and the build fails with the diff;
equal: verify the lock's hashes against disk and compile. This also *is* scenario 3 (verify)
without a second rule. The objections to (a) in scenario 1 fall away: the file is written only
when created or deliberately updated, and a race needs two calls sharing one path, an authoring
error.

**B. `cscSettings { ... }`**, a second builder with the same operations whose `Run` returns the
`CscSettingsType`; the tuned block moves into `let settings = cscSettings { ... }` unchanged and
scenario 1b applies (`Csc.resolve settings` in the lock rule, `fromlock` in the build rule).
~15 lines.

**How the lock gets updated -- decided (user, 2026-09-24), and path A is built.** A script
variable such as `-d UPDATE_LOCKS=true` was proposed and rejected on 2026-09-22 (a global
switch on the whole tool for what is, so far, a narrow case); the decision that replaced it is:

- **strict by default** -- a lock that disagrees with the resolved settings fails the build,
  printing the diff. Not "follow the settings and warn": the npm split is inverted here on
  purpose, since a lock exists to be authoritative and the compilation it guards is the
  reproducibility claim. `nofailonerror` still downgrades it to a warning (and then compiles
  from the settings).
- **the update is explicit and is a target of the script's own** -- `CscLock.record path
  settings` in a phony the script declares (`"update-locks" => recipe { ... }`), or simply
  deleting the lock file, a missing target already meaning "produce it". No engine-level mode,
  no global variable, nothing that updates a lock as a side effect of building.
- **the lock file is not a target of the engine** on this path: `lock "path"` writes it from
  inside the compile recipe, which is what lets a tuned `csc { }` block stay where it is.
  Recommendation 1b (the lock as a real file target) still stands for scripts that prefer it.

Built 2026-09-24 as `csc { lock "path" }` plus `CscLock.record` and `CscLock.verify`; the
semantics, the failure message and the script pattern are in `csc-syntax.md`, "Locking composed
settings: `lock`". Path B (`cscSettings { }`) is not built.

**Common trap of both paths, resolved (2026-09-23)**: `resolve` now records a `.resx` resource as
a permanent `(resx, .resources)` pair in `Resources`, not a temp file with a random name in
`/res:` -- a lock recorded from settings with `.resx` resources is compilable and diffable as is;
see `csc-syntax.md`'s composed-mode resx paragraph.

## Recommendation

1. **1b** -- `Csc.resolve` made public, lock recorded by a plain file-target rule. Smallest
   change; everything else needs this value. S.
2. **5** -- hash at record time, in the same rule; without it 2/3 have nothing to check. S.
3. **2** -- already done; the trade-off above is the documentation gap this note closes.
4. **4** -- update, free once 1b exists. S.
5. **3** -- build `Lock.diff` now; hold `csc { locked }` sugar and `Policy` wiring for when
   `Policy` is designed. M for the diff primitive.
6. **6** -- default to one lock file per target (nothing new needed); build `Csc.resolveAll`
   only when a script wants one file for several composed compilations. M when needed, else skip.
7. **7** -- skip until a non-SDK toolchain is actually replayed from a lock; no concrete case yet
   (dataengine is netstandard2.0 throughout).
8. **8** -- no action now; keep the flat `Lock.Project`, let the planned split absorb
   composed-mode locks the same way it absorbs import-mode ones.

**Amended after scenario 9 (2026-09-22):** the migration path A (`lock "path"`, locked-mode
semantics) is the primary user-facing scenario, B the addition; 1b stays the internal shape.
The update mechanism is undecided -- no global variable.

Smallest API covering 1-5: `Csc.resolve : CscSettingsType -> Recipe<Lock.Project * (string *
string) list * string list>` (made public) plus `Lock.diff : Project -> Project -> string list`
(new). Everything else -- record rule, hashing, update, verify -- is fsx-level glue over those
two and the `Lock` functions that already exist (`hashed`, `write`, `read`, `project`,
`mapPaths`). That keeps the symmetry the task asked about: `Project.import` is the recipe
producing a `Lock.Project` for an msbuild project; `Csc.resolve` is the recipe producing one for
a composed `csc {}` -- both feed `fromlock`, and nothing about record/verify/update needs to
live anywhere but a rule built from what already exists.
