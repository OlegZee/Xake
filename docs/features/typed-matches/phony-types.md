# Phony rules: a separate pattern type — cost estimate

Answers the open item of [`brief.md`](brief.md), decision 5: what does a separate provided type
for phony rules (`Command<"...">` next to `Pattern<"...">`) cost, against a `.Phony` member on
`Pattern<>`. Prototype: [`experiments/04-command-type`](experiments/README.md) (section 04).
Status: material for discussion, nothing decided here beyond the estimate.

## 1. How phony rules work today

| piece | where | what it does |
|---|---|---|
| `PhonyAction of string` (target), `PhonyRule of string * Recipe` (rule) | `src/core/Types.fs:12,88` | one name per rule; there is no multi-name phony rule |
| `name => recipe`, `phony "n" (...)` (CE op), `"n" <== [..]`, `"n" <<< [..]` | `XakeScript.fs:105`, `XakeScriptBuilder.fs:113`, `ScriptFuncs.fs:233,252` | all build a plain `PhonyRule`; `<==`/`<<<` add `alwaysRerun()` |
| `command "n" { }` | `RuleBuilder.fs:12` | a `PhonyRule` that **adds `AlwaysRerun`** to its result; `=>` does not |
| matching | `ExecCore.fs:88-91` | `Path.matchGroups pattern "" name` — the same pattern AST and regex as files (`PatternAst`, `Path.fs:171`), root `""`; groups and the `"0"` whole match go to `RuleMatches`; specificity ranks phony rules like file rules (`RuleSpecificityTests.fs:56`) |
| phony vs file | `ExecCore.makeTarget`, `ExecCore.fs:319-329` | `need ["x"]` takes strings; a name that **any** phony rule (also inside `DelegatedRule`) matches becomes `PhonyAction`, everything else a `FileTarget` under the project root. Phony wins over a same-named file |
| change stamp | `DependencyAnalysis.fs:34,45` | a phony target's stamp is its completion time from the database |
| `getTargetFile(s)` | `ScriptFuncs.fs:160-172` | fail for phony targets — phony rules have no outputs |

So a phony name **does** accept a pattern with groups, with the full file grammar: `*` stops at
`/` (`test::(project:*)/(slice:*)` uses `/` as a separator), `**` crosses it, case-insensitive on
Windows. The engine tells phony from file only by which rule kinds match the string at `need`
time; a generated name is a plain string, so **a typed phony type needs no engine change**.

### Real usages with groups

| rule | where | hand-built names for it |
|---|---|---|
| `"lock::(name:*)::(cfg:*)"` | `build.fsc.fsx:47`, `src/hermetic.tests/LockRule.fs:17`, `Locks.fs:160` (doc) | `build.fsc.fsx:51,56` `$"lock::{r}::{cfg}"`; `LockRule.fs:21,32` (incl. a projects x configs cross product); `Locks.fs:164,167,301` (doc) |
| `"test::(project:*)/(slice:*)"` | `build.fsx:117`, `build.fsc.fsx:81` | `testjobs.fsx:46,54` `testJobName` + `$"test::{..}"`; `build.fsx:106`, `build.fsc.fsx:73` |
| `"test-build::(project:*)"` | `build.fsx:109`, `build.fsc.fsx:74` | `build.fsx:121`, `build.fsc.fsx:85` |
| `"download:(pkg:*)"`, `"restore:(project:*)"` | `samples/resources.fsx:40,55` | `:49-50` `sprintf "download:%s"` |
| `"testsuite-(num:*)"` (delegated) | `samples/delegated-tests.fsx:99`, `delegated-proc.fsx:138` | `:116`/`:114` `sprintf "testsuite-%03d"`; `samples/worker.fsx:17` |
| `"clean::(b:*)::(cfg:*)"`, `"build::(x:*)"` | `RuleSpecificityTests.fs:56,81`, `PatternAstTests.fs:52` | engine tests, not script style |

9 grouped phony rules in scripts/samples, ~14 hand-built name sites (plus docs). Every grouped
rule is written with `=>`, none with `command`. The ~60 group-less phony rules (`"main"`,
`"clean"`, `command "pack"`) gain nothing from a type: they have no groups and one name.

## 2. Design sketch

### Name

| candidate | for | against |
|---|---|---|
| `Command<>` | the user's working name; the CLI word (`xake update-locks`); matches `command "x" {}` | `command "x" {}` adds `AlwaysRerun`, `=>` does not: `X.Rule` cannot be both. A converted `command` silently loses AlwaysRerun, or a converted `=>` silently gains it |
| `Phony<>` | the engine's word (`PhonyRule`, `PhonyAction`, CE op `phony`, docs); `=>` semantics are what "phony" means here | less friendly to newcomers |
| `Action<>`, `Task<>`, `Target<>` | — | collide with `System.Action`, `System.Threading.Tasks.Task`, `Xake.Target`: rejected |
| `Job<>`, `Step<>` | free names | new vocabulary for one type |

