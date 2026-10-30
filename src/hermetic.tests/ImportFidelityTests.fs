namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// What a description carries beyond sources and references, and what `project` makes of it
/// (fixes-plan round A1): `.resx` resources compiled by `project`, manifest names as msbuild's,
/// `LogicalName`, `noWarn` with the SDK's defaults, `warningsAsErrors`, assembly attributes in
/// the generated source, item metadata read case-insensitively.
[<TestFixture>]
type ``Import fidelity``() =
    inherit XakeTestBase("import-fidelity")

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
            failwithf "'dotnet %s' failed (exit %d):\n%s\n%s" (String.Join (" ", args)) p.ExitCode out err

    let manifestResources (path: string) =
        use stream = File.OpenRead path
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        [ for h in md.ManifestResources -> md.GetString (md.GetManifestResource h).Name ] |> List.sort

    let assemblyAttributes (path: string) =
        use stream = File.OpenRead path
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        [ for h in md.GetAssemblyDefinition().GetCustomAttributes () do
            let a = md.GetCustomAttribute h
            if a.Constructor.Kind = System.Reflection.Metadata.HandleKind.MemberReference then
                let m = md.GetMemberReference (System.Reflection.Metadata.MemberReferenceHandle.op_Explicit a.Constructor)
                if m.Parent.Kind = System.Reflection.Metadata.HandleKind.TypeReference then
                    let t = md.GetTypeReference (System.Reflection.Metadata.TypeReferenceHandle.op_Explicit m.Parent)
                    yield md.GetString t.Namespace + "." + md.GetString t.Name ]

    let resx (entries: (string * string) list) =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
        + "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n"
        + "  <resheader name=\"version\"><value>2.0</value></resheader>\n"
        + (entries |> List.map (fun (k, v) -> sprintf "  <data name=\"%s\" xml:space=\"preserve\"><value>%s</value></data>\n" k v) |> String.concat "")
        + "</root>\n"

    let lockOf name cfg = sprintf "locks/%s/%s.json" name cfg
    let outOf cfg fwk (_: string) = sprintf "out/%s/%s" cfg fwk
    let version = Var.string (name = "FID_VERSION") |> withDefault "1.0.0"

    /// fid/Fid.csproj: two resx (one under a folder msbuild mangles), a LogicalName resource,
    /// a `Res\*` glob, a linked file (`Link`, through a `_` folder), `ManifestResourceName` on a file
    /// and on a resx, CLSCompliant and AssemblyMetadata attributes, NoWarn on top of the SDK's
    /// with TreatWarningsAsErrors and doc on (so CS0618/CS1591 would fail the build).
    let fixture () =
        for d in [ "fid"; "shared"; "out"; "locks"; "obj" ] do if Directory.Exists d then Directory.Delete (d, true)
        for d in [ "fid/1st Folder"; "fid/Res"; "fid/Data"; "shared" ] do Directory.CreateDirectory d |> ignore
        let csproj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <RootNamespace>Acme.Fid</RootNamespace>
    <AssemblyName>Acme.Fidelity</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);1591;0618</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="Data\blob.txt" LogicalName="blob.bin" />
    <EmbeddedResource Include="Res\*" />
    <EmbeddedResource Include="..\shared\Linked.txt" Link="Shared\_\Linked.txt" />
    <EmbeddedResource Include="Data\named.txt" ManifestResourceName="Custom.Named" />
    <EmbeddedResource Update="Data\Other.resx" ManifestResourceName="Custom.Other" />
    <AssemblyAttribute Include="System.CLSCompliantAttribute">
      <_Parameter1>true</_Parameter1>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>Key</_Parameter1>
      <_Parameter2>Va"lue</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
