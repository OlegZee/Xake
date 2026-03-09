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
        /// Overrides "want", i.e. target list
        Targets: string list

        /// Global script variables
        Vars: (string * string) list

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
            IgnoreCommandLine = false
            Nologo = false
            DbFileName = ".xake"
            DryRun = false
            DumpDeps = false
            Progress = true
            ResetDb = false
            NoPersist = false
            Teardown = []
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

    /// Main type.
    type XakeScript = XakeScript of ExecOptions * Rules<ExecContext>
