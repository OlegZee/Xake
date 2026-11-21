# Topic 3: the two wait-for maps (candidate 1)

State at 8d12b23.

## 1. What each map guards

| | `WaitGraph` (ExecTypes.fs, in `RunCounters.Waits`) | `Verdicts.waits` (DependencyAnalysis.fs) |
|---|---|---|
| Who waits | a **task** (its target names) | a **verdict computation** (`Of t`, and `Fresh t`) |
| For what | a running task: in `need` (`Waits.Need`), or in the task's verdict, for a dependee still running (`Waits.Verdict`) | another target's memoised verdict still in progress (`dependee`) |
| An edge closing a cycle | `need`: always added, and it **cancels** the verdict waits it closes; task verdict: **refused** | **refused**; the dependee counts as changed |
| Lifetime | `RunCounters`: one per context (script run, or `Demand`) | one per run (`beginRun`) |
| Lock | one per `RunCounters` | one per `Verdicts` |
| Callers | ExecCore: `execNeed` (Need), `verdict` (Verdict) | `Verdicts.taskOf`/`Fresh` |
| Tests | ContentCutoffTests | VerdictsTests: two entries into one cycle, self-dependency |

Both keep `waiter -> awaited` by target name and check `reaches awaited waiter` under a lock;
the `reaches` walk is written twice.

## 2. Can one cycle run through both maps?

No. A cycle needs an edge back from a verdict computation to a task, and there is none:

- `Of t` never waits for a task. It reads `Scheduler.query` only, and the transitive rule takes a
  dependee only when its task has `NotStarted`. Its observations wait only for `RunFiles` claims,
  which complete in `finally` (C6).
- Tasks wait for tasks (`need`, the task verdict) and, through `Fresh`, for memoised verdicts.

So the two maps hold different kinds of node (tasks; verdict computations) and the graphs cannot
form one cycle. Two maps are correct; the cost is that the code says neither this nor why.

## 3. A finding: `Fresh` registers edges it does not need

`Fresh t` (a task's own verdict) awaits the dependees' `Of` through `dependee t`, which registers
the edge `t -> d` in `Verdicts.waits` under the name `t`. The memoised `Of t` registers under the
same name. Nothing ever waits for `Fresh t`, so its edges can never be part of a cycle. But
sharing the name, they can be **refused spuriously**: if `Of d` is waiting for `Of t` (it started
before t's task did), `Fresh t -> d` is refused as a cycle, and `d` counts as changed. The result
is an extra rebuild of `t`, never a wrong skip. It is rare (it needs mutual records and that
timing).

## 4. Options

- **a. Keep two maps, say so, and share the primitive (recommended).**
  1. One small internal type `WaitFor` (by name: `TryAdd waiter awaited` refuses a cycle,
     `Remove`, `Reaches`), used by both. The `reaches` walk is written once.
  2. `Fresh` awaits `Of d` directly and registers nothing (finding 3 gone; nothing waits for
     `Fresh`, so it cannot deadlock).
  3. Names that say what is waiting: `RunCounters.Waits` -> `TaskWaits`, `Verdicts.waits` ->
     `verdictWaits`; doc comments state section 2.
  No behaviour change except finding 3 (fewer spurious rebuilds).
- **b. One graph** whose nodes are `Task t` and `Verdict t`, with three edge kinds (need: hard;
  task verdict: cancellable; verdict: refusable). One explanation and one lock, but the hot path
  (the warm walk over thousands of verdicts) then shares a lock with every `need`, the lifetimes
  differ (per context vs per run), and the separation of section 2 is no longer visible in the
  types. More change for a property the system already has.
- **c. Leave the code, only document section 2.**

A related question (candidate 7, later): `WaitGraph` and `FastGate` live in `RunCounters`, a
public counters object inside the frozen `ExecContext`. They are run state, not counters.
