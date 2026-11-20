// Mailbox throughput: how many "give me the state of this file" requests an agent like the
// database agent answers per second, by calling pattern. Run from a bench folder with src/
// generated (`mksrc`), after `dotnet build src/core -c Release`:
//   dotnet fsi ../../../docs/features/idle-perf/bench/mailbox.fsx
#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
open Xake
open System
open System.Threading
open System.Threading.Tasks

let dir = IO.Directory.GetCurrentDirectory()
let files = [| for i in 0 .. 9999 -> File.make (dir + sprintf "/src/s%d.txt" i) |]
let N = 180_000
let pick k = files.[(k * 7919) % 4433]

type Info = { Stamp: int64; Size: int64; Changed: int }
type Msg =
    | Get of File * AsyncReplyChannel<Info option>
    | GetStat of File * AsyncReplyChannel<Info option>
    | GetMany of File[] * AsyncReplyChannel<Info option[]>
    | Put of File * Info
    | Ping of AsyncReplyChannel<unit>

let table0 = files |> Array.map (fun f -> f, { Stamp = 1L; Size = 2L; Changed = 3 }) |> Map.ofArray

/// An agent like Xake's: an immutable Map threaded through the loop.
let startAgent () =
    MailboxProcessor<Msg>.Start(fun mb ->
        let rec loop (table: Map<File, Info>) = async {
            let! msg = mb.Receive()
            match msg with
            | Get (f, ch) -> ch.Reply (Map.tryFind f table); return! loop table
            | GetStat (f, ch) -> ch.Reply (if File.exists f then Map.tryFind f table else None); return! loop table
            | GetMany (fs, ch) -> ch.Reply (fs |> Array.map (fun f -> Map.tryFind f table)); return! loop table
            | Put (f, i) -> return! loop (Map.add f i table)
            | Ping ch -> ch.Reply (); return! loop table }
        loop table0)

/// The same service on its own thread: a blocking queue, replies through TaskCompletionSource.
type ThreadAgent() =
    let q = new Collections.Concurrent.BlockingCollection<File * TaskCompletionSource<Info option>>()
    let table = Collections.Generic.Dictionary<File, Info>()
    do for f in files do table.[f] <- { Stamp = 1L; Size = 2L; Changed = 3 }
    let t = Thread((fun () ->
                for (f, tcs) in q.GetConsumingEnumerable() do
                    tcs.SetResult (match table.TryGetValue f with | true, i -> Some i | _ -> None)), IsBackground = true)
    do t.Start()
    member _.Get f =
        let tcs = TaskCompletionSource<Info option>(TaskCreationOptions.RunContinuationsAsynchronously)
        q.Add((f, tcs)); tcs.Task

let run name (requests: int) (filesPerRequest: int) (body: unit -> unit) =
    body ()   // warm-up pass is the body itself on a smaller N would complicate; one full pass first
    GC.Collect(); GC.WaitForPendingFinalizers()
    let g0 = GC.CollectionCount 0
    let a0 = GC.GetTotalAllocatedBytes true
    let sw = Diagnostics.Stopwatch.StartNew()
    body ()
    let el = sw.Elapsed.TotalSeconds
    let alloc = GC.GetTotalAllocatedBytes true - a0
    printfn "%-58s %9.2f us/req %10.0f req/s %11.0f files/s %6d B/req  gen0 %d"
        name (el * 1e6 / float requests) (float requests / el) (float (requests * filesPerRequest) / el)
        (alloc / int64 requests) (GC.CollectionCount 0 - g0)

let a = startAgent ()

printfn "Requests for the state of one file (an in-memory Map of 10,000 files); %d per pass; %d cores" N Environment.ProcessorCount
printfn ""
printfn "-- one caller, waits for every reply (the verdict today)"
run "PostAndReply" N 1 (fun () -> for k in 0 .. N-1 do a.PostAndReply(fun ch -> Get (pick k, ch)) |> ignore)
run "PostAndAsyncReply |> Async.RunSynchronously (Xake today)" N 1 (fun () ->
    for k in 0 .. N-1 do a.PostAndAsyncReply(fun ch -> Get (pick k, ch)) |> Async.RunSynchronously |> ignore)
