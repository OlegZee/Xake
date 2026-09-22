namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The C# compiler's command line, as data. The import takes the exact invocation msbuild
/// would have made and the compile step replays it, so both sides need to know which of the
/// arguments name files: those get made absolute and tokenized when the lock is written, and
/// they are the inputs the compile step depends on.
module CscArgs =

    /// One argument: a source file, or a switch with its (possibly empty) value.
    type Arg =
        | Source of string
        | Switch of name: string * value: string

    /// `/reference:a.dll` -> Switch ("reference", "a.dll"); the name is lowercased and the
    /// `+`/`-` of a boolean switch stays with it (`/optimize+` -> Switch ("optimize+", "")).
    let parse (arg: string) =
        // an absolute Unix path also starts with '/': it has a second slash before any colon
        let head = match arg.IndexOf ':' with | -1 -> arg | i -> arg.Substring (0, i)
        if arg.Length > 1 && arg.[0] = '/' && head.IndexOf ('/', 1) < 0 then
            match arg.IndexOf ':' with
            | -1 -> Switch (arg.Substring(1).ToLowerInvariant(), "")
            | i -> Switch (arg.Substring(1, i - 1).ToLowerInvariant(), arg.Substring (i + 1))
        else Source arg

    let private aliases =
        [ "r", "reference"; "a", "analyzer"; "res", "resource"; "linkres", "linkresource"
          "l", "link"; "d", "define"; "lib", "lib" ] |> Map.ofList

    /// Switch names with their aliases folded.
    let canonical name = aliases |> Map.tryFind name |> Option.defaultValue name

    /// How a switch's value is shaped, for the ones whose value names files.
    type private Shape =
        /// one path
        | Path
        /// comma-separated paths; a reference may carry an `alias=` prefix
        | PathList
        /// `path,name,...` -- only the first element is a path
        | PathFirst

    let private shapes =
        [ "out", Path; "doc", Path; "keyfile", Path; "ruleset", Path; "sourcelink", Path
          "pdb", Path; "refout", Path; "win32icon", Path; "win32res", Path; "win32manifest", Path
          "appconfig", Path; "generatedfilesout", Path; "touchedfiles", Path; "analyzerconfig", Path
          "reference", PathList; "analyzer", PathList; "additionalfile", PathList; "embed", PathList
          "link", PathList; "addmodule", PathList; "lib", PathList
          "resource", PathFirst; "linkresource", PathFirst; "errorlog", PathFirst ]
        |> Map.ofList

    /// Switches that name the compiler's inputs, with the value's file path(s).
    let private inputSwitches =
        set [ "reference"; "analyzer"; "additionalfile"; "embed"; "link"; "addmodule"; "keyfile"
              "ruleset"; "analyzerconfig"; "resource"; "linkresource"; "win32icon"; "win32res"
              "win32manifest"; "appconfig" ]

    let private outputSwitches = set [ "out"; "doc"; "refout"; "pdb"; "errorlog"; "generatedfilesout"; "touchedfiles" ]

    /// Splits a switch value on ',' the way csc itself does: a comma inside a `"..."` quoted
    /// segment does not split (msbuild quotes a path in a list when the path itself contains a
    /// comma, e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs` a netstandard
    /// project embeds); the quotes themselves are not part of the path and are dropped.
    let private splitList (value: string) =
        let items = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let mutable inQuotes = false
        for ch in value do
            match ch with
            | '"' -> inQuotes <- not inQuotes
            | ',' when not inQuotes -> items.Add (current.ToString()); current.Clear() |> ignore
            | c -> current.Append c |> ignore
        items.Add (current.ToString())
        List.ofSeq items

    /// Re-quotes a path list item if joining it back with ',' would make it split again where
    /// it did not before -- the inverse of `splitList`.
    let private quoteIfNeeded (s: string) = if s.Contains "," then "\"" + s + "\"" else s

    /// A `/reference:` (etc.) list item may be `alias=path`: the alias is a short identifier
    /// with no path separator in it, always at the very start, so an `=` is only the alias
    /// marker when it comes before the first `/` -- a bare path may itself contain an `=` after
    /// one (e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs`, once its
    /// comma-hiding quotes are gone).
    let private aliasSplitIndex (p: string) =
        match p.IndexOf '=' with
        | -1 -> -1
        | i -> match p.IndexOf '/' with | slash when slash >= 0 && slash < i -> -1 | _ -> i

    /// Paths named by one argument.
    let paths arg =
        match arg with
        | Source path -> [path]
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> [value]
            | Some PathList ->
                splitList value |> List.filter ((<>) "")
                |> List.map (fun p -> match aliasSplitIndex p with | -1 -> p | i -> p.Substring (i + 1))
            | Some PathFirst -> [(splitList value).[0]]
            | None -> []

    /// Rewrites every path an argument names, leaving the rest of it as it is.
    let mapPaths (f: string -> string) arg =
        match arg with
        | Source path -> Source (f path)
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> Switch (name, f value)
            | Some PathList ->
                let mapped =
                    splitList value |> List.map (fun p ->
                        if p = "" then p else
                        (match aliasSplitIndex p with
                         | -1 -> f p
                         | i -> p.Substring (0, i + 1) + f (p.Substring (i + 1)))
                        |> quoteIfNeeded)
                Switch (name, System.String.Join (",", mapped))
            | Some PathFirst ->
                let parts = splitList value |> List.mapi (fun i p -> if i = 0 then quoteIfNeeded (f p) else p)
                Switch (name, System.String.Join (",", parts))
            | None -> arg

    let format = function
        | Source path -> path
        | Switch (name, "") -> "/" + name
        | Switch (name, value) -> sprintf "/%s:%s" name value

    /// The files a command line reads: the sources and everything an input switch names.
    let inputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Source path -> [path]
            | Switch (name, _) as arg when inputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    /// The files a command line writes.
    let outputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Switch (name, _) as arg when outputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    let sources (args: string list) =
        args |> List.choose (parse >> function | Source path -> Some path | _ -> None)

    let switchValues name (args: string list) =
        args |> List.choose (parse >> function
            | Switch (n, _) as arg when canonical n = name -> Some (paths arg)
            | _ -> None) |> List.concat

    /// Makes every path relative to `dir` absolute, with forward slashes.
    let absolutize (dir: string) (args: string list) =
        let absolute (p: string) =
            let p = p.Replace ('\\', '/')
            if p = "" then p
            else System.IO.Path.GetFullPath (System.IO.Path.Combine (dir, p)) |> fun p -> p.Replace ('\\', '/')
        args |> List.map (parse >> mapPaths absolute >> format)

