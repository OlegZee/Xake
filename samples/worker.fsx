// ---------------------------------------------------------------------------
// "Remote agent" worker — a STANDALONE process (no Xake dependency).
// The orchestrator dispatches one of these per suite; this is the code that, in
// a real system, would run on another machine after checking out the source.
//
//   Usage:  dotnet fsi worker.fsx <id> <storeDir>
//
// It "runs" suite <id>, then publishes the outcome to the shared store
// (a file in <storeDir>, standing in for an S3 object), and exits with a
// non-zero code on failure. The orchestrator never runs this logic itself.
// ---------------------------------------------------------------------------

let args = fsi.CommandLineArgs        // [| "worker.fsx"; <id>; <storeDir> |]
let id = int args.[1]
let storeDir = args.[2]

let name = sprintf "testsuite-%03d" id
let seconds = 2 + (id % 4)            // simulated 2..5s suite duration
let shouldFail = id % 7 = 0           // every 7th suite "fails"

System.Threading.Thread.Sleep(seconds * 1000)

let status = if shouldFail then "FAILED" else "passed"
System.IO.Directory.CreateDirectory storeDir |> ignore
System.IO.File.WriteAllText(System.IO.Path.Combine(storeDir, name + ".result"), status)

let pid = System.Diagnostics.Process.GetCurrentProcess().Id
printfn "    [worker pid %d] ran %s -> %s (%ds)" pid name status seconds

exit (if shouldFail then 1 else 0)
