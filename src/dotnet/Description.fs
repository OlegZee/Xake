namespace Xake.Dotnet

open System.IO

open Xake

/// What a project is (`xake.project.json`), as data: name, language, the project file,
/// frameworks, sources, references, packages, options and package metadata. Rarely changes;
/// written by an explicit import (Xake.Hermetic.Dotnet) or by hand, committed, read by the
/// build: `csc { project "path" }` / `fsc { project "path" }` compose a compilation from it
/// (`ProjectPlan`). No placeholders: a variant msbuild evaluates differently is a separate
/// description. Schema: `docs/schemas/xake.project.schema.json`, page `docs/description.md`.
///
/// `project` is relative to the description; every other path is relative to the project
/// file's folder. Nothing here resolves paths or reads the project file.
module Description =

    type Language = FSharp | CSharp

    type Kind = Library | Exe

    /// A package reference as the project file declares it; `Version` is exact or a NuGet
    /// range, resolved at lock generation, never by the compiler. `Private`: compile-time only.
    type Package = { Id: string; Version: string option; Private: bool }

    /// An embedded resource: a plain file, or a `.resx` that `project` compiles to a
    /// `.resources` first. `Name` is the manifest name (msbuild's `LogicalName`, or the
    /// `ManifestResourceName` msbuild gave it, when that is not the default); `None` is
    /// msbuild's default (`ProjectPlan.manifestName`).
    type Resource = { File: string; Name: string option }

    /// The lists a framework can add to (`byFramework`), also the project-wide ones.
    /// `Resources`: the embedded resources only this framework has (an `EmbeddedResource` under
    /// a condition on the framework). `FrameworkReferences`: shared frameworks (msbuild's
    /// `FrameworkReference` items), e.g. `Microsoft.AspNetCore.App`.
    type Items =
        { Defines: string list; References: string list; FrameworkReferences: string list
          Packages: Package list; Args: string list; Resources: Resource list }

    /// `NoWarn`: the warnings the project suppresses beyond the SDK's own defaults (C#'s
    /// `1701;1702`), which `project` adds back.
    type Options =
        { Doc: bool; LangVersion: string option; WarningsAsErrors: bool; NoWarn: string list
          Nullable: string option; KeyFile: string option; Args: string list }
        static member Default =
            { Doc = false; LangVersion = None; WarningsAsErrors = false; NoWarn = []; Nullable = None; KeyFile = None; Args = [] }

    /// An assembly-level attribute (msbuild's `AssemblyAttribute` item other than
    /// `InternalsVisibleTo`): the attribute type and its arguments as source text, emitted
    /// verbatim (`["true"]`, `["\"text\""]`, `["Name = 1"]`).
    type Attribute = { Type: string; Args: string list }

    /// An `InternalsVisibleTo` friend; written as a plain string when it has no key.
    type Friend = { Name: string; Key: string option }

    type Repository = { Type: string option; Url: string option }

    /// Package metadata for packing; present when the project is packed.
    type Metadata =
        { Id: string; Description: string option; Authors: string option; Copyright: string option
          Tags: string list; License: string option; ProjectUrl: string option
          Repository: Repository option; Icon: string option; Readme: string option
          /// projectRefs whose assemblies go into this package instead of becoming dependencies
          Bundle: string list }

    /// One project description. `Schema` is the file's `$schema`, kept so a round trip is exact.
    type Project =
        { Schema: string option
          Name: string
          Project: string
          AssemblyName: string option
          RootNamespace: string option
          Language: Language
          Kind: Kind
          Frameworks: string list
          Sources: string list
          Resources: Resource list
          Defines: string list
          References: string list
          FrameworkReferences: string list
          ProjectRefs: string list
          Packages: Package list
          Options: Options
          InternalsVisibleTo: Friend list
          AssemblyAttributes: Attribute list
          ByFramework: (string * Items) list
          Package: Metadata option }

    /// The conventional file name of a description beside its project file.
    [<Literal>]
    let FileName = "xake.project.json"

    // ---- reading ------------------------------------------------------------------------

    open Json

    /// The fields of each kind of object, in the order `format` writes them. The schema
    /// (docs/schemas/xake.project.schema.json) lists the same; a test keeps the two in step.
    let internal fields =
        Map.ofList
            [ "project", [ "$schema"; "name"; "project"; "assemblyName"; "rootNamespace"; "language"; "kind"; "frameworks"
                           "sources"; "resources"; "defines"; "references"; "frameworkReferences"; "projectRefs"; "packages"; "options"
                           "internalsVisibleTo"; "assemblyAttributes"; "byFramework"; "package" ]
              "package", [ "id"; "version"; "private" ]
              "items", [ "defines"; "references"; "frameworkReferences"; "packages"; "args"; "resources" ]
              "resource", [ "file"; "name" ]
              "options", [ "doc"; "langVersion"; "warningsAsErrors"; "noWarn"; "nullable"; "keyFile"; "args" ]
              "friend", [ "name"; "key" ]
              "attribute", [ "type"; "args" ]
              "metadata", [ "id"; "description"; "authors"; "copyright"; "tags"; "license"; "projectUrl"; "repository"
                            "icon"; "readme"; "bundle" ]
              "repository", [ "type"; "url" ] ]

    let private at (parent: string) (name: string) = if parent = "" then name else parent + "." + name
    let private fail (path: string) message = failwithf "'%s': %s" path message

    /// The members of an object, failing on anything but an object, an unknown or a repeated field.
    let private members path (known: string list) value =
        match value with
        | JObject ms ->
            ms |> List.iteri (fun i (k, _) ->
                if not (List.contains k known) then fail (at path k) "unknown field"
                if ms |> List.take i |> List.exists (fst >> (=) k) then fail (at path k) "repeated field")
            ms
        | _ -> fail (if path = "" then "(root)" else path) "expected an object"

    let private str path = function JString s -> s | _ -> fail path "expected a string"
    let private bool path = function JOther "true" -> true | JOther "false" -> false | _ -> fail path "expected true or false"
    let private list path item = function
        | JArray items -> items |> List.mapi (fun i v -> item (sprintf "%s[%d]" path i) v)
        | _ -> fail path "expected an array"
    let private strs path = list path str

    let private opt ms parent name read = ms |> List.tryPick (fun (k, v) -> if k = name then Some (read (at parent name) v) else None)
    let private req ms parent name read = match opt ms parent name read with Some v -> v | None -> fail (at parent name) "required"
    let private orEmpty ms parent name read = opt ms parent name read |> Option.defaultValue []

    let private package path v =
        let ms = members path fields.["package"] v
        { Id = req ms path "id" str; Version = opt ms path "version" str
          Private = opt ms path "private" bool |> Option.defaultValue false }

    let private resource path v =
        let rs = members path fields.["resource"] v
        { File = req rs path "file" str; Name = opt rs path "name" str }

    let private items path ms =
        { Defines = orEmpty ms path "defines" strs; References = orEmpty ms path "references" strs
          FrameworkReferences = orEmpty ms path "frameworkReferences" strs
          Packages = orEmpty ms path "packages" (fun p -> list p package); Args = orEmpty ms path "args" strs
          Resources = orEmpty ms path "resources" (fun p -> list p resource) }

    let private options path v =
        let ms = members path fields.["options"] v
        { Doc = opt ms path "doc" bool |> Option.defaultValue false
          LangVersion = opt ms path "langVersion" str
          WarningsAsErrors = opt ms path "warningsAsErrors" bool |> Option.defaultValue false
          NoWarn = orEmpty ms path "noWarn" strs
          Nullable =
            opt ms path "nullable" (fun p v ->
                match str p v with
                | "enable" | "disable" | "warnings" | "annotations" as n -> n
                | n -> fail p (sprintf "'%s' is not one of enable, disable, warnings, annotations" n))
          KeyFile = opt ms path "keyFile" str
          Args = orEmpty ms path "args" strs }

    let private friend path = function
        | JString name -> { Name = name; Key = None }
        | v -> let ms = members path fields.["friend"] v in { Name = req ms path "name" str; Key = opt ms path "key" str }

    let private metadata path v =
        let ms = members path fields.["metadata"] v
        let s name = opt ms path name str
        { Id = req ms path "id" str; Description = s "description"; Authors = s "authors"; Copyright = s "copyright"
          Tags = orEmpty ms path "tags" strs; License = s "license"; ProjectUrl = s "projectUrl"
          Repository =
            opt ms path "repository" (fun p v ->
                let rs = members p fields.["repository"] v
                { Type = opt rs p "type" str; Url = opt rs p "url" str })
          Icon = s "icon"; Readme = s "readme"; Bundle = orEmpty ms path "bundle" strs }

    let private ofJson (root: Value) : Project =
        let ms =
            members "" fields.["project"] root
        let s name = opt ms "" name str
        let d =
            { Schema = s "$schema"
              Name = req ms "" "name" (fun p v -> match str p v with "" -> fail p "must not be empty" | n -> n)
              Project = req ms "" "project" str
              AssemblyName = s "assemblyName"
              RootNamespace = s "rootNamespace"
              Language =
                req ms "" "language" (fun p v ->
                    match str p v with
                    | "fsharp" -> FSharp | "csharp" -> CSharp
                    | l -> fail p (sprintf "'%s' is not one of fsharp, csharp" l))
              Kind =
                opt ms "" "kind" (fun p v ->
                    match str p v with
                    | "library" -> Library | "exe" -> Exe
                    | k -> fail p (sprintf "'%s' is not one of library, exe" k))
                |> Option.defaultValue Library
              Frameworks = req ms "" "frameworks" (fun p v -> match strs p v with [] -> fail p "needs at least one framework" | f -> f)
              Sources = req ms "" "sources" strs
              Resources = orEmpty ms "" "resources" (fun p -> list p resource)
              Defines = orEmpty ms "" "defines" strs
              References = orEmpty ms "" "references" strs
              FrameworkReferences = orEmpty ms "" "frameworkReferences" strs
              ProjectRefs = orEmpty ms "" "projectRefs" strs
              Packages = orEmpty ms "" "packages" (fun p -> list p package)
              Options = opt ms "" "options" options |> Option.defaultValue Options.Default
              InternalsVisibleTo = orEmpty ms "" "internalsVisibleTo" (fun p -> list p friend)
              AssemblyAttributes =
                orEmpty ms "" "assemblyAttributes" (fun p -> list p (fun p v ->
                    let ats = members p fields.["attribute"] v
                    { Type = req ats p "type" str; Args = orEmpty ats p "args" strs }))
              ByFramework =
                orEmpty ms "" "byFramework" (fun p v ->
                    let tfms = match v with JObject fs -> List.map fst fs | _ -> []
                    members p tfms v |> List.map (fun (tfm, i) -> let ip = at p tfm in tfm, items ip (members ip fields.["items"] i)))
              Package = opt ms "" "package" metadata }
        d.ByFramework |> List.iter (fun (tfm, _) ->
            if not (List.contains tfm d.Frameworks) then fail (at "byFramework" tfm) "not one of the frameworks")
        d.Package |> Option.iter (fun m -> m.Bundle |> List.iteri (fun i b ->
            if not (List.contains b d.ProjectRefs) then fail (sprintf "package.bundle[%d]" i) (sprintf "'%s' is not one of the projectRefs" b)))
        d

    /// Parses and validates a description. A failure message starts with the offending field,
    /// e.g. `'packages[0].id': required`.
    let parse (text: string) : Project =
        let json =
            try Json.parse text
            with e -> failwithf "malformed json: %s" e.Message
        ofJson json

    /// `parse` of a file, outside a recipe; failures are prefixed with the file name.
    let read (path: string) : Project =
        try System.IO.File.ReadAllText path |> parse
        with e -> failwithf "%s: %s" path e.Message

    // ---- writing ------------------------------------------------------------------------

    /// Lines are at most this wide where a value fits; a longer value breaks one item per line.
    let private width = 100

    let rec private inlined = function
        | JString s -> escape s
        | JOther s -> s
        | JArray items -> "[" + (items |> List.map inlined |> String.concat ", ") + "]"
        | JObject [] -> "{}"
        | JObject ms -> "{ " + (ms |> List.map (fun (k, v) -> escape k + ": " + inlined v) |> String.concat ", ") + " }"

    /// `prefix` is what precedes the value on its line (indent and key), for the width check.
    let rec private render (indent: string) (prefix: string) (force: bool) (value: Value) =
        let flat = inlined value
        let inner = indent + "  "
        match value with
        | JArray (_ :: _ as items) when force || prefix.Length + flat.Length > width ->
            let body = items |> List.map (fun v -> inner + render inner inner false v) |> String.concat ",\n"
            "[\n" + body + "\n" + indent + "]"
        | JObject (_ :: _ as ms) when force || prefix.Length + flat.Length > width ->
            let body =
                ms |> List.map (fun (k, v) -> let p = inner + escape k + ": " in p + render inner p false v)
                |> String.concat ",\n"
            "{\n" + body + "\n" + indent + "}"
        | _ -> flat

    let private toJson (d: Project) : Value =
        let s (v: string) = JString v
        let strs (l: string list) = JArray (List.map s l)
        let field name = Option.map (fun v -> name, v)
        let some name v = Some (name, v)
        let maybe name = Option.map (fun v -> name, s v)
        let nonEmpty name l = match l with [] -> None | l -> Some (name, JArray l)
        let flag name b = if b then Some (name, JOther "true") else None
        let package (p: Package) =
            JObject (List.choose id [ some "id" (s p.Id); maybe "version" p.Version; flag "private" p.Private ])
        let resource (r: Resource) = JObject (List.choose id [ some "file" (s r.File); maybe "name" r.Name ])
        let items (i: Items) =
            [ nonEmpty "defines" (List.map s i.Defines); nonEmpty "references" (List.map s i.References)
              nonEmpty "frameworkReferences" (List.map s i.FrameworkReferences)
              nonEmpty "packages" (List.map package i.Packages); nonEmpty "args" (List.map s i.Args)
              nonEmpty "resources" (List.map resource i.Resources) ]
        let o = d.Options
        let options =
            if o = Options.Default then None
            else
                Some ("options", JObject (List.choose id
                        [ flag "doc" o.Doc; maybe "langVersion" o.LangVersion; flag "warningsAsErrors" o.WarningsAsErrors
                          nonEmpty "noWarn" (List.map s o.NoWarn)
                          maybe "nullable" o.Nullable; maybe "keyFile" o.KeyFile; nonEmpty "args" (List.map s o.Args) ]))
        let friend (f: Friend) =
            match f.Key with
            | None -> s f.Name
            | Some key -> JObject [ "name", s f.Name; "key", s key ]
        let metadata (m: Metadata) =
            JObject (List.choose id
                [ some "id" (s m.Id); maybe "description" m.Description; maybe "authors" m.Authors
                  maybe "copyright" m.Copyright; nonEmpty "tags" (List.map s m.Tags); maybe "license" m.License
                  maybe "projectUrl" m.ProjectUrl
                  m.Repository |> field "repository" |> Option.map (fun (k, r) ->
                      k, JObject (List.choose id [ maybe "type" r.Type; maybe "url" r.Url ]))
                  maybe "icon" m.Icon; maybe "readme" m.Readme; nonEmpty "bundle" (List.map s m.Bundle) ])
        JObject (List.choose id
            [ maybe "$schema" d.Schema
              some "name" (s d.Name)
              some "project" (s d.Project)
              maybe "assemblyName" d.AssemblyName
              maybe "rootNamespace" d.RootNamespace
              some "language" (s (match d.Language with FSharp -> "fsharp" | CSharp -> "csharp"))
              some "kind" (s (match d.Kind with Library -> "library" | Exe -> "exe"))
              some "frameworks" (strs d.Frameworks)
              some "sources" (strs d.Sources)
              nonEmpty "resources" (List.map resource d.Resources)
              nonEmpty "defines" (List.map s d.Defines)
              nonEmpty "references" (List.map s d.References)
              nonEmpty "frameworkReferences" (List.map s d.FrameworkReferences)
              nonEmpty "projectRefs" (List.map s d.ProjectRefs)
              nonEmpty "packages" (List.map package d.Packages)
              options
              nonEmpty "internalsVisibleTo" (List.map friend d.InternalsVisibleTo)
              nonEmpty "assemblyAttributes" (d.AssemblyAttributes |> List.map (fun a ->
                  JObject (List.choose id [ some "type" (s a.Type); nonEmpty "args" (List.map s a.Args) ])))
              (match d.ByFramework with
               | [] -> None
               | fs -> Some ("byFramework", JObject (fs |> List.map (fun (tfm, i) -> tfm, JObject (List.choose id (items i))))))
              d.Package |> Option.map (fun m -> "package", metadata m) ])

    /// The text `save` writes: fields in a fixed order, absent and empty ones left out, a value
    /// on one line when it fits in 100 columns, LF line ends, a final newline.
    let format (d: Project) : string = render "" "" true (toJson d) + "\n"

    let private normalize (text: string) = text.Replace ("\r\n", "\n")

    /// Writes `d` to `path` unless the file already holds the same text (line ends aside), so
    /// an unchanged description keeps its timestamp. Returns whether the file was written.
    let write (path: string) (d: Project) : bool =
        let text = format d
        if System.IO.File.Exists path && normalize (System.IO.File.ReadAllText path) = text then false
        else
            let dir = Path.GetDirectoryName (Path.GetFullPath path)
            Directory.CreateDirectory dir |> ignore
            System.IO.File.WriteAllText (path, text)
            true

    // ---- recipes ------------------------------------------------------------------------

    let private fullPath (path: string) =
        recipe {
            let! options = getCtxOptions ()
            return if Path.IsPathRooted path then path else options.ProjectRoot </> path
        }

    /// Reads a description, resolved against the build's project root, and depends on it.
    let load (path: string) : Recipe<ExecContext, Project> =
        recipe {
            let! full = fullPath path
            do! needFiles (Filelist [ File.make full ])
            return read full
        }

    /// `write` from a recipe: the path is resolved against the build's project root. Writing
    /// is not depending: no input is recorded.
    let save (path: string) (d: Project) : Recipe<ExecContext, unit> =
        recipe {
            let! full = fullPath path
            write full d |> ignore
        }

    /// The description named `name` among the files of `descriptions`. Only the matching file
    /// becomes an input of the calling recipe (one `FileDep`); the others are read to find it
    /// but not depended on, so editing another project's description does not rebuild this one.
    /// Fails when no file or more than one file describes `name`.
    let find (descriptions: Fileset) (name: string) : Recipe<ExecContext, Project> =
        recipe {
            let! options = getCtxOptions ()
            let (Filelist files) = descriptions |> Fileset.toFileList options.ProjectRoot
            let matching =
                files |> List.choose (fun f -> let d = read f.FullName in if d.Name = name then Some (f, d) else None)
            match matching with
            | [ f, d ] ->
                do! needFiles (Filelist [ f ])
                return d
            | [] when files.IsEmpty -> return failwithf "no description named '%s': the fileset matches no file" name
            | [] ->
                return failwithf "no description named '%s' among %s" name
                                 (files |> List.map (fun f -> f.FullName) |> String.concat ", ")
            | many ->
                return failwithf "'%s' is described by more than one file: %s" name
                                 (many |> List.map (fun (f, _) -> f.FullName) |> String.concat ", ")
        }

    /// The project names of `descriptions`, in order.
    let names (descriptions: Project list) : string list = descriptions |> List.map (fun d -> d.Name)

    /// The conventional description of a project file: `xake.project.json` in the same folder.
    let beside (projectFile: string) : string = Path.Combine (Path.GetDirectoryName projectFile, FileName)
