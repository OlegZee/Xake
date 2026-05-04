// common tasks
module internal Xake.ProcessExec

open System.Diagnostics
open System.Threading.Tasks

/// Starts an external process, redirecting stdout and stderr to the given handlers.
/// Returns the process exit code.
let pexec handleStd handleErr cmd args (envvars:(string * string) list) workDir = async {
    let pinfo =
      ProcessStartInfo
        (cmd, args,
          UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardError = true, RedirectStandardOutput = true)

    for name, value in envvars do
        pinfo.EnvironmentVariables.[name] <- value

    match workDir with
    | Some path -> pinfo.WorkingDirectory <- path
    | _ -> ()

    use proc = new Process(StartInfo = pinfo, EnableRaisingEvents = true)

    let tcs = TaskCompletionSource<unit>()
    proc.Exited.Add(fun _ -> tcs.TrySetResult() |> ignore)

    proc.ErrorDataReceived.Add(fun e -> if e.Data <> null then handleErr e.Data)
    proc.OutputDataReceived.Add(fun e -> if e.Data <> null then handleStd e.Data)

    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    do! tcs.Task |> Async.AwaitTask
    proc.WaitForExit()
    return proc.ExitCode
}
