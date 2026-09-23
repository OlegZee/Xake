namespace Tests

open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Dotnet

/// `Nuget` reads two kinds of file `dotnet restore` already leaves behind: `project.assets.json`
/// (the restore graph) and the package cache (`.nupkg.metadata` plus the nuspec, per package).
/// Both halves are exercised here with a hand-written fixture -- no restore involved -- and one
/// integration test reads the sandbox's own restore of a tiny real project.
[<TestFixture>]
type ``Nuget assets``() =
    inherit XakeTestBase("nuget")

    /// Two packages (`Foo.Bar` depending on `Baz.Qux`), one project reference (`MyProj`), and
    /// both a `netstandard2.0` and a `net8.0` target -- the second target also carries a
    /// `net8.0/win-x64` (rid) entry with a different dependency set, to prove the rid-less entry
    /// is the one picked.
    let assetsJson = """
{
  "targets": {
    "netstandard2.0": {
      "Foo.Bar/1.2.3": {
        "type": "package",
        "dependencies": { "Baz.Qux": "[4.5.6, )" }
      },
      "Baz.Qux/4.5.6": { "type": "package" },
      "MyProj/1.0.0": {
        "type": "project",
        "dependencies": { "Baz.Qux": "4.5.6" }
      }
    },
    "net8.0": {
      "Foo.Bar/1.2.3": {
        "type": "package",
        "dependencies": { "Baz.Qux": "[4.5.6, )" }
      },
      "Baz.Qux/4.5.6": { "type": "package" }
    },
    "net8.0/win-x64": {
      "Foo.Bar/1.2.3": { "type": "package" }
    }
  },
  "project": {
    "frameworks": {
      "netstandard2.0": { "dependencies": { "Foo.Bar": { "target": "Package", "version": "[1.2.3, )" } } },
      "net8.0": { "dependencies": { "Foo.Bar": { "target": "Package", "version": "[1.2.3, )" } } }
    }
  }
}
"""

    let writeCachePackage (root: string) (id: string) (version: string) (metadata: string option) (nuspec: string) =
        let dir = root </> id </> version
        Directory.CreateDirectory dir |> ignore
        metadata |> Option.iter (fun m -> File.WriteAllText (dir </> ".nupkg.metadata", m))
        File.WriteAllText (dir </> (id + ".nuspec"), nuspec)

    let fooBarNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Foo.Bar</id>
    <version>1.2.3</version>
    <authors>Acme Corp</authors>
    <license type="expression">MIT</license>
    <repository type="git" url="https://example.com/foo.bar" commit="deadbeef" />
  </metadata>
</package>
"""

    let bazQuxNuspec = """<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>Baz.Qux</id>
    <version>4.5.6</version>
    <authors>Someone Else</authors>
    <licenseUrl>https://example.com/license.txt</licenseUrl>
  </metadata>
