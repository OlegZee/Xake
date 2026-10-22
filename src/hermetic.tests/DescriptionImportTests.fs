namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Import`: a project file evaluated by msbuild into its description -- Xake's own three
/// projects, write-on-change, a brand chosen by a global property, the msbuild files read.
[<TestFixture>]
type ``Description import``() =
    inherit XakeTestBase("description-import")

    let src = Path.GetFullPath (__SOURCE_DIRECTORY__ </> "..")

    /// Runs `Import.descriptions` in a small build and hands back what it returned.
    member x.Import (projects: string list) (descriptionOf: string -> string) (globals: (string * string) list) =
        let result = ref []
        do xake {x.TestOptions with FileLog = "import.log"; ThrowOnError = true} {
            wantOverride [ "import" ]
            rules [
                "import" => recipe {
                    do! alwaysRerun ()   // as a `command` would
                    let! imported = Import.descriptions projects descriptionOf globals
                    result.Value <- imported
                }
            ]
        }
        result.Value

    [<Test>]
    member x.``relative paths walk up with ..``() =
        Assert.That(Import.relativeTo "/a/b/c" "/a/b/c/d.fs", Is.EqualTo "d.fs")
        Assert.That(Import.relativeTo "/a/b/c" "/a/x/y.fs", Is.EqualTo "../../x/y.fs")
        Assert.That(Import.relativeTo "/a/b/" "/a/b/src/p.fsproj", Is.EqualTo "src/p.fsproj")

    member x.OwnProjects () =
        [ src </> "core" </> "Xake.fsproj"; src </> "dotnet" </> "Xake.Dotnet.fsproj"; src </> "hermetic" </> "Xake.Hermetic.Dotnet.fsproj" ]

    /// Descriptions in a folder of the test's own, one per project name: never beside the
    /// real projects.
    member x.Own (project: string) = Directory.GetCurrentDirectory () </> "own" </> Path.GetFileNameWithoutExtension project </> Description.FileName

    [<Test; Category("Integration")>]
    member x.``Xake's own projects import into descriptions that round-trip``() =
        let imported = x.Import (x.OwnProjects ()) x.Own [ "Configuration", "Release" ]
        let ds = imported |> List.map (fun i -> Description.read i.Description)
        for i, d in List.zip imported ds do
            Assert.That(Description.format d, Is.EqualTo (File.ReadAllText i.Description), sprintf "%s does not round-trip" i.Description)
            Assert.That(d.Language, Is.EqualTo Description.FSharp)
            Assert.That(d.Frameworks, Is.EqualTo [ "net462"; "netstandard2.0" ])
            Assert.That(d.Packages |> List.filter (fun p -> p.Id = "FSharp.Core"), Is.EqualTo [ ({ Id = "FSharp.Core"; Version = Some "8.0.100"; Private = false } : Description.Package) ])
            Assert.That(d.Options.Doc, Is.True)
            // the project file, relative to the description
            Assert.That(Path.GetFullPath (Path.Combine (Path.GetDirectoryName i.Description, d.Project)), Is.EqualTo (Path.GetFullPath i.Project))
            // the SDK's implicit packages are not the project's, and are said to be dropped
            Assert.That(d.Packages |> List.exists (fun p -> p.Id = "NETStandard.Library" || p.Id = "System.ValueTuple"), Is.False)
            Assert.That(i.Dropped |> List.exists (fun s -> s.Contains "implicit package NETStandard.Library"), Is.True)
        match ds with
        | [ core; dotnet; hermetic ] ->
            Assert.That(core.Name, Is.EqualTo "Xake")
            // every Compile item of the project file, in order
            let compileItems =
                System.Text.RegularExpressions.Regex.Matches(File.ReadAllText (src </> "core" </> "Xake.fsproj"), """<Compile Include="([^"]+)" />""")
                |> Seq.map (fun m -> m.Groups.[1].Value.Replace('\\', '/')) |> List.ofSeq
            Assert.That(core.Sources, Is.EqualTo compileItems)
            Assert.That(core.Sources |> List.take 3, Is.EqualTo [ "Prelude.fs"; "Logging.fs"; "Pickler.fs" ])
            Assert.That(core.Sources, Does.Contain "Tasks/Cp.fs")
            Assert.That(core.InternalsVisibleTo |> List.map (fun f -> f.Name), Is.EqualTo [ "Xake.Dotnet"; "tests" ])
            Assert.That(core.Package, Is.EqualTo None, "IsPackable is false")
            Assert.That(dotnet.Name, Is.EqualTo "Xake.Dotnet")
            Assert.That(dotnet.Sources |> List.head, Is.EqualTo "Tool.fs")
            Assert.That(dotnet.ProjectRefs, Is.EqualTo [ "Xake" ])
            Assert.That(dotnet.ByFramework |> List.map fst, Is.EqualTo [ "net462"; "netstandard2.0" ])
            Assert.That((dotnet.ByFramework |> List.find (fst >> (=) "net462") |> snd).References, Is.EqualTo [ "System.Windows.Forms" ])
            Assert.That((dotnet.ByFramework |> List.find (fst >> (=) "netstandard2.0") |> snd).Packages |> List.map (fun p -> p.Id, p.Version),
                        Is.EqualTo [ "Microsoft.Win32.Registry", Some "5.0.0" ])
            let pkg = dotnet.Package.Value
            Assert.That(pkg.Id, Is.EqualTo "Xake")
            Assert.That(pkg.Bundle, Is.EqualTo [ "Xake" ], "the PrivateAssets=all reference is packed into the package")
            Assert.That((pkg.Icon, pkg.Readme), Is.EqualTo ((Some "../../Icon.png", Some "../../readme.md")))
            Assert.That(pkg.License, Is.EqualTo (Some "MIT"))
            Assert.That(hermetic.Packages |> List.map (fun p -> p.Id, p.Version),
                        Is.EqualTo [ "Xake", Some "[3.11.0.40, 3.12)"; "FSharp.Core", Some "8.0.100" ])
            Assert.That(hermetic.Sources, Does.Contain "Import.fs")
            Assert.That(hermetic.Package.Value.Readme, Is.EqualTo (Some "readme.md"))
        | _ -> Assert.Fail "three descriptions expected"
        // the files msbuild read: the project itself, nothing of the SDK
        for i in imported do
            Assert.That(i.Inputs, Does.Contain i.Project)
            Assert.That(i.Inputs |> List.forall (fun f -> not (Path.IsPathRooted f)), Is.True, sprintf "%A" i.Inputs)

    [<Test; Category("Integration")>]
    member x.``re-importing an unchanged project writes nothing``() =
        let projects = x.OwnProjects () |> List.take 1
        x.Import projects x.Own [] |> ignore
        let file = x.Own projects.Head
        let stamp = System.DateTime (2001, 1, 1)
        File.SetLastWriteTimeUtc (file, stamp)
        let again = x.Import projects x.Own []
        Assert.That(again.Head.Written, Is.False)
        Assert.That(File.GetLastWriteTimeUtc file, Is.EqualTo stamp)

    [<Test; Category("Integration")>]
    member x.``the shared frameworks are written as frameworkReferences, not the target's own``() =
        let dir = Directory.GetCurrentDirectory () </> "shared-fwk"
        if Directory.Exists dir then Directory.Delete (dir, true)
        for d in [ dir </> "Web"; dir </> "Lib" ] do Directory.CreateDirectory d |> ignore
        // the Web SDK's implicit Microsoft.AspNetCore.App
        File.WriteAllText (dir </> "Web" </> "Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>""")
        File.WriteAllText (dir </> "Web" </> "P.cs", "class P { static void Main() {} }\n")
        // an explicit one, and a plain library (Microsoft.NETCore.App only: nothing written)
        File.WriteAllText (dir </> "Lib" </> "Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks></PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
</Project>""")
        File.WriteAllText (dir </> "Lib" </> "L.cs", "class L {}\n")
        let imported = x.Import [ dir </> "Web" </> "Web.csproj"; dir </> "Lib" </> "Lib.csproj" ] Description.beside []
        let web, lib = Description.read imported.[0].Description, Description.read imported.[1].Description
        Assert.That(web.FrameworkReferences, Is.EqualTo [ "Microsoft.AspNetCore.App" ])
        Assert.That(lib.FrameworkReferences, Is.Empty)
        Assert.That((lib.ByFramework |> List.find (fst >> (=) "net8.0") |> snd).FrameworkReferences, Is.EqualTo [ "Microsoft.AspNetCore.App" ])
        Assert.That(lib.ByFramework |> List.exists (fst >> (=) "netstandard2.0"), Is.False)

    [<Test; Category("Integration")>]
    member x.``a global property picks the brand, and the description lives in the brand's folder``() =
        let dir = Directory.GetCurrentDirectory () </> "brand"
        if Directory.Exists dir then Directory.Delete (dir, true)
        for d in [ dir </> "Widgets" </> "Legacy"; dir </> "Shared" ] do Directory.CreateDirectory d |> ignore
        File.WriteAllText (dir </> "Directory.Build.props", """<Project>
  <PropertyGroup>
    <Brand Condition="'$(Brand)' == ''">ACME</Brand>
    <AssemblyName>$(Brand).Widgets</AssemblyName>
  </PropertyGroup>
</Project>""")
        File.WriteAllText (dir </> "Widgets" </> "Widgets.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <DefineConstants>$(DefineConstants);WIDGETS</DefineConstants>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="Legacy/**" />
    <Compile Include="../Shared/Common.cs" Link="Common.cs" />
  </ItemGroup>
</Project>""")
        File.WriteAllText (dir </> "Widgets" </> "A.cs", "class A {}\n")
        File.WriteAllText (dir </> "Widgets" </> "Legacy" </> "L.cs", "class L {}\n")
        File.WriteAllText (dir </> "Shared" </> "Common.cs", "class C {}\n")
        let project = dir </> "Widgets" </> "Widgets.csproj"
        let descriptionOf brand (p: string) = dir </> brand </> Path.GetFileNameWithoutExtension p + ".json"

        let globex = x.Import [ project ] (descriptionOf "globex") [ "Brand", "Globex" ] |> List.head
        let acme = x.Import [ project ] (descriptionOf "acme") [] |> List.head
        let d = Description.read globex.Description
        Assert.That(d.AssemblyName, Is.EqualTo (Some "Globex.Widgets"))
        Assert.That((Description.read acme.Description).AssemblyName, Is.EqualTo (Some "ACME.Widgets"))
        Assert.That(d.Name, Is.EqualTo "Widgets")
        Assert.That(d.Project, Is.EqualTo "../Widgets/Widgets.csproj")
        Assert.That(d.Language, Is.EqualTo Description.CSharp)
        Assert.That(d.Defines, Is.EqualTo [ "WIDGETS" ])
        Assert.That(d.Options.Nullable, Is.EqualTo (Some "enable"))
        Assert.That(d.Options.LangVersion, Is.EqualTo None, "the SDK's default for the framework is not the project's")
        // msbuild's default glob and its excludes, then the project's own items, as masks
        Assert.That(d.Sources, Is.EqualTo [ "**/*.cs"; "!bin/**/*"; "!obj/**/*"; "!**/.*/**/*"; "!Legacy/**/*"; "../Shared/Common.cs" ])
        Assert.That(globex.Dropped |> List.filter (fun s -> s.Contains "masks"), Is.Empty, "the masks expand to msbuild's Compile list")
        // what msbuild read: the project and its Directory.Build.props, nothing of the SDK
        Assert.That(globex.Inputs, Is.EqualTo [ "brand/Directory.Build.props"; "brand/Widgets/Widgets.csproj" ])
