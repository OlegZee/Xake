#r "../../../../src/core/bin/Release/netstandard2.0/Xake.dll"  // run from a bench folder with src/ generated
open Xake
let dir = System.IO.Directory.GetCurrentDirectory()
let files = [| for i in 0 .. 9999 -> File.make (dir + sprintf "/src/s%d.txt" i) |]
let N = 178200
let pick k = files.[(k * 7919) % 4433]
let time name f =
    f 1000 |> ignore
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let r = f N
    printfn "%-40s %6d ms  %6.2f us/op" name sw.ElapsedMilliseconds (float sw.Elapsed.TotalMilliseconds * 1000.0 / float N)
    r
let map = files |> Array.map (fun f -> f, 1) |> Map.ofArray
time "Map<File,_>.tryFind (10k)" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do if (Map.tryFind (pick k) map).IsSome then c <- c + 1); c) |> ignore
let hs = System.Collections.Generic.HashSet<File>(files)
time "HashSet<File>.Add (present)" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do if hs.Add (pick k) then c <- c + 1); c) |> ignore
let dict = System.Collections.Generic.Dictionary<File,int>()
for f in files do dict.[f] <- 1
time "Dictionary<File,_>.TryGetValue" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do if dict.ContainsKey (pick k) then c <- c + 1); c) |> ignore
time "File.Exists (1 stat)" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do if File.exists (pick k) then c <- c + 1); c) |> ignore
time "FileInfo: Exists+LastWriteTime+Length" (fun n -> let mutable c = 0L in (for k in 0 .. n-1 do let fi = File.getFileInfo (pick k) in (if fi.Exists then c <- c + fi.Length + fi.LastWriteTime.Ticks)); c) |> ignore
time "4.0: File.Exists + GetLastWriteTime" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do let f = File.getSysFileName (pick k) in if System.IO.File.Exists f && System.IO.File.GetLastWriteTime f > System.DateTime.MinValue then c <- c + 1); c) |> ignore
let agent = MailboxProcessor<AsyncReplyChannel<int>>.Start(fun mb -> let rec loop () = async { let! ch = mb.Receive() in ch.Reply 1; return! loop () } in loop ())
time "agent round trip: PostAndReply" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do c <- c + agent.PostAndReply id); c) |> ignore
time "agent: PostAndAsyncReply+RunSynchronously" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do c <- c + (agent.PostAndAsyncReply id |> Async.RunSynchronously)); c) |> ignore
time "async { return } + RunSynchronously" (fun n -> let mutable c = 0 in (for k in 0 .. n-1 do c <- c + (async { return 1 } |> Async.RunSynchronously)); c) |> ignore
