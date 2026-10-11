namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Tasks
open Xake.Dotnet

/// A project description (`Description`) turned into a compilation: what `fsc { project
/// "path" }` / `csc { project "path" }` do, and the package step a lock generation runs before
/// them (`restore`). Nothing here resolves a package inside the compile: the description's
/// packages come from `restore` (an msbuild restore of a synthesized project) and are handed
/// to the block with `packages`.
module Described =

    /// What `restore` resolved for one description and framework: the compile-time assemblies
    /// of every package in the restore graph (absolute paths, sorted) and the graph itself, as
    /// a lock entry records it (`Lock.Entry.Packages`).
    type Restored = { Framework: string; References: string list; Packages: Package list }

    /// What follows `project` in a block: the description file, the configuration (`config`)
    /// and the restored packages (`packages`).
    type Spec = { Path: string; Config: string option; Restored: Restored option }

    let private same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)

    /// The symbols the .NET SDK defines for a target framework (`_ImplicitDefineConstant` in
    /// `Microsoft.NET.Sdk.BeforeCommon.targets`), in the SDK's order: `netstandard2.0` ->
    /// `NETSTANDARD; NETSTANDARD2_0; NETSTANDARD1_0_OR_GREATER .. NETSTANDARD2_0_OR_GREATER`,
    /// `net462` -> `NETFRAMEWORK; NET462; NET20_OR_GREATER .. NET462_OR_GREATER`, `net8.0` ->
    /// `NET; NET8_0; NETCOREAPP; NET5_0_OR_GREATER .. NET8_0_OR_GREATER;
    /// NETCOREAPP1_0_OR_GREATER .. NETCOREAPP3_1_OR_GREATER`. Unknown monikers give none.
    let frameworkDefines (framework: string) : string list =
        let f = framework.ToLowerInvariant ()
        let upTo (versions: string list) (v: string) prefix =
            match List.tryFindIndex ((=) v) versions with
            | Some i -> versions |> List.take (i + 1) |> List.map (fun v -> prefix + v + "_OR_GREATER")
            | None -> []
        let netstandard = [ "1_0"; "1_1"; "1_2"; "1_3"; "1_4"; "1_5"; "1_6"; "2_0"; "2_1" ]
        let netcoreapp = [ "1_0"; "1_1"; "2_0"; "2_1"; "2_2"; "3_0"; "3_1" ]
        let netfx = [ "20"; "30"; "35"; "40"; "45"; "451"; "452"; "46"; "461"; "462"; "47"; "471"; "472"; "48"; "481" ]
        let m = System.Text.RegularExpressions.Regex.Match (f, @"^(netstandard|netcoreapp|net)(\d+)\.(\d+)$")
        if m.Success then
            let kind, v = m.Groups.[1].Value, m.Groups.[2].Value + "_" + m.Groups.[3].Value
            match kind with
            | "netstandard" -> [ "NETSTANDARD"; "NETSTANDARD" + v ] @ upTo netstandard v "NETSTANDARD"
            | "netcoreapp" -> [ "NETCOREAPP"; "NETCOREAPP" + v ] @ upTo netcoreapp v "NETCOREAPP"
            | _ ->
                let major = int m.Groups.[2].Value
                [ "NET"; "NET" + v; "NETCOREAPP" ]
                @ [ for n in 5 .. major -> sprintf "NET%d_0_OR_GREATER" n ]
                @ (netcoreapp |> List.map (fun v -> "NETCOREAPP" + v + "_OR_GREATER"))
        elif System.Text.RegularExpressions.Regex.IsMatch (f, @"^net\d+$") && List.contains (f.Substring 3) netfx then
            let v = f.Substring 3
            [ "NETFRAMEWORK"; "NET" + v ] @ upTo netfx v "NET"
        else []

    /// The configuration preset the .NET SDK applies (SDK 10.0.401: `Microsoft.NET.Sdk.props`,
    /// `Microsoft.FSharp.NetSdk.props`, the `ImplicitConfigurationDefine` of
    /// `Microsoft.NET.Sdk.CSharp.targets`/`FSharpTargetsShim.targets`; checked against the
    /// compiler command lines of `dotnet build -c Debug|Release`): the defines and the
    /// switches, and for F# whether tail calls stay on.
    ///
    /// | | F# | C# |
    /// |---|---|---|
    /// | Debug | `--debug+ --debug:portable --optimize- --tailcalls-`, TRACE;DEBUG | `/debug+ /debug:portable /optimize-`, TRACE;DEBUG |
    /// | Release | `--debug:portable --optimize+`, TRACE;RELEASE | `/debug- /debug:portable /optimize+`, TRACE;RELEASE |
    let preset (language: Description.Language) (config: string) : string list * string list * bool =
        match language, config.ToLowerInvariant () with
        | Description.FSharp, "debug" -> [ "TRACE"; "DEBUG" ], [ "--debug+"; "--debug:portable"; "--optimize-" ], false
        | Description.FSharp, "release" -> [ "TRACE"; "RELEASE" ], [ "--debug:portable"; "--optimize+" ], true
        | Description.CSharp, "debug" -> [ "TRACE"; "DEBUG" ], [ "/debug+"; "/debug:portable"; "/optimize-" ], true
        | Description.CSharp, "release" -> [ "TRACE"; "RELEASE" ], [ "/debug-"; "/debug:portable"; "/optimize+" ], true
        | _ -> failwithf "config '%s': only Debug and Release are known (the SDK's own presets)" config

    /// The framework assemblies the SDK references implicitly for a .NET Framework 4.5+ target
    /// (`_SDKImplicitReference`, `Microsoft.NET.Sdk.BeforeCommon.targets`); an import leaves
    /// them out of a description, `project` adds them back.
    let implicitFrameworkReferences =
        [ "System"; "System.Data"; "System.Drawing"; "System.Xml"; "System.Core"; "System.Runtime.Serialization"
          "System.Xml.Linq"; "System.Numerics"; "System.IO.Compression.FileSystem" ]

    /// The package the F# SDK references implicitly (`Microsoft.FSharp.NetSdk.props`): `System.ValueTuple`
    /// 4.6.2 for .NET Framework 4.0 to 4.7, unless the description names it.
    let implicitPackages (d: Description.Project) (framework: string) : Description.Package list =
        let f = framework.ToLowerInvariant ()
        let old = [ "net40"; "net45"; "net451"; "net452"; "net46"; "net461"; "net462"; "net47" ]
        let named = (d.Packages @ (d.ByFramework |> List.collect (fun (_, i) -> i.Packages))) |> List.exists (fun p -> p.Id = "System.ValueTuple")
        if d.Language = Description.FSharp && List.contains f old && not named then
            [ { Id = "System.ValueTuple"; Version = Some "4.6.2"; Private = false } ]
        else []

    let private normal (path: string) = path.Replace ('\\', '/')

    /// The package items of one framework: the project-wide ones, then `byFramework`'s.
    let private itemsFor (d: Description.Project) (framework: string) =
        let extra = d.ByFramework |> List.tryPick (fun (tfm, items) -> if same tfm framework then Some items else None)
        let pick f = extra |> Option.map f |> Option.defaultValue []
        d.Packages @ pick (fun i -> i.Packages), d.Defines @ pick (fun i -> i.Defines),
        d.References @ pick (fun i -> i.References), d.Options.Args @ pick (fun i -> i.Args)

    /// The synthesized project `restore` runs `dotnet restore` on: one `PackageReference`
    /// per package of the description for `framework`, the version as the description states
    /// it (exact, a range, or none -- floating is left to msbuild), and no implicit framework
    /// package (`NETStandard.Library`, the .NET Framework reference assemblies), which the
    /// compile takes from its target framework instead.
    let restoreProjectText (framework: string) (packages: Description.Package list) =
        [ yield "<Project Sdk=\"Microsoft.NET.Sdk\">"
          yield "  <PropertyGroup>"
          yield sprintf "    <TargetFramework>%s</TargetFramework>" framework
          yield "    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>"
          yield "    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>"
          yield "  </PropertyGroup>"
          yield "  <ItemGroup>"
          for p in packages do
              let version = p.Version |> Option.map (sprintf " Version=\"%s\"") |> Option.defaultValue ""
              let privateAssets = if p.Private then " PrivateAssets=\"all\"" else ""
              yield sprintf "    <PackageReference Include=\"%s\"%s%s />" p.Id version privateAssets
          yield "  </ItemGroup>"
          yield "</Project>"
          yield "" ]
        |> String.concat "\n"

    /// <summary>
    /// Resolves the packages of description `d` for `framework` the way msbuild does: writes
    /// `restoreProjectText` to `obj/xake/packages/&lt;name&gt;/&lt;framework&gt;/restore.csproj` under
    /// the project root, runs `dotnet restore` on it into the build's package folder
    /// (`DotNetFwk.packageRoot`: `NUGET_PACKAGES`), then reads its `project.assets.json`
    /// (`Nuget.readAssets`, `Nuget.compileItems`). The repository's `nuget.config` applies; its
    /// `Directory.Build.props`/`Directory.Packages.props` do not. This is the lock-generation
    /// step: `fsc`/`csc` never call it, they take its result through `packages`. A description
    /// without packages for `framework` gives an empty result without running anything.
    /// </summary>
    let restore (d: Description.Project) (framework: string) : Recipe<ExecContext, Restored> =
        recipe {
            let packages, _, _, _ = itemsFor d framework
            let packages = packages @ implicitPackages d framework
            if List.isEmpty packages then
                return { Framework = framework; References = []; Packages = [] }
            else
                let! options = getCtxOptions ()
                let! root = DotNetFwk.packageRoot ()
                let root = normal root |> fun r -> r.TrimEnd '/'
                let dir = Path.Combine (options.ProjectRoot, "obj", "xake", "packages", d.Name, framework)
                Directory.CreateDirectory dir |> ignore
                let project = Path.Combine (dir, "restore.csproj")
                File.WriteAllText (project, restoreProjectText framework packages)
                let! exitCode =
                    shell {
                        cmd "dotnet"
                        args [ "restore"; project; "-v:quiet"; "-nologo"; "-p:NuGetAudit=false"
                               "-p:ImportDirectoryBuildProps=false"; "-p:ImportDirectoryBuildTargets=false"
                               "-p:ImportDirectoryPackagesProps=false"; "-p:ManagePackageVersionsCentrally=false" ]
                        env ("NUGET_PACKAGES", root)
                        workdir dir
                        logprefix "[restore]"
                        stdoutlevel (Tool.diagnosticLevel Level.Verbose)
                        erroutlevel (Tool.diagnosticLevel Level.Verbose)
                    }
                if exitCode <> 0 then
                    failwithf "'%s': restoring the packages of the description for %s failed with exit code %d (see '%s'): %s"
                        d.Name framework exitCode project
                        (packages |> List.map (fun p -> p.Id + " " + defaultArg p.Version "*") |> String.concat ", ")
                let assets = Path.Combine (dir, "obj", "project.assets.json")
                let graph = Nuget.readAssets assets framework
                let references =
                    Nuget.compileItems assets framework
                    |> List.map (fun (id, version, item) ->
                        normal (Path.Combine (root, id.ToLowerInvariant (), version.ToLowerInvariant (), item)))
                    |> List.distinct
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal (a, b))
                return { Framework = framework; References = references; Packages = Lock.packagesOf root graph }
        }

    let private hasWildcard (s: string) = s.Contains "*" || s.Contains "?"

    /// <summary>
    /// The files absolute C# source masks name, read through `getFiles` so that the target
    /// depends on the listing (an added `.cs` reruns it). The convention is the import's
    /// (`Import.csharpMasks`): an entry is an include, a `!` prefix makes it an exclude, applied in
    /// order (`["**/*.cs", "!obj/**/*", "!bin/**/*"]`), and `dir/**/*` is every file below
    /// `dir`. The masks become one Xake fileset whose base is the leading wildcard-free folder
    /// of the first include; the files come out sorted (ordinal).
    /// </summary>
    let expand (masks: string list) : Recipe<ExecContext, string list> =
        recipe {
            let split (mask: string) =
                let isExclude = mask.StartsWith "!"
                isExclude, normal (if isExclude then mask.Substring 1 else mask)
            let entries = masks |> List.map split
            match entries |> List.tryFind (fst >> not) with
            | None -> return []
            | Some (_, first) ->
                let fixedOf (mask: string) =
                    let parts = mask.Split '/' |> List.ofArray
                    let fixedParts = parts |> List.takeWhile (hasWildcard >> not)
                    String.concat "/" fixedParts, parts |> List.skip fixedParts.Length |> String.concat "/"
                let baseDir =
                    match fixedOf first with
                    | dir, "" -> normal (Path.GetDirectoryName dir)
                    | dir, _ -> dir
                let relative (mask: string) =
                    let dir, rest = fixedOf mask
                    let rel =
                        if dir = baseDir then "."
                        elif dir.StartsWith (baseDir + "/") then dir.Substring (baseDir.Length + 1)
                        else
                            System.Uri.UnescapeDataString (
                                (System.Uri (baseDir.TrimEnd '/' + "/")).MakeRelativeUri(System.Uri dir).ToString ())
                    match rel, rest with
                    | ".", r -> r
                    | r, "" -> r
                    | r, rest -> r + "/" + rest
                let fs =
                    entries |> List.fold (fun (fs: Fileset) (isExclude, mask) ->
                        if isExclude then fs - relative mask else fs + relative mask) (Fileset.(~+) baseDir)
                let! (Filelist found) = getFiles fs
                return
                    found |> List.map (File.getFullName >> normal) |> List.distinct
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal (a, b))
        }

    /// What `project` contributes to a compilation, resolved at recipe time.
    type internal Plan = {
        Project: Description.Project
        Framework: string
        /// the project file's folder, absolute, '/'
        ProjectDir: string
        Defines: string list
        Args: string list
        GlobalRefs: string list
        PackageRefs: string list
        /// sources (F#) or absolute masks (C#)
        Sources: string list
        Out: File
        Doc: string option
        Target: TargetType
        Tailcalls: bool
        /// the generated `InternalsVisibleTo` source, when the description names friends
        Friends: (string * string) option }

    let private fullOf (root: string) (path: string) =
        normal (Path.GetFullPath (if Path.IsPathRooted path then path else Path.Combine (root, path)))

    /// The description's plan for one framework, from the settings `project` follows.
    let internal plan (language: Description.Language) (spec: Spec) (targetFramework: string) (out: File) (refs: Fileset) =
        recipe {
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let descriptionPath = fullOf root spec.Path
            let! d = Description.load descriptionPath
            if d.Language <> language then
                failwithf "'%s': the description '%s' is %s, not for this compiler" d.Name spec.Path
                    (match d.Language with Description.FSharp -> "F#" | Description.CSharp -> "C#")
            let projectDir = fullOf (Path.GetDirectoryName descriptionPath) d.Project |> Path.GetDirectoryName |> normal
            let inProject = fullOf projectDir

            // the framework: the block's `targetfwk`, else the description's only one, else NETFX-TARGET
            let! netfxTarget = getVar "NETFX-TARGET"
            let framework =
                let pick (f: string) =
                    match d.Frameworks |> List.tryFind (same f) with
                    | Some f -> f
                    | None -> failwithf "'%s': framework '%s' is not one of the description's (%s)" d.Name f (String.concat ", " d.Frameworks)
                match targetFramework, d.Frameworks, netfxTarget with
                | f, _, _ when not (System.String.IsNullOrEmpty f) -> pick f
                | _, [ only ], _ -> only
                | _, _, Some f when f.Trim () <> "" -> pick (f.Trim ())
                | _ ->
                    failwithf "'%s': the description targets %s; choose one with targetfwk before project" d.Name (String.concat ", " d.Frameworks)

            let packages, defines, references, extraArgs = itemsFor d framework
            let packageRefs =
                match packages, spec.Restored with
                | [], _ -> []
                | _, None ->
                    failwithf "'%s': the description references packages (%s); resolve them with Described.restore and pass the result with `packages` -- fsc and csc do not resolve packages"
                        d.Name (packages |> List.map (fun p -> p.Id) |> String.concat ", ")
                | _, Some r when not (same r.Framework framework) ->
                    failwithf "'%s': the packages were restored for %s, the compilation is for %s" d.Name r.Framework framework
                | _, Some r -> r.References

            // a projectRef is passed as a reference by the script (its output path is the script's layout)
            let (Filelist given) = toFileList root refs
            for name in d.ProjectRefs do
                if not (given |> List.exists (fun f -> same (Path.GetFileNameWithoutExtension f.FullName) name)) then
                    do! trace Warning "'%s': projectRef '%s' is not among the references; pass its output with `ref` before `project`" d.Name name

            let! out =
                if out <> File.undefined then recipe { return out } else
                recipe {
                    let! ctx = getCtx ()
                    let files =
                        ctx.Targets |> List.choose (function FileTarget f -> Some f | _ -> None)
                        |> List.filter (fun f -> not (f.Name.EndsWith ".runtimeconfig.json"))
                    match files with
                    | f :: _ -> return f
                    | [] ->
                        let! f = getTargetFile ()
                        return f
                }

            let configDefines, configArgs, tailcalls =
                match spec.Config with
                | Some cfg -> preset language cfg
                | None -> [], [], true
            let traceDefine, configName = (configDefines |> List.truncate 1), (configDefines |> List.skip (min 1 configDefines.Length))
            let o = d.Options
            let fs = language = Description.FSharp
            let switch (name: string) value = if fs then sprintf "--%s:%s" name value else sprintf "/%s:%s" name value
            // embedded resources, plain files: the manifest name as given, else msbuild's default
            // (root namespace, else assembly name, then the path with '.' for '/')
            let resources =
                d.Resources |> List.map (fun r ->
                    if r.File.EndsWith (".resx", System.StringComparison.OrdinalIgnoreCase) then
                        failwithf "'%s': resource '%s': a .resx in a description is not compiled by `project` yet" d.Name r.File
                    let prefix = d.RootNamespace |> Option.orElse d.AssemblyName |> Option.defaultValue d.Name
                    let name = r.Name |> Option.defaultValue (prefix + "." + (normal r.File).Replace ('/', '.'))
                    switch "resource" (inProject r.File + "," + name))
            let pathmap = (root |> normal |> fun r -> r.TrimEnd '/') + "/=/_/"
            let args =
                configArgs
                @ [ (if fs then "--deterministic+" else "/deterministic+"); switch "pathmap" pathmap ]
                @ (o.LangVersion |> Option.map (switch "langversion") |> Option.toList)
                @ (if o.WarningsAsErrors then [ (if fs then "--warnaserror+" else "/warnaserror+") ] else [])
                @ (match o.Nullable with
                   | Some n when not fs -> [ switch "nullable" n ]
                   | Some "enable" -> [ "--checknulls+" ]
                   | _ -> [])
                @ (o.KeyFile |> Option.map (inProject >> switch "keyfile") |> Option.toList)
                @ resources
                @ extraArgs

            let assemblyName = Path.GetFileNameWithoutExtension out.Name
            let friends =
                match d.InternalsVisibleTo with
                | [] -> None
                | list ->
                    let attr (f: Description.Friend) =
                        let value = match f.Key with Some key -> sprintf "%s, PublicKey=%s" f.Name key | None -> f.Name
                        if fs then sprintf "[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"%s\")>]" value
                        else sprintf "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"%s\")]" value
                    let path =
                        normal (Path.Combine (root, "obj", "xake", assemblyName, framework,
                                              assemblyName + ".InternalsVisibleTo" + (if fs then ".fs" else ".cs")))
                    let body =
                        [ yield "// <auto-generated/> by Xake from the project description"
                          if fs then yield "namespace FSharp"; yield ""
                          yield! list |> List.map attr
                          if fs then yield "do ()"
                          yield "" ]
                    Some (path, String.concat "\n" body)

            return
                { Project = d
                  Framework = framework
                  ProjectDir = projectDir
                  Defines = (traceDefine @ defines @ configName @ frameworkDefines framework) |> List.distinct
                  Args = args
                  GlobalRefs =
                    (if List.contains "NETFRAMEWORK" (frameworkDefines framework) then implicitFrameworkReferences
                     // composed csc references only mscorlib for netstandard (fsc adds netstandard.dll itself)
                     elif not fs && List.contains "NETSTANDARD" (frameworkDefines framework) then [ "netstandard" ]
                     else []) @ references
                    |> List.map (fun r -> if r.EndsWith ".dll" then r else r + ".dll") |> List.distinct
                  PackageRefs = packageRefs
                  Sources =
                    d.Sources |> List.map (fun s -> if s.StartsWith "!" then "!" + inProject (s.Substring 1) else inProject s)
                  Out = out
                  Doc = if o.Doc then Some (normal (Path.ChangeExtension (out.FullName, ".xml"))) else None
                  Target = (match d.Kind with Description.Library -> TargetType.Library | Description.Exe -> TargetType.Exe)
                  Tailcalls = tailcalls
                  Friends = friends } : Plan
        }

    let private filesOf (paths: string list) =
        paths |> List.fold (fun fs p -> fs + (!! p)) Fileset.Empty

    /// The resolved `Fsc` of `fsc { ...; project path; ... }`: the settings with the plan
    /// applied, `Fsc.ofSettings`, and the generated `InternalsVisibleTo` source first.
    let fsc (settings: FscSettingsType) (spec: Spec) : Recipe<ExecContext, Fsc> =
        recipe {
            let! p = plan Description.FSharp spec settings.TargetFramework settings.Out settings.Ref
            let s =
                { settings with
                    TargetFramework = p.Framework
                    Out = p.Out
                    Target = p.Target
                    Src = settings.Src + filesOf p.Sources
                    Ref = settings.Ref + filesOf p.PackageRefs
                    RefGlobal = settings.RefGlobal @ p.GlobalRefs
                    Define = settings.Define @ p.Defines |> List.distinct
                    Doc = (match p.Doc with Some doc -> File.make doc | None -> settings.Doc)
                    Tailcalls = settings.Tailcalls && p.Tailcalls
                    CommandArgs = settings.CommandArgs @ p.Args }
            let! f = Fsc.ofSettings s
            match p.Friends with
            | None -> return f
            | Some (path, content) ->
                return { f with Fsc.Sources = path :: f.Sources; Fsc.Generated = (path, content) :: f.Generated }
        }

    /// The resolved `Csc` of `csc { ...; project path; ... }`, its `Sources` the description's
    /// masks (absolute), as a lock records them; the files they name are expanded (`expand`,
    /// a `getFiles` dependency) for the composition and by `run`. The generated
    /// `InternalsVisibleTo` source comes first.
    let csc (settings: CscSettingsType) (spec: Spec) : Recipe<ExecContext, Csc> =
        recipe {
            let! p = plan Description.CSharp spec settings.TargetFramework settings.Out settings.Ref
            let! files = expand p.Sources
            let s =
                { settings with
                    TargetFramework = p.Framework
                    Out = p.Out
                    Target = p.Target
                    Src = settings.Src + filesOf files
                    Ref = settings.Ref + filesOf p.PackageRefs
                    RefGlobal = settings.RefGlobal @ p.GlobalRefs
                    Define = settings.Define @ p.Defines |> List.distinct
                    CommandArgs = settings.CommandArgs @ p.Args @ (p.Doc |> Option.map ((+) "/doc:") |> Option.toList) }
            let! c = Csc.ofSettings s
            let generated = p.Friends |> Option.toList
            return { c with Sources = (generated |> List.map fst) @ p.Sources; Generated = generated @ c.Generated }
        }

    /// `c` with every source mask replaced by the files it names now (`expand`).
    let expandCsc (c: Csc) : Recipe<ExecContext, Csc> =
        recipe {
            // the generated sources are files, kept in front; the rest are the masks
            let generated = c.Generated |> List.map fst
            let! files = expand (c.Sources |> List.filter (fun s -> not (List.contains s generated)))
            return { c with Sources = (c.Sources |> List.filter (fun s -> List.contains s generated)) @ files }
        }

    /// <summary>
    /// Builds the C# compilation `masked` (its `Sources` the description's masks) gated by
    /// the lock at `path`, the way `Lock.buildWith` gates a composed one: a missing lock is
    /// recorded (under CI it fails), a present one must equal `masked` (masks compared as
    /// written), and the recorded entry is compiled with its masks expanded (`expand`), so
    /// the lock keeps the masks and an added `.cs` is compiled without touching it.
    /// </summary>
    let buildCsc (options: Lock.Options) (path: string) (masked: Csc) : Recipe<ExecContext, unit> =
        recipe {
            let! ctxOptions = getCtxOptions ()
            let full = fullOf ctxOptions.ProjectRoot path
            if not (File.Exists full) then
                let! ci = Lock.underCi
                if ci then
                    let msg = sprintf "'%s': the lock '%s' is not there. Under CI a lock is never recorded: record it on a developer machine and commit it." masked.Name path
                    do! trace Error "%s" msg
                    failwith msg
                do! Lock.record path (Lock.Compilation.Csc masked)
            else
                let! differences = Lock.verify path (Lock.Compilation.Csc masked)
                if not (List.isEmpty differences) then
                    let msg =
                        sprintf "'%s': the resolved compilation differs from the lock '%s':\n%s\nUpdate the lock deliberately: delete '%s', or run the target that calls Lock.record \"%s\"."
                            masked.Name path (differences |> String.concat "\n") path path
                    do! trace Error "%s" msg
                    if options.Run.FailOnError then failwith msg
            let! doc = Lock.load path
            let recorded = Lock.entry masked.Name doc
            let! expanded = expandCsc recorded.Csc
            do! Lock.compileWith options { recorded with Compilation = Lock.Compilation.Csc expanded }
        }