run "  same, agent also does File.Exists" N 1 (fun () ->
    for k in 0 .. N-1 do a.PostAndAsyncReply(fun ch -> GetStat (pick k, ch)) |> Async.RunSynchronously |> ignore)
run "one async loop awaiting each reply (no RunSynchronously)" N 1 (fun () ->
    async { for k in 0 .. N-1 do
                let! _ = a.PostAndAsyncReply(fun ch -> Get (pick k, ch)) in () } |> Async.RunSynchronously)

printfn ""
printfn "-- many callers at once (the agent's queue is never empty)"
for callers in [ 4; 16; 64 ] do
    run (sprintf "%d async callers, each awaiting its replies" callers) N 1 (fun () ->
        [ for c in 0 .. callers-1 -> async {
            for k in c .. callers .. N-1 do
                let! _ = a.PostAndAsyncReply(fun ch -> Get (pick k, ch)) in () } ]
        |> Async.Parallel |> Async.RunSynchronously |> ignore)
run "all requests at once: Async.Parallel of 150 per group" N 1 (fun () ->
    [ for t in 0 .. N/150-1 -> async {
        let! _ = [ for j in 0 .. 149 -> a.PostAndAsyncReply(fun ch -> Get (pick (t*150+j), ch)) ] |> Async.Parallel
        return () } ]
    |> Async.Parallel |> Async.RunSynchronously |> ignore)
run "  same, agent also does File.Exists (the full build today)" N 1 (fun () ->
    [ for t in 0 .. N/150-1 -> async {
        let! _ = [ for j in 0 .. 149 -> a.PostAndAsyncReply(fun ch -> GetStat (pick (t*150+j), ch)) ] |> Async.Parallel
        return () } ]
    |> Async.Parallel |> Async.RunSynchronously |> ignore)

printfn ""
printfn "-- the agent's own rate: posts without replies, then one reply"
run "Post (no reply), then Ping" N 1 (fun () ->
    for k in 0 .. N-1 do a.Post (Put (pick k, { Stamp = 1L; Size = 2L; Changed = 3 }))
    a.PostAndReply Ping)

printfn ""
printfn "-- batches: one request carries several files"
for size in [ 10; 150; 1000 ] do
    let reqs = N / size
    run (sprintf "one caller, batch of %d, RunSynchronously" size) reqs size (fun () ->
        for r in 0 .. reqs-1 do
            let fs = Array.init size (fun j -> pick (r*size+j))
            a.PostAndAsyncReply(fun ch -> GetMany (fs, ch)) |> Async.RunSynchronously |> ignore)
run "16 async callers, batch of 150" (N/150) 150 (fun () ->
    [ for c in 0 .. 15 -> async {
        for r in c .. 16 .. N/150-1 do
            let fs = Array.init 150 (fun j -> pick (r*150+j))
            let! _ = a.PostAndAsyncReply(fun ch -> GetMany (fs, ch)) in () } ]
    |> Async.Parallel |> Async.RunSynchronously |> ignore)

printfn ""
printfn "-- the same service on its own thread (BlockingCollection, Dictionary)"
let ta = ThreadAgent()
run "one caller, blocking wait (.Result)" N 1 (fun () -> for k in 0 .. N-1 do ta.Get(pick k).Result |> ignore)
run "16 async callers, awaiting" N 1 (fun () ->
    [ for c in 0 .. 15 -> async {
        for k in c .. 16 .. N-1 do
            let! _ = ta.Get(pick k) |> Async.AwaitTask in () } ]
    |> Async.Parallel |> Async.RunSynchronously |> ignore)

printfn ""
printfn "-- no agent: the lookup itself, one thread"
run "Map.tryFind" N 1 (fun () -> for k in 0 .. N-1 do Map.tryFind (pick k) table0 |> ignore)
run "File.Exists" N 1 (fun () -> for k in 0 .. N-1 do File.exists (pick k) |> ignore)
