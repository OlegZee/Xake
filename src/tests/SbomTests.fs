namespace Tests

open System
open System.IO
open System.IO.Compression
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Dotnet.Sbom

/// `Sbom.cycloneDx` (pure JSON rendering) and `Sbom.forAssembly` (the join between a compile's
/// lock and what the restore graph/cache know about its packages) -- both exercised with
/// hand-built fixtures, no restore or compile involved.
[<TestFixture>]
type ``Sbom cycloneDx``() =
    inherit XakeTestBase("sbom")

    let hash alg content : Hash = { Alg = alg; Content = content }

    let sampleComponent bomRef name version hashContent : Component =
        { Type = "library"; BomRef = bomRef; Name = name; Version = version
          Supplier = "Acme"; Purl = sprintf "pkg:nuget/%s@%s" name version
          Hashes = [ hash "SHA-512" hashContent ]; License = "MIT"; Scope = "required"; Components = [] }

    let fooBarNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Foo.Bar</id>
    <version>1.2.3</version>
    <authors>Acme Corp</authors>
    <license type="expression">MIT</license>
  </metadata>
</package>
"""

    let refOnlyNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Ref.Only</id>
    <version>1.0.0</version>
    <authors>Someone</authors>
  </metadata>
</package>
"""

    let fooBarMetadata = """{ "version": 2, "contentHash": "AAAA", "source": "https://api.nuget.org/v3/index.json" }"""
    let refOnlyMetadata = """{ "version": 2, "contentHash": "AAAA", "source": "https://api.nuget.org/v3/index.json" }"""

    let sampleBom hashContent : Bom =
        let root = { Type = "library"; BomRef = "asm:MyAssembly"; Name = "MyAssembly"; Version = "1.0.0"
                     Supplier = ""; Purl = ""; Hashes = [ hash "SHA-256" "deadbeef" ]; License = ""; Scope = ""; Components = [] }
        let pkg = sampleComponent "pkg:nuget/foo.bar@1.2.3" "foo.bar" "1.2.3" hashContent
        { Root = root
          Components = [ pkg ]
          Dependencies = [ "asm:MyAssembly", [ "pkg:nuget/foo.bar@1.2.3" ] ]
          Formulation = [] }

    [<Test>]
    member x.``emits deterministic CycloneDX``() =
        let bom = sampleBom "aabbcc"
        let json1 = cycloneDx bom
        let json2 = cycloneDx bom

        Assert.That (json2, Is.EqualTo json1)
        Assert.That (json1, Does.Contain "\"bomFormat\": \"CycloneDX\"")
        Assert.That (json1, Does.Contain "\"specVersion\": \"1.6\"")
        Assert.That (json1, Does.Not.Contain "timestamp")

        let serialLine = json1.Split '\n' |> Array.find (fun l -> l.Contains "serialNumber")
        Assert.That (serialLine, Does.Match "urn:uuid:[0-9a-f-]{36}")
        let uuid = serialLine.Substring (serialLine.IndexOf "urn:uuid:" + 9)
        // version nibble ('5') is the first character of the third group
        Assert.That (uuid.Split('-').[2].[0], Is.EqualTo '5')

        // changing one hash changes the serial number
        let jsonChanged = cycloneDx (sampleBom "ddeeff")
        Assert.That (jsonChanged, Is.Not.EqualTo json1)
        let changedSerial = jsonChanged.Split '\n' |> Array.find (fun l -> l.Contains "serialNumber")
        Assert.That (changedSerial, Is.Not.EqualTo serialLine)

        // fixed key order
        let indexOf (s: string) = json1.IndexOf s
        Assert.That (indexOf "\"bomFormat\"", Is.LessThan (indexOf "\"specVersion\""))
        Assert.That (indexOf "\"specVersion\"", Is.LessThan (indexOf "\"serialNumber\""))
        Assert.That (indexOf "\"serialNumber\"", Is.LessThan (indexOf "\"version\""))
        Assert.That (indexOf "\"version\"", Is.LessThan (indexOf "\"metadata\""))
        Assert.That (indexOf "\"metadata\"", Is.LessThan (indexOf "\"components\""))
        Assert.That (indexOf "\"components\"", Is.LessThan (indexOf "\"dependencies\""))
        Assert.That (indexOf "\"dependencies\"", Is.LessThan (indexOf "\"formulation\""))

    [<Test>]
    member x.``builds the bom of an assembly from the lock``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgs"

        let fooDir = cacheRoot </> "foo.bar" </> "1.2.3"
        Directory.CreateDirectory (fooDir </> "lib" </> "netstandard2.0") |> ignore
        File.WriteAllText (fooDir </> ".nupkg.metadata", fooBarMetadata)
        File.WriteAllText (fooDir </> "foo.bar.nuspec", fooBarNuspec)
        let fooDll = fooDir </> "lib" </> "netstandard2.0" </> "Foo.Bar.dll"
        File.WriteAllText (fooDll, "foo bar contents")

        let refDir = cacheRoot </> "ref.only" </> "1.0.0"
        Directory.CreateDirectory (refDir </> "ref" </> "netstandard2.0") |> ignore
        File.WriteAllText (refDir </> ".nupkg.metadata", refOnlyMetadata)
        File.WriteAllText (refDir </> "ref.only.nuspec", refOnlyNuspec)
        let refDll = refDir </> "ref" </> "netstandard2.0" </> "Ref.dll"
        File.WriteAllText (refDll, "ref only contents")

        let projectRefDll = Directory.GetCurrentDirectory() </> "sibling" </> "Sibling.dll"
        Directory.CreateDirectory (Path.GetDirectoryName projectRefDll) |> ignore

        let analyzerFile = Directory.GetCurrentDirectory() </> "analyzer.dll"
        File.WriteAllText (analyzerFile, "analyzer contents")

        let assemblyFile = Directory.GetCurrentDirectory() </> "MyAssembly.dll"
        File.WriteAllText (assemblyFile, "the shipped assembly")

        // no compiled reference was ever attributed to this one (nothing in `lock.References`
        // points into its cache entry) but it ships a runtime asset and is reachable from a
        // direct dependency through the graph -- `Nuget.ships` should still call it `required`.
        let runtimeDir = cacheRoot </> "runtime.only" </> "2.0.0"
        Directory.CreateDirectory (runtimeDir </> "runtimes" </> "win" </> "lib" </> "net6.0") |> ignore
        File.WriteAllText (runtimeDir </> "runtimes" </> "win" </> "lib" </> "net6.0" </> "x.dll", "runtime asset")

        // present in the restore graph, no compiled reference, and nothing in its cache entry
        // ships (and it is not reachable from a direct dependency either) -- `excluded`.
        let noShipDir = cacheRoot </> "excluded.noship" </> "3.0.0"
        Directory.CreateDirectory noShipDir |> ignore

        let assets : Nuget.Assets = {
            // mixed-case ids, as `project.assets.json` spells them -- the cache directories
            // above stay lowercase, as the real NuGet cache always does.
            Packages = [ "Foo.Bar", "1.2.3"; "Ref.Only", "1.0.0"; "Runtime.Only", "2.0.0"; "Excluded.NoShip", "3.0.0" ]
            Graph = [ ("Foo.Bar", "1.2.3"), ("Ref.Only", "1.0.0"); ("Foo.Bar", "1.2.3"), ("Runtime.Only", "2.0.0") ]
            Direct = [ "Foo.Bar" ]
            Framework = "netstandard2.0"
        }

        let lock : Lock.Project = {
            Name = "MyAssembly"
            Project = ""
            Directory = ""
            Compiler = { Tool = "csc"; Path = ""; Sha256 = ""; Sdk = "8.0.100" }
            Args = []
            References = [ Lock.hashed fooDll; Lock.hashed refDll; { Path = projectRefDll; Sha256 = "" } ]
            Analyzers = [ Lock.hashed analyzerFile ]
            ProjectRefs = []
            Imports = []
            Generated = []
            Resources = []
            Properties = Map.ofList [ "Version", "1.0.0" ]
        }

        let bom = Sbom.forAssembly cacheRoot assets lock assemblyFile

        Assert.That (bom.Root.BomRef, Is.EqualTo "asm:MyAssembly")
        Assert.That (bom.Root.Hashes, Is.EqualTo [ { Alg = "SHA-256"; Content = Lock.sha256 assemblyFile } ])
        Assert.That (bom.Root.Version, Is.EqualTo "1.0.0")

        let fooComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/Foo.Bar@1.2.3")
        Assert.That (fooComponent.Scope, Is.EqualTo "required")
        Assert.That (fooComponent.Supplier, Is.EqualTo "Acme Corp")
        Assert.That (fooComponent.License, Is.EqualTo "MIT")
        Assert.That (fooComponent.Hashes, Is.EqualTo [ { Alg = "SHA-512"; Content = "000000" } ])
        Assert.That (fooComponent.Components |> List.map (fun f -> f.Hashes), Is.EqualTo [ [ { Alg = "SHA-256"; Content = Lock.sha256 fooDll } ] ])

        let refComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/Ref.Only@1.0.0")
        Assert.That (refComponent.Scope, Is.EqualTo "excluded")

        // no referenced file, but reachable from the direct dependency and ships a `runtimes/`
        // asset -- required, with no nested file evidence (nothing was ever referenced).
        let runtimeComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/Runtime.Only@2.0.0")
        Assert.That (runtimeComponent.Scope, Is.EqualTo "required")
        Assert.That (runtimeComponent.Components, Is.Empty)

        // no referenced file, not reachable from any direct dependency, nothing in its cache
        // entry ships -- excluded.
        let noShipComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/Excluded.NoShip@3.0.0")
        Assert.That (noShipComponent.Scope, Is.EqualTo "excluded")
        Assert.That (noShipComponent.Components, Is.Empty)

        let projectRefComponent = bom.Components |> List.find (fun c -> c.BomRef = "file:" + projectRefDll)
        Assert.That (projectRefComponent.Type, Is.EqualTo "file")
        Assert.That (projectRefComponent.Scope, Is.EqualTo "required")
        Assert.That (projectRefComponent.Hashes, Is.EqualTo (List.empty<Hash>))

        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "asm:MyAssembly") |> snd, Is.EqualTo [ "pkg:nuget/Foo.Bar@1.2.3" ])
        Assert.That (
            bom.Dependencies |> List.find (fun (r, _) -> r = "pkg:nuget/Foo.Bar@1.2.3") |> snd,
            Is.EquivalentTo [ "pkg:nuget/Ref.Only@1.0.0"; "pkg:nuget/Runtime.Only@2.0.0" ])

        Assert.That (bom.Formulation |> List.exists (fun c -> c.BomRef = "file:" + analyzerFile && c.Scope = "excluded"), Is.True)
        Assert.That (bom.Formulation |> List.exists (fun c -> c.BomRef = "tool:csc"), Is.True)

    [<Test>]
    member x.``the emitted json parses``() =
        let bom = sampleBom "112233"
        let json = cycloneDx bom

        let parsed = Fsproj.Json.parse json
        let componentsCount =
            match Fsproj.Json.field "components" parsed with
            | Some v -> (Fsproj.Json.asArray v).Length
            | None -> 0

        Assert.That (componentsCount, Is.EqualTo 1)

    [<Test>]
    member x.``forPackage builds the bom of a nupkg from its assemblies' boms``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgsForPackage"

        // one package shared by both assemblies, each referencing a different file out of it --
        // exercises the "files merged under their package" part of the merge.
        let commonPkgNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12"><id>Common.Pkg</id><version>1.0.0</version><authors>Acme</authors></metadata>
