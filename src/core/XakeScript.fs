namespace Xake

[<AutoOpen>]
module XakeScript =

    /// The value of a path root (`pathRoot`, `ExecOptions.PathRoots`).
    type RootValue =
        /// An absolute path, or a path relative to the project root (recorded as written, so it moves with the project).
        | RootPath of string
        /// The launch-time value of a variable; unset or empty leaves the root undeclared for the run.
        | RootVar of string

    /// A path root whose value is the launch-time value of variable `name`.
    let fromVar name = RootVar name

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

        /// The script's values: the entries given to `xake`/`xakeArgs` and every `var "N" "v"` op (appended); `CliVars` holds `-d`.
        /// A name defined twice in the script is an error at launch.
        ScriptVars: (string * string) list

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

        /// No declaration reads the environment at launch (neither `XAKE__<NAME>` nor an explicit
        /// `Env`/`envVar` source): for tests and for a program that embeds Xake. `getEnv` is not
        /// affected. No command-line flag sets it.
        IgnoreEnvironment: bool

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

        /// Declared path roots: database paths under a root are stored relative to it (docs/database.md).
        /// `$(ProjectRoot)` is always a root and is not listed here.
        PathRoots: (string * RootValue) list
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
            ScriptVars = []
            CliVars = []
            VarDecls = []
            VarDefinedBy = []
            IgnoreCommandLine = false
            IgnoreEnvironment = false
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
            PathRoots = []
        }
    end

    /// The right-hand side of the file-rule operators: a recipe, or a recipe with a header
    /// (`recipe { needs ..; .. }`). An implementation detail of `..>`, `*..>` and `..?>`.
    type RuleBody = RuleBody with
        static member ($) (RuleBody, body: Recipe<ExecContext, unit>) = RuleHeader.Empty, body
        static member ($) (RuleBody, Headed (header, body): Headed<ExecContext>) = header, body

    /// Creates the rule for the files a condition accepts. The recipe may have a header without
    /// `also` and without groups (a condition has no pattern).
    let inline ( ..?> ) (condition: string -> bool) body : ExecContext Rule =
        let header, recipe = RuleBody $ body
        FileConditionRule (condition, header, recipe)

    /// Creates a file rule that maps a pattern to a recipe, with or without a header.
    let inline ( ..> ) (pattern: string) body : ExecContext Rule =
        let header, recipe = RuleBody $ body
        FileRule ([ pattern ], header, recipe)

    /// Creates a file rule with several outputs (the first is the main one) from a recipe, with or
    /// without a header.
    let inline ( *..> ) (patterns: #seq<string>) body : ExecContext Rule =
        let header, recipe = RuleBody $ body
        FileRule (List.ofSeq patterns, header, recipe)

    /// Creates phony action (check if I can unify the operator name)
    let (=>) name action =
        PhonyRule (name, action)

    open WorkerPool

    let private wrapRecipe (resource: Resource) (body: Recipe<ExecContext, unit>) : Recipe<ExecContext, unit> =
        Recipe (fun (s, ctx: ExecContext) -> async {
            do! Scheduler.withYieldedSlot ctx.Engine.Scheduler (Resource.acquire resource 1)
            try
                return! A.runAction body (s, { ctx with HeldResources = resource.ResourceName :: ctx.HeldResources })
            finally
                Resource.release resource 1
        })

    /// Locks a rule (1 unit, CPU-slot yielding). For a file rule it is `requires resource` in the
    /// header: the engine builds the static `needs`, then takes the lock around the body. A phony
    /// rule has no header: its whole recipe holds the unit. While the unit is held, as inside
    /// `withResource`, a `need` of a target that has a rule fails (it could deadlock); plain files
    /// are allowed, `allowNeed` lifts it. Compose with the rule operators, e.g.
    /// `"restore:*" => recipe { ... } |> requiring restoreLock`.
    let rec requiring (resource: Resource) (rule: ExecContext Rule) : ExecContext Rule =
        let lock (header: RuleHeader) = { header with Requires = header.Requires @ [ resource, 1 ] }
        match rule with
        | FileRule (ps, h, r)          -> FileRule (ps, lock h, r)
        | FileConditionRule (c, h, r)  -> FileConditionRule (c, lock h, r)
        | PhonyRule (n, r)             -> PhonyRule (n, wrapRecipe resource r)
        | DelegatedRule (inner, exec)  -> DelegatedRule (requiring resource inner, exec)

    /// Marks a rule as delegated, delegating its up-to-date check and execution to the
    /// caller-supplied executor. Script authors keep writing `need ["target"]`; whether the
    /// target runs locally, runs remotely, or is fetched from a shared store is the
    /// executor's decision. Compose with the existing rule operators, e.g.
    /// `"out/*.bin" ..> recipe { ... } |> delegated myExecutor`.
    let delegated (executor: DelegatedExecutor<ExecContext>) (rule: ExecContext Rule) : ExecContext Rule =
        DelegatedRule (rule, executor)

    /// Main type.
    type XakeScript = XakeScript of ExecOptions * Rules<ExecContext>
