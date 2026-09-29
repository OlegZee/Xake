# Getting a lock out of `csc {}` settings

> **Status 2026-09-29 (API names brought up to date with the code).** Written 2026-09-22
> against the flat lock; since then the lock split (stage B) made `Lock.Project` `Lock.Entry`
> (`Name`, `Framework`, `Evaluation`, `Compilation`, `Dependencies`) and `Lock.File`
> `Lock.Document` (`Configuration`, `Properties`, `Entries`); stage A2 replaced the `fromlock`
> setting with `CscLock.compile entry`; `Csc.resolve` shipped as `CscLock.resolve`; migration
> path A shipped as `csc { lock "path" }` (stage C). The scenarios below use today's names; the
> reasoning is the 2026-09-22 one. Each recommendation carries its status at the end.

A `Lock.Entry` -- the structured `csc` command line, hashed references/analyzers/compiler,
generated inputs -- comes from `Project.import` (msbuild design-time build), and is replayed
with `CscLock.compile entry` (`CscLock.compileWith options entry` for non-default
`RunOptions`). The composed mode (`csc { src ...; ref ...; define ... }`) builds a `Lock.Entry`
internally too, in the private `resolve` (`Dotnet.csc.fs`), which `run` then compiles. This asks
how a script gets that value out, for which uses, and what each needs in the library.

Today's public form: `CscLock.resolve : CscSettingsType -> Recipe<ExecContext, Lock.Entry>` --
the entry alone (the private `resolve` also returns the framework's env vars, which only `Csc`
uses; the resx temp files it once returned are gone, see the end of §9). It leaves every
`Sha256 = ""`, sets `Name` from the output file, `Framework` from `targetfwk`/`NETFX-TARGET`,
`Compilation.Directory = options.ProjectRoot`, an all-empty `Evaluation` (no project, imports,
SDK or pin behind composed settings) and `Dependencies.Packages = []`.

## 1. Record

A build compiles with composed settings and writes what it resolved, so the next reader has
evidence -- the same role `Project.import`'s lock plays for msbuild projects.

**(a) An operation on `csc {}`.** `lock "path"` sets a `CscSettingsType` field (built as
`Lock: string option`, with the strict semantics of §9 rather than an unconditional write);
`Csc` writes the resolved entry (wrapped in a one-entry `Lock.Document`) after `resolve`,
alongside `run` compiling it -- record-while-building. Effort S. Risk: written unconditionally every build (no
dependency tracking of the lock itself), and races if two `csc {}` calls share one lock path
(scenario 6).

**(b) A public resolve a separate lock rule calls** (built as `CscLock.resolve`, not
`Csc.resolve`: F# will not let a module share the name of the `let`-bound `Csc`), mirroring
`Project.import`: the lock is its own file target, a downstream rule reads it and compiles it
with `CscLock.compile`.

```fsharp
"out/helloworld.lock.json" ..> recipe {
    // resolve + Lock.rehash + Lock.save of a one-entry Lock.Document; nothing is compiled
    do! CscLock.record "out/helloworld.lock.json" settings
}
"out/helloworld.dll" ..> recipe {
    let! doc = Lock.load "out/helloworld.lock.json"   // needFiles the lock itself
    do! CscLock.compile (Lock.entry "helloworld" doc)
}
```

(Hand-rolled, the first rule is `let! entry = CscLock.resolve settings` then
`do! Lock.save path { Configuration = ""; Properties = []; Entries = [ Lock.rehash entry ] }`.)

Library change: expose `resolve`. Built as `CscLock.resolve`, returning the entry alone.

Two things record has to handle regardless of (a) or (b): `resolve` used to compile `.resx`
into temp files as a side effect (no longer: see the end of §9 -- a resx is now a permanent
`(resx, .resources)` pair under `obj/xake/<assembly>/` in `Compilation.Resources`, compiled by
`run` when missing). And `resolve` probes the toolchain (`DotNetFwk.locateFramework`) to fill
`Dependencies.Compiler.Path`/`Version` -- that probe must succeed on the recording machine,
which is a trap for "record once, replay anywhere" if the replay machine's framework layout
differs (scenario 7). An empty `Evaluation` (no `Project`, `Sdk` or `SdkPin`) and
`Compilation.Directory = ProjectRoot` are fine to leave as is -- there is no project file to
name, and the directory is the only true fact available.

Recommend **(b)**: smaller, gives the lock the same status as `Project.import`'s (a file target
another rule `need`s) rather than a side effect of compiling. Add `lock` (a) only if "compile and
record in one step" is wanted enough to accept scenario 6's races.

