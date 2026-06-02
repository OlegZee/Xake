// ---------------------------------------------------------------------------
// Delegated build — a complete, runnable LOCAL example.
//
// It shows the three new core mechanisms working together:
//   1. a delegated-rule hook        — `delegated executor rule`
//   2. CPU-slot decoupling            — `runDetached`
//   3. a concurrency Resource         — `Resource.newResource` / `acquire`
//
// The "remote" work here is a REAL external process (`/bin/sh ...`). The
// `executor` plays the role of Qualhalla's infrastructure:
//   * a run-scoped result store      (≈ S3 / Garage): identity -> BuildResult
//   * in-flight deduplication        (≈ Temporal at-most-once by identity)
//   * an advisory dispatch budget    (a delegated Resource)
// All of that is policy living OUTSIDE Xake core — exactly as intended.
//
// USAGE:
//   dotnet fsi samples/delegated.fsx
//   dotnet fsi samples/delegated.fsx -- -- main
// ---------------------------------------------------------------------------

// Use the locally built library (the new API is not on NuGet yet).
#r "../out/netstandard2.0/Xake.dll"

open Xake
open System.Collections.Concurrent
open System.Threading.Tasks

// ---------------------------------------------------------------------------
// "Infrastructure" layer — this is what Qualhalla would implement.
// A factory that returns a DelegatedExecutor backed by an in-memory,
// run-scoped store + in-flight dedup, throttled by a dispatch Resource.
// ---------------------------------------------------------------------------

/// Run-scoped identity for a target. Per ADR-0010 a run-scoped key
/// (session + target identity) is enough — no content addressing needed.
/// Here we derive it from the phony name and deliberately collapse a
/// "-mirror" suffix so two different targets share one identity (to show dedup).
let identityOf (targets: Target list) =
    match List.head targets with
    | PhonyAction name -> name.Replace("build:", "").Replace("-mirror", "")
    | FileTarget file  -> sprintf "%A" file

let makeLocalExecutor (budget: Resource) : DelegatedExecutor<ExecContext> =
    // ≈ S3: published results, keyed by identity. Empty at start of the run.
    let store = ConcurrentDictionary<string, BuildResult>()
    // ≈ Temporal: at-most-once execution per identity; concurrent demands join.
    let inFlight = ConcurrentDictionary<string, Lazy<Task<BuildResult>>>()

    fun ctx targets runBody ->
        let key = identityOf targets
        async {
            match store.TryGetValue key with
            | true, cached ->
                // Up-to-date check delegated to us: a hit means SKIP the body.
                printfn "  [store]   HIT  '%s' — returning shared result, body NOT run" key
                return cached
            | _ ->
                let fresh = lazy (
                    (async {
                        // Advisory dispatch budget (caller-owned policy).
                        return! Resource.withAcquired budget 1 (async {
                            printfn "  [dispatch] MISS '%s' — running the work (budget acquired)" key
                            let! result = runBody ()        // runs the recipe body exactly once
                            store.[key] <- result           // publish to the shared store
                            printfn "  [store]   PUT  '%s'" key
                            return result
                        })
                    }) |> Async.StartAsTask)

                let entry = inFlight.GetOrAdd(key, fresh)
                if not (obj.ReferenceEquals(entry, fresh)) then
                    printfn "  [dedup]   '%s' — joined an in-flight execution (body not run again)" key
                let! result = entry.Value |> Async.AwaitTask
                inFlight.TryRemove key |> ignore
                return result
        }

// ---------------------------------------------------------------------------
// The actual "remote" work: a REAL external process.
// (Swap this for ssh/docker/Temporal-activity in a real delegated runner.)
// ---------------------------------------------------------------------------

let runExternal (script: string) = async {
    let isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
    let shell, flag = if isWindows then "cmd.exe", "/c" else "/bin/sh", "-c"
    let psi = System.Diagnostics.ProcessStartInfo(shell, RedirectStandardOutput = true, UseShellExecute = false)
    psi.ArgumentList.Add flag
    psi.ArgumentList.Add script
    use p = System.Diagnostics.Process.Start psi
    let! out = p.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
    p.WaitForExit()
    return out.Trim()
}

/// A recipe that "compiles a module" by shelling out to an external process.
let buildModule name = recipe {
    let! output = runExternal (sprintf "sleep 1; echo \"compiled %s (pid $$)\"" name)
    printfn "  [body]    external process for '%s' -> %s" name output
}

// ---------------------------------------------------------------------------
// The script. The author only ever writes `need` — distribution is a property
// of the rule, configured once via `|> delegated executor`.
// ---------------------------------------------------------------------------

// One executor instance per run. Dispatch budget = at most 2 remote runs at once.
let executor = makeLocalExecutor (Resource.newResource "dispatch" 2)

do xakeScript {
    consolelog Verbosity.Quiet      // keep Xake's own logging quiet; we printfn our own trace
    noPersist                       // always run, ignore the .xake db for this demo
    want ["main"]

    rules [
        // Three delegated rules. "build:core" and "build:core-mirror" intentionally
        // share an identity ("core") to demonstrate in-flight dedup.
        ("build:core"        => buildModule "core")  |> delegated executor
        ("build:core-mirror" => buildModule "core")  |> delegated executor
        ("build:utils"       => buildModule "utils") |> delegated executor

        // A plain (non-delegated) I/O-bound rule that must NOT pin a CPU slot:
        // `runDetached` releases the slot while it waits on the external process.
        "slow-io" => runDetached (recipe {
                let! _ = runExternal "sleep 1; echo done"
                printfn "  [body]    detached I/O finished (held no CPU slot while waiting)"
            })

        "main" => recipe {
            printfn "\n=== Phase 1: parallel need — expect 1 miss + 1 dedup for 'core', 1 miss for 'utils' ==="
            do! need ["build:core"; "build:core-mirror"; "build:utils"; "slow-io"]

            printfn "\n=== Phase 2: re-need 'build:core' — expect a store HIT (no external process) ==="
            do! need ["build:core"]

            printfn "\n=== Done. ==="
        }
    ]
}
