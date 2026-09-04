namespace Xake.Dotnet

[<AutoOpen>]
module MsbuildImpl =

    open Xake
    open Xake.Tasks
    open DotNetTaskTypes

    /// Sln (msbuild/xbuild) task settings.
    type MSBuildSettingsType = {
        /// Build file location
        BuildFile: string
        /// Build these targets.
        Target: string list
        /// Set or override project-level properties.
        Property: (string*string) list
        /// Maximum number of concurrent processes. Some 0 - to use number of processors on the computer.
        MaxCpuCount: int option
        /// The version of MSBuild toolset (tasks, targets etc) to use during the build.
        ToolsVersion: string option
        /// Output this amount of information. All MSBuild output is considered Infomation so is displayed when logging level is Chatty.
        Verbosity: MsbVerbosity
        /// Insert command-line settings from file
        RspFile: string option
        /// Build fails on compile error.
        FailOnError: bool
    } with static member Default = {
            BuildFile = null
            Target = []
            Property = []
            MaxCpuCount = None
            ToolsVersion = None
            Verbosity = Normal
            RspFile = None
            FailOnError = true
        }

    /// Default settings for the Sln (MSBuild) task.
    let MSBuildSettings = MSBuildSettingsType.Default

    /// <summary>
    /// Builds a project or a solution with MSBuild (xbuild under mono).
    /// </summary>
    /// <param name="settings">MSBuild settings</param>
    /// <returns>Recipe running the build</returns>
    let MSBuild (settings:MSBuildSettingsType) =

        recipe {
            do! trace Level.Debug "MSBuild: settings=%A" settings

            let! dotnetFwk = getVar "NETFX"
            let fwkInfo = DotNetFwk.locateFramework dotnetFwk

            let pfx = "[msbuild]"

            let verbosityKey = function | Quiet -> "q" | Minimal -> "m" | Normal -> "n" | Detailed -> "d" | Diag -> "diag"

            let commandLineArgs =
                seq {
                    yield "/nologo"
                    yield settings.BuildFile

                    match settings.Target with
                        | [] -> ()
                        | lst -> yield "/t:" + (lst |> String.concat ";")

                    match settings.Property with
                        | [] -> ()
                        | lst -> yield "/property:" + (lst |> List.map (fun (k,v) -> sprintf "%s=%s" k v) |> String.concat ";")

                    match settings.MaxCpuCount with
                        | None -> ()
                        | Some 0 -> yield "/m"
                        | Some n -> yield sprintf "/m:%i" n

                    if Option.isSome settings.ToolsVersion then yield sprintf "/toolsversion:%s" (Option.get settings.ToolsVersion)
                    if Option.isSome settings.RspFile then yield sprintf "@%s" (Option.get settings.RspFile)

                    match settings.Verbosity with
                        | Normal -> ()
                        | v -> yield sprintf "/verbosity:%s" (verbosityKey v)
                }

            do! trace Info "%s making '%s' using framework '%s'" pfx settings.BuildFile fwkInfo.Version
            do! trace Debug "Command line: '%s'" (commandLineArgs |> String.concat " ")

            let! exitCode =
                shell {
                    cmd fwkInfo.MsbuildTool
                    args commandLineArgs
                    logprefix pfx
                    stdoutlevel (Impl.levelFromString Level.Verbose)
                    erroutlevel (Impl.levelFromString Level.Verbose)
                }


            do! trace Info "%s done '%s'" pfx settings.BuildFile
            do! Impl.failOnExitCode settings.FailOnError settings.BuildFile exitCode
        }

    /// Computation expression builder for the msbuild task.
    type MSBuildSettingsBuilder() =

        /// <summary>Sets the project or solution file to build</summary>
        [<CustomOperation("buildfile")>]  member __.BuildFile(s:MSBuildSettingsType, value) = {s with BuildFile = value}
        /// <summary>Adds a target to build</summary>
        [<CustomOperation("target")>]     member __.Target(s:MSBuildSettingsType, value) =    {s with Target = s.Target @ [value]}
        /// <summary>Sets the list of targets to build</summary>
        [<CustomOperation("targets")>]    member __.Targets(s:MSBuildSettingsType, value) =   {s with Target = value}
        /// <summary>Sets or overrides a project-level property</summary>
        [<CustomOperation("prop")>]       member __.Prop(s:MSBuildSettingsType, (name, value)) = {s with Property = s.Property @ [(name, value)]}
        /// <summary>Sets or overrides project-level properties</summary>
        [<CustomOperation("props")>]      member __.Props(s:MSBuildSettingsType, value) =     {s with Property = s.Property @ value}
        /// <summary>Maximum number of concurrent processes, 0 for the number of processors</summary>
        [<CustomOperation("maxcpu")>]     member __.MaxCpu(s:MSBuildSettingsType, value) =    {s with MaxCpuCount = Some value}
        /// <summary>The MSBuild toolset version to use</summary>
        [<CustomOperation("toolsversion")>] member __.ToolsVersion(s:MSBuildSettingsType, value) = {s with ToolsVersion = Some value}
        /// <summary>How much information MSBuild outputs</summary>
        [<CustomOperation("verbosity")>]  member __.Verbosity(s:MSBuildSettingsType, value) = {s with Verbosity = value}
        /// <summary>Inserts command-line settings from a response file</summary>
        [<CustomOperation("rspfile")>]    member __.RspFile(s:MSBuildSettingsType, value) =   {s with RspFile = Some value}
        /// <summary>Does not fail the build on a non-zero exit code</summary>
        [<CustomOperation("nofailonerror")>] member __.NoFailOnError(s:MSBuildSettingsType) = {s with FailOnError = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = MSBuildSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = MSBuildSettingsType.Default
        member __.Run(s:MSBuildSettingsType) = MSBuild s

    /// The msbuild task builder instance.
    let msbuild = MSBuildSettingsBuilder()
