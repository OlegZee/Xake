namespace Xake

open Xake.WorkerPool

/// Execution status of a single target: built, skipped, or was already a file.
type ExecStatus = | Succeed | Skipped | JustFile

/// Controls which source(s) a variable resolves from.
type LookupScope = ArgAndEnv | EnvOnly | ArgOnly

/// An element of a `var "N" [...]` source list: `Env`/`Default` are tried in list order, the
/// first that defines the name gives its value; `Secret`/`SecretHashed` define nothing, they
/// make the variable a secret (presence only, or a salted HMAC; with both, `SecretHashed`);
/// `Cli "ext"` defines nothing either: `-d ext=v` sets the variable instead of `-d N=v`.
type VarSource =
    | Env of string
    | Default of string
    | Cli of string
    | Secret
    | SecretHashed

/// Where the value of a variable came from (the banner shows it).
[<RequireQualifiedAccess>]
type VarOrigin =
    /// the engine's per-demand `?vars`
    | Request
    /// `-d NAME=value`, with the name given (the external name of the variable)
    | Cli of string
    /// an `ExecOptions.ScriptVars` entry or a constant `var "N" "v"`
    | Script
    /// an `Env` source of the declaration
    | Env of string
    /// a `Default` source of the declaration
    | Default

/// A variable declaration: a `var "N" [...]` rule or a `varschema` field, recorded by the op
/// without a value and evaluated at launch. A Var read finds its value through an equal
/// declaration: all fields but `DeclaredBy` equal.
type VarDecl = {
    /// The internal name: what `getVar`, `?vars`, the banner and the variable set use.
    Key: string
    /// The external name: what `-d` uses (`cliArg` of a field, `Cli` of a rule, else the key).
    CliName: string
    /// The `Env`/`Default` sources in list order (no markers; a rule with more than one `Cli`
    /// keeps them all here, for the launch error).
    Sources: VarSource list
    /// The typed default of a field (`withDefault`), the first `Default` of a rule.
    DefaultValue: obj option
    Scope: LookupScope
    /// `required` without a default.
    Required: bool
    Secret: SecretMode
    ValueType: System.Type
    /// `var "N" [...]` or `varschema field F`, for the diagnostics.
    DeclaredBy: string
}

/// A variable of the set built at launch: its value (if any), where it came from, its declaration.
type VarEntry = {
    Value: string option
    Origin: VarOrigin option
    Decl: VarDecl option
}

/// What a run did (docs/content-cutoff.md): logged at its end ("Run summary: ...") and handed to
/// `onSummary` / `XakeEngine.LastSummary`.
type RunSummary = {
    /// Targets whose recipe ran (delegated targets count when their executor was called).
    TargetsRun: int
    /// Targets found up to date.
    TargetsSkipped: int
    /// Targets rebuilt with the same content: their dependents did not rerun.
    CutOff: int
    /// Files hashed to compare content.
    FilesHashed: int
    /// Time spent hashing.
    HashTime: System.TimeSpan
    /// Tasks started in the worker pool (a target requested twice is one task).
    TasksScheduled: int
}

/// Receives the summary of every run (`onSummary` in a script).
type IRunSummarySink =
    abstract Report: RunSummary -> unit

