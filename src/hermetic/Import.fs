namespace Xake.Hermetic.Dotnet

open System
open System.IO
open System.Xml.Linq

open Xake
open Xake.Tasks
open Xake.Dotnet

/// Imports a project file (csproj, fsproj) into its description (`xake.project.json`,
/// `Description`): msbuild evaluates the project under the caller's global properties, once
/// for the project and once per target framework, and the description is the projection of
/// what it reports. No targets run and nothing is restored. The description is written only
/// when its content changes. Page: `docs/hermetic/description.md` "Import".
module Import =

    /// What importing one project gave.
    type Imported =
        { /// the project file, relative to the build's project root
          Project: string
          /// the description file, relative to the build's project root
          Description: string
          /// whether the description file was (re)written
          Written: bool
          /// the msbuild files the evaluation read, relative to the project root, sorted: the
          /// project, `Directory.Build.props`/`.targets` and whatever else it imports, except
          /// the SDK's own files, package files and restore's output under `obj/`
          Inputs: string list
          /// what msbuild knows that the description does not carry (no schema field for it),
          /// one line each, e.g. `Xake.fsproj: property PackageIconUrl`
          Dropped: string list
          /// the locks written (`record`), relative to the project root; empty for `project`
          Locks: string list }

    let private properties =
        [ "MSBuildProjectName"; "MSBuildProjectExtension"; "MSBuildProjectDirectory"; "TargetFrameworks"; "TargetFramework"
          "AssemblyName"; "RootNamespace"; "OutputType"; "Configuration"; "DefineConstants"; "LangVersion"; "Nullable"
          "TreatWarningsAsErrors"; "NoWarn"; "GenerateDocumentationFile"; "SignAssembly"; "AssemblyOriginatorKeyFile"; "OtherFlags"
          "EnableDefaultItems"; "EnableDefaultCompileItems"; "DefaultItemExcludes"; "DefaultExcludesInProjectFolder"
          "IsPackable"; "PackageId"; "Description"; "Authors"; "Copyright"; "PackageTags"; "PackageLicenseExpression"
          "PackageProjectUrl"; "RepositoryType"; "RepositoryUrl"; "PackageIcon"; "PackageReadmeFile"
          "NetCoreRoot"; "MSBuildExtensionsPath"; "MSBuildSDKsPath"; "MSBuildBinPath"; "NuGetPackageRoot"; "BaseIntermediateOutputPath"
          // the repository's own (`record` imports it from its targets file)
          "CustomAfterMicrosoftCommonTargets" ]
    let private itemTypes =
        [ "Compile"; "Reference"; "PackageReference"; "PackageVersion"; "ProjectReference"; "AssemblyAttribute"
          "InternalsVisibleTo"; "EmbeddedResource"; "None"; "Content" ]
    /// What a project file may set that the description carries (or that only shapes what it
    /// carries); any other property or item type in a repository file is reported as dropped.
    let private carried = set (properties @ itemTypes @ [ "DisableImplicitFSharpCoreReference"; "ManagePackageVersionsCentrally" ])

    /// One evaluation's result dump: properties, and items as (identity, metadata).
    type internal Dump = { Props: Map<string, string>; Items: Map<string, (string * Map<string, string>) list> }

    let internal readDump (file: string) =
        let root = File.ReadAllText file |> Json.parse
        let strings = function
            | Json.JObject ms -> ms |> List.choose (fun (k, v) -> Json.asString v |> Option.map (fun s -> k, s)) |> Map.ofList
            | _ -> Map.empty
        { Props = Json.field "Properties" root |> Option.map strings |> Option.defaultValue Map.empty
          Items =
            match Json.field "Items" root with
            | Some (Json.JObject ms) ->
                ms |> List.map (fun (k, v) ->
                    k, Json.asArray v |> List.map (fun i ->
                        let m = strings i
                        (m |> Map.tryPick (fun k v -> if k.Equals ("Identity", StringComparison.OrdinalIgnoreCase) then Some v else None)
                           |> Option.defaultValue ""), m))
                |> Map.ofList
            | _ -> Map.empty }

    let private prop (d: Dump) name = d.Props |> Map.tryFind name |> Option.defaultValue ""
    let private items (d: Dump) name = d.Items |> Map.tryFind name |> Option.defaultValue []
    /// Item metadata by name, case-insensitively as msbuild reads it (`Version`, `version`).
    let private meta (name: string) (m: Map<string, string>) =
        match Map.tryFind name m with
        | Some v -> v
        | None -> m |> Map.tryPick (fun k v -> if k.Equals (name, StringComparison.OrdinalIgnoreCase) then Some v else None) |> Option.defaultValue ""
    let private isTrue (s: string) = s.Trim().Equals ("true", StringComparison.OrdinalIgnoreCase)
    let private slash (p: string) = p.Replace ('\\', '/')
    let private split (s: string) = s.Split ';' |> Array.map (fun s -> s.Trim ()) |> Array.filter ((<>) "") |> List.ofArray
    let private comparison = if Env.isUnix then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase

    /// The metadata msbuild gives every item; the rest of an `AssemblyAttribute`'s is its arguments.
    let private wellKnown =
        set [ "identity"; "fullpath"; "rootdir"; "filename"; "extension"; "relativedir"; "directory"; "recursivedir"
              "modifiedtime"; "createdtime"; "accessedtime"; "definingprojectfullpath"; "definingprojectdirectory"
              "definingprojectname"; "definingprojectextension" ]

    /// `value` as a source literal of type `t` (`None`: not representable, or not of that type).
    let private literalOf (fs: bool) (t: Type) (value: string) : string option =
        let inv = Globalization.CultureInfo.InvariantCulture
        let v = value.Trim ()
        // a quote or backslash: a verbatim string (C# and F#), so the text carries no `\` escape
        // (a lock's writer turns every `\` of the generated source into `/`)
        if t = typeof<string> || t = typeof<obj> then
            if value.Contains "\"" || value.Contains "\\" then Some ("@\"" + value.Replace ("\"", "\"\"") + "\"")
            else Some ("\"" + value + "\"")
        elif t = typeof<bool> then
            match Boolean.TryParse v with
            | true, b -> Some (if b then "true" else "false")
            | _ -> None
        elif t.IsEnum then
            if Enum.GetNames t |> Array.contains v then Some (t.FullName.Replace ('+', '.') + "." + v) else None
        elif t = typeof<Type> then
            if v = "" then None elif fs then Some (sprintf "typeof<%s>" v) else Some (sprintf "typeof(%s)" v)
        elif t.IsPrimitive && t <> typeof<char> && t <> typeof<nativeint> && t <> typeof<unativeint> then
            try Convert.ChangeType (v, t, inv) |> ignore; Some v with _ -> None
        else None

    /// <summary>
    /// The arguments of an `AssemblyAttribute` item as source text, the way msbuild's
    /// `WriteCodeFragment` writes them: `_Parameter1`, `_Parameter2`... positional, any other
    /// metadata named (`Name = value`). `_ParameterN_IsLiteral=true` is verbatim and
    /// `_ParameterN_TypeName` names the type; otherwise the types come from the first
    /// constructor of the attribute type (when this process knows it, e.g.
    /// `System.CLSCompliantAttribute`) whose parameters take every value, and are strings when
    /// none does or the type is unknown. `None` (with a `dropped` line) for what it cannot write.
    /// </summary>
    let internal attributeArgs (fs: bool) (attribute: string) (m: Map<string, string>) (dropped: string -> unit) : string list option =
        let key (k: string) = k.ToLowerInvariant ()
        let positional =
            m |> Map.toList |> List.choose (fun (k, v) ->
                let k = key k
                if k.StartsWith "_parameter" then
                    match Int32.TryParse (k.Substring 10) with
                    | true, n -> Some (n, v)
                    | _ -> None
                else None)
            |> List.sortBy fst
        let named =
            m |> Map.toList |> List.filter (fun (k, _) -> not (wellKnown.Contains (key k)) && not ((key k).StartsWith "_"))
        let side n suffix = meta (sprintf "_Parameter%d_%s" n suffix) m
        let attributeType =
            match Type.GetType attribute with
            | null -> typeof<obj>.Assembly.GetType attribute |> Option.ofObj
            | t -> Some t
        if positional |> List.mapi (fun i (n, _) -> n <> i + 1) |> List.exists id then
            dropped (sprintf "assembly attribute %s: its _ParameterN are not numbered 1, 2, ..." attribute); None
        else
        let explicitType n =
            match side n "TypeName" with
            | "" -> None
            | name -> Some (match Type.GetType name with null -> typeof<obj>.Assembly.GetType name | t -> t)
        let inferred : Type list option =
            attributeType |> Option.bind (fun t ->
                t.GetConstructors () |> Array.tryPick (fun c ->
                    let ps = c.GetParameters ()
                    let fits =
                        ps.Length = positional.Length
                        && Array.forall2 (fun (p: Reflection.ParameterInfo) (n, v) ->
                               isTrue (side n "IsLiteral") || (literalOf fs p.ParameterType v).IsSome) ps (Array.ofList positional)
                    if fits then Some (ps |> Array.map (fun p -> p.ParameterType) |> List.ofArray) else None))
        let positionalArgs =
            positional |> List.mapi (fun i (n, v) ->
                if isTrue (side n "IsLiteral") then Some v
                else
                    let t =
                        match side n "TypeName", explicitType n with
                        | "", _ -> inferred |> Option.map (fun ts -> ts.[i]) |> Option.defaultValue typeof<string>
                        | _, Some (null) | _, None -> null
                        | _, Some t -> t
                    match t with
                    | null -> dropped (sprintf "assembly attribute %s: _Parameter%d_TypeName=%s is not a type this import knows" attribute n (side n "TypeName")); None
                    | t ->
                        match literalOf fs t v with
                        | Some l -> Some l
                        | None -> dropped (sprintf "assembly attribute %s: _Parameter%d '%s' is not a %s" attribute n v t.FullName); None)
        let namedArgs =
            named |> List.map (fun (k, v) ->
                let t =
                    attributeType |> Option.bind (fun at -> at.GetProperty k |> Option.ofObj |> Option.map (fun p -> p.PropertyType))
                    |> Option.defaultValue typeof<string>
                match literalOf fs t v with
                | Some l -> Some (sprintf "%s = %s" k l)
                | None -> dropped (sprintf "assembly attribute %s: %s '%s' is not a %s" attribute k v t.FullName); None)
        let all = positionalArgs @ namedArgs
        if all |> List.forall Option.isSome then Some (List.choose id all) else None

    /// `path` relative to the folder `dir` (both absolute), with `/`; `..` where it leaves it.
    let internal relativeTo (dir: string) (path: string) =
        let parts (p: string) = (slash (Path.GetFullPath p)).TrimEnd('/').Split '/' |> List.ofArray
        let rec common a b =
            match a, b with
            | x :: xs, y :: ys when String.Equals (x, y, comparison) -> common xs ys
            | _ -> a, b
        match common (parts dir) (parts path) with
        | up, _ when List.length up = List.length (parts dir) -> String.concat "/" (parts path)   // another volume
        | up, rest -> List.replicate up.Length ".." @ rest |> String.concat "/"

    /// The C# sources as Xake masks: msbuild's default glob and its excludes (those that can
    /// match a `.cs` file), then the `Compile` elements of the repository files in import order;
    /// a `!` marks an exclude, applied in order. msbuild's `dir/**` (every file below) is
    /// written as Xake's `dir/**/*`.
    let internal csharpMasks (d: Dump) (xmlFiles: (string * XDocument) list) (dropped: string -> unit) =
        let projectDir = slash (prop d "MSBuildProjectDirectory")
        // the folder of a repository file, from its base URI (`XDocument.Load` with `SetBaseUri`)
        let fileDir (doc: XDocument) =
            match doc.BaseUri with
            | null | "" -> None
            | uri -> Some (slash (Path.GetDirectoryName (Uri(uri).LocalPath)))
        // a mask with `$(Name)`: the properties msbuild knows here substituted (the file's own
        // `MSBuildThisFile*`, the evaluated ones); `None` when one is unknown
        let substitute (doc: XDocument) (mask: string) =
            let unknown = ref false
            let value =
                Text.RegularExpressions.Regex.Replace (mask, @"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)", fun m ->
                    let name = m.Groups.[1].Value
                    match name.ToLowerInvariant (), fileDir doc with
                    | "msbuildthisfiledirectory", Some dir -> dir + "/"
                    | "msbuildthisfiledirectorynoroot", Some dir -> dir.TrimStart('/') + "/"
                    | "msbuildprojectdirectory", _ -> projectDir
                    | _ ->
                        match d.Props |> Map.tryPick (fun k v -> if k.Equals (name, StringComparison.OrdinalIgnoreCase) then Some v else None) with
                        | Some v -> v
                        | None -> unknown.Value <- true; m.Value)
            if unknown.Value then None
            else
                // relative to the project's folder, as every other mask
                let v = slash value
                let v = if Path.IsPathRooted v then relativeTo projectDir v else v
                Some (v.Replace ("//", "/"))
        // the evaluated `Compile` items a repository file defines, relative to the project's folder
        let definedIn (doc: XDocument) =
            match doc.BaseUri with
            | null | "" -> []
            | uri ->
                let path = slash (Uri(uri).LocalPath)
                [ for id, m in items d "Compile" do
                    if String.Equals (slash (meta "DefiningProjectFullPath" m), path, comparison) then
                        let full = if Path.IsPathRooted id then id else Path.Combine (projectDir, id)
                        yield relativeTo projectDir full ]
        // the files the other `Compile Include`s of a repository file name (literal masks and
        // the ones `substitute` resolves), full paths: what the items evaluated from that file
        // hold besides the include with an unknown property
        let coveredIn (doc: XDocument) =
            let masks =
                [ for e in doc.Descendants () do
                    if e.Name.LocalName = "Compile" && not (e.Ancestors () |> Seq.exists (fun a -> a.Name.LocalName = "Target")) then
                        match e.Attribute (XName.Get "Include") with
                        | null -> ()
                        | a ->
                            for mask in split a.Value do
                                if mask.Contains "@(" || mask.Contains "%(" then ()
                                elif mask.Contains "$(" then match substitute doc mask with Some m -> yield m | None -> ()
                                else yield slash mask ]
            if masks.IsEmpty then Set.empty
            else
                let elements = masks |> List.map (fun m -> Includes (Path.parse (if m.EndsWith "**" then m + "/*" else m)))
                let (Filelist files) = Fileset ({ Xake.Fileset.DefaultOptions with BaseDir = Some projectDir }, elements) |> Fileset.toFileList projectDir
                files |> List.map (fun f -> slash f.FullName) |> Set.ofList
        let defaults =
            if isTrue (prop d "EnableDefaultItems") && isTrue (prop d "EnableDefaultCompileItems") then
                let excludes =
                    split (prop d "DefaultItemExcludes" + ";" + prop d "DefaultExcludesInProjectFolder")
                    |> List.map (fun e -> (slash e).Replace ("//", "/"))
                    |> List.filter (fun e -> e.EndsWith "**" || e.EndsWith ".cs" || e.EndsWith "*")
                    |> List.distinct
                // `bin/Debug/**` adds nothing to `bin/**`
                let subsumed (e: string) =
                    excludes |> List.exists (fun o -> o <> e && o.EndsWith "/**" && e.StartsWith (o.Substring (0, o.Length - 2)))
                "**/*.cs" :: (excludes |> List.filter (subsumed >> not) |> List.map ((+) "!"))
            else []
        let expandedDocs = Collections.Generic.HashSet<XDocument> (HashIdentity.Reference)
        let explicit =
            [ for file, doc in xmlFiles do
                for e in doc.Descendants () do
                    if e.Name.LocalName = "Compile" && not (e.Ancestors () |> Seq.exists (fun a -> a.Name.LocalName = "Target")) then
                        let attr n = match e.Attribute (XName.Get n) with null -> "" | a -> a.Value
                        if attr "Condition" <> "" || (e.Parent <> null && e.Parent.Attribute (XName.Get "Condition") <> null) then
                            dropped (sprintf "%s: the condition of a Compile item (its masks are kept unconditionally)" file)
                        for name, prefix in [ "Include", ""; "Exclude", "!"; "Remove", "!" ] do
                            for mask in split (attr name) do
                                if mask.Contains "@(" || mask.Contains "%(" then
                                    dropped (sprintf "%s: Compile %s=\"%s\" (not a plain mask)" file name mask)
                                elif mask.Contains "$(" then
                                    // the evaluated path (`$(BuildTmpPath)\VersionInfo.cs`, a shared
                                    // project's `$(MSBuildThisFileDirectory)X.cs`)
                                    match substitute doc mask with
                                    | Some m -> yield prefix + m
                                    | None when prefix = "" ->
                                        // a property the evaluation does not report: the items
                                        // msbuild evaluated from this file, by name, but those
                                        // the file's other includes name (they are at their
                                        // own place already: msbuild lists each item once)
                                        let covered = coveredIn doc
                                        let full (f: string) = slash (Path.GetFullPath (Path.Combine (projectDir, f)))
                                        match definedIn doc |> List.filter (fun f -> not (covered.Contains (full f))) with
                                        | _ when not (expandedDocs.Add doc) -> ()
                                        | [] -> dropped (sprintf "%s: Compile %s=\"%s\" (a property the import does not know)" file name mask)
                                        | fs -> yield! fs
                                    | None -> dropped (sprintf "%s: Compile %s=\"%s\" (a property the import does not know)" file name mask)
                                else yield prefix + slash mask ]
        // msbuild's `dir/**` is every file below; Xake's is the directories, files are `dir/**/*`
        let xake (m: string) = if m.EndsWith "**" then m + "/*" else m
        defaults @ explicit |> List.map xake

    /// Expands masks as `csharpMasks` writes them, against `dir`, in order.
    let internal expandMasks (dir: string) (masks: string list) =
        let elements =
            masks |> List.map (fun m -> if m.StartsWith "!" then Excludes (Path.parse (m.Substring 1)) else Includes (Path.parse m))
        let (Filelist files) = Fileset ({ Xake.Fileset.DefaultOptions with BaseDir = Some dir }, elements) |> Fileset.toFileList dir
        files |> List.map (fun f -> slash f.FullName)

    /// The description of one project from its evaluations: `outer` (no `TargetFramework`) and
    /// one per framework, in `frameworks` order. Pure but for `expandMasks` reading the folder.
    /// `targets` are the design-time builds of the frameworks (`record`), when there are: the
    /// embedded resources then take the names and cultures msbuild gave them
    /// (`ManifestResourceName`, `LogicalName`, `WithCulture`), per framework.
    let internal describe (descriptionDir: string) (outer: Dump) (inner: (string * Dump) list)
                          (isSdkFile: string -> bool) (xmlFiles: (string * XDocument) list) (previous: Description.Project option)
                          (targets: (string * Dump) list) =
        let dropped = ResizeArray<string> ()
        let drop s = if not (dropped.Contains s) then dropped.Add s
        let p = prop outer
        let name = p "MSBuildProjectName"
        let projectDir = slash (p "MSBuildProjectDirectory")
        let language =
            match (p "MSBuildProjectExtension").ToLowerInvariant () with
            | ".fsproj" -> Description.FSharp
            | ".csproj" -> Description.CSharp
            | ext -> failwithf "'%s': %s is neither an fsproj nor a csproj" name ext
        let inRepo (m: Map<string, string>) = not (isTrue (meta "IsImplicitlyDefined" m)) && not (isSdkFile (meta "DefiningProjectFullPath" m))
        let relative (path: string) =
            let full = if Path.IsPathRooted path then path else Path.Combine (projectDir, path)
            relativeTo projectDir full
        let first = snd (List.head inner)
        // the properties a repository file sets; a value the SDK defaults (LangVersion by the
        // framework, Authors to the assembly name, ...) is not the project's
        let declared =
            set [ for _, doc in xmlFiles do
                    for g in doc.Root.Descendants () do
                        if g.Name.LocalName = "PropertyGroup" then
                            for c in g.Elements () -> c.Name.LocalName ]
        let ifDeclared name value = if declared.Contains name then value else None

        // ---- per framework, then split into what every framework has and the rest
        let definesOf (d: Dump) =
            let sdk = set [ "TRACE"; (prop d "Configuration").ToUpperInvariant () ]
            split (prop d "DefineConstants") |> List.filter (sdk.Contains >> not) |> List.distinct
        let referencesOf (d: Dump) =
            items d "Reference" |> List.filter (snd >> inRepo) |> List.map (fun (id, m) ->
                if meta "HintPath" m <> "" then drop (sprintf "%s: Reference %s HintPath=%s (references are by name)" name id (meta "HintPath" m))
                id)
        let packagesOf (tfm: string) (d: Dump) =
            let central = items d "PackageVersion" |> List.map (fun (id, m) -> id, meta "Version" m) |> Map.ofList
            [ for id, m in items d "PackageReference" do
                if inRepo m then
                    let version =
                        [ meta "VersionOverride" m; meta "Version" m; central |> Map.tryFind id |> Option.defaultValue "" ]
                        |> List.tryFind ((<>) "")
                    for k in [ "IncludeAssets"; "ExcludeAssets"; "Aliases" ] do
                        if meta k m <> "" then drop (sprintf "%s: PackageReference %s %s=%s" name id k (meta k m))
                    yield ({ Id = id; Version = version
                             Private = (meta "PrivateAssets" m).Split ';' |> Array.exists (fun a -> a.Trim().Equals ("all", StringComparison.OrdinalIgnoreCase)) }
                           : Description.Package)
                else drop (sprintf "%s: implicit package %s %s (%s), added by the SDK" name id (meta "Version" m) tfm) ]
        // ---- embedded resources, per framework: with the design-time builds (`targets`) as
        // msbuild named them, else from the evaluation
        let rootNs = match p "RootNamespace" with "" -> name.Replace (" ", "_") | r -> r
        let cultures =
            lazy (Globalization.CultureInfo.GetCultures Globalization.CultureTypes.AllCultures
                  |> Array.map (fun c -> c.Name) |> Array.filter ((<>) "") |> set)
        let resourcesOf (tfm: string) (evaluated: Dump) =
            let fromTargets = targets |> List.tryFind (fst >> (=) tfm) |> Option.map snd
            let d = defaultArg fromTargets evaluated
            items d "EmbeddedResource" |> List.distinctBy fst |> List.choose (fun (id, m) ->
                // a shared project's (`.projitems`) item is an absolute path: relative to the project
                let file = relative (slash id)
                let isResx = file.EndsWith (".resx", StringComparison.OrdinalIgnoreCase)
                let byDefault = Described.manifestName rootNs file
                match fromTargets with
                | Some _ ->
                    if isTrue (meta "WithCulture" m) then
                        drop (sprintf "%s: EmbeddedResource %s (culture %s: a satellite assembly, not built)" name file (meta "Culture" m)); None
                    else
                        // csc embeds a resx as the `.resources` it compiles to, named
                        // `<ManifestResourceName>.resources`; any other file by its `LogicalName`
                        let given =
                            match meta "LogicalName" m, meta "ManifestResourceName" m with
                            | "", "" -> byDefault
                            | "", manifest -> if isResx then manifest + ".resources" else manifest
                            | logical, _ -> logical
                        Some ({ File = file; Name = if given = byDefault then None else Some given } : Description.Resource)
                | None ->
                    // the evaluation alone (`project`, the composed route): what the project
                    // states. A culture is the suffix of the file name, as msbuild's
                    // `AssignCulture` sees it; a satellite assembly is not built
                    let culture =
                        let n = Path.GetFileNameWithoutExtension file
                        match n.LastIndexOf '.' with
                        | -1 -> None
                        | i -> let c = n.Substring (i + 1) in if cultures.Value.Contains c then Some c else None
                    // the manifest name msbuild gives it, when it is not the default of the file
                    // path: `LogicalName` as is; `ManifestResourceName` on a resx + `.resources`
                    // (what GenerateResource writes); on another file msbuild then sets no
                    // `LogicalName` and csc names it after the file; a `Link`, whose path stands
                    // for the file's in the default name (`CreateCSharpManifestResourceName`)
                    let logical =
                        match meta "LogicalName" m, meta "ManifestResourceName" m, slash (meta "Link" m) with
                        | "", "", "" -> ""
                        | "", "", link -> Described.manifestName rootNs link
                        | "", manifest, _ -> if isResx then manifest + ".resources" else Path.GetFileName file
                        | logical, _, _ -> logical
                    let dependentUpon = meta "DependentUpon" m
                    match culture with
                    | Some c when meta "WithCulture" m <> "false" ->
                        drop (sprintf "%s: EmbeddedResource %s (culture %s: a satellite assembly, not built)" name file c); None
                    | _ ->
                        // msbuild names a resx after the class of its DependentUpon source (by
                        // convention the `.cs` of the same name beside it); the evaluation does not
                        // tell which -- `record` takes the name msbuild gives
                        let besideSource =
                            isResx && language = Description.CSharp
                            && File.Exists (Path.Combine (projectDir, Path.ChangeExtension (file, ".cs")))
                        if logical = "" && (dependentUpon <> "" || besideSource) then
                            drop (sprintf "%s: EmbeddedResource %s is named after the class of %s (DependentUpon); the description takes the default name, set its `name`"
                                      name file (if dependentUpon <> "" then dependentUpon else Path.ChangeExtension (Path.GetFileName file, ".cs")))
                        Some ({ File = file; Name = match logical with "" -> None | n -> Some n } : Description.Resource))
        let perFramework = inner |> List.map (fun (tfm, d) -> tfm, (definesOf d, referencesOf d, packagesOf tfm d, resourcesOf tfm d))
        let common pick = perFramework |> List.map (snd >> pick) |> List.reduce (fun a b -> a |> List.filter (fun x -> List.contains x b))
        let defines, references, packages = common (fun (d, _, _, _) -> d), common (fun (_, r, _, _) -> r), common (fun (_, _, p, _) -> p)
        let resources = common (fun (_, _, _, r) -> r)
        let byFramework =
            if inner.Length < 2 then []
            else
                perFramework |> List.choose (fun (tfm, (d, r, p, rs)) ->
                    let extra all common = all |> List.filter (fun x -> not (List.contains x common))
                    let i : Description.Items =
                        { Defines = extra d defines; References = extra r references; Packages = extra p packages; Args = []
                          Resources = extra rs resources }
                    if i.Defines.IsEmpty && i.References.IsEmpty && i.Packages.IsEmpty && i.Resources.IsEmpty then None else Some (tfm, i))

        // ---- sources
        let sources =
            match language with
            | Description.FSharp ->
                let ofDump d = items d "Compile" |> List.map (fst >> slash)
                for tfm, d in List.tail inner do
                    if ofDump d <> ofDump first then drop (sprintf "%s: the Compile list for %s differs from %s's (only the first is kept)" name tfm (fst inner.Head))
                ofDump first
            | Description.CSharp ->
                let masks = csharpMasks first xmlFiles drop
                let expanded = expandMasks projectDir masks |> Set.ofList
                let msbuild = items first "Compile" |> List.map (fun (id, _) -> slash (Path.GetFullPath (Path.Combine (projectDir, id)))) |> Set.ofList
                for f in Set.difference msbuild expanded do drop (sprintf "%s: msbuild compiles %s, the masks miss it" name (relative f))
                for f in Set.difference expanded msbuild do drop (sprintf "%s: the masks take %s, msbuild does not compile it" name (relative f))
                masks

        // ---- friends, resources, other assembly attributes
        let friend (value: string) =
            match value.Split ([| ',' |], 2) with
            | [| n; key |] when key.Trim().StartsWith "PublicKey=" -> ({ Name = n.Trim (); Key = Some (key.Trim().Substring 10) } : Description.Friend)
            | _ -> ({ Name = value.Trim (); Key = None } : Description.Friend)
        let friends =
            [ for id, m in items first "AssemblyAttribute" do
                if id = "System.Runtime.CompilerServices.InternalsVisibleToAttribute" then yield friend (meta "_Parameter1" m)
              for id, m in items first "InternalsVisibleTo" do
                yield ({ Name = id; Key = match meta "Key" m with "" -> None | k -> Some k } : Description.Friend) ]
        let attributes =
            [ for id, m in items first "AssemblyAttribute" do
                if id <> "System.Runtime.CompilerServices.InternalsVisibleToAttribute" && inRepo m then
                    match attributeArgs (language = Description.FSharp) id m (fun s -> drop (sprintf "%s: %s" name s)) with
                    | Some args -> yield ({ Type = id; Args = args } : Description.Attribute)
                    | None -> () ]

        // ---- options
        let fp = prop first
        let options : Description.Options =
            { Doc = isTrue (fp "GenerateDocumentationFile")
              LangVersion = ifDeclared "LangVersion" (match fp "LangVersion" with "" -> None | v -> Some v)
              WarningsAsErrors = isTrue (fp "TreatWarningsAsErrors")
              // what the project adds to the SDK's defaults (`project` adds them back)
              NoWarn =
                let sdk = set (Described.sdkNoWarn language)
                (fp "NoWarn").Split ([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun w -> w.Trim ()) |> Array.filter (fun w -> w <> "" && not (sdk.Contains w))
                |> Array.distinct |> List.ofArray
              Nullable =
                match (fp "Nullable").ToLowerInvariant () with
                | "" -> None
                | "enable" | "disable" | "warnings" | "annotations" as n -> Some n
                | n -> drop (sprintf "%s: Nullable=%s" name n); None
              KeyFile = if isTrue (fp "SignAssembly") && fp "AssemblyOriginatorKeyFile" <> "" then Some (relative (fp "AssemblyOriginatorKeyFile")) else None
              Args = (fp "OtherFlags").Split ([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray }

        // ---- project references and the package
        let projectRefs = items first "ProjectReference" |> List.map (fun (id, m) -> Path.GetFileNameWithoutExtension (slash id), m)
        let assemblyName = p "AssemblyName"
        let packed (file: string) =
            // `PackageIcon`/`PackageReadmeFile` name the file inside the package; the project
            // file names it on disk, on a packed None/Content item
            let candidates =
                [ for t in [ "None"; "Content" ] do
                    for id, m in items outer t do
                        if isTrue (meta "Pack" m) then
                            let inPackage = ((slash (meta "PackagePath" m)).Trim '/' + "/" + Path.GetFileName (slash id)).TrimStart '/'
                            if inPackage = (slash file).TrimStart '/' then yield relative id ]
            match candidates with c :: _ -> Some c | [] -> Some file
        let package =
            if not (isTrue (p "IsPackable")) then None
            else
                let opt v = match v with "" -> None | v -> Some v
                let m : Description.Metadata =
                    { Id = p "PackageId"
                      Description = ifDeclared "Description" (opt (p "Description"))
                      Authors = ifDeclared "Authors" (opt (p "Authors"))
                      Copyright = opt (p "Copyright")
                      Tags = split (p "PackageTags")
                      License = opt (p "PackageLicenseExpression")
                      ProjectUrl = opt (p "PackageProjectUrl")
                      Repository =
                        match opt (p "RepositoryType"), opt (p "RepositoryUrl") with
                        | None, None -> None
                        | t, u -> Some ({ Type = t; Url = u } : Description.Repository)
                      Icon = opt (p "PackageIcon") |> Option.bind packed
                      Readme = opt (p "PackageReadmeFile") |> Option.bind packed
                      // a reference kept out of the package's dependencies goes into the package
                      Bundle = projectRefs |> List.filter (fun (_, m) -> (meta "PrivateAssets" m).Equals ("all", StringComparison.OrdinalIgnoreCase)) |> List.map fst }
                Some m

        // ---- the rest of what the repository files say
        for file, doc in xmlFiles do
            for e in doc.Root.Elements () do
                match e.Name.LocalName with
                | "PropertyGroup" | "ItemGroup" ->
                    for c in e.Elements () do
                        if not (carried.Contains c.Name.LocalName) then
                            drop (sprintf "%s: %s %s" file (if e.Name.LocalName = "PropertyGroup" then "property" else "item") c.Name.LocalName)
                | "Target" -> drop (sprintf "%s: target %s" file (match e.Attribute (XName.Get "Name") with null -> "" | a -> a.Value))
                | "Import" | "Sdk" -> ()
                | other -> drop (sprintf "%s: %s" file other)

        let project : Description.Project =
            { Schema = previous |> Option.bind (fun d -> d.Schema)
              Name = name
              Project = relativeTo descriptionDir (p "MSBuildProjectDirectory" </> name + p "MSBuildProjectExtension")
              AssemblyName = if assemblyName = name then None else Some assemblyName
              RootNamespace = match p "RootNamespace" with r when r = "" || r = name -> None | r -> Some r
              Language = language
              Kind = match (p "OutputType").ToLowerInvariant () with "exe" | "winexe" -> Description.Exe | _ -> Description.Library
              Frameworks = List.map fst inner
              Sources = sources
              Resources = resources
              Defines = defines
              References = references
              ProjectRefs = List.map fst projectRefs
              Packages = packages
              Options = options
              InternalsVisibleTo = friends
              AssemblyAttributes = attributes
              ByFramework = byFramework
              Package = package }
        project, List.ofSeq dropped

    // ---- running msbuild ------------------------------------------------------------------

    /// The recipes run concurrently (the msbuild runs among them are `throttled`), their
    /// results in order; the dependencies each records are the caller's.
    let private concurrently (recipes: Recipe<ExecContext, 'a> list) : Recipe<ExecContext, 'a list> =
        Recipe (fun (r, ctx) ->
            async {
                let! results = Async.Parallel (recipes |> List.map (fun (Recipe f) -> f (r, ctx)))
                let added =
                    results |> Seq.collect (fun (b, _) -> b.Depends |> Seq.filter (fun d -> not (List.contains d r.Depends)))
                    |> Seq.distinct |> List.ofSeq
                return { r with Depends = r.Depends @ added }, (results |> Array.map snd |> List.ofArray)
            })

    /// The recipes one after another, their results in order.
    let private sequentially (recipes: Recipe<ExecContext, 'a> list) : Recipe<ExecContext, 'a list> =
        recipes |> List.fold (fun (acc: Recipe<ExecContext, 'a list>) r ->
            recipe {
                let! xs = acc
                let! x = r
                return xs @ [ x ] }) (recipe { return [] })

    /// The msbuild processes an import runs at once, over every project and every run of each:
    /// the processor count.
    let private msbuildSlots = new Threading.SemaphoreSlim (Environment.ProcessorCount)

    /// `r` (an msbuild run) once one of the slots is free.
    let private throttled (r: Recipe<ExecContext, 'a>) : Recipe<ExecContext, 'a> =
        let (Recipe f) = r
        Recipe (fun state ->
            async {
                do! msbuildSlots.WaitAsync () |> Async.AwaitTask
                try return! f state
                finally msbuildSlots.Release () |> ignore
            })

    /// `dotnet msbuild <arguments>`, throttled; a non-zero exit fails the import
    /// (`Project.runMsbuild`): a description or a lock written from a failed run would be
    /// silently incomplete.
    let private msbuild (environment: (string * string) list) (arguments: string list) (projectFile: string) =
        throttled (Project.runMsbuild environment arguments projectFile)

    /// What msbuild's evaluation of one project gave: the project without a framework, each
    /// framework, the files it imported.
    type internal Evaluated = { Outer: Dump; Inner: (string * Dump) list; Imports: string list }

    /// The evaluations of `projectFile` (properties and items, and the preprocessed text for the
    /// files it imports): the project, then each of its frameworks, concurrently.
    let private evaluate (environment: (string * string) list) (projectFile: string) (switches: string list) (temp: string) =
        recipe {
            let one (framework: string option) =
                recipe {
                    let tf = match framework with Some f -> [ "-p:TargetFramework=" + f ] | None -> []
                    let tag = defaultArg framework "outer"
                    let dump, pp = temp </> tag + ".json", temp </> tag + ".pp"
                    do! msbuild environment ([ projectFile; "-nologo"; "-verbosity:quiet" ] @ switches @ tf
                                             @ [ "-getProperty:" + String.concat "," properties
                                                 "-getItem:" + String.concat "," itemTypes
                                                 "-getResultOutputFile:" + dump ]) projectFile
                    do! msbuild environment ([ projectFile; "-nologo" ] @ switches @ tf @ [ "-pp:" + pp ]) projectFile
                    return readDump dump, Project.parseImports (File.ReadAllText pp)
                }
            let! outer, outerImports = one None
            let declared =
                match split (prop outer "TargetFrameworks") with
                | [] -> split (prop outer "TargetFramework")
                | fs -> fs
            if declared.IsEmpty then failwithf "'%s': msbuild reports no TargetFramework(s)" projectFile
            let! inner =
                if prop outer "TargetFrameworks" = "" then recipe { return [ declared.Head, (outer, []) ] }
                else
                    recipe {
                        let! evaluated = declared |> List.map (Some >> one) |> concurrently
                        return List.zip declared evaluated
                    }
            return { Outer = outer; Inner = inner |> List.map (fun (f, (d, _)) -> f, d)
                     Imports = outerImports @ (inner |> List.collect (fun (_, (_, i)) -> i)) }
        }

    /// The repository's msbuild files among `imports` (not the SDK's, not a package's, not
    /// restore's output under `obj/`), and the test for an SDK or package file.
    let private repositoryFiles (outer: Dump) (packageRoot: string) (imports: string list) =
        let under (dirs: string list) (path: string) =
            let path = slash path
            dirs |> List.exists (fun d -> d <> "" && path.StartsWith ((slash d).TrimEnd '/' + "/", comparison))
        let sdkDirs = [ for n in [ "NetCoreRoot"; "MSBuildExtensionsPath"; "MSBuildSDKsPath"; "MSBuildBinPath" ] -> prop outer n ]
        let isSdkFile = under (sdkDirs @ [ prop outer "NuGetPackageRoot"; packageRoot ])
        let obj =
            let b = slash (prop outer "BaseIntermediateOutputPath")
            if b = "" then "" elif Path.IsPathRooted b then b else Path.Combine (prop outer "MSBuildProjectDirectory", b)
        let files = imports |> List.map slash |> List.distinct |> List.filter (fun f -> not (isSdkFile f) && not (under [ obj ] f))
        files, isSdkFile

    /// Writes the description of an evaluated project (`describe`; `targets`, the design-time
    /// builds of `record`, may be empty) when its content changes; the import's result and
    /// the description.
    let private writeDescription (root: string) (projectFile: string) (descriptionFile: string) (packageRoot: string)
                                 (ev: Evaluated) (targets: (string * Dump) list) =
        recipe {
            let repoFiles, isSdkFile = repositoryFiles ev.Outer packageRoot ev.Imports
            let xmlFiles = repoFiles |> List.map (fun f -> relativeTo root f, XDocument.Load (f, LoadOptions.SetBaseUri))
            let previous = if File.Exists descriptionFile then Some (Description.read descriptionFile) else None
            let description, dropped = describe (Path.GetDirectoryName descriptionFile) ev.Outer ev.Inner isSdkFile xmlFiles previous targets
            let written = Description.write descriptionFile description
            do! trace Info "%s '%s'" (if written then "wrote" else "unchanged:") (relativeTo root descriptionFile)
            for d in dropped do do! trace Info "  not in the description (%s): %s" (Path.GetFileName projectFile) d
            let imported : Imported =
                { Project = relativeTo root projectFile
                  Description = relativeTo root descriptionFile
                  Written = written
                  Inputs = repoFiles |> List.map (relativeTo root) |> List.sortWith (fun a b -> String.CompareOrdinal (a, b))
                  Dropped = dropped
                  Locks = [] }
            return imported, description, repoFiles
        }

    /// Imports `project` (a csproj or fsproj, relative to the project root or absolute) into
    /// the description at `descriptionOf project`, msbuild evaluating it under `globals` (e.g.
    /// `["Configuration", "Release"; "Brand", "MESCIUS"]`). The file is written only when its
    /// content changes; an existing file's `$schema` is kept. Records no input: an explicit
    /// command, not a rule (`Imported.Inputs` is the list of files msbuild read, for a
    /// fingerprint). Only the description: `record` also writes the locks.
    let project (project: string) (descriptionOf: string -> string) (globals: (string * string) list) : Recipe<ExecContext, Imported> =
        recipe {
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let full (p: string) = Path.GetFullPath (if Path.IsPathRooted p then p else root </> p)
            let projectFile = full project
            let descriptionFile = full (descriptionOf project)
            let! packageRoot = DotNetFwk.packageRoot ()
            let switches = [ for k, v in globals -> sprintf "-p:%s=%s" k v ]
            let temp = Path.Combine (Path.GetTempPath (), "xake-import-" + Guid.NewGuid().ToString "N")
            Directory.CreateDirectory temp |> ignore
            let! ev = evaluate [] projectFile switches temp
            Directory.Delete (temp, true)
            let! imported, _, _ = writeDescription root projectFile descriptionFile packageRoot ev []
            return imported
        }

    /// `project` for each of `projects`, concurrently (at most the processor count of msbuild
    /// processes at once); the results in order.
    let descriptions (projects: string list) (descriptionOf: string -> string) (globals: (string * string) list) : Recipe<ExecContext, Imported list> =
        projects |> List.map (fun p -> project p descriptionOf globals) |> concurrently

    // ---- recording msbuild's resolved compile inputs ----------------------------------------

    /// What `record` is told: the configurations to record, the properties msbuild runs with,
    /// the script's extra roots and its layout.
    type RecordOptions = {
        /// One lock per project and configuration, e.g. `["Release"]`
        Configurations: string list
        /// What else selects the compilation, e.g. `["Brand", "GCCN"]` (not `Configuration`)
        Properties: (string * string) list
        /// The script's extra roots (`Roots.make`): a path under one is written tokenized
        Roots: (string * string) list
        /// The description of a project file (`Description.beside`)
        DescriptionOf: string -> string
        /// The lock of a project (by its description's name) and configuration
        LockOf: string -> string -> string
        /// The output folder of a project for a configuration and framework (`outOf cfg fwk name`):
        /// where a project reference is found (`project:<name>`)
        OutOf: string -> string -> string -> string
    } with
        static member Default =
            { Configurations = [ "Release" ]; Properties = []; Roots = []
              DescriptionOf = Description.beside
              LockOf = (fun name cfg -> sprintf "locks/%s/%s.json" name cfg)
              OutOf = (fun cfg fwk name -> sprintf "out/%s/%s/%s" cfg fwk name) }

    /// The targets of the design-time build: what `CoreBuild` runs up to the compiler, but the
    /// project's `PreBuildEvent` -- `BuildOnlySettings` (what a build, not a design-time
    /// evaluation, resolves), `PrepareForBuild` (with the SDK's `AssignLinkMetadata`: the link
    /// of a shared project's item, hence its manifest name), the references, the resources, the
    /// key, and `Compile`, whose compiler reports its command line instead of running.
    let internal designTimeTargets = "BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;Compile"

    /// The items of the design-time build `record` reads, with the metadata it reads of each.
    let internal designTimeItems =
        [ "CscCommandLineArgs", []
          "FscCommandLineArgs", []
          "ReferencePath",
            [ "ReferenceSourceTarget"; "MSBuildSourceProjectFile"; "OriginalProjectReferenceItemSpec"; "NearestTargetFramework"
              "SetTargetFramework"; "ReferenceAssembly"; "Aliases"; "NuGetPackageId"; "NuGetPackageVersion" ]
          "Analyzer", [ "NuGetPackageId"; "NuGetPackageVersion" ]
          "Compile", []
          "EmbeddedResource", [ "OutputResource"; "WithCulture"; "Culture"; "LogicalName"; "ManifestResourceName"; "Link"; "DependentUpon"; "Type" ]
          "EditorConfigFiles", [] ]
    let internal designTimeProperties =
        [ "AssemblyName"; "MSBuildProjectFullPath"; "MSBuildProjectDirectory"; "MSBuildProjectName"; "TargetFramework"
          "TargetFrameworkIdentifier"; "TargetFrameworkMoniker"; "DefineConstants"; "LangVersion"; "Version"; "OutputType"
          "NETCoreSdkVersion"; "NetCoreRoot"; "NuGetPackageRoot"; "ProjectAssetsFile"; "IntermediateOutputPath"
          "CscToolPath"; "CscToolExe"; "CSharpCoreTargetsPath"; "RoslynTargetsPath"; "DotnetFscCompilerPath"
          "SourceRevisionId"; "GenerateResourceUsePreserializedResources"; "Deterministic"; "SignAssembly"; "AssemblyOriginatorKeyFile"
          "TargetFileName"; "Configuration" ]

    /// <summary>
    /// The targets file `record` hands every project (`CustomAfterMicrosoftCommonTargets`; the
    /// script's own value is imported from it, `XakeChainedAfterCommonTargets`): the target
    /// `XakeRecordDump`, run after the design-time targets in the same project instance,
    /// writes the properties and items `record` reads to a file of its own under
    /// `$(XakeDumpRoot)`, one per line, tab-separated: <c>P name value</c>, <c>I type identity
    /// metadata...</c> (the metadata of `designTimeItems`, in that order). One msbuild process
    /// then runs every design-time build of a configuration, and a project several others
    /// reference is evaluated once.
    /// </summary>
    let internal dumpTargets () =
        let esc (s: string) = Security.SecurityElement.Escape s
        let property name = sprintf "P%%09%s%%09$([MSBuild]::Escape('$(%s)'))" name name
        let item (name, (metadata: string list)) =
            let fields = "%(Identity)" :: (metadata |> List.map (sprintf "%%(%s)")) |> String.concat "%09"
            sprintf "    <WriteLinesToFile File=\"$(_XakeDumpFile)\" Lines=\"%s\" />" (esc (sprintf "@(%s->'I%%09%s%%09%s')" name name fields))
        String.concat "\n"
            [ "<Project>"
              "  <!-- written by Xake.Hermetic.Dotnet's Import.record for one import; deleted after it -->"
              "  <Import Project=\"$(XakeChainedAfterCommonTargets)\" Condition=\"'$(XakeChainedAfterCommonTargets)' != '' and Exists('$(XakeChainedAfterCommonTargets)')\" />"
              // what the traversal hands one build only: not to a referenced project (which is
              // then evaluated once for all the projects referencing it)
              "  <PropertyGroup><_GlobalPropertiesToRemoveFromProjectReferences>$(_GlobalPropertiesToRemoveFromProjectReferences);IntermediateOutputPath;XakeChainedAfterCommonTargets</_GlobalPropertiesToRemoveFromProjectReferences></PropertyGroup>"
              "  <Target Name=\"XakeRecordDump\">"
              "    <PropertyGroup><_XakeDumpFile>$(XakeDumpRoot)/$([System.Guid]::NewGuid().ToString('N')).txt</_XakeDumpFile></PropertyGroup>"
              sprintf "    <WriteLinesToFile File=\"$(_XakeDumpFile)\" Overwrite=\"true\" Lines=\"%s\" />"
                  (esc (designTimeProperties |> List.map property |> String.concat ";"))
              yield! designTimeItems |> List.map item
              "  </Target>"
              "</Project>"
              "" ]

    /// A file `XakeRecordDump` wrote, as a `Dump`.
    let internal readRecordDump (file: string) : Dump =
        let metadataOf = Map.ofList designTimeItems
        let props = ResizeArray<string * string> ()
        let items = Collections.Generic.Dictionary<string, ResizeArray<string * Map<string, string>>> ()
        for line in File.ReadAllLines file do
            match line.Split '\t' |> List.ofArray with
            | "P" :: name :: value -> props.Add (name, String.concat "\t" value)
            | "I" :: kind :: identity :: values ->
                let names = metadataOf |> Map.tryFind kind |> Option.defaultValue []
                let m = Seq.zip names values |> Seq.filter (fun (_, v) -> v <> "") |> Map.ofSeq |> Map.add "Identity" identity
                if not (items.ContainsKey kind) then items.[kind] <- ResizeArray ()
                items.[kind].Add (identity, m)
            | _ -> ()
        { Props = props |> Seq.filter (fun (_, v) -> v <> "") |> Map.ofSeq
          Items = items |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq }

    /// A solution of `projects` (absolute) for each of `configurations` on `platform` (the
    /// script's `Platform`, else `Any CPU`): a restore of all of them under any of those
    /// (`-p:Configuration=<cfg>`). A solution configuration it does not declare fails the
    /// restore (MSB4126).
    let internal solutionText (configurations: string list) (platform: string) (projects: string list) =
        let guid (path: string) =
            use md5 = Security.Cryptography.MD5.Create ()
            Guid(md5.ComputeHash (Text.Encoding.UTF8.GetBytes path)).ToString("B").ToUpperInvariant ()
        let kind (path: string) =
            if path.EndsWith (".fsproj", StringComparison.OrdinalIgnoreCase) then "{6EC3EE1D-3C4E-46DD-8F32-0CC8E7565705}"
            else "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"
        String.concat "\n"
            [ yield ""
              yield "Microsoft Visual Studio Solution File, Format Version 12.00"
              yield "# Visual Studio Version 17"
              for p in projects do
                  yield sprintf "Project(\"%s\") = \"%s\", \"%s\", \"%s\"" (kind p) (Path.GetFileNameWithoutExtension p) (p.Replace ('/', Path.DirectorySeparatorChar)) (guid p)
                  yield "EndProject"
              yield "Global"
              yield "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution"
              for cfg in configurations do
                  yield sprintf "\t\t%s|%s = %s|%s" cfg platform cfg platform
              yield "\tEndGlobalSection"
              yield "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"
              for p in projects do
                  for cfg in configurations do
                      yield sprintf "\t\t%s.%s|%s.ActiveCfg = %s|%s" (guid p) cfg platform cfg platform
                      yield sprintf "\t\t%s.%s|%s.Build.0 = %s|%s" (guid p) cfg platform cfg platform
              yield "\tEndGlobalSection"
              yield "EndGlobal"
              yield "" ]

    /// The properties of the design-time build a lock entry keeps (`Evaluation.Properties`).
    let private keptProperties =
        [ "AssemblyName"; "TargetFrameworkMoniker"; "LangVersion"; "Version"; "OutputType"; "Deterministic"; "SignAssembly"
          "GenerateResourceUsePreserializedResources"; "TargetFileName" ]

    /// What one project's entries are built from, besides its design-time builds.
    type internal RecordContext = {
        /// the build's project root, '/'
        Root: string
        ProjectFile: string
        /// the project file's folder, absolute, '/'
        ProjectDir: string
        Description: Description.Project
        DescriptionFile: string
        PackageRoot: string
        /// the project's SDK pin and the global.json stating it
        Pin: Lock.SdkPin
        PinFile: string option
        /// the global.json as a prerequisite names it: tokenized, or `global.json` alone when it
        /// lies outside the roots (it is quoted in messages, never opened)
        Pin': string
        /// the repository's msbuild files the evaluation read (absolute)
        Inputs: string list
        /// the project files this import records (absolute, '/'): a project reference must be one
        Imported: string list
        OutOf: string -> string -> string -> string
    }

    /// The file a design-time build reported, absolute, '/'.
    let private absoluteIn (dir: string) (path: string) =
        let path = slash path
        slash (Path.GetFullPath (if Path.IsPathRooted path then path else Path.Combine (dir, path)))

    /// The compiler msbuild would run: csc from `CscToolPath`/`CscToolExe`, else the
    /// `bincore/csc.dll` beside the C# targets that drive the task (the SDK's own, or a
    /// `Microsoft.Net.Compilers.Toolset` package's); fsc from `DotnetFscCompilerPath`.
    let internal compilerOf (isFSharp: bool) (d: Dump) =
        let p = prop d
        if isFSharp then
            match (p "DotnetFscCompilerPath").Trim().Trim '"' with
            | "" -> failwithf "'%s': the design-time build reported no DotnetFscCompilerPath -- not an SDK-style F# project?" (p "MSBuildProjectFullPath")
            | path -> slash (Path.GetFullPath path)
        else
            match p "CscToolPath" with
            | "" ->
                match p "CSharpCoreTargetsPath" with
                | "" -> slash (p "RoslynTargetsPath" </> "bincore" </> "csc.dll")
                | csTargets -> slash (Path.GetFullPath (Path.GetDirectoryName (slash csTargets) </> "bincore" </> "csc.dll"))
            | toolPath -> slash (toolPath </> (match p "CscToolExe" with "" -> "csc.dll" | exe -> exe))

    /// The project references of a design-time build: each referenced project's output in the
    /// build's layout (`outOf cfg <framework msbuild chose> <name>`), and the paths msbuild
    /// names it by (its output, its reference assembly).
    let internal projectsOf (ctx: RecordContext) (cfg: string) (d: Dump) : (Lock.ProjectOutput * string list) list =
        let found =
          [ for id, m in items d "ReferencePath" do
            let source = meta "MSBuildSourceProjectFile" m
            if meta "ReferenceSourceTarget" m = "ProjectReference" || source <> "" then
                let projectFile = absoluteIn ctx.ProjectDir (match source with "" -> meta "OriginalProjectReferenceItemSpec" m | s -> s)
                let name = Path.GetFileNameWithoutExtension projectFile
                // its output is found by the lock of that project, which this import must write
                if not (ctx.Imported |> List.exists (fun p -> String.Equals (p, projectFile, comparison))) then
                    failwithf "'%s' (%s|%s) references the project '%s', which this import does not record: its output would have no lock; add it to the imported projects"
                        ctx.ProjectFile cfg (prop d "TargetFramework") projectFile
                let framework =
                    match meta "NearestTargetFramework" m, meta "SetTargetFramework" m with
                    | "", set when set.StartsWith "TargetFramework=" -> set.Substring 16
                    | "", _ -> prop d "TargetFramework"
                    | f, _ -> f
                let output = absoluteIn ctx.Root (ctx.OutOf cfg framework name </> Path.GetFileName (slash id))
                let names = [ id; meta "ReferenceAssembly" m ] |> List.filter ((<>) "") |> List.map (absoluteIn ctx.ProjectDir)
                yield projectFile, (({ Name = name; Configuration = cfg; Framework = framework; Path = output } : Lock.ProjectOutput), names) ]
        // a project is `project:<name>`: two project files of one name cannot both be
        match found |> List.map fst |> List.distinct |> List.groupBy Path.GetFileNameWithoutExtension |> List.tryFind (fun (_, fs) -> fs.Length > 1) with
        | Some (name, files) ->
            failwithf "'%s' (%s|%s) references two projects named '%s' (%s); a recorded lock names a project reference by its name: rename one"
                ctx.ProjectFile cfg (prop d "TargetFramework") name (String.concat ", " files)
        | None -> ()
        found |> List.map snd |> List.distinctBy (fun (p, _) -> p.Name)

    /// An argument without the quotes msbuild puts around a path with a character it deems
    /// special (`~`, a space): a source `"<path>"`, a switch value `"<path>"` or `"<path>",<name>`.
    /// A quoted path with a comma keeps its quotes (the switch's list syntax needs them).
    let internal unquote (arg: string) =
        let strip (v: string) =
            let m = Text.RegularExpressions.Regex.Match (v, "^\"([^\",]*)\"(,.*)?$")
            if m.Success then m.Groups.[1].Value + m.Groups.[2].Value else v
        if arg.StartsWith "/" || arg.StartsWith "-" then
            match arg.IndexOf ':' with
            | -1 -> arg
            | i -> arg.Substring (0, i + 1) + strip (arg.Substring (i + 1))
        else strip arg

    /// Text as a lock records it: LF line ends.
    let private lf (text: string) = text.Replace ("\r\n", "\n")

    /// <summary>
    /// The kinds of file msbuild generates that a lock records as text (`Generated`): sources
    /// (`.cs`, `.fs`, `.vb` -- AssemblyInfo, GlobalUsings, the framework attribute, a package's
    /// entry point), analyzer configurations (`.editorconfig`, `.globalconfig`) and `.json`,
    /// `.txt`. By content kind, not by a list of binaries: any other file is copied byte for
    /// byte beside the lock (`entryOf`).
    /// </summary>
    let internal isRecordedText (path: string) =
        match (Path.GetExtension path).ToLowerInvariant () with
        | ".cs" | ".fs" | ".vb" | ".editorconfig" | ".globalconfig" | ".json" | ".txt" -> true
        | _ -> false

    /// <summary>
    /// The lock entry of one design-time build (`d`, of `cfg` and `framework`, whose
    /// intermediate folder was `scratch`): msbuild's compiler command line as recorded, with
    /// <list type="bullet">
    /// <item>the output switches dropped (`/out:`, `/refout:`: the build's);</item>
    /// <item>what msbuild generated under `scratch` (assembly info, global usings, the
    /// framework attribute, the analyzer config, the compiled `.resources`) moved to the
    /// entry's generated folder (`Described.generatedDirOf`), the text files recorded
    /// (`Generated`, LF), the `.resx` it compiles as `Resources`;</item>
    /// <item>a source from a package (Test.Sdk's entry point) recorded the same way;</item>
    /// <item>for C#, the user sources as the description's masks when they name exactly the
    /// files msbuild compiles (else msbuild's files, with a warning);</item>
    /// <item>a project reference as the referenced project's output in the build's layout
    /// (`Recorded.Projects`), unhashed; every other reference and analyzer hashed, but the
    /// SDK's own files, which the SDK version identifies (`Prerequisites`).</item>
    /// </list>
    /// Pure but for reading the generated files and hashing.
    /// </summary>
    let internal entryOf (ctx: RecordContext) (cfg: string) (framework: string) (scratch: string) (binaryDir: string) (d: Dump)
                         : Lock.Entry * string list * (string * string) list =
        let warnings = ResizeArray<string> ()
        let p = prop d
        let scratch = (slash scratch).TrimEnd '/' + "/"
        let binaryDir = (slash binaryDir).TrimEnd '/' + "/"
        let assemblyName = match p "AssemblyName" with "" -> ctx.Description.Name | a -> a
        let generatedDir =
            let key = Described.generatedKey ctx.Root ctx.DescriptionFile ctx.ProjectDir assemblyName
            (Described.generatedDirOf ctx.Root key framework (Some cfg)).TrimEnd '/' + "/"
        let fscArgs = items d "FscCommandLineArgs" |> List.map fst
        let isFSharp = not (List.isEmpty fscArgs)
        let parked (a: string) =
            let a = a.TrimStart('-', '/').ToLowerInvariant ()
            a.StartsWith "sourcelink:" || a = "embed" || a.StartsWith "embed:" || a.StartsWith "embed+" || a.StartsWith "embed-"
        let args =
            if isFSharp then fscArgs |> List.filter (parked >> not) |> List.map unquote |> FscArgs.absolutize ctx.ProjectDir
            else items d "CscCommandLineArgs" |> List.map fst |> List.filter (parked >> not) |> List.map unquote |> CscArgs.absolutize ctx.ProjectDir
        if List.isEmpty args then
            failwithf "'%s' (%s|%s): the design-time build reported no compiler command line -- CoreCompile did not run" ctx.ProjectFile cfg framework
        let packageRoots =
            [ ctx.PackageRoot; p "NuGetPackageRoot" ] |> List.filter ((<>) "") |> List.map (fun r -> (slash r).TrimEnd '/' + "/") |> List.distinct
        let inPackage (path: string) = packageRoots |> List.exists (fun r -> path.StartsWith (r, comparison))
        let sdkDir =
            match p "NetCoreRoot", p "NETCoreSdkVersion" with
            | "", _ | _, "" -> None
            | root, v -> Some ((slash root).TrimEnd '/' + "/sdk/" + v + "/")
        let inSdk (path: string) = sdkDir |> Option.exists (fun s -> path.StartsWith (s, comparison))

        // the project references, by every path msbuild names them with
        let projects = projectsOf ctx cfg d
        let projectPaths = dict [ for o, names in projects do for n in names -> n, o.Path ]
        // what msbuild generated under its intermediate folder: a known text kind is recorded
        // in the lock (`Generated`), a `.resources` compiled from a `.resx` is compiled again by
        // the build, anything else (a WinForms `.licenses`, a WPF `g.resources`, a `.res`) is
        // copied byte for byte beside the lock (`binaryDir`) and named there
        let inputs = if isFSharp then FscArgs.inputs args else CscArgs.inputs args
        let inScratch (path: string) = path.StartsWith (scratch, comparison)
        let resxOutputs =
            set [ for _, m in items d "EmbeddedResource" do
                    if meta "OutputResource" m <> "" then yield absoluteIn ctx.ProjectDir (meta "OutputResource" m) ]
        let binaries =
            [ for path in inputs |> List.map slash |> List.distinct do
                if inScratch path && File.Exists path && not (isRecordedText path) && not (resxOutputs.Contains path) then
                    yield path, binaryDir + path.Substring scratch.Length ]
        let binaryOf = dict binaries
        let moved (path: string) =
            let path = slash path
            match projectPaths.TryGetValue path, binaryOf.TryGetValue path with
            | (true, output), _ -> output
            | _, (true, copy) -> copy
            | _ -> if inScratch path then generatedDir + path.Substring scratch.Length else path
        // a source from a package: recorded beside the generated ones
        let packaged (path: string) = generatedDir + Path.GetFileName path
        // the build's own (the output, the reference assembly), and what is parked: SourceLink's
        // map and the embedded untracked sources (`pr/sourcelink`; they name the commit and the
        // remote of the importing checkout)
        let dropped = set [ "out"; "refout"; "sourcelink"; "embed" ]
        let isOutput (o: string) =
            not (o.StartsWith "@")
            && (if isFSharp then
                    match FscArgs.parse o with FscArgs.Switch (_, n, _) -> dropped.Contains (FscArgs.canonical n) | _ -> false
                else
                    match CscArgs.parse o with CscArgs.Switch (n, _) -> dropped.Contains (CscArgs.canonical n) | _ -> false)

        // what msbuild generated: text inputs under the scratch folder, and package sources
        let generated =
            [ for path in inputs |> List.map slash |> List.distinct do
                if File.Exists path && isRecordedText path then
                    if inScratch path then
                        yield moved path, (lf (File.ReadAllText path)).Replace (scratch, generatedDir)
                    elif inPackage path && (if isFSharp then FscArgs.sources args else CscArgs.sources args) |> List.exists (fun s -> slash s = path) then
                        yield packaged path, lf (File.ReadAllText path) ]
        let fromPackage = set [ for path in inputs |> List.map slash do if inPackage path then yield path ]

        let composed =
            if isFSharp then Lock.Compilation.Fsc (Fsc.ofArgs args) else Lock.Compilation.Csc (Csc.ofArgs args)
        let entry0 = Lock.ofCompilation composed
        // the package sources first (their path is the package's until moved), then the rest
        let entry1 =
            entry0 |> Lock.mapPaths (fun path ->
                let path = slash path
                if fromPackage.Contains path && (composed.Sources |> List.exists (fun s -> slash s = path)) then packaged path else moved path)
        let c = entry1.Compilation

        // the sources: msbuild's, in its order; for C#, the description's masks beside them when
        // they name exactly the files msbuild compiles (a build then takes an added file too)
        let isGenerated (s: string) = generated |> List.exists (fst >> (=) s)
        let user = c.Sources |> List.filter (isGenerated >> not)
        let sources = c.Sources
        let masks =
            match ctx.Description.Language with
            | Description.CSharp ->
                let masks = ctx.Description.Sources
                let expanded = expandMasks ctx.ProjectDir masks |> Set.ofList
                let compiled = user |> List.map slash |> Set.ofList
                if expanded = compiled then
                    let inProject (s: string) = slash (Path.GetFullPath (Path.Combine (ctx.ProjectDir, s)))
                    masks |> List.map (fun m -> if m.StartsWith "!" then "!" + inProject (m.Substring 1) else inProject m)
                else
                    let missed = Set.difference compiled expanded |> Set.toList
                    let extra = Set.difference expanded compiled |> Set.toList
                    warnings.Add (
                        sprintf "'%s' (%s|%s): the description's source masks do not name what msbuild compiles (missed: %s; extra: %s); the lock keeps msbuild's files only (an added .cs needs a re-import)"
                            ctx.Description.Name cfg framework
                            (match missed with [] -> "none" | m -> m |> List.map (relativeTo ctx.ProjectDir) |> String.concat ", ")
                            (match extra with [] -> "none" | e -> e |> List.map (relativeTo ctx.ProjectDir) |> String.concat ", "))
                    []
            | Description.FSharp -> []

        // the .resx msbuild compiles for the main assembly, as (resx, compiled .resources)
        let resourceSwitches = if isFSharp then FscArgs.switchValues "resource" c.Options else CscArgs.switchValues "resource" c.Options
        let named (path: string) = resourceSwitches |> List.exists (fun v -> v = path || v.StartsWith (path + ","))
        let resources =
            [ for id, m in items d "EmbeddedResource" do
                if meta "OutputResource" m <> "" && not (isTrue (meta "WithCulture" m)) then
                    let compiled = moved (absoluteIn ctx.ProjectDir (meta "OutputResource" m))
                    if named compiled then yield absoluteIn ctx.ProjectDir id, compiled ]
            |> List.distinct

        // the hashes: a project's output and the SDK's own files carry none
        let isProjectOutput (path: string) = projects |> List.exists (fun (o, _) -> o.Path = path)
        let hash (path: string) = if isProjectOutput path || inSdk path then "" else Csc.sha256 path
        let compilerPath = compilerOf isFSharp d
        let dependencies (deps: Dependencies) : Dependencies =
            { Compiler =
                { Tool = (if isFSharp then "fsc" else "csc"); Path = compilerPath
                  Sha256 = (if inSdk compilerPath then "" else Csc.sha256 compilerPath)
                  Version = Csc.compilerVersion compilerPath }
              References = deps.References |> List.map (fun r -> { r with Sha256 = hash r.Path })
              Analyzers = deps.Analyzers |> List.map (fun a -> { a with Sha256 = hash a.Path }) }
        let compilation =
            match c with
            | Lock.Compilation.Csc x ->
                Lock.Compilation.Csc
                    { x with
                        Name = assemblyName; Framework = framework; Directory = ctx.ProjectDir
                        Options = x.Options |> List.filter (isOutput >> not)
                        Sources = sources; Generated = generated; Resources = resources
                        Dependencies = dependencies x.Dependencies }
            | Lock.Compilation.Fsc x ->
                Lock.Compilation.Fsc
                    { x with
                        Fsc.Name = assemblyName; Fsc.Framework = framework; Fsc.Directory = ctx.ProjectDir
                        Fsc.Options = x.Options |> List.filter (isOutput >> not)
                        Fsc.Sources = sources; Fsc.Generated = generated; Fsc.Resources = resources
                        Fsc.Dependencies = dependencies x.Dependencies }

        // the restore graph of this framework, for the SBOM and the package hashes
        let packages =
            match p "ProjectAssetsFile" with
            | assets when assets <> "" && File.Exists assets ->
                try Nuget.readAssets assets framework |> Lock.packagesOf ctx.PackageRoot
                with e -> warnings.Add (sprintf "'%s' (%s): the restore graph is not recorded: %s" ctx.Description.Name framework e.Message); []
            | _ -> []
        let sdk = p "NETCoreSdkVersion"
        let entry : Lock.Entry =
            { Compilation = compilation
              Evaluation =
                { Project = slash ctx.ProjectFile
                  ProjectRefs = projects |> List.map (fun (o, _) -> o.Name)
                  Imports = ctx.Inputs |> List.map Csc.hashed
                  Sdk = sdk
                  SdkPin = Some ctx.Pin
                  Properties = d.Props |> Map.filter (fun k v -> List.contains k keptProperties && v <> "") }
              Packages = packages
              // the SDK the entry was recorded with: its compiler, analyzers and shims
              Prerequisites =
                if sdk = "" then []
                else [ { Kind = Lock.DotnetSdk; Version = sdk; Pin = ctx.Pin' } ]
              Recorded = Some { Projects = projects |> List.map fst; Masks = masks; Placed = None } }
        // the commit a SourceLink-free AssemblyInfo still names (`InformationalVersion`): a token
        let entry = match p "SourceRevisionId" with "" -> entry | sha -> entry |> Lock.mapText (Git.tokenize sha)

        // .NET Framework reads preserialized resources only through System.Resources.Extensions:
        // msbuild writes them so when the project asks for it, so the recorded references must hold it
        if isTrue (p "GenerateResourceUsePreserializedResources") && p "TargetFrameworkIdentifier" = ".NETFramework"
           && not (List.isEmpty resources)
           && not (entry.Dependencies.References |> List.exists (fun r -> (Path.GetFileName r.Path).Equals ("System.Resources.Extensions.dll", StringComparison.OrdinalIgnoreCase))) then
            failwithf "'%s' (%s|%s): msbuild writes the .resx as preserialized resources (GenerateResourceUsePreserializedResources), which .NET Framework reads only through System.Resources.Extensions, and the recorded references do not have it; reference the package System.Resources.Extensions"
                ctx.Description.Name cfg framework
        entry, List.ofSeq warnings, binaries

    /// <summary>
    /// What a recorded entry's command line names that no portable name covers, as messages:
    /// every file a switch or a source names (`/analyzerconfig:`, `/keyfile:`, `/ruleset:`,
    /// `/win32icon:`, `/win32manifest:`, `/additionalfile:`, `/resource:`, ...) must have an
    /// identity (`Lock.Identity.ofPath`: the package folder, the packs, the entry's SDK, a
    /// referenced project, a root), as the references must; a `/pathmap:` source path must lie
    /// under a root. An `.editorconfig` above the repository is such a file: declare its folder
    /// as a root, or move it in. Pure.
    /// </summary>
    let internal unportablePaths (roots: (string * string) list) (entry: Lock.Entry) : string list =
        let c = entry.Compilation
        let context : Lock.Identity.Context =
            { Roots = roots; Sdk = entry.Evaluation.Sdk
              Projects = entry.Recorded |> Option.map (fun r -> r.Projects) |> Option.defaultValue [] }
        let isFSharp = match c with Lock.Compilation.Fsc _ -> true | _ -> false
        let args = c.Args |> List.filter (fun a -> not (a.StartsWith "@"))
        let files = (if isFSharp then FscArgs.inputs args else CscArgs.inputs args) |> List.map slash |> List.distinct
        let pathmaps =
            [ for a in args do
                let bare = a.TrimStart ('-', '/')
                if bare.StartsWith ("pathmap:", StringComparison.OrdinalIgnoreCase) then
                    yield! (bare.Substring 8).Split ',' ]
            |> List.choose (fun m -> match m.IndexOf '=' with -1 -> None | i -> Some (m.Substring (0, i)))
            |> List.distinct
        [ for f in files do
            if (Lock.Identity.ofPath context f).IsNone then
                yield sprintf "'%s' (%s): the compiler's command line names '%s', which cannot be named portably: it is not in the package folder, the .NET packs or the SDK %s, nor under $(ProjectRoot) or a root the script declares; declare its folder as a root (RecordOptions.Roots) or move it into the repository"
                        c.Name c.Framework f entry.Evaluation.Sdk
          for m in pathmaps do
            if not ((Roots.tokenize roots (slash m)).StartsWith "$(") then
                yield sprintf "'%s' (%s): the compiler's /pathmap: maps '%s', which is under no root; declare its folder as a root (RecordOptions.Roots)" c.Name c.Framework m ]

    /// The warning for a recorded lock whose SDK prerequisite (`sdk`, the SDK that recorded it)
    /// is stricter than the project's `global.json` (`pin`): a machine with another SDK the pin
    /// allows has to install `sdk` or import again (and every lock of the set changes).
    let internal sdkPinWarning (name: string) (sdk: string) (pin: Lock.SdkPin) =
        sprintf "'%s': the lock pins the .NET SDK %s (a prerequisite), more strictly than global.json does (%s); a machine with another SDK must install %s or import again. To say so in global.json: { \"sdk\": { \"version\": \"%s\", \"rollForward\": \"disable\" } }"
            name sdk (match pin with Lock.NoGlobalJson -> "no global.json" | p -> Lock.sdkPinText p) sdk sdk

    /// The strings of this machine a portable lock must not contain (`record` checks the text
    /// it writes): the project root, the package folder, the .NET root, the home and temp folders
    /// -- each only where no token stands for it -- and a runtime identifier (`osx-arm64`) as a
    /// path segment or a whole value.
    let internal hostStrings (roots: (string * string) list) (text: string) : string list =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let candidates =
            [ for _, root in roots -> root
              yield home
              yield Path.GetTempPath () ]
            |> List.map (fun s -> (slash s).TrimEnd '/')
            |> List.filter (fun s -> s.Length > 1)
            |> List.distinct
        // a runtime identifier: a lock recorded without one names no machine's
        let rids =
            [ for os in [ "osx"; "linux"; "linux-musl"; "win" ] do
                for arch in [ "x64"; "x86"; "arm64"; "arm" ] -> sprintf "%s-%s" os arch ]
        [ for line in text.Split '\n' do
            let l = slash line
            if candidates |> List.exists (fun c -> l.Contains c) || rids |> List.exists (fun r -> l.Contains ("/" + r + "/") || l.Contains ("\"" + r + "\"")) then
                yield line.Trim () ]

    /// <summary>
    /// Imports the `projects` (csproj, fsproj; relative to the project root or absolute) as
    /// msbuild resolves them, and writes, per project, its description (`describe`, as
    /// `project`) and one lock per configuration (`options.LockOf name cfg`) whose entries --
    /// one per framework -- record msbuild's resolved compile inputs:
    /// <list type="number">
    /// <item>one restore per project, without a framework (`-t:Restore`,
    /// `RestoreRecursive=false`), concurrently;</item>
    /// <item>the evaluations for the description, and one design-time build per
    /// (configuration, framework) -- `ProvideCommandLineArgs`, `SkipCompilerExecution`,
    /// `BuildProjectReferences=false`, the targets `CoreBuild` runs up to the compiler
    /// (`designTimeTargets`) -- reporting the compiler's command line, `ReferencePath`,
    /// `Analyzer`, `Compile`, `EmbeddedResource`; every design-time build of a configuration in
    /// one msbuild process;</item>
    /// <item>each entry (`entryOf`): the command line as msbuild hands it to the compiler, the
    /// files msbuild generated as recorded texts, the references and analyzers by identity
    /// (`Lock.Identity`: `pkg:`, `pack:`, `sdk:`, `project:`), the SDK version as a
    /// prerequisite.</item>
    /// </list>
    /// Nothing about the compilation is derived by Xake. A file that cannot be named portably
    /// (outside the package folder, the packs, the SDK and the roots) fails the import, and so
    /// does a lock text that still holds a string of this machine (`hostStrings`). A lock is
    /// written only when its text changes. Records no input: an explicit command, as `project`.
    /// </summary>
    let record (options: RecordOptions) (projects: string list) : Recipe<ExecContext, Imported list> =
        recipe {
            if List.isEmpty options.Configurations then failwith "RecordOptions.Configurations is empty: name at least one configuration"
            let! ctxOptions = getCtxOptions ()
            let root = slash (Path.GetFullPath ctxOptions.ProjectRoot)
            let full (p: string) = slash (Path.GetFullPath (if Path.IsPathRooted p then p else root </> p))
            let! packageRoot, packageSource = DotNetFwk.packageRootWithSource ()
            // with the script variable `NUGET_PACKAGES`, msbuild restores into the build's folder too
            let environment = if packageSource = DotNetFwk.ScriptVariable then [ "NUGET_PACKAGES", packageRoot ] else []
            let! roots = Roots.currentWith options.Roots
            let globals = [ "NuGetAudit", "false" ] @ options.Properties
            let switches cfg = [ for k, v in ("Configuration", cfg) :: globals -> sprintf "-p:%s=%s" k v ]
            let primary = List.head options.Configurations
            let started = DateTime.UtcNow
            let temp = root </> "obj" </> "xake" </> "import" </> Guid.NewGuid().ToString "N"
            Directory.CreateDirectory temp |> ignore
            try
                let projectFiles = projects |> List.map full |> List.distinct
                // the design-time builds write what they generate (AssemblyInfo, the compiled
                // resources) into an intermediate folder of their own, keyed by the configuration
                // and the properties: two imports with different properties at once (two brands)
                // never read each other's files, nor an IDE's or `dotnet build`'s
                let key cfg =
                    use sha = Security.Cryptography.SHA256.Create ()
                    let text =
                        ("Configuration", cfg) :: globals |> List.map (fun (k, v) -> k.ToLowerInvariant () + "=" + v)
                        |> List.sort |> String.concat "\n"
                    sha.ComputeHash (Text.Encoding.UTF8.GetBytes text) |> Array.take 6 |> Array.map (sprintf "%02x") |> String.concat ""
                let runProperties (key: string) (ev: Evaluated) (framework: string) =
                    let escape (v: string) = v.Replace("%", "%25").Replace(";", "%3B")
                    let projectDir = prop ev.Outer "MSBuildProjectDirectory"
                    let baseObj = match prop ev.Outer "BaseIntermediateOutputPath" with "" -> "obj/" | b -> b
                    let intermediate = sprintf "%s/xake-record/%s/%s/" ((absoluteIn projectDir baseObj).TrimEnd '/') key framework
                    // the project's own targets after the common targets (a repository's
                    // `Directory.Build.props`, or the script's): imported from Xake's
                    let chained =
                        match prop ev.Outer "CustomAfterMicrosoftCommonTargets" with
                        | "" -> []
                        | v -> [ "XakeChainedAfterCommonTargets", absoluteIn projectDir v ]
                    [ "TargetFramework", framework; "IntermediateOutputPath", intermediate ] @ chained
                    |> List.map (fun (k, v) -> k + "=" + escape v) |> String.concat ";"
                let elapsed (since: DateTime) = (DateTime.UtcNow - since).TotalSeconds
                let platform =
                    options.Properties |> List.tryFind (fun (k, _) -> k.Equals ("Platform", StringComparison.OrdinalIgnoreCase))
                    |> Option.map snd |> Option.defaultValue "Any CPU"
                // the restore of every project (a solution of them) under one configuration,
                // before anything reads what it writes
                let solution = temp </> "import.sln"
                File.WriteAllText (solution, solutionText options.Configurations platform projectFiles)
                let restore cfg =
                    recipe {
                        let phase = DateTime.UtcNow
                        do! msbuild environment ([ solution; "-nologo"; "-verbosity:quiet"; "-t:Restore" ] @ switches cfg) solution
                        do! trace Info "import: the restore of %d project(s) for %s in %.1f s" projectFiles.Length cfg (elapsed phase)
                    }
                // 1. the restore under the first configuration
                do! restore primary
                // 2. the evaluations (the description), concurrently
                let phase = DateTime.UtcNow
                let! evaluations =
                    projectFiles |> List.mapi (fun index pf ->
                        recipe {
                            let projectTemp = temp </> string index
                            Directory.CreateDirectory projectTemp |> ignore
                            let! ev = evaluate environment pf (switches primary) projectTemp
                            return ev
                        })
                    |> concurrently
                do! trace Info "import: %d project evaluation(s) in %.1f s" projectFiles.Length (elapsed phase)
                // 3. every design-time build of a configuration in one msbuild process: a traversal
                // project over (project, framework), the dump target handed to every project
                let targetsFile = temp </> "xake-record.targets"
                File.WriteAllText (targetsFile, dumpTargets ())
                let recordSwitches cfg =
                    [ for k, v in ("Configuration", cfg) :: globals do
                        if not (k.Equals ("CustomAfterMicrosoftCommonTargets", StringComparison.OrdinalIgnoreCase)) then
                            yield sprintf "-p:%s=%s" k v ]
                    @ [ "-p:CustomAfterMicrosoftCommonTargets=" + slash targetsFile
                        "-p:ProvideCommandLineArgs=true"; "-p:SkipCompilerExecution=true"; "-p:BuildProjectReferences=false"
                        // CoreCompile lists this among its outputs: a file that never exists keeps it
                        // from being skipped as up to date
                        "-p:NonExistentFile=__NonExistentSubDir__/__NonExistentFile__" ]
                let runs = [ for pf, ev in List.zip projectFiles evaluations do for f, _ in ev.Inner -> pf, ev, f ]
                let phase = DateTime.UtcNow
                // one configuration after the other: its restore (the assets file is the project's,
                // not the configuration's, and a package may depend on the configuration), its
                // design-time builds, and a copy of each assets file they read
                let! dumps =
                    options.Configurations |> List.map (fun cfg ->
                        recipe {
                            if cfg <> primary then do! restore cfg
                            let dumpRoot = temp </> "dump-" + cfg
                            let assetsRoot = temp </> "assets-" + cfg
                            Directory.CreateDirectory dumpRoot |> ignore
                            Directory.CreateDirectory assetsRoot |> ignore
                            let traversal = temp </> sprintf "record-%s.proj" cfg
                            let runItems =
                                runs |> List.map (fun (pf, ev, f) ->
                                    sprintf "    <XakeRun Include=\"%s\" AdditionalProperties=\"%s\" />"
                                        (Security.SecurityElement.Escape pf)
                                        (Security.SecurityElement.Escape (runProperties (key cfg) ev f)))
                            File.WriteAllText (traversal, String.concat "\n" ([ "<Project>"; "  <ItemGroup>" ] @ runItems @
                                                                              [ "  </ItemGroup>"
                                                                                "  <Target Name=\"Build\">"
                                                                                (sprintf "    <MSBuild Projects=\"@(XakeRun)\" Targets=\"%s;XakeRecordDump\" BuildInParallel=\"true\" />" designTimeTargets)
                                                                                "  </Target>"; "</Project>"; "" ]))
                            do! msbuild environment
                                    ([ traversal; "-nologo"; "-verbosity:quiet"; "-m"; "-nodeReuse:false"; "-p:XakeDumpRoot=" + slash dumpRoot ]
                                     @ recordSwitches cfg) traversal
                            let byRun =
                                Directory.GetFiles (dumpRoot, "*.txt") |> Array.map readRecordDump
                                |> Array.mapi (fun i d ->
                                    // the restore of the next configuration rewrites the assets file
                                    let d =
                                        match prop d "ProjectAssetsFile" with
                                        | assets when assets <> "" && File.Exists assets ->
                                            let copy = assetsRoot </> sprintf "%d.json" i
                                            File.Copy (assets, copy)
                                            { d with Props = d.Props |> Map.add "ProjectAssetsFile" copy }
                                        | _ -> d
                                    (slash (prop d "MSBuildProjectFullPath"), prop d "TargetFramework"), d)
                                |> Map.ofArray
                            return
                                [ for pf, _, f in runs ->
                                    match byRun |> Map.tryFind (slash pf, f) with
                                    | Some d -> pf, (cfg, f, d)
                                    | None -> failwithf "'%s': the design-time build for %s|%s wrote nothing; the import stops" pf cfg f ]
                        })
                    |> sequentially
                let dumps = List.concat dumps
                do! trace Info "import: %d design-time build(s) in %.1f s" dumps.Length (elapsed phase)
                let evaluated =
                    List.zip projectFiles evaluations
                    |> List.map (fun (pf, ev) ->
                        let runs =
                            [ for p, (cfg, f, d) in dumps do
                                if p = pf then
                                    // msbuild's own intermediate folder of that build: what it generated
                                    let scratch = absoluteIn (slash (Path.GetDirectoryName pf)) (prop d "IntermediateOutputPath")
                                    yield cfg, f, scratch, d ]
                        pf, ev, runs)

                // 3. the descriptions and the locks
                let results = ResizeArray<Imported> ()
                for pf, ev, runs in evaluated do
                    let descriptionFile = full (options.DescriptionOf pf)
                    let targets = [ for cfg, f, _, d in runs do if cfg = primary then yield f, d ]
                    let! imported, description, inputs = writeDescription root pf descriptionFile packageRoot ev targets
                    let pinFile, pin = Lock.sdkPinAt (Path.GetDirectoryName pf)
                    let ctx : RecordContext =
                        { Root = root; ProjectFile = slash pf; ProjectDir = slash (Path.GetDirectoryName pf)
                          Description = description; DescriptionFile = descriptionFile; PackageRoot = packageRoot
                          Pin = pin; PinFile = pinFile; Inputs = inputs; OutOf = options.OutOf
                          Imported = projectFiles |> List.map slash
                          Pin' =
                            match pinFile |> Option.map (Roots.tokenize roots) with
                            | Some t when t.StartsWith "$(" -> t
                            | Some t -> Path.GetFileName t
                            | None -> "" }
                    // the lock pins the SDK that recorded it (a prerequisite): say so where
                    // global.json lets another one build
                    match [ for _, _, _, d in runs -> prop d "NETCoreSdkVersion" ] |> List.filter ((<>) "") |> List.tryHead with
                    | Some sdk when pin <> Lock.Pinned sdk ->
                        do! trace Warning "%s" (sdkPinWarning description.Name sdk pin)
                    | _ -> ()
                    let locks = ResizeArray<string> ()
                    for cfg in options.Configurations do
                        let lockPath = options.LockOf description.Name cfg
                        let lockFull = full lockPath
                        // the generated binaries of the lock, beside it: `<lock>.files/<framework>/`
                        let filesDir = Path.ChangeExtension (lockFull, null) + ".files"
                        let built = [ for c, f, scratch, d in runs do if c = cfg then yield f, entryOf ctx cfg f scratch (filesDir </> f) d ]
                        for _, (_, warnings, _) in built do
                            for w in warnings do do! trace Warning "%s" w
                        let entries = built |> List.map (fun (_, (e, _, _)) -> e)
                        match entries |> List.collect (unportablePaths roots) |> List.distinct with
                        | [] -> ()
                        | problems -> failwith (String.concat "\n" problems)
                        // HERMETIC=on: what a lock may not be written with (messages 2 and 9)
                        let! refused = Lock.hermeticRecordGate lockPath roots entries
                        if not (List.isEmpty refused) then
                            let msg = refused |> String.concat "\n"
                            do! trace Error "%s" msg
                            failwith msg
                        let text = Lock.format roots { Configuration = cfg; Properties = options.Properties; Entries = entries }
                        match hostStrings roots text with
                        | [] -> ()
                        | lines ->
                            failwithf "'%s': the lock '%s' would hold strings of this machine, so it would not build elsewhere:\n%s"
                                description.Name lockPath (lines |> List.truncate 10 |> String.concat "\n")
                        // the binaries first (a lock never names a file that is not there), each
                        // written when its bytes change, and nothing else left in the folder
                        let copies = built |> List.collect (fun (_, (_, _, b)) -> b)
                        for source, copy in copies do
                            let bytes = File.ReadAllBytes source
                            if not (File.Exists copy) || File.ReadAllBytes copy <> bytes then
                                Directory.CreateDirectory (Path.GetDirectoryName copy) |> ignore
                                File.WriteAllBytes (copy, bytes)
                        if Directory.Exists filesDir then
                            let kept = copies |> List.map (snd >> Path.GetFullPath) |> set
                            for f in Directory.GetFiles (filesDir, "*", SearchOption.AllDirectories) do
                                if not (kept.Contains (Path.GetFullPath f)) then File.Delete f
                            if List.isEmpty copies then Directory.Delete (filesDir, true)
                        let existing = if File.Exists lockFull then Some (lf (File.ReadAllText lockFull)) else None
                        if existing = Some text then
                            do! trace Info "'%s': the lock '%s' is unchanged" description.Name lockPath
                        else
                            Directory.CreateDirectory (Path.GetDirectoryName lockFull) |> ignore
                            File.WriteAllText (lockFull, text)
                            do! trace Info "'%s': wrote the lock '%s'" description.Name lockPath
                        locks.Add lockPath
                    results.Add { imported with Locks = List.ofSeq locks }
                do! trace Info "imported %d project(s) for %s in %.1f s" projectFiles.Length (String.concat ", " options.Configurations)
                        (DateTime.UtcNow - started).TotalSeconds
                return List.ofSeq results
            finally
                try Directory.Delete (temp, true) with _ -> ()
        }
