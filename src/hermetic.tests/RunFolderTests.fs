namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// `RunFolder.plan` and `RunFolder.apply` over files of a scratch folder: precedence, links,
/// a no-op, stale files, never writing into a placed or a foreign file.
[<TestFixture>]
type ``Run folder placing``() =
    inherit XakeTestBase("run-folder-placing")

    let write (path: string) (text: string) =
        Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath path)) |> ignore
        File.WriteAllText (path, text)

    let fresh (dir: string) =
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory dir |> ignore

    [<Test>]
    member _.``the root's own files win, the closure keeps the higher assembly version, conflicts are listed``() =
        let versions = dict [ "/p/a/1.0/X.dll", Version (1, 0); "/p/a/2.0/X.dll", Version (2, 0); "/p/b/X.dll", Version (1, 5) ]
        let versionOf (s: string) = match versions.TryGetValue s with | true, v -> Some v | _ -> None
        let c own src (To: string) : RunFolder.Candidate = { To = To; Source = src; Own = own }
        let files, conflicts =
            RunFolder.plan versionOf (fun _ _ -> false)
                [ c true "/out/App.dll" "App.dll"
                  c true "/p/a/1.0/X.dll" "X.dll"          // the root's own: kept, though lower
                  c false "/p/a/2.0/X.dll" "X.dll"
                  c false "/p/b/Y.dll" "Y.dll"
                  c false "/p/a/1.0/Y.dll" "y.dll"         // one path ignoring case
                  c false "/p/a/2.0/Z.dll" "Z.dll"
                  c false "/p/b/X.dll" "sub/X.dll"
                  c false "/p/a/2.0/X.dll" "sub/X.dll"     // higher assembly version replaces
                  c false "/out/App.dll" "App.dll" ]       // the same source: no conflict
        Assert.That(files, Is.EqualTo [ "App.dll", "/out/App.dll"; "X.dll", "/p/a/1.0/X.dll"; "Y.dll", "/p/b/Y.dll"
                                        "Z.dll", "/p/a/2.0/Z.dll"; "sub/X.dll", "/p/a/2.0/X.dll" ])
        Assert.That(conflicts |> List.map (fun c -> c.Path, c.Kept, c.Dropped),
                    Is.EqualTo [ "X.dll", "/p/a/1.0/X.dll", "/p/a/2.0/X.dll"
                                 "y.dll", "/p/b/Y.dll", "/p/a/1.0/Y.dll"
                                 "sub/X.dll", "/p/a/2.0/X.dll", "/p/b/X.dll" ])
        Assert.That(conflicts.[0].Reason, Is.EqualTo "the root's own")
        Assert.That(conflicts.[2].Reason, Does.StartWith "assembly version 2.0 > 1.5")
        // the same content under two sources is no conflict
        let _, none = RunFolder.plan versionOf (fun _ _ -> true) [ c true "/a/X.dll" "X.dll"; c false "/b/X.dll" "X.dll" ]
        Assert.That(none, Is.Empty)

    [<Test>]
    member _.``placing links files, a no-op touches nothing, a stale file goes, a changed source is placed again``() =
        fresh "src"
        fresh "run"
        write "src/a.dll" "a"
        write "src/b/c.txt" "c"
        write "src/d.txt" "d"
        let manifest = Path.GetFullPath "run/app/.xake-run.txt"
        let full p = Path.GetFullPath p
        let files = [ "a.dll", full "src/a.dll"; "b/c.txt", full "src/b/c.txt"; "d.txt", full "src/d.txt" ]
        let s1 = RunFolder.apply RunFolder.Link manifest [ "# test" ] ignore files
        Assert.That((s1.Placed, s1.Kept, s1.Removed, s1.Written), Is.EqualTo ((3, 0, 0, true)))
        Assert.That(s1.Modes, Is.EqualTo [ "link", 3 ])
        Assert.That(RunFolder.fileId "run/app/a.dll", Is.EqualTo (RunFolder.fileId "src/a.dll"))
        Assert.That(RunFolder.fileId "run/app/a.dll", Is.Not.EqualTo (RunFolder.fileId "src/d.txt"))
        Assert.That(File.ReadAllText manifest, Does.Contain (sprintf "link\tb/c.txt\t%s" (full "src/b/c.txt")))
        // a no-op: nothing placed, the manifest not written
        let stamp = File.GetLastWriteTimeUtc manifest
        let ids = files |> List.map (fun (p, _) -> RunFolder.fileId ("run/app/" + p))
        let s2 = RunFolder.apply RunFolder.Link manifest [ "# test" ] ignore files
        Assert.That((s2.Placed, s2.Kept, s2.Removed, s2.Written), Is.EqualTo ((0, 3, 0, false)))
        Assert.That(File.GetLastWriteTimeUtc manifest, Is.EqualTo stamp)
        Assert.That(files |> List.map (fun (p, _) -> RunFolder.fileId ("run/app/" + p)), Is.EqualTo ids)
        // a source replaced by a new file (a new inode): unlinked and linked again; the old
        // file is not written into
        let oldInode = RunFolder.fileId "src/a.dll"
        File.Delete "src/a.dll"
        write "src/a.dll" "a2"
        Assert.That(RunFolder.fileId "src/a.dll", Is.Not.EqualTo oldInode)
        // a path no longer planned is removed with the folders it leaves empty
        let s3 = RunFolder.apply RunFolder.Link manifest [ "# test" ] ignore [ "a.dll", full "src/a.dll"; "d.txt", full "src/d.txt" ]
        Assert.That((s3.Placed, s3.Kept, s3.Removed), Is.EqualTo ((1, 1, 1)))
        Assert.That(File.ReadAllText "run/app/a.dll", Is.EqualTo "a2")
        Assert.That(File.Exists "run/app/b/c.txt", Is.False)
        Assert.That(Directory.Exists "run/app/b", Is.False)

    [<Test>]
    member _.``a copy keeps the source's time and is placed again when the source changes; a foreign file is never overwritten``() =
        fresh "src2"
        fresh "run2"
        write "src2/a.dll" "a"
        write "src2/e.txt" "planned"
        write "run2/app/e.txt" "the user's"          // not placed by a run
        let manifest = Path.GetFullPath "run2/app/.xake-run.txt"
        let files = [ "a.dll", Path.GetFullPath "src2/a.dll"; "e.txt", Path.GetFullPath "src2/e.txt" ]
        let checkedFiles = ResizeArray<string> ()
        let s1 = RunFolder.apply RunFolder.Copy manifest [] checkedFiles.Add files
        Assert.That(s1.Modes, Is.EqualTo [ "copy", 1 ])
        Assert.That(s1.Foreign, Is.EqualTo [ "e.txt" ])
        Assert.That(File.ReadAllText "run2/app/e.txt", Is.EqualTo "the user's")
        Assert.That(File.ReadAllText manifest, Does.Not.Contain "e.txt")
        Assert.That(List.ofSeq checkedFiles, Is.EqualTo [ Path.GetFullPath "src2/a.dll" ])
        Assert.That(RunFolder.fileId "run2/app/a.dll", Is.Not.EqualTo (RunFolder.fileId "src2/a.dll"))
        Assert.That(File.GetLastWriteTimeUtc "run2/app/a.dll", Is.EqualTo (File.GetLastWriteTimeUtc "src2/a.dll"))
        // the source rewritten in place: a copy is stale, placed again
        File.WriteAllText ("src2/a.dll", "a-changed")
        File.SetLastWriteTimeUtc ("src2/a.dll", DateTime.UtcNow.AddMinutes 1.0)
        let s2 = RunFolder.apply RunFolder.Copy manifest [] ignore files
        Assert.That((s2.Placed, s2.Kept), Is.EqualTo ((1, 0)))
        Assert.That(File.ReadAllText "run2/app/a.dll", Is.EqualTo "a-changed")
        // a file of the planned content where no run placed it is taken over
        File.WriteAllText ("run2/app/e.txt", "planned")
        let s3 = RunFolder.apply RunFolder.Copy manifest [] ignore files
        Assert.That(s3.Foreign, Is.Empty)
        Assert.That(File.ReadAllText manifest, Does.Contain "e.txt")


    [<Test>]
    member _.``a path outside the run folder is refused, planned or read from the manifest``() =
        fresh "src3"
        fresh "run3"
        write "src3/a.txt" "a"
        let manifest = Path.GetFullPath "run3/app/.xake-run.txt"
        let src = Path.GetFullPath "src3/a.txt"
        for bad in [ "../escape.txt"; "sub/../../escape.txt"; Path.GetFullPath "run3/abs.txt"; "/etc/x.txt" ] do
            let e = Assert.Catch(fun () -> RunFolder.apply RunFolder.Copy manifest [] ignore [ bad, src ] |> ignore)
            Assert.That(e.Message, Does.Contain "a run folder's paths stay inside it", bad)
        Assert.That(File.Exists "run3/escape.txt", Is.False)
        // a folder on the way that is a link to elsewhere
        if not Env.isWindows then
            Directory.CreateDirectory "run3/app" |> ignore
            Directory.CreateDirectory "run3/elsewhere" |> ignore
            Directory.CreateSymbolicLink ("run3/app/linked", Path.GetFullPath "run3/elsewhere") |> ignore
            let e = Assert.Catch(fun () -> RunFolder.apply RunFolder.Copy manifest [] ignore [ "linked/a.txt", src ] |> ignore)
            Assert.That(e.Message, Does.Contain "goes through the link")
            Assert.That(File.Exists "run3/elsewhere/a.txt", Is.False)
        // a hand-edited manifest naming a file outside: refused, the file stays
        write "run3/victim.txt" "keep me"
        write manifest (sprintf "copy\t../victim.txt\t%s\t\n" src)
        Assert.Catch(fun () -> RunFolder.apply RunFolder.Copy manifest [] ignore [ "a.txt", src ] |> ignore) |> ignore
        Assert.That(File.ReadAllText "run3/victim.txt", Is.EqualTo "keep me")

    [<Test>]
    member _.``a file changed since a run placed it is neither removed nor overwritten``() =
        fresh "src4"
        fresh "run4"
        write "src4/a.txt" "a"
        write "src4/b.txt" "b"
        let manifest = Path.GetFullPath "run4/app/.xake-run.txt"
        let a, b = Path.GetFullPath "src4/a.txt", Path.GetFullPath "src4/b.txt"
        RunFolder.apply RunFolder.Copy manifest [] ignore [ "a.txt", a; "b.txt", b ] |> ignore
        // the user (or a test) writes into both placed copies
        File.WriteAllText ("run4/app/a.txt", "the user's a")
        File.WriteAllText ("run4/app/b.txt", "the user's b")
        // b's source changes; a is no longer planned
        File.WriteAllText (b, "b2")
        File.SetLastWriteTimeUtc (b, DateTime.UtcNow.AddMinutes 1.0)
        let s = RunFolder.apply RunFolder.Copy manifest [] ignore [ "b.txt", b ]
        Assert.That(s.Modified, Is.EqualTo [ "a.txt" ])
        Assert.That(s.Foreign, Is.EqualTo [ "b.txt" ])
        Assert.That(s.Removed, Is.EqualTo 0)
        Assert.That(File.ReadAllText "run4/app/a.txt", Is.EqualTo "the user's a")
        Assert.That(File.ReadAllText "run4/app/b.txt", Is.EqualTo "the user's b")
        // an unchanged placed file no longer planned still goes
        write "src4/c.txt" "c"
        let c = Path.GetFullPath "src4/c.txt"
        RunFolder.apply RunFolder.Copy manifest [] ignore [ "c.txt", c ] |> ignore
        let s2 = RunFolder.apply RunFolder.Copy manifest [] ignore []
        Assert.That(s2.Removed, Is.EqualTo 1)
        Assert.That(File.Exists "run4/app/c.txt", Is.False)

    [<Test>]
    member _.``a case-only rename keeps the file where names ignore case``() =
        fresh "src5"
        fresh "run5"
        write "src5/Foo.dll" "foo"
        let manifest = Path.GetFullPath "run5/app/.xake-run.txt"
        let src = Path.GetFullPath "src5/Foo.dll"
        RunFolder.apply RunFolder.Link manifest [] ignore [ "Foo.dll", src ] |> ignore
        let s = RunFolder.apply RunFolder.Link manifest [] ignore [ "foo.dll", src ]
        // where names ignore case (macOS, Windows) one file, kept; elsewhere two names, the old removed
        Assert.That(s.Removed, Is.EqualTo (if Env.isWindows || OperatingSystem.IsMacOS () then 0 else 1))
        Assert.That(File.Exists "run5/app/foo.dll")
        Assert.That(File.ReadAllText manifest, Does.Contain "\tfoo.dll\t")

    [<Test>]
    member _.``a source rewritten in place rewrites the manifest: a rule needing it runs again``() =
        fresh "src6"
        fresh "run6"
        write "src6/a.dll" "a"
        let manifest = Path.GetFullPath "run6/app/.xake-run.txt"
        let src = Path.GetFullPath "src6/a.dll"
        RunFolder.apply RunFolder.Link manifest [] ignore [ "a.dll", src ] |> ignore
        let text = File.ReadAllText manifest
        Assert.That(text, Does.Contain (sprintf "link\ta.dll\t%s\t%s" src (RunFolder.stampOf src)))
        let s0 = RunFolder.apply RunFolder.Link manifest [] ignore [ "a.dll", src ]
        Assert.That(s0.Written, Is.False)
        // the same file (a link keeps it), new bytes and time
        let id = RunFolder.fileId src
        File.WriteAllText (src, "a, recompiled")
        File.SetLastWriteTimeUtc (src, DateTime.UtcNow.AddMinutes 1.0)
        Assert.That(RunFolder.fileId src, Is.EqualTo id)
        let s = RunFolder.apply RunFolder.Link manifest [] ignore [ "a.dll", src ]
        Assert.That((s.Placed, s.Kept, s.Written), Is.EqualTo ((0, 1, true)))
        Assert.That(File.ReadAllText manifest, Is.Not.EqualTo text)