/// Who waits for whom in a run (content cut-off): a target's verdict waits for a running dependee
/// unless that would close a cycle with the `need`s and verdict waits in progress; a `need` that
/// closes a cycle through a verdict wait cancels that wait (the dependee then counts as changed).
[<Sealed>]
type internal WaitGraph() =
    let lck = obj ()
    // waiter -> (awaited, the cancellation of a verdict wait)
    let edges = System.Collections.Generic.Dictionary<string, ResizeArray<string * System.Threading.Tasks.TaskCompletionSource<bool> option>>()
    let reaches (src: string) (dst: string) =
        let seen = System.Collections.Generic.HashSet<string>()
        let rec go k = k = dst || (seen.Add k && (match edges.TryGetValue k with | true, es -> es |> Seq.exists (fst >> go) | _ -> false))
        go src
    let add waiters awaited cancel =
        for w in waiters do
            match edges.TryGetValue w with
            | true, es -> es.Add((awaited, cancel))
            | _ -> edges.[w] <- ResizeArray [ (awaited, cancel) ]
    let remove waiters awaited =
        lock lck (fun () ->
            for w in waiters do
                match edges.TryGetValue w with
                | true, es ->
                    match Seq.tryFindIndex (fst >> (=) awaited) es with
                    | Some i -> es.RemoveAt i
                    | None -> ()
                    if es.Count = 0 then edges.Remove w |> ignore
                | _ -> ())
    /// Registers `waiters` (the names of a task's targets) waiting in `need` for `awaited`; verdict
    /// waits closed into a cycle are cancelled. Returns the unregistration.
    member _.Need (waiters: string list) (awaited: string list) =
        let cancels =
            lock lck (fun () ->
                for a in awaited do add waiters a None
                [ for KeyValue (w, es) in edges do
                    for (a, cancel) in es do
                        match cancel with
                        | Some c when reaches a w -> yield c
                        | _ -> () ])
        for c in cancels do c.TrySetResult true |> ignore
        fun () -> for a in awaited do remove waiters a
    /// Registers a verdict wait of `waiters` for `awaited`: None when it would close a cycle, else
    /// the cancellation (set when a later `need` closes one) and the unregistration.
    member _.Verdict (waiters: string list) (awaited: string) =
        lock lck (fun () ->
            if waiters |> List.exists (reaches awaited) then None
            else
                let cancel = System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
                add waiters awaited (Some cancel)
                Some (cancel, fun () -> remove waiters awaited))

/// The counters of one script run, or one `Demand` in engine mode, and the state the content
/// verdict shares across tasks. Incremented with `Interlocked` from any task.
[<Sealed; AllowNullLiteral>]
type RunCounters() =
    [<DefaultValue>] val mutable private targetsRun: int
    [<DefaultValue>] val mutable private targetsSkipped: int
    [<DefaultValue>] val mutable private cutOff: int
    [<DefaultValue>] val mutable private filesHashed: int
    [<DefaultValue>] val mutable private hashTicks: int64
    [<DefaultValue>] val mutable private tasksScheduled: int
    member internal c.TargetRun () = System.Threading.Interlocked.Increment &c.targetsRun |> ignore
    member internal c.TargetSkipped () = System.Threading.Interlocked.Increment &c.targetsSkipped |> ignore
    member internal c.TargetCutOff () = System.Threading.Interlocked.Increment &c.cutOff |> ignore
    member internal c.FileHashed (elapsed: System.TimeSpan) =
        System.Threading.Interlocked.Increment &c.filesHashed |> ignore
        System.Threading.Interlocked.Add (&c.hashTicks, elapsed.Ticks) |> ignore
    member internal c.TaskScheduled () = System.Threading.Interlocked.Increment &c.tasksScheduled |> ignore
    /// The fast path's blocking database lookups: at most as many at once as there are cores.
    member val internal FastGate = new System.Threading.SemaphoreSlim(max 2 System.Environment.ProcessorCount) with get
    member val internal Waits = WaitGraph() with get
    /// The counters so far.
    member c.Summary : RunSummary =
        { TargetsRun = System.Threading.Volatile.Read &c.targetsRun
          TargetsSkipped = System.Threading.Volatile.Read &c.targetsSkipped
          CutOff = System.Threading.Volatile.Read &c.cutOff
          FilesHashed = System.Threading.Volatile.Read &c.filesHashed
          HashTime = System.TimeSpan.FromTicks (System.Threading.Interlocked.Read &c.hashTicks)
          TasksScheduled = System.Threading.Volatile.Read &c.tasksScheduled }

module RunSummary =
    /// The log line of a summary.
    let format (s: RunSummary) =
        sprintf "Run summary: targets run %d, skipped %d, tasks %d, cut-off %d, files hashed %d (%d ms)"
            s.TargetsRun s.TargetsSkipped s.TasksScheduled s.CutOff s.FilesHashed (int s.HashTime.TotalMilliseconds)

/// Engine-level options: immutable after construction.
type EngineOptions = {
    ProjectRoot: string
    Threads: int
    Logger: ILogger
    Teardown: string list
    Rules: Rules<ExecContext>
    /// How to select among the rules that match a target.
    RuleMatch: RuleMatchMode
    /// The variable set built at launch: every key with its value, origin and declaration.
    /// Var reads, the banner and the per-demand checks look here.
    Variables: Map<string, VarEntry>
    /// Content cut-off (docs/content-cutoff.md); false (`--content-cutoff=off`): the 4.0 decisions.
    ContentCutoff: bool
    /// The order in which ready tasks start (docs/implnotes.md, "Critical path").
    Schedule: Schedule
    /// Receives the summary of every run; None: only the log line.
    SummarySink: IRunSummarySink option
} with
    static member Default = {
        ProjectRoot = System.IO.Directory.GetCurrentDirectory()
        Threads = System.Environment.ProcessorCount
        Logger = CustomLogger (fun _ -> false) ignore
        Teardown = []
        Rules = Rules []
        RuleMatch = MostSpecific
        Variables = Map.empty
        ContentCutoff = true
        Schedule = CriticalPath
        SummarySink = None
    }

