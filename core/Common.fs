﻿// common tasks

[<AutoOpen>]
module Xake.Common

open Xake
open System.IO
open System.Diagnostics

// internal implementation
let internal _system cmd args =
  async {
    let pinfo =
      ProcessStartInfo
        (cmd, args,
          UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardError = true, RedirectStandardOutput = true)

    let proc = new Process(StartInfo = pinfo)
     
    proc.ErrorDataReceived.Add(fun  e -> if e.Data <> null then log Level.Error "%s" e.Data)
    proc.OutputDataReceived.Add(fun e -> if e.Data <> null then log Level.Info "%s" e.Data)

    do proc.Start() |> ignore

    do proc.BeginOutputReadLine()
    do proc.BeginErrorReadLine()

    proc.EnableRaisingEvents <- true
    let! _ = Async.AwaitEvent proc.Exited

    return proc.ExitCode
  }

// joins and escapes strings
let private joinArgs (args:string list list) =
  // TODO quote
  (" ", args |> List.fold (@) [] |> Array.ofList) |> System.String.Join

// executes external process and waits until it completes
let system cmd ([<System.ParamArray>] args) =
  async {
    do log Level.Info "[system] starting '%s'" cmd
    let! exitCode = _system cmd (joinArgs args)
    do log Level.Info "[system] сompleted '%s' exitcode: %d" cmd exitCode
    return exitCode
  }


// executes command
let cmd cmdline ([<System.ParamArray>] args : string list list) =
  async {
    do log Level.Info "[cmd] starting '%s'" cmdline
    let! exitCode = _system "cmd.exe" (joinArgs (["/c"; cmdline] :: args))
    do log Level.Info "[cmd] completed '%s' exitcode: %d" cmdline exitCode
    return exitCode
  } 

// reads the file and returns all text
let readtext artifact =
  artifact |> fullname |> File.ReadAllText
