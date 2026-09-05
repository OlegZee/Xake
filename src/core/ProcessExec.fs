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

/// Runs an external process and blocks until it exits, returning the exit code.
/// For use outside recipes -- framework and tool probing, where there is no async context to
/// await in. Safe on the worker pool: recipes run without a SynchronizationContext, and the
/// process output handlers are raised on other thread-pool threads.
let pexecSync handleStd handleErr cmd args envvars workDir =
    pexec handleStd handleErr cmd args envvars workDir |> Async.RunSynchronously