/// Engine-level immutable state shared across all tasks.
and EngineState = internal {
    Options: EngineOptions
    Db: Storage.Db
    /// Hashes file contents, one computation per (file, stat); shared across runs.
    Hasher: FileProbe.Hasher
    Scheduler: Scheduler<ExecStatus>
    RootLogger: ILogger
}

/// Script execution context
and ExecContext = {
    Engine: EngineState
    // Build-level
    Vars: (string * string) list
    Progress: Agent<Progress.ProgressReport>
    /// The verdict of a task's targets, after its static `needs` (waits for running dependees).
    NeedRebuild: Target list -> Async<bool>
    /// The run's verdict without building or waiting: true when the target is up to date.
    UpToDate: Target -> Async<bool>
    ShowProgress: bool
    // Task-level
    Targets: Target list
    RuleMatches: Map<string,string>
    Ordinal: int
    Logger: ILogger
    /// Names of the resources the running recipe holds (innermost first), set by
    /// `withResource` and `requiring` for their body. A `need` of a target that has a rule
    /// fails while it is not empty (hold-and-wait deadlock); `allowNeed` clears it.
    HeldResources: string list
    /// The files named in this run, keyed by the path upper-cased (invariant): the first spelling
    /// (the `File`) and whether it was a target. One file under two spellings is an error for two targets, a
    /// warning otherwise (5.0). Created per run, with the run's memo.
    Spellings: System.Collections.Concurrent.ConcurrentDictionary<string, File * bool>
    /// The files observed and recorded in this run (docs/features/idle-perf, L4). Created per run,
    /// with the run's memo.
    RunFiles: FileTracker.RunFiles
    /// The epoch (run number) of the run this context belongs to, taken at `Scheduler.newRun`:
    /// a result built in the run gets it (A2).
    RunEpoch: int
    /// The counters of the script run (or `Demand`): `RunSummary`, and the waits of the verdict.
    Counters: RunCounters
} with
    /// Shortcut to engine options.
    member ctx.Options = ctx.Engine.Options
    /// Shortcut to the build database.
    member internal ctx.Db = ctx.Engine.Db
    /// Where this context's file observations run: its database, its run, whether it hashes.
    member internal ctx.Observe =
        { FileTracker.Context.Db = ctx.Engine.Db; FileTracker.Context.Hasher = ctx.Engine.Hasher
          FileTracker.Context.Run = ctx.RunFiles; FileTracker.Context.Hashing = ctx.Engine.Options.ContentCutoff
          FileTracker.Context.OnHashed = match ctx.Counters with null -> ignore | c -> c.FileHashed }

/// Functions over the execution context for code outside this assembly. A record copy
/// (`{ ctx with ... }`) compiled in another assembly calls the full constructor and breaks
/// when a field is added; these keep the copy inside Xake.dll.
module ExecContext =
    /// The context with its logger replaced, e.g. by one that also collects lines.
    let withLogger (logger: ILogger) (ctx: ExecContext) = { ctx with Logger = logger }

/// Internal utility functions for reading environment and script variables.
module internal Util =

    let private nullableToOption = function | null -> None | s -> Some s
    /// Reads an environment variable, returning None if unset.
    let getEnvVar = System.Environment.GetEnvironmentVariable >> nullableToOption

    let private valueByName variableName = function |name,value when name = variableName -> Some value | _ -> None
    /// Looks up a script variable by name from a key-value list.
    let getVar (vars: (string * string) list) name = vars |> List.tryPick (valueByName name)

/// Help metadata for --help output.
type VarHelp = {
    TypeName: string
    EnvVarName: string option
    DefaultStr: string option
    IsRequired: bool
    Description: string option
    Scope: LookupScope
    /// A secret variable: --help shows no default, -d is refused (env-only) or warned about.
    IsSecret: bool
}

