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
        /// (resx, .resources) pairs; the output is compiled when missing
        Resources: (string * string) list
        /// Environment variables of the compiler process
        Environment: (string * string) list
        /// The compiler's working directory
        Directory: string
        FailOnError: bool
        /// Classifies a line of compiler output by log level
        Diagnostics: string -> Level
    }

    /// The launch for a compiler file: a `.dll` through `dotnet`, anything else directly.
    let launchOf (path: string) = if Impl.endsWith ".dll" path then Dotnet path else Native path

    let private sha256 (path: string) = if File.Exists path then Hash.sha256 path else ""

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
            // `needFiles`d so the engine decides whether an edit reruns this recipe;
            // regenerating only when the output is missing (not on a timestamp comparison)
            // keeps `run` from being a second rebuilder next to the engine's
            // (conceptual-review.md 2.3).
            do! needFiles (Filelist (plan.Resources |> List.map (fst >> File.make)))
            for (resx, resourcesFile) in plan.Resources do
                if not (File.Exists resourcesFile) then
                    Resx.compile resx resourcesFile

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

            let rspFile = Path.GetTempFileName()
            File.WriteAllLines (rspFile, plan.ResponseArgs |> List.map Impl.escapeArgument)
            let commandLineArgs =
                seq {
                    yield! plan.Leading
                    yield "@" + rspFile
                }

            // the response file has to go regardless of how the compilation ends
            let deleteTempFiles () =
                try System.IO.File.Delete rspFile with _ -> ()

            let tool, extraArgs =
                match plan.Launch with
                | Dotnet dll -> "dotnet", [dll]
                | Native exe -> exe, []

            match plan.ClientSwitchesNote with
            | Some note -> do! trace Debug "%s" note
            | None -> ()
            let commandLineArgs = Seq.append plan.ClientSwitches commandLineArgs

            do! trace Debug "Command line: '%s %s'" tool
                    ((extraArgs @ List.ofSeq commandLineArgs) |> String.concat " ")

            try
                let! exitCode =
                    shell {
                        cmd tool
                        args (Seq.append extraArgs commandLineArgs)
                        envs plan.Environment
                        // `dotnet build` runs the compiler with cwd = the project's directory;
                        // some inputs are resolved against it rather than against an argument
                        // (an XML-doc `<include file='../..'>` path, CS1589)
                        workdir plan.Directory
                        logprefix plan.LogPrefix
                        stdoutlevel plan.Diagnostics
                        erroutlevel plan.Diagnostics
                    }

                do! Tool.failOnExitCode plan.FailOnError plan.Name exitCode
            finally
                deleteTempFiles ()
        }
