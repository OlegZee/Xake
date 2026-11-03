namespace Tests

open System.IO
open System.Threading.Tasks
open NUnit.Framework

open Xake
open Xake.BuildLog
open Xake.Storage

/// An exception inside a MailboxProcessor loop used to kill the agent silently, leaving every
/// later request waiting forever at 0 % CPU. These tests inject such a failure and require the
/// error to be logged and the build to fail (or the agent to keep answering) within a timeout.
[<TestFixture>]
type AgentFailureTests() =
    inherit XakeTestBase("agentfail")

    let timeoutMs = 30000

    [<Test>]
    member x.``pool agent failure fails the build instead of hanging``() =
        let logged = System.Collections.Concurrent.ConcurrentQueue<string>()
        // The pool agent logs "Task queued" from inside its loop: make exactly that throw.
        let write (s: string) =
            if s.Contains "Task queued" then failwith "injected logger failure"
            logged.Enqueue s
        let options =
            { x.TestOptions with
                ThrowOnError = true; FileLog = ""; NoPersist = true
                CustomLogger = CustomLogger (fun _ -> true) write }

        let build = Task.Run(fun () ->
            do xake options {
                phony "main" (recipe { return () })
            })

        let completed = try build.Wait(timeoutMs) with _ -> true
        Assert.IsTrue(completed, "the build hangs")
        Assert.IsTrue(build.IsFaulted, "the build must fail")
        let errors = logged |> Seq.filter (fun s -> s.Contains "WorkerPool agent failed to schedule 'main'") |> List.ofSeq
        Assert.IsNotEmpty(errors, sprintf "no agent error logged; got %A" (List.ofSeq logged))
        StringAssert.Contains("[ERROR]", errors.Head)
        StringAssert.Contains("injected logger failure", errors.Head)

    [<Test>]
    member x.``database agent survives a failed store and the file stays readable``() =
        let logged = System.Collections.Concurrent.ConcurrentQueue<string>()
        let logger = CustomLogger (fun _ -> true) logged.Enqueue
        let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-agentfail-" + string (System.Guid.NewGuid()))
        Directory.CreateDirectory dir |> ignore
        let dbpath = dir </> ".xake"
        let mkTarget name = name |> File.make |> FileTarget
        let get (db: Agent<DatabaseApi>) t = db.PostAndReply((fun ch -> GetResult(t, ch)), timeoutMs)
        try
            let db = openDb dbpath logger
            let good1 = { makeResult [mkTarget "good1.dll"] with Depends = [ Var("A", Some "1") ] }
            // a null variable name makes the pickler throw (BinaryWriter.Write(null: string))
            // half way through the record
            let bad = { makeResult [mkTarget "bad.dll"] with Depends = [ Var("B", Some "2"); Var(null, None) ] }
            let good2 = { makeResult [mkTarget "good2.dll"] with Depends = [ Var("C", Some "3") ] }
            Store good1 |> db.Post
            Store bad |> db.Post
            Store good2 |> db.Post

            Assert.IsTrue((get db (mkTarget "bad.dll")).IsSome, "the result is kept in memory for this run")
            db.PostAndReply(CloseWait, timeoutMs)

            let errors = logged |> Seq.filter (fun s -> s.Contains "Database agent failed to store the result of 'bad.dll'") |> List.ofSeq
            Assert.IsNotEmpty(errors, sprintf "no agent error logged; got %A" (List.ofSeq logged))
            StringAssert.Contains("[ERROR]", errors.Head)
            Assert.IsTrue(logged |> Seq.exists (fun s -> s.Contains "[WARN]" && s.Contains "Database not fully saved: 1 result(s)"),
                          sprintf "no warning at close; got %A" (List.ofSeq logged))

            // reopen: the good records read back, the failed one left nothing in the file
            logged.Clear()
            let db2 = openDb dbpath logger
            Assert.AreEqual(Some good1.Depends, get db2 (mkTarget "good1.dll") |> Option.map (fun r -> r.Depends))
            Assert.AreEqual(Some good2.Depends, get db2 (mkTarget "good2.dll") |> Option.map (fun r -> r.Depends))
            Assert.IsTrue((get db2 (mkTarget "bad.dll")).IsNone)
            db2.PostAndReply(CloseWait, timeoutMs)
            Assert.IsFalse(logged |> Seq.exists (fun s -> s.Contains "Failed to read database"), sprintf "database unreadable: %A" (List.ofSeq logged))
        finally
            try Directory.Delete(dir, true) with _ -> ()
