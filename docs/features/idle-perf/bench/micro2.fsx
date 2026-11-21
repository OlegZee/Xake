#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"  // run from a bench folder with src/ generated
open Xake
let dir = System.IO.Directory.GetCurrentDirectory()
let files = [| for i in 0 .. 9999 -> File.make (dir + sprintf "/src/s%d.txt" i) |]
let N = 178200
let pick k = files.[(k * 7919) % 4433]
let measure name (f: int -> unit) =
    for k in 0 .. 999 do f k
    let g0 = System.GC.CollectionCount 0
    let a0 = System.GC.GetTotalAllocatedBytes false
    let sw = System.Diagnostics.Stopwatch.StartNew()
    for k in 0 .. N-1 do f k
    let a = System.GC.GetTotalAllocatedBytes false - a0
    printfn "%-48s %6.2f us/op  %5d B/op  gen0 %d" name (sw.Elapsed.TotalMilliseconds * 1000.0 / float N) (a / int64 N) (System.GC.CollectionCount 0 - g0)
// round trip patterns
let agent = MailboxProcessor<AsyncReplyChannel<int>>.Start(fun mb -> let rec loop () = async { let! ch = mb.Receive() in ch.Reply 1; return! loop () } in loop ())
measure "PostAndReply (trivial agent)" (fun _ -> agent.PostAndReply id |> ignore)
measure "PostAndAsyncReply |> RunSynchronously" (fun _ -> agent.PostAndAsyncReply id |> Async.RunSynchronously |> ignore)
measure "async{let! x = PostAndAsyncReply} |> RunSync" (fun _ -> async { let! x = agent.PostAndAsyncReply id in return x } |> Async.RunSynchronously |> ignore)
measure "async { return 1 } |> RunSynchronously" (fun _ -> async { return 1 } |> Async.RunSynchronously |> ignore)
// a stat inside an agent fed one message at a time, as the database agent is
let statTicks = ref 0L
let statAgent =
    MailboxProcessor<File * AsyncReplyChannel<bool>>.Start(fun mb ->
        let rec loop () = async {
            let! (f, ch) = mb.Receive()
            let t = System.Diagnostics.Stopwatch.GetTimestamp()
            let e = File.exists f
            statTicks.Value <- statTicks.Value + (System.Diagnostics.Stopwatch.GetTimestamp() - t)
            ch.Reply e
            return! loop () }
        loop ())
measure "PostAndReply to an agent doing File.exists" (fun k -> statAgent.PostAndReply(fun ch -> pick k, ch) |> ignore)
printfn "%-48s %6.2f us/op" "  of which File.exists inside the agent" (float statTicks.Value * 1e6 / float System.Diagnostics.Stopwatch.Frequency / float (N + 1000))
measure "File.exists on the calling thread" (fun k -> File.exists (pick k) |> ignore)
