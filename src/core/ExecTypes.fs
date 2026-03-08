namespace Xake

open Xake.WorkerPool

type ExecStatus = | Succeed | Skipped | JustFile

/// Engine-level options: immutable after construction.
type EngineOptions = {
    ProjectRoot: string
    Threads: int
    Logger: ILogger
    Teardown: string list
    Rules: Rules<ExecContext>
} with
    static member Default = {
        ProjectRoot = System.IO.Directory.GetCurrentDirectory()
        Threads = System.Environment.ProcessorCount
        Logger = CustomLogger (fun _ -> false) ignore
        Teardown = []
        Rules = Rules []
    }

/// Engine-level immutable state shared across all tasks.
and EngineState = {
    Options: EngineOptions
    Db: Agent<Storage.DatabaseApi>
    Scheduler: Scheduler<ExecStatus>
    RootLogger: ILogger
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
} with
    member ctx.Options = ctx.Engine.Options
    member ctx.Db = ctx.Engine.Db

module internal Util =

    let private nullableToOption = function | null -> None | s -> Some s
    let getEnvVar = System.Environment.GetEnvironmentVariable >> nullableToOption

    let private valueByName variableName = function |name,value when name = variableName -> Some value | _ -> None
    let getVar (vars: (string * string) list) name = vars |> List.tryPick (valueByName name)
