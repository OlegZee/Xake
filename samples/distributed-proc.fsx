// ---------------------------------------------------------------------------
// Distributed TEST RUN — TWO-PROCESS variant.
//
// Here the orchestrator (this process) does NOT run any suite logic. Its
// `DistributedExecutor` is the seam where execution leaves the local process:
//   * on a MISS it dispatches the suite to a SEPARATE worker process
//     (`dotnet fsi worker.fsx <id> <storeDir>`) and waits for it;
//   * the worker publishes its result to a shared store (a directory ≈ S3);
//   * on a HIT it reads the stored result and runs NO worker at all;
//   * concurrent demands for the same suite deduplicate to one worker.
//
// This is exactly what `distributed` adds over a plain `runDetached` rule: a
// pluggable rebuilder/executor that can serve from a shared store, dedup, and
// run work elsewhere. Note there is NO `runDetached` in this script — the
// executor's wait is already off the CPU pool (the core runs it detached), so
// the dispatch budget (not the core count) is the only concurrency limit.
//
// USAGE:  dotnet fsi samples/distributed-proc.fsx
// ---------------------------------------------------------------------------

#r "../out/netstandard2.0/Xake.dll"

open Xake
open System.IO
open System.Threading
open System.Diagnostics
open System.Collections.Concurrent
open System.Threading.Tasks

let SUITES = 24          // number of suites
let BUDGET = 6           // max workers running at once

let scriptDir = __SOURCE_DIRECTORY__
let workerScript = Path.Combine(scriptDir, "worker.fsx")
let storeDir = Path.Combine(scriptDir, "temp", "distrun-store")

// ---------------------------------------------------------------------------
// Concurrency meter — proves the budget caps live worker processes.
// ---------------------------------------------------------------------------
let printLock = obj()
let log fmt = Printf.kprintf (fun s -> lock printLock (fun () -> System.Console.Out.WriteLine s)) fmt

let running = ref 0
let peak    = ref 0
let gate    = obj()
let enter () =
    let n = Interlocked.Increment running
    lock gate (fun () -> if n > peak.Value then peak.Value <- n)
let leave () = Interlocked.Decrement running |> ignore

// ---------------------------------------------------------------------------
// The shared result store (≈ S3 bucket). Run-scoped: cleared at start.
// ---------------------------------------------------------------------------
let resultPathOf name = Path.Combine(storeDir, name + ".result")
let tryFetch name = let p = resultPathOf name in if File.Exists p then Some (File.ReadAllText p) else None

// ---------------------------------------------------------------------------
// Dispatch one suite to a separate worker PROCESS and wait for it.
// ---------------------------------------------------------------------------
let dispatchToWorker id = async {
    let psi = ProcessStartInfo("dotnet", UseShellExecute = false)
    psi.ArgumentList.Add "fsi"
    psi.ArgumentList.Add workerScript
    psi.ArgumentList.Add (string id)
    psi.ArgumentList.Add storeDir
    use p = Process.Start psi
    do! p.WaitForExitAsync() |> Async.AwaitTask
    return p.ExitCode
}

// ---------------------------------------------------------------------------
// The distributed executor (≈ Qualhalla policy): store lookup + in-flight dedup
// + dispatch budget. It synthesizes the BuildResult; the local recipe body is
// never run, because the work happens in the worker process.
// ---------------------------------------------------------------------------
let makeExecutor (budget: Resource) : DistributedExecutor<ExecContext> =
    let inFlight = ConcurrentDictionary<string, Lazy<Task<BuildResult>>>()
    let synthResult targets : BuildResult =
        { Targets = targets; Built = System.DateTime.Now; Depends = []; Steps = [] }

    fun ctx targets _runBody ->
        let name = match List.head targets with PhonyAction n -> n | t -> sprintf "%A" t
        let id = int (name.Substring "testsuite-".Length)
        async {
            match tryFetch name with
            | Some status ->
                log "  [cached]   %-16s : %s" name status          // HIT: no worker, body not run
                return synthResult targets
            | None ->
                let fresh = lazy (
                    (async {
                        do! Resource.acquire budget 1   // detached executor holds no CPU slot — non-yielding acquire
                        try
                            enter ()
                            let! _code = dispatchToWorker id        // runs in ANOTHER process
                            leave ()
                            let status = tryFetch name |> Option.defaultValue "?"
                            log "  [executed] %-16s : %s" name status
                            return synthResult targets
                        finally
                            Resource.release budget 1
                    }) |> Async.StartAsTask)
                let entry = inFlight.GetOrAdd(name, fresh)
                if not (obj.ReferenceEquals(entry, fresh)) then
                    log "  [dedup]    %-16s : joined an in-flight worker" name
                let! result = entry.Value |> Async.AwaitTask
                inFlight.TryRemove name |> ignore
                return result
        }

// ---------------------------------------------------------------------------
// Script. One masked rule; author writes only `need`. The recipe body is empty
// because execution is delegated to the worker process via the executor.
// ---------------------------------------------------------------------------
let executor = makeExecutor (Resource.newResource "dispatch" BUDGET)
let suiteNames = [ for i in 1..SUITES -> sprintf "testsuite-%03d" i ]

// fresh run-scoped store
if Directory.Exists storeDir then Directory.Delete(storeDir, true)
Directory.CreateDirectory storeDir |> ignore

printfn "Orchestrator pid %d — dispatching %d suites, budget %d, worker = separate process\n"
    (Process.GetCurrentProcess().Id) SUITES BUDGET

do xakeScript {
    consolelog Verbosity.Quiet
    noPersist
    want ["main"]

    rules [
        "main" => recipe {
            printfn "Phase 1 — MISS: each suite dispatched to its own worker process"
            do! need suiteNames
            printfn "\nPhase 2 — re-need the same suites: served from the store, NO workers"
            do! need suiteNames
            printfn "\nDone. Peak concurrent worker processes: %d (budget = %d)." peak.Value BUDGET
        }

        // empty body: the work lives in the worker process, not here
        ("testsuite-(num:*)" => recipe { () }) |> distributed executor
    ]
}
