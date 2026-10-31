// Measurement B: a phony target that writes the 10,000-line corpus through Xake's logging.
// Driven by run_e2e.py (one process per run). Environment:
//   PERF_PCT   0 = no secret declarations; 2 = five `var "TOKENi" [Secret]` declarations, no line
//              carries a secret; 1 = the same declarations and 1% of the lines carry a secret
// Output: build.log (file log, Diag level) in the working directory; stdout lines ELAPSED_ENQUEUE_MS
// (the trace calls) and ELAPSED_MS (until the last line is on disk).
// Usage (from an empty work directory, so the .xake database and build.log stay out of the repo):
//   dotnet fsi <path>/e2e.fsx -- -- main --resetdb   (with PERF_PCT and XAKE__TOKEN1..5 set)
#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#load "corpus.fsx"

open System
open System.Diagnostics
open System.IO
open Xake
open Xake.Logging

let mode = match Environment.GetEnvironmentVariable "PERF_PCT" with null | "" -> 0 | s -> int s
let pct = if mode = 1 then 1 else 0    // mode 2: declarations only

// The file logger posts to a MailboxProcessor and returns at once, so the lines are written after
// the trace calls return. The end marker is the last line posted; the target waits until it is on
// disk, so the timing includes the write and the run cannot end with lines still queued.
let marker = "PERF-END-MARK"
let awaitMarker (path: string) =
    let deadline = DateTime.UtcNow.AddMinutes 5.0
    let mutable found = false
    while not found do
        if DateTime.UtcNow > deadline then failwithf "marker not written to %s" path
        try
            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            fs.Seek(max 0L (fs.Length - 4096L), SeekOrigin.Begin) |> ignore
            use sr = new StreamReader(fs)
            found <- sr.ReadToEnd().Contains marker
        with :? IOException -> ()
        if not found then Threading.Thread.Sleep 1

let target =
    recipe {
        let lines = Corpus.baseLines () |> Corpus.withSecrets pct
        let sw = Stopwatch.StartNew()
        for line in lines do
            do! trace Info "%s" line
        do! trace Info "%s" marker
        let enqueued = sw.Elapsed.TotalMilliseconds
        awaitMarker "build.log"
        Console.WriteLine (sprintf "ELAPSED_ENQUEUE_MS=%.3f" enqueued)
        Console.WriteLine (sprintf "ELAPSED_MS=%.3f" sw.Elapsed.TotalMilliseconds)
    }

if mode = 0 then
    do xakeScript {    // variant "none": no declarations
        consolelog Silent
        filelog "build.log" Diag
        phony "main" target
    }
else
    do xakeScript {
        consolelog Silent
        filelog "build.log" Diag
        var "TOKEN1" [Secret]
        var "TOKEN2" [Secret]
        var "TOKEN3" [Secret]
        var "TOKEN4" [Secret]
        var "TOKEN5" [Secret]
        phony "main" target
    }
