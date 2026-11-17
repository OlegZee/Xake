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

/// <summary>
/// What a run did: logged at its end ("Run summary: ...") and handed to `onSummary` /
/// `XakeEngine.LastSummary`. In Xake 5.0 step 1 `CutOff`, `FilesHashed` and `HashTime` are 0
/// (content cut-off fills them).
/// </summary>
type RunSummary = {
    /// Targets whose recipe ran (delegated targets count here when their executor was called).
    TargetsRun: int
    /// Targets found up to date.
    TargetsSkipped: int
    /// Targets rebuilt with the same content, whose dependents did not rerun.
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

/// The counters of one run (one per script run, one per `Demand` in engine mode). Incremented
/// with `Interlocked` from any task; `Summary` reads them.
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
    // per-run state of the decision (content cut-off): the targets finished in this run, and the
    // memo of decisions made without building, dropped for a target when it finishes
    member val internal Started = System.Collections.Concurrent.ConcurrentDictionary<Target, bool>() with get
    member val internal Finished = System.Collections.Concurrent.ConcurrentDictionary<Target, bool>() with get
    member val internal Decisions = System.Collections.Concurrent.ConcurrentDictionary<Target, Lazy<string option>>() with get
    member internal c.MarkFinished (t: Target) =
        c.Finished.[t] <- true
        c.Decisions.TryRemove t |> ignore
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
    /// Receives the summary of every run or demand (`onSummary`); None: only the log line.
    SummarySink: IRunSummarySink option
    /// Content cut-off on (the default) or off (`--content-cutoff=off`: decide as Xake 4.0).
    ContentCutoff: bool
} with
    static member Default = {
        ProjectRoot = System.IO.Directory.GetCurrentDirectory()
        Threads = System.Environment.ProcessorCount
        Logger = CustomLogger (fun _ -> false) ignore
        Teardown = []
        Rules = Rules []
        RuleMatch = MostSpecific
        Variables = Map.empty
        SummarySink = None
        ContentCutoff = true
    }

/// Engine-level immutable state shared across all tasks.
and EngineState = {
    Options: EngineOptions
    Db: Agent<Storage.DatabaseApi>
    Scheduler: Scheduler<ExecStatus>
    RootLogger: ILogger
    /// The content histories of files (content cut-off, docs/content-cutoff.md).
    Content: ContentStore
}

/// Script execution context
and ExecContext = {
    Engine: EngineState
    // Build-level
    Vars: (string * string) list
    Progress: Agent<Progress.ProgressReport>
    NeedRebuild: Target list -> bool
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
    /// The counters of the current run (one per script run or `Demand`).
    Counters: RunCounters
} with
    /// Shortcut to engine options.
    member ctx.Options = ctx.Engine.Options
    /// Shortcut to the build database agent.
    member ctx.Db = ctx.Engine.Db

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

