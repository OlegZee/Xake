namespace Tests

open System
open System.IO
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

        let assets : Nuget.Assets = {
            Packages = [ "foo.bar", "1.2.3"; "ref.only", "1.0.0" ]
            Graph = [ ("foo.bar", "1.2.3"), ("ref.only", "1.0.0") ]
            Direct = [ "foo.bar" ]
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

        let fooComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/foo.bar@1.2.3")
        Assert.That (fooComponent.Scope, Is.EqualTo "required")
        Assert.That (fooComponent.Supplier, Is.EqualTo "Acme Corp")
        Assert.That (fooComponent.License, Is.EqualTo "MIT")
        Assert.That (fooComponent.Hashes, Is.EqualTo [ { Alg = "SHA-512"; Content = "000000" } ])
        Assert.That (fooComponent.Components |> List.map (fun f -> f.Hashes), Is.EqualTo [ [ { Alg = "SHA-256"; Content = Lock.sha256 fooDll } ] ])

        let refComponent = bom.Components |> List.find (fun c -> c.BomRef = "pkg:nuget/ref.only@1.0.0")
        Assert.That (refComponent.Scope, Is.EqualTo "excluded")

        let projectRefComponent = bom.Components |> List.find (fun c -> c.BomRef = "file:" + projectRefDll)
        Assert.That (projectRefComponent.Type, Is.EqualTo "file")
        Assert.That (projectRefComponent.Scope, Is.EqualTo "required")
        Assert.That (projectRefComponent.Hashes, Is.EqualTo (List.empty<Hash>))

        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "asm:MyAssembly") |> snd, Is.EqualTo [ "pkg:nuget/foo.bar@1.2.3" ])
        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "pkg:nuget/foo.bar@1.2.3") |> snd, Is.EqualTo [ "pkg:nuget/ref.only@1.0.0" ])

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
