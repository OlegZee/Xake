namespace Xake.Dotnet

/// The `fsc {}` builder. What it builds -- the settings, the resolved `Fsc` and its runner --
/// lives in `Fsc.fs`; this file only names the operations.
[<AutoOpen>]
module FscBuilder =

    open Xake

    /// The state of an `fsc {}` block after `resolve`: "resolve, do not run". `Run` on it
    /// returns the `Fsc` instead of compiling.
    type FscRequest = FscRequest of FscSettingsType

    /// Computation expression builder for the fsc task.
    type FscSettingsBuilder() =

        /// <summary>Limits which platforms the code can run on</summary>
        [<CustomOperation("platform")>]  member __.Platform(s:FscSettingsType, value) =  {s with Platform = value}
        /// <summary>Specifies the format of the output file</summary>
        [<CustomOperation("target")>]    member __.Target(s:FscSettingsType, value) =    {s with Target = value}
        /// <summary>Specifies the .NET framework to compile against</summary>
        [<CustomOperation("targetfwk")>] member __.TargetFwk(s:FscSettingsType, value) = {s with TargetFramework = value}
        /// <summary>Specifies the output file name</summary>
        [<CustomOperation("out")>]       member __.OutFile(s:FscSettingsType, value) =   {s with Out = value}
        /// <summary>Sets the source fileset</summary>
        [<CustomOperation("src")>]       member __.SrcFiles(s:FscSettingsType, value) =  {s with Src = value}

        /// <summary>Adds assemblies to reference</summary>
        [<CustomOperation("ref")>]       member __.Ref(s:FscSettingsType, value) =       {s with Ref = s.Ref + value}
        /// <summary>Adds assemblies to reference when the condition holds</summary>
        [<CustomOperation("refif")>]     member __.Refif(s:FscSettingsType, cond, (value:Fileset)) = {s with Ref = s.Ref +? (cond,value)}
        /// <summary>Sets the assemblies to reference</summary>
        [<CustomOperation("refs")>]      member __.Refs(s:FscSettingsType, value) =      {s with Ref = value}
        /// <summary>References the specified assemblies from GAC</summary>
        [<CustomOperation("grefs")>]     member __.RefGlobal(s:FscSettingsType, value) = {s with RefGlobal = value}

        /// <summary>Embeds a resource set</summary>
        [<CustomOperation("resources")>] member __.Resources(s:FscSettingsType, value) = {s with FscSettingsType.Resources = value :: s.Resources}
        /// <summary>Embeds several resource sets</summary>
        [<CustomOperation("resourceslist")>] member __.ResourcesList(s:FscSettingsType, values) = {s with FscSettingsType.Resources = values @ s.Resources}

        /// <summary>Defines conditional compilation symbols</summary>
        [<CustomOperation("define")>]    member __.Define(s:FscSettingsType, value) =    {s with Define = value}
        /// <summary>Writes the xml documentation file</summary>
        [<CustomOperation("doc")>]       member __.Doc(s:FscSettingsType, value) =       {s with Doc = value}
        /// <summary>Uses a specific F# compiler version</summary>
        [<CustomOperation("fscver")>]    member __.FscVer(s:FscSettingsType, value) =    {s with FscVersion = Some value}
        /// <summary>Does not reference the default CLI assemblies</summary>
        [<CustomOperation("noframework")>] member __.NoFramework(s:FscSettingsType) =   {s with NoFramework = true}
        /// <summary>Turns tailcall generation off</summary>
        [<CustomOperation("notailcalls")>] member __.NoTailcalls(s:FscSettingsType) =   {s with Tailcalls = false}
        /// <summary>Passes custom arguments to the compiler</summary>
        [<CustomOperation("args")>]      member __.Args(s:FscSettingsType, args) =       {s with CommandArgs = args}
        /// <summary>Does not fail the build on a compile error</summary>
        [<CustomOperation("nofailonerror")>] member __.NoFailOnError(s:FscSettingsType) = {s with FailOnError = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = FscSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = FscSettingsType.Default
        /// <summary>Stops short of compiling: the block returns the resolved `Fsc`
        /// (`Fsc.ofSettings`) instead of a recipe that compiles it. Must be the last
        /// operation -- the ones after it do not type-check. Outside a file rule it needs an
        /// explicit `out`, since the output otherwise defaults to the rule's target.</summary>
        [<CustomOperation("resolve")>]    member __.Resolve(s:FscSettingsType) = FscRequest s

        member __.Run(s:FscSettingsType) = Fsc.compile s
        member __.Run(FscRequest s) = Fsc.ofSettings s

    /// The fsc task builder instance.
    let fsc = FscSettingsBuilder()