## 2. Replay

Already built. Gains: hash verification of every reference/analyzer/compiler (`run`'s mismatch
check); `needFiles` on everything the args name, not just declared `src`/`ref`; exact
reproducibility from the lock regardless of what currently matches on disk.

Loses: filesets stop being live -- a new source under the `src` mask is invisible until the lock
is regenerated, since replay compiles `entry.Args` (rebuilt from `Compilation` and
`Dependencies`, not stored), never `settings.Src`. Same trade `packages.
lock.json` makes against a floating `PackageReference` range: the lock is authoritative, and
staying current is a deliberate step (regenerate, diff, commit), not implicit.

## 3. Verify / enforce

Compile from settings but fail if the resolved compilation differs from a committed lock.

What to compare: not the flat args byte-for-byte (machine-local framework-probe paths differ
before tokenization). As built, the recorded side is read with `Lock.load`, which expands the
`$(Token)` roots (`Roots`) against this machine, so both sides are compared as expanded paths;
`Lock.diff` compares `Framework`, `Compilation.Options`/`Sources` as ordered lists, `Defines` and
`Evaluation.ProjectRefs` as sets, the compiler, `Evaluation.Sdk`, the hashed lists by path,
`Generated`/`Resources` by key and `Dependencies.Packages` by id. Hashes are compared only when
both sides carry one (the resolved side never does), so they matter only once record populates
them (scenario 5) -- and the runner then checks the recorded ones against disk. The report is
one line per difference (`- x`, `+ x`, `~ field: a -> b`).

Where it lives: `Lock.diff : Lock.Entry -> Lock.Entry -> string list` (pure, in `module Lock`
in `Project.fs`) is the primitive -- built. On top: a `csc {}` operation (resolve, diff, fail
before compiling, else compile from the lock -- which also gets hash verification for free;
built as `csc { lock "path" }`, §9), or a standalone check (built as
`CscLock.verify : string -> CscSettingsType -> Recipe<ExecContext, string list>`, the diff
alone, nothing compiled or written).

