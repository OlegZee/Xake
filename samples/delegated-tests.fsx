// ---------------------------------------------------------------------------
// Delegated TEST RUN — single-process simulation.
//
//   * 100 suites matched by ONE masked rule "testsuite-(num:*)"; each "runs" as
//     a real external process that sleeps 5–10s.
//   * Dispatch BUDGET caps concurrency at 20.
//
// KEY POINT: a delegated rule is NEVER bounded by the local CPU/thread pool.
// The whole delegated task (the executor AND the rule body) runs detached, so
// THREADS below is deliberately tiny (4) yet the observed peak is the BUDGET (20).
// `runDetached` is therefore NOT needed inside a delegated rule body — the
// engine already runs it off the CPU pool. (Contrast: `runDetached` IS needed for
// an ordinary, non-delegated I/O rule whose body runs holding a CPU slot.)
//
// USAGE:  dotnet fsi samples/delegated-tests.fsx
// ---------------------------------------------------------------------------

#r "../out/netstandard2.0/Xake.dll"

open Xake
open System.Threading
open System.Collections.Concurrent
open System.Threading.Tasks

let SUITES = 100
let BUDGET = 20
let THREADS = 4              // tiny on purpose — proves delegated work ignores the CPU pool

// ---------------------------------------------------------------------------
// Live concurrency meter — peak number of suites running at once.
// ---------------------------------------------------------------------------
let running = ref 0
let peak    = ref 0
let gate    = obj()
let enter () =
    let n = Interlocked.Increment running
    lock gate (fun () -> if n > peak.Value then peak.Value <- n)
let leave () = Interlocked.Decrement running |> ignore

// ---------------------------------------------------------------------------
// Infrastructure layer (≈ Qualhalla): run-scoped store, in-flight dedup, budget.
// ---------------------------------------------------------------------------
let identityOf (targets: Target list) =
    match List.head targets with
    | PhonyAction name -> name
    | FileTarget file  -> sprintf "%A" file

let makeLocalExecutor (budget: Resource) : DelegatedExecutor<ExecContext> =
    let store    = ConcurrentDictionary<string, BuildResult>()
    let inFlight = ConcurrentDictionary<string, Lazy<Task<BuildResult>>>()

    fun ctx targets runBody ->
        let key = identityOf targets
        async {
            match store.TryGetValue key with
            | true, cached -> return cached                    // hit: skip the body
            | _ ->
                let fresh = lazy (
                    (async {
                        // The engine runs this executor detached (it holds no CPU slot),
                        // so use `withAcquired` (no CPU-slot yielding).
                        return! Resource.withAcquired budget 1 (async {
                            let! result = runBody ()           // runs the suite exactly once
                            store.[key] <- result
                            return result
                        })
                    }) |> Async.StartAsTask)
                let entry = inFlight.GetOrAdd(key, fresh)      // concurrent demands join one run
                let! result = entry.Value |> Async.AwaitTask
                inFlight.TryRemove key |> ignore
                return result
        }

// ---------------------------------------------------------------------------
// The "remote" work: a real external process returning an exit code.
// ---------------------------------------------------------------------------
let runExternal (script: string) = async {
    let isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
    let shell, flag = if isWindows then "cmd.exe", "/c" else "/bin/sh", "-c"
    let psi = System.Diagnostics.ProcessStartInfo(shell, RedirectStandardOutput = true, UseShellExecute = false)
    psi.ArgumentList.Add flag
    psi.ArgumentList.Add script
    use p = System.Diagnostics.Process.Start psi
    do! p.WaitForExitAsync() |> Async.AwaitTask
    return p.ExitCode
}

let printLock = obj()
let logLine fmt = Printf.kprintf (fun s -> lock printLock (fun () -> System.Console.Out.WriteLine s)) fmt

// ---------------------------------------------------------------------------
// ONE masked rule. `num` (the captured suite number) drives the simulated
// duration (5..10s) and outcome (every 7th suite "fails"). No `runDetached` —
// a delegated body is already detached from the CPU pool by the engine.
// ---------------------------------------------------------------------------
let executor = makeLocalExecutor (Resource.newResource "dispatch" BUDGET)

let suiteRule =
    ("testsuite-(num:*)" => recipe {
        let! num = getRuleMatch "num"
        let name = sprintf "testsuite-%s" num
        let n = int num
        let seconds = 5 + (n % 6)
        let shouldFail = n % 7 = 0

        enter ()
        try
            let! code = runExternal (sprintf "sleep %d; exit %d" seconds (if shouldFail then 1 else 0))
            let status = if code = 0 then "passed" else "FAILED"
            logLine "  %-16s : %-6s (%ds)" name status seconds
        finally
            leave ()
    })
    |> delegated executor

let suiteNames = [ for i in 1..SUITES -> sprintf "testsuite-%03d" i ]

do RulesBuilder { ExecOptions.Default with Threads = THREADS; NoPersist = true; ConLogLevel = Silent; FileLogLevel = Silent; Progress = false } {
    want ["main"]

    rules [
        "main" => recipe {
            logLine "Running %d suites | budget=%d | threads=%d\n" SUITES BUDGET THREADS
            do! need suiteNames
            logLine "\nDone. Peak concurrency: %d   (budget=%d, threads=%d — note: NOT capped by threads)" peak.Value BUDGET THREADS
        }

        suiteRule
    ]
}
