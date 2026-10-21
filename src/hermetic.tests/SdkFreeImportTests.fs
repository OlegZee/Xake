namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Import.record` of projects on the toolset compiler (`Microsoft.Net.Compilers.Toolset` with
/// `RoslynCompilerType=Toolset`) and package analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`),
/// at the versions the SDK in use bundles: the lock names nothing under
/// `$(DotnetRoot)/sdk/<v>/` and carries no `dotnet-sdk` prerequisite, and it builds against a
/// .NET root whose `sdk/` and `packs/` are empty -- a targeting pack (`pack:`) from the package
/// folder -- into the same bytes as `dotnet build` (docs/features/hermetic-build/sdk-free-locks.md,
/// steps 1-3).
[<TestFixture>]
type ``Import record on package compiler and analyzers``() =
    inherit XakeTestBase("import-record-sdkfree")

    /// The SDK `dotnet` picks in the current folder.
    let sdkVersion () =
        let psi = ProcessStartInfo ("dotnet", "--version")
        psi.RedirectStandardOutput <- true
        psi.UseShellExecute <- false
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd ()
        p.WaitForExit ()
        out.Trim ()

    /// The versions of the compiler and the analyzers the SDK in use bundles, as their packages
    /// are versioned on nuget.org: the product version without its prerelease and commit
    /// (`5.9.0-1.26505.111+a8dd...` is `Microsoft.Net.Compilers.Toolset` 5.9.0,
    /// `10.0.402-servicing...` is `Microsoft.CodeAnalysis.NetAnalyzers` 10.0.402).
    let bundledVersions () =
        let root = (DotNetFwk.dotnetRoot ()).Value
        let sdk = Path.Combine (root, "sdk", sdkVersion ())
        let plain (file: string) = (FileVersionInfo.GetVersionInfo file).ProductVersion.Split([| '-'; '+' |]).[0]
        plain (Path.Combine (sdk, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll")),
        plain (Path.Combine (sdk, "Sdks", "Microsoft.NET.Sdk", "analyzers", "Microsoft.CodeAnalysis.NetAnalyzers.dll"))

    /// A library on the package compiler and analyzers (`DebugType` none: a pdb's path is the
    /// output's, which differs between `bin/` and the build's layout, and would be the only
    /// difference in the bytes).
    let project (frameworks: string) =
        let toolset, analyzers = bundledVersions ()
        RecordKit.sdkProject
            (sprintf "<TargetFrameworks>%s</TargetFrameworks><Deterministic>true</Deterministic><DebugType>none</DebugType><RoslynCompilerType>Toolset</RoslynCompilerType>" frameworks)
            (sprintf "<PackageReference Include=\"Microsoft.Net.Compilers.Toolset\" Version=\"%s\" PrivateAssets=\"all\" /><PackageReference Include=\"Microsoft.CodeAnalysis.NetAnalyzers\" Version=\"%s\" PrivateAssets=\"all\" />" toolset analyzers),
        toolset, analyzers

    /// A .NET root with nothing installed: empty `sdk/` and `packs/`.
    let emptyDotnet () =
        let dir = Path.GetFullPath "no-dotnet"
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory (Path.Combine (dir, "sdk")) |> ignore
        Directory.CreateDirectory (Path.Combine (dir, "packs")) |> ignore
        dir

    /// The outputs of `name` built from its lock with `$(DotnetRoot)` at `dotnetRoot`.
    member private x.BuildAgainst (label: string) (name: string) (dotnetRoot: string) =
        let extra = [ "$(DotnetRoot)", dotnetRoot ]
        let compile = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAsWith extra (RecordKit.lockOf name cfg) fwk [] }
        RecordKit.run x.TestOptions label
            [ command "build" { do! Locks.needAllWith extra (!! (name + "/xake.project.json")) "Release" RecordKit.lockOf RecordKit.outOf }
              "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile ]
            [ "build" ]

    /// Imports `name` and checks that its lock names nothing of the SDK. Returns the lock text.
    member private x.Import (name: string) =
        RecordKit.import x.TestOptions ("import-" + name) RecordKit.recordDefault [ sprintf "%s/%s.csproj" name name ] |> ignore
        let lock = File.ReadAllText (RecordKit.lockOf name "Release")
        // nothing of the SDK: no sdk: identity, no prerequisite
        Assert.That(lock, Does.Not.Contain "\"sdk:")
        Assert.That(lock, Does.Not.Contain "Prerequisites")
        Assert.That(lock, Does.Not.Contain "dotnet-sdk")
        lock

    /// Builds `name` from its lock against an empty .NET root and with msbuild, and compares
    /// the outputs of `frameworks` byte for byte.
    member private x.BuildCompare (name: string) (frameworks: string list) =
        x.BuildAgainst ("build-" + name) name (emptyDotnet ())
        RecordKit.runDotnet (Path.GetFullPath name) [ "build"; name + ".csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
        for fwk in frameworks do
            let ours = RecordKit.outOf "Release" fwk name </> name + ".dll"
            let theirs = sprintf "%s/bin/Release/%s/%s.dll" name fwk name
            Assert.That(File.Exists ours, ours)
            Assert.That(File.Exists theirs, theirs)
            Assert.That(File.ReadAllBytes ours, Is.EqualTo (File.ReadAllBytes theirs), sprintf "%s and %s differ" ours theirs)

    [<Test; Category("Integration")>]
    member x.``a net472 and netstandard2.0 library records no SDK and builds without one, as dotnet build does``() =
        let text, toolset, analyzers = project "net472;netstandard2.0"
        RecordKit.write [ "lib"; "out"; "locks"; "obj" ]
            [ "lib/lib.csproj", text
              "lib/C.cs", "namespace Lib { public class C { public static string S(object o) => o.ToString(); public int this[int i] => i * 2; } }\n" ]
        let lock = x.Import "lib"
        x.BuildCompare "lib" [ "net472"; "netstandard2.0" ]
        Assert.That(lock, Does.Contain (sprintf "\"Id\": \"pkg:microsoft.net.compilers.toolset/%s/" toolset))
        Assert.That(lock, Does.Contain (sprintf "\"Id\": \"pkg:microsoft.codeanalysis.netanalyzers/%s/analyzers/dotnet/cs/Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll\"" analyzers))

    /// The targeting pack of net6.0, which no .NET 8 or 10 installation has under `packs/`:
    /// msbuild takes it from the package folder, the lock names it `pack:` with the package's
    /// hashes, and the build against a .NET root with an empty `packs/` reads the same files.
    /// (A pack msbuild took from `packs/` is not byte-identical to the nuget.org package of the
    /// same version -- the package signs its assemblies, and its facades and source generators
    /// are other builds -- so such a lock does not build from the package folder; see
    /// docs/hermetic/lock.md, Prerequisites.)
    [<Test; Category("Integration")>]
    member x.``a targeting pack is taken from the package folder when packs/ lacks it``() =
        let text, _, _ = project "net6.0"
        RecordKit.write [ "ref"; "out"; "locks"; "obj" ]
            [ "ref/ref.csproj", text.Replace ("<Deterministic>", "<CheckEolTargetFramework>false</CheckEolTargetFramework><Deterministic>")
              "ref/C.cs", "namespace Ref { public class C { public static string S(object o) => System.Text.Json.JsonSerializer.Serialize (o); } }\n" ]
        let lock = x.Import "ref"
        let m = Text.RegularExpressions.Regex.Match (lock, "\"Id\": \"pack:microsoft\\.netcore\\.app\\.ref/([^/]+)/ref/net6\\.0/System\\.Runtime\\.dll\"")
        Assert.That(m.Success, "the lock names the targeting pack by identity")
        let version = m.Groups.[1].Value
        let installed = Path.Combine ((DotNetFwk.dotnetRoot ()).Value, "packs", "Microsoft.NETCore.App.Ref", version)
        if Directory.Exists installed then
            Assert.Inconclusive (sprintf "this .NET has the targeting pack %s under packs/: the import hashed those files, not the package's" version)
        // the package folder holds the pack; the .NET root of the build has none
        let packageFolder = Restore.packageRoot Restore.Options.Default
        Assert.That(File.Exists (Path.Combine (packageFolder, "microsoft.netcore.app.ref", version, "ref", "net6.0", "System.Runtime.dll")))
        x.BuildCompare "ref" [ "net6.0" ]
