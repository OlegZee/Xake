namespace Tests

open System.IO
open NUnit.Framework

open Xake

/// Rule matching cost: `locateRule` runs every rule pattern against every `need`ed target.
[<TestFixture>]
type RuleMatchTests() =
    inherit XakeTestBase("rulematch")

    let noop = Recipe (fun (r, _) -> async { return r, () })

    let mkRules n =
        [ for i in 1..n -> FileRule (sprintf "out/r%i/(cfg:*)/(name:*).dll" i, noop) ]
        @ [ FileRule ("out/(cfg:*)/(fwk:*)/(name:*).dll", noop) ]
        |> Rules

    [<Test>]
    member x.``locateRule finds the first matching rule and its groups``() =
        let root = x.TestOptions.ProjectRoot
        let rules = mkRules 3
        let target = root </> "out" </> "Debug" </> "net472" </> "A.B.dll" |> File.make |> FileTarget
        match ExecCore.locateRule rules root target with
        | Some (FileRule (pattern, _), groups, _) ->
            Assert.AreEqual("out/(cfg:*)/(fwk:*)/(name:*).dll", pattern)
            let groups = Map.ofList groups
            Assert.AreEqual("Debug", groups.["cfg"])
            Assert.AreEqual("net472", groups.["fwk"])
            Assert.AreEqual("A.B", groups.["name"])
        | other -> Assert.Fail(sprintf "unexpected %A" other)

        let other = root </> "src" </> "a.cs" |> File.make |> FileTarget
        Assert.IsTrue(ExecCore.locateRule rules root other |> Option.isNone)

    /// 30 rules x 5000 targets, none of them matching (the common case for plain inputs).
    [<Test; Explicit("benchmark")>]
    member x.``bench locateRule 30 rules x 5000 targets``() =
        let root = x.TestOptions.ProjectRoot
        let rules = mkRules 29
        let targets = [| for i in 1..5000 -> root </> "src" </> sprintf "p%i" (i % 50) </> sprintf "f%i.cs" i |> File.make |> FileTarget |]
        for _ in 1..3 do
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let found = targets |> Array.choose (ExecCore.locateRule rules root) |> Array.length
            sw.Stop()
            Assert.AreEqual(0, found)
            TestContext.Progress.WriteLine (sprintf "BENCH locateRule 30x5000: %d ms" sw.ElapsedMilliseconds)

    /// AR-like: a script with the `out/(cfg:*)/(fwk:*)/(name:*).dll` rule needing 3000 plain files.
    [<Test; Explicit("benchmark")>]
    member x.``bench script needing 3000 files``() =
        let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-rulematch-" + string (System.Guid.NewGuid()))
        Directory.CreateDirectory dir |> ignore
        let files = [ for i in 1..3000 -> dir </> sprintf "f%i.cs" i ]
        for f in files do File.WriteAllText(f, "x")
        try
          for _ in 1..3 do
              let sw = System.Diagnostics.Stopwatch.StartNew()
              do xake {x.TestOptions with NoPersist = true; Threads = 4; ConLogLevel = Silent; FileLogLevel = Silent; Progress = false} {
                  rules [
                      "main" => recipe { do! needFiles (Filelist (files |> List.map File.make)) }
                      "out/(cfg:*)/(fwk:*)/(name:*).dll" ..> recipe { return () }
                      "out/(cfg:*)/(fwk:*)/(name:*).xml" ..> recipe { return () }
                      "obj/resx/(b:*)/(name:*)/**/*.resources" ..> recipe { return () }
                      "out/(cfg:*)/(fwk:*)/(name:*).pdb" ..> recipe { return () }
                      "obj/(name:*)/*.g.cs" ..> recipe { return () }
                  ]
              }
              sw.Stop()
              TestContext.Progress.WriteLine (sprintf "BENCH script need 3000 files: %d ms" sw.ElapsedMilliseconds)
        finally
            try Directory.Delete(dir, true) with _ -> ()
