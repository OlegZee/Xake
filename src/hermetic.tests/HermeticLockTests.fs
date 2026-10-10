namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `HERMETIC=on` on the lock side (hermetic-mode.md, messages 9-11): a lock is not written,
/// and not replayed, when it names a path outside `$(ProjectRoot)` and `$(NuGetPackageRoot)`
/// that no prerequisite covers; an import of a project whose SDK is not pinned exactly fails.
/// Each test runs in a fresh project root of its own, with the variables passed as `Vars`
/// (and `CI=off`, so the suite passes with `CI=true` in the environment).
[<TestFixture>]
type ``Hermetic lock``() =
    inherit XakeTestBase("hermetic-lock")

    let toolsetVersion = "4.12.0"
    let nuget () = DotNetFwk.nugetRoot ()
    let dotnet () = DotNetFwk.dotnetRoot () |> Option.get

    /// The released SDKs installed, newest last.
    let installedSdks () =
        Directory.GetDirectories (dotnet () </> "sdk")
        |> Array.map Path.GetFileName
        |> Array.choose (fun name -> match System.Version.TryParse name with | true, v -> Some (v, name) | _ -> None)
        |> Array.sortBy fst |> Array.map snd |> List.ofArray

    /// A fresh project root under the test folder, with `globalJson` when given.
    let projectRoot (name: string) (globalJson: string option) =
        let dir = Path.GetFullPath name
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory dir |> ignore
        globalJson |> Option.iter (fun text -> File.WriteAllText (dir </> "global.json", text))
        dir

    let exactPin version = sprintf """{ "sdk": { "version": "%s", "rollForward": "disable" } }""" version

    /// The variables that make a composed csc for the SDK's framework hermetic.
    let hermeticVars (root: string) =
        let fwk = DotNetFwk.sdkFramework root |> Option.get
        [ "CI", "off"
          "HERMETIC", "on"
          "NUGET_PACKAGES", nuget ()
          "CSC_TOOLSET", toolsetVersion
          "NETCORE_REF_VERSION", DotNetFwk.sdkTargetingPackVersion root fwk |> Option.get ]

    let without (names: string list) vars = vars |> List.filter (fun (k, _) -> not (List.contains k names))

    /// Runs `body` as the rule `label` under `root` with `vars`; the failure text, if any.
    let run (options: ExecOptions) (root: string) (label: string) (vars: (string * string) list) (body: Recipe<ExecContext, unit>) =
        try
            do xake {options with ProjectRoot = root; FileLog = root </> (label + ".log"); FileLogLevel = Loud; ThrowOnError = true; Vars = vars} {
                wantOverride ([label])
                rules [ label => body ]
            }
            None
        with e -> Some (e.ToString())

    let succeeds = function
        | None -> ()
        | Some text -> Assert.Fail text

    let fails = function
        | Some text -> text
        | None -> Assert.Fail "expected the build to fail"; ""

    [<Test; Category("Integration")>]
    member x.``a csc lock records under HERMETIC=on with NUGET_PACKAGES, CSC_TOOLSET and NETCORE_REF_VERSION``() =
        let root = projectRoot "record-csc" None
        File.WriteAllText (root </> "a.cs", "public class A {}\n")
        run x.TestOptions root "record" (hermeticVars root)
            (csc { src !!"a.cs"; out (File.make (root </> "A.dll")); target Library; lock "locks/a.json" })
        |> succeeds

        let text = File.ReadAllText (root </> "locks/a.json")
        Assert.That(text, Does.Not.Contain "$(DotnetRoot)", "a hermetic csc lock names nothing under the .NET root")
        Assert.That(text, Does.Contain "$(NuGetPackageRoot)/microsoft.net.compilers.toolset/")
        Assert.That(text, Does.Not.Contain "Prerequisites")
        Assert.That(File.Exists (root </> "A.dll"), Is.True, "the recorded lock was not compiled")

    [<Test; Category("Integration")>]
    member x.``without CSC_TOOLSET the lock is refused naming the SDK compiler (message 9)``() =
        let root = projectRoot "record-sdk-csc" None
        File.WriteAllText (root </> "b.cs", "public class B {}\n")
        // resolved with the mode off and no toolset: the compiler is the SDK's csc
        let mutable resolved : Csc option = None
        run x.TestOptions root "resolve" (("HERMETIC", "off") :: without [ "HERMETIC"; "CSC_TOOLSET" ] (hermeticVars root))
            (recipe {
                let! c = csc { src !!"b.cs"; out (File.make (root </> "B.dll")); target Library; resolve }
                resolved <- Some c })
        |> succeeds
        let c = Option.get resolved
        let compiler = c.Dependencies.Compiler.Path.Replace('\\', '/')
        let dotnetRoot = (dotnet ()).Replace('\\', '/').TrimEnd '/'
        Assert.That(compiler, Does.StartWith (dotnetRoot + "/sdk/"))

        // recorded with the mode on: refused, nothing written
        let text =
            run x.TestOptions root "record" (hermeticVars root) (Lock.record "locks/b.json" (Lock.Compilation.Csc c))
            |> fails
        let tokenized = "$(DotnetRoot)" + compiler.Substring dotnetRoot.Length
        Assert.That(text, Does.Contain (sprintf "'B': HERMETIC=on: refusing to write the lock 'locks/b.json': %s is outside $(ProjectRoot) and $(NuGetPackageRoot), and no prerequisite covers it" tokenized))
        Assert.That(File.Exists (root </> "locks/b.json"), Is.False, "a refused lock must not be written")

        // and without the script variable NUGET_PACKAGES the package folder is refused too (message 2)
        let text =
            run x.TestOptions root "record2" (without [ "NUGET_PACKAGES" ] (hermeticVars root)) (Lock.record "locks/b.json" (Lock.Compilation.Csc c))
            |> fails
        Assert.That(text, Does.Contain "'B': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable")

        // with the mode off the same compilation records, as before
        run x.TestOptions root "record3" (("HERMETIC", "off") :: without [ "HERMETIC" ] (hermeticVars root)) (Lock.record "locks/b.json" (Lock.Compilation.Csc c))
        |> succeeds
        Assert.That(File.Exists (root </> "locks/b.json"), Is.True)

    [<Test; Category("Integration")>]
    member x.``an fsc lock under an exact pin records the SDK prerequisite and replays under HERMETIC=on``() =
        let sdk = installedSdks () |> List.last
        let root = projectRoot "record-fsc" (Some (exactPin sdk))
        File.WriteAllText (root </> "F.fs", "module F\nlet value = 1\n")
        let vars = ("FSHARP_CORE_VERSION", DotNetFwk.fsharpCoreVersion) :: without [ "CSC_TOOLSET" ] (hermeticVars root)
        run x.TestOptions root "record" vars
            (fsc { src !!"F.fs"; out (File.make (root </> "F.dll")); target Library; lock "locks/f.json" })
        |> succeeds

        let lockFile = root </> "locks/f.json"
        let text = File.ReadAllText lockFile
        Assert.That(text, Does.Contain (sprintf "\"Path\": \"$(DotnetRoot)/sdk/%s/FSharp/fsc.dll\"" sdk))
        Assert.That(text, Does.Contain (sprintf "{ \"Kind\": \"dotnet-sdk\", \"Version\": \"%s\", \"Pin\": \"$(ProjectRoot)/global.json\" }" sdk))
        // the compiler is the only path under the .NET root
        let dotnetLines = text.Split '\n' |> Array.filter (fun l -> l.Contains "$(DotnetRoot)")
        Assert.That(dotnetLines.Length, Is.EqualTo 1, String.concat "\n" dotnetLines)

        // replayed from the lock, under the mode
        File.Delete (root </> "F.dll")
        run x.TestOptions root "replay" vars
            (recipe {
                let! doc = Lock.load "locks/f.json"
                do! Lock.compile doc.Entries.Head })
        |> succeeds
        Assert.That(File.Exists (root </> "F.dll"), Is.True, "the lock did not replay")

    [<Test>]
    member x.``a lock naming the targeting pack under the .NET root is not replayed under HERMETIC=on (message 10)``() =
        let root = projectRoot "replay-packs" None
        let dotnetRoot = (dotnet ()).Replace('\\', '/').TrimEnd '/'
        let packRef = dotnetRoot + "/packs/Microsoft.NETCore.App.Ref/8.0.0/ref/net8.0/System.Runtime.dll"
        let compiler = (nuget ()).Replace('\\', '/').TrimEnd '/' + "/microsoft.net.compilers.toolset/4.12.0/tasks/netcore/bincore/csc.dll"
        let c : Csc =
            { Name = "P"; Framework = "net8.0"; Directory = root
              Options = []; Defines = []; Sources = [ root </> "p.cs" ]; Generated = []; Resources = []; RuntimeConfig = None
              Dependencies =
                { Compiler = { Tool = "csc"; Path = compiler; Sha256 = ""; Version = "4.12.0" }
                  References = [ { Path = packRef; Sha256 = ""; Alias = "" } ]
                  Analyzers = [] } }
        let lockFile = root </> "locks/p.json"
        Directory.CreateDirectory (Path.GetDirectoryName lockFile) |> ignore
        File.WriteAllText (lockFile, Lock.format (Roots.make root [ Roots.packageRootOverride (nuget ()) |> List.head ]) { Configuration = ""; Properties = []; Entries = [ Lock.ofCsc c ] })

        let text =
            run x.TestOptions root "replay" (hermeticVars root)
                (recipe {
                    let! doc = Lock.load "locks/p.json"
                    do! Lock.compileWith Lock.Options.Default doc.Entries.Head })
            |> fails
        Assert.That(text, Does.Contain "'P': HERMETIC=on: the lock names $(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/8.0.0/ref/net8.0/System.Runtime.dll, outside $(ProjectRoot) and $(NuGetPackageRoot) and not covered by a prerequisite; re-record it with HERMETIC=on")
        Assert.That(text, Does.Not.Contain "microsoft.net.compilers.toolset", "the compiler is under the package folder")

    [<Test; Category("Integration")>]
    member x.``an import of a project with an unpinned SDK fails under HERMETIC=on (message 11)``() =
        // a global.json of its own that rolls forward: not an exact pin, whatever the
        // repository pins
        let root = projectRoot "import-unpinned" (Some """{ "sdk": { "version": "8.0.100", "rollForward": "latestMajor" } }""")
        let projectFile = root </> "Unpinned.csproj"
        File.WriteAllText (projectFile,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n")
        File.WriteAllText (root </> "U.cs", "public class U {}\n")
        let lockFile = root </> "locks/unpinned.json"
        let text =
            run x.TestOptions root "import" [ "CI", "off"; "HERMETIC", "on"; "NUGET_PACKAGES", nuget () ]
                (Project.import { Project.ImportOptions.Default with
                                    Projects = [ projectFile ]
                                    Frameworks = [ "netstandard2.0" ]
                                    Output = lockFile })
            |> fails
        Assert.That(text, Does.Contain "'Unpinned': HERMETIC=on: the project's SDK is not pinned (")
        Assert.That(text, Does.Contain "); the import's compiler and analyzers come from the SDK, so pin it in global.json: { \"sdk\": { \"version\": \"")
        Assert.That(text, Does.Not.Contain "refusing to write the lock 'locks", "the SDK paths are reported once, by message 11")
        Assert.That(File.Exists lockFile, Is.False, "a refused lock must not be written")
