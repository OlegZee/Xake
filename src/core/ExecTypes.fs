namespace Xake

open Xake.WorkerPool

/// Execution status of a single target: built, skipped, or was already a file.
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
    /// Shortcut to engine options.
    member ctx.Options = ctx.Engine.Options
    /// Shortcut to the build database agent.
    member ctx.Db = ctx.Engine.Db

/// Internal utility functions for reading environment and script variables.
module internal Util =

    let private nullableToOption = function | null -> None | s -> Some s
    /// Reads an environment variable, returning None if unset.
    let getEnvVar = System.Environment.GetEnvironmentVariable >> nullableToOption

    let private valueByName variableName = function |name,value when name = variableName -> Some value | _ -> None
    /// Looks up a script variable by name from a key-value list.
    let getVar (vars: (string * string) list) name = vars |> List.tryPick (valueByName name)

/// Controls which source(s) a variable resolves from.
type LookupScope = ArgAndEnv | EnvOnly | ArgOnly

/// Help metadata for --help output.
type VarHelp = {
    TypeName: string
    EnvVarName: string option
    DefaultStr: string option
    IsRequired: bool
    Description: string option
    Scope: LookupScope
}