</package>
"""

    [<Test>]
    member x.``reads packages, graph and direct dependencies for the netstandard2_0 target``() =
        let assetsFile = Directory.GetCurrentDirectory() </> "project.assets.json"
        File.WriteAllText (assetsFile, assetsJson)

        let assets = Nuget.readAssets assetsFile "netstandard2.0"

        Assert.That (assets.Framework, Is.EqualTo "netstandard2.0")
        Assert.That (assets.Packages |> List.sort, Is.EqualTo (["Baz.Qux", "4.5.6"; "Foo.Bar", "1.2.3"] |> List.sort))
        Assert.That (assets.Direct, Is.EqualTo ["Foo.Bar"])
        Assert.That (assets.Graph, Contains.Item (("Foo.Bar", "1.2.3"), ("Baz.Qux", "4.5.6")))
        // the project reference's own dependency shows up as an edge even though it is excluded from Packages
        Assert.That (assets.Graph, Contains.Item (("MyProj", "1.0.0"), ("Baz.Qux", "4.5.6")))

    /// A single-`TargetFramework` project: NuGet keys `targets` by the full framework name,
    /// not the alias (dataengine's `project.assets.json` has `.NETStandard,Version=v2.0`).
    [<Test>]
    member x.``finds the target keyed by the full framework name of a single-target project``() =
        let assetsFile = Directory.GetCurrentDirectory() </> "project.assets.single.json"
        File.WriteAllText (assetsFile, """
            {
              "targets": {
                ".NETStandard,Version=v2.0": {
                  "Foo.Bar/1.2.3": { "type": "package", "dependencies": { "Baz.Qux": "[4.5.6, )" } },
                  "Baz.Qux/4.5.6": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "netstandard2.0": { "targetAlias": "netstandard2.0", "dependencies": { "Foo.Bar": { "target": "Package", "version": "[1.2.3, )" } } }
                }
              }
            }
            """)
        let assets = Nuget.readAssets assetsFile "netstandard2.0"
        Assert.That (assets.Packages |> List.sort, Is.EqualTo (["Baz.Qux", "4.5.6"; "Foo.Bar", "1.2.3"] |> List.sort))
        Assert.That (assets.Direct, Is.EqualTo ["Foo.Bar"])

        Assert.That (Nuget.frameworkFullName "netstandard2.0", Is.EqualTo ".NETStandard,Version=v2.0")
        Assert.That (Nuget.frameworkFullName "net472", Is.EqualTo ".NETFramework,Version=v4.7.2")
        Assert.That (Nuget.frameworkFullName "netcoreapp3.1", Is.EqualTo ".NETCoreApp,Version=v3.1")
        Assert.That (Nuget.frameworkFullName "net8.0", Is.EqualTo "net8.0")

    [<Test>]
    member x.``picks the rid-less net8_0 entry over net8_0-win-x64``() =
        let assetsFile = Directory.GetCurrentDirectory() </> "project.assets.net8.json"
        File.WriteAllText (assetsFile, assetsJson)

        let assets = Nuget.readAssets assetsFile "net8.0"

        Assert.That (assets.Packages |> List.sort, Is.EqualTo (["Baz.Qux", "4.5.6"; "Foo.Bar", "1.2.3"] |> List.sort))
        Assert.That (assets.Graph, Contains.Item (("Foo.Bar", "1.2.3"), ("Baz.Qux", "4.5.6")))

    [<Test>]
    member x.``reads a cached package's metadata and nuspec``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgs"
        let metadata = """{ "version": 2, "contentHash": "AAAA==", "source": "https://api.nuget.org/v3/index.json" }"""
        writeCachePackage cacheRoot "foo.bar" "1.2.3" (Some metadata) fooBarNuspec

        let package = Nuget.readCache cacheRoot "Foo.Bar" "1.2.3"

        Assert.That (package.Id, Is.EqualTo "Foo.Bar")
        Assert.That (package.Version, Is.EqualTo "1.2.3")
        Assert.That (package.Sha512, Is.EqualTo "AAAA==")
        Assert.That (package.Source, Is.EqualTo "https://api.nuget.org/v3/index.json")
        Assert.That (package.License, Is.EqualTo "MIT")
        Assert.That (package.Supplier, Is.EqualTo "Acme Corp")
        Assert.That (package.Repository, Is.EqualTo "https://example.com/foo.bar")
        Assert.That (package.Commit, Is.EqualTo "deadbeef")
        Assert.That (package.Directory, Is.EqualTo (cacheRoot </> "foo.bar" </> "1.2.3"))

    [<Test>]
    member x.``falls back to the legacy licenseUrl and leaves hash/source empty when metadata is missing``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgs2"
        writeCachePackage cacheRoot "baz.qux" "4.5.6" None bazQuxNuspec

        let package = Nuget.readCache cacheRoot "Baz.Qux" "4.5.6"

        Assert.That (package.Sha512, Is.EqualTo "")
        Assert.That (package.Source, Is.EqualTo "")
        Assert.That (package.License, Is.EqualTo "https://example.com/license.txt")
        Assert.That (package.Supplier, Is.EqualTo "Someone Else")
        Assert.That (package.Repository, Is.EqualTo "")
        Assert.That (package.Commit, Is.EqualTo "")
        Assert.That (package.Directory, Is.Not.EqualTo "")

    [<Test>]
    member x.``a package missing from the cache reads as empty, not an exception``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgs3"
        Directory.CreateDirectory cacheRoot |> ignore

        let package = Nuget.readCache cacheRoot "Nowhere.Package" "9.9.9"

        Assert.That (package.Directory, Is.EqualTo "")
        Assert.That (package.Sha512, Is.EqualTo "")
        Assert.That (package.License, Is.EqualTo "")
        Assert.That (package.Supplier, Is.EqualTo "")

    [<Test>]
    member x.``packageOf finds the id and version for a file under the cache root``() =
        let root = "/cache/root"
        Assert.That (Nuget.packageOf root "/cache/root/foo.bar/1.2.3/lib/netstandard2.0/Foo.Bar.dll", Is.EqualTo (Some ("foo.bar", "1.2.3")))

    [<Test>]
    member x.``ships is true for a package with files under lib or runtimes``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgsShipsLib"
        Directory.CreateDirectory (cacheRoot </> "foo.bar" </> "1.2.3" </> "lib" </> "netstandard2.0") |> ignore
        File.WriteAllText (cacheRoot </> "foo.bar" </> "1.2.3" </> "lib" </> "netstandard2.0" </> "Foo.Bar.dll", "x")
        Assert.That (Nuget.ships cacheRoot "Foo.Bar" "1.2.3", Is.True)

        let cacheRoot2 = Directory.GetCurrentDirectory() </> "pkgsShipsRuntimes"
        Directory.CreateDirectory (cacheRoot2 </> "runtime.only" </> "2.0.0" </> "runtimes" </> "win" </> "lib" </> "net6.0") |> ignore
        File.WriteAllText (cacheRoot2 </> "runtime.only" </> "2.0.0" </> "runtimes" </> "win" </> "lib" </> "net6.0" </> "x.dll", "x")
        Assert.That (Nuget.ships cacheRoot2 "Runtime.Only" "2.0.0", Is.True)

    [<Test>]
    member x.``ships is false when the package is missing, empty, or has no lib or runtimes files``() =
        let cacheRoot = Directory.GetCurrentDirectory() </> "pkgsNoShip"
        Assert.That (Nuget.ships cacheRoot "Nowhere.Package" "9.9.9", Is.False, "missing from the cache entirely")

        Directory.CreateDirectory (cacheRoot </> "empty.package" </> "1.0.0") |> ignore
        Assert.That (Nuget.ships cacheRoot "Empty.Package" "1.0.0", Is.False, "cache entry with nothing in it")

        Directory.CreateDirectory (cacheRoot </> "ref.only" </> "1.0.0" </> "ref" </> "netstandard2.0") |> ignore
        File.WriteAllText (cacheRoot </> "ref.only" </> "1.0.0" </> "ref" </> "netstandard2.0" </> "Ref.dll", "x")
        Assert.That (Nuget.ships cacheRoot "Ref.Only" "1.0.0", Is.False, "only a ref/ folder, no lib or runtimes")

        Directory.CreateDirectory (cacheRoot </> "empty.lib" </> "1.0.0" </> "lib" </> "netstandard2.0") |> ignore
        Assert.That (Nuget.ships cacheRoot "Empty.Lib" "1.0.0", Is.False, "lib/ folder present but no files in it")

    [<Test>]
    member x.``packageOf returns None for a path outside the cache root``() =
        let root = "/cache/root"
        Assert.That (Nuget.packageOf root "/somewhere/else/foo.bar/1.2.3/Foo.Bar.dll", Is.EqualTo None)
        Assert.That (Nuget.packageOf root "/cache/root", Is.EqualTo None)

    /// Restores a tiny generated csproj referencing a real, small NuGet package that is very
    /// likely already in the machine's cache (network otherwise), then reads the sandbox's own
    /// `project.assets.json` and cache entry with the same two functions used above.
    [<Test; Category("Integration")>]
    member x.``reads a real restore of Newtonsoft.Json from this machine's cache``() =
        let projectDir = Directory.GetCurrentDirectory() </> "nugetIntegration"
        Directory.CreateDirectory projectDir |> ignore

        let csproj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
"""
        File.WriteAllText (projectDir </> "NugetIntegration.csproj", csproj)

        let psi = ProcessStartInfo ("dotnet", "restore")
        psi.WorkingDirectory <- projectDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use proc = Process.Start psi
        let stdout = proc.StandardOutput.ReadToEnd ()
        let stderr = proc.StandardError.ReadToEnd ()
        proc.WaitForExit ()
        Assert.That (proc.ExitCode, Is.EqualTo 0, sprintf "dotnet restore failed:\n%s\n%s" stdout stderr)

        let assetsFile = projectDir </> "obj" </> "project.assets.json"
        let assets = Nuget.readAssets assetsFile "net8.0"

        Assert.That (assets.Packages, Contains.Item ("Newtonsoft.Json", "13.0.3"))

        let cacheRoot =
            match System.Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
            | null | "" -> System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile </> ".nuget" </> "packages"
            | dir -> dir

        let package = Nuget.readCache cacheRoot "Newtonsoft.Json" "13.0.3"

        Assert.That (package.Sha512, Is.Not.EqualTo "")
        Assert.That (package.License, Is.EqualTo "MIT")
        Assert.That (package.Supplier, Is.EqualTo "James Newton-King")
