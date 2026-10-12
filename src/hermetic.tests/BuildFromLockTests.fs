namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// `Locks.compileAs` and `Locks.needAll`: the build from Xake-generated locks into the
/// script's layout, with the draft's rule shape (`target "out/(cfg:*)/(fwk:*)/(name:*).dll"`).
/// Fixtures: two F# libraries `A -> B` (B with doc) and a C# library with source masks, under
/// `p/`, their locks generated first by the script's phony `lock::` rule through `update-locks`
/// (`LockRule`, as docs/hermetic/lock.md has it).
[<TestFixture>]
type ``Building from Xake-generated locks``() =
    inherit XakeTestBase("buildfromlock")

    let fixtures = __SOURCE_DIRECTORY__ </> "fixtures" </> "src"
    let srcRoot = Path.GetFullPath (__SOURCE_DIRECTORY__ </> "..")
    let lockOf name cfg = sprintf "locks/%s/%s.json" name cfg
    let outOf cfg fwk (_: string) = sprintf "out/%s/%s" cfg fwk
    let dll cfg name = sprintf "out/%s/netstandard2.0/%s.dll" cfg name
    let fsharpCore : Description.Package = { Id = "FSharp.Core"; Version = Some "8.0.100"; Private = false }
    let defaultVersion = Var.string (name = "TEST_VERSION") |> withDefault "1.2.3"
    let otherVersion = Var.string (name = "OTHER_VERSION") |> withDefault "9.9.9"
    let versions = [ "p/B/**", defaultVersion; "**", otherVersion ]
    let fixtureOf name = sprintf "p/%s/%s" name Description.FileName

    let describe (name: string) (language: Description.Language) (sources: string list) (refs: string list) (doc: bool) =
        let dir = Path.GetFullPath ("p" </> name)
        let d : Description.Project =
            { Schema = None; Name = name; Project = name + (if language = Description.FSharp then ".fsproj" else ".csproj")
              AssemblyName = None; RootNamespace = None; Language = language; Kind = Description.Library
              Frameworks = [ "netstandard2.0" ]; Sources = sources; Resources = []; Defines = []; References = []
              ProjectRefs = refs; Packages = (if language = Description.FSharp then [ fsharpCore ] else [])
              Options = { Description.Options.Default with Doc = doc }; InternalsVisibleTo = []; AssemblyAttributes = []; ByFramework = []; Package = None }
        Description.write (dir </> Description.FileName) d |> ignore

    /// p/B (F#, doc), p/A (F#, references B, no doc), p/Cs (C#, masks); fresh sources.
    let fixture () =
        for dir in [ "p"; "out"; "locks"; "obj" ] do
            if Directory.Exists dir then Directory.Delete (dir, true)
        for dir in [ "p/B"; "p/A"; "p/Cs/Sub"; "p/Cs/obj" ] do Directory.CreateDirectory dir |> ignore
        File.WriteAllText ("p/B/Lib.fs", "module B.Lib\n/// The answer\nlet answer = 42\n")
        File.WriteAllText ("p/A/Lib.fs", "module A.Lib\nlet twice = 2 * B.Lib.answer\n")
        File.WriteAllText ("p/Cs/A.cs", "namespace Cs { public class A {} }\n")
        File.WriteAllText ("p/Cs/Sub/B.cs", "namespace Cs { public class B {} }\n")
        File.WriteAllText ("p/Cs/obj/Broken.cs", "this is not C#\n")
        describe "B" Description.FSharp [ "Lib.fs" ] [] true
        describe "A" Description.FSharp [ "Lib.fs" ] [ "B" ] false
        describe "Cs" Description.CSharp [ "**/*.cs"; "!obj/**/*"; "!bin/**/*" ] [] false

    let typeNames (path: string) =
        use stream = File.OpenRead path
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        [ for h in md.TypeDefinitions -> md.GetString (md.GetTypeDefinition h).Name ]

    /// `build` (and the layout's dll rule) with the script's `extra` rules beside them.
    member private x.ScriptWith (extra: Rule<ExecContext> list) (label: string) (vars: (string * string) list) (descriptions: Fileset) (cfg: string) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = ("CI", "off") :: vars; FileLogLevel = Loud} {
            wantOverride [ "build" ]
            rules extra
            rules [
                command "build" { do! Locks.needAll descriptions cfg lockOf outOf }
                target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
                    let! cfg = getRuleMatch "cfg"
                    let! fwk = getRuleMatch "fwk"
                    let! name = getRuleMatch "name"
                    do! Locks.compileAs (lockOf name cfg) fwk versions }
            ]
        }

    member private x.Script label vars descriptions cfg = x.ScriptWith [] label vars descriptions cfg

    /// `update-locks` over `projects` (their descriptions at `descriptionOf name`) in `configs`;
    /// the `name::cfg` of each lock rule run.
    member private x.Generate (label: string) (descriptionOf: string -> string) (projects: string list) (configs: string list) =
        let ran = ResizeArray ()
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off" ]; FileLogLevel = Loud} {
            wantOverride [ "update-locks" ]
            rules [ LockRule.lockRule lockOf outOf descriptionOf id ran; LockRule.updateLocks configs projects ]
        }
        List.ofSeq ran

    member private x.Stamps (paths: string list) = paths |> List.map File.GetLastWriteTimeUtc

    [<Test>]
    member x.``the version of a project is the first matching mask``() =
        let versions = [ "src/hermetic/**", "0.2"; "**", "3.6" ]
        Assert.That(Locks.versionFor versions "src/hermetic/xake.project.json", Is.EqualTo (Some "0.2"))
        Assert.That(Locks.versionFor versions "src/core/xake.project.json", Is.EqualTo (Some "3.6"))
        Assert.That(Locks.versionFor [ "src/core/**", "1" ] "src/hermetic/xake.project.json", Is.EqualTo None)

    /// The acceptance: A -> B built from the locks into the layout, the .xml beside B's dll;
    /// a second build does nothing (also in Debug, where nothing asks for the .xml twice); a
    /// change to B rebuilds A; two clean builds give identical assemblies.
    [<Test; Category("Integration")>]
    member x.``A over B from the locks: incremental and deterministic``() =
        fixture ()
        let descriptions = !! "p/A/xake.project.json" + "p/B/xake.project.json"
        x.Generate "generate" fixtureOf [ "A"; "B" ] [ "Release"; "Debug" ] |> ignore
        let lockText = File.ReadAllText (lockOf "A" "Release")
        Assert.That(lockText, Does.Contain "\"$(ProjectRoot)/out/Release/netstandard2.0/B.dll\", \"Sha256\": \"\"")

        x.Script "release-1" [] descriptions "Release"
        let outputs = [ dll "Release" "B"; dll "Release" "A" ]
        for f in outputs do Assert.That(File.Exists f, Is.True, f)
        Assert.That(File.Exists "out/Release/netstandard2.0/B.xml", Is.True, "B asks for doc: the .xml is beside the dll")
        Assert.That(File.Exists "out/Release/netstandard2.0/A.xml", Is.False)
        Assert.That(File.ReadAllText (lockOf "A" "Release"), Is.EqualTo lockText, "the build writes no lock")
        let stamps = x.Stamps outputs

        x.Script "release-2" [] descriptions "Release"
        Assert.That(x.Stamps outputs, Is.EqualTo stamps, "nothing changed, nothing rebuilt")

        // Debug: A asks for no doc; twice, the second does nothing
        x.Script "debug-1" [] descriptions "Debug"
        let debug = [ dll "Debug" "B"; dll "Debug" "A" ]
        let debugStamps = x.Stamps debug
        Assert.That(File.Exists "out/Debug/netstandard2.0/A.xml", Is.False)
        x.Script "debug-2" [] descriptions "Debug"
        Assert.That(x.Stamps debug, Is.EqualTo debugStamps, "Debug without doc does not rebuild")

        // a change to B rebuilds B, then A
        File.WriteAllText ("p/B/Lib.fs", "module B.Lib\n/// The answer\nlet answer = 43\n")
        x.Script "release-3" [] descriptions "Release"
        let after = x.Stamps outputs
        Assert.That(after.[0], Is.GreaterThan stamps.[0], "B rebuilt")
        Assert.That(after.[1], Is.GreaterThan stamps.[1], "A rebuilt after B")

        // two clean builds: the same bytes
        let bytes () = outputs |> List.map File.ReadAllBytes
        let first = bytes ()
        Directory.Delete ("out", true)
        File.Delete ".xake"
        x.Script "release-clean" [] descriptions "Release"
        Assert.That(bytes (), Is.EqualTo first, "two clean builds differ")

    /// A fresh clone: the locks of A -> B are committed, `.xake` and the outputs are not.
    /// `build` (with the lock rule in the script) compiles from the committed locks through
    /// `needAll` + `compileAs` and never runs a lock rule; the locks stay untouched.
    [<Test; Category("Integration")>]
    member x.``a fresh clone builds from the committed locks without running a lock rule``() =
        fixture ()
        let descriptions = !! "p/A/xake.project.json" + "p/B/xake.project.json"
        let generated = x.Generate "fresh-generate" fixtureOf [ "A" ] [ "Release" ]
        Assert.That(generated, Is.EqualTo [ "B::Release"; "A::Release" ], "update-locks generates B first, then A")
        let locks = [ lockOf "A" "Release"; lockOf "B" "Release" ]
        let texts = locks |> List.map File.ReadAllText
        let stamps = x.Stamps locks

        // the clone: no database, no outputs
        Assert.That(File.Exists x.TestOptions.DbFileName, Is.True, "the generating run left a database")
        File.Delete x.TestOptions.DbFileName
        if Directory.Exists "out" then Directory.Delete ("out", true)
        System.Threading.Thread.Sleep 1100

        let ran = ResizeArray ()
        let lockRules = [ LockRule.lockRule lockOf outOf fixtureOf id ran; LockRule.updateLocks [ "Release" ] [ "A" ] ]
        x.ScriptWith lockRules "fresh-build" [] descriptions "Release"
        let outputs = [ dll "Release" "B"; dll "Release" "A" ]
        for f in outputs do Assert.That(File.Exists f, Is.True, f)
        let built = x.Stamps outputs
        x.ScriptWith lockRules "fresh-build-again" [] descriptions "Release"
        Assert.That(List.ofSeq ran, Is.Empty, "a lock rule ran during build")
        Assert.That(x.Stamps outputs, Is.EqualTo built, "the second build does nothing")
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts, "the build writes no lock")
        Assert.That(x.Stamps locks, Is.EqualTo stamps)

    /// A fresh clone with an empty package folder (CI): the packages the lock names are
    /// fetched by the replay's restore before anything needs them (needs nuget.org).
    [<Test; Category("Integration")>]
    member x.``an empty package folder is fetched before the build needs the packages``() =
        fixture ()
        let descriptions = !! "p/B/xake.project.json"
        x.Generate "empty-generate" fixtureOf [ "B" ] [ "Release" ] |> ignore
        let packages = Path.GetFullPath "empty-packages"
        if Directory.Exists packages then Directory.Delete (packages, true)
        File.Delete x.TestOptions.DbFileName
        x.Script "empty-build" [ "NUGET_PACKAGES", packages; "NUGET_FETCH", "on" ] descriptions "Release"
        Assert.That(File.Exists (dll "Release" "B"), Is.True)
        Assert.That(File.Exists (packages </> "fsharp.core" </> "8.0.100" </> "lib" </> "netstandard2.0" </> "FSharp.Core.dll"), Is.True, "FSharp.Core fetched")

    /// C# masks are expanded at build through getFiles: an added `.cs` rebuilds; `obj/` is
    /// not a source.
    [<Test; Category("Integration")>]
    member x.``a C# lock keeps its masks; an added file rebuilds``() =
        fixture ()
        let descriptions = !! "p/Cs/xake.project.json"
        x.Generate "generate-cs" fixtureOf [ "Cs" ] [ "Release" ] |> ignore
        let lockText = File.ReadAllText (lockOf "Cs" "Release")
        Assert.That(lockText, Does.Contain "\"$(ProjectRoot)/p/Cs/**/*.cs\"")
        Assert.That(lockText, Does.Not.Contain "B.cs")

        x.Script "cs-1" [] descriptions "Release"
        let out = dll "Release" "Cs"
        Assert.That(typeNames out, Is.SupersetOf [ "A"; "B" ])
        let stamp = File.GetLastWriteTimeUtc out
        x.Script "cs-2" [] descriptions "Release"
        Assert.That(File.GetLastWriteTimeUtc out, Is.EqualTo stamp, "nothing changed, nothing rebuilt")

        File.WriteAllText ("p/Cs/Sub/C.cs", "namespace Cs { public class C {} }\n")
        x.Script "cs-3" [] descriptions "Release"
        Assert.That(typeNames out, Is.SupersetOf [ "A"; "B"; "C" ], "class C is in the rebuilt assembly")
        Assert.That(File.ReadAllText (lockOf "Cs" "Release"), Is.EqualTo lockText, "an added file changes no lock")

    /// A described project without a lock fails the build, pointing at `update-locks`.
    [<Test; Category("Integration")>]
    member x.``a description without a lock fails naming update-locks``() =
        fixture ()
        let e = Assert.Throws<XakeException>(fun () -> x.Script "no-lock" [] (!! "p/B/xake.project.json") "Release")
        Assert.That(e.Data0, Does.Contain "`update-locks`")
        Assert.That(e.Data0, Does.Contain "'B' ('locks/B/Release.json')")

    /// The HERMETIC gate runs inside compileAs: the SDK's compiler and reference packs are
    /// outside the roots, so the lock is not replayed and nothing is compiled.
    [<Test; Category("Integration")>]
    member x.``HERMETIC=on refuses the lock inside compileAs``() =
        fixture ()
        let descriptions = !! "p/B/xake.project.json"
        x.Generate "generate-hermetic" fixtureOf [ "B" ] [ "Release" ] |> ignore
        let e = Assert.Throws<XakeException>(fun () -> x.Script "hermetic" [ "HERMETIC", "on" ] descriptions "Release")
        Assert.That(e.Data0, Does.Contain "'B': HERMETIC=on: the lock 'locks/B/Release.json' names")
        Assert.That(File.Exists (dll "Release" "B"), Is.False)

    /// A `$(Version)` token in `Generated` takes the version of the first matching mask; the
    /// variable is a dependency: another value rebuilds.
    [<Test; Category("Integration")>]
    member x.``the Version token in Generated is the matching mask's version``() =
        fixture ()
        let descriptions = !! "p/B/xake.project.json"
        x.Generate "generate-version" fixtureOf [ "B" ] [ "Release" ] |> ignore
        // a lock whose Generated carries the token (the description has no versioning yet)
        let roots = Roots.builtin (Directory.GetCurrentDirectory ())
        let path = lockOf "B" "Release"
        let doc = Lock.read roots path
        let source = Path.GetFullPath "obj/xake/B/netstandard2.0/B.Version.fs" |> fun p -> p.Replace ('\\', '/')
        let content = "namespace B\nopen System.Reflection\n[<assembly: AssemblyInformationalVersion(\"$(Version)\")>]\ndo ()\n"
        let withVersion (e: Lock.Entry) =
            match e.Compilation with
            | Lock.Compilation.Fsc f ->
                { e with Compilation = Lock.Compilation.Fsc { f with Sources = source :: f.Sources; Generated = (source, content) :: f.Generated } }
            | _ -> e
        File.WriteAllText (path, Lock.format roots { doc with Entries = doc.Entries |> List.map withVersion })
        Assert.That(File.ReadAllText path, Does.Contain "$(Version)")

        let text () = System.Text.Encoding.UTF8.GetString (File.ReadAllBytes (dll "Release" "B"))
        x.Script "version-1" [] descriptions "Release"
        Assert.That(text (), Does.Contain "1.2.3")
        Assert.That(File.ReadAllText source, Does.Contain "\"1.2.3\"")
        x.Script "version-2" [ "TEST_VERSION", "2.0.1" ] descriptions "Release"
        Assert.That(text (), Does.Contain "2.0.1", "another version rebuilds")
        Assert.That(File.ReadAllText path, Does.Contain "$(Version)", "the lock keeps the token")

    /// Xake's three projects from the locks `update-locks` writes, into out/Release/<fwk>/.
    [<Test; Category("Integration")>]
    member x.``Xake's three projects build from their generated locks``() =
        for dir in [ "desc"; "out"; "locks" ] do
            if Directory.Exists dir then Directory.Delete (dir, true)
        let described =
            [ for dir in [ "core"; "dotnet"; "hermetic" ] do
                let d = Description.read (fixtures </> dir </> Description.FileName)
                let target = Path.GetFullPath ("desc" </> dir)
                Directory.CreateDirectory target |> ignore
                Description.write (target </> Description.FileName) { d with Project = Path.GetRelativePath (target, srcRoot </> dir </> d.Project) } |> ignore
                yield d.Name, sprintf "desc/%s/%s" dir Description.FileName ] |> Map.ofList
        let descriptions = !! "desc/*/xake.project.json"
        x.Generate "generate-xake" (fun n -> described.[n]) (List.ofSeq described.Keys) [ "Release" ] |> ignore
        x.Script "build-xake" [] descriptions "Release"
        for fwk in [ "net462"; "netstandard2.0" ] do
            for name in [ "Xake"; "Xake.Dotnet"; "Xake.Hermetic.Dotnet" ] do
                Assert.That(File.Exists (sprintf "out/Release/%s/%s.dll" fwk name), Is.True, name + " " + fwk)
                Assert.That(File.Exists (sprintf "out/Release/%s/%s.xml" fwk name), Is.True, name + " " + fwk + " doc")
        let alc = System.Runtime.Loader.AssemblyLoadContext ("from-lock", true)
        try
            let a = alc.LoadFromAssemblyPath (Path.GetFullPath "out/Release/netstandard2.0/Xake.dll")
            Assert.That(a.GetExportedTypes (), Is.Not.Empty)
        finally alc.Unload ()

    /// G5: A3 -> B3 -> C, A3's source uses C's type through B3 and its description names only
    /// B3; the lock carries C's output too (unhashed, after B3's), and A3 compiles.
    [<Test; Category("Integration")>]
    member x.``transitive project references reach the compiler``() =
        fixture ()
        for dir in [ "p/C"; "p/B3"; "p/A3" ] do Directory.CreateDirectory dir |> ignore
        File.WriteAllText ("p/C/Lib.fs", "module C.Lib\ntype T = { V: int }\n")
        File.WriteAllText ("p/B3/Lib.fs", "module B3.Lib\nlet make () : C.Lib.T = { V = 7 }\n")
        File.WriteAllText ("p/A3/Lib.fs", "module A3.Lib\nlet v = (B3.Lib.make ()).V\n")
        describe "C" Description.FSharp [ "Lib.fs" ] [] false
        describe "B3" Description.FSharp [ "Lib.fs" ] [ "C" ] false
        describe "A3" Description.FSharp [ "Lib.fs" ] [ "B3" ] false
        x.Generate "generate-transitive" fixtureOf [ "A3" ] [ "Release" ] |> ignore
        let text = File.ReadAllText (lockOf "A3" "Release")
        let b3 = text.IndexOf "\"$(ProjectRoot)/out/Release/netstandard2.0/B3.dll\", \"Sha256\": \"\""
        let c = text.IndexOf "\"$(ProjectRoot)/out/Release/netstandard2.0/C.dll\", \"Sha256\": \"\""
        Assert.That(b3, Is.GreaterThan -1, text)
        Assert.That(c, Is.GreaterThan b3, "C's output follows B3's")
        let entry = (Lock.read (Roots.builtin (Directory.GetCurrentDirectory ())) (lockOf "A3" "Release")).Entries.Head
        Assert.That(entry.Evaluation.ProjectRefs, Is.EqualTo [ "B3" ], "ProjectRefs keeps the direct references")
        x.Script "build-transitive" [] (!! "p/A3/xake.project.json") "Release"
        for name in [ "C"; "B3"; "A3" ] do Assert.That(File.Exists (dll "Release" name), Is.True, name)
