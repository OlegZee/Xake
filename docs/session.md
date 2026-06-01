# Session notes — distributed Xake (EP-0018)

A handoff for continuing tomorrow from a clean slate. Full design is in
[`docs/distributed.md`](distributed.md); this file captures the **non-obvious** knowledge,
current state, and how to pick back up.

## What this is

A fork of Xake (branch `feature/distributed-xake`) extended so an external system
(Qualhalla — Temporal + S3 test/build runner) can express *distributed* builds where
**distribution is a property of a rule, not a new verb**. Three opt-in core mechanisms were
added; all policy (Temporal/S3/env) stays in the caller. Source of truth for intent:
- `…/qualhalla/.../EP-0018/xake-core-extension-prompt.md` (the task)
- `…/qualhalla/.../adr/0010-distributed-xake-primitives.md` (the decision)
- Local plan: `~/.claude/plans/follow-users-olegz-projects-work-qualhal-cheeky-dahl.md`

## The three mechanisms (what shipped)

1. **`Resource`** (Shake-style) — `Resource.newResource name n` / `newUnbounded name`;
   `withResource res n recipe` in recipe code. Bounded = FIFO agent (no starvation).
2. **`runDetached recipe`** — runs a body without holding a CPU slot (for I/O-bound waits).
3. **`distributed executor rule`** — wraps any rule; the caller-supplied
   `DistributedExecutor<'ctx> = 'ctx -> Target list -> (unit -> Async<BuildResult>) -> Async<BuildResult>`
   owns the up-to-date check + execution (store lookup / in-flight dedup / run+publish).

## Key insights / gotchas (the stuff that cost time)

- **Distributed rules are NEVER bounded by the local CPU pool.** This is the final, correct
  design. `ExecCore.runDistributed` runs the *entire* task — executor **and** body — detached
  (`withYieldedSlot` around the whole executor call). Concurrency is governed only by the
  dispatch `Resource` the executor applies (or is unbounded). Therefore **`runDetached`
  inside a distributed body is redundant.** (An earlier version reacquired a CPU slot for the
  body via a `withReacquiredSlot` helper — that was wrong and has been removed.)

- **`withYieldedSlot` assumes the caller holds exactly one CPU slot** (it does `Release()`
  first, then reacquires). Calling it when you hold **no** slot releases a *phantom* permit
  and corrupts the scheduler semaphore (silently disabling the `Threads` limit). This bit us:
  `Resource.acquireYielding` (which wraps `withYieldedSlot`) must only be used from
  slot-holding **recipe** code. Inside a **DistributedExecutor** (already detached) use the
  non-yielding **`Resource.acquire`** instead. That distinction is the reason `acquire` exists.

- **"Where the I/O lives" decides whether you need `runDetached`:**
  - I/O in the rule **body** + *non-distributed* rule → needs `runDetached` (body holds a slot).
  - I/O in the rule **body** + *distributed* rule → already detached by engine, redundant.
  - I/O in the **executor** → always detached, never need `runDetached`.

- **A distributed body should be leaf work.** Declare `need` dependencies in the *enclosing*
  rule. A `need` from inside a detached distributed body still works but can briefly exceed
  the `Threads` count during the wait (same phantom-release mechanism).

- **Two independent dedup layers:** worker pool dedups by `Target.FullName` (local); the
  executor dedups by *identity key* (cross-target / cross-process). Orthogonal.

- The distributed branch **skips `ctx.NeedRebuild`** (the executor is the rebuilder) but
  **still `Store`s** the `BuildResult` so downstream `ArtifactDep`s resolve as built.

- **No `maxThreads` CE operation exists.** Set CPU slots via the options record
  (`RulesBuilder { ExecOptions.Default with Threads = N }`) or the `-t N` CLI flag.

- **F# gotcha that wasted time:** `do! need names; printfn …` on one line mis-parses as
  `do! (need names; printfn …)` — `need` is built but never run. Keep CE statements on
  separate lines.

## Current state

- ✅ Builds clean (net462 + netstandard2.0). Full test suite: **216 passed, 1 skipped, 0 failed**.
- ✅ 7 tests in `src/tests/DistributedTests.fs` (resource cap / unbounded / detached-scale /
  **distributed-not-CPU-bounded** / hook miss-dedup-hit).
- ✅ 4 runnable samples (`samples/distributed.fsx`, `distributed-tests.fsx`,
  `distributed-proc.fsx` + `worker.fsx`). Verified: `distributed-tests.fsx` shows `peak=20`
  with `THREADS=4` (proves CPU-pool decoupling); `distributed-proc.fsx` shows separate worker
  PIDs + phase-2 cache hit with no workers.
- ✅ Docs: `docs/distributed.md` (design + samples + API + caveats).
- ⚠️ **Nothing is committed.** All work is uncommitted on branch `feature/distributed-xake`.

### Files touched
- `src/core/Types.fs` — `DistributedExecutor<'ctx>`, `DistributedRule` DU case.
- `src/core/Resource.fs` — **new**: `Resource`, `newResource`/`newUnbounded`, FIFO agent,
  `acquire` (non-yielding) / `acquireYielding` / `release`. Registered in `Xake.fsproj`.
- `src/core/ExecCore.fs` — `locateRule`/`getAction`/`isPhonyRule` recurse through
  `DistributedRule`; `execOne` distributed branch runs the whole task detached.
- `src/core/XakeScript.fs` — `distributed` combinator.
- `src/core/ScriptFuncs.fs` — public `runDetached`, `withResource`.
- `src/core/WorkerPool.fs` — (a `withReacquiredSlot` helper was added then **removed**; net
  no change beyond what's needed — `withYieldedSlot` is the only slot helper).
- `src/tests/DistributedTests.fs` — **new**; registered in `tests.fsproj`.
- `docs/distributed.md`, `docs/session.md` — **new**.

## How to pick back up

```bash
# build + full test suite
dotnet fsi build.fsx -- -- build test        # self-hosting build (rebuilds out/)
dotnet test src/tests                          # or just the tests
dotnet test src/tests --filter "FullyQualifiedName~Distributed"

# run the samples (build out/ first if needed: dotnet fsi build.fsx -- -- build)
dotnet fsi samples/distributed.fsx
dotnet fsi samples/distributed-tests.fsx
dotnet fsi samples/distributed-proc.fsx
```

## Open items / possible next steps

- **Commit the branch** (not done yet) and open a PR to `dev`.
- The `need`-from-detached-distributed-body soft CPU over-subscription is **documented, not
  fixed**. Revisit only if a real use case puts `need` inside a distributed body.
- Shake's time-based `newThrottle` was **deferred** — add if needed.
- **Out of scope here (next layer, in Qualhalla):** the real executor — Temporal dispatch
  (deterministic workflow id = at-most-once dedup), S3/Garage run-scoped result store, env
  provisioning. The samples fake these locally (in-memory/on-disk store, child processes).
- Consider whether the engine should offer a built-in dispatch `Resource` convenience
  (currently caller-supplied by decision — kept core minimal).
