namespace Xake.Hermetic.Dotnet

open System
open System.IO
open System.Xml.Linq

open Xake
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
          Dropped: string list }

    let private properties =
        [ "MSBuildProjectName"; "MSBuildProjectExtension"; "MSBuildProjectDirectory"; "TargetFrameworks"; "TargetFramework"
          "AssemblyName"; "RootNamespace"; "OutputType"; "Configuration"; "DefineConstants"; "LangVersion"; "Nullable"
          "TreatWarningsAsErrors"; "NoWarn"; "GenerateDocumentationFile"; "SignAssembly"; "AssemblyOriginatorKeyFile"; "OtherFlags"
          "EnableDefaultItems"; "EnableDefaultCompileItems"; "DefaultItemExcludes"; "DefaultExcludesInProjectFolder"
          "IsPackable"; "PackageId"; "Description"; "Authors"; "Copyright"; "PackageTags"; "PackageLicenseExpression"
          "PackageProjectUrl"; "RepositoryType"; "RepositoryUrl"; "PackageIcon"; "PackageReadmeFile"
          "NetCoreRoot"; "MSBuildExtensionsPath"; "MSBuildSDKsPath"; "MSBuildBinPath"; "NuGetPackageRoot"; "BaseIntermediateOutputPath" ]
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
        let explicit =
            [ for file, doc in xmlFiles do
                for e in doc.Descendants () do
                    if e.Name.LocalName = "Compile" && not (e.Ancestors () |> Seq.exists (fun a -> a.Name.LocalName = "Target")) then
                        let attr n = match e.Attribute (XName.Get n) with null -> "" | a -> a.Value
                        if attr "Condition" <> "" || (e.Parent <> null && e.Parent.Attribute (XName.Get "Condition") <> null) then
                            dropped (sprintf "%s: the condition of a Compile item (its masks are kept unconditionally)" file)
                        for name, prefix in [ "Include", ""; "Exclude", "!"; "Remove", "!" ] do
                            for mask in split (attr name) do
                                if mask.Contains "$(" || mask.Contains "@(" || mask.Contains "%(" then
                                    dropped (sprintf "%s: Compile %s=\"%s\" (not a plain mask)" file name mask)
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
    let internal describe (descriptionDir: string) (outer: Dump) (inner: (string * Dump) list)
                          (isSdkFile: string -> bool) (xmlFiles: (string * XDocument) list) (previous: Description.Project option) =
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
        let perFramework = inner |> List.map (fun (tfm, d) -> tfm, (definesOf d, referencesOf d, packagesOf tfm d))
        let common pick = perFramework |> List.map (snd >> pick) |> List.reduce (fun a b -> a |> List.filter (fun x -> List.contains x b))
        let defines, references, packages = common (fun (d, _, _) -> d), common (fun (_, r, _) -> r), common (fun (_, _, p) -> p)
        let byFramework =
            if inner.Length < 2 then []
            else
                perFramework |> List.choose (fun (tfm, (d, r, p)) ->
                    let extra all common = all |> List.filter (fun x -> not (List.contains x common))
                    let i : Description.Items = { Defines = extra d defines; References = extra r references; Packages = extra p packages; Args = [] }
                    if i.Defines.IsEmpty && i.References.IsEmpty && i.Packages.IsEmpty then None else Some (tfm, i))

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
        let cultures =
            lazy (Globalization.CultureInfo.GetCultures Globalization.CultureTypes.AllCultures
                  |> Array.map (fun c -> c.Name) |> Array.filter ((<>) "") |> set)
        let resources =
            items first "EmbeddedResource" |> List.choose (fun (id, m) ->
                let file = slash id
                let culture =
                    let n = Path.GetFileNameWithoutExtension file
                    match n.LastIndexOf '.' with
                    | -1 -> None
                    | i -> let c = n.Substring (i + 1) in if cultures.Value.Contains c then Some c else None
                let logical = meta "LogicalName" m
                let dependentUpon = meta "DependentUpon" m
                let isResx = file.EndsWith (".resx", StringComparison.OrdinalIgnoreCase)
                match culture with
                | Some c when meta "WithCulture" m <> "false" ->
                    drop (sprintf "%s: EmbeddedResource %s (culture %s: a satellite assembly, not built)" name file c); None
                | _ ->
                    // msbuild names a resx after the class of its DependentUpon source (by convention
                    // the `.cs` of the same name beside it); the import does not read C#
                    let besideSource =
                        isResx && language = Description.CSharp
                        && File.Exists (Path.Combine (projectDir, Path.ChangeExtension (file, ".cs")))
                    if logical = "" && (dependentUpon <> "" || besideSource) then
                        drop (sprintf "%s: EmbeddedResource %s is named after the class of %s (DependentUpon); the description takes the default name, set its `name`"
                                  name file (if dependentUpon <> "" then dependentUpon else Path.ChangeExtension (Path.GetFileName file, ".cs")))
                    Some ({ File = file; Name = match logical with "" -> None | n -> Some n } : Description.Resource))

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

    /// Imports `project` (a csproj or fsproj, relative to the project root or absolute) into
    /// the description at `descriptionOf project`, msbuild evaluating it under `globals` (e.g.
    /// `["Configuration", "Release"; "Brand", "MESCIUS"]`). The file is written only when its
    /// content changes; an existing file's `$schema` is kept. Records no input: an explicit
    /// command, not a rule (`Imported.Inputs` is the list of files msbuild read, for a
    /// fingerprint).
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
            let evaluate (framework: string option) =
                recipe {
                    let tf = match framework with Some f -> [ "-p:TargetFramework=" + f ] | None -> []
                    let tag = defaultArg framework "outer"
                    let dump, pp = temp </> tag + ".json", temp </> tag + ".pp"
                    do! Project.runMsbuild [] ([ projectFile; "-nologo"; "-verbosity:quiet" ] @ switches @ tf
                                               @ [ "-getProperty:" + String.concat "," properties
                                                   "-getItem:" + String.concat "," itemTypes
                                                   "-getResultOutputFile:" + dump ]) projectFile
                    do! Project.runMsbuild [] ([ projectFile; "-nologo" ] @ switches @ tf @ [ "-pp:" + pp ]) projectFile
                    return readDump dump, Project.parseImports (File.ReadAllText pp)
                }
            let! outer, outerImports = evaluate None
            let declared =
                match split (prop outer "TargetFrameworks") with
                | [] -> split (prop outer "TargetFramework")
                | fs -> fs
            if declared.IsEmpty then failwithf "'%s': msbuild reports no TargetFramework(s)" project
            let inner = ResizeArray<string * Dump> ()
            let imports = ResizeArray<string> (outerImports)
            if prop outer "TargetFrameworks" = "" then inner.Add (declared.Head, outer)
            else
                for f in declared do
                    let! d, i = evaluate (Some f)
                    inner.Add (f, d)
                    imports.AddRange i
            Directory.Delete (temp, true)

            // what is not the repository's: the SDK, packages, restore's output
            let under (dirs: string list) (path: string) =
                let path = slash path
                dirs |> List.exists (fun d -> d <> "" && path.StartsWith ((slash d).TrimEnd '/' + "/", comparison))
            let sdkDirs = [ for n in [ "NetCoreRoot"; "MSBuildExtensionsPath"; "MSBuildSDKsPath"; "MSBuildBinPath" ] -> prop outer n ]
            let isSdkFile = under (sdkDirs @ [ prop outer "NuGetPackageRoot"; packageRoot ])
            let obj =
                let b = slash (prop outer "BaseIntermediateOutputPath")
                if b = "" then "" elif Path.IsPathRooted b then b else Path.Combine (prop outer "MSBuildProjectDirectory", b)
            let repoFiles = List.ofSeq imports |> List.map slash |> List.distinct |> List.filter (fun f -> not (isSdkFile f) && not (under [ obj ] f))
            let xmlFiles = repoFiles |> List.map (fun f -> relativeTo root f, XDocument.Load f)

            let previous = if File.Exists descriptionFile then Some (Description.read descriptionFile) else None
            let description, dropped = describe (Path.GetDirectoryName descriptionFile) outer (List.ofSeq inner) isSdkFile xmlFiles previous
            let written = Description.write descriptionFile description
            do! trace Info "%s '%s'" (if written then "wrote" else "unchanged:") (relativeTo root descriptionFile)
            for d in dropped do do! trace Info "  not in the description: %s" d
            return ({ Project = relativeTo root projectFile
                      Description = relativeTo root descriptionFile
                      Written = written
                      Inputs = repoFiles |> List.map (relativeTo root) |> List.sortWith (fun a b -> String.CompareOrdinal (a, b))
                      Dropped = dropped } : Imported)
        }

    /// `project` for each of `projects`, in order.
    let descriptions (projects: string list) (descriptionOf: string -> string) (globals: (string * string) list) : Recipe<ExecContext, Imported list> =
        recipe {
            let result = ResizeArray<Imported> ()
            for p in projects do
                let! imported = project p descriptionOf globals
                result.Add imported
            return List.ofSeq result
        }
