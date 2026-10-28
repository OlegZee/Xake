namespace Tests

open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `csc { project }` / `fsc { project }`: a composed compilation from a project description
/// (`ProjectPlan`) -- against `dotnet build` of the same project (type and method set, manifest
/// resources), an ASP.NET Core app through `frameworkReferences`, the rejections and the
/// precedence of the block's own settings.
[<TestFixture>]
type ``Composed compilation from a description``() =
    inherit XakeTestBase("projectplan")

    let write (path: string) (text: string) =
        Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath path)) |> ignore
        File.WriteAllText (path, text)

    let runDotnet (workDir: string) (args: string list) =
        let psi = ProcessStartInfo ("dotnet")
        // no msbuild node left holding the redirected output (ReadToEnd would wait for it)
        psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.WorkingDirectory <- workDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd ()
        let err = p.StandardError.ReadToEnd ()
        p.WaitForExit ()
        if p.ExitCode <> 0 then
            failwithf "'dotnet %s' failed (exit %d):\n%s\n%s" (System.String.Join (" ", args)) p.ExitCode out err

    /// The types (with their method names) and the manifest resources of an assembly, the
    /// compiler's own helper types (`<...>`, the embedded attributes) left out.
    let shape (dll: string) =
        use stream = File.OpenRead dll
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        let types =
            [ for h in md.TypeDefinitions do
                let t = md.GetTypeDefinition h
                let ns, name = md.GetString t.Namespace, md.GetString t.Name
                if not (name.StartsWith "<") && ns <> "Microsoft.CodeAnalysis" && ns <> "System.Runtime.CompilerServices" then
                    let methods = [ for m in t.GetMethods () -> md.GetString (md.GetMethodDefinition m).Name ] |> List.sort
                    yield sprintf "%s.%s: %s" ns name (String.concat ", " methods) ]
            |> List.sort
        let resources = [ for h in md.ManifestResources -> md.GetString (md.GetManifestResource h).Name ] |> List.sort
        let references = [ for h in md.AssemblyReferences -> md.GetString (md.GetAssemblyReference h).Name ] |> List.sort
        types, resources, references

    let failureOf (f: unit -> unit) =
        let e = Assert.Catch f
        e.ToString ()

    /// A library for netstandard2.0 and net8.0 as a csproj and as a description of it.
    let library (dir: string) =
        write (dir </> "Lib.csproj") """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
    <RootNamespace>Sample</RootNamespace>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <DefineConstants>$(DefineConstants);FEATURE</DefineConstants>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="data/greeting.txt" />
    <EmbeddedResource Include="data/named.txt" LogicalName="Sample.Named" />
    <InternalsVisibleTo Include="Lib.Tests" />
  </ItemGroup>
</Project>
"""
        write (dir </> "Calc.cs") """namespace Sample
{
    public class Calc
    {
        public int Add(int a, int b) => a + b;
        public string? Maybe(string? s) => s;
#if FEATURE
        public int Feature() => 1;
#endif
#if NET8_0_OR_GREATER
        public System.Half Modern() => (System.Half)1;
#endif
#if NETSTANDARD
        public int Legacy() => 2;
#endif
        internal static int Hidden() => 3;
    }
}
"""
        write (dir </> "Util" </> "Text.cs") """namespace Sample.Util
{
    internal static class Text
    {
        public static string Upper(string s) => s.ToUpperInvariant();
    }

    public sealed class Point
    {
        public Point(int x, int y) { X = x; Y = y; }
        public int X { get; }
        public int Y { get; }
        public override string ToString() => $"{X},{Y}";
    }
}
"""
        write (dir </> "data" </> "greeting.txt") "hello"
        write (dir </> "data" </> "named.txt") "named"
        write (dir </> Description.FileName) """{
  "name": "Lib",
  "project": "Lib.csproj",
  "rootNamespace": "Sample",
  "language": "csharp",
  "kind": "library",
  "frameworks": ["netstandard2.0", "net8.0"],
  "sources": ["**/*.cs", "!obj/**/*", "!bin/**/*"],
  "resources": [{ "file": "data/greeting.txt" }, { "file": "data/named.txt", "name": "Sample.Named" }],
  "defines": ["FEATURE"],
  "options": { "langVersion": "latest", "nullable": "enable" },
  "internalsVisibleTo": ["Lib.Tests"]
}
"""

    [<Test; Category("Integration")>]
    member x.``csc project builds a library with the type set dotnet build gives, for netstandard2.0 and net8.0``() =
        let root = x.TestOptions.ProjectRoot
        let dir = root </> "lib"
        library dir
        // dotnet build first: its obj/ and bin/ (generated AssemblyInfo sources) must stay out
        runDotnet dir [ "build"; "Lib.csproj"; "-c"; "Release" ]
        do xake {x.TestOptions with ThrowOnError = true} {
            want ["out/netstandard2.0/Lib.dll"; "out/net8.0/Lib.dll"]
            rules [
                "out/(fwk:*)/Lib.dll" ..> recipe {
                    let! fwk = getRuleMatch "fwk"
                    do! csc { targetfwk fwk; project "lib/xake.project.json" }
                }
            ]
        }
        for fwk in [ "netstandard2.0"; "net8.0" ] do
            let types, resources, _ = shape (root </> "out" </> fwk </> "Lib.dll")
            let expectedTypes, expectedResources, _ = shape (dir </> "bin" </> "Release" </> fwk </> "Lib.dll")
            Assert.That(types, Is.EqualTo expectedTypes, fwk)
            Assert.That(resources, Is.EqualTo expectedResources, fwk)
            Assert.That(types |> List.exists (fun t -> t.Contains "Feature"), Is.True, "the description's define")
        Assert.That(File.Exists (root </> "obj" </> "xake" </> "Lib" </> "net8.0" </> "Lib.InternalsVisibleTo.cs"))

    [<Test; Category("Integration")>]
    member x.``csc project compiles an ASP.NET Core minimal API through frameworkReferences``() =
        let root = x.TestOptions.ProjectRoot
        let dir = root </> "web"
        write (dir </> "Program.cs") """using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Results.Text("hello"));
app.Run();
"""
        write (dir </> Description.FileName) """{
  "name": "Web",
  "project": "Web.csproj",
  "language": "csharp",
  "kind": "exe",
  "frameworks": ["net8.0"],
  "sources": ["**/*.cs", "!obj/**/*", "!bin/**/*"],
  "frameworkReferences": ["Microsoft.AspNetCore.App"],
  "options": { "nullable": "enable" }
}
"""
        do xake {x.TestOptions with ThrowOnError = true} {
            want ["out/web/Web.dll"]
            rules [ "out/web/Web.dll" ..> csc { project "web/xake.project.json" } ]
        }
        let _, _, references = shape (root </> "out" </> "web" </> "Web.dll")
        Assert.That(references, Does.Contain "Microsoft.AspNetCore")
        Assert.That(references, Does.Contain "Microsoft.AspNetCore.Http.Results")

    [<Test; Category("Integration")>]
    member x.``fsc project compiles the sources in the listed order``() =
        let root = x.TestOptions.ProjectRoot
        let dir = root </> "fslib"
        write (dir </> "A.fs") "module Sample.A\nlet value = 41\n"
        write (dir </> "B.fs") "module Sample.B\nlet next = Sample.A.value + 1\n#if FEATURE\nlet feature = true\n#endif\n"
        write (dir </> Description.FileName) """{
  "name": "FsLib",
  "project": "FsLib.fsproj",
  "language": "fsharp",
  "kind": "library",
  "frameworks": ["netstandard2.0"],
  "sources": ["A.fs", "B.fs"],
  "defines": ["FEATURE"],
  "packages": [{ "id": "FSharp.Core", "version": "8.0.100" }],
  "internalsVisibleTo": ["FsLib.Tests"]
}
"""
        do xake {x.TestOptions with ThrowOnError = true} {
            want ["out/fs/FsLib.dll"]
            rules [ "out/fs/FsLib.dll" ..> fsc { project "fslib/xake.project.json" } ]
        }
        let types, _, _ = shape (root </> "out" </> "fs" </> "FsLib.dll")
        Assert.That(types |> List.exists (fun t -> t.StartsWith "Sample.B:" && t.Contains "get_feature"), Is.True, sprintf "%A" types)

    [<Test>]
    member x.``packages are rejected, naming the hermetic package``() =
        let root = x.TestOptions.ProjectRoot
        write (root </> "pkg" </> "A.cs") "class A {}"
        write (root </> "pkg" </> Description.FileName) """{
  "name": "Pkg",
  "project": "Pkg.csproj",
  "language": "csharp",
  "frameworks": ["netstandard2.0"],
  "sources": ["A.cs"],
  "byFramework": { "netstandard2.0": { "packages": [{ "id": "Newtonsoft.Json", "version": "13.0.3" }] } }
}
"""
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/pkg/Pkg.dll"]
                rules [ "out/pkg/Pkg.dll" ..> csc { project "pkg/xake.project.json" } ]
            })
        Assert.That(message, Does.Contain "'Pkg': the description references packages (Newtonsoft.Json 13.0.3) for netstandard2.0")
        Assert.That(message, Does.Contain "Xake.Hermetic.Dotnet")

    [<Test>]
    member x.``a projectRef needs its output among the block's references``() =
        let root = x.TestOptions.ProjectRoot
        write (root </> "app" </> "A.cs") "class A {}"
        write (root </> "app" </> Description.FileName) """{
  "name": "App",
  "project": "App.csproj",
  "language": "csharp",
  "frameworks": ["netstandard2.0"],
  "sources": ["A.cs"],
  "projectRefs": ["Core"]
}
"""
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/app/App.dll"]
                rules [ "out/app/App.dll" ..> csc { project "app/xake.project.json" } ]
            })
        Assert.That(message, Does.Contain "'App': projectRefs 'Core' have no output path; pass each one's output with `ref`")
        // with the output given it composes, the reference included
        let resolved = ref None
        do xake {x.TestOptions with ThrowOnError = true} {
            want ["out/app/App.dll"]
            rules [
                "out/app/App.dll" ..> recipe {
                    let! c = csc { ref (!! "out/core/Core.dll"); project "app/xake.project.json"; resolve }
                    resolved.Value <- Some c
                }
            ]
        }
        Assert.That(resolved.Value.Value.Args |> List.exists (fun a -> a.EndsWith "out/core/Core.dll"), Is.True)

    [<Test>]
    member x.``a culture-specific resource and a language mismatch are rejected``() =
        let root = x.TestOptions.ProjectRoot
        write (root </> "sat" </> "A.cs") "class A {}"
        write (root </> "sat" </> Description.FileName) """{
  "name": "Sat",
  "project": "Sat.csproj",
  "language": "csharp",
  "frameworks": ["netstandard2.0"],
  "sources": ["A.cs"],
  "resources": [{ "file": "Strings.de.resx" }]
}
"""
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/sat/Sat.dll"]
                rules [ "out/sat/Sat.dll" ..> csc { project "sat/xake.project.json" } ]
            })
        Assert.That(message, Does.Contain "resource 'Strings.de.resx' is culture-specific (de)")
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/sat/Sat2.dll"]
                rules [ "out/sat/Sat2.dll" ..> fsc { project "sat/xake.project.json" } ]
            })
        Assert.That(message, Does.Contain "'Sat': the description 'sat/xake.project.json' is C#, not for fsc")

    [<Test>]
    member x.``two frameworks need targetfwk, and it must be one of them``() =
        let root = x.TestOptions.ProjectRoot
        write (root </> "two" </> "A.cs") "class A {}"
        write (root </> "two" </> Description.FileName) """{
  "name": "Two",
  "project": "Two.csproj",
  "language": "csharp",
  "frameworks": ["netstandard2.0", "net8.0"],
  "sources": ["A.cs"]
}
"""
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/two/Two.dll"]
                rules [ "out/two/Two.dll" ..> recipe { let! _ = csc { project "two/xake.project.json"; resolve } in () } ]
            })
        Assert.That(message, Does.Contain "'Two': the description targets netstandard2.0, net8.0; choose one with targetfwk")
        let message = failureOf (fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                want ["out/two/Two.dll"]
                rules [ "out/two/Two.dll" ..> recipe { let! _ = csc { targetfwk "net462"; project "two/xake.project.json"; resolve } in () } ]
            })
        Assert.That(message, Does.Contain "framework 'net462' is not one of the description's (netstandard2.0, net8.0)")

    [<Test>]
    member x.``the block's own settings win over the description``() =
        let root = x.TestOptions.ProjectRoot
        write (root </> "ovr" </> "A.cs") "class A {}"
        write (root </> "ovr" </> "Other.cs") "class Other {}"
        write (root </> "ovr" </> "Vendor" </> "Vendor.dll") ""
        write (root </> "ovr" </> Description.FileName) """{
  "name": "Ovr",
  "project": "Ovr.csproj",
  "language": "csharp",
  "kind": "exe",
  "frameworks": ["netstandard2.0"],
  "sources": ["A.cs"],
  "defines": ["FROM_DESCRIPTION"],
  "references": ["System.Net.Http", "Vendor/Vendor.dll"],
  "options": { "langVersion": "9.0", "noWarn": ["CS0168"], "doc": true, "args": ["/warn:1"] }
}
"""
        let described = ref None
        let overridden = ref None
        do xake {x.TestOptions with ThrowOnError = true} {
            want ["out/ovr/Ovr.dll"; "out/ovr/Ovr2.dll"]
            rules [
                "out/ovr/Ovr.dll" ..> recipe {
                    let! c = csc { project "ovr/xake.project.json"; resolve }
                    described.Value <- Some c
                }
                "out/ovr/Ovr2.dll" ..> recipe {
                    let! c = csc {
                        project "ovr/xake.project.json"
                        target Library
                        src (!! "ovr/Other.cs")
                        define ["FROM_BLOCK"]
                        args ["/langversion:latest"; "/warn:4"]
                        resolve }
                    overridden.Value <- Some c
                }
            ]
        }
        let d = described.Value.Value.Args
        Assert.That(d, Does.Contain "/target:exe")
        Assert.That(d |> List.exists (fun a -> a.StartsWith "/define:" && a.Contains "FROM_DESCRIPTION" && a.Contains "NETSTANDARD2_0"), Is.True, sprintf "%A" d)
        Assert.That(d, Does.Contain "/deterministic+")
        Assert.That(d, Does.Contain "/langversion:9.0")
        Assert.That(d, Does.Contain "/nowarn:1701,1702,CS0168")
        Assert.That(d |> List.exists (fun a -> a.StartsWith "/doc:" && a.EndsWith "out/ovr/Ovr.xml"), Is.True)
        Assert.That(d |> List.exists (fun a -> a.StartsWith "/reference:" && a.EndsWith "System.Net.Http.dll"), Is.True)
        Assert.That(d |> List.exists (fun a -> a.StartsWith "/reference:" && a.EndsWith "ovr/Vendor/Vendor.dll"), Is.True)
        Assert.That(described.Value.Value.Sources |> List.exists (fun s -> s.EndsWith "ovr/A.cs"), Is.True)
        Assert.That(described.Value.Value.Directory.TrimEnd('/'), Does.EndWith "ovr")

        let o = overridden.Value.Value
        Assert.That(o.Args, Does.Contain "/target:library")
        Assert.That(o.Args |> List.exists (fun a -> a.Contains "FROM_DESCRIPTION"), Is.False)
        Assert.That(o.Args |> List.exists (fun a -> a.StartsWith "/define:" && a.Contains "FROM_BLOCK" && a.Contains "NETSTANDARD"), Is.True)
        Assert.That(o.Sources |> List.exists (fun s -> s.EndsWith "ovr/Other.cs"), Is.True)
        Assert.That(o.Sources |> List.exists (fun s -> s.EndsWith "ovr/A.cs"), Is.False)
        // the block's switches come after the description's: the compiler takes the last one
        let index (a: string) = o.Args |> List.findIndex ((=) a)
        Assert.That(index "/langversion:latest", Is.GreaterThan (index "/langversion:9.0"))
        Assert.That(index "/warn:4", Is.GreaterThan (index "/warn:1"))

    [<Test>]
    member x.``manifest names and framework defines follow the SDK``() =
        Assert.That(ProjectPlan.manifestName "Sample" "Properties/Resources.resx", Is.EqualTo "Sample.Properties.Resources.resources")
        Assert.That(ProjectPlan.manifestName "Sample" "1st Folder/x.txt", Is.EqualTo "Sample._1st_Folder.x.txt")
        Assert.That(ProjectPlan.frameworkDefines "netstandard2.0",
                    Is.EqualTo [ "NETSTANDARD"; "NETSTANDARD2_0"; "NETSTANDARD1_0_OR_GREATER"; "NETSTANDARD1_1_OR_GREATER"
                                 "NETSTANDARD1_2_OR_GREATER"; "NETSTANDARD1_3_OR_GREATER"; "NETSTANDARD1_4_OR_GREATER"
                                 "NETSTANDARD1_5_OR_GREATER"; "NETSTANDARD1_6_OR_GREATER"; "NETSTANDARD2_0_OR_GREATER" ])
        Assert.That(ProjectPlan.frameworkDefines "net8.0-windows" |> List.take 3, Is.EqualTo [ "NET"; "NET8_0"; "NETCOREAPP" ])
        Assert.That(ProjectPlan.cultureOf "Strings.de.resx", Is.EqualTo (Some "de"))
        Assert.That(ProjectPlan.cultureOf "Strings.resx", Is.EqualTo None)
        Assert.That(ProjectPlan.cultureOf "Foo.Designer.resx", Is.EqualTo None)