/// The imported compilation of a project: exactly what `dotnet build` would have handed the
/// compiler, plus the identity (path and SHA-256) of everything the compiler would read that
/// is not in the repository -- packages, analyzers, the compiler itself, the msbuild files
/// that produced the answer. One lock file per (framework, variant) holds one entry per
/// project.
module Lock =

    /// A file the build reads that is not source: where it is and what it was.
    type Hashed = {
        Path: string
        /// Lowercase hex SHA-256, or empty when the file did not exist at import time (a
        /// project reference points at the referenced project's own output, not built yet)
        Sha256: string
    }

    type Compiler = {
        /// "csc" or "fsc"
        Tool: string
        /// The compiler assembly, `csc.dll` under the SDK's Roslyn directory unless the project
        /// pins a compiler package
        Path: string
        Sha256: string
        /// The SDK that evaluated the project (`NETCoreSdkVersion`)
        Sdk: string
    }

    type Project = {
        /// AssemblyName
        Name: string
        /// The project file
        Project: string
        /// The project's directory: the compiler's working directory in `dotnet build`, and
        /// what relative arguments were resolved against
        Directory: string
        Compiler: Compiler
        /// The verbatim command line, paths absolute
        Args: string list
        /// Every assembly referenced, hashed
        References: Hashed list
        Analyzers: Hashed list
        /// ProjectReference items, as project files
        ProjectRefs: string list
        /// The msbuild files whose evaluation produced this, outside the SDK
        Imports: Hashed list
        /// Inputs msbuild generated during the import (assembly attributes, the editorconfig
        /// it derives from properties), by path, with their content: they depend on the
        /// commit and the properties, and are small
        Generated: (string * string) list
        /// `.resx` files this project embeds, compiled by `PrepareResources`: (resx path,
        /// `.resources` output path), both absolute. `run` regenerates the output from the
        /// resx (via `Xake.Dotnet.Resx`) whenever it is missing or older than the resx, so a
        /// machine with only the lock -- or a cleaned `obj/` -- can still reproduce the exact
        /// input the recorded `/resource:` switch names.
        Resources: (string * string) list
        Properties: Map<string, string>
    } with
        member this.Sources = CscArgs.sources this.Args
        member this.Output = CscArgs.switchValues "out" this.Args |> List.tryHead

    type File = {
        Framework: string
        Configuration: string
        /// The properties the import ran with, e.g. `Brand`
        Properties: (string * string) list
        Projects: Project list
    }

    let internal sha256 (path: string) =
        if not (System.IO.File.Exists path) then "" else
        use stream = System.IO.File.OpenRead path
        use algo = System.Security.Cryptography.SHA256.Create()
        algo.ComputeHash stream |> Array.map (sprintf "%02x") |> String.concat ""

    let hashed path = { Path = path; Sha256 = sha256 path }

    /// Rewrites every path the project's command line, references, analyzers and generated
    /// files carry, through `f`. A build script uses this to point a project reference (the
    /// lock has it unhashed, at the referenced project's own `bin/Release/.../X.dll`) at the
    /// path the script itself produces that output at -- the rest of the lock, including the
    /// hashes that identify what was actually compiled against, stays as imported. A rewritten
    /// `Hashed` entry loses its hash: the hash on record was computed for the old path, and it
    /// would otherwise be checked against a different file.
    let mapPaths (f: string -> string) (project: Project) : Project =
        let rewriteHashed (h: Hashed) =
            let path = f h.Path
            if path = h.Path then h else { Path = path; Sha256 = "" }
        { project with
            Args = project.Args |> List.map (CscArgs.parse >> CscArgs.mapPaths f >> CscArgs.format)
            References = project.References |> List.map rewriteHashed
            Analyzers = project.Analyzers |> List.map rewriteHashed
            Generated = project.Generated |> List.map (fun (path, content) -> f path, content)
            Resources = project.Resources |> List.map (fun (resx, resources) -> f resx, f resources) }

    open Fsproj.Json

    /// Replaces a root anywhere in the string, not only as a prefix: `/pathmap:<root>=/_/`
    /// carries one in the middle.
    let internal tokenizeAll roots (text: string) =
        let text = text.Replace ('\\', '/')
        roots |> List.fold (fun (text: string) (token: string, root: string) ->
            // the root followed by a separator or the end, not a longer name with that prefix
            System.Text.RegularExpressions.Regex.Replace (
                text, System.Text.RegularExpressions.Regex.Escape root + "(?=[/=,;]|$)", token.Replace ("$", "$$"))) text

    let private writeProject roots (project: Project) =
        let str = tokenizeAll roots >> escape
        let strings name (items: string list) =
            items |> List.map (str >> sprintf "        %s") |> String.concat ",\n"
            |> sprintf "      %s: [\n%s\n      ]" (escape name)
        let hashedList name (items: Hashed list) =
            items
            |> List.map (fun h -> sprintf "        { \"Path\": %s, \"Sha256\": %s }" (str h.Path) (escape h.Sha256))
            |> String.concat ",\n"
            |> sprintf "      %s: [\n%s\n      ]" (escape name)
        let pairs name (items: (string * string) list) =
            items |> List.map (fun (k, v) -> sprintf "        %s: %s" (str k) (str v)) |> String.concat ",\n"
            |> sprintf "      %s: {\n%s\n      }" (escape name)

        [ sprintf "      \"Name\": %s" (escape project.Name)
          sprintf "      \"Project\": %s" (str project.Project)
          sprintf "      \"Directory\": %s" (str project.Directory)
          sprintf "      \"Compiler\": { \"Tool\": %s, \"Path\": %s, \"Sha256\": %s, \"Sdk\": %s }"
            (escape project.Compiler.Tool) (str project.Compiler.Path) (escape project.Compiler.Sha256) (escape project.Compiler.Sdk)
          strings "Args" project.Args
          hashedList "References" project.References
          hashedList "Analyzers" project.Analyzers
          strings "ProjectRefs" project.ProjectRefs
          hashedList "Imports" project.Imports
          pairs "Generated" project.Generated
          pairs "Resources" project.Resources
          pairs "Properties" (project.Properties |> Map.toList) ]
        |> String.concat ",\n" |> sprintf "    {\n%s\n    }"

    /// The lock as text: paths tokenized against the given roots, one line per argument, so the
    /// file is the same on every machine and a diff of two locks is the difference between two
    /// compilations. `roots` is the full list (built-in plus any extra a script declared, e.g.
    /// via `Fsproj.withRoots`), longest root first.
    let writeWith roots (lock: File) =
        [ sprintf "  \"Framework\": %s" (escape lock.Framework)
          sprintf "  \"Configuration\": %s" (escape lock.Configuration)
          sprintf "  \"Properties\": {\n%s\n  }"
            (lock.Properties |> List.map (fun (k, v) -> sprintf "    %s: %s" (escape k) (escape v)) |> String.concat ",\n")
          sprintf "  \"Projects\": [\n%s\n  ]" (lock.Projects |> List.map (writeProject roots) |> String.concat ",\n") ]
        |> String.concat ",\n" |> sprintf "{\n%s\n}\n"

    /// `writeWith` against the built-in roots only (package cache, project root, SDK).
    let write (lock: File) = writeWith (Fsproj.roots ()) lock

    let private readProject roots value =
        let expand = Fsproj.expand roots
        let str name = field name value |> Option.bind asString |> Option.defaultValue ""
        let strings name = field name value |> Option.map asArray |> Option.defaultValue [] |> List.choose asString |> List.map expand
        let hashedList name =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun h ->
                { Path = field "Path" h |> Option.bind asString |> Option.defaultValue "" |> expand
                  Sha256 = field "Sha256" h |> Option.bind asString |> Option.defaultValue "" })
        let pairs name =
            match field name value with
            | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> expand k, expand v))
            | _ -> []
        let compiler = field "Compiler" value |> Option.defaultValue (JObject [])
        let cstr name = field name compiler |> Option.bind asString |> Option.defaultValue ""
        {
            Name = str "Name"
            Project = str "Project" |> expand
            Directory = str "Directory" |> expand
            Compiler = { Tool = cstr "Tool"; Path = cstr "Path" |> expand; Sha256 = cstr "Sha256"; Sdk = cstr "Sdk" }
            Args = strings "Args"
            References = hashedList "References"
            Analyzers = hashedList "Analyzers"
            ProjectRefs = strings "ProjectRefs"
            Imports = hashedList "Imports"
            Generated = pairs "Generated"
            Resources = pairs "Resources"
            Properties = pairs "Properties" |> Map.ofList
        }

    /// Reads a lock `write`/`writeWith` produced, paths expanded for this machine. `roots` must
    /// be the same list (or a superset) used to write it, or a token stays untranslated.
    let parseWith roots (text: string) =
        let root = Fsproj.Json.parse text
        let str name = field name root |> Option.bind asString |> Option.defaultValue ""
        {
            Framework = str "Framework"
            Configuration = str "Configuration"
            Properties =
                match field "Properties" root with
                | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> k, v))
                | _ -> []
            Projects = field "Projects" root |> Option.map asArray |> Option.defaultValue [] |> List.map (readProject roots)
        }

    /// `parseWith` against the built-in roots only.
    let parse (text: string) = parseWith (Fsproj.roots ()) text

    let readWith roots (path: string) = System.IO.File.ReadAllText path |> parseWith roots

    let read (path: string) = System.IO.File.ReadAllText path |> parse

    /// The entry for one project, by assembly name or by project file name.
    let project (name: string) (lock: File) =
        lock.Projects |> List.tryFind (fun p ->
            p.Name = name || System.IO.Path.GetFileNameWithoutExtension p.Project = name)
        |> function
            | Some p -> p
            | None -> failwithf "project '%s' is not in the lock (%s)" name (lock.Projects |> List.map (fun p -> p.Name) |> String.concat ", ")

