# Topic 7: engine state in `ExecContext` (candidate 7)

State at e620df9.

## 1. The problem

`ExecContext` is the recipe's context: a public record that every recipe, library and Xake.Dotnet
receives. A record is constructed and copied by its full constructor, so code compiled against
one Xake breaks at run time when a field is added: the record is frozen within a major
(`RecordShapeTests`). Its 15 fields mix three kinds:

| Kind | Fields | Who reads them outside the engine |
|---|---|---|
| What a recipe needs | `Targets`, `RuleMatches`, `Vars`, `Logger`, `Ordinal`, `Engine` (opaque, `Options`) | Xake.Dotnet: `Targets`, `Vars`, `Logger`, `Engine` (as a per-engine cache key), `ExecContext.withLogger` |
| Task state the engine sets | `HeldResources` | none (set by `withResource`, `requiring`, `allowNeed`) |
| Engine/run internals | `Progress`, `ShowProgress`, `NeedRebuild`, `UpToDate`, `Spellings`, `RunFiles`, `RunEpoch`, `Counters` (with `TaskWaits`, `FastGate`) | none |

Every engine change that needs per-run state (5.0 added `Spellings`, `RunEpoch`, `UpToDate`,
`Counters`, `RunFiles`) changes the frozen shape; within 5.x it could not. `EngineState` already
solved this for the engine: its fields are internal (`EngineState = internal { ... }`).

## 2. Options

- **a. One opaque field for the run (recommended).** The internals move into a record with an
  internal representation, as `EngineState`:

  ```fsharp
  and RunState = internal {
      Progress: Agent<Progress.ProgressReport>; ShowProgress: bool
      NeedRebuild: Target list -> Async<bool>; UpToDate: Target -> Async<bool>
      Spellings: ...; RunFiles: FileTracker.RunFiles; RunEpoch: int; Counters: RunCounters }

  and ExecContext = {
      Engine: EngineState; Run: RunState
      Vars; Targets; RuleMatches; Ordinal; Logger; HeldResources }
  ```

  The public shape is 8 fields and stays frozen within 5.x; per-run state changes inside
  `RunState` without touching it. Xake.Dotnet keeps compiling (it uses `Targets`, `Vars`,
  `Logger`, `Engine`, `withLogger`). `beginRun` replaces `Run`; engine code reads `ctx.Run.X`.
- **b. The whole `ExecContext` opaque** (`ExecContext = internal { ... }`), with public read
  functions (`ExecContext.targets ctx`, ...). Nothing to freeze any more, but a record cannot have
  a public member named as its internal field, so `ctx.Targets` becomes a function call or a
  property under another name: every recipe and Xake.Dotnet change.
- **c. Leave it**, documenting each field as internal.

## 3. Related, in the same step if a is chosen

- `RunCounters` (public) carries `TaskWaits` and `FastGate`, which are run state, not counters.
  With a, `Counters` is reachable only inside the engine; `RunSummary` stays public through
  `XakeEngine.LastSummary` and `onSummary`.
- `HeldResources` stays in `ExecContext`: it is per task (each `need` starts a task with none).
- `RecordShapeTests`: `ExecContext` becomes `[ Engine; Run; Vars; Targets; RuleMatches; Ordinal;
  Logger; HeldResources ]`; the comment says per-run state goes into `RunState`.
- Release notes: the fields that moved.

No behaviour change.
