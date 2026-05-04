namespace Xake.Tasks

open Xake
open Xake.Env
open Xake.ProcessExec

[<AutoOpen>]
module ShellImpl =

    type CaptureStream = Stdout | Stderr | Both

    type OutputDest =
        | ToLog        of CaptureStream * (string -> Level)
        | ToFile       of CaptureStream * string
        | ToFileAppend of CaptureStream * string
        | ToHandler    of CaptureStream * (string -> unit)

    /// <summary>
    /// Configuration options for shell command execution.
    /// </summary>
    type ShellOptions = {
        /// The command to execute
        Command: string
        /// Command line arguments
        Args: string seq
        /// Prefix for log messages
        LogPrefix:string
        /// Function to determine log level for standard output
        StdOutLevel: string -> Level
        /// Function to determine log level for error output
        ErrOutLevel: string -> Level
        /// Environment variables to set for the process
        EnvVars: (string * string) list
        /// Working directory for the process
        WorkingDir: string option
        /// Indicates command has to be executed under mono/.net runtime
        UseClr: bool
        /// Whether to fail the build on non-zero exit code
        FailOnErrorLevel: bool
        /// Additional output destinations (file, log, handler)
        CaptureSpecs: OutputDest list
    }
    with static member Default = {
            Command = null; Args = []
            LogPrefix = ""; StdOutLevel = (fun _ -> Info); ErrOutLevel = (fun _ -> Error)
            EnvVars = []
            WorkingDir = None
            UseClr = false
            FailOnErrorLevel = false
            CaptureSpecs = []
        }

    type ShellModeExitCode = private ShellModeExitCode of ShellOptions
    type ShellModeOutput   = private ShellModeOutput   of ShellOptions
    type ShellModeBoth     = private ShellModeBoth     of ShellOptions

    let private shellCore (opts: ShellOptions) (extraStd: string -> unit) =
      let args = opts.Args |> String.concat " "
      let isExt file ext = System.IO.Path.GetExtension(file).Equals(ext, System.StringComparison.OrdinalIgnoreCase)

      recipe {
        let cmd = opts.Command
        do! trace Info "[shell] starting '%s'" cmd

        let! ctx = getCtx()
        let log = ctx.Logger.Log

        do! trace Level.Debug "[shell] settings: '%A'" opts // dangerous to log all options, but we need it for debugging purposes. Consider redacting sensitive info in the future.

        let makeFileWriter path append =
            let sw = new System.IO.StreamWriter(path, append, System.Text.Encoding.UTF8)
            let gate = obj ()
            let mutable closed = false
            let write (line: string) = lock gate (fun () -> if not closed then sw.WriteLine line)
            let dispose () = lock gate (fun () -> closed <- true; sw.Flush(); (sw :> System.IDisposable).Dispose())
            write, dispose

        let sinks =
            opts.CaptureSpecs
            |> List.map (fun dest ->
                match dest with
                | ToLog (stream, levelFn) ->
                    stream, (fun line -> log (levelFn line) "%s %s" opts.LogPrefix line), None
                | ToFile (stream, path) ->
                    let write, dispose = makeFileWriter path false
                    stream, write, Some dispose
                | ToFileAppend (stream, path) ->
                    let write, dispose = makeFileWriter path true
                    stream, write, Some dispose
                | ToHandler (stream, handler) ->
                    stream, handler, None)

        let covers sinkStream target =
            match sinkStream with Both -> true | s -> s = target

        let stdSuppressLog = opts.CaptureSpecs |> List.exists (fun d -> match d with ToLog (s, _) -> covers s Stdout | _ -> false)
        let errSuppressLog = opts.CaptureSpecs |> List.exists (fun d -> match d with ToLog (s, _) -> covers s Stderr | _ -> false)

        let fanOut target line =
            for sinkStream, write, _ in sinks do
                if covers sinkStream target then write line

        let handleErr s =
            if not errSuppressLog then log (opts.ErrOutLevel s) "%s %s" opts.LogPrefix s
            fanOut Stderr s

        let handleStd s =
            if not stdSuppressLog then log (opts.StdOutLevel s) "%s %s" opts.LogPrefix s
            extraStd s
            fanOut Stdout s

        let cmd, args =
            if isWindows && not <| isExt cmd ".exe" then
                "cmd.exe", sprintf "/c %s %s" cmd args
            elif opts.UseClr && not isWindows then
                "mono", cmd + " " + args
            else
                cmd, args

        let! exitCode = async {
            try
                return! pexec handleStd handleErr cmd args opts.EnvVars opts.WorkingDir
            finally
                for _, _, d in sinks do d |> Option.iter (fun dispose -> dispose ())
        }

        if exitCode <> 0 && opts.FailOnErrorLevel then failwith "System command resulted in non-zero errorlevel"

        do! trace Info "[shell] completed '%s' exitcode: %d" cmd exitCode

        return exitCode
      }

    /// <summary>
    /// Start shell/system process.
    /// Executes a shell command with the specified options and returns the exit code.
    /// </summary>
    /// <param name="opts">Shell execution options</param>
    /// <returns>Recipe that returns the exit code of the executed command</returns>
    let Shell (opts: ShellOptions) = shellCore opts ignore

    /// <summary>
    /// Builder for shell command execution with a fluent API.
    /// Allows configuration of shell options using computation expression syntax.
    /// </summary>
    /// <param name="commandLine">The command line to parse into command and arguments</param>
    type ShellBuilder(commandLine: string) =

        let command, arglist =
            match List.ofArray <| commandLine.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries) with
            | [] -> "", []
            | x::xs -> x, xs

        /// <summary>Sets the command to execute</summary>
        [<CustomOperation("cmd")>]      member __.Command(a:ShellOptions, value) = {a with Command = value}
        
        /// <summary>
        /// Add arguments to the command.
        /// Arguments are appended to the existing list of arguments.
        /// </summary>
        [<CustomOperation("args")>]
        member __.Args(a:ShellOptions, value) =    {a with Args = Seq.append a.Args value}
        /// <summary>Sets the working directory for the process</summary>
        [<CustomOperation("workdir")>]  member __.WorkDir(a:ShellOptions, value) = {a with WorkingDir = Some value}
        /// <summary>Execute the command under mono/.net runtime</summary>
        [<CustomOperation("useclr")>]   member __.UseClr(a :ShellOptions) =        {a with UseClr = true}
        /// <summary>Fail the build on non-zero exit code</summary>
        [<CustomOperation("failonerror")>] member __.FailOnError(a :ShellOptions)= {a with FailOnErrorLevel = true}

        /// <summary>Add a single argument to the command</summary>
        [<CustomOperation("arg")>]     member __.Arg(a:ShellOptions, value) =    {a with Args = Seq.append a.Args [value]}
        /// <summary>Set an environment variable for the process</summary>
        [<CustomOperation("env")>]     member __.Env(a:ShellOptions, (name, value)) = {a with EnvVars = a.EnvVars @ [(name, value)]}

        // Appends multiple environment variables to the shell invocation.
        [<CustomOperation("envs")>]    member _.Envs(opts: ShellOptions, vars: (string * string) list) = { opts with EnvVars = opts.EnvVars @ vars }

        /// <summary>Set the prefix for log messages</summary>
        [<CustomOperation("logprefix")>] member __.LogPrefix(a:ShellOptions, value) = {a with LogPrefix = value}

        /// <summary>Attach an additional stdout handler</summary>
        [<CustomOperation("stdout")>]
        member _.Stdout(state:ShellOptions, handler: string -> unit) =
            { state with CaptureSpecs = state.CaptureSpecs @ [ToHandler (Stdout, handler)] }

        /// <summary>Attach an additional stderr handler</summary>
        [<CustomOperation("stderr")>]
        member _.Stderr(state:ShellOptions, handler: string -> unit) =
            { state with CaptureSpecs = state.CaptureSpecs @ [ToHandler (Stderr, handler)] }

        /// <summary>Add an output destination: ToLog, ToFile, ToFileAppend, or ToHandler</summary>
        [<CustomOperation("captureOutput")>]
        member _.CaptureOutput(state: ShellOptions, dest: OutputDest) =
            { state with CaptureSpecs = state.CaptureSpecs @ [dest] }

        /// <summary>Add multiple output destinations at once</summary>
        [<CustomOperation("captureOutput")>]
        member _.CaptureOutput(state: ShellOptions, dests: OutputDest list) =
            { state with CaptureSpecs = state.CaptureSpecs @ dests }

        /// <summary>Return exit code from the CE</summary>
        [<CustomOperation("result")>]
        member _.Result(opts: ShellOptions) = ShellModeExitCode opts

        [<CustomOperation("result")>]
        member _.Result(ShellModeOutput opts) = ShellModeBoth opts

        /// <summary>Capture stdout lines from the CE</summary>
        [<CustomOperation("output")>]
        member _.Output(opts: ShellOptions) = ShellModeOutput opts

        [<CustomOperation("output")>]
        member _.Output(ShellModeExitCode opts) = ShellModeBoth opts

        /// <summary>Shorthand for result + output: returns exit code and stdout lines</summary>
        [<CustomOperation("resultAndOutput")>]
        member _.ResultAndOutput(opts: ShellOptions) = ShellModeBoth opts

        member __.Bind(x, f) = f x
        member __.Yield(()) = __.Zero()
        member __.For(sq, b) = for e in sq do b e

        member __.Zero() = { ShellOptions.Default with Command = command; Args = arglist }
        member __.Run(opts:ShellOptions) = Shell opts

        member __.Run(ShellModeExitCode opts) = shellCore opts ignore

        member __.Run(ShellModeOutput opts) =
            recipe {
                let lines = System.Collections.Generic.List<string>()
                let! _ = shellCore opts lines.Add
                return lines |> Seq.toList
            }

        member __.Run(ShellModeBoth opts) =
            recipe {
                let lines = System.Collections.Generic.List<string>()
                let! exitCode = shellCore opts lines.Add
                return exitCode, lines |> Seq.toList
            }

    /// <summary>
    /// Default shell builder with no command set.
    /// Use with computation expression syntax to configure shell options.
    /// </summary>
    let shell = ShellBuilder ""

    /// <summary>
    /// Create a shell builder with a command line.
    /// Command-line is split by spaces into command and arguments.
    /// </summary>
    /// <param name="cmd">The command line to parse</param>
    let shellCmd cmd = ShellBuilder cmd

    /// <summary>
    /// "sh" command builder. Inherits from ShellBuilder and sets FailOnErrorLevel to true by default.
    /// Does not return result of the command execution for simpler invocation syntax.
    /// </summary>
    /// <param name="cmd">The command line to run</param>
    type ShBuilder(cmd: string) =
        inherit ShellBuilder(cmd)
        member __.Run(opts:ShellOptions) = Shell opts |> map ignore
        member __.Zero() = { base.Zero() with FailOnErrorLevel = true }


    /// <summary>
    /// Create a shell builder with a command.
    /// /// Example usage:
    /// <code>
    /// do! sh "dotnet build -c Release" { workdir = "src/core" }
    /// </code>
    /// </summary>
    /// <param name="cmd">The command line to run.</param>
    let sh cmd = ShBuilder cmd