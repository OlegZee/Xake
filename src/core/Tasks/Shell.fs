namespace Xake.Tasks

open Xake
open Xake.Env
open Xake.ProcessExec

[<AutoOpen>]
module ShellImpl =

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
    }
    with static member Default = {
            Command = null; Args = []
            LogPrefix = ""; StdOutLevel = (fun _ -> Info); ErrOutLevel = (fun _ -> Error)
            EnvVars = []
            WorkingDir = None
            UseClr = false
            FailOnErrorLevel = false
        }

    /// <summary>
    /// Start shell/system process.
    /// Executes a shell command with the specified options and returns the exit code.
    /// </summary>
    /// <param name="opts">Shell execution options</param>
    /// <returns>Recipe that returns the exit code of the executed command</returns>
    let Shell (opts: ShellOptions) =
      let args = (opts.Args |> String.concat " ")
      let isExt file ext = System.IO.Path.GetExtension(file).Equals(ext, System.StringComparison.OrdinalIgnoreCase)

      recipe {
        let cmd = opts.Command
        do! trace Info "[shell] starting '%s'" cmd

        let! ctx = getCtx()
        let log = ctx.Logger.Log

        do! trace Level.Debug "[shell] settings: '%A'" opts

        let handleErr s = log (opts.ErrOutLevel s) "%s %s" opts.LogPrefix s
        let handleStd s = log (opts.StdOutLevel s) "%s %s" opts.LogPrefix s

        let cmd, args =
            if isWindows && not <| isExt cmd ".exe" then
                "cmd.exe", (sprintf "/c %s %s" cmd args)
            else if opts.UseClr && not isWindows then
                "mono", cmd + " " + args
            else
                cmd, args
        let exitCode = pexec handleStd handleErr cmd args opts.EnvVars opts.WorkingDir
        if exitCode <> 0 && opts.FailOnErrorLevel then failwith "System command resulted in non-zero errorlevel"

        // let! exitCode = _system opts
        do! trace Info "[shell] completed '%s' exitcode: %d" cmd exitCode

        return exitCode
      }

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
        /// <summary>Set the prefix for log messages</summary>
        [<CustomOperation("logprefix")>] member __.LogPrefix(a:ShellOptions, value) = {a with LogPrefix = value}

        [<CustomOperation("stdout")>]
        member _.Stdout(state:ShellOptions, handler: string -> unit) =
            {state with StdOutLevel = fun x -> handler x; state.StdOutLevel x}
        
        [<CustomOperation("stderr")>]
        member _.Stderr(state:ShellOptions, handler: string -> unit) =
            {state with ErrOutLevel = fun x -> handler x; state.ErrOutLevel x}

        member __.Bind(x, f) = f x
        member __.Yield(()) = __.Zero()
        member __.For(sq, b) = for e in sq do b e

        member __.Zero() = { ShellOptions.Default with Command = command; Args = arglist }
        member __.Run(opts:ShellOptions) = Shell opts

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