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
    /// an `ExecOptions.Vars` entry or a constant `var "N" "v"`
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

module internal VarDecl =

    /// How the diagnostics name a definition of a variable (spec E-2, E-7).
    let constantLabel (name: string) = sprintf "var \"%s\" \"...\"" name
    let ruleLabel (name: string) = sprintf "var \"%s\" [...]" name
    let fieldLabel (field: string) = sprintf "varschema field %s" field

    /// The declaration of a `var "N" [sources]` rule: a string in both scopes, never required;
    /// the secret markers set its mode (`SecretHashed` wins), `Cli` its external name; markers
    /// are not kept as sources.
    let ofRule (name: string) (sources: VarSource list) =
        let cliNames = sources |> List.choose (function Cli ext -> Some ext | _ -> None)
        let sources' =
            sources |> List.filter (function
                | Env _ | Default _ -> true
                | Cli _ -> cliNames.Length > 1      // kept for the launch error (two command-line names)
                | Secret | SecretHashed -> false)
        { Key = name
          CliName = cliNames |> List.tryHead |> Option.defaultValue name
          Sources = sources'
          DefaultValue = sources' |> List.tryPick (function Default v -> Some (box v) | _ -> None)
          Scope = ArgAndEnv
          Required = false
          Secret =
            if List.contains SecretHashed sources then SecretHash
            elif List.contains Secret sources then SecretPresence
            else NotSecret
          ValueType = typeof<string>
          DeclaredBy = ruleLabel name }

    /// The names of its `Env` sources, in list order.
    let envNames (decl: VarDecl) = decl.Sources |> List.choose (function Env e -> Some e | _ -> None)

    let isSecret (decl: VarDecl) = decl.Secret <> NotSecret

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
} with
    static member Default = {
        ProjectRoot = System.IO.Directory.GetCurrentDirectory()
        Threads = System.Environment.ProcessorCount
        Logger = CustomLogger (fun _ -> false) ignore
        Teardown = []
        Rules = Rules []
        RuleMatch = MostSpecific
        Variables = Map.empty
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
    /// Names of the resources the running recipe holds (innermost first), set by
    /// `withResource` and `requiring` for their body. A `need` of a target that has a rule
    /// fails while it is not empty (hold-and-wait deadlock); `allowNeed` clears it.
    HeldResources: string list
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

