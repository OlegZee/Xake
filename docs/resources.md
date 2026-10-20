# Resources and `need`: the hold-and-wait deadlock

Design note. Status: **decided and implemented in Xake 3.10**: (c) + (a), with the decisions
in section 6. Scope: Xake core (`Resource`, `withResource`, `requiring`, `need`) and the
compiler launcher shared by `Csc.run` and `Fsc.run`. The user-facing rules are in
[delegated.md](delegated.md#1-resource) and [csc-server.md](csc-server.md#limiting-concurrent-compiles-csc_jobs-fsc_jobs).

## 0. Summary

A recipe that calls `need`/`needFiles` while it holds units of a bounded `Resource` can
deadlock the build: every unit ends up held by a recipe that waits for dependencies, and the
dependencies wait for a unit. Neither the CPU slots nor any thread is exhausted, so the build
hangs at zero CPU with nothing to report. Shake forbids `need` inside `withResource` for
exactly this reason. Recommendation: **(c)** throttle compilers at the right place (inside the
compiler launcher, after all its `needFiles`, controlled by the script variables `CSC_JOBS` /
`FSC_JOBS`), plus **(a)** fail fast on a `need` of a rule target while a resource is held.
Engine-level detection **(d)** is a later safety net; **(b)** is rejected; **(e)** is
optional sugar.

## 1. The incident

The consumer script wrapped the library compile recipe in a resource to cap compilers at 6:

```fsharp
let cscJobs = Resource.newResource "csc" 6
"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> withResource cscJobs 1 (Locks.compileAsWith roots lockOf fwk versions)
```

`Locks.compileAsWith` → `compileDll` (`src/hermetic/Locks.fs` ~490–590) calls, in order:
`Lock.loadWith` (a `needFiles` of the lock), `staleImports` (a `needFiles` of the msbuild
imports), then `needFiles` of the **project-reference outputs** (line ~518, other `.dll`
targets of the same rule), then `Lock.compileFromWith` → `runCsc` → `Csc.run` →
`CompilerRunner.run`, which itself `needFiles` the compiler, the `.resx` files and every
input on the command line (`src/dotnet/CompilerRunner.fs` lines 94, 117, 154) before it
starts the process. All of that ran inside the resource. Result on a solution of ~720
projects: 721 tasks started, 0 completed, no CPU.

## 2. The mechanism, from the engine code

### 2.1 The pieces

- **CPU slots** (`WorkerPool.fs`): a `SemaphoreSlim(maxThreads)`. `maxThreads` is `-t`
  (`Program.fs`), default `Environment.ProcessorCount` (`ExecTypes.fs`, `XakeScript.fs`). The
  pool agent takes a slot (`throttler.WaitAsync`) before it starts a task, and the task
  releases it in its `finally`. "Started <target> as task n" is logged after the slot is
  taken (`ExecCore.run`).
- **`Scheduler.withYieldedSlot work`**: `Release()` the caller's slot, run `work`, then
  `WaitAsync` to get a slot back. Pure async: no thread is blocked while `work` waits.
- **`need`** (`ExecCore.need` → `execNeed`): the single funnel for `need`, `needFiles` and
  `dependsOn`. It runs `execParallel` (one `Run` message per target to the pool agent, then
  awaits each task) **inside `withYieldedSlot`**: a recipe waiting for its dependencies holds
  no CPU slot.
- **`withResource r n body`** (`ScriptFuncs.fs`):

  ```fsharp
  do! Scheduler.withYieldedSlot ctx.Engine.Scheduler (Resource.acquire resource units)
  try  let! b = body in return b
  finally Resource.release resource units
  ```

  The slot is yielded **for the acquire only**. Once the units are granted the slot is taken
  back and `body` runs holding **both** the slot and the units. Nothing in `body` knows that
  units are held, so a `need` in `body` yields the CPU slot (as every `need` does) but keeps
  the units.
- **`Resource`** (`Resource.fs`): a `MailboxProcessor` with `available` and a FIFO queue of
  `(units, reply)`. `Acquire` replies at once when units are free and nobody is queued,
  otherwise it queues. `Release` drains the queue head-first. `PostAndAsyncReply`: a waiter
  is a parked continuation, not a thread.
- **`requiring r rule`** (`XakeScript.fs`) wraps the **whole** recipe of a rule the same
  way (1 unit), so any `need` in such a rule runs with the unit held.

### 2.2 The deadlock on paper

Take `-t 12`, `csc` resource of 6, projects with references.

1. The root `need`s every `.dll`. The pool starts tasks as slots free up. Each compile task
   takes a slot, reaches `withResource`, yields the slot and posts `Acquire 1`. The first 6
   get units; the rest queue FIFO. Since every waiter yields its slot, the pool keeps
   starting tasks until **every** compile target is started and parked: that is the "721
   started".
2. A holder `H` (it has a unit) reaches `needFiles [refs]`. `execNeed` yields `H`'s slot and
   posts `Run` for each reference. A reference `R` already started is joined (its task is
   returned from the pool's `running` map); `H` now awaits `R`'s task.
3. `R` is parked in the resource queue behind other waiters. It can only run when a unit is
   released. Units are released only when a holder finishes its body.
4. Leaf projects (no references) that got a unit finish and release; FIFO hands the unit to
   the next waiter, which is most often a non-leaf whose references are still queued
   behind it. After a few rounds all 6 units belong to holders in step 2, and every
   reference they await is in step 3.

Wait-for graph at the hang: `H1..H6 → (await task) → R… → (Acquire queue) → unit held by
H1..H6`. A cycle through the resource, though the **dependency graph itself is acyclic**.
Nothing holds a CPU slot (holders yielded it in `need`, waiters in `acquire`), no thread is
blocked, no process runs: zero CPU, no error, no progress line moves.

The same holds with **one** unit and two rules that need each other's outputs under the
same lock (`requiring restoreLock` on `restore:A` which needs `restore:B`): an immediate
hang.

### 2.3 Could the worker pool also be exhausted?

Not by this pattern. Both waits (`need`, `acquire`) go through `withYieldedSlot`, so a
recipe that waits holds no slot; `-t` caps only recipes that are **executing** (including a
running compiler process, which is awaited while the slot is held). The slot pool is
exhausted only by recipes that block while holding the slot (a synchronous wait, a
`Thread.Sleep`, a lock taken outside `withResource`), which is a different bug. One related
detail: the pool agent awaits `throttler.WaitAsync` inside its message loop, so while all
slots are busy it does not even answer `Run` for targets that are already running or
finished (head-of-line blocking). That only delays; slot release does not go through the
agent, so it cannot deadlock by itself.

## 3. What Shake does

Shake's documentation of `withResource`: *"Run an action which uses part of a finite
resource. ... You cannot depend on a rule (e.g. need) while a resource is held."*
The implementation wraps the body in `blockApply` (`applyBlockedBy (Just reason)`, the
reason naming the resource); `apply` (the root of `need`) checks the flag and throws
`errorNoApply`:

```
Build system error - cannot currently introduce a dependency (e.g. calling 'apply')
  Reason:    <within withResource using the resource>
  Key type:  ...
  Key value: ...
Move the call earlier/later
```

`unsafeAllowApply` lifts the block for a sub-action ("use with caution"). Shake has no
rule-level wrapper like `requiring`, and it deliberately does not release the resource
around a `need`.

## 4. Options

### a. Forbid `need` while a resource is held (Shake's rule)

**Detection.** Add a task-level field to `ExecContext`, e.g.
`HeldResources: string list` (resource names, innermost first).

- `withResource` runs the body with an extended context, the way `runCsc`'s `collecting`
  swaps the logger: `Recipe (fun (r, ctx) -> … A.runAction body (r, { ctx with
  HeldResources = resource.ResourceName :: ctx.HeldResources }))`. `requiring`'s
  `wrapRecipe` does the same.
- `ExecCore.newTaskContext` **resets** it to `[]`: a child task is built from the needing
  task's context (`execOne ctx` inside `execNeed`), and must not inherit it.
- `ExecCore.execNeed` (the one funnel; the top-level `execParallel` at `runTargets` has no
  resource) checks it before posting anything and raises `XakeException`:

  > `need inside withResource: target 'out/Release/net472/B.dll' needs 'out/Release/net472/A.dll'
  > while it holds the resource 'csc'. A dependency built under a held resource can deadlock
  > the build. Move the need before withResource (or acquire the resource around the
  > command only).`

- Construction sites of `ExecContext` (`XakeScriptBuilder.fs:154`, `ExecCore.fs:~310`) get
  `HeldResources = []`.

**Refinement (recommended).** Fail only for targets that **have a rule**
(`locateRule` → `Some`). A `need` of a plain source file never schedules anything (it is
answered `JustFile` without the pool), cannot deadlock, and is common inside critical
sections (a recipe that records the files it read). Shake forbids both; the refinement keeps
existing scripts working where they are safe and still catches every case that can hang.
Even a rule target that is already built in this run is refused: whether it is finished is
timing-dependent, and an error that depends on timing is worse than a consistent one.

**Escape hatch.** `allowNeed body` (Shake's `unsafeAllowApply`): runs `body` with
`HeldResources = []`. For the rare case where the author knows the dependency's closure
never touches the resource.

**`requiring`.** It wraps the whole rule, so under (a) a `requiring` rule that `need`s a
rule target fails. That is correct (it is the same hazard, and the one-unit case hangs
immediately), but it changes a public API's behaviour; see open question 2.

- Pros: turns a silent hang into an immediate, named error at the first offending `need`;
  small; matches Shake; no runtime cost (a list check per `need`).
- Cons: breaking for scripts that need rule targets inside a resource today and happen not
  to hang (small graphs, enough units); a public record gains a field (source break only for
  code that constructs `ExecContext` itself, which is engine-internal in practice).
- Existing library uses checked: `Project.withProjectLock` (its `needFiles` of imports and
  git files come after the locked body), `Restore` (`Locks.forRoot`: download only),
  `Described` (per-key restore locks, nested in key order: restore only), `Sign`
  (`withAcquired`, executor level, not a recipe). None needs inside the resource.
- Size: ~40 lines core, ~120 lines tests.

### b. Release the units around a `need`, re-acquire after

`execNeed` would release every held resource, build the dependencies, then re-acquire.

Why Shake does not, and why it is unsound here as a default:

- **Breaks the body's invariant.** A `Resource` of 1 is used as a mutex
  (`withProjectLock`, the restore locks): the body checks state after acquiring ("whoever
  held the lock may have restored exactly what this entry needs — recheck") and relies on
  exclusivity until it leaves. A silent release in the middle makes the critical section
  non-atomic without any sign in the code.
- **Nested holds.** `Described.prefetch` holds several per-key locks taken in key order. A
  re-acquire must release all and take them again in the original order, or it introduces
  lock-order deadlocks; partial re-acquire is a new class of bug.
- **Fairness.** The re-acquire goes to the back of the FIFO queue: a recipe can lose its
  place repeatedly, and long chains get the worst latency.
- **Non-reentrancy hides.** A dependency that takes the same resource would now succeed,
  which hides real design errors.
- It is sound only for pure **throttles** (units count processes, nothing in the body
  depends on continuity), and only then if the body holds no other state. That would need a
  second resource kind (`newThrottle` vs `newLock`) and the same context plumbing as (a).

Verdict: reject as default; not worth a second resource kind while (c) removes the need.

### c. The compiler launcher takes the resource at the right place

**The seam.** Not `runCsc` and not `Csc.run`: both lead into `CompilerRunner.run`, which
`needFiles` the compiler, the `.resx` and every input (`CompilerRunner.fs` 94, 117, 154). A
resource around them would still be held across `need`. The one correct place is **inside
`CompilerRunner.run`, around the process launch only**: after `do! verify hashedFiles`,
wrapping the response-file write, the `shell { … }` call and the post-processing in its
`try … finally`. At that point every dependency is built and verified; the body only runs
the compiler. `CompilerRunner` is internal and is the single launcher of `Csc.run` and
`Fsc.run`, so this is no hook on `Csc.run` and no new public surface on the record API: the
existing decision (no hooks on `Csc.run`) stands.

**The limit.** Script variable `CSC_JOBS` (positive integer; unset, empty or `0` =
unlimited), then the environment variable `XAKE_CSC_JOBS`, then unlimited: the same lookup
order and spelling convention as `CSC_SERVER` / `XAKE_CSC_SERVER`
(`Csc.CompilerServer.resolve`). A value that is not a non-negative integer is traced as a
warning and taken as unlimited. One difference from `CSC_SERVER`, deliberate: the value is
read from `ctx.Vars` **without** `getVar`, so it is **not** recorded as a dependency.
Concurrency does not change any output; changing `-d CSC_JOBS=4` must not rebuild the
solution.

**The resource.** Process-wide, created once per tool, the pattern already used by
`ProjectLocks` and the restore locks: a `ConcurrentDictionary<string, Resource>` in
`CompilerRunner`, `GetOrAdd ("csc", fun _ -> Resource.newResource "csc" n)`. When a later
build in the same process (tests, a long-running host) reads a different `n`, it calls
`Resource.resize`. Unlimited means no resource at all on that path (no agent round-trip).
The acquire goes through `withResource`, so the wait yields the CPU slot, and with (a) in
place the guard would catch any future `need` added inside that block.

**fsc.** `Fsc.run` goes through the same `CompilerRunner.run`, so it is covered by the same
seam. `RunPlan` (internal) gets the variable names from the caller: `Csc.run` passes
`CSC_JOBS`/`XAKE_CSC_JOBS`, `Fsc.run` passes `FSC_JOBS`/`XAKE_FSC_JOBS`, each with its own
resource. fsc has no server and runs the whole compiler per process (heavier on memory), so
a separate budget is the useful default; see open question 3.

**Interplay.** `-t` caps all executing recipes; `CSC_JOBS` caps compiler processes below
that, so restores, copies and the hash checks keep using all slots. With `CSC_SERVER=on`
the throttled processes are the thin clients, but the server compiles one request per client
connection, so the cap still bounds concurrent compilations. The analyzer-crash retry in
`runCsc` calls `Csc.run` a second time: it releases and re-acquires a unit, which is fine.

**Consumer migration.** Drop the `withResource` wrapper; pass `-d CSC_JOBS=6` (or set it
with `var` in `xakeScript`).

- Pros: removes the hazard where it was introduced; the knob lives with `CSC_SERVER`;
  covers every caller of `Csc.run`/`Fsc.run`, hermetic or not; no API change.
- Cons: ships in the `Xake` package (`src/dotnet`), not in `Xake.Hermetic.Dotnet`; the
  hermetic package's dependency range on Xake must include the release.
- Size: ~50 lines in `CompilerRunner.fs`/`Csc.fs`/`Fsc.fs`, ~100 lines tests.

*Interim, hermetic-only (not recommended):* acquire in `Locks.compileDll` around
`Lock.compileFromWith`, after its own reference `needFiles`. The runner's later `needFiles`
then meet only finished targets and plain files, so it would not hang in practice, but it is
still a `need` under a resource (option (a) would reject it) and it misses `Lock.compile`,
`Lock.build` and `Fsc`.

### d. Engine-level deadlock detection

The engine could notice "nothing can progress" and fail with a dump instead of hanging.

Signals that exist or are cheap to add:

- executing recipes: `maxThreads - Throttle.CurrentCount` (slots held);
- why a slot is yielded: `withYieldedSlot` serves `need`, resource acquire, `runDetached`
  and delegated executors. Tagging the call (a counter per kind) separates "waiting for
  something external" (detached, delegated: may progress) from "waiting inside the graph"
  (need, acquire);
- per resource: a `Stats` message to the agent returning `available`, queue length, and the
  holders (names would need the acquiring target passed with `Acquire`).

Rule: slots held = 0, detached/delegated waits = 0, at least one resource has queued
waiters, and no task completed for a quiet period (e.g. 2 s) → deadlock. Then fail every
queued acquire with an error that lists the resource, its holders and the targets each
holder awaits. Failing a waiter needs the reply channel to carry an error
(`AsyncReplyChannel<Result<unit, exn>>`), a small protocol change in `Resource.fs`.

- Pros: catches every hold-and-wait shape, including ones (a) allows (`allowNeed`, an
  executor-level `withAcquired`) and future patterns; good diagnostics.
- Cons: heuristic (a recipe awaiting an external event while holding no slot and not
  marked detached would be a false positive); a watchdog timer and counters in the hot
  path; more code than (a)+(c) together. It reports after the fact; (a) reports at the
  offending line.
- Size: ~200 lines core, ~150 lines tests. Worth it later, as a safety net.

### e. Ordering helper (`needThen` / `withResourceAfter`)

```fsharp
let withResourceAfter (deps: Filelist) r n body = recipe { do! needFiles deps; return! withResource r n body }
```

Pure sugar: it helps only when the dependencies are known before the body starts. In the
incident they are computed inside the body (`compileDll` reads the lock to learn the
references), so the helper would not have been usable without restructuring the recipe, and
it does nothing to stop the misuse. It is a fine thing to name in (a)'s error message ("need
first, then withResource"), not a fix on its own. Optional.

## 5. Recommendation

**(c) + (a)**, in one PR to `dev`; (d) later if a hang of this kind recurs; (b) rejected;
(e) only if the owner wants the sugar.

### Work breakdown

Core (`src/core`):
- `ExecTypes.fs`: `HeldResources: string list` on `ExecContext`.
- `ScriptFuncs.fs`: `withResource` extends it for the body; new `allowNeed`.
- `XakeScript.fs`: `requiring`'s `wrapRecipe` extends it.
- `ExecCore.fs`: `newTaskContext` resets it; `execNeed` refuses rule targets while it is
  non-empty, naming the resource(s), the needing target and the needed targets.
- `XakeScriptBuilder.fs`, `ExecCore.fs` context construction: initial `[]`.

Compiler launcher (`src/dotnet`):
- `CompilerRunner.fs`: process-wide per-tool resources; `RunPlan` gets the jobs variable
  names; the launch block (rsp → `shell` → runtimeconfig) runs under `withResource` when a
  limit is set; the value read from `ctx.Vars`, then the environment, not recorded.
- `Csc.fs` / `Fsc.fs`: pass `CSC_JOBS`/`XAKE_CSC_JOBS` and `FSC_JOBS`/`XAKE_FSC_JOBS`.

Tests (`src/tests`; the resource tests live in `DelegatedTests.fs`, compiler tests in
`CscServerTests.fs`/`CompilerCrashTests.fs`/`FscTests.fs`):
- `need` of a rule target inside `withResource` fails fast (with a timeout guard so a
  regression fails instead of hanging), message names resource and both targets.
- `need` of a plain file inside `withResource` succeeds and is recorded.
- `need` before `withResource` succeeds; `allowNeed` lifts the guard.
- a child task started by a `need` does not inherit `HeldResources`.
- the incident shape: a chain of 10 targets, resource of 2, `need` of the previous target
  inside the resource → fails fast today (it hung before).
- a `requiring` rule that needs a rule target → per decision on question 2.
- `CSC_JOBS=1` with several independent compiles (the fake compiler used by the crash
  tests, recording start/stop) → never more than one compiler at a time; unset → overlap
  allowed; a reference chain with `CSC_JOBS=1` completes; changing `CSC_JOBS` does not
  rebuild; `XAKE_CSC_JOBS` used when the script variable is unset; `FSC_JOBS` likewise.

Tests (`src/hermetic.tests`): one `Locks.compileAs` reference chain built with
`-d CSC_JOBS=1` completes (no deadlock through the library recipe).

Docs:
- `docs/delegated.md` §1 (Resources): the rule "no `need` of a rule target while a resource
  is held", why (hold-and-wait), `allowNeed`, and that `requiring` wraps the whole rule;
  correct the "divergence from Shake" paragraph (Shake's waiters do not pin threads either;
  the real divergence is the plain-file refinement).
- `docs/csc-server.md`: `CSC_JOBS` / `XAKE_CSC_JOBS` next to `CSC_SERVER`, `FSC_JOBS`;
  note that it is not a dependency.
- `docs/hermetic-guide.md`: one line where the compile rule is shown.
- `docs/release-notes.md`; `samples/resources.fsx` comment on `requiring`.

### PR

- Title: **`resources: fail fast on need inside withResource; CSC_JOBS/FSC_JOBS throttle the compiler launch`**
- Size: ~100 lines production, ~350 lines tests, ~80 lines docs. Can be split into
  "resources: need inside withResource fails fast" and "compilers: CSC_JOBS/FSC_JOBS" if
  smaller reviews are preferred; (c) does not depend on (a).
- Release: both parts ship in the `Xake` package. A field added to a public record and a
  behaviour change of `withResource`/`requiring` argue for a minor version (3.10); then
  `Xake.Hermetic.Dotnet`'s range `[3.9.0.30, 3.10)` must be widened in a follow-up.

## 6. Decisions (were: open questions for the owner)

The owner took the recommended default on every question:

1. Only targets that **have a rule** are refused; a plain file without a rule is allowed.
2. `requiring` is subject to the guard (it wraps the whole rule), documented so.
3. fsc has its own `FSC_JOBS` / `XAKE_FSC_JOBS`; csc uses `CSC_JOBS` / `XAKE_CSC_JOBS`.
4. The guard is an error from the first release, no warning period.
5. The core goes to 3.10. The hermetic package's range is **not** widened in the same change:
   it copies an `ExecContext` (`{ ctx with Logger = ... }` in `Lock.fs`), which compiles to the
   record's full constructor, so a build of it on 3.9 does not run on 3.10. It needs a release
   built on 3.10 (the range policy: one release per core minor).
6. No engine-level detection (d) in this change.

The questions as they were put:

1. **Plain-file refinement**: refuse only rule targets (default), or every `need` like
   Shake? → rule targets only.
2. **`requiring`**: subject to the guard (default), exempt, or deprecated in favour of
   `withResource` around the command? → subject to the guard, documented; it is the same
   hazard.
3. **fsc budget**: separate `FSC_JOBS` (default) or one shared compiler budget under
   `CSC_JOBS`? → separate.
4. **Error or warning first**: error immediately (default), or one release of warnings? →
   error: the alternative is a hang.
5. **Version**: 3.10 with the hermetic range widened (default), or 3.9.x? → 3.10.
6. **(d) detection**: now or later? → later, tracked as an idea.
