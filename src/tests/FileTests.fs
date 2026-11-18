namespace Tests

open System.IO
open NUnit.Framework

open Xake

/// `File` in 5.0: the normalised absolute path, ordinal on every OS (decision 1a).
[<TestFixture>]
type ``File type``() =
    inherit XakeTestBase("filetype")

    static member UnixCases : obj[][] = [|
        [| "/a/b/c.txt"; "/a/b/c.txt" |]
        [| "/a/./b/c.txt"; "/a/b/c.txt" |]
        [| "/a/x/../b/c.txt"; "/a/b/c.txt" |]
        [| "/a//b///c.txt"; "/a/b/c.txt" |]
        [| "/a/b/"; "/a/b" |]
        [| "/"; "/" |]
        [| "/A/b/C.txt"; "/A/b/C.txt" |]
        [| @"/a\b.txt"; @"/a\b.txt" |]
        [| "//srv/share/a"; "/srv/share/a" |]
        [| "/../a"; "/a" |]
        [| "../a/../b"; "../b" |]
    |]

    static member WindowsCases : obj[][] = [|
        [| @"C:\a\b\c.txt"; "C:/a/b/c.txt" |]
        [| @"C:\a\.\x\..\b\"; "C:/a/b" |]
        [| @"C:\"; "C:/" |]
        [| @"C:\.."; "C:/" |]
        [| @"C:\..\a"; "C:/a" |]
        [| "C://a"; "C:/a" |]
        [| @"C:\\a\b"; "C:/a/b" |]
        [| "a//b"; "a/b" |]
        [| @"c:\A\b"; "C:/A/b" |]
        [| "c:"; "C:" |]
        [| @"\\srv\share\a.txt"; "//srv/share/a.txt" |]
        [| @"\\srv\share\x\..\a.txt"; "//srv/share/a.txt" |]
        [| "C:/a//b"; "C:/a/b" |]
    |]

    [<TestCaseSource("UnixCases")>]
    member _.``collapse: Unix rules``(input: string, expected: string) =
        Assert.AreEqual(expected, Path.collapseWith false input)

    [<TestCaseSource("WindowsCases")>]
    member _.``collapse: Windows rules``(input: string, expected: string) =
        Assert.AreEqual(expected, Path.collapseWith true input)

    [<Test>]
    member x.``make gives the absolute path, Name with '/'``() =
        let root = x.TestOptions.ProjectRoot
        let f = File.make "sub/../a.txt"
        Assert.AreEqual((Path.collapse (Directory.GetCurrentDirectory() </> "a.txt")), f.Name)
        Assert.IsFalse(f.Name.Contains "\\")
        Assert.AreEqual(Path.GetFullPath(root </> "a.txt"), File.getSysFileName f)
        Assert.AreEqual(Path.GetFullPath(root </> "a.txt"), (File.getFileInfo f).FullName)
        Assert.AreEqual(File.make (root </> "a.txt"), f)
        Assert.Throws<exn>(fun () -> File.make "  " |> ignore) |> ignore

    [<Test>]
    member _.``display: relative under the root, absolute outside``() =
        let root = if Env.isUnix then "/proj" else @"C:\proj"
        let abs (p: string) = if Env.isUnix then p else "C:" + p
        let show = File.display root
        Assert.AreEqual("out/a.dll", show (File.make (abs "/proj/out/a.dll")))
        Assert.AreEqual("a.dll", show (File.make (abs "/proj/a.dll")))
        Assert.AreEqual(Path.collapse (abs "/projx/a.dll"), show (File.make (abs "/projx/a.dll")))
        Assert.AreEqual(Path.collapse (abs "/other/a.dll"), show (File.make (abs "/other/a.dll")))
        Assert.AreEqual("a.dll", File.display (abs "/proj/") (File.make (abs "/proj/a.dll")))
        Assert.AreEqual(Path.collapse (abs "/proj2/a.c"), show (File.make (abs "/proj2/a.c")), "the root prefix ends on a segment boundary")

    [<Test>]
    member _.``Equals, GetHashCode and CompareTo agree on case variants``() =
        let rnd = System.Random 42
        let variant (s: string) =
            s |> String.map (fun c -> if rnd.Next 2 = 0 then System.Char.ToUpperInvariant c else System.Char.ToLowerInvariant c)
        let names = [ for _ in 1 .. 200 -> "/p/" + variant "abc/Def.txt" ]
        let files = names |> List.map File.make
        for a in files do
            for b in files do
                let eq = a.Equals b
                let cmp = (a :> System.IComparable).CompareTo b
                Assert.AreEqual(eq, (cmp = 0))
                Assert.AreEqual(eq, (a.Name = b.Name))
                if eq then Assert.AreEqual(a.GetHashCode(), b.GetHashCode())
                Assert.AreEqual(sign cmp, -(sign ((b :> System.IComparable).CompareTo a)))
        Assert.AreEqual(names |> List.distinct |> List.length, files |> Set.ofList |> Set.count)
        Assert.AreSame(File.comparer, System.StringComparer.Ordinal)

    [<Test>]
    member _.``case variants are distinct files and targets``() =
        let a, b = File.make "/p/out/A.dll", File.make "/p/out/a.dll"
        Assert.AreNotEqual(a, b)
        Assert.AreEqual(2, Set.ofList [ a; b ] |> Set.count)
        Assert.AreEqual(2, Set.ofList [ FileTarget a; FileTarget b ] |> Set.count)
        Assert.AreEqual(2, Set.ofList [ PhonyAction "Main"; PhonyAction "main" ] |> Set.count)
        Assert.AreEqual(FileTarget (File.make "/p/out/./A.dll"), FileTarget a)

    [<Test>]
    member _.``rule lookup is ordinal``() =
        let noop = Recipe (fun (r, _) -> async { return r, () })
        let rules = Rules [ FileRule (["out/(n:*).dll"], RuleHeader.Empty, noop); FileRule (["Src/*.cs"], RuleHeader.Empty, noop) ]
        let root = if Env.isUnix then "/p" else @"C:\p"
        let t (p: string) = root </> p |> File.make |> FileTarget
        Assert.IsTrue(ExecCore.locateRule rules root (t "out/A.dll") |> Option.isSome)
        Assert.IsTrue(ExecCore.locateRule rules root (t "OUT/A.dll") |> Option.isNone)
        Assert.IsTrue(ExecCore.locateRule rules root (t "Src/a.cs") |> Option.isSome)
        Assert.IsTrue(ExecCore.locateRule rules root (t "src/a.cs") |> Option.isNone)

    [<Test>]
    member _.``the worker pool runs case variants as two tasks``() =
        let scheduler = WorkerPool.Scheduler.create (CustomLogger (fun _ -> false) ignore) 2
        let runs = ref 0
        let run (t: Target) =
            let action = async { System.Threading.Interlocked.Increment runs |> ignore; return 1 }
            let wait = (WorkerPool.Scheduler.pool scheduler).PostAndReply(fun ch -> WorkerPool.Run(t, [ t ], action, ch))
            wait |> Async.RunSynchronously
        run (FileTarget (File.make "/p/A.txt")) |> ignore
        run (FileTarget (File.make "/p/a.txt")) |> ignore
        run (FileTarget (File.make "/p/a.txt")) |> ignore
        Assert.AreEqual(2, runs.Value)

/// The two-spellings check (decision 1a).
[<TestFixture>]
type ``Two spellings``() =
    inherit XakeTestBase("spellings")

    member x.Options (log: System.Collections.Generic.List<string>) =
        { x.TestOptions with ThrowOnError = true; FileLog = ""
                             CustomLogger = CustomLogger (fun l -> l = Level.Error || l = Level.Warning) (fun s -> lock log (fun () -> log.Add s)) }

    [<Test>]
    member x.``two targets named two ways fail, naming both``() =
        let log = System.Collections.Generic.List<string>()
        Assert.Throws<XakeException>(fun () ->
            xake (x.Options log) {
                rules [
                    "main" => recipe {
                        do! need [ "out/Foo.txt" ]
                        do! need [ "out/foo.txt" ]
                    }
                    "out/(n:*).txt" ..> recipe { () }
                ]
            }) |> ignore
        Assert.IsTrue(log.Exists(fun s -> s.Contains "target 'out/Foo.txt' is also named 'out/foo.txt'"), sprintf "%A" (List.ofSeq log))

    [<Test>]
    member x.``a file found under two spellings warns once``() =
        let log = System.Collections.Generic.List<string>()
        File.WriteAllText("src.txt", "")
        xake (x.Options log) {
            rules [
                "main" => recipe {
                    let! _ = getFiles (fileset { includes "src.txt"; includes "SRC.txt" })
                    let! _ = getFiles (fileset { includes "SRC.txt" })
                    ()
                }
            ]
        }
        let warnings = log.FindAll(fun s -> s.Contains "file 'src.txt' is also named 'SRC.txt'")
        if Env.isUnix then Assert.AreEqual(1, warnings.Count, sprintf "%A" (List.ofSeq log))
        else Assert.LessOrEqual(warnings.Count, 1)

    [<Test>]
    member x.``a.cs and A.cs found by one fileset warn on a case-sensitive volume``() =
        Directory.CreateDirectory "cs" |> ignore
        File.WriteAllText("cs/a.cs", "")
        File.WriteAllText("cs/A.cs", "")
        if Directory.GetFiles "cs" |> Array.length < 2 then Assert.Ignore "the volume ignores case"
        let log = System.Collections.Generic.List<string>()
        xake (x.Options log) {
            rules [ "main" => recipe { let! _ = getFiles (fileset { includes "cs/*.cs" }) in () } ]
        }
        Assert.IsTrue(log.Exists(fun s -> s.Contains "is also named"), sprintf "%A" (List.ofSeq log))
