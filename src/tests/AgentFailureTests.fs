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
    member x.``database agent survives a failed store``() =
        let logged = System.Collections.Concurrent.ConcurrentQueue<string>()
        let logger = CustomLogger (fun _ -> true) logged.Enqueue
        let dbpath = x.TestOptions.ProjectRoot </> ".xake-agentfail"
        try File.Delete dbpath with _ -> ()

        let db = openDb dbpath logger
        let target = "bad.dll" |> File.make |> FileTarget
        // a null variable name makes the pickler throw (BinaryWriter.Write(null: string))
        let bad = { makeResult [target] with Depends = [ Var(null, None) ] }
        Store bad |> db.Post

        let found = db.PostAndReply((fun ch -> GetResult(target, ch)), timeoutMs)
        Assert.IsTrue(found.IsSome, "the result is kept in memory for this run")
        db.PostAndReply(CloseWait, timeoutMs)

        let errors = logged |> Seq.filter (fun s -> s.Contains "Database agent failed to store the result of 'bad.dll'") |> List.ofSeq
        Assert.IsNotEmpty(errors, sprintf "no agent error logged; got %A" (List.ofSeq logged))
        StringAssert.Contains("[ERROR]", errors.Head)