/// Run folders over a flat output (`out/<cfg>/<fwk>/`, every project of a framework in one
/// folder; `run/<cfg>/<fwk>/<name>/`, `Locks.runFolderWith`): a library, two console apps
/// wanting different versions of one package, a Test.Sdk project referencing the library and
/// an app, a .NET Framework WinExe -- each run folder against `dotnet build`'s bin, the tests run
/// from theirs, links, a no-op, and a build and a deploy in separate runs after an edit.
[<TestFixture>]
type ``Run folders``() =
    inherit XakeTestBase("run-folders")

    let projects = [ "lib/lib.csproj"; "console/console.csproj"; "console2/console2.csproj"; "tests/tests.csproj"; "desk/desk.csproj" ]
    let lockOf = RecordKit.lockOf
    let outOf = Locks.flatOutOf
    let projectOfAsm = dict [ "Lib.Core", "lib"; "console", "console"; "console2", "console2"; "tests", "tests"; "desk", "desk" ]
    let libSource (text: string) =
        sprintf "namespace Lib { public class C { public static string S() => Newtonsoft.Json.JsonConvert.SerializeObject (new [] { 1, 2 }) + \"%s\"; } }\n" text

    let fixture () =
        RecordKit.write [ "lib"; "console"; "console2"; "tests"; "desk"; "out"; "run"; "runc"; "locks"; "obj" ]
            [ "lib/lib.csproj",
                RecordKit.sdkProject "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks><AssemblyName>Lib.Core</AssemblyName><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>$(NoWarn);1591</NoWarn>"
                    "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
              "lib/C.cs", libSource ""
              "console/console.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType>"
                    ("<ProjectReference Include=\"../lib/lib.csproj\" />"
                     + "<None Update=\"data/input.txt\" CopyToOutputDirectory=\"PreserveNewest\" />"
                     + "<Content Include=\"assets/**\" CopyToOutputDirectory=\"PreserveNewest\" />")
              "console/P.cs", "class P { static void Main() { System.Console.WriteLine(Lib.C.S() + System.IO.File.ReadAllText(\"data/input.txt\")); } }\n"
              "console/data/input.txt", "input"
              "console/assets/a/b.txt", "b"
              "console2/console2.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType>"
                    "<PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />"
              "console2/P.cs", "class P { static void Main() { System.Console.WriteLine(typeof(Newtonsoft.Json.JsonConvert).Assembly.GetName().Version); } }\n"
              "tests/tests.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable>"
                    ("<ProjectReference Include=\"../lib/lib.csproj\" /><ProjectReference Include=\"../console/console.csproj\" />"
                     + "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.6.0\" /><PackageReference Include=\"NUnit\" Version=\"3.13.3\" />"
                     + "<PackageReference Include=\"NUnit3TestAdapter\" Version=\"4.2.1\" />")
              "tests/T.cs", "namespace T { [NUnit.Framework.TestFixture] public class A { [NUnit.Framework.Test] public void B() => NUnit.Framework.StringAssert.StartsWith(\"[1,2]\", Lib.C.S()); } }\n"
              "desk/desk.csproj",
                RecordKit.sdkProject "<TargetFramework>net472</TargetFramework><OutputType>WinExe</OutputType><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>$(NoWarn);1591</NoWarn>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" />"
              "desk/P.cs", "class P { static void Main() { System.Console.WriteLine(Lib.C.S()); } }\n" ]

    let compile = recipe {
        let! cfg = getRuleMatch "cfg"
        let! fwk = getRuleMatch "fwk"
        let! asm = getRuleMatch "asm"
        do! Locks.compileAs (lockOf projectOfAsm.[asm] cfg) fwk [] }
    let doc = recipe {
        let! cfg = getRuleMatch "cfg"
        let! fwk = getRuleMatch "fwk"
        let! asm = getRuleMatch "asm"
        do! Locks.doc Locks.Settings.Default (lockOf projectOfAsm.[asm] cfg) fwk }

    /// A run of the fixture's script: the compile rules over the flat output, the run folders by
    /// links (`run/`) and by copies (`runc/`).
    let script (options: ExecOptions) (label: string) (targets: string list) =
        let runFolder (mode: RunFolder.Mode) = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.runFolderWith [] mode lockOf outOf cfg fwk name }
        RecordKit.run options label
            [ command "build" { do! Locks.needAll (!! "*/xake.project.json") "Release" lockOf outOf }
              command "deploy" {
                let! folders = Locks.runFoldersWith [] (!! "*/xake.project.json") "Release" lockOf Locks.runOf
                do! need folders }
              "run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" ..> runFolder RunFolder.Link
              "runc/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" ..> runFolder RunFolder.Copy
              "out/(cfg:*)/(fwk:*)/(asm:*).dll" ..> compile
              "out/(cfg:*)/(fwk:*)/(asm:*).exe" ..> compile
              "out/(cfg:*)/(fwk:*)/(asm:*).xml" ..> doc ]
            targets

    let runProcess (exe: string) (args: string list) (dir: string) =
        let psi = ProcessStartInfo (exe)
        // no msbuild node left holding the redirected output (ReadToEnd would wait for it)
        psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.WorkingDirectory <- dir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd ()
        let err = p.StandardError.ReadToEnd ()
        p.WaitForExit ()
        if p.ExitCode <> 0 then failwithf "'%s %s' failed (exit %d):\n%s\n%s" exe (String.Join (" ", args)) p.ExitCode out err
        out

    let filesOf (dir: string) =
        Directory.GetFiles (dir, "*", SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (Path.GetFileName(f).StartsWith ".xake-run"))
        |> Array.map (fun f -> Path.GetRelativePath(dir, f).Replace ('\\', '/')) |> Array.sort |> List.ofArray

    let packages () =
        match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
        | null | "" -> Environment.GetFolderPath Environment.SpecialFolder.UserProfile </> ".nuget/packages"
        | p -> p

    let mutable prepared = false

    member private x.Prepare () =
        if not prepared then
            fixture ()
            RecordKit.import x.TestOptions "import-1" { RecordKit.recordDefault with OutOf = outOf } projects |> ignore
            script x.TestOptions "build-1" [ "build" ]
            script x.TestOptions "deploy-1" [ "deploy" ]
            for p in [ "console"; "console2"; "tests"; "desk" ] do
                RecordKit.runDotnet (Path.GetFullPath p) [ "build"; p + ".csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
            prepared <- true

    [<Test>]
    member x.``a run folder holds the files dotnet build puts in bin, from the project's own closure``() =
        x.Prepare ()
        // the flat output holds the compile's outputs only: no package file, no item
        let flat = filesOf "out/Release/net8.0"
        Assert.That(flat, Does.Not.Contain "Newtonsoft.Json.dll")
        Assert.That(flat, Does.Not.Contain "data/input.txt")
        for name, fwk in [ "console", "net8.0"; "console2", "net8.0"; "tests", "net8.0"; "desk", "net472" ] do
            let ours, theirs = sprintf "run/Release/%s/%s" fwk name, sprintf "%s/bin/Release/%s" name fwk
            Assert.That(filesOf ours, Is.EqualTo (filesOf theirs), name)
        // the items, at their target paths; a referenced application's runtime files
        Assert.That(File.ReadAllText "run/Release/net8.0/console/data/input.txt", Is.EqualTo "input")
        Assert.That(File.Exists "run/Release/net8.0/console/assets/a/b.txt")
        Assert.That(File.Exists "run/Release/net8.0/tests/console.runtimeconfig.json")
        // the known difference: msbuild's .NET Framework config has binding redirects, the
        // recorded one not (S55) -- the file is there, its text differs only by them
        Assert.That(runProcess "dotnet" [ "console.dll" ] (Path.GetFullPath "run/Release/net8.0/console"), Does.StartWith "[1,2]input")

    [<Test>]
    member x.``the test project runs from its run folder``() =
        x.Prepare ()
        let out = runProcess "dotnet" [ "test"; "tests.dll" ] (Path.GetFullPath "run/Release/net8.0/tests")
        Assert.That(out, Does.Contain "Passed!")

    [<Test>]
    member x.``two apps get their own versions of one package file, linked from the package folder``() =
        x.Prepare ()
        let version (p: string) = (Reflection.AssemblyName.GetAssemblyName p).Version
        Assert.That(version "run/Release/net8.0/console/Newtonsoft.Json.dll", Is.EqualTo (Version (13, 0, 0, 0)))
        Assert.That(version "run/Release/net8.0/console2/Newtonsoft.Json.dll", Is.EqualTo (Version (12, 0, 0, 0)))
        Assert.That(runProcess "dotnet" [ "console2.dll" ] (Path.GetFullPath "run/Release/net8.0/console2"), Does.StartWith "12.0.0.0")
        // hard links where the file system has them: the package's and the output's files
        let id = RunFolder.fileId
        if (id "run/Release/net8.0/console/console.dll").IsSome then
            Assert.That(id "run/Release/net8.0/console/Newtonsoft.Json.dll", Is.EqualTo (id (packages () </> "newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll")))
            Assert.That(id "run/Release/net8.0/console2/Newtonsoft.Json.dll", Is.EqualTo (id (packages () </> "newtonsoft.json/12.0.3/lib/netstandard2.0/Newtonsoft.Json.dll")))
            Assert.That(id "run/Release/net8.0/tests/Lib.Core.dll", Is.EqualTo (id "out/Release/net8.0/Lib.Core.dll"))
            Assert.That(id "run/Release/net8.0/console/data/input.txt", Is.EqualTo (id "console/data/input.txt"))
            let manifest = File.ReadAllText "run/Release/net8.0/console/.xake-run.txt"
            Assert.That(manifest, Does.Not.Contain "\ncopy\t")
            Assert.That(manifest, Does.Not.Contain "\nclone\t")

    [<Test>]
    member x.``a deploy that changes nothing touches nothing``() =
        x.Prepare ()
        let manifests = Directory.GetFiles ("run", RunFolder.Manifest, SearchOption.AllDirectories) |> Array.sort
        Assert.That(manifests, Has.Length.EqualTo 4)
        // the manifests (a hard-linked file's time is its source's: a recompile moves it)
        let state () = [ for m in manifests -> m, File.GetLastWriteTimeUtc m ]
        let ids () = [ for m in manifests do for f in Directory.GetFiles (Path.GetDirectoryName m, "*", SearchOption.AllDirectories) -> RunFolder.fileId f ]
        let before, beforeIds = state (), ids ()
        script x.TestOptions "deploy-noop" [ "deploy" ]
        Assert.That(File.ReadAllText "deploy-noop.log", Does.Not.Contain "Started run")
        Assert.That(File.ReadAllText "deploy-noop.log", Does.Not.Contain "/.xake-run.txt as task")
        // a rule that runs again (an input's time moved, not its content) finds every file in
        // place; the manifests naming that input record its new stamp (a rule needing them runs
        // again, as for any input whose time moved), the others are untouched
        File.SetLastWriteTimeUtc ("console/data/input.txt", DateTime.UtcNow.AddMinutes 1.0)
        script x.TestOptions "deploy-rerun" [ "deploy" ]
        Assert.That(File.ReadAllText "deploy-rerun.log", Does.Match "run folder Release/net8.0/console: \\d+ files \\(\\d+ link\\), 0 placed, \\d+ unchanged, 0 removed")
        let naming (m: string) = (File.ReadAllText m).Contains "data/input.txt"
        Assert.That(state () |> List.filter (fst >> naming >> not), Is.EqualTo (before |> List.filter (fst >> naming >> not)))
        Assert.That(state () |> List.filter (fst >> naming), Is.Not.EqualTo (before |> List.filter (fst >> naming)))
        Assert.That(ids (), Is.EqualTo beforeIds)
        script x.TestOptions "deploy-noop-2" [ "deploy" ]
        Assert.That(File.ReadAllText "deploy-noop-2.log", Does.Not.Contain "/.xake-run.txt as task")

    [<Test>]
    member x.``an edit, a build and a deploy in separate runs place the changed assembly``() =
        x.Prepare ()
        let copied = "runc/Release/net8.0/console/Lib.Core.dll"
        let target = "runc/Release/net8.0/console/.xake-run.txt"
        let linked = "run/Release/net8.0/console/.xake-run.txt"
        try
            script x.TestOptions "s66-deploy-1" [ target; linked ]
            let manifests () = File.ReadAllText target, File.ReadAllText linked
            let manifestsBefore = manifests ()
            let before = File.ReadAllBytes copied
            Assert.That(before, Is.EqualTo (File.ReadAllBytes "out/Release/net8.0/Lib.Core.dll"))
            Assert.That(File.ReadAllText target, Does.Contain "copy\tLib.Core.dll\t")
            File.WriteAllText ("lib/C.cs", libSource "!")
            script x.TestOptions "s66-build" [ "build" ]
            Assert.That(File.ReadAllBytes "out/Release/net8.0/Lib.Core.dll", Is.Not.EqualTo before)
            // a run of its own: the compile is up to date, the run folder sees that its
            // assembly was rebuilt by the earlier run (ArtifactDep2) and copies it again
            script x.TestOptions "s66-deploy-2" [ target; linked ]
            Assert.That(File.ReadAllBytes copied, Is.EqualTo (File.ReadAllBytes "out/Release/net8.0/Lib.Core.dll"))
            // the manifests, the rules' targets, change with the recompiled assembly (copies and
            // links alike): a rule needing them (tests, pack) runs again
            let after = manifests ()
            Assert.That(fst after, Is.Not.EqualTo (fst manifestsBefore))
            Assert.That(snd after, Is.Not.EqualTo (snd manifestsBefore))
        finally
            File.WriteAllText ("lib/C.cs", libSource "")
            script x.TestOptions "s66-revert" [ "build"; "deploy" ]

    [<Test>]
    member x.``deploy alone on a clean tree builds first and places what build then deploy places``() =
        x.Prepare ()
        let folders = [ "net8.0/console"; "net8.0/console2"; "net8.0/tests"; "net472/desk" ]
        let state () = [ for f in folders -> f, filesOf ("run/Release/" + f) ]
        let reference = state ()
        for d in [ "out"; "run"; "runc" ] do Directory.Delete (d, true)
        File.Delete ".xake"
        script x.TestOptions "b1-deploy" [ "deploy" ]
        Assert.That(state (), Is.EqualTo reference)
        for f in [ "console.runtimeconfig.json"; "console.deps.json"; "console.pdb"; "Lib.Core.pdb"; "Lib.Core.xml" ] do
            Assert.That(File.Exists ("run/Release/net8.0/console/" + f), f)
        Assert.That(runProcess "dotnet" [ "console.dll" ] (Path.GetFullPath "run/Release/net8.0/console"), Does.StartWith "[1,2]input")
        // and the next deploy is a no-op
        script x.TestOptions "b1-deploy-2" [ "deploy" ]
        Assert.That(File.ReadAllText "b1-deploy-2.log", Does.Not.Contain "/.xake-run.txt as task")

    [<Test>]
    member x.``needing the XML doc of a .NET Framework application builds its exe``() =
        x.Prepare ()
        for f in [ "out/Release/net472/desk.exe"; "out/Release/net472/desk.xml" ] do File.Delete f
        File.Delete ".xake"
        script x.TestOptions "s64" [ "out/Release/net472/desk.xml" ]
        Assert.That(File.Exists "out/Release/net472/desk.exe")
        Assert.That(File.Exists "out/Release/net472/desk.xml")
        Assert.That(File.Exists "out/Release/net472/desk.dll", Is.False)
