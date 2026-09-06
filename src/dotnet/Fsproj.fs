namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// Reads a project file the way msbuild reads it. A build script that drives the compilers
/// itself still needs to know what to compile, what to reference and what to define; asking
/// msbuild for it costs one evaluation and gives exactly what `dotnet build` would use --
/// conditions, imports, generated assembly attributes and the resolved reference list
/// included.
module Fsproj =

    /// Just enough JSON for what msbuild's `-getItem`/`-getProperty` writes. A parser of our
    /// own rather than System.Text.Json, which is a package dependency on netstandard2.0 and
    /// would land on every consumer of Xake.
    module internal Json =

        type Value =
            | JString of string
            | JObject of (string * Value) list
            | JArray of Value list
            /// numbers, booleans and null -- msbuild writes none of them, kept so that an
            /// unexpected value does not fail the whole parse
            | JOther of string

        let parse (text: string) =
            let mutable pos = 0
            let fail message = failwithf "malformed json at %d: %s" pos message
            let skipWs () = while pos < text.Length && System.Char.IsWhiteSpace text.[pos] do pos <- pos + 1
            let expect (c: char) =
                skipWs ()
                if pos >= text.Length || text.[pos] <> c then fail (sprintf "expected '%c'" c)
                pos <- pos + 1

            let parseString () =
                expect '"'
                let value = System.Text.StringBuilder()
                let mutable closed = false
                while not closed do
                    if pos >= text.Length then fail "unterminated string"
                    let c = text.[pos]
                    pos <- pos + 1
                    match c with
                    | '"' -> closed <- true
                    | '\\' ->
                        let escaped = text.[pos]
                        pos <- pos + 1
                        match escaped with
                        | 'n' -> value.Append '\n' |> ignore
                        | 't' -> value.Append '\t' |> ignore
                        | 'r' -> value.Append '\r' |> ignore
                        | 'b' -> value.Append '\b' |> ignore
                        | 'f' -> value.Append '\012' |> ignore
                        | 'u' ->
                            value.Append (char (System.Convert.ToInt32 (text.Substring (pos, 4), 16))) |> ignore
                            pos <- pos + 4
                        | c -> value.Append c |> ignore
                    | c -> value.Append c |> ignore
                value.ToString()

            // defined ahead of parseValue and taking the item parser as an argument: inside a
            // `let rec ... and ...` group it would be pinned to one item type
            let parseSequence closing (parseItem: unit -> 'item) : 'item list =
                pos <- pos + 1
                let items = ResizeArray()
                skipWs ()
                if text.[pos] = closing then pos <- pos + 1
                else
                    let mutable more = true
                    while more do
                        items.Add (parseItem ())
                        skipWs ()
                        match text.[pos] with
                        | ',' -> pos <- pos + 1
                        | c when c = closing -> pos <- pos + 1; more <- false
                        | c -> fail (sprintf "unexpected '%c'" c)
                List.ofSeq items

            let rec parseValue () =
                skipWs ()
                if pos >= text.Length then fail "unexpected end of input"
                match text.[pos] with
                | '"' -> JString (parseString ())
                | '{' -> JObject (parseSequence '}' (fun () ->
                            skipWs ()
                            let name = parseString ()
                            expect ':'
                            name, parseValue ()))
                | '[' -> JArray (parseSequence ']' parseValue)
                | _ ->
                    let start = pos
                    while pos < text.Length && text.[pos] <> ',' && text.[pos] <> '}' && text.[pos] <> ']'
                          && not (System.Char.IsWhiteSpace text.[pos]) do
                        pos <- pos + 1
                    JOther (text.Substring (start, pos - start))

            let value = parseValue ()
            skipWs ()
            value

        /// Escapes a string as a json literal.
        let escape (value: string) =
            let escaped = System.Text.StringBuilder()
            for c in value do
                match c with
                | '"' -> escaped.Append "\\\"" |> ignore
                | '\\' -> escaped.Append "\\\\" |> ignore
                | '\n' -> escaped.Append "\\n" |> ignore
                | '\r' -> escaped.Append "\\r" |> ignore
                | '\t' -> escaped.Append "\\t" |> ignore
                | c when c < ' ' -> escaped.AppendFormat ("\\u{0:x4}", int c) |> ignore
                | c -> escaped.Append c |> ignore
            sprintf "\"%s\"" (escaped.ToString())

        let field name = function
            | JObject members -> members |> List.tryPick (fun (n, v) -> if n = name then Some v else None)
            | _ -> None

        let asString = function | JString s -> Some s | _ -> None
        let asArray = function | JArray items -> items | _ -> []

    /// What a project file says about one target framework.
    type ProjectInfo = {
        /// The assembly it produces
        AssemblyName: string
        /// Source files in compile order: CompileBefore, then Compile, then CompileAfter. The
        /// generated assembly attributes are the CompileBefore item, so they come first.
        Sources: string list
        /// Every assembly the compiler has to reference, resolved. A project reference shows
        /// up as the referenced project's msbuild output, which a script driving its own
        /// layout will want to substitute.
        References: string list
        /// ProjectReference items, as paths to the project files
        ProjectRefs: string list
        /// DefineConstants, including the symbols msbuild derives from the framework
        Defines: string list
        /// The properties that were asked for
        Properties: Map<string, string>
    }

    /// What to ask msbuild about.
    type EvalOptions = {
        /// The project file
        Project: string
        /// Target framework its items and properties are resolved for
        Framework: string
        /// Configuration, as in `dotnet build -c`
        Configuration: string
        /// Additional properties, e.g. ["Version", "1.2.3"]
        Properties: (string * string) list
        /// File the result is written to
        Output: string
    } with static member Default = {
            Project = ""
            Framework = ""
            Configuration = "Release"
            Properties = []
            Output = ""
        }

    /// Item lists and properties the evaluation asks for. `PrepareForBuild` is what triggers
    /// the SDK's implicit define constants, `GenerateAssemblyInfo` writes the attributes file
    /// into `obj`, and `ResolveReferences` produces the reference list.
    let internal targets = "PrepareForBuild;GenerateAssemblyInfo;ResolveReferences"
    let internal items = "CompileBefore,Compile,CompileAfter,ReferencePath,ProjectReference"
    let internal wantedProperties = "AssemblyName,DefineConstants,Optimize,DebugType,NoWarn,OtherFlags"

    /// Roots replaced by a token when an evaluation is kept, and expanded back when it is
    /// read: what a build links against sits under a package cache and a checkout, and neither
    /// is in the same place on the next machine. Longest root first, so the more specific one
    /// wins.
    let internal roots () =
        let nuget =
            match System.Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
            | null | "" ->
                System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
                    </> ".nuget" </> "packages"
            | dir -> dir
        [ "$(NuGetPackageRoot)", nuget
          "$(ProjectRoot)", Directory.GetCurrentDirectory() ]
        |> List.map (fun (token, path) -> token, path.Replace('\\', '/').TrimEnd '/')
        |> List.sortByDescending (snd >> String.length)

    /// Paths are written with '/' whatever the platform: a kept evaluation is a file people
    /// read and diff, and fsc takes forward slashes everywhere.
    let internal tokenize roots (path: string) =
        let path = path.Replace('\\', '/')
        roots
        |> List.tryPick (fun (token: string, root: string) ->
            if path.StartsWith (root + "/") then Some (token + path.Substring root.Length) else None)
        |> Option.defaultValue path

    let internal expand roots (path: string) =
        roots |> List.fold (fun (path: string) (token: string, root: string) -> path.Replace(token, root)) path

    let private defines (properties: Map<string, string>) =
        properties |> Map.tryFind "DefineConstants" |> Option.defaultValue ""
        |> fun value -> value.Split(';') |> List.ofArray |> List.filter (System.String.IsNullOrWhiteSpace >> not)

    /// Reads what msbuild wrote in answer to `-getItem`/`-getProperty`.
    let internal parseEvaluation (resultFile: string) =
        let root = File.ReadAllText resultFile |> Json.parse

        let itemPaths name =
            Json.field "Items" root
            |> Option.bind (Json.field name)
            |> Option.map Json.asArray |> Option.defaultValue []
            // Identity is relative to the project, FullPath is not
            |> List.choose (fun item ->
                match Json.field "FullPath" item |> Option.bind Json.asString with
                | Some path -> Some path
                | None -> Json.field "Identity" item |> Option.bind Json.asString)

        let properties =
            match Json.field "Properties" root with
            | Some (Json.JObject members) ->
                members |> List.choose (fun (name, value) -> Json.asString value |> Option.map (fun v -> name, v)) |> Map.ofList
            | _ -> Map.empty

        {
            AssemblyName =
                properties |> Map.tryFind "AssemblyName"
                |> Option.defaultValue (Path.GetFileNameWithoutExtension resultFile)
            Sources = itemPaths "CompileBefore" @ itemPaths "Compile" @ itemPaths "CompileAfter"
            References = itemPaths "ReferencePath"
            ProjectRefs = itemPaths "ProjectReference"
            Defines = defines properties
            Properties = properties
        }

    /// The project as this build reads it. msbuild answers with every metadata field of every
    /// item -- a couple of hundred kilobytes of which two fields are ever looked at -- so what
    /// gets kept is this: readable, diffable, and about a twentieth of the size.
    let internal write (project: ProjectInfo) =
        let roots = roots ()
        let list name items =
            items
            |> List.map (tokenize roots >> Json.escape >> sprintf "    %s")
            |> String.concat ",\n"
            |> sprintf "  %s: [\n%s\n  ]" (Json.escape name)

        [   sprintf "  %s: {\n%s\n  }" (Json.escape "Properties")
                (project.Properties
                 |> Map.toList
                 |> List.map (fun (name, value) -> sprintf "    %s: %s" (Json.escape name) (Json.escape value))
                 |> String.concat ",\n")
            list "Sources" project.Sources
            list "References" project.References
            list "ProjectRefs" project.ProjectRefs
        ] |> String.concat ",\n" |> sprintf "{\n%s\n}\n"

    /// <summary>
    /// Evaluates a project file with msbuild and writes the result. Make it the recipe of a
    /// file rule: the result then only gets rebuilt when the project file changes, and
    /// msbuild stays out of the way of every other build.
    /// </summary>
    /// <param name="options">What to evaluate and where to put the answer</param>
    let evaluate (options: EvalOptions) =

        recipe {
            let properties =
                [ "Configuration", options.Configuration
                  "TargetFramework", options.Framework
                  // resolving a project reference must not build it -- that is msbuild
                  // compiling the very thing the script is about to compile itself
                  "BuildProjectReferences", "false"
                  // an obj subtree of its own: `dotnet build` and `dotnet test` generate the
                  // same assembly attributes file with their own version, and the two would
                  // otherwise keep invalidating each other's builds
                  "IntermediateOutputPath", sprintf "obj/xake/%s/" options.Framework ]
                @ options.Properties

            // msbuild dumps every metadata field of every item; it goes to a scratch file and
            // only what this build reads is kept
            let dump = options.Output + ".msbuild"

            // every element spelled out: a list expression mixing literals with a `for`
            // comprehension turns the literals into statements and quietly drops them
            let commandLine =
                [ "msbuild"; options.Project
                  // ResolveReferences reads project.assets.json, so a restore has to have run
                  "-restore"; "-nologo"; "-verbosity:quiet" ]
                @ [for name, value in properties -> sprintf "-p:%s=%s" name value]
                @ [ sprintf "-t:%s" targets
                    sprintf "-getItem:%s" items
                    sprintf "-getProperty:%s" wantedProperties
                    sprintf "-getResultOutputFile:%s" dump ]

            do! trace Info "evaluating '%s' for '%s'" options.Project options.Framework

            Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath options.Output)) |> ignore

            let! exitCode =
                shell {
                    cmd "dotnet"
                    args commandLine
                    logprefix "[msbuild]"
                    stdoutlevel (Impl.levelFromString Level.Verbose)
                    erroutlevel (Impl.levelFromString Level.Error)
                }

            do! Impl.failOnExitCode true options.Project exitCode

            File.WriteAllText (options.Output, parseEvaluation dump |> write)
            File.Delete dump
        }

    /// <summary>
    /// Reads the file `evaluate` wrote.
    /// </summary>
    /// <param name="resultFile">The file the evaluation was written to</param>
    let parse (resultFile: string) =
        let root = File.ReadAllText resultFile |> Json.parse
        let roots = roots ()

        let strings name =
            Json.field name root |> Option.map Json.asArray |> Option.defaultValue []
            |> List.choose Json.asString |> List.map (expand roots)

        let properties =
            match Json.field "Properties" root with
            | Some (Json.JObject members) ->
                members |> List.choose (fun (name, value) -> Json.asString value |> Option.map (fun v -> name, v)) |> Map.ofList
            | _ -> Map.empty

        {
            AssemblyName =
                properties |> Map.tryFind "AssemblyName"
                |> Option.defaultValue (Path.GetFileNameWithoutExtension resultFile)
            Sources = strings "Sources"
            References = strings "References"
            ProjectRefs = strings "ProjectRefs"
            Defines = defines properties
            Properties = properties
        }