</package>
"""
        let commonDir = cacheRoot </> "common.pkg" </> "1.0.0"
        Directory.CreateDirectory (commonDir </> "lib" </> "netstandard2.0") |> ignore
        File.WriteAllText (commonDir </> ".nupkg.metadata", fooBarMetadata)
        File.WriteAllText (commonDir </> "common.pkg.nuspec", commonPkgNuspec)
        let compADll = commonDir </> "lib" </> "netstandard2.0" </> "CompA.dll"
        let compBDll = commonDir </> "lib" </> "netstandard2.0" </> "CompB.dll"
        File.WriteAllText (compADll, "component seen by assembly A")
        File.WriteAllText (compBDll, "component seen by assembly B")

        let assemblyADll = Directory.GetCurrentDirectory() </> "A.dll"
        let assemblyBDll = Directory.GetCurrentDirectory() </> "B.dll"
        File.WriteAllText (assemblyADll, "assembly A")
        File.WriteAllText (assemblyBDll, "assembly B")

        let assets : Nuget.Assets =
            { Packages = [ "Common.Pkg", "1.0.0" ]; Graph = []; Direct = [ "Common.Pkg" ]; Framework = "netstandard2.0" }

        let lockFor name refDll : Lock.Project =
            { Name = name; Project = ""; Directory = ""
              Compiler = { Tool = "csc"; Path = ""; Sha256 = ""; Sdk = "8.0.100" }
              Args = []; References = [ Lock.hashed refDll ]; Analyzers = []; ProjectRefs = []
              Imports = []; Generated = []; Resources = []; Properties = Map.ofList [ "Version", "1.0.0" ] }

        let bomA = Sbom.forAssembly cacheRoot assets (lockFor "A" compADll) assemblyADll
        let bomB = Sbom.forAssembly cacheRoot assets (lockFor "B" compBDll) assemblyBDll

        // the nupkg itself: a `test.nuspec` at the zip root plus one shipped file, built with
        // `ZipArchive` the way `Sbom.forPackage` reads it back.
        let myPkgNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12"><id>MyPkg</id><version>1.0.0</version><authors>Acme</authors></metadata>
</package>
"""
        let nupkgPath = Directory.GetCurrentDirectory() </> "MyPkg.1.0.0.nupkg"
        do
            use fs = File.Create nupkgPath
            use archive = new ZipArchive (fs, ZipArchiveMode.Create)
            let writeEntry (name: string) (content: string) =
                let entry = archive.CreateEntry name
                use writer = new StreamWriter (entry.Open ())
                writer.Write content
            writeEntry "test.nuspec" myPkgNuspec
            writeEntry "lib/netstandard2.0/A.dll" "the shipped dll"

        let bom = Sbom.forPackage nupkgPath [ bomA; bomB ]

        Assert.That (bom.Root.BomRef, Is.EqualTo "nupkg:MyPkg.1.0.0")
        Assert.That (bom.Root.Name, Is.EqualTo "MyPkg")
        Assert.That (bom.Root.Version, Is.EqualTo "1.0.0")

        let nupkgBytes = File.ReadAllBytes nupkgPath
        let expectedSha256 =
            use a = System.Security.Cryptography.SHA256.Create ()
            a.ComputeHash nupkgBytes |> Array.map (sprintf "%02x") |> String.concat ""
        let expectedSha512 =
            use a = System.Security.Cryptography.SHA512.Create ()
            a.ComputeHash nupkgBytes |> Array.map (sprintf "%02x") |> String.concat ""
        Assert.That (bom.Root.Hashes, Is.EquivalentTo [ { Alg = "SHA-256"; Content = expectedSha256 }; { Alg = "SHA-512"; Content = expectedSha512 } ])

        // both assembly roots nested under the package root, hashes carried, no further nesting
        Assert.That (bom.Root.Components |> List.map (fun c -> c.BomRef), Is.EquivalentTo [ "asm:A"; "asm:B" ])
        Assert.That ((bom.Root.Components |> List.find (fun c -> c.BomRef = "asm:A")).Hashes, Is.EqualTo bomA.Root.Hashes)
        Assert.That (bom.Root.Components |> List.forall (fun c -> c.Components.IsEmpty), Is.True)

        // the shared package appears once at the top level, with both files nested under it
        let mergedPkg = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/Common.Pkg@1.0.0")
        Assert.That (bom.Components |> List.length, Is.EqualTo 1)
        Assert.That (mergedPkg.Components |> List.map (fun f -> f.Name), Is.EquivalentTo [ "CompA.dll"; "CompB.dll" ])

        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "nupkg:MyPkg.1.0.0") |> snd, Is.EquivalentTo [ "asm:A"; "asm:B" ])
        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "asm:A") |> snd, Is.EqualTo [ "pkg:nuget/Common.Pkg@1.0.0" ])
        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "asm:B") |> snd, Is.EqualTo [ "pkg:nuget/Common.Pkg@1.0.0" ])