"""
        let source = """namespace Acme.Fid
{
    public class A
    {
        [System.Obsolete("old")] public static void Old() {}
        public static void New() { Old(); }
        [System.CLSCompliant(false)] public static uint U() => 1;
    }
}
"""
        File.WriteAllText ("fid/Fid.csproj", csproj)
        File.WriteAllText ("fid/A.cs", source)
        File.WriteAllText ("fid/Strings.resx", resx [ "Greeting", "Hello"; "Bye", "Good bye" ])
        File.WriteAllText ("fid/1st Folder/Messages.resx", resx [ "One", "1" ])
        File.WriteAllText ("fid/Res/a.txt", "a\n")
        File.WriteAllText ("fid/Res/b.json", "{}\n")
        File.WriteAllText ("fid/Data/blob.txt", "blob\n")
        File.WriteAllText ("fid/Data/named.txt", "named\n")
        File.WriteAllText ("fid/Data/Other.resx", resx [ "Other", "o" ])
        File.WriteAllText ("shared/Linked.txt", "linked\n")

    member private x.Run (label: string) (rules': Rule<ExecContext> list) (targets: string list) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off"; "CSC_SERVER", "off" ]; FileLogLevel = Loud} {
            varschema {| Version = version |}     // a Var read needs a declaration of its key
            want targets
            rules rules'
        }

    member private x.Import () =
        let result = ref []
        x.Run "import" [ "import" => recipe {
                            do! alwaysRerun ()
                            let! i = Import.descriptions [ "fid/Fid.csproj" ] Description.beside []
                            result.Value <- i } ] [ "import" ]
        result.Value.Head

    member private x.Build (label: string) =
        x.Run label
            [ LockRule.lockRule lockOf outOf (fun _ -> "fid/xake.project.json") id (ResizeArray ())
              LockRule.updateLocks [ "Release" ] [ "Fid" ]
              command "build" { do! Locks.needAll (!! "fid/xake.project.json") "Release" lockOf outOf }
              target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
                  let! cfg = getRuleMatch "cfg"
                  let! fwk = getRuleMatch "fwk"
                  let! name = getRuleMatch "name"
                  do! Locks.compileAs (lockOf "Fid" cfg) fwk [ "**", version ] } ]
            [ "update-locks"; "build" ]

    /// The acceptance of G1-G4 and G13 on one fixture: import -> description (fields present)
    /// -> lock -> build; the assembly's manifest resources are msbuild's, the attributes are in.
    [<Test; Category("Integration")>]
    member x.``a C# project with resx, LogicalName, attributes and NoWarn imports, locks and builds as msbuild``() =
        fixture ()
        let here = Directory.GetCurrentDirectory ()
        runDotnet (here </> "fid") [ "build"; "Fid.csproj"; "-c"; "Release"; "-p:NuGetAudit=false"; "-o"; here </> "msbuild" ]
        let msbuildDll = here </> "msbuild" </> "Acme.Fidelity.dll"

        let imported = x.Import ()
        let d = Description.read "fid/xake.project.json"
        Assert.That(d.Resources |> List.sortBy (fun r -> r.File),
            Is.EqualTo ([ { File = "../shared/Linked.txt"; Name = Some "Acme.Fid.Shared.__.Linked.txt" }
                          { File = "1st Folder/Messages.resx"; Name = None }; { File = "Data/Other.resx"; Name = Some "Custom.Other.resources" }
                          { File = "Data/blob.txt"; Name = Some "blob.bin" }; { File = "Data/named.txt"; Name = Some "named.txt" }
                          { File = "Res/a.txt"; Name = None }; { File = "Res/b.json"; Name = None }; { File = "Strings.resx"; Name = None } ]
                        : Description.Resource list))
        Assert.That(d.Options.NoWarn, Is.EqualTo [ "1591"; "0618" ])
        Assert.That(d.Options.WarningsAsErrors, Is.True)
        Assert.That(d.AssemblyAttributes,
            Is.EqualTo ([ { Type = "System.CLSCompliantAttribute"; Args = [ "true" ] }
                          { Type = "System.Reflection.AssemblyMetadataAttribute"; Args = [ "\"Key\""; "@\"Va\"\"lue\"" ] } ] : Description.Attribute list))
        Assert.That(imported.Dropped |> List.filter (fun s -> s.Contains "assembly attribute" || s.Contains "NoWarn" || s.Contains "EmbeddedResource"), Is.Empty)

        x.Build "build-1"
        let dll = "out/Release/netstandard2.0/Acme.Fidelity.dll"
        Assert.That(manifestResources dll, Is.EqualTo (manifestResources msbuildDll))
        Assert.That(manifestResources dll, Does.Contain "Acme.Fid._1st_Folder.Messages.resources")
        Assert.That(manifestResources dll, Is.SupersetOf [ "Acme.Fid.Shared.__.Linked.txt"; "Custom.Other.resources"; "named.txt" ])
        Assert.That(assemblyAttributes dll, Is.SupersetOf [ "System.CLSCompliantAttribute"; "System.Reflection.AssemblyMetadataAttribute" ])
        let lockText = File.ReadAllText (lockOf "Fid" "Release")
        Assert.That(lockText, Does.Contain "/nowarn:1701,1702,1591,0618")
        Assert.That(lockText, Does.Contain "/warnaserror+")
        Assert.That(lockText, Does.Contain "$(ProjectRoot)/fid/Strings.resx")
        Assert.That(lockText, Does.Contain "/resource:$(ProjectRoot)/obj/xake/Acme.Fidelity/netstandard2.0/Release/Acme.Fid.Strings.resources,Acme.Fid.Strings.resources")
        // the compiled .resources is msbuild's, byte for byte
        let ours = "obj/xake/Acme.Fidelity/netstandard2.0/Release/Acme.Fid.Strings.resources"
        let theirs = Directory.GetFiles ("fid/obj", "Acme.Fid.Strings.resources", SearchOption.AllDirectories) |> Array.head
        Assert.That(File.ReadAllBytes ours, Is.EqualTo (File.ReadAllBytes theirs))

        // G13: importing again writes nothing
        let again = x.Import ()
        Assert.That(again.Written, Is.False)

        // an edited resx is compiled again
        let stamp = File.GetLastWriteTimeUtc dll
        File.WriteAllText ("fid/Strings.resx", resx [ "Greeting", "Hallo" ])
        File.SetLastWriteTimeUtc ("fid/Strings.resx", DateTime.UtcNow.AddSeconds 2.0)
        x.Build "build-2"
        Assert.That(File.GetLastWriteTimeUtc dll, Is.GreaterThan stamp)
        Assert.That(Text.Encoding.UTF8.GetString (File.ReadAllBytes ours), Does.Contain "Hallo")

    /// Item metadata is read whatever its case (`version`, `privateassets`, `logicalname`).
    [<Test>]
    member x.``item metadata is read case-insensitively``() =
        let dir = Path.GetFullPath "meta"
        Directory.CreateDirectory dir |> ignore
        let dump : Import.Dump =
            { Props = Map.ofList [ "MSBuildProjectName", "M"; "MSBuildProjectExtension", ".fsproj"; "MSBuildProjectDirectory", dir
                                   "AssemblyName", "M"; "TargetFramework", "netstandard2.0"; "NoWarn", "FS0044;3370" ]
              Items =
                Map.ofList
                    [ "PackageReference", [ "System.Text.Json", Map.ofList [ "identity", "System.Text.Json"; "version", "8.0.5"; "privateassets", "All" ] ]
                      "EmbeddedResource", [ "x.txt", Map.ofList [ "Identity", "x.txt"; "logicalname", "X" ] ]
                      "Compile", [ "A.fs", Map.ofList [ "Identity", "A.fs" ] ] ] }
        let d, _ = Import.describe dir dump [ "netstandard2.0", dump ] (fun _ -> false) [] None []
        Assert.That(d.Packages, Is.EqualTo ([ { Id = "System.Text.Json"; Version = Some "8.0.5"; Private = true } ] : Description.Package list))
        Assert.That(d.Resources, Is.EqualTo ([ { File = "x.txt"; Name = Some "X" } ] : Description.Resource list))
        Assert.That(d.Options.NoWarn, Is.EqualTo [ "FS0044"; "3370" ], "F#: the SDK suppresses nothing")

    [<Test>]
    member x.``assembly attribute arguments are written as WriteCodeFragment writes them``() =
        let args attribute (m: (string * string) list) =
            let dropped = ResizeArray ()
            Import.attributeArgs false attribute (Map.ofList (("Identity", attribute) :: m)) dropped.Add, List.ofSeq dropped
        Assert.That(args "System.CLSCompliantAttribute" [ "_Parameter1", "true" ], Is.EqualTo ((Some [ "true" ], ([]: string list))))
        Assert.That(args "System.Runtime.InteropServices.ComVisibleAttribute" [ "_Parameter1", "false" ] |> fst, Is.EqualTo (Some [ "false" ]))
        Assert.That(args "System.Runtime.InteropServices.GuidAttribute" [ "_Parameter1", "0b6e8d1c" ] |> fst, Is.EqualTo (Some [ "\"0b6e8d1c\"" ]))
        Assert.That(args "Some.UnknownAttribute" [ "_Parameter1", "true"; "_Parameter2", "a\\b" ] |> fst, Is.EqualTo (Some [ "\"true\""; "@\"a\\b\"" ]))
        Assert.That(args "Some.UnknownAttribute" [ "_Parameter1", "42"; "_Parameter1_IsLiteral", "true" ] |> fst, Is.EqualTo (Some [ "42" ]))
        Assert.That(args "Some.UnknownAttribute" [ "_Parameter1", "42"; "_Parameter1_TypeName", "System.Int32" ] |> fst, Is.EqualTo (Some [ "42" ]))
        Assert.That(args "System.Reflection.AssemblyMetadataAttribute" [ "_Parameter1", "K"; "_Parameter2", "V" ] |> fst, Is.EqualTo (Some [ "\"K\""; "\"V\"" ]))
        Assert.That(args "Some.UnknownAttribute" [ "Named", "n" ] |> fst, Is.EqualTo (Some [ "Named = \"n\"" ]))
        let none, dropped = args "Some.UnknownAttribute" [ "_Parameter2", "x" ]
        Assert.That(none, Is.EqualTo None)
        Assert.That(dropped, Has.Length.EqualTo 1)

    [<Test>]
    member x.``manifest names are msbuild's``() =
        Assert.That(ProjectPlan.manifestName "Acme" "Strings.resx", Is.EqualTo "Acme.Strings.resources")
        Assert.That(ProjectPlan.manifestName "Acme" "Properties/Resources.resx", Is.EqualTo "Acme.Properties.Resources.resources")
        Assert.That(ProjectPlan.manifestName "Acme" "1st Folder/a-b.txt", Is.EqualTo "Acme._1st_Folder.a-b.txt")
        Assert.That(ProjectPlan.manifestName "Acme" "my.dir/x-y/z.xsd", Is.EqualTo "Acme.my.dir.x_y.z.xsd")
        Assert.That(ProjectPlan.manifestName "" "Res/a.txt", Is.EqualTo "Res.a.txt")
        // a folder that comes out as `_` is `__`, wherever it is in the path
        Assert.That(ProjectPlan.manifestName "Root" "_/x.txt", Is.EqualTo "Root.__.x.txt")
        Assert.That(ProjectPlan.manifestName "Root" "a/_/x.txt", Is.EqualTo "Root.a.__.x.txt")
        Assert.That(ProjectPlan.manifestName "Root" "a/-/x.txt", Is.EqualTo "Root.a.__.x.txt")
        Assert.That(ProjectPlan.manifestName "Root" "a/_b/x.txt", Is.EqualTo "Root.a._b.x.txt")

    /// `project` composes `/nowarn:` from the SDK's defaults and the description's, `/warnaserror+`
    /// only when asked, and one generated source with the friends and the attributes.
    [<Test; Category("Integration")>]
    member x.``project composes nowarn, warnaserror and the attributes source``() =
        let dir = "compose"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (dir </> "A.cs", "namespace C { public class A {} }\n")
        let write (d: string) = File.WriteAllText (dir </> Description.FileName, d)
        write "{\n  \"name\": \"C\",\n  \"project\": \"C.csproj\",\n  \"language\": \"csharp\",\n  \"frameworks\": [\"netstandard2.0\"],\n  \"sources\": [\"*.cs\"],\n  \"options\": { \"warningsAsErrors\": true, \"noWarn\": [\"1591\", \"1701\"] },\n  \"internalsVisibleTo\": [\"C.Tests\"],\n  \"assemblyAttributes\": [{ \"type\": \"System.CLSCompliantAttribute\", \"args\": [\"true\"] }]\n}\n"
        let resolved = ref None
        let resolve () =
            x.Run "compose" [ "compose" => recipe {
                                 do! alwaysRerun ()
                                 let! c = csc { out (File.make "out/C.dll"); project (dir + "/" + Description.FileName); config "Release"; resolve }
                                 resolved.Value <- Some c } ] [ "compose" ]
            resolved.Value.Value
        let c = resolve ()
        Assert.That(c.Options, Does.Contain "/warnaserror+")
        Assert.That(c.Options, Does.Contain "/nowarn:1701,1702,1591")
        let path, content = c.Generated.Head
        Assert.That(path, Does.EndWith "obj/xake/C/netstandard2.0/C.AssemblyAttributes.cs")
        Assert.That(content,
            Is.EqualTo "// <auto-generated/> by Xake from the project description\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"C.Tests\")]\n[assembly: System.CLSCompliantAttribute(true)]\n")
        Assert.That(c.Sources.Head, Is.EqualTo path)

        write "{\n  \"name\": \"C\",\n  \"project\": \"C.csproj\",\n  \"language\": \"csharp\",\n  \"frameworks\": [\"netstandard2.0\"],\n  \"sources\": [\"*.cs\"]\n}\n"
        let plain = resolve ()
        Assert.That(plain.Options, Has.None.EqualTo "/warnaserror+")
        Assert.That(plain.Options, Does.Contain "/nowarn:1701,1702", "the SDK's defaults, always")
        Assert.That(plain.Generated, Is.Empty)