/// Imports a C# project the way Visual Studio learns a project's compiler switches: a
/// design-time build in which the compiler task is asked to report its command line instead
/// of running (`ProvideCommandLineArgs`, `SkipCompilerExecution`). Nothing about the
/// compilation is reconstructed; what msbuild would have run is what the lock holds.
module Project =

    /// How a project's SDK is selected, from `global.json` searched upwards from the project's
    /// directory the way the .NET host itself resolves it (`sdk.version` plus
    /// `sdk.rollForward`, default `latestPatch` when a version is set and the policy is
    /// absent). Only `Pinned` makes the SDK a fixed input: with anything else the exact SDK
    /// `dotnet` picks depends on what is installed on the machine, and the compiler recorded
    /// in the lock drifts with it.
    type SdkPin =
        | NoGlobalJson
        | Pinned of version: string
        | RollsForward of version: string * policy: string
        | NoVersion of file: string

    /// Walks up from `projectDir` looking for `global.json` and reads its `sdk.version` /
    /// `sdk.rollForward`. Pure: no msbuild involved.
    let sdkPin (projectDir: string) : SdkPin =
        let rec findGlobalJson (dir: string) =
            let path = Path.Combine (dir, "global.json")
            if File.Exists path then Some path
            else
                match Path.GetDirectoryName (dir: string) with
                | null | "" -> None
                | parent when parent = dir -> None
                | parent -> findGlobalJson parent
        match findGlobalJson (Path.GetFullPath projectDir) with
        | None -> NoGlobalJson
        | Some file ->
            let root = File.ReadAllText file |> Fsproj.Json.parse
            let sdk = Fsproj.Json.field "sdk" root
            match sdk |> Option.bind (Fsproj.Json.field "version") |> Option.bind Fsproj.Json.asString with
            | None -> NoVersion file
            | Some version ->
                match sdk |> Option.bind (Fsproj.Json.field "rollForward") |> Option.bind Fsproj.Json.asString with
                | Some "disable" -> Pinned version
                | Some policy -> RollsForward (version, policy)
                | None -> RollsForward (version, "latestPatch")

    /// The pin, as recorded in the lock's `Properties.["SdkPin"]` and quoted in trace messages.
    let internal sdkPinText = function
        | NoGlobalJson -> "none"
        | Pinned version -> "exact " + version
        | RollsForward (version, policy) -> sprintf "%s rollForward:%s" version policy
        | NoVersion file -> sprintf "no version (%s)" file

    type ImportOptions = {
        /// The project files. All of them land in one lock: what varies between projects of
        /// one framework and variant is small next to what they share
        Projects: string list
        /// Target framework the import runs for
        Framework: string
        Configuration: string
        /// What else selects the compilation, e.g. `["Brand", "MESCIUS"]`. Each distinct set
        /// needs its own `Variant` so the generated files do not overwrite each other's
        Properties: (string * string) list
        /// Names the obj subtree the import generates into (`obj/xake/<framework>/<variant>/`);
        /// the brand, typically. Empty when the framework alone selects the compilation
        Variant: string
        /// The lock file to write
        Output: string
        /// Extra roots to tokenize paths against, beyond the built-in three (`$(NuGetPackageRoot)`,
        /// `$(ProjectRoot)`, `$(DotnetRoot)`) -- one token per sibling repository, e.g.
        /// `["$(DataEngineRoot)", "/abs/path/to/dataengine"]` when a `Projects` entry or a
        /// project reference resolves outside `$(ProjectRoot)` (the current directory). See
        /// `Fsproj.withRoots`.
        Roots: (string * string) list
    } with static member Default = {
            Projects = []
            Framework = ""
            Configuration = "Release"
            Properties = []
            Variant = ""
            Output = ""
            Roots = []
        }

    /// `PrepareResources` runs resgen so the `/resource:` switches name real files;
    /// `Compile` (not `CoreCompile`) so that everything hooked before it -- generated
    /// assembly attributes, `BeforeCompile` extensions -- has run.
    let internal targets = "PrepareResources;Compile"
    let internal items = "CscCommandLineArgs,ReferencePath,Analyzer,ProjectReference,EmbeddedResource"
    let internal wantedProperties =
        "AssemblyName,MSBuildProjectFullPath,MSBuildProjectDirectory,IntermediateOutputPath,BaseIntermediateOutputPath,TargetPath," +
        "CscToolPath,CscToolExe,CSharpCoreTargetsPath,RoslynTargetsPath,NETCoreSdkVersion,NetCoreRoot,NuGetPackageRoot,ProjectAssetsFile," +
        "TargetFrameworkMoniker,LangVersion,Version,InformationalVersion,SignAssembly,AssemblyOriginatorKeyFile,Deterministic"

    /// Every file msbuild imported, from a preprocessed project (`-pp`): each import is
    /// announced by a banner with the file's path on the line above a rule of `=`.
    let internal parseImports (preprocessed: string) =
        let lines = preprocessed.Split '\n' |> Array.map (fun l -> l.TrimEnd '\r')
        [ for i in 0 .. lines.Length - 2 do
            let line = lines.[i].Trim()
            let next = lines.[i + 1].Trim()
            if line <> "" && next.Length > 10 && next |> Seq.forall ((=) '=')
               && not (line.StartsWith "<") && Path.IsPathRooted line then
                yield line ]
        |> List.distinct

    /// Builds the lock entry from what msbuild wrote. `pin` is the project's SDK pin (from
    /// `sdkPin`, computed separately since this function stays pure/no file walking of its
    /// own beyond the msbuild result and import list it is already given).
    let internal parseImport (resultFile: string) (imports: string list) (pin: SdkPin) =
        let root = File.ReadAllText resultFile |> Fsproj.Json.parse
        let items name =
            Fsproj.Json.field "Items" root |> Option.bind (Fsproj.Json.field name)
            |> Option.map Fsproj.Json.asArray |> Option.defaultValue []
        let identity item = Fsproj.Json.field "Identity" item |> Option.bind Fsproj.Json.asString |> Option.defaultValue ""
        let fullPath item =
            Fsproj.Json.field "FullPath" item |> Option.bind Fsproj.Json.asString |> Option.defaultValue (identity item)
        let metadata name item = Fsproj.Json.field name item |> Option.bind Fsproj.Json.asString |> Option.defaultValue ""
        let properties =
            match Fsproj.Json.field "Properties" root with
            | Some (Fsproj.Json.JObject members) ->
                members |> List.choose (fun (name, value) -> Fsproj.Json.asString value |> Option.map (fun v -> name, v)) |> Map.ofList
            | _ -> Map.empty
        let prop name = properties |> Map.tryFind name |> Option.defaultValue ""

        let directory = (prop "MSBuildProjectDirectory").Replace ('\\', '/')
        let args = items "CscCommandLineArgs" |> List.map identity |> CscArgs.absolutize directory
        let slash (p: string) = p.Replace ('\\', '/')

        // A project that pins the compiler via the `Microsoft.Net.Compilers.Toolset` package
        // does not set `CscToolPath`/`CscToolExe` -- that package only redirects
        // `CSharpCoreTargetsPath` (and the `Csc` task's assembly) to its own `tasks/<tfm>/`
        // directory, and the task's `ToolPath` defaults to a `bincore` folder next to whatever
        // targets file is driving it. So `CSharpCoreTargetsPath`'s own directory (not
        // `RoslynTargetsPath`, which the SDK always reports as its own Roslyn regardless of a
        // toolset override) is what actually tells the SDK csc.dll from the package's: for an
        // unpinned project it is `<sdk>/Roslyn/Microsoft.CSharp.Core.targets`, so this produces
        // the exact same path the old `RoslynTargetsPath </> "bincore" </> "csc.dll"` fallback
        // did; for a pinned one it is
        // `<nuget>/microsoft.net.compilers.toolset/<version>/build/../tasks/netcore/Microsoft.CSharp.Core.targets`,
        // so this resolves to the package's own compiler.
        let compilerPath =
            match prop "CscToolPath" with
            | "" ->
                match prop "CSharpCoreTargetsPath" with
                | "" -> prop "RoslynTargetsPath" </> "bincore" </> "csc.dll"
                | csTargets ->
                    // the package's path goes through `build/../tasks`: folded, so the lock
                    // names one spelling of the file
                    Path.GetFullPath (Path.GetDirectoryName (slash csTargets) </> "bincore" </> "csc.dll")
            | toolPath -> toolPath </> (match prop "CscToolExe" with | "" -> "csc.dll" | exe -> exe)
            |> slash

        let absoluteDir (dir: string) =
            let dir = slash dir
            (if Path.IsPathRooted dir then dir else Path.GetFullPath (Path.Combine (directory, dir)) |> slash).TrimEnd '/' + "/"
        let intermediate = prop "IntermediateOutputPath" |> absoluteDir
        // where restore writes its props and targets
        let baseIntermediate = (match prop "BaseIntermediateOutputPath" with | "" -> "obj/" | dir -> dir) |> absoluteDir

        // `PrepareResources` compiles every resx `EmbeddedResource` and records where: prefer
        // `OutputResource` (`GenerateResource`'s own, exact, relative to the project directory)
        // and fall back to `IntermediateOutputPath + ManifestResourceName + ".resources"` for
        // an older SDK that does not set it. Non-resx embedded resources are passed as plain
        // files on the `/resource:` switch and need nothing recorded here.
        let resources =
            items "EmbeddedResource"
            |> List.filter (fun item -> (identity item).ToLowerInvariant().EndsWith ".resx")
            |> List.map (fun item ->
                // `FullPath`, a well-known item metadata, is unreliable here under the msbuild
                // CLI's `-getItem`: seen live on an `<EmbeddedResource Update="...">` item
                // (page's `Properties\Resources.resx`, resolved via the SDK's default-items
                // glob), its `FullPath` came back resolved against this *process's* current
                // directory instead of the project's own directory -- `RootDir` and `Directory`
                // in the same metadata bag were equally off. `Identity` combined with
                // `directory` (a *property*, not well-known item metadata, and not subject to
                // this) is reliable, the same combine `OutputResource` already gets below.
                let identityPath = identity item |> slash
                let resx =
                    if Path.IsPathRooted identityPath then identityPath
                    else Path.GetFullPath (Path.Combine (directory, identityPath)) |> slash
                let output =
                    match metadata "OutputResource" item with
                    | "" -> intermediate + metadata "ManifestResourceName" item + ".resources"
                    | outputResource ->
                        let outputResource = slash outputResource
                        if Path.IsPathRooted outputResource then outputResource
                        else Path.GetFullPath (Path.Combine (directory, outputResource)) |> slash
                resx, output)
        let resourceOutputs = resources |> List.map snd |> Set.ofList

        // msbuild-generated *text* inputs (assembly attributes, the derived .editorconfig):
        // the compiled .resources files are binary and already tracked, separately, in
        // `resources` -- reading one with `File.ReadAllText` here would corrupt it (and `run`
        // would then write the mangled text back over the real file).
        let generated =
            CscArgs.inputs args
            |> List.filter (fun path -> path.StartsWith intermediate && File.Exists path && not (resourceOutputs.Contains path))
            |> List.map (fun path -> path, File.ReadAllText path)

        // an import under the SDK is the SDK version, recorded with the compiler; one under
        // obj is restore's, regenerated by the import itself. The rest are the evaluation's
        // inputs: the project, the Directory.Build files, package build files
        let sdkRoot = (prop "NetCoreRoot" |> slash).TrimEnd '/'
        let imports =
            imports |> List.map slash
            |> List.filter (fun path -> not (sdkRoot <> "" && path.StartsWith (sdkRoot + "/")) && not (path.StartsWith baseIntermediate))

        let analyzers = CscArgs.switchValues "analyzer" args
        let entry : Lock.Project = {
            Name = prop "AssemblyName"
            Project = prop "MSBuildProjectFullPath" |> slash
            Directory = directory
            Compiler = { Tool = "csc"; Path = compilerPath; Sha256 = Lock.sha256 compilerPath; Sdk = prop "NETCoreSdkVersion" }
            Args = args
            References = CscArgs.switchValues "reference" args |> List.map Lock.hashed
            Analyzers = analyzers |> List.map Lock.hashed
            ProjectRefs = items "ProjectReference" |> List.map (fullPath >> slash)
            Imports = imports |> List.map Lock.hashed
            Generated = generated
            Resources = resources
            Properties =
                properties |> Map.filter (fun name _ ->
                    List.contains name [ "AssemblyName"; "TargetFrameworkMoniker"; "LangVersion"; "Version"; "InformationalVersion"
                                         "SignAssembly"; "AssemblyOriginatorKeyFile"; "Deterministic"; "TargetPath"; "IntermediateOutputPath"
                                         "NETCoreSdkVersion" ])
                |> Map.add "SdkPin" (sdkPinText pin)
        }
        entry

    /// <summary>
    /// Imports the projects and writes the lock. Make it the recipe of a file rule over the
    /// lock: msbuild then runs only when a project file or one of the files it imports
    /// changed.
    /// </summary>
    let import (options: ImportOptions) =

        recipe {
            let variantDir = if options.Variant = "" then "" else options.Variant + "/"
            let properties =
                [ "Configuration", options.Configuration
                  "TargetFramework", options.Framework
                  // the compiler reports its command line and does not run
                  "ProvideCommandLineArgs", "true"
                  "SkipCompilerExecution", "true"
                  // resolving a project reference must not build it
                  "BuildProjectReferences", "false"
                  // the generated files are per (framework, variant): brands sharing one obj
                  // overwrite each other's assembly attributes
                  "IntermediateOutputPath", sprintf "obj/xake/%s/%s" options.Framework variantDir
                  // the audit talks to the feeds and its warnings turn fatal under
                  // TreatWarningsAsErrors; it is not the import's business
                  "NuGetAudit", "false" ]
                @ options.Properties

            let switches = [for name, value in properties -> sprintf "-p:%s=%s" name value]

            do! needFiles (Filelist (options.Projects |> List.map File.make))
            Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath options.Output)) |> ignore

            let msbuild (arguments: string list) name =
                recipe {
                    let! exitCode =
                        shell {
                            cmd "dotnet"
                            args ("msbuild" :: arguments)
                            logprefix "[msbuild]"
                            stdoutlevel (Impl.levelFromString Level.Verbose)
                            erroutlevel (Impl.levelFromString Level.Error)
                        }
                    do! Impl.failOnExitCode true name exitCode
                }

            let projects = ResizeArray<Lock.Project>()
            for project in options.Projects do
                do! trace Info "importing '%s' for '%s' %s" project options.Framework (if options.Variant = "" then "" else "(" + options.Variant + ")")

                let dump = options.Output + "." + Path.GetFileNameWithoutExtension project + ".msbuild"
                let preprocessed = dump + ".pp"

                // the design-time build; the compiler's command line comes back as an item list
                do! msbuild
                        ([ project; "-restore"; "-nologo"; "-verbosity:quiet" ] @ switches
                         @ [ sprintf "-t:%s" targets
                             sprintf "-getItem:%s" items
                             sprintf "-getProperty:%s" wantedProperties
                             sprintf "-getResultOutputFile:%s" dump ]) project

                // the files that took part in the evaluation; MSBuildAllProjects no longer
                // tells, the preprocessed project does
                do! msbuild ([ project; "-nologo" ] @ switches @ [ sprintf "-pp:%s" preprocessed ]) project

                let imports = File.ReadAllText preprocessed |> parseImports
                let pin = sdkPin (Path.GetDirectoryName (Path.GetFullPath project))
                let entry = parseImport dump imports pin
                File.Delete dump
                File.Delete preprocessed

                match pin with
                | Pinned v when entry.Compiler.Sdk <> "" && entry.Compiler.Sdk <> v ->
                    do! trace Warning "'%s': the SDK is pinned to %s but msbuild ran %s -- the pinned SDK is not installed on this machine" entry.Name v entry.Compiler.Sdk
                | Pinned _ -> ()
                | other ->
                    do! trace Warning "'%s': the SDK is not pinned (%s) -- the lock's Compiler section (%s) will drift with every SDK the machine picks; pin it with global.json { sdk: { version, rollForward: \"disable\" } }" entry.Name (sdkPinText other) entry.Compiler.Sdk

                // the evaluation's inputs, so that a Directory.Build.props edit re-imports
                // and nothing else does
                do! needFiles (Filelist (entry.Imports |> List.map (fun (h: Lock.Hashed) -> File.make h.Path)))

                // every resx output has to be named by a /resource: switch, or `run` would
                // regenerate a file the compiler never reads
                let resourceInputs = CscArgs.switchValues "resource" entry.Args
                for (resx, resourcesFile) in entry.Resources do
                    if not (List.contains resourcesFile resourceInputs) then
                        do! trace Warning "'%s' compiles to '%s' but no /resource: switch names that path" resx resourcesFile

                projects.Add entry

            let lock : Lock.File = {
                Framework = options.Framework
                Configuration = options.Configuration
                Properties = options.Properties
                Projects = List.ofSeq projects
            }
            File.WriteAllText (options.Output, Lock.writeWith (Fsproj.withRoots options.Roots) lock)
        }
