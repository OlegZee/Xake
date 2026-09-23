namespace Tests

open System
open System.IO
open System.IO.Compression
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Dotnet.Sbom

/// `Sbom.cycloneDx` (pure JSON rendering) and `Sbom.forAssembly` (the join between a compile's
/// lock entry -- references and the package graph recorded at import -- and what the cache
/// knows about its packages) -- both exercised with hand-built fixtures, no restore or compile
/// involved.
[<TestFixture>]
type ``Sbom cycloneDx``() =
    inherit XakeTestBase("sbom")

    let hash alg content : Hash = { Alg = alg; Content = content }

    let sampleComponent bomRef name version hashContent : Component =
        { Type = "library"; BomRef = bomRef; Name = name; Version = version
          Supplier = "Acme"; Purl = sprintf "pkg:nuget/%s@%s" name version
          Hashes = [ hash "SHA-512" hashContent ]; License = "MIT"; Scope = "required"; Components = []; Properties = [] }

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

    /// A lock entry with just what `forAssembly` reads: references, analyzers, packages, the
    /// compiler and the version property.
    let entryOf name (references: Lock.Hashed list) (analyzers: Lock.Hashed list) (packages: Lock.Package list) : Lock.Entry =
        { Name = name
          Framework = "netstandard2.0"
          Evaluation = { Project = ""; ProjectRefs = []; Imports = []; Sdk = "8.0.100"; SdkPin = None; Properties = Map.ofList [ "Version", "1.0.0" ] }
          Compilation = { Directory = ""; Options = []; Defines = []; Sources = []; Generated = []; Resources = [] }
          Dependencies =
            { Compiler = { Tool = "csc"; Path = ""; Sha256 = ""; Version = "4.11.0" }
              References = references |> List.map (fun r -> { Path = r.Path; Sha256 = r.Sha256; Alias = "" })
              Analyzers = analyzers
              Packages = packages } }

    let sampleBom hashContent : Bom =
        let root = { Type = "library"; BomRef = "asm:MyAssembly"; Name = "MyAssembly"; Version = "1.0.0"
                     Supplier = ""; Purl = ""; Hashes = [ hash "SHA-256" "deadbeef" ]; License = ""; Scope = ""; Components = []; Properties = [] }
        let pkg = sampleComponent "pkg:nuget/foo.bar@1.2.3" "foo.bar" "1.2.3" hashContent
        { Root = root
          Components = [ pkg ]
          Dependencies = [ "asm:MyAssembly", [ "pkg:nuget/foo.bar@1.2.3" ] ]
          Formulation = []; Compositions = []; Annotations = [] }

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

        // mixed-case ids, as `project.assets.json` spells them -- the cache directories above
        // stay lowercase, as the real NuGet cache always does. The graph is the lock's own
        // (`Project.packages` at import), sha512 included.
        let packages : Lock.Package list =
            [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA"; Direct = true; DependsOn = [ "Ref.Only"; "Runtime.Only" ] }
              { Id = "Ref.Only"; Version = "1.0.0"; Sha512 = "AAAA"; Direct = false; DependsOn = [] }
              { Id = "Runtime.Only"; Version = "2.0.0"; Sha512 = ""; Direct = false; DependsOn = [] }
              { Id = "Excluded.NoShip"; Version = "3.0.0"; Sha512 = ""; Direct = false; DependsOn = [] } ]

        let lock = entryOf "MyAssembly" [ Lock.hashed fooDll; Lock.hashed refDll; { Path = projectRefDll; Sha256 = "" } ] [ Lock.hashed analyzerFile ] packages

        let bom = Sbom.forAssembly cacheRoot lock assemblyFile

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
        Assert.That (bom.Formulation |> List.exists (fun c -> c.BomRef = "tool:csc" && c.Version = "4.11.0"), Is.True)
        Assert.That (bom.Formulation |> List.exists (fun c -> c.BomRef = "tool:dotnet-sdk" && c.Version = "8.0.100"), Is.True)

    [<Test>]
    member x.``the emitted json parses``() =
        let bom = sampleBom "112233"
        let json = cycloneDx bom

        let parsed = Json.parse json
        let componentsCount =
            match Json.field "components" parsed with
            | Some v -> (Json.asArray v).Length
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

        let packages : Lock.Package list = [ { Id = "Common.Pkg"; Version = "1.0.0"; Sha512 = "AAAA"; Direct = true; DependsOn = [] } ]
        let lockFor name refDll = entryOf name [ Lock.hashed refDll ] [] packages

        let bomA = Sbom.forAssembly cacheRoot (lockFor "A" compADll) assemblyADll
        let bomB = Sbom.forAssembly cacheRoot (lockFor "B" compBDll) assemblyBDll

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

