# Delegated builds: Resources, detached execution, and the delegated-rule hook

This note documents three opt-in extensions to Xake core that let an external system
express *delegated* builds — rules that execute across many machines, share their
results, and deduplicate concurrent demands — while leaving every existing local build
unchanged. They were added for [Qualhalla](https://example.invalid)'s delegated
test/build runner (EP-0018), but the mechanism is general.

The guiding constraint: **distribution is a property of a rule, not a new API verb.** The
script author keeps writing `need ["target"]`; whether a target runs locally, runs on a
remote agent, or is fetched from a shared store is decided at rule-definition time. Xake
core provides the *mechanism* and the extension points; *policy* — remote dispatch, result
storage, environment provisioning — lives in the caller.

## Conceptual map

The design follows two well-known references:

- **Shake's `Resource`** (`newResource` / `withResource`). A named, concurrency-limited
  resource the scheduler honors when running tasks in parallel. Xake had no equivalent; we
  port the model. See extension 1.
- **"Build Systems à la Carte"** (Mokhov, Mitchell, Peyton Jones) decomposes a build system
  into a **scheduler** — task ordering and parallelism — and a **rebuilder** — the decision
  whether a task must run. In Xake the scheduler is the worker pool (`WorkerPool.fs`) and
  the rebuilder is `NeedRebuild` / `getChangeReasons` (`DependencyAnalysis.fs`). The
  delegated-rule hook (extension 3) **replaces the rebuilder for one rule** — the caller's
  executor decides up-to-dateness by consulting a shared store — while reusing the
  scheduler. Detached execution (extension 2) is a scheduler refinement: it moves I/O-bound
  work off the CPU resource.

## 1. Resource

A `Resource` is a named concurrency budget, either bounded (`newResource name n`) or
unbounded (`newUnbounded name`). Acquire units around a recipe with `withResource`:

```fsharp
let downloads = Resource.newResource "downloads" 4

"out/*.zip" ..> recipe {
    do! withResource downloads 1 (recipe {
        // at most 4 of these run concurrently, regardless of the thread pool size
        do! fetchArtifact ()
    })
}
```

- A **bounded** resource is backed by a small FIFO agent that grants units in arrival
  order, so waiters never starve. A **bounded resource caps concurrency**; an **unbounded
  resource imposes no limit** (acquire/release are no-ops).
- **Divergence from Shake worth noting:** while a recipe is *blocked* waiting to acquire
  units, `withResource` **yields its CPU slot** (via `Scheduler.withYieldedSlot`). A blocked
  resource holder therefore never pins a worker thread, which avoids deadlock when the
  number of would-be holders exceeds the CPU-slot count. Once the units are granted, the CPU
  slot is reacquired and the body runs holding both. The resource is released in a `finally`,
  even on exception.

API (`Resource.fs`, `ScriptFuncs.fs`):

```fsharp
Resource.newResource  : string -> int -> Resource     // bounded, quantity >= 1
Resource.newUnbounded : string -> Resource
withResource          : Resource -> int -> Recipe<ExecContext,'b> -> Recipe<ExecContext,'b>

// async-level bracket and low-level acquire/release for non-recipe code (e.g. an executor):
Resource.withAcquired    : Resource -> int -> Async<'a> -> Async<'a>  // bracket: acquire, run body, release
Resource.acquire         : Resource -> int -> Async<unit>
Resource.release         : Resource -> int -> unit
```

`withResource` is the recipe-level bracket — it yields the CPU slot while waiting for the
resource so the worker pool is not starved. `withAcquired` is the async-level bracket for
code that does not hold a CPU slot, such as a `DelegatedExecutor`.

## 2. Detached execution — decoupling I/O-bound rules from the CPU pool

Xake bounds parallelism with a single semaphore sized to `Threads` (default
`Environment.ProcessorCount`). A rule that merely *waits* on a remote operation is not doing
CPU work, yet it would hold a CPU slot for the whole wait — so a build with, say, 100
concurrent remote dispatches would be throttled to processor count.

`runDetached` runs a recipe body **without holding a CPU slot** for its duration: the slot
is released before the body runs and reacquired after (guaranteed even on exception). Many
more detached rules than CPU cores can be in flight at once.

```fsharp
"remote-suite" => recipe {
    do! runDetached (recipe {
        // blocks on remote work; does not consume a local CPU slot
        do! awaitRemoteWorkflow ()
    })
}
```

The concurrency of detached work is governed by whatever `Resource` the caller chooses to
wrap it in (a "dispatch budget"), not by the CPU pool. Core does not bake in a dispatch
resource — the budget is the caller's policy.

```fsharp
runDetached : Recipe<ExecContext,'b> -> Recipe<ExecContext,'b>
```

Built on the existing `Scheduler.withYieldedSlot`.

## 3. Delegated-rule hook — pluggable execution and rebuilder

`delegated executor rule` wraps any inner rule (file, phony, or multi-file) so its
up-to-date check **and** execution are delegated to a caller-supplied function:

```fsharp
type DelegatedExecutor<'ctx> =
    'ctx -> Target list -> (unit -> Async<BuildResult>) -> Async<BuildResult>
```

The executor receives the targets and a `body` thunk that runs the inner recipe locally and
yields its `BuildResult`. It owns the full rebuilder decision:

1. compute an identity key for the target,
2. look the key up in a shared, run-scoped result store,
3. on a **hit**, return the stored result **without calling `body`**,
4. dedup against an **in-flight** execution of the same key (concurrent demands collapse to
   one), and
5. on a **miss**, call `body` once, publish the result to the store, and return it.

Script authors write nothing special — `need ["target"]` is unchanged. The rule is marked at
definition time:

```fsharp
rules [
    ("run-suite-*" => recipe { do! runSuite () }) |> delegated qualhallaExecutor
]
```

### How core runs a delegated rule (`ExecCore.execOne`)

- The rule is still posted through the scheduler's `Run` message, so **local per-target
  dedup by target name is preserved** (two demands for the same target still collapse to one
  task).
- The **`ctx.NeedRebuild` gate is skipped** — the executor *is* the rebuilder.
- The **entire delegated task — the executor AND the `body` thunk — runs detached**
  (`withYieldedSlot` around the whole executor call). A delegated rule therefore **never
  consumes a local CPU slot**, and is **never bounded by the `Threads` pool**. Its
  concurrency is governed solely by whatever dispatch `Resource` the executor applies (or is
  unbounded). This is goal #2: delegated work is I/O-bound waiting on a remote agent, not
  local CPU work.
  - Consequence: **`runDetached` inside a delegated body is redundant** — the engine has
    already detached it. (`runDetached` is for *non-delegated* I/O-bound rules, whose body
    runs holding a CPU slot.)
  - A delegated body should be **leaf work**; declare prerequisites with `need` in the
    *enclosing* rule, not inside the delegated body. (A `need` issued from inside a detached
    body still works but may briefly exceed the local `Threads` count during the wait, since
    `withYieldedSlot` would release a slot the detached body does not hold.)
- The returned `BuildResult` is still **stored in the local `.xake` database**, so downstream
  targets that `need` this one resolve their `ArtifactDep` as built.

### Two independent dedup layers

- **Worker pool** dedups identical *local targets* by `Target.FullName`.
- **Executor** dedups identical *identity keys* — possibly from different targets or
  different processes — via its own in-flight map.

These are orthogonal; the executor's key-based dedup is the one that spans machines.

### Cross-machine caveat

A result published by another machine may carry `FileDep` timestamps that don't match the
local filesystem. On the next *local* run this can trigger one re-check/rebuild of that
target. This is accepted (correctness over caching). The artifact bytes themselves are
delivered out of band by the executor (e.g. object storage); core stores only the
`BuildResult` record.

## Examples

Three runnable samples under `samples/` (each `#r`s the locally built
`out/netstandard2.0/Xake.dll`; build it once with `dotnet fsi build.fsx -- -- build`, then
run a sample with `dotnet fsi`):

| Sample | Demonstrates |
|--------|--------------|
| `delegated.fsx` | All three mechanisms together: a delegated rule whose body runs a real external process; an in-memory run-scoped store showing a **cache hit** (phase 2) and **in-flight dedup** of two targets that share one identity; a *non-delegated* `slow-io` rule using `runDetached`; a dispatch budget via `Resource`. |
| `delegated-tests.fsx` | 100 test suites via ONE masked rule `testsuite-(num:*)`, governed by a dispatch budget of 20. `THREADS=4` yet peak concurrency = 20 — **a delegated rule is not bounded by the CPU pool**, and `runDetached` is intentionally absent (redundant for a delegated body). |
| `delegated-proc.fsx` + `worker.fsx` | Two-process variant: the orchestrator dispatches each suite to a **separate worker process** and collects results from an on-disk run-scoped store. Phase 1 spawns workers (distinct PIDs); phase 2 serves every suite from the store with **no workers** (cache hit). Makes visible that the executor is the seam where work leaves the local process. |

**Where the I/O lives decides whether you need `runDetached`:**
- I/O in the rule **body** (e.g. the external `sleep` in `delegated-tests.fsx`): a
  *non-delegated* rule would hold a CPU slot and needs `runDetached`; a *delegated*
  rule's body is already detached by the engine, so `runDetached` is redundant.
- I/O in the **executor** (e.g. the worker dispatch in `delegated-proc.fsx`): always
  detached by the engine — no `runDetached` anywhere.

There is no `maxThreads` computation-expression operation — set the CPU-slot count via the
options record (`RulesBuilder { ExecOptions.Default with Threads = N }`, as the samples do)
or the `-t N` CLI flag.

## Files

| File | Change |
|------|--------|
| `Types.fs` | `DelegatedExecutor<'ctx>` type; `DelegatedRule` case on the `Rule` DU |
| `Resource.fs` | `Resource` type, `newResource`/`newUnbounded`, FIFO agent, `acquire`/`release`/`withAcquired` |
| `ExecCore.fs` | `locateRule`/`getAction`/`isPhonyRule` recurse through `DelegatedRule`; `execOne` delegated branch runs the whole task detached |
| `XakeScript.fs` | `delegated` combinator |
| `ScriptFuncs.fs` | public `runDetached`, `withResource` |
| `tests/DelegatedTests.fs` | resource cap / unbounded / detached-scale / **delegated-not-CPU-bounded** / hook miss-dedup-hit |
| `samples/` | `delegated.fsx`, `delegated-tests.fsx`, `delegated-proc.fsx`, `worker.fsx` |