This is the same shape as brief §11's planned `Policy` module (`Policy.check : Rule list -> Lock
-> Violation list`) -- a `HashMatch`/`Declared` rule generalizes this to import-mode locks too.
Build `Lock.diff` now (scenario 3 needs it regardless); hold `csc { locked }` and any `Policy`
wiring for when `Policy` is designed (tracker rung 4) -- don't grow two enforcement mechanisms.
(Overtaken by §9: the user chose the strict `csc { lock }` on 2026-09-24; `Policy` is not built.)

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

`resolve` leaves every `Sha256` empty. `Lock.hashed : string -> Lock.Hashed` already exists; it
is just never called from the composed path. Built as `Lock.rehash : Lock.Entry -> Lock.Entry`
(fills every hash -- references, analyzers, imports, compiler, and the compiler `Version` when
empty), called by `CscLock.record` and by `csc { lock }` when it first writes the lock.

Where: **at record time only, not inside `resolve`.** `resolve` runs on every composed `Csc`
call, including ordinary compiles with no lock intent -- hashing every reference (dozens of
assemblies, framework globals included) on every build would tax the common case for nothing.
The record rule (1b) is a file target that only reruns when its own inputs change, so the hash
cost is paid there, cached like everything else.

```fsharp
let! entry = CscLock.resolve settings
let hashed = Lock.rehash entry     // what the 2026-09-22 draft spelled out by hand
```

Effort S (glue only). This is the one piece of scenario 1 that is not "just call an existing
function" -- without it, scenarios 2's mismatch check and 3's diff have nothing to check for a
composed-mode lock.

## 6. Many `csc` calls, one lock file

`Lock.Document` holds several `Entries` per variant (every framework inside, since
ab978d3; it was per (framework, variant) when this was written); parallel rules cannot
read-modify-write one shared file safely -- the race (a)'s unconditional write hits directly, and
(b) hits too if two projects were made to share one lock target.

1. **One lock file per target** (what 1b's snippet does). No aggregation, no race by
   construction -- each record rule owns a distinct output path. Consistent with how
   `CscLock.compile` takes one entry at a time via `Lock.entry name doc` (or
   `Lock.entryFor framework name doc`) regardless of how many share a file.
2. **A phony aggregate target** that resolves every compilation without compiling and writes one
   file -- structurally `Project.import`'s loop over `ImportOptions.Projects`, for composed
   settings instead. Needs a new entry point: `CscLock.resolveAll : CscSettingsType list ->
   Recipe<Lock.Entry list>` (not built; or a script-level fold calling `CscLock.resolve` per
   settings value).
   Effort M, mostly in recipe sequencing and per-project error reporting, matching what
   `Project.import` already does.
3. **Aggregate later**: keep per-target files as the source of truth, merge several
   `Lock.Document`s into one distribution file only when needed -- `{ d with Entries = d.Entries
   @ other.Entries }` is the whole of it, no new concept.

Recommend **1** as default (zero new surface, no race by construction); build **2** only when a
script genuinely wants one lock for several composed compilations, modeled on `Project.import`'s
loop. Skip **3** until a concrete need for a merged file shows up.

## 7. Env vars and temp files

`CscLock.compile entry = run RunOptions.Default entry []` -- empty env. The
composed mode needs `DotNetFwk`'s env vars (e.g. PATH for a full-framework or mono toolchain) at
`run` time; a replayed lock carries none.

Breaks when: the compiler needs environment beyond what its resolved path alone provides --
mono-hosted `csc`, or a full-framework toolchain resolving supporting DLLs via PATH. The
SDK-hosted `dotnet <csc.dll>` path (`run`'s own special case) is least at risk, since `dotnet`
resolves the SDK itself; `cscpath`/native-launcher branches carry the risk.

Does the lock need to record env? No -- `Lock.Entry` is deliberately the file format, and env
is a property of *how* a compiler runs on *this* machine, not *what* was compiled (the same
reasoning that kept env a `run` parameter, not a lock field, in the one-resolved-form refactor).
The fix, when replay needs env, is for `CscLock.compile` to re-derive it the way `resolve` does --
from `DotNetFwk.locateFramework`, not from the lock. Today it does not (`[]` unconditionally);
fine for the proven SDK/netstandard case, a real gap the day a full-framework or mono lock is
replayed.

If it comes up: let the replay resolve env from `DotNetFwk.locateFramework (Some fwk)` with
`fwk` the entry's own `Framework` (a `RunOptions` field or a `compileWith` variant; the
2026-09-22 draft said `csc { fromlock project; targetfwk ... }`, a shape stage A2 removed).
Effort S once needed -- no design blocker, just undone work; `Entry.Framework` and
`Dependencies.Compiler.Path`/`Version` carry enough identity to make this recoverable rather
than guessed. Still open 2026-09-29: `CscLock.compile`/`compileWith` pass `[]`.

## 8. Interaction with the planned lock split

(Done in stage B, aa5ddc8: `Lock.Entry` = `Evaluation` / `Compilation` / `Dependencies`, with
`Compilation.Options` structured around four section markers and the round trip checked at
import. `resolve` did not end up building the structure directly: it still composes a flat arg
list and runs it through the same `Lock.Compilation.ofArgs` and round-trip check as the import.
A composed record fills `Compilation` and `Dependencies` and leaves `Evaluation` empty. The
paragraphs below are the 2026-09-22 reasoning.)

Tracker's "Lock stability" plans to split `Lock.Project` (now `Lock.Entry`) into dependencies (references,
analyzers, compiler, imports -- rare, reviewed) and compilation (args, sources, generated --
every PR), later making compilation structured fields instead of a raw arg list, round-tripped
against msbuild's own line at import.

Nothing above fights that split -- it eases it: composed settings (`Src`, `Ref`, `Define`,
`Target`, `Platform`, ...) are already structured; `resolve` is the one place that flattens them
into an arg list. Once the compilation section is structured, `resolve` builds that structure directly
from `CscSettingsType` instead of an arg list -- a more natural producer of the structured form
than `Project.import`'s parser, which reconstructs structure out of a flat list it did not
choose. The round-trip obligation the split adds for import-mode locks has no equivalent on the
composed side, since composing *is* the only source.

Decide alongside the split, not before: whether a composed-mode record belongs in the
dependencies section, the compilation section, or both. Building scenario 1 now against the
current flat `Lock.Project`, letting `write`/`parse` (now `Lock.writeWith`/`parseWith`, with
`Lock.save`/`load` as the recipe forms) absorb the split later (as already planned
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
scenario 1b applies (`CscLock.record path settings` in the lock rule, `Lock.load` +
`CscLock.compile` in the build rule). ~15 lines. **Not built.**

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

Built 2026-09-24 as `csc { lock "path" }` (`CscSettingsType.Lock: string option`) plus
`CscLock.record : string -> CscSettingsType -> Recipe<ExecContext, unit>` and
`CscLock.verify : string -> CscSettingsType -> Recipe<ExecContext, string list>`. What `lock`
does: lock file missing -- `Lock.rehash` the resolved entry, `Lock.save` it as a one-entry
`Lock.Document`, compile the rehashed entry; present and `Lock.diff recorded resolved = []` --
compile the *recorded* entry, so its hashes gate the build; present and different -- fail with
the diff and the two update routes (delete the file, or run the target calling
`CscLock.record`); `nofailonerror` turns that failure into a warning and compiles the resolved
entry. The semantics, the failure message and the script pattern are in `csc-syntax.md`,
"Locking composed settings: `lock`". Path B (`cscSettings { }`) is not built.

**Common trap of both paths, resolved (2026-09-23)**: `resolve` now records a `.resx` resource as
a permanent `(resx, .resources)` pair in `Resources`, not a temp file with a random name in
`/res:` -- a lock recorded from settings with `.resx` resources is compilable and diffable as is;
see `csc-syntax.md`'s composed-mode resx paragraph.

## Recommendation

1. **1b** -- resolve made public, lock recorded by a plain file-target rule. Smallest
   change; everything else needs this value. S. *Built as `CscLock.resolve` (entry only), with
   `CscLock.record` as the one-call record rule body.*
2. **5** -- hash at record time, in the same rule; without it 2/3 have nothing to check. S.
   *Built as `Lock.rehash`, called by `CscLock.record` and by `csc { lock }`.*
3. **2** -- already done; the trade-off above is the documentation gap this note closes.
   *Built: replay is `CscLock.compile` / `compileWith` (stage A2, replacing `fromlock`).*
4. **4** -- update, free once 1b exists. S. *Built: delete the lock, or a script target calling
   `CscLock.record`; no `UPDATE_LOCKS` variable.*
5. **3** -- build `Lock.diff` now; hold `csc { locked }` sugar and `Policy` wiring for when
   `Policy` is designed. M for the diff primitive. *Built: `Lock.diff`, `CscLock.verify`, and
   the enforcing form as `csc { lock "path" }` (§9, the user's 2026-09-24 decision); `Policy`
   not built.*
6. **6** -- default to one lock file per target (nothing new needed); build `resolveAll`
   only when a script wants one file for several composed compilations. M when needed, else skip.
   *As recommended: one file per target; `resolveAll` not built.*
7. **7** -- skip until a non-SDK toolchain is actually replayed from a lock; no concrete case yet
   (dataengine is netstandard2.0 throughout). *Not built: replay still runs with empty env.*
8. **8** -- no action now; keep the flat `Lock.Project`, let the planned split absorb
   composed-mode locks the same way it absorbs import-mode ones. *Done: the split (stage B)
   absorbed them -- `resolve` goes through `Lock.Compilation.ofArgs` like the import.*

**Amended after scenario 9 (2026-09-22):** the migration path A (`lock "path"`, locked-mode
semantics) is the primary user-facing scenario, B the addition; 1b stays the internal shape.
The update mechanism is undecided -- no global variable. (Decided 2026-09-24 and built as path
A, `csc { lock "path" }`; B not built -- see §9.)

Smallest API covering 1-5, as built: `CscLock.resolve : CscSettingsType -> Recipe<ExecContext,
Lock.Entry>` plus `Lock.diff : Lock.Entry -> Lock.Entry -> string list`, with `Lock.rehash`,
`Lock.save`/`load`, `Lock.entry`/`entryFor` and `Lock.mapPaths` around them -- and, beyond the
2026-09-22 minimum, the library itself now carries the glue it expected scripts to write:
`CscLock.record`, `CscLock.verify` and `csc { lock }`. That keeps the symmetry the task asked
about: `Project.import` is the recipe producing a `Lock.Entry` for an msbuild project;
`CscLock.resolve` is the recipe producing one for a composed `csc {}` -- both feed
`CscLock.compile`.
