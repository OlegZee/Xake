namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The one place that starts a compiler over a resolved compilation, for `Csc.run` and
/// `Fsc.run` alike. Each of them turns its record and its own options into a `RunPlan` -- data,
/// with every difference between the two compilers spelled out as a field -- and hands it to
/// `run`, which applies the same gate to both: generated files written back, inputs
/// `needFiles`d, every hash verified before the compiler starts.
module internal CompilerRunner =

    /// How the compiler process is started.
    type Launch =
        /// `dotnet <dll> ...`: a managed compiler (the SDK's `csc.dll`/`fsc.dll`, a toolset
        /// package's `csc.dll`)
        | Dotnet of dll: string
        /// the file itself: a native launcher, a .NET Framework `csc.exe`/`fsc.exe`, an override
        | Native of exe: string

    /// How a line of the response file is written; the two compilers read it differently.
    type ResponseQuoting =
        /// Windows argv quoting of the whole line (`Tool.quoteArgument`): csc unquotes it, and a
        /// path with a space has to be quoted or csc splits it (CS2001)
        | Argv
        /// The argument as is: fsc takes each line as one argument and does not unquote a
        /// switch (`"--reference:a b/l.dll"` is read as a file, FS2020; `--reference:"a b/l.dll"`
        /// keeps the quotes, FS0084)
        | Verbatim

    type RunPlan = {
        /// The compilation's name, for the log and the failure messages
        Name: string
        /// The compiler's tool name as recorded (`"csc"`, `"fsc"`), for the trace
        Tool: string
        /// The prefix of the compiler's output lines in the log (`"[csc]"`, `"[fsc]"`)
        LogPrefix: string
        /// The compiler the compilation names: `needFiles`d (a tracked dependency) and, when
        /// `RequireCompiler`, required to exist
        CompilerPath: string
        CompilerVersion: string
        /// Whether a missing `CompilerPath` is an error (false when an override path is
        /// what actually runs)
        RequireCompiler: bool
        Launch: Launch
        /// Arguments that stay on the command line, before the response file (csc's
        /// `/noconfig`, which csc ignores inside one); `[]` for fsc
        Leading: string list
        /// Everything else, written to the response file one argument per line
        ResponseArgs: string list
        /// How each of `ResponseArgs` is written
        ResponseQuoting: ResponseQuoting
        /// Client-side switches ahead of everything (csc's `/shared`, `/keepalive`); never in
        /// the response file; `[]` for fsc, which has no server
        ClientSwitches: string list
        /// Traced at Debug when present (why the compiler server is not used)
        ClientSwitchesNote: string option
        /// Files the command line reads, from the dialect (`CscArgs.inputs`/`FscArgs.inputs`)
        Inputs: string list
        /// Files the command line writes, from the dialect; their directories are created
        Outputs: string list
        /// (path, expected SHA-256) of every hashed file; an empty hash is not checked
        Hashed: (string * string) list
        /// (path, content) written back when missing or different
        Generated: (string * string) list
        /// (resx, .resources) pairs; the output is compiled again on every run, written when it changes
        Resources: (string * string) list
        /// (path, content) of the `.runtimeconfig.json` the rule declares; written after a
        /// successful compile when missing or different
        RuntimeConfig: (string * string) option
        /// Environment variables of the compiler process
        Environment: (string * string) list
        /// The compiler's working directory
        Directory: string
        FailOnError: bool
        /// Classifies a line of compiler output by log level
        Diagnostics: string -> Level
        /// (build variable, its 3.15 environment variable, no longer read) holding the limit on
        /// concurrent launches of this compiler: `CSC_JOBS`/`XAKE_CSC_JOBS`, `FSC_JOBS`/`XAKE_FSC_JOBS`
        Jobs: string * string
        /// A resource the script supplies and the units one launch takes (`RunOptions.Throttle`,
        /// `FscRunOptions.Throttle`): when set, the launch acquires it instead of the budget of
        /// `Jobs`, which is then not read
        Throttle: (Resource * int) option
    }

    /// The launch for a compiler file: a `.dll` through `dotnet`, anything else directly.
    let launchOf (path: string) = if Impl.endsWith ".dll" path then Dotnet path else Native path

    /// The runtimeconfig content for a compilation's framework; fails when it is not `netN.0`.
    let runtimeConfigContent (name: string) (framework: string) =
        match Impl.runtimeConfigContent framework with
        | Some content -> content
        | None -> failwithf "'%s': a runtimeconfig is declared, but %s applications do not use one" name (if Impl.isEmpty framework then "this framework's" else framework)

    /// A response file line for `arg`.
    let responseLine (quoting: ResponseQuoting) (arg: string) =
        match quoting with
        | Argv -> Tool.quoteArgument arg
        | Verbatim -> arg

    /// The `dotnet` host that runs a managed compiler: the full path under
    /// `DotNetFwk.dotnetRoot ()` when it is there, so that on Windows `shell` starts it directly
    /// (a command without `.exe` goes through `cmd.exe /c`), else `dotnet` from the PATH.
    let dotnetHost () =
        match DotNetFwk.dotnetRoot () with
        | Some root ->
            let host = Path.Combine (root, if Env.isWindows then "dotnet.exe" else "dotnet")
            if File.Exists host then host else "dotnet"
        | None -> "dotnet"

    /// The program and its arguments (each one argument, quoted here) that start a compiler:
    /// `shell` joins its arguments with a space and quotes nothing, and the SDK's compilers
    /// live under `C:\Program Files\dotnet` by default.
    let commandLine (launch: Launch) (args: string list) =
        match launch with
        | Dotnet dll -> dotnetHost (), (dll :: args) |> List.map Tool.quoteArgument
        | Native exe -> exe, args |> List.map Tool.quoteArgument

    let private sha256 (path: string) = if File.Exists path then Hash.sha256 path else ""

    /// Parses a jobs value: a positive integer is the limit; empty or `0` is unlimited
    /// (`Ok None`); anything else is `Error`.
    let parseJobs (value: string) : Result<int option, string> =
        match value.Trim() with
        | "" | "0" -> Ok None
        | v ->
            match System.Int32.TryParse v with
            | true, n when n > 0 -> Ok (Some n)
            | _ -> Result.Error value

    /// The limit on concurrent launches in effect: the build variable (from the context's
    /// variables), then unlimited (`None`), with the name and the value when it is not a
    /// non-negative integer. Read from the variables directly, not with `getVar`: the
    /// concurrency changes no output, so it is not a dependency of the target. The second name
    /// (`XAKE_CSC_JOBS`, `XAKE_FSC_JOBS`) is not read: the environment counts only through the
    /// script, e.g. <c>var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]</c>.
    let jobsLimit (vars: (string * string) list) (variable: string, _envVariable: string) =
        match vars |> List.tryPick (fun (name, value) -> if name = variable then Some value else None) with
        | None -> None, None
        | Some value ->
            match parseJobs value with
            | Ok limit -> limit, None
            | Result.Error bad -> None, Some (variable, bad)

    type private Budget = { Resource: Resource; mutable Limit: int }

    /// Process-wide, one per jobs variable: every build in the process (tests, a long-running
    /// host) shares it; a different limit resizes it.
    let private budgets = System.Collections.Concurrent.ConcurrentDictionary<string, Budget> ()

    let private budgetFor (name: string) (limit: int) =
        let budget = budgets.GetOrAdd (name, fun _ -> { Resource = Resource.newResource name limit; Limit = limit })
        lock budget (fun () ->
            if budget.Limit <> limit then
                Resource.resize budget.Resource limit
                budget.Limit <- limit)
        budget.Resource

    /// Writes the response file, starts the compiler and checks its outcome. Every input is
    /// built and verified by now: this is what the jobs limit throttles, and nothing in it may
    /// `need` (it runs under `withResource`).
    let private launch (plan: RunPlan) : Recipe<ExecContext, unit> =
        recipe {
            let rspFile = Path.GetTempFileName()
            File.WriteAllLines (rspFile, plan.ResponseArgs |> List.map (responseLine plan.ResponseQuoting))

            // the response file has to go regardless of how the compilation ends -- unless the
            // compiler died without a word, then it is kept for a rerun by hand
            let keepRsp = ref false
            let deleteTempFiles () =
                if not keepRsp.Value then
                    try System.IO.File.Delete rspFile with _ -> ()

            match plan.ClientSwitchesNote with
            | Some note -> do! trace Debug "%s" note
            | None -> ()
            let tool, commandLineArgs =
                commandLine plan.Launch (plan.ClientSwitches @ plan.Leading @ [ "@" + rspFile ])

            do! trace Debug "Command line: '%s %s'" tool (commandLineArgs |> String.concat " ")

            // what the compiler printed, kept (bounded) for a failure without a diagnostic: a
            // crash (exit 134, `Environment.FailFast`) logs nothing at the default verbosity
            let output = System.Collections.Generic.Queue<string> ()
            let sawError = ref false
            // one run: a repeated warning is logged once, the errors are kept for the failure
            let diagnostics = Tool.DiagnosticLog plan.Diagnostics
            let keep (stream: string) (line: string) =
                lock output (fun () ->
                    output.Enqueue (stream + line)
                    if output.Count > 200 then output.Dequeue () |> ignore)
            let classify (stream: string) (line: string) =
                keep stream line
                let level = diagnostics.Level line
                if level = Level.Error then sawError.Value <- true
                level

            try
                let! exitCode =
                    shell {
                        cmd tool
                        args commandLineArgs
                        envs plan.Environment
                        // `dotnet build` runs the compiler with cwd = the project's directory;
                        // some inputs are resolved against it rather than against an argument
                        // (an XML-doc `<include file='../..'>` path, CS1589)
                        workdir plan.Directory
                        logprefix plan.LogPrefix
                        stdoutlevel (classify "")
                        erroutlevel (classify "stderr: ")
                    }

                if exitCode <> 0 && not sawError.Value then
                    keepRsp.Value <- true
                    let printed = lock output (fun () -> List.ofSeq output)
                    do! trace Error "('%s') %s exited with code %d and reported no error%s; response file kept: %s\n%s"
                            plan.Name plan.Tool exitCode
                            (if exitCode = 134 || exitCode = -6 then " (an abort: Environment.FailFast or a runtime crash)" else "")
                            rspFile
                            (match printed with [] -> "(it printed nothing)" | lines -> lines |> String.concat "\n")
                do! Tool.failOnExitCodeWith plan.FailOnError plan.Name diagnostics exitCode

                // an output next to the compiler's, declared by the rule; rewritten only when it
                // differs, so an up-to-date one keeps its timestamp
                match plan.RuntimeConfig with
                | Some (path, content) when exitCode = 0 ->
                    if not (File.Exists path && File.ReadAllText path = content) then
                        File.WriteAllText (path, content)
                | _ -> ()
            finally
                deleteTempFiles ()
        }

    /// Runs the plan: the steps `Csc.run` always took, in its order.
    let run (plan: RunPlan) : Recipe<ExecContext, unit> =
        recipe {
            do! trace Info "compiling '%s' (%s %s)" plan.Name plan.Tool plan.CompilerVersion

            if plan.RequireCompiler && not (File.Exists plan.CompilerPath) then
                let msg = sprintf "'%s': the compiler %s does not exist" plan.Name plan.CompilerPath
                do! trace Error "%s" msg
                if plan.FailOnError then failwith msg

            // the compiler is hashed (below) but was never a tracked dependency, so an SDK or
            // toolset update that changes its bytes left the target looking up to date and the
            // hash check never ran (conceptual-review.md 2.4)
            do! needFiles (Filelist [File.make plan.CompilerPath])

            // the resolved compilation is the source of truth for what msbuild (or the
            // composed front end) generated (assembly attributes, TFM defines): write it back
            // whenever it is missing or someone touched it
            for (path, content) in plan.Generated do
                let upToDate = File.Exists path && File.ReadAllText path = content
                if not upToDate then
                    let dir = Path.GetDirectoryName path
                    if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore
                    File.WriteAllText (path, content)

            // fsc creates the directory of --out but not that of --doc; csc neither
            for path in plan.Outputs do
                let dir = Path.GetDirectoryName path
                if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore

            // a resx is named on the command line as the `.resources` file it produces, which
            // has to exist before the `needFiles` of the inputs below sees it. The resx is
            // `needFiles`d so the engine decides whether an edit reruns this recipe; when it
            // runs, every `.resources` is compiled again from its resx (no timestamp
            // comparison, so `run` is not a second rebuilder next to the engine's,
            // conceptual-review.md 2.3) and written only when its bytes change.
            do! needFiles (Filelist (plan.Resources |> List.map (fst >> File.make)))
            for (resx, resourcesFile) in plan.Resources do
                Resx.refresh resx resourcesFile |> ignore

            // everything that carries a hash has to be exactly what was recorded, or the
            // compilation is not the one described
            let hashedFiles = plan.Hashed |> List.filter (fun (_, expected) -> expected <> "")
            let verify files =
                recipe {
                    let mismatches =
                        files |> List.choose (fun (path: string, expected) ->
                            let actual = if File.Exists path then sha256 path else "missing"
                            if actual = expected then None else Some (path, expected, actual))
                    if not (List.isEmpty mismatches) then
                        let detail =
                            mismatches
                            |> List.map (fun (path, expected, actual) -> sprintf "%s: expected %s, got %s" path expected actual)
                            |> String.concat "\n"
                        do! trace Error "('%s') hash mismatch:\n%s" plan.Name detail
                        if plan.FailOnError then
                            failwithf "('%s') hash mismatch:\n%s" plan.Name detail
                }

            // a hashed file that is missing and that no rule of the script produces cannot be
            // obtained by the `needFiles` below, which would stop at the first one with "Neither
            // rule nor file"; report all of them now, each with the hash it was expected to have
            let! ctxOptions = getCtxOptions()
            let hasRule path =
                ExecCore.locateRule ctxOptions.Rules ctxOptions.ProjectRoot (FileTarget (File.make path)) |> Option.isSome
            do! verify (hashedFiles |> List.filter (fun (path, _) -> not (File.Exists path) && not (hasRule path)))

            // the generated files and the `.resources` outputs have to exist before the inputs
            // are demanded (neither has a rule, so the engine takes them as plain files). This
            // needs everything the args name -- the framework's references included. It also
            // has to come before the full hash check: a reference another rule of the script
            // produces is (re)built here, and the check verifies what the compiler is about to
            // read, not what was on disk before.
            do! needFiles (Filelist (plan.Inputs |> List.map File.make))

            do! verify hashedFiles

            // the throttle is taken here, after every `needFiles`: holding it across a `need`
            // can deadlock the build (docs/resources.md). The script's own resource, when the
            // compilation names one, replaces the jobs limit.
            match plan.Throttle with
            | Some (resource, units) -> do! withResource resource units (launch plan)
            | None ->
                let! ctx = getCtx()
                let limit, invalid = jobsLimit ctx.Vars plan.Jobs
                match invalid with
                | Some (name, value) -> do! trace Warning "%s='%s' is not a non-negative integer; compiler launches are not limited" name value
                | None -> ()
                match limit with
                | Some n -> do! withResource (budgetFor (fst plan.Jobs) n) 1 (launch plan)
                | None -> do! launch plan
        }
