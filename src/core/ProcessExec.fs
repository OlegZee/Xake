// common tasks
module internal Xake.ProcessExec

open System.Diagnostics
open System.Threading.Tasks

/// How long the output of an exited process is read on (ms) before its exit code is returned
/// without the rest: the output pipes stay open as long as a process it left running holds them.
let drainTimeoutMs = 5000

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

    // a null line is the end of the stream
    let outEnd, errEnd = TaskCompletionSource<unit>(), TaskCompletionSource<unit>()
    proc.ErrorDataReceived.Add(fun e -> if e.Data <> null then handleErr e.Data else errEnd.TrySetResult() |> ignore)
    proc.OutputDataReceived.Add(fun e -> if e.Data <> null then handleStd e.Data else outEnd.TrySetResult() |> ignore)

    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    do! tcs.Task |> Async.AwaitTask
    // The process has exited; its output is read to the end, but not waited for forever: a
    // process it started and left running (an msbuild node, a compiler server) inherits the
    // pipes and keeps them open, so the end may not come until that one exits (the parameterless
    // WaitForExit waits for it). The exit code is there already (Exited).
    let drained = Task.WhenAll(outEnd.Task, errEnd.Task)
    let! _ = Task.WhenAny(drained, Task.Delay drainTimeoutMs) |> Async.AwaitTask
    return proc.ExitCode
}

/// Runs an external process and blocks until it exits, returning the exit code.
/// For use outside recipes -- framework and tool probing, where there is no async context to
/// await in. Safe on the worker pool: recipes run without a SynchronizationContext, and the
/// process output handlers are raised on other thread-pool threads.
let pexecSync handleStd handleErr cmd args envvars workDir =
    pexec handleStd handleErr cmd args envvars workDir |> Async.RunSynchronously
