namespace Xake.Dotnet

open System.IO

open Xake

/// <summary>
/// A project description (`Description`) turned into what a composed compilation needs: what
/// `csc { project "path" }` and `fsc { project "path" }` add to their settings. Only what a
/// description says by itself is honoured -- sources, resources, defines, options, assembly
/// attributes, framework assemblies by name or path and the shared frameworks of the SDK's
/// packs. Anything that needs a package restore or a lock (`packages`, a `projectRefs` entry
/// with no output given) is rejected with a message naming Xake.Hermetic.Dotnet; nothing here
/// runs msbuild or resolves a NuGet graph.
/// </summary>
module ProjectPlan =

    /// What `project` contributes to a compilation for one framework, resolved at recipe time.
    type Plan = {
        Description: Description.Project
        /// the description file, absolute, '/'
        Path: string
        /// the framework picked from the description's (`targetfwk`, the only one, `NETFX-TARGET`)
        Framework: string
        /// the project file's folder, absolute, '/': the compiler runs there, as under `dotnet build`
        ProjectDir: string
        /// the description's defines, project-wide then the framework's
        Defines: string list
        /// the symbols the SDK defines for the framework (`frameworkDefines`)
        FrameworkDefines: string list
        /// switches in the compiler's own syntax: deterministic, options, resources, then the
        /// description's raw `args`
        Args: string list
        /// framework assemblies by name, `.dll` appended (`grefs`)
        GlobalRefs: string list
        /// assemblies by path, absolute: `references` given as paths, then the shared frameworks'
        References: string list
        /// the source files in compile order, absolute, '/'
        Sources: string list
        Kind: Description.Kind
        Doc: bool
        /// the generated source of `internalsVisibleTo` and `assemblyAttributes`, when it names any
        Generated: (string * string) option
        /// the `.resx` resources as (resx, compiled `.resources`), absolute
        Resx: (string * string) list }

    let private same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)

    let private normal (path: string) = path.Replace ('\\', '/')

    let private fullOf (root: string) (path: string) =
        normal (Path.GetFullPath (if Path.IsPathRooted path then path else Path.Combine (root, path)))

    /// The base framework of a platform framework (`net8.0-windows` -> `net8.0`), the framework
    /// itself otherwise.
    let baseFramework (framework: string) : string =
        let m = System.Text.RegularExpressions.Regex.Match (framework, @"^([A-Za-z]+\d+(?:\.\d+)*)-[A-Za-z]+(\d+(\.\d+)*)?$")
        if m.Success then m.Groups.[1].Value.ToLowerInvariant () else framework

    /// The symbols the .NET SDK defines for a target framework (`_ImplicitDefineConstant` in
    /// `Microsoft.NET.Sdk.BeforeCommon.targets`), in the SDK's order: `netstandard2.0` ->
    /// `NETSTANDARD; NETSTANDARD2_0; NETSTANDARD1_0_OR_GREATER .. NETSTANDARD2_0_OR_GREATER`,
    /// `net462` -> `NETFRAMEWORK; NET462; NET20_OR_GREATER .. NET462_OR_GREATER`, `net8.0` ->
    /// `NET; NET8_0; NETCOREAPP; NET5_0_OR_GREATER .. NET8_0_OR_GREATER;
    /// NETCOREAPP1_0_OR_GREATER .. NETCOREAPP3_1_OR_GREATER`. A platform framework gives its
    /// base's (the platform's own symbols are not added). Unknown monikers give none.
    let frameworkDefines (framework: string) : string list =
        let f = (baseFramework framework).ToLowerInvariant ()
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

    /// The framework assemblies the SDK references implicitly for a .NET Framework 4.5+ target
    /// (`_SDKImplicitReference`, `Microsoft.NET.Sdk.BeforeCommon.targets`); an import leaves
    /// them out of a description, `project` adds them back.
    let implicitFrameworkReferences =
        [ "System"; "System.Data"; "System.Drawing"; "System.Xml"; "System.Core"; "System.Runtime.Serialization"
          "System.Xml.Linq"; "System.Numerics"; "System.IO.Compression.FileSystem" ]

    /// The warnings the SDK suppresses for every project (`Microsoft.NET.Sdk.CSharp.props`:
    /// `1701;1702`; the F# SDK sets none); `project` adds the description's `noWarn` to them.
    let sdkNoWarn (language: Description.Language) : string list =
        match language with
        | Description.CSharp -> [ "1701"; "1702" ]
        | Description.FSharp -> []

    let private everettId (c: char) =
        System.Char.IsLetterOrDigit c
        || (match System.Char.GetUnicodeCategory c with
            | System.Globalization.UnicodeCategory.ConnectorPunctuation
            | System.Globalization.UnicodeCategory.NonSpacingMark
            | System.Globalization.UnicodeCategory.SpacingCombiningMark
            | System.Globalization.UnicodeCategory.EnclosingMark -> true
            | _ -> false)
    let private everettFirst (c: char) =
        System.Char.IsLetter c || System.Char.GetUnicodeCategory c = System.Globalization.UnicodeCategory.ConnectorPunctuation

    /// A folder path as msbuild makes it part of a manifest name (`MakeValidEverettIdentifier`):
    /// `/` -> `.`, in each dot-separated part a character that is not an identifier character
    /// becomes `_`, and a part that does not start like an identifier gets a leading `_`.
    let private everett (dir: string) =
        let b = System.Text.StringBuilder ()
        let part (s: string) =
            if s.Length > 0 then
                if everettFirst s.[0] then b.Append s.[0] |> ignore
                else
                    b.Append '_' |> ignore
                    if everettId s.[0] then b.Append s.[0] |> ignore
                for c in s.Substring 1 do b.Append (if everettId c then c else '_') |> ignore
        // a folder that comes out as a single `_` becomes `__` (`MakeValidEverettFolderIdentifier`)
        let folder (s: string) =
            let start = b.Length
            s.Split '.' |> Array.iteri (fun i p -> (if i > 0 then b.Append '.' |> ignore); part p)
            if b.Length - start = 1 && b.[start] = '_' then b.Append '_' |> ignore
        (normal dir).Split '/' |> Array.iteri (fun i f -> (if i > 0 then b.Append '.' |> ignore); folder f)
        b.ToString ()

    /// <summary>
    /// msbuild's default manifest name of an embedded resource (`CreateCSharpManifestResourceName`,
    /// no `DependentUpon`, no culture): `rootNamespace`, the folder of `file` (relative to the
    /// project folder) as an identifier (`a/b c` -> `a.b_c`, `1st` -> `_1st`), the file name --
    /// for a `.resx` without its extension, plus `.resources`. Empty parts are left out.
    /// </summary>
    let manifestName (rootNamespace: string) (file: string) : string =
        let file = normal file
        let dir, name = match file.LastIndexOf '/' with -1 -> "", file | i -> file.Substring (0, i), file.Substring (i + 1)
        let leaf =
            if name.EndsWith (".resx", System.StringComparison.OrdinalIgnoreCase) then Path.GetFileNameWithoutExtension name + ".resources"
            else name
        [ rootNamespace; (if dir = "" then "" else everett dir); leaf ] |> List.filter ((<>) "") |> String.concat "."

    /// The root namespace of a description as msbuild defaults it: `rootNamespace`, else the
    /// project name with ` ` -> `_`.
    let rootNamespace (d: Description.Project) : string =
        d.RootNamespace |> Option.defaultValue (d.Name.Replace (" ", "_"))

    /// The culture of a culture-specific resource (`Strings.de.resx`, `Logo.fr-FR.png`): the
    /// second extension when it names a culture this machine knows. Such a resource belongs in
    /// a satellite assembly, which the composed path does not build.
    let cultureOf (file: string) : string option =
        let stem = Path.GetFileNameWithoutExtension (normal file)
        match Path.GetExtension stem with
        | "" -> None
        | ext ->
            let name = ext.Substring 1
            let known =
                try
                    System.Globalization.CultureInfo.GetCultures System.Globalization.CultureTypes.AllCultures
                    |> Array.exists (fun c -> c.Name <> "" && same c.Name name)
                with _ -> false
            if known then Some name else None

    /// <summary>
    /// The folder key of a described compilation's generated files (`obj/xake/&lt;key&gt;/&lt;framework&gt;`
    /// under the build's project root): the assembly name when the description is the project's
    /// own (beside the project file), else the description's folder relative to the project
    /// root: two descriptions of one project (brands) then never share generated sources. The
    /// assembly name again for a description outside the project root.
    /// </summary>
    let generatedKey (root: string) (descriptionPath: string) (projectDir: string) (assemblyName: string) : string =
        let comparison = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
        let full (p: string) = (fullOf root p).TrimEnd '/'
        let descriptionDir = full (Path.GetDirectoryName (full descriptionPath))
        if System.String.Equals (descriptionDir, full projectDir, comparison) then assemblyName
        else
            let r = full root
            if descriptionDir.StartsWith (r + "/", comparison) then descriptionDir.Substring (r.Length + 1) else assemblyName

    let private hasWildcard (s: string) = s.Contains "*" || s.Contains "?"

    /// <summary>
    /// The files a description's `sources` name, in order, through `getFiles` (so an added
    /// file reruns the target): an entry is an include, a `!` prefix makes it an exclude of what
    /// the entries before it included (msbuild's `Remove`), paths relative to `projectDir`.
    /// Files come in the listed order when no entry has a wildcard (F#'s compile order); with
    /// a wildcard they are sorted (ordinal), as the C# import's masks expect.
    /// </summary>
    let expandSources (projectDir: string) (entries: string list) : Recipe<ExecContext, string list> =
        recipe {
            match entries |> List.filter (fun e -> not (e.StartsWith "!")) with
            | [] -> return []
            | _ ->
                let fs =
                    entries |> List.fold (fun (fs: Fileset) (entry: string) ->
                        if entry.StartsWith "!" then fs - normal (entry.Substring 1) else fs + normal entry) (Fileset.(~+) projectDir)
                let! (Filelist found) = getFiles fs
                let files = found |> List.map (File.getFullName >> normal) |> List.distinct
                if entries |> List.exists hasWildcard then
                    return files |> List.sortWith (fun a b -> System.String.CompareOrdinal (a, b))
                else return files
        }

    /// The description's lists for one framework: the project-wide ones, then `byFramework`'s.
    let itemsFor (d: Description.Project) (framework: string) : Description.Items =
        let extra = d.ByFramework |> List.tryPick (fun (tfm, items) -> if same tfm framework then Some items else None)
        let pick f = extra |> Option.map f |> Option.defaultValue []
        { Defines = d.Defines @ pick (fun i -> i.Defines)
          References = d.References @ pick (fun i -> i.References)
          FrameworkReferences = d.FrameworkReferences @ pick (fun i -> i.FrameworkReferences)
          Packages = d.Packages @ pick (fun i -> i.Packages)
          Args = d.Options.Args @ pick (fun i -> i.Args)
          Resources = d.Resources @ pick (fun i -> i.Resources) }

    /// The framework of a described compilation: `requested` (the block's `targetfwk`) when
    /// given, else the description's only framework, else `NETFX-TARGET`; it must be one of the
    /// description's.
    let pickFramework (d: Description.Project) (requested: string) (netfxTarget: string option) : string =
        let pick (f: string) =
            match d.Frameworks |> List.tryFind (same f) with
            | Some f -> f
            | None -> failwithf "'%s': framework '%s' is not one of the description's (%s)" d.Name f (String.concat ", " d.Frameworks)
        match requested, d.Frameworks, netfxTarget with
        | f, _, _ when not (System.String.IsNullOrWhiteSpace f) -> pick (f.Trim ())
        | _, [ only ], _ -> only
        | _, _, Some f when f.Trim () <> "" -> pick (f.Trim ())
        | _ ->
            failwithf "'%s': the description targets %s; choose one with targetfwk (or the NETFX-TARGET variable)"
                d.Name (String.concat ", " d.Frameworks)

    /// A `references` entry that names a file rather than a framework assembly: it has a
    /// folder (`lib/Foo.dll`, `../bin/Bar.dll`) or is rooted.
    let private isPath (reference: string) =
        reference.Contains "/" || reference.Contains "\\" || Path.IsPathRooted reference

    /// <summary>
    /// The plan of the description at `path` (relative to the project root) for a `language`
    /// compilation of the assembly `assemblyName`: `requested` is the block's `targetfwk`
    /// (null or empty when none), `given` the files the block references (`ref`), which must
    /// include the output of every `projectRefs` entry. Loads the description as an input of
    /// the calling recipe. Fails, naming the description, on a language mismatch, a framework
    /// it does not list, `packages`, a `projectRefs` entry with no output given, an unknown
    /// shared framework and a culture-specific resource.
    /// </summary>
    let make (language: Description.Language) (path: string) (requested: string) (assemblyName: string) (given: File list)
             : Recipe<ExecContext, Plan> =
        recipe {
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let descriptionPath = fullOf root path
            let! d = Description.load descriptionPath
            let fs = language = Description.FSharp
            if d.Language <> language then
                failwithf "'%s': the description '%s' is %s, not for %s" d.Name path
                    (match d.Language with Description.FSharp -> "F#" | Description.CSharp -> "C#") (if fs then "fsc" else "csc")
            let projectDir = fullOf (Path.GetDirectoryName descriptionPath) d.Project |> Path.GetDirectoryName |> normal
            let inProject = fullOf projectDir

            let! netfxTarget = getVar "NETFX-TARGET"
            let framework = pickFramework d requested netfxTarget
            let items = itemsFor d framework

            // packages need a restore, which the composed path never runs. FSharp.Core is the
            // exception for F#: fsc composes its own (`FSHARP_CORE_VERSION`, Fsc.ofSettings)
            let packages = items.Packages |> List.filter (fun p -> not (fs && same p.Id "FSharp.Core"))
            if not (List.isEmpty packages) then
                failwithf "'%s': the description references packages (%s) for %s; the composed %s does not restore packages. Build it with Xake.Hermetic.Dotnet (Described.restore and `packages`, or a lock), or reference the assemblies by path in `references`"
                    d.Name (packages |> List.map (fun p -> p.Id + (p.Version |> Option.map ((+) " ") |> Option.defaultValue "")) |> String.concat ", ")
                    framework (if fs then "fsc" else "csc")

            // a projectRef's output path is the script's layout: it comes with `ref`
            let missing =
                d.ProjectRefs |> List.filter (fun name ->
                    not (given |> List.exists (fun f -> same (Path.GetFileNameWithoutExtension f.FullName) name)))
            if not (List.isEmpty missing) then
                failwithf "'%s': projectRefs %s have no output path; pass each one's output with `ref` (e.g. ref !! \"out/%s/%s.dll\") -- the composed path neither builds nor locates project references (the hermetic lock records them)"
                    d.Name (missing |> List.map (sprintf "'%s'") |> String.concat ", ") framework missing.Head

            for f in items.FrameworkReferences do
                if not (DotNetFwk.sharedFrameworks |> List.exists (same f)) then
                    failwithf "'%s': frameworkReferences '%s' is not one of %s" d.Name f (String.concat ", " DotNetFwk.sharedFrameworks)

            let key = generatedKey root descriptionPath projectDir assemblyName
            let generatedDir = normal (Path.Combine (Array.append [| root; "obj"; "xake" |] (Array.append (key.Split '/') [| framework |])))
            let switch (name: string) value = if fs then sprintf "--%s:%s" name value else sprintf "/%s:%s" name value

            // embedded resources: the manifest name as given, else msbuild's default
            // (`manifestName`); a `.resx` is embedded as the `.resources` it compiles to (a build
            // product under the generated folder, compiled by the runner)
            for r in items.Resources do
                match cultureOf r.File with
                | Some culture ->
                    failwithf "'%s': resource '%s' is culture-specific (%s): satellite assemblies are not built by the composed path; leave it out of the description or build the satellite separately"
                        d.Name r.File culture
                | None -> ()
            let resources =
                items.Resources |> List.map (fun r ->
                    let name = r.Name |> Option.defaultValue (manifestName (rootNamespace d) r.File)
                    if r.File.EndsWith (".resx", System.StringComparison.OrdinalIgnoreCase) then
                        let compiled = generatedDir + "/" + name
                        Some (inProject r.File, compiled), switch "resource" (compiled + "," + name)
                    else None, switch "resource" (inProject r.File + "," + name))

            let o = d.Options
            let nowarn = sdkNoWarn language @ o.NoWarn |> List.distinct
            let args =
                [ (if fs then "--deterministic+" else "/deterministic+") ]
                @ (o.LangVersion |> Option.map (switch "langversion") |> Option.toList)
                @ (if o.WarningsAsErrors then [ (if fs then "--warnaserror+" else "/warnaserror+") ] else [])
                @ (if List.isEmpty nowarn then [] else [ switch "nowarn" (String.concat "," nowarn) ])
                @ (match o.Nullable with
                   | Some n when not fs -> [ switch "nullable" n ]
                   | Some "enable" -> [ "--checknulls+" ]
                   | _ -> [])
                @ (o.KeyFile |> Option.map (inProject >> switch "keyfile") |> Option.toList)
                @ (resources |> List.map snd)
                @ items.Args

            // one generated source per compile: the friends, then the other assembly
            // attributes, in the description's order, LF line ends
            let generated =
                match d.InternalsVisibleTo, d.AssemblyAttributes with
                | [], [] -> None
                | friends, others ->
                    let line (text: string) = if fs then sprintf "[<assembly: %s>]" text else sprintf "[assembly: %s]" text
                    let friend (f: Description.Friend) =
                        let value = match f.Key with Some key -> sprintf "%s, PublicKey=%s" f.Name key | None -> f.Name
                        line (sprintf "System.Runtime.CompilerServices.InternalsVisibleTo(\"%s\")" value)
                    let other (a: Description.Attribute) = line (sprintf "%s(%s)" a.Type (String.concat ", " a.Args))
                    let kind = if List.isEmpty others then ".InternalsVisibleTo" else ".AssemblyAttributes"
                    let file = generatedDir + "/" + assemblyName + kind + (if fs then ".fs" else ".cs")
                    let body =
                        [ yield "// <auto-generated/> by Xake from the project description"
                          if fs then yield "namespace FSharp"; yield ""
                          yield! friends |> List.map friend
                          yield! others |> List.map other
                          if fs then yield "do ()"
                          yield "" ]
                    Some (file, String.concat "\n" body)

            // the shared frameworks' reference packs (Microsoft.NETCore.App is the target
            // framework's own, which the composition references anyway)
            let shared = ResizeArray<string> ()
            for f in items.FrameworkReferences |> List.filter (same "Microsoft.NETCore.App" >> not) |> List.distinctBy (fun f -> f.ToLowerInvariant ()) do
                let! dlls = DotNetFwk.sharedFrameworkReferences f framework
                shared.AddRange dlls

            let defines = frameworkDefines framework
            let byName, byPath = items.References |> List.partition (isPath >> not)
            let globalRefs =
                (if List.contains "NETFRAMEWORK" defines then implicitFrameworkReferences
                 // composed csc references only mscorlib for netstandard (fsc adds netstandard.dll itself)
                 elif not fs && List.contains "NETSTANDARD" defines then [ "netstandard" ]
                 else []) @ byName
                |> List.map (fun r -> if r.EndsWith (".dll", System.StringComparison.OrdinalIgnoreCase) then r else r + ".dll")
                // an assembly name is case-insensitive (`WindowsBase`, `windowsbase`): the first spelling
                |> List.distinctBy (fun r -> r.ToLowerInvariant ())

            let! sources = expandSources projectDir d.Sources

            return
                { Description = d
                  Path = descriptionPath
                  Framework = framework
                  ProjectDir = projectDir
                  Defines = items.Defines |> List.distinct
                  FrameworkDefines = defines
                  Args = args
                  GlobalRefs = globalRefs
                  References = (byPath |> List.map inProject) @ List.ofSeq shared |> List.distinct
                  Sources = sources
                  Kind = d.Kind
                  Doc = o.Doc
                  Generated = generated
                  Resx = resources |> List.choose fst }
        }
