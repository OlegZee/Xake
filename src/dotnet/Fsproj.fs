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
    /// `roots` is the list paths are tokenized against (see `Roots`).
    let internal writeWith roots (project: ProjectInfo) =
        let list name items =
            items
            |> List.map (Roots.tokenize roots >> Json.escape >> sprintf "    %s")
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

            let! roots = Roots.current
            File.WriteAllText (options.Output, parseEvaluation dump |> writeWith roots)
            File.Delete dump
        }

    /// <summary>
    /// Reads the file `evaluate` wrote, expanding its tokens against `roots` (see `Roots`).
    /// Inside a recipe use `load`, which takes the build's own project root.
    /// </summary>
    /// <param name="roots">The roots the file was written against</param>
    /// <param name="resultFile">The file the evaluation was written to</param>
    let parseWith roots (resultFile: string) =
        let root = File.ReadAllText resultFile |> Json.parse

        let strings name =
            Json.field name root |> Option.map Json.asArray |> Option.defaultValue []
            |> List.choose Json.asString |> List.map (Roots.expand roots)

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

    /// <summary>
    /// Reads the file `evaluate` wrote, against the roots of this build -- the engine's
    /// project root, not the process's current directory.
    /// </summary>
    /// <param name="resultFile">The file the evaluation was written to</param>
    let load (resultFile: string) : Recipe<ExecContext, ProjectInfo> =
        recipe {
            let! roots = Roots.current
            return parseWith roots resultFile
        }
