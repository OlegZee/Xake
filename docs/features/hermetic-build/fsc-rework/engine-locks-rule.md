# Engine facts for a lock-generating rule

Research on `pr/lock-generate` (`src/core`, everything up to #46) and `pr/build-from-lock` (`src/hermetic`).
Proofs: `dotnet fsi` scripts against a copy of `src/core` built to a scratch dir (outputs below, trimmed).

## Q1. Dependency primitives against a matching file rule

**Answer.** Every primitive that *demands* a file (`need`, `needFiles`, `dependsOn`) goes through the same
rule lookup; a rule that matches the path runs it. The only ways to depend on a file **without** asking
for its rule are `getFiles` (it records only the file *list*, not the contents) and a `FileDep` you write
into the build result yourself. There is no public "input only" primitive.

| Primitive | Signature (where) | Asks for the rule? | Records |
|---|---|---|---|
| `need` | `string list -> Recipe<ExecContext,unit>` (ScriptFuncs.fs:63) | yes: `makeTarget` -> `ExecCore.need` | `ArtifactDep`, or `FileDep` if no rule matches |
| `needFiles` | `Filelist -> Recipe<_,unit>` (ScriptFuncs.fs:130) | yes: `ExecCore.need (List.map FileTarget files)` | same |
| `dependsOn` | `Fileset -> Recipe<_,unit>` (ScriptFuncs.fs:140) | yes (`getFiles` + `needFiles`) | `GetFiles` + the above |
| `getFiles` | `Fileset -> Recipe<_,Filelist>` (ScriptFuncs.fs:116) | no | `GetFiles (fileset, files)`: change of the *list* only (DependencyAnalysis.fs:55-62) |
| `alwaysRerun` | `unit -> Recipe<_,unit>` (ScriptFuncs.fs:80) | n/a | `AlwaysRerun` |
| `FileDep`/`ArtifactDep` | DU cases (Types.fs:45-46) | no, if added by hand | whatever you put in |

Mechanism: `execOne` (ExecCore.fs:105) calls `locateRule` (ExecCore.fs:169); with a rule, the task runs if
`ctx.NeedRebuild` (ExecCore.fs:110) and the dependency is `ArtifactDep target` (ExecCore.fs:180). Only when
no rule matches and the file exists is it `JustFile` with `FileDep (file, mtime)` (ExecCore.fs:184-185).
**(b) fresh clone**: no `.xake` -> `GetResult` is `None` -> `"Not built yet"` (DependencyAnalysis.fs:104-105)
-> the matching rule runs and overwrites the committed file. Also: a rule that records **no** dependencies
reruns every time (`"No dependencies"`, DependencyAnalysis.fs:86-87), and a file target that is missing
reruns (DependencyAnalysis.fs:99).
**(c)** No built-in. A library can do it: `getResult`/`setResult` are public (`[<AutoOpen>] module Xake.Recipe`,
RecipeFunctions.fs:23,29) and `FileDep` is a public case, so appending `FileDep (f, File.getLastWriteTime f)`
to `Depends` gives a real input dependency (consumer reruns when the file changes) that never consults rules.

Proof (`q1.fsx`): committed `out/x.txt`, no `.xake`, each consumer run twice; `out/x.txt` rule has no deps,
`out/y.txt` rule `needFiles ["src.txt"]`; `fileDepOnly` = the `setResult` trick below.
```
c-need-y     | out/y.txt='by-rule'      | RULE out/y.txt RAN; c-need-y ran          <- run 2 skipped both
c-need       | out/x.txt='by-rule'      | RULE out/x.txt RAN; c-need ran; RULE out/x.txt RAN; c-need ran
c-needFiles  | out/x.txt='by-rule'      | RULE out/x.txt RAN; c-needFiles ran; RULE out/x.txt RAN; c-needFiles ran
c-dependsOn  | out/x.txt='by-rule'      | RULE out/x.txt RAN; c-dependsOn ran; RULE out/x.txt RAN; c-dependsOn ran
c-getFiles   | out/x.txt='pre-existing' | c-getFiles ran                            <- rule never ran
c-fileDep    | out/x.txt='pre-existing' | c-fileDep ran                             <- rule never ran, run 2 skipped
c-fileDep after edit of out/x.txt | c-fileDep ran                                 <- input change detected
```
```fsharp
let fileDepOnly (path: string) = recipe {
    let! ctx = getCtx()
    let f = File.make (Path.Combine(ctx.Options.ProjectRoot, path))
    let! r = getResult()
    do! setResult { r with Depends = FileDep (f, File.getLastWriteTime f) :: r.Depends } }
```
Trap: one-line `recipe { do! need [..]; mark ".." }` parses as `do! (need [..]; mark ..)`; `need` is dropped.
## Q2. Phony rules

**Answer.** A target runs at most once per run: the worker pool keys tasks by `Target.FullName` and joins
running or finished ones (WorkerPool.fs:26-57; `NewRun` clears finished, WorkerPool.fs:66). A phony `=>` rule
(XakeScript.fs:101) is stored in the database like any target (`Store result`, ExecCore.fs:121) and is
**skipped** when its recorded deps are unchanged; it reruns if it recorded no deps (DependencyAnalysis.fs:86)
or a dependee rebuilt. `command` (RuleBuilder.fs:12-21), `<==` (ScriptFuncs.fs:223-226) and `<<<` add
`AlwaysRerun`. Patterns with captures work in phony names (`Path.matchGroups pattern ""`, ExecCore.fs:71-76;
`makeTarget` prefers a phony match, ExecCore.fs:215-225); `getRuleMatch` reads them. A phony can `need` a phony.
`getTargetFile` fails in a phony (ScriptFuncs.fs:155).

Proof (`q2.fsx`): `"lock::(name:*)::(cfg:*)" => recipe { name/cfg via getRuleMatch; if A then need
["lock::B::"+cfg]; write locks/<name>/<cfg>.json }`, `"locks" <== [A; B]`, `"build" => needFiles [locks/A/Release.json]`.
```
run 1: lock::A::Release            lock B Release ran; lock A Release ran
run 2: lock::A::Release            lock B Release ran; lock A Release ran     <- no deps recorded: reruns
run 3: locks (A and B wanted)      lock B Release ran; lock A Release ran     <- B once though needed twice
fresh .xake: build                 build ran                                  <- no lock rule ran (JustFile)
again: build                       (nothing)
```
Variant `q2b.fsx`: lock rule also `needFiles [<name>.project.json]`:
```
run 1: lock::A::Release            lock B Release ran; lock A Release ran
run 2: lock::A::Release            (nothing)                                  <- phony recorded and skipped
run 3: locks, B.project.json edited lock B Release ran; lock A Release ran     <- A reran via ArtifactDep B
```
**Conclusion.** Yes: phony `lock::<name>::<cfg>` rules writing `locks/<name>/<cfg>.json` as a side effect
give ordering by `need`, captures by `getRuleMatch`, and leave no file rule on the lock path, so `build`'s
`needFiles`/`Lock.load` sees `JustFile`. Downsides: the lock is not a target -- a deleted/hand-edited lock is
not noticed (the "target file does not exist" check is FileTarget-only, DependencyAnalysis.fs:99), so the
rule must `alwaysRerun` (or be run via `command`/`<==`) to be safe; no `getTargetFile`, the path is rebuilt
from the captures; `A`'s dependency on B's lock file is an `ArtifactDep` on a phony name, not on the file.
## Q3. What a recipe can read

| Primitive | Signature | Documented |
|---|---|---|
| `getCtx` | `unit -> Recipe<ExecContext,ExecContext>` (ScriptFuncs.fs:11) | no |
| `getCtxOptions` | `unit -> Recipe<_,EngineOptions>` (ScriptFuncs.fs:52): `ProjectRoot`, `Threads`, `Logger`, `Teardown`, `Rules` (ExecTypes.fs:9-15) | tasks.md:521 |
| `getTargetFile` | `unit -> Recipe<_,File>` (ScriptFuncs.fs:150), fails for phony | tasks.md:522 |
| `getTargetFiles` | `unit -> Recipe<_,File list>` (ScriptFuncs.fs:163) | no |
| `getTargetFullName` | `unit -> Recipe<_,string>` (ScriptFuncs.fs:173) | tasks.md:523 |
| `getRuleMatches` / `getRuleMatch` | `unit -> Recipe<_,Map<string,string>>` / `string -> Recipe<_,string>` ("" if absent) (ScriptFuncs.fs:183,194) | not in cheatsheet/tasks (hermetic-guide.md:390, roadmap.md:10) |
| `getVar` | `string -> Recipe<_,string option>`, records `Var` (ScriptFuncs.fs:102) | tasks.md:519 |
| `getEnv` | `string -> Recipe<_,string option>`, records `EnvVar` (ScriptFuncs.fs:88) | tasks.md:520 |
| `OptionalVar`/`RequiredVar` | `let! v = someVar` (Vars.fs:97-100), resolves via `getVar`/`getEnv` (Vars.fs:81-93) | `vars` op |
| `getResult`/`setResult` | RecipeFunctions.fs:23,29 | no |

`ExecContext` (ExecTypes.fs:33-45) holds `Vars: (string*string) list`, `Targets`, `RuleMatches`, `Engine`
-- **no user-state slot**; `ExecOptions` (XakeScript.fs:7-62) has none either (only string `Vars`). A layout
declared "once" must live in the script closure (a `let` before `xake {}`) and be passed or partially
applied; a recipe cannot discover it. `FileConditionRule` gives no captures (ExecCore.fs:47-50, TODO).
## Today: how the lock dependency is declared

`Lock.load`/`loadWith` (`pr/build-from-lock` src/hermetic/Lock.fs:610-622) does `needFiles [lock]`; it is used
by `Locks.compileAs` (Locks.fs:327), `Lock.buildWith` (Lock.fs:1179, after a `File.Exists` check at 1165) and
`Described.buildCsc` (Described.fs:461). `Locks.needAll` checks existence with `File.Exists` (Locks.fs:402).
So today with **no** rule on `locks/...` it is `JustFile` + `FileDep` -- safe. The moment a file rule
matches the lock path, every `Lock.load` in `build` would run that rule on a fresh clone. "Only `needFiles`"
does not change that: `needFiles` *is* rule lookup. What must change is `Lock.load` recording the `FileDep`
itself (the `setResult` trick) instead of `needFiles`.
## Options

**(1) File rule for the lock, `build` reads it as an input only.** Feasible, with a library primitive.
```fsharp
"locks/(name:*)/(cfg:*).json" ..> recipe {
    do! alwaysRerun ()                       // explicit regeneration every time it is demanded
    do! Locks.generate layout }              // name/cfg from getRuleMatch, refs via need ["locks/B/<cfg>.json"]
command "locks" { do! need [ for p in projects do for c in cfgs -> $"locks/{p}/{c}.json" ] }
command "build" { do! Locks.needAll ... }    // compileAs -> Lock.loadInput: FileDep by hand, no rule lookup
```
Fresh clone/CI: `build` never demands a lock target, so the rule never runs; a missing lock must fail
explicitly (`File.getLastWriteTime` of a missing file is not an error by itself). Inside generation, B's lock
must be read with `need` (to order) and A's own current lock read without `need` (it is the target).
`Locks.generate` gets name/cfg from `getRuleMatch` (or parses `getTargetFile`) and the target path itself;
`outOf`/layout still from the closure. Risk: any `Lock.load`/`needFiles` of a lock path anywhere in `build`
(e.g. a ref lock in a compile rule) re-enables generation -- every reader must use the input-only variant.

**(2) Phony `lock::<name>::<cfg>` writing the lock as a side effect.** Works today (proof Q2).
```fsharp
"lock::(name:*)::(cfg:*)" => recipe {
    do! alwaysRerun ()
    do! Locks.generate layout }              // name/cfg via getRuleMatch; refs: need ["lock::B::<cfg>"]
"locks" <== [ for p in projects do for c in cfgs -> $"lock::{p}::{c}" ]
command "build" { do! Locks.needAll ... }    // unchanged: Lock.load needFiles -> JustFile
```
Fresh clone/CI: no rule matches `locks/...`, `Lock.load` stays as is. `Locks.generate` needs name/cfg
(`getRuleMatch`), `lockOf` to compute the output path (no `getTargetFile`), `outOf` from the closure.
Downsides: no target tracking of the written file (deleted lock unnoticed unless `alwaysRerun`); ordering
expressed on phony names; the phony name grammar is a second naming convention next to the path.

**(3) Other engine means.** `FileConditionRule` (`..?>`, XakeScript.fs:91): a predicate that can close over a
script-level flag (e.g. "the command line contains `locks`"), so the lock file rule exists only in a
generating run; no captures (parse the path), the flag is read once at script construction -- the engine has
no rule set switched by a `Var` (rules are fixed in `XakeScript (options, rules)`, XakeScript.fs:136); a
generate+build in one invocation would regenerate. `DelegatedRule` (Types.fs:85) lets an executor decide
whether to run the body: an executor that returns a stored result when the lock exists would be a heavier
form of "input only".