Lean: **`Phony<>`**, because `.Rule` must have exactly `=>` semantics (all real grouped rules
are `=>`; the lock rule calls `alwaysRerun()` itself). `Command<>` is fine if the doc states that
`.Rule` is `=>`, not `command`. No `Command` type exists in `Xake` today (`ShellOptions.Command`
is a record field, no clash). The prototype uses `Command<>`; the name costs nothing either way.

### Members, `Pattern<>` vs `Command<>`

Brief decision 4 names are used for the phony type; `Pattern<>` in the prototype keeps 03's
names (`Make`/`Path`/`All`/`Glob`), to be renamed when it is built.

| member | `Pattern<"...">` | `Command<"...">` | shared code |
|---|---|---|---|
| static params | one per output (decision 2), labels (decision 3) | **exactly one** — no multi-name phony rule exists | parser |
| `m.<group>` typed properties | yes | yes | yes |
| bare wildcards by position (decision 6) | yes | yes (`testsuite-*` -> `m._1`, `Target(_1 = ..)`) | yes |
| `Text` | first pattern (+ `Texts`) | the name pattern | yes |
| `Get : Recipe<_, T>` | yes | yes | yes |
| `Rule (T -> Recipe)` | `FileRule`/`MultiFileRule` | `PhonyRule`, `=>` semantics | 3 lines each |
| `Make (g..) : T` | yes | yes (checked: the name must match back) | yes |
| outputs `m.dll` / labelled | yes | **none** (no outputs; `getTargetFile` fails for phony) | — |
| `m.Name` | — | the matched action's name (`"0"` group; also for bare `*`) | — |
| `Target (g..) : string` | path | action name, for `need`/`<==` | the same runtime |
| `For (g: list..) : string list` | cross product, all outputs | cross product of names | yes |
| `Glob (?g..) : Fileset` | yes | **none** — phony names are not on disk | — |
| `Need (g..) : Recipe<unit>` | possible, not needed | `do! Lock.Need(name = r, cfg = c)` (prototyped) | — |

Multi-target: not meaningful (one name per `PhonyRule`). Glob/Fileset: meaningless. A
`TryMatch : string -> T option` (parse a name back) would fit both types; not prototyped.

### Before / after

`build.fsc.fsx:47-56` (the lock rule and `update-locks`):
```fsharp
"lock::(name:*)::(cfg:*)" => recipe {
    let! name = getRuleMatch "name"
    let! cfg  = getRuleMatch "cfg"
    let! d = Description.load (descriptionOf name)
    for r in d.ProjectRefs do do! need [ $"lock::{r}::{cfg}" ]
    ... }
command "update-locks" { for name, _ in projects do do! need [ $"lock::{name}::Release" ] }
```
```fsharp
type Lock = Command<"lock::(name:*)::(cfg:*)">
Lock.Rule(fun m -> recipe {
    let! d = Description.load (descriptionOf m.name)
    for r in d.ProjectRefs do do! Lock.Need(name = r, cfg = m.cfg)
    ... })
command "update-locks" { for name, _ in projects do do! Lock.Need(name = name, cfg = "Release") }
// LockRule.fs:32 (cross product):
"update-locks" <== Lock.For(name = projects, cfg = configs)
```
(`update-locks` stays a `command`: sequential on purpose, its `for` is the point.)

`build.fsx:106-121` + `testjobs.fsx:46,54` (the pattern and its formatter in two files today):
```fsharp
// testjobs.fsx
type TestJob   = Command<"test::(project:*)/(slice:*)">
type TestBuild = Command<"test-build::(project:*)">
let testJobNeeds job = ... |> List.map (fun j -> TestJob.Target(project = j.Project, slice = j.Name))
// build.fsx
"test" <== [ for j in testJobs -> TestJob.Target(project = j.Project, slice = j.Name) ]
TestBuild.Rule(fun m -> recipe { do! sh $"dotnet build {testProjectDir m.project} -c Release" { ... } })
TestJob.Rule(fun m -> recipe {
    let job = testJob m.project m.slice
    do! TestBuild.Need(project = m.project)
    ... })
```
`testJobName` disappears; a job name containing `/` fails at `Target` with the pattern and values
(`*` does not cross `/`) instead of silently matching nothing.

`samples/resources.fsx:40-55`:
```fsharp
type Download = Command<"download:(pkg:*)">
"downloads" <== Download.For(pkg = packages)
Download.Rule(fun m -> recipe { ... m.pkg ... })
```

## 3. `.Phony` member on `Pattern<>` — the alternative

`Pattern<"lock::(name:*)::(cfg:*)">.Phony(fun m -> ...)`: ~5 lines in the provider and 6 in the
runtime. What it does not give:
- the type does not know it is a name: `Path`/`Format` read oddly, `Glob` returns a fileset of
  names, output properties (`m.Out0`, labels) exist and are meaningless;
- a multi-output `Pattern<"a", "b">.Phony` can only fail at **run time** (script start), or the
  member is omitted for multi-output patterns — then `.Phony` is there for some patterns and not
  others, by a rule the user has to know;
- the reader of `type Lock = Pattern<...>` cannot tell a file layout from an action name until
  the registration line.