/// `Sbom.forPackageScoped`: the package-scope document the SDP RFC asks for (nuget-sbom.md
/// "Package scope") -- tier 1 from the nupkg's shipped files, tier 2 from the nuspec, nothing
/// transitive, the boundary declared -- and `Verify.sbomPackageScope`, its acceptance checks.
[<TestFixture>]
type ``Sbom package scope``() =
    inherit XakeTestBase("sbom-scope")

    let write (path: string) (content: string) =
        Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore
        File.WriteAllText (path, content)
        path

    let entryOf name framework (references: Lock.Hashed list) (packages: Lock.Package list) : Lock.Entry =
        { Name = name
          Framework = framework
          Evaluation = { Project = ""; ProjectRefs = []; Imports = []; Sdk = "8.0.100"; SdkPin = None; Properties = Map.ofList [ "Version", "1.0.0" ] }
          Compilation = { Directory = ""; Options = []; Defines = []; Sources = []; Generated = []; Resources = [] }
          Dependencies =
            { Compiler = { Tool = "csc"; Path = ""; Sha256 = ""; Version = "4.11.0" }
              References = references |> List.map (fun r -> { Path = r.Path; Sha256 = r.Sha256; Alias = "" })
              Analyzers = []
              Packages = packages } }

    let myPkgNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>MyPkg</id>
    <version>1.0.0</version>
    <authors>Acme</authors>
    <license type="expression">MIT</license>
    <dependencies>
      <group targetFramework=".NETStandard2.0">
        <dependency id="Foo.Bar" version="[1.2.3, )" />
        <dependency id="DS.Internal" version="2.0.0" />
        <dependency id="CycloneDX.Core" version="1.0.0" />
        <dependency id="Unresolved.Pkg" version="9.9.9" />
      </group>
      <group targetFramework="net8.0">
        <dependency id="Only.Net8" version="1.0.0" />
      </group>
    </dependencies>
  </metadata>
</package>
"""

    let fooBarNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package><metadata><id>Foo.Bar</id><version>1.2.3</version><authors>Foo Authors</authors><license type="expression">Apache-2.0</license></metadata></package>
"""
    let internalNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package><metadata><id>DS.Internal</id><version>2.0.0</version><authors>Acme</authors><license type="expression">LicenseRef-Proprietary</license></metadata></package>
