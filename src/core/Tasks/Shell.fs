﻿namespace Xake.Tasks

open Xake
open Xake.Env
open Xake.ProcessExec

[<AutoOpen>]
module ShellImpl =

    type ShellOptions = {
        Command: string
        Args: string seq
        LogPrefix:string
        StdOutLevel: string -> Level; ErrOutLevel: string -> Level
        EnvVars: (string * string) list
        WorkingDir: string option

        /// Indicates command has to be executed under mono/.net runtime
        UseClr: bool
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

    /// Start shell/system process.
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

    type ShellBuilder() =

        [<CustomOperation("cmd")>]      member __.Command(a:ShellOptions, value) = {a with Command = value}
        [<CustomOperation("args")>]     member __.Args(a:ShellOptions, value) =    {a with Args = value}
        [<CustomOperation("workdir")>]  member __.WorkDir(a:ShellOptions, value) = {a with WorkingDir = Some value}
        [<CustomOperation("useclr")>]   member __.UseClr(a :ShellOptions) =        {a with UseClr = true}
        [<CustomOperation("failonerror")>] member __.FailOnError(a :ShellOptions)= {a with FailOnErrorLevel = true}

        member __.Bind(x, f) = f x
        member __.Yield(()) = ShellOptions.Default
        member __.For(sq, b) = for e in sq do b e

        member __.Zero() = ShellOptions.Default
        member __.Run(opts:ShellOptions) = Shell opts

    let shell = ShellBuilder()