/// The state of an `fsc {}` block after `project "path"`: the settings so far, the
/// description and what `config`, `packages` and `lock` added. Only those, `nofetch` and
/// `resolve` may follow `project`.
type FscDescribed = { FscSettings: FscSettingsType; FscSpec: Described.Spec; FscLock: (string * bool) option }

/// `fsc { ...; project "path"; ...; resolve }`: the resolved `Fsc`.
type FscDescribedRequest = FscDescribedRequest of FscDescribed

/// The C# twin of `FscDescribed`.
type CscDescribed = { CscSettings: CscSettingsType; CscSpec: Described.Spec; CscLock: (string * bool) option }

/// `csc { ...; project "path"; ...; resolve }`: the resolved `Csc`, sources as masks.
type CscDescribedRequest = CscDescribedRequest of CscDescribed

/// `fsc { project "path" }`, `config`, `packages`: an F# compilation from a project
/// description (`Described.fsc`). The operations before `project` (`targetfwk`, `out`, `ref`
/// for project references, ...) are the settings it builds on:
/// <code>
/// "out/Release/netstandard2.0/Xake.dll" ..> recipe {
///     let! d = Description.load "src/core/xake.project.json"
///     let! pkgs = Described.restore d "netstandard2.0"          // lock generation's step
///     do! fsc { targetfwk "netstandard2.0"; project "src/core/xake.project.json"; config "Release"; packages pkgs }
/// }
/// </code>
[<AutoOpen>]
module FscDescribedBuilder =
    type FscSettingsBuilder with
        /// <summary>Compiles the project the description at `path` (relative to the project root)
        /// describes: its sources in order, defines, framework references, the packages given
        /// with `packages`, options, friends, `--deterministic+` and `--pathmap` of the project
        /// root. The framework is `targetfwk`, else the description's only one, else
        /// `NETFX-TARGET`.</summary>
        [<CustomOperation("project")>]
        member _.Project(s: FscSettingsType, path: string) =
            { FscSettings = s; FscSpec = { Path = path; Config = None; Restored = None }; FscLock = None }

        /// <summary>The SDK's configuration preset, `Debug` or `Release` (`Described.preset`).</summary>
        [<CustomOperation("config")>]
        member _.Config(d: FscDescribed, config: string) = { d with FscSpec = { d.FscSpec with Config = Some config } }

        /// <summary>The description's packages as `Described.restore` resolved them; required
        /// when it has any.</summary>
        [<CustomOperation("packages")>]
        member _.Packages(d: FscDescribed, restored: Described.Restored) = { d with FscSpec = { d.FscSpec with Restored = Some restored } }

        /// <summary>Gates the compilation by the lock at `path`, as `lock` does for settings.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(d: FscDescribed, path: string) = { d with FscLock = Some (path, true) }

        /// <summary>After `lock`: never fetch a package the lock names.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(d: FscDescribed) = { d with FscLock = d.FscLock |> Option.map (fun (p, _) -> p, false) }

        /// <summary>Returns the resolved `Fsc` instead of compiling; must be the last operation.</summary>
        [<CustomOperation("resolve")>]
        member _.Resolve(d: FscDescribed) = FscDescribedRequest d

        member _.Run(FscDescribedRequest d) = Described.fsc d.FscSettings d.FscSpec

        member _.Run(d: FscDescribed) =
            recipe {
                let! f = Described.fsc d.FscSettings d.FscSpec
                let! run = Fsc.runOptions { d.FscSettings with TargetFramework = f.Framework }
                match d.FscLock with
                | None -> do! Fsc.run run f
                | Some (path, fetch) ->
                    do! Lock.buildWith
                            { Lock.Options.Default with FscRun = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                            path (Lock.Compilation.Fsc f)
            }

/// `csc { project "path" }`, `config`, `packages`: the C# twin of `fsc { project }`. The
/// description's sources are masks; they stay masks in the resolved `Csc` and in its lock, and
/// are expanded when compiling (`Described.expand`, a `getFiles` dependency).
[<AutoOpen>]
module CscDescribedBuilder =
    type CscSettingsBuilder with
        /// <summary>Compiles the project the description at `path` describes; see `fsc { project }`.</summary>
        [<CustomOperation("project")>]
        member _.Project(s: CscSettingsType, path: string) =
            { CscSettings = s; CscSpec = { Path = path; Config = None; Restored = None }; CscLock = None }

        /// <summary>The SDK's configuration preset, `Debug` or `Release` (`Described.preset`).</summary>
        [<CustomOperation("config")>]
        member _.Config(d: CscDescribed, config: string) = { d with CscSpec = { d.CscSpec with Config = Some config } }

        /// <summary>The description's packages as `Described.restore` resolved them.</summary>
        [<CustomOperation("packages")>]
        member _.Packages(d: CscDescribed, restored: Described.Restored) = { d with CscSpec = { d.CscSpec with Restored = Some restored } }

        /// <summary>Gates the compilation by the lock at `path`; the lock keeps the masks
        /// (`Described.buildCsc`).</summary>
        [<CustomOperation("lock")>]
        member _.Lock(d: CscDescribed, path: string) = { d with CscLock = Some (path, true) }

        /// <summary>After `lock`: never fetch a package the lock names.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(d: CscDescribed) = { d with CscLock = d.CscLock |> Option.map (fun (p, _) -> p, false) }

        /// <summary>Returns the resolved `Csc` (sources as masks) instead of compiling.</summary>
        [<CustomOperation("resolve")>]
        member _.Resolve(d: CscDescribed) = CscDescribedRequest d

        member _.Run(CscDescribedRequest d) = Described.csc d.CscSettings d.CscSpec

        member _.Run(d: CscDescribed) =
            recipe {
                let! masked = Described.csc d.CscSettings d.CscSpec
                let! run = Csc.runOptions { d.CscSettings with TargetFramework = masked.Framework }
                match d.CscLock with
                | None ->
                    let! c = Described.expandCsc masked
                    do! Csc.run run c
                | Some (path, fetch) ->
                    do! Described.buildCsc
                            { Lock.Options.Default with Run = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                            path masked
            }