"""
    let metadata = """{ "version": 2, "contentHash": "AAAA", "source": "https://api.nuget.org/v3/index.json" }"""

    /// A nupkg with content for two TFMs, a satellite, a native, a `ref/` facade, docs,
    /// readme/icon and a stale `sbom/` tree -- plus the restore-scope BOM of its
    /// netstandard2.0 assembly, whose lock graph carries a transitive package the nuspec does
    /// not declare.
    member private x.Fixture () =
        let here = Directory.GetCurrentDirectory ()
        let cacheRoot = here </> "pkgs"
        let fooDll = write (cacheRoot </> "foo.bar" </> "1.2.3" </> "lib" </> "netstandard2.0" </> "Foo.Bar.dll") "foo bar bytes"
        write (cacheRoot </> "foo.bar" </> "1.2.3" </> "foo.bar.nuspec") fooBarNuspec |> ignore
        write (cacheRoot </> "foo.bar" </> "1.2.3" </> ".nupkg.metadata") metadata |> ignore
        let internalDll = write (cacheRoot </> "ds.internal" </> "2.0.0" </> "lib" </> "netstandard2.0" </> "DS.Internal.dll") "internal bytes"
        write (cacheRoot </> "ds.internal" </> "2.0.0" </> "ds.internal.nuspec") internalNuspec |> ignore
        write (cacheRoot </> "ds.internal" </> "2.0.0" </> ".nupkg.metadata") metadata |> ignore
        let transitiveDll = write (cacheRoot </> "transitive.pkg" </> "0.1.0" </> "lib" </> "netstandard2.0" </> "Transitive.dll") "transitive bytes"

        let packages : Lock.Package list =
            [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA"; Direct = true; DependsOn = [ "Transitive.Pkg" ] }
              { Id = "DS.Internal"; Version = "2.0.0"; Sha512 = "AAAA"; Direct = true; DependsOn = [] }
              { Id = "Transitive.Pkg"; Version = "0.1.0"; Sha512 = ""; Direct = false; DependsOn = [] } ]

        let build = here </> "build"
        let asmDll = write (build </> "netstandard2.0" </> "MyAsm.dll") "my assembly, netstandard2.0"
        let asmNet8 = write (build </> "net8.0" </> "MyAsm.dll") "my assembly, net8.0"
        let asmDoc = write (build </> "MyAsm.xml") "<doc/>"
        let satellite = write (build </> "de" </> "MyAsm.resources.dll") "german strings"
        let native = write (build </> "native.dll") "native bytes"
        let facade = write (build </> "ref" </> "MyAsm.dll") "reference facade"
        let readme = write (build </> "readme.md") "# readme"
        let icon = write (build </> "icon.png") "png"
        let stale = write (build </> "stale.json") "{}"
        let props = write (build </> "MyPkg.props") "<Project/>"

        let entry = entryOf "MyAsm" "netstandard2.0" [ Lock.hashed fooDll; Lock.hashed internalDll; Lock.hashed transitiveDll ] packages
        let asmBom = Sbom.forAssembly cacheRoot entry asmDll

        let nuspecFile = write (here </> "MyPkg.nuspec") myPkgNuspec
        let nupkg = here </> "MyPkg.1.0.0.nupkg"
        Pack.nupkg nupkg nuspecFile
            [ { Pack.Path = "lib/netstandard2.0/MyAsm.dll"; Pack.Source = asmDll }
              { Pack.Path = "lib/netstandard2.0/MyAsm.xml"; Pack.Source = asmDoc }
              { Pack.Path = "lib/netstandard2.0/de/MyAsm.resources.dll"; Pack.Source = satellite }
              { Pack.Path = "lib/net8.0/MyAsm.dll"; Pack.Source = asmNet8 }
              { Pack.Path = "ref/netstandard2.0/MyAsm.dll"; Pack.Source = facade }
              { Pack.Path = "runtimes/win-x64/native/native.dll"; Pack.Source = native }
              { Pack.Path = "build/netstandard2.0/MyPkg.props"; Pack.Source = props }
              { Pack.Path = "build/net8.0/MyPkg.props"; Pack.Source = props }
              { Pack.Path = "readme.md"; Pack.Source = readme }
              { Pack.Path = "icon.png"; Pack.Source = icon }
              { Pack.Path = "sbom/netstandard2.0/bom.cdx.json"; Pack.Source = stale } ]
            Pack.defaultOptions
        nupkg, asmBom, asmDll

    [<Test>]
    member x.``tier 1 is the nupkg's shipped files for the framework, hashed``() =
        let nupkg, asmBom, asmDll = x.Fixture ()
        let bom = Sbom.forPackageScoped nupkg "netstandard2.0" [ asmBom ]

        Assert.That (bom.Root.BomRef, Is.EqualTo "nupkg:MyPkg.1.0.0")
        Assert.That (bom.Root.Name, Is.EqualTo "MyPkg")
        Assert.That (bom.Root.Version, Is.EqualTo "1.0.0")
        Assert.That (bom.Root.Purl, Is.EqualTo "pkg:nuget/MyPkg@1.0.0")
        Assert.That (bom.Root.Hashes, Is.Empty)
        Assert.That (bom.Root.Properties, Is.EqualTo [ { Name = "xake:nuget:targetFramework"; Value = "netstandard2.0" } ])

        let paths = bom.Root.Components |> List.map (fun c -> c.Properties |> List.find (fun p -> p.Name = "xake:nuget:path") |> fun p -> p.Value)
        Assert.That (paths, Is.EquivalentTo [ "lib/netstandard2.0/MyAsm.dll"; "lib/netstandard2.0/de/MyAsm.resources.dll"; "runtimes/win-x64/native/native.dll"; "build/netstandard2.0/MyPkg.props" ])

        // our own assembly, recognised by hash: library, with the assembly BOM's name/version
        let own = bom.Root.Components |> List.find (fun c -> c.BomRef = "nupkg:MyPkg.1.0.0/lib/netstandard2.0/MyAsm.dll")
        Assert.That (own.Type, Is.EqualTo "library")
        Assert.That (own.Name, Is.EqualTo "MyAsm")
        Assert.That (own.Version, Is.EqualTo "1.0.0")
        Assert.That (own.Hashes |> List.map (fun h -> h.Alg), Is.EqualTo [ "SHA-256"; "SHA-512" ])
        Assert.That ((own.Hashes |> List.find (fun h -> h.Alg = "SHA-256")).Content, Is.EqualTo (Lock.sha256 asmDll))

        let satellite = bom.Root.Components |> List.find (fun c -> c.Name = "MyAsm.resources.dll")
        Assert.That (satellite.Type, Is.EqualTo "library")
        Assert.That (satellite.Version, Is.EqualTo "")
        let props = bom.Root.Components |> List.find (fun c -> c.Name = "MyPkg.props")
        Assert.That (props.Type, Is.EqualTo "file")

    [<Test>]
    member x.``tier 2 is the nuspec group, verbatim, resolved from the evidence; nothing transitive``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let bom = Sbom.forPackageScoped nupkg "netstandard2.0" [ asmBom ]

        Assert.That (bom.Components |> List.map (fun c -> c.Name), Is.EquivalentTo [ "Foo.Bar"; "DS.Internal"; "Unresolved.Pkg" ])

        let foo = bom.Components |> List.find (fun c -> c.Name = "Foo.Bar")
        Assert.That (foo.BomRef, Is.EqualTo "pkg:nuget/Foo.Bar@1.2.3")
        Assert.That (foo.Version, Is.EqualTo "1.2.3")
        Assert.That (foo.Properties, Is.EqualTo [ { Name = "dt:nuget:declaredVersionRange"; Value = "[1.2.3, )" } ])
        Assert.That (foo.Supplier, Is.EqualTo "Foo Authors")
        Assert.That (foo.License, Is.EqualTo "Apache-2.0")
        Assert.That (foo.Hashes, Is.EqualTo [ { Alg = "SHA-512"; Content = "000000" } ])
        Assert.That (foo.Components, Is.Empty)

        // internal: one line -- version and purl resolved, but no supplier/license/hash pulled from the graph
        let internalPkg = bom.Components |> List.find (fun c -> c.Name = "DS.Internal")
        Assert.That (internalPkg.BomRef, Is.EqualTo "pkg:nuget/DS.Internal@2.0.0")
        Assert.That (internalPkg.Supplier, Is.EqualTo "")
        Assert.That (internalPkg.License, Is.EqualTo "")
        Assert.That (internalPkg.Hashes, Is.Empty)
        Assert.That (internalPkg.Properties, Is.EqualTo [ { Name = "dt:nuget:declaredVersionRange"; Value = "2.0.0" } ])

        // declared but nowhere in the restore evidence: still listed, purl without a version
        let unresolved = bom.Components |> List.find (fun c -> c.Name = "Unresolved.Pkg")
        Assert.That (unresolved.Version, Is.EqualTo "")
        Assert.That (unresolved.Purl, Is.EqualTo "pkg:nuget/Unresolved.Pkg")

        // dependencies[]: root -> own assembly + tier 2; assembly -> what it directly used; never a tier-2 ref
        let asmRef = "nupkg:MyPkg.1.0.0/lib/netstandard2.0/MyAsm.dll"
        Assert.That (bom.Dependencies |> List.map fst, Is.EquivalentTo [ "nupkg:MyPkg.1.0.0"; asmRef ])
        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = "nupkg:MyPkg.1.0.0") |> snd,
                     Is.EqualTo [ asmRef; "pkg:nuget/DS.Internal@2.0.0"; "pkg:nuget/Foo.Bar@1.2.3"; "pkg:nuget/Unresolved.Pkg" ])
        Assert.That (bom.Dependencies |> List.find (fun (r, _) -> r = asmRef) |> snd,
                     Is.EqualTo [ "pkg:nuget/DS.Internal@2.0.0"; "pkg:nuget/Foo.Bar@1.2.3" ])

        Assert.That (bom.Formulation, Is.Empty)

    [<Test>]
    member x.``declares the boundary and renders deterministically``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let bom = Sbom.forPackageScoped nupkg "netstandard2.0" [ asmBom ]

        let complete = bom.Compositions |> List.find (fun c -> c.Aggregate = "complete")
        let incomplete = bom.Compositions |> List.find (fun c -> c.Aggregate = "incomplete")
        Assert.That (complete.Assemblies, Is.EqualTo [ "nupkg:MyPkg.1.0.0" ])
        Assert.That (incomplete.Dependencies, Is.EqualTo [ "nupkg:MyPkg.1.0.0"; "nupkg:MyPkg.1.0.0/lib/netstandard2.0/MyAsm.dll" ])
        let annotation = bom.Annotations |> List.exactlyOne
        Assert.That (annotation.Subjects, Is.EqualTo [ "nupkg:MyPkg.1.0.0" ])
        Assert.That (annotation.Text, Does.Contain "consuming product")
        Assert.That (annotation.Timestamp, Does.Match @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

        let json1 = Sbom.cycloneDx bom
        let json2 = Sbom.cycloneDx (Sbom.forPackageScoped nupkg "netstandard2.0" [ asmBom ])
        Assert.That (json2, Is.EqualTo json1)
        Assert.That (json1, Does.Contain "\"compositions\"")
        Assert.That (json1, Does.Contain "\"annotations\"")
        Assert.That (json1, Does.Contain "\"dt:nuget:declaredVersionRange\"")
        Assert.That (json1, Does.Not.Contain "Transitive.Pkg")
        Assert.That (json1, Does.Not.Contain "CycloneDX.Core")
        Assert.That (json1, Does.Not.Contain "tool:csc")
        let parsed = Json.parse json1
        Assert.That (Json.field "compositions" parsed |> Option.map (Json.asArray >> List.length), Is.EqualTo (Some 2))
        Assert.That (Sbom.packageSbomPath "netstandard2.0", Is.EqualTo "sbom/netstandard2.0/bom.cdx.json")

    [<Test>]
    member x.``one document per framework``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let bom = Sbom.forPackageScoped nupkg "net8.0" [ asmBom ]
        let paths = bom.Root.Components |> List.map (fun c -> c.Properties |> List.find (fun p -> p.Name = "xake:nuget:path") |> fun p -> p.Value)
        Assert.That (paths, Is.EquivalentTo [ "lib/net8.0/MyAsm.dll"; "runtimes/win-x64/native/native.dll"; "build/net8.0/MyPkg.props" ])
        // the net8.0 assembly matches no input BOM by hash: a plain library with the file's name
        let own = bom.Root.Components |> List.find (fun c -> c.Name = "MyAsm.dll")
        Assert.That (own.Version, Is.EqualTo "")
        Assert.That (bom.Components |> List.map (fun c -> c.Name), Is.EqualTo [ "Only.Net8" ])
        Assert.That (bom.Dependencies |> List.map fst, Is.EqualTo [ "nupkg:MyPkg.1.0.0" ])

    [<Test>]
    member x.``the verifier passes the generated document and catches tampering``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let bom = Sbom.forPackageScoped nupkg "netstandard2.0" [ asmBom ]
        Assert.That (Verify.sbomPackageScope nupkg "netstandard2.0" bom, Is.Empty)

        // a shipped binary without a component (3.2), a tier-2 ref in dependencies[] (3.4),
        // a range that drifted from the nuspec (3.3), a component the nuspec never declared (3.1)
        let drift (c: Component) =
            if c.Name = "Foo.Bar" then { c with Properties = [ { Name = "dt:nuget:declaredVersionRange"; Value = "1.2.3" } ] } else c
        let undeclared = { bom.Components.Head with BomRef = "pkg:nuget/Transitive.Pkg@0.1.0"; Name = "Transitive.Pkg"; Purl = "pkg:nuget/Transitive.Pkg@0.1.0" }
        let tampered =
            { bom with
                Root = { bom.Root with Components = bom.Root.Components |> List.filter (fun c -> c.Name <> "native.dll") }
                Components = (bom.Components |> List.map drift) @ [ undeclared ]
                Dependencies = bom.Dependencies @ [ "pkg:nuget/Foo.Bar@1.2.3", [] ]
                Compositions = [] }
        let findings = Verify.sbomPackageScope nupkg "netstandard2.0" tampered
        Assert.That (findings |> List.exists (fun f -> f.StartsWith "3.2" && f.Contains "native.dll"), Is.True, String.concat "\n" findings)
        Assert.That (findings |> List.exists (fun f -> f.StartsWith "3.3" && f.Contains "Foo.Bar"), Is.True)
        Assert.That (findings |> List.exists (fun f -> f.StartsWith "3.1" && f.Contains "Transitive.Pkg"), Is.True)
        Assert.That (findings |> List.exists (fun f -> f.StartsWith "3.4" && f.Contains "tier-2"), Is.True)
        Assert.That (findings |> List.filter (fun f -> f.StartsWith "3.4" && f.Contains "composition") |> List.length, Is.EqualTo 2)

    [<Test>]
    member x.``options override how content is collected and dependencies classified``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let registry (path: string) : Component list =
            if path.EndsWith "MyAsm.dll" then
                [ { Type = "library"; BomRef = "vendored:Tiny.Json@1.0"; Name = "Tiny.Json"; Version = "1.0"; Supplier = ""; Purl = "pkg:nuget/Tiny.Json@1.0"
                    Hashes = []; License = "MIT"; Scope = ""; Components = []; Properties = [ { Name = "xake:pedigree"; Value = "merged" } ] } ]
            else []
        let options =
            { Sbom.defaultPackageScope with
                // `.props` are not content here; `ref/` facades are
                IsPlumbing = fun path -> Sbom.PackageScope.plumbing path || path.EndsWith ".props"
                IsShipped = fun tfm path -> Sbom.PackageScope.shippedFor tfm path || path.StartsWith ("ref/" + tfm + "/")
                // Foo.* is our own; nothing is tooling
                IsInternal = Sbom.PackageScope.idPrefixes [ "Foo." ]
                IsTooling = fun _ -> false
                Subcomponents = registry
                DeclaredRangeProperty = "acme:range"
                RootProperties = [ { Name = "acme:brand"; Value = "Blue" } ]
                BoundaryText = "depth 1 only"
                AnnotationTimestamp = "2026-01-01T00:00:00Z"
                KeepFormulation = true }
        let bom = Sbom.forPackageScopedWith options nupkg "netstandard2.0" [ asmBom ]

        let paths = bom.Root.Components |> List.map (fun c -> c.Properties |> List.find (fun p -> p.Name = Sbom.pathProperty) |> fun p -> p.Value)
        Assert.That (paths, Is.EquivalentTo [ "lib/netstandard2.0/MyAsm.dll"; "lib/netstandard2.0/de/MyAsm.resources.dll"; "runtimes/win-x64/native/native.dll"; "ref/netstandard2.0/MyAsm.dll" ])
        let own = bom.Root.Components |> List.find (fun c -> c.BomRef = "nupkg:MyPkg.1.0.0/lib/netstandard2.0/MyAsm.dll")
        Assert.That (own.Components |> List.map (fun c -> c.Name), Is.EqualTo [ "Tiny.Json" ])

        Assert.That (bom.Components |> List.map (fun c -> c.Name), Is.EquivalentTo [ "Foo.Bar"; "DS.Internal"; "CycloneDX.Core"; "Unresolved.Pkg" ])
        let foo = bom.Components |> List.find (fun c -> c.Name = "Foo.Bar")
        Assert.That (foo.Supplier, Is.EqualTo "")
        Assert.That (foo.Properties, Is.EqualTo [ { Name = "acme:range"; Value = "[1.2.3, )" } ])
        let internalPkg = bom.Components |> List.find (fun c -> c.Name = "DS.Internal")
        Assert.That (internalPkg.Supplier, Is.EqualTo "Acme")

        Assert.That (bom.Root.Properties, Is.EqualTo [ { Name = "xake:nuget:targetFramework"; Value = "netstandard2.0" }; { Name = "acme:brand"; Value = "Blue" } ])
        Assert.That ((bom.Annotations |> List.exactlyOne).Text, Is.EqualTo "depth 1 only")
        Assert.That ((bom.Annotations |> List.exactlyOne).Timestamp, Is.EqualTo "2026-01-01T00:00:00Z")
        Assert.That (bom.Formulation |> List.exists (fun c -> c.BomRef = "tool:csc"), Is.True)

        // the verifier applies the same options -- and the default rule would reject this document
        Assert.That (Verify.sbomPackageScopeWith options nupkg "netstandard2.0" bom, Is.Empty)
        Assert.That (Verify.sbomPackageScope nupkg "netstandard2.0" bom, Is.Not.Empty)

    [<Test>]
    member x.``no boundary text means no annotation, and the verifier says so``() =
        let nupkg, asmBom, _ = x.Fixture ()
        let options = { Sbom.defaultPackageScope with BoundaryText = "" }
        let bom = Sbom.forPackageScopedWith options nupkg "netstandard2.0" [ asmBom ]
        Assert.That (bom.Annotations, Is.Empty)
        Assert.That (Sbom.cycloneDx bom, Does.Not.Contain "annotations")
        Assert.That (Verify.sbomPackageScopeWith options nupkg "netstandard2.0" bom |> List.exists (fun f -> f.Contains "annotation"), Is.True)
