namespace Xake

[<AutoOpen>]
module XakeScript =

    /// Script execution options
    type ExecOptions = {
        /// Defines project root folder
        ProjectRoot : string
        /// Maximum number of rules processed simultaneously.
        Threads: int

        /// custom logger
        CustomLogger: ILogger

        /// Log file and verbosity level.
        FileLog: string
        FileLogLevel: Verbosity

        /// Console output verbosity level. Default is Warn
        ConLogLevel: Verbosity
        /// Targets to build: the default for `want`, which replaces it; command-line targets replace both
        Targets: string list

        /// Global script variables: the script's values (a `var "N" "v"` op appends here).
        /// A name defined twice in the script is an error at launch.
        Vars: (string * string) list

        /// The `-d` values of the command line, the last `-d` first.
        CliVars: (string * string) list

        /// The variable declarations (`var "N" [...]` rules, `varschema` fields) in script order,
        /// without values: they are evaluated at launch.
        VarDecls: VarDecl list

        /// Each definition made by a `var`/`varschema` op, in script order: the name and how it
        /// was defined (`var "N" "..."`, `var "N" [...]`, `varschema field F`), for the diagnostics.
        VarDefinedBy: (string * string) list

        /// Defines whether `run` should throw exception if script fails.
        /// Default is false which means to exit process with non-zero code.
        ThrowOnError: bool

        /// Ignores command line swithes
        IgnoreCommandLine: bool

        /// Disable logo message
        Nologo: bool

        /// Database file
        DbFileName: string

        /// Do not execute rules, just display run stats
        DryRun: bool

        /// Dump dependencies only
        DumpDeps: bool

        /// Dump dependencies only
        Progress: bool

        /// Reset database before build
        ResetDb: bool

        /// Skip build database; every target always rebuilds.
        NoPersist: bool

        /// Targets executed sequentially during XakeEngine.StopAsync.
        Teardown: string list

        /// Targets executed sequentially before the build (CLI) or at engine start; accumulated by the `setup` op.
        Setup: string list

        /// Deferred --help exit; set by CLI parser, acted on in Run.
        ShowHelp: bool

        /// Variable schema collected by the `vars` operation, used for --help output.
        VarSchema: (string * VarHelp) list

        /// Rule selection strategy among the rules that match a target.
        RuleMatch: RuleMatchMode
    } with
        static member Default = {
            ProjectRoot = System.IO.Directory.GetCurrentDirectory()
            Threads = System.Environment.ProcessorCount
            ConLogLevel = Normal

            CustomLogger = CustomLogger (fun _ -> false) ignore
            FileLog = "build.log"
            FileLogLevel = Chatty
            Targets = []
            ThrowOnError = false
            Vars = List<string*string>.Empty
            CliVars = []
            VarDecls = []
            VarDefinedBy = []
            IgnoreCommandLine = false
            Nologo = false
            DbFileName = ".xake"
            DryRun = false
            DumpDeps = false
            Progress = true
            ResetDb = false
            NoPersist = false
            Teardown = []
            Setup = []
            ShowHelp = false
            VarSchema = []
            RuleMatch = MostSpecific
        }
    end

    /// Creates the rule for specified file pattern.
    let ( ..?> ) fn fnRule = FileConditionRule (fn, fnRule)

    /// Creates a file rule that maps a pattern to a recipe.
    let ( ..> ) pattern actionBody = FileRule (pattern, actionBody)

    /// Creates a multi-file rule from a sequence of patterns and a shared recipe.
    let ( *..> ) (patterns: #seq<string>) actionBody =
        MultiFileRule (patterns |> List.ofSeq, actionBody)

    /// Creates phony action (check if I can unify the operator name)
    let (=>) name action = PhonyRule (name, action)

    open WorkerPool

    let private wrapRecipe (resource: Resource) (body: Recipe<ExecContext, unit>) : Recipe<ExecContext, unit> =
        Recipe (fun (s, ctx: ExecContext) -> async {
            do! Scheduler.withYieldedSlot ctx.Engine.Scheduler (Resource.acquire resource 1)
            try
                return! A.runAction body (s, { ctx with HeldResources = resource.ResourceName :: ctx.HeldResources })
            finally
                Resource.release resource 1
        })

    let rec private mapRecipe (f: Recipe<ExecContext, unit> -> Recipe<ExecContext, unit>) = function
        | FileRule (p, r)          -> FileRule (p, f r)
        | MultiFileRule (ps, r)    -> MultiFileRule (ps, f r)
        | PhonyRule (n, r)         -> PhonyRule (n, f r)
        | FileConditionRule (c, r) -> FileConditionRule (c, f r)
        | DelegatedRule (inner, exec) -> DelegatedRule (mapRecipe f inner, exec)

    /// Wraps a rule so its recipe runs under a resource lock (1 unit, CPU-slot yielding).
    /// The whole recipe holds the unit, so, as inside `withResource`, a `need` of a target
    /// that has a rule fails (it could deadlock); plain files are allowed, `allowNeed` lifts it.
    /// Compose with the existing rule operators, e.g.
    /// `"restore:*" => recipe { ... } |> requiring restoreLock`.
    let requiring (resource: Resource) (rule: ExecContext Rule) : ExecContext Rule =
        rule |> mapRecipe (wrapRecipe resource)

    /// Marks a rule as delegated, delegating its up-to-date check and execution to the
    /// caller-supplied executor. Script authors keep writing `need ["target"]`; whether the
    /// target runs locally, runs remotely, or is fetched from a shared store is the
    /// executor's decision. Compose with the existing rule operators, e.g.
    /// `"out/*.bin" ..> recipe { ... } |> delegated myExecutor`.
    let delegated (executor: DelegatedExecutor<ExecContext>) (rule: ExecContext Rule) : ExecContext Rule =
        DelegatedRule (rule, executor)

    /// Main type.
    type XakeScript = XakeScript of ExecOptions * Rules<ExecContext>
