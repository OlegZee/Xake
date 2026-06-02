# Session notes — delegated Xake (EP-0018)

A handoff for continuing tomorrow from a clean slate. Full design is in
[`docs/delegated.md`](delegated.md); this file captures the **non-obvious** knowledge,
current state, and how to pick back up.

## What this is

A fork of Xake (branch `feature/delegated-xake`) extended so an external system
(Qualhalla — Temporal + S3 test/build runner) can express *delegated* builds where
**distribution is a property of a rule, not a new verb**. Three opt-in core mechanisms were
added; all policy (Temporal/S3/env) stays in the caller. Source of truth for intent:
- `…/qualhalla/.../EP-0018/xake-core-extension-prompt.md` (the task)
- `…/qualhalla/.../adr/0010-delegated-xake-primitives.md` (the decision)
- Local plan: `~/.claude/plans/follow-users-olegz-projects-work-qualhal-cheeky-dahl.md`

## The three mechanisms (what shipped)

1. **`Resource`** (Shake-style) — `Resource.newResource name n` / `newUnbounded name`;
   `withResource res n recipe` in recipe code. Bounded = FIFO agent (no starvation).
2. **`runDetached recipe`** — runs a body without holding a CPU slot (for I/O-bound waits).
3. **`delegated executor rule`** — wraps any rule; the caller-supplied
   `DelegatedExecutor<'ctx> = 'ctx -> Target list -> (unit -> Async<BuildResult>) -> Async<BuildResult>`
   owns the up-to-date check + execution (store lookup / in-flight dedup / run+publish).

## Key insights / gotchas (the stuff that cost time)

- **Delegated rules are NEVER bounded by the local CPU pool.** This is the final, correct
  design. `ExecCore.runDelegated` runs the *entire* task — executor **and** body — detached
  (`withYieldedSlot` around the whole executor call). Concurrency is governed only by the
  dispatch `Resource` the executor applies (or is unbounded). Therefore **`runDetached`
  inside a delegated body is redundant.** (An earlier version reacquired a CPU slot for the
  body via a `withReacquiredSlot` helper — that was wrong and has been removed.)

- **`withYieldedSlot` assumes the caller holds exactly one CPU slot** (it does `Release()`
  first, then reacquires). Calling it when you hold **no** slot releases a *phantom* permit
  and corrupts the scheduler semaphore (silently disabling the `Threads` limit). This bit us:
  `Scheduler.withYieldedSlot` must only be used from slot-holding **recipe** code.
  Inside a **DelegatedExecutor** (already detached) use `Resource.acquire` or
  `Resource.withAcquired` instead — they do not touch the CPU slot.

- **"Where the I/O lives" decides whether you need `runDetached`:**
  - I/O in the rule **body** + *non-delegated* rule → needs `runDetached` (body holds a slot).
  - I/O in the rule **body** + *delegated* rule → already detached by engine, redundant.
  - I/O in the **executor** → always detached, never need `runDetached`.

- **A delegated body should be leaf work.** Declare `need` dependencies in the *enclosing*
  rule. A `need` from inside a detached delegated body still works but can briefly exceed
  the `Threads` count during the wait (same phantom-release mechanism).

- **Two independent dedup layers:** worker pool dedups by `Target.FullName` (local); the
  executor dedups by *identity key* (cross-target / cross-process). Orthogonal.

- The delegated branch **skips `ctx.NeedRebuild`** (the executor is the rebuilder) but
  **still `Store`s** the `BuildResult` so downstream `ArtifactDep`s resolve as built.

- **No `maxThreads` CE operation exists.** Set CPU slots via the options record
  (`RulesBuilder { ExecOptions.Default with Threads = N }`) or the `-t N` CLI flag.

- **F# gotcha that wasted time:** `do! need names; printfn …` on one line mis-parses as
  `do! (need names; printfn …)` — `need` is built but never run. Keep CE statements on
  separate lines.

## Current state

- ✅ Builds clean (net462 + netstandard2.0). Full test suite: **216 passed, 1 skipped, 0 failed**.
- ✅ 7 tests in `src/tests/DelegatedTests.fs` (resource cap / unbounded / detached-scale /
  **delegated-not-CPU-bounded** / hook miss-dedup-hit).
- ✅ 4 runnable samples (`samples/delegated.fsx`, `delegated-tests.fsx`,
  `delegated-proc.fsx` + `worker.fsx`). Verified: `delegated-tests.fsx` shows `peak=20`
  with `THREADS=4` (proves CPU-pool decoupling); `delegated-proc.fsx` shows separate worker
  PIDs + phase-2 cache hit with no workers.
- ✅ Docs: `docs/delegated.md` (design + samples + API + caveats).
- ⚠️ **Nothing is committed.** All work is uncommitted on branch `feature/delegated-xake`.

### Files touched
- `src/core/Types.fs` — `DelegatedExecutor<'ctx>`, `DelegatedRule` DU case.
- `src/core/Resource.fs` — **new**: `Resource`, `newResource`/`newUnbounded`, FIFO agent,
  `acquire`/`release`/`withAcquired`. Registered in `Xake.fsproj`.
- `src/core/ExecCore.fs` — `locateRule`/`getAction`/`isPhonyRule` recurse through
  `DelegatedRule`; `execOne` delegated branch runs the whole task detached.
- `src/core/XakeScript.fs` — `delegated` combinator.
- `src/core/ScriptFuncs.fs` — public `runDetached`, `withResource`.
- `src/core/WorkerPool.fs` — (a `withReacquiredSlot` helper was added then **removed**; net
  no change beyond what's needed — `withYieldedSlot` is the only slot helper).
- `src/tests/DelegatedTests.fs` — **new**; registered in `tests.fsproj`.
- `docs/delegated.md`, `docs/session.md` — **new**.

## How to pick back up

```bash
# build + full test suite
dotnet fsi build.fsx -- -- build test        # self-hosting build (rebuilds out/)
dotnet test src/tests                          # or just the tests
dotnet test src/tests --filter "FullyQualifiedName~Delegated"

# run the samples (build out/ first if needed: dotnet fsi build.fsx -- -- build)
dotnet fsi samples/delegated.fsx
dotnet fsi samples/delegated-tests.fsx
dotnet fsi samples/delegated-proc.fsx
```

## Open items / possible next steps

- **Commit the branch** (not done yet) and open a PR to `dev`.
- The `need`-from-detached-delegated-body soft CPU over-subscription is **documented, not
  fixed**. Revisit only if a real use case puts `need` inside a delegated body.
- Shake's time-based `newThrottle` was **deferred** — add if needed.
- **Out of scope here (next layer, in Qualhalla):** the real executor — Temporal dispatch
  (deterministic workflow id = at-most-once dedup), S3/Garage run-scoped result store, env
  provisioning. The samples fake these locally (in-memory/on-disk store, child processes).
- Consider whether the engine should offer a built-in dispatch `Resource` convenience
  (currently caller-supplied by decision — kept core minimal).
