namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `HERMETIC=on`: the gate at the end of `Csc.ofSettings`/`Fsc.ofSettings`. One test per
/// requirement: set, the compilation resolves with every path under the project root or the
/// package folder; missing, the message that names the fix.
[<TestFixture>]
type ``Hermetic mode``() =
    inherit XakeTestBase("hermetic")

    let toolsetVersion = "4.12.0"
    let norm (p: string) = p.Replace('\\', '/').TrimEnd '/'
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
        File.WriteAllText (dir </> "a.cs", "public class A {}\n")
        File.WriteAllText (dir </> "a.fs", "module A\nlet a = 1\n")
        dir

    let exactPin version = sprintf """{ "sdk": { "version": "%s", "rollForward": "disable" } }""" version

    /// The variables that make a composed compilation for the SDK's framework hermetic.
    let fullVars (root: string) =
        let fwk = DotNetFwk.sdkFramework root |> Option.get
        [ "HERMETIC", "on"
          "NUGET_PACKAGES", nuget ()
          "CSC_TOOLSET", toolsetVersion
          "NETCORE_REF_VERSION", DotNetFwk.sdkTargetingPackVersion root fwk |> Option.get
          "FSHARP_CORE_VERSION", DotNetFwk.fsharpCoreVersion ]

    let without (names: string list) vars = vars |> List.filter (fun (k, _) -> not (List.contains k names))

    /// Runs `build` in a rule under `root` with `vars`: the resolved value, or the failure text.
    let run (options: ExecOptions) (root: string) (name: string) (vars: (string * string) list) (build: unit -> Recipe<ExecContext, 'T>) =
        let resolved = ref None
        try
            do xake {options with ProjectRoot = root; FileLog = root </> (name + ".log"); ThrowOnError = true; Vars = vars} {
                wantOverride ([name])
                rules [
                    name => recipe {
                        let! v = build ()
                        resolved.Value <- Some v
                    }
                ]
            }
            Choice1Of2 (Option.get resolved.Value)
        with e -> Choice2Of2 (e.ToString())

    let cscIn options root name vars (extraRef: string option) =
        run options root name vars (fun () ->
            csc {
                src !!"a.cs"
                ref (match extraRef with Some r -> Fileset.Empty ++ r | None -> Fileset.Empty)
                out (File.make (root </> name + ".dll"))
                resolve })

    let fscIn options root name vars =
        run options root name vars (fun () -> fsc { src !!"a.fs"; out (File.make (root </> name + ".dll")); resolve })

    let failure = function
        | Choice2Of2 text -> text
        | Choice1Of2 _ -> Assert.Fail "expected the resolve to fail"; ""

    let resolved = function
        | Choice1Of2 v -> v
        | Choice2Of2 text -> Assert.Fail text; Unchecked.defaultof<_>

    let message2 name =
        sprintf "'%s': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable (var \"NUGET_PACKAGES\" \".nuget/packages\", or -d NUGET_PACKAGES=<dir>)" name

    [<Test>]
    member __.``HERMETIC values parse like CI and anything else fails``() =
        for v in [ "on"; "ON"; "true"; "yes"; "1" ] do Assert.That(HermeticMode.parse (Some v), Is.True, v)
        for v in [ "off"; "False"; "no"; "0"; "" ] do Assert.That(HermeticMode.parse (Some v), Is.False, v)
        Assert.That(HermeticMode.parse None, Is.False)

    [<Test; Category("Integration")>]
    member x.``message 1: an unrecognized HERMETIC value fails``() =
        let root = projectRoot "m1" None
        let text = cscIn x.TestOptions root "m1" (("HERMETIC", "maybe") :: without ["HERMETIC"] (fullVars root)) None |> failure
        Assert.That(text, Does.Contain "HERMETIC='maybe': expected on or off")

    [<Test; Category("Integration")>]
    member x.``csc resolves under HERMETIC=on with NUGET_PACKAGES, CSC_TOOLSET and NETCORE_REF_VERSION``() =
        let root = projectRoot "ok-csc" None
        let c : Csc = cscIn x.TestOptions root "okcsc" (fullVars root) None |> resolved
        let roots = [ norm root + "/"; norm (nuget ()) + "/" ]
        for i in Csc.hermeticInputs c do
            Assert.That(roots |> List.exists (fun r -> (norm i.Path).StartsWith r), Is.True, i.Path)
        Assert.That(norm c.Dependencies.Compiler.Path, Does.StartWith (norm (nuget ()) + "/microsoft.net.compilers.toolset/"))

    [<Test; Category("Integration")>]
    member x.``HERMETIC=off checks nothing``() =
        let root = projectRoot "off" None
        let c : Csc = cscIn x.TestOptions root "off" [ "HERMETIC", "off" ] None |> resolved
        Assert.That(c.Name, Is.EqualTo "off")

    [<Test; Category("Integration")>]
    member x.``message 2: no NUGET_PACKAGES script variable``() =
        let root = projectRoot "m2" None
        let text = cscIn x.TestOptions root "m2" (without ["NUGET_PACKAGES"] (fullVars root)) None |> failure
        Assert.That(text, Does.Contain (message2 "m2"))

    [<Test; Category("Integration")>]
    member x.``message 2: the environment variable NUGET_PACKAGES alone does not count``() =
        let root = projectRoot "m2env" None
        let saved = System.Environment.GetEnvironmentVariable "NUGET_PACKAGES"
        System.Environment.SetEnvironmentVariable ("NUGET_PACKAGES", nuget ())
        try
            let text = cscIn x.TestOptions root "m2env" (without ["NUGET_PACKAGES"] (fullVars root)) None |> failure
            Assert.That(text, Does.Contain (message2 "m2env"))
        finally
            System.Environment.SetEnvironmentVariable ("NUGET_PACKAGES", saved)

    [<Test; Category("Integration")>]
    member x.``message 3: csc from the SDK``() =
        let root = projectRoot "m3" None
        let text = cscIn x.TestOptions root "m3" (without ["CSC_TOOLSET"] (fullVars root)) None |> failure
        // the repository's global.json rolls forward to the newest SDK
        let sdk = installedSdks () |> List.last
        let compiler = dotnet () </> "sdk" </> sdk </> "Roslyn" </> "bincore" </> "csc.dll"
        Assert.That(text, Does.Contain (sprintf "'m3': HERMETIC=on: the compiler %s comes from the .NET SDK; set CSC_TOOLSET (or toolset in the csc block) to take csc from the Microsoft.Net.Compilers.Toolset package" compiler))

    [<Test; Category("Integration")>]
    member x.``message 4: the SDK's targeting pack``() =
        let root = projectRoot "m4" None
        let fwk = DotNetFwk.sdkFramework root |> Option.get
        let version = DotNetFwk.sdkTargetingPackVersion root fwk |> Option.get
        let packDir = norm (dotnet ()) + sprintf "/packs/Microsoft.NETCore.App.Ref/%s/ref/%s" version fwk
        Assume.That(Directory.Exists packDir, Is.True, "the SDK's targeting pack is not installed")
        let text = cscIn x.TestOptions root "m4" (without ["NETCORE_REF_VERSION"] (fullVars root)) None |> failure
        let expected =
            sprintf "'m4': HERMETIC=on: the reference assemblies for %s come from %s under the .NET SDK; add a %s version to NETCORE_REF_VERSION (this SDK bundles %s)"
                fwk packDir (fwk.Substring 3) version
        Assert.That(text, Does.Contain expected)
        // once per framework, not once per assembly
        Assert.That(text.Split([| expected |], System.StringSplitOptions.None).Length, Is.EqualTo 2)

    [<Test; Category("Integration")>]
    member x.``message 5: fsc's default FSharp.Core from the SDK``() =
        let sdk = installedSdks () |> List.last
        let root = projectRoot "m5" (Some (exactPin sdk))
        let text = fscIn x.TestOptions root "m5" (without ["FSHARP_CORE_VERSION"] (fullVars root)) |> failure
        let fsharpCore = dotnet () </> "sdk" </> sdk </> "FSharp" </> "FSharp.Core.dll"
        Assert.That(text, Does.Contain (sprintf "'m5': HERMETIC=on: the default FSharp.Core %s comes from the .NET SDK; set FSHARP_CORE_VERSION to take the FSharp.Core package, or reference an FSharp.Core.dll" fsharpCore))
        Assert.That(text, Does.Not.Contain "fsc has no NuGet package", "the exact pin covers the compiler")

    [<Test; Category("Integration")>]
    member x.``message 6: fsc without an exact global.json pin``() =
        let root = projectRoot "m6" (Some """{ "sdk": { "version": "8.0.100", "rollForward": "latestMajor" } }""")
        let text = fscIn x.TestOptions root "m6" (fullVars root) |> failure
        let pinFile = root </> "global.json"
        let sdk = installedSdks () |> List.last   // latestMajor: the newest
        Assert.That(text, Does.Contain (sprintf "'m6': HERMETIC=on: fsc has no NuGet package, so the .NET SDK is a prerequisite and must be pinned exactly (found: 8.0.100 rollForward:latestMajor (%s)); pin it in global.json: { \"sdk\": { \"version\": \"%s\", \"rollForward\": \"disable\" } }" pinFile sdk))

    [<Test; Category("Integration")>]
    member x.``message 7: the pinned SDK is not installed``() =
        let root = projectRoot "m7" (Some (exactPin "99.0.100"))
        let text = fscIn x.TestOptions root "m7" (fullVars root) |> failure
        let pinFile = root </> "global.json"
        Assert.That(text, Does.Contain (sprintf "'m7': HERMETIC=on: global.json (%s) pins the .NET SDK 99.0.100, but '%s' does not have it; install it (dotnet-install --version 99.0.100 --install-dir %s)" pinFile (dotnet ()) (dotnet ())))

    [<Test; Category("Integration")>]
    member x.``message 8: a reference outside the roots, reported after the compiler``() =
        let root = projectRoot "m8" None
        let outsideDir = Path.Combine (Path.GetTempPath (), "xake-hermetic-outside")
        Directory.CreateDirectory outsideDir |> ignore
        let outside = Path.GetFullPath (outsideDir </> "Outside.dll")
        File.WriteAllText (outside, "")
        let text = cscIn x.TestOptions root "m8" (without ["CSC_TOOLSET"] (fullVars root)) (Some outside) |> failure
        let m8 =
            sprintf "'m8': HERMETIC=on: %s is outside the project root '%s' and the package folder '%s', and no prerequisite covers it"
                (outside.Replace('\\', '/')) root (nuget ())
        Assert.That(text, Does.Contain m8)
        // all violations together, the compiler first
        let m3 = text.IndexOf "'m8': HERMETIC=on: the compiler "
        Assert.That(m3, Is.GreaterThanOrEqualTo 0)
        Assert.That(m3, Is.LessThan (text.IndexOf m8))

    [<Test; Category("Integration")>]
    member x.``fsc with an exact global.json pin resolves to the SDK compiler``() =
        let sdk = installedSdks () |> List.last
        let root = projectRoot "pinned" (Some (exactPin sdk))
        let f : Fsc = fscIn x.TestOptions root "pinned" (fullVars root) |> resolved
        let compiler = dotnet () </> "sdk" </> sdk </> "FSharp" </> "fsc.dll"
        Assert.That(f.Dependencies.Compiler.Path, Is.EqualTo compiler)
        Assert.That(DotNetFwk.sdkPrerequisite root compiler, Is.EqualTo (Some (sdk, root </> "global.json")))
        Assert.That(DotNetFwk.sdkPrerequisite root (dotnet () </> "packs" </> "x.dll"), Is.EqualTo None)

    [<Test>]
    member __.``global.json pins are read like the host reads them``() =
        let write name text =
            let dir = Path.GetFullPath name
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText (dir </> "global.json", text)
            dir, dir </> "global.json"
        let dir, file = write "gj-exact" """{ "sdk": { "rollForward": "disable", "version": "10.0.401" } }"""
        Assert.That(DotNetFwk.globalJsonPin dir, Is.EqualTo (DotNetFwk.Exact ("10.0.401", file)))
        let dir, file = write "gj-patch" """{ "sdk": { "version": "10.0.401" } }"""
        Assert.That(DotNetFwk.globalJsonPin dir, Is.EqualTo (DotNetFwk.RollsForward ("10.0.401", "latestPatch", file)))
        let dir, file = write "gj-none" """{ "msbuild-sdks": { } }"""
        Assert.That(DotNetFwk.globalJsonPin dir, Is.EqualTo (DotNetFwk.NoVersion file))

    /// The check on its own, over tokenized paths, as the hermetic lock uses it.
    [<Test>]
    member __.``HermeticMode.check works over tokenized roots``() =
        let roots : HermeticMode.Roots =
            { ProjectRoots = [ "$(ProjectRoot)" ]
              PackageRoot = "$(NuGetPackageRoot)"
              PackageRootDeclared = true
              DotnetRoot = Some "$(DotnetRoot)"
              SdkPin = DotNetFwk.Exact ("10.0.401", "$(ProjectRoot)/global.json")
              SdkInstalled = fun _ -> true
              TargetingPackVersion = fun _ -> None }
        let input role path : HermeticMode.Input = { Role = role; Path = path }
        let ok =
            [ input (HermeticMode.Compiler "fsc") "$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll"
              input HermeticMode.Reference "$(NuGetPackageRoot)/fsharp.core/8.0.100/lib/netstandard2.1/FSharp.Core.dll"
              input HermeticMode.Other "$(ProjectRoot)/src/a.fs" ]
        Assert.That(HermeticMode.check "t" roots ok, Is.Empty)
        let bad =
            [ input HermeticMode.Other "/elsewhere/b.fs"
              input HermeticMode.Reference "$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/System.Runtime.dll"
              input HermeticMode.Reference "$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/mscorlib.dll"
              input (HermeticMode.Compiler "csc") "$(DotnetRoot)/sdk/10.0.401/Roslyn/bincore/csc.dll" ]
        let messages = HermeticMode.check "t" roots bad
        Assert.That(messages.Length, Is.EqualTo 3)
        Assert.That(messages.[0], Does.Contain "the compiler $(DotnetRoot)/sdk/10.0.401/Roslyn/bincore/csc.dll comes from the .NET SDK")
        Assert.That(messages.[1], Is.EqualTo "'t': HERMETIC=on: the reference assemblies for net10.0 come from $(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0 under the .NET SDK; add a 10.0 version to NETCORE_REF_VERSION")
        Assert.That(messages.[2], Is.EqualTo "'t': HERMETIC=on: /elsewhere/b.fs is outside the project root '$(ProjectRoot)' and the package folder '$(NuGetPackageRoot)', and no prerequisite covers it")