A separate type turns each of those into "no such member" (FS0039) at compile time — verified for
`Glob` and outputs in 04.

## 4. Cost

### Measured in the prototype (04 vs 03)

| | 03 | 04 | delta |
|---|---|---|---|
| `Provider.fs` | 119 | 145 | +26 (shared helpers 40, Pattern-only 53, Command-only 29) |
| `Runtime.fs` | 63 | 83 | +20 (`phony`, `name`, `needOne`) |
| engine | — | — | 0 |
Refactoring 03 into "shared helpers + two roots" took the shared part from inline code; no
logic was duplicated. Build time unchanged (6 s, dominated by `ProvidedTypes.fs`).

### Estimate for the product (incremental over building `Pattern<>` itself)

| item | `Command<>` (separate type) | `.Phony` member |
|---|---|---|
| design-time (provider) | 40-60 lines: second root + static param, `Rule`, `Name`, `Target`/`For`/`Need`, reject labels/multi; the parser, group props, positional wildcards, `Get`, `Make`, cross product shared | 5-10 |
| runtime (in `Xake.dll`) | 20-30 lines | 5-10 |
| engine | none | none |
| tests | 80-150 lines: run-time (rule, `Target`/`For`/`Need`, bare `*`, `/` in a value, phony-over-file resolution) + compile-fail cases (missing `Glob`/outputs, typo, missing arg) — on the compile-fail harness `Pattern<>` needs anyway | 40-80 |
| docs | 40-80 lines: a section in the typed-patterns page, cheatsheet line, `=>`-not-`command` note; optional conversion of `build.fsx`/`build.fsc.fsx`/`testjobs.fsx` (~30 lines changed) | 20-40 |
| **total** | **~180-320 lines, 1-1.5 days** | **~70-140 lines, 0.5 day** |

The difference is **about half a day to one day**. `Pattern<>` itself (AST reuse, separate static
params, labels, positional wildcards, `Glob` -> `Fileset`, packaging) is the large item; the
phony type rides on it.

### Complexity risks

- **Two provided types, one parser** — low. Both call the same parse/check functions
  (`PatternAst` via whatever reuse is chosen); the prototype shows the split is helper
  functions, not copies. Risk: member drift (a feature added to one, forgotten in the other);
  mitigated by building group members in one helper and a test that lists both types' members.
- **Separate static params (decision 2)** — makes `Command<>` simpler, not harder: one fixed
  parameter. `Pattern<>`'s N optional parameters do not touch it.
- **Labels (decision 3)** — apply to outputs only. If the label is in-string syntax, the
  `Command<>` parser must reject it (one check, one negative test). If labels become extra
  static parameters, nothing to do.
- **Positional bare wildcards (decision 6)** — fully shared (same tokens, same `_1` naming);
  `testsuite-*` is the phony case. 04 keeps 03's "no generation with a bare wildcard" and
  shows `Rule` + `m.Name` work on `testsuite-*`.
- **Reserved member names** — a group called `Name`, `Text`, `Get`, `Rule`, `Need`, `For`,
  `Target`, `Make` collides with a member. Shared issue (`Pattern<>` has `Text`/`Get` too); the
  provider should reject it with a clear message. `Command<>` adds `Name`/`Need` to the list.
- **`Rule` semantics** — `=>` vs `command` (AlwaysRerun) is the one real design trap; settle it
  with the name (see section 2).
- **Unregistered rule** — `Lock.Target(...)` is a string; if no `Lock.Rule` is registered,
  `makeTarget` treats it as a file and the build fails "no rule". Same as today and as
  `Pattern<>`. `Need` could build `PhonyAction` directly (no file fallback, a clearer error):
  `ExecCore` is `internal`, but the runtime part lives in `Xake.dll` (decision 1), so it can call
  `ExecCore.need [PhonyAction n]` — still no engine change. The prototype (outside `Xake.dll`)
  goes through the string `need`.
- **IDE, packaging** — no new risk: same provider assembly, one more root type.

## 5. Recommendation

Take the separate type: it costs ~0.5-1 day more than a `.Phony` member, adds no engine change,
shares the parser and the group machinery, and turns meaningless members (outputs, `Glob`,
multi-name) into compile errors. Build it **after** `Pattern<>`, in the same PR or the next one,
as the second root of the same provider. Members: `Rule` (`=>` semantics), `Get`, groups +
positional `_n`, `Name`, `Make`, `Target`, `For`, `Need`; no `Glob`, no outputs, one static param.

## 6. Open questions

1. Name: `Command<>` (user's) or `Phony<>` (engine's, and unambiguous about `=>` semantics)?
2. `.Rule` semantics: `=>` (no AlwaysRerun; recommended — matches every grouped rule today), or
   a second registration member with AlwaysRerun mirroring `command "x" {}`?
3. Keep `Need` (typed `need` of one name) — and should `Pattern<>` get the same member?
4. `TryMatch : string -> T option` for both types (parse a name back), now or later?
5. Group-less phony names (`Command<"clean">`) — allowed (harmless) or rejected as pointless?
