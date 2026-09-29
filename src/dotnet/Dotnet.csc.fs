namespace Xake.Dotnet

/// The `csc {}` builder. What it builds -- the settings, the resolved `Csc` and its runner --
/// lives in `Csc.fs`; this file only names the operations.
[<AutoOpen>]
module CscBuilder =

    open Xake

    /// The state of a `csc {}` block after `resolve`: "resolve, do not run". `Run` on it
    /// returns the `Csc` instead of compiling.
    type CscRequest = CscRequest of CscSettingsType

    /// Computation expression builder for the csc task.
    type CscSettingsBuilder() =

        [<CustomOperation("platform")>]  member __.Platform(s:CscSettingsType, value) =    {s with Platform = value}
        [<CustomOperation("target")>]    member __.Target(s:CscSettingsType, value) =    {s with Target = value}
        [<CustomOperation("targetfwk")>] member __.TargetFwk(s:CscSettingsType, value) = {s with TargetFramework = value}
        [<CustomOperation("out")>]       member __.OutFile(s:CscSettingsType, value) =   {s with Out = value}
        [<CustomOperation("src")>]       member __.SrcFiles(s:CscSettingsType, value) =  {s with Src = value}

        [<CustomOperation("ref")>]       member __.Ref(s:CscSettingsType, value) =         {s with Ref = s.Ref + value}
        [<CustomOperation("refif")>]     member __.Refif(s:CscSettingsType, cond, (value:Fileset)) = {s with Ref = s.Ref +? (cond,value)}

        [<CustomOperation("refs")>]      member __.Refs(s:CscSettingsType, value) =        {s with Ref = value}
        [<CustomOperation("grefs")>]     member __.RefGlobal(s:CscSettingsType, value) =   {s with RefGlobal = value}
        [<CustomOperation("resources")>] member __.Resources(s:CscSettingsType, value) =   {s with CscSettingsType.Resources = value :: s.Resources}
        [<CustomOperation("resourceslist")>] member __.ResourcesList(s:CscSettingsType, values) = {s with CscSettingsType.Resources = values @ s.Resources}

        [<CustomOperation("define")>]    member __.Define(s:CscSettingsType, value) =      {s with Define = value}
        [<CustomOperation("unsafe")>]    member __.Unsafe(s:CscSettingsType, value) =      {s with Unsafe = value}
        [<CustomOperation("cscpath")>]       member __.CscPath(s:CscSettingsType, value) =   {s with CscPath = Some value}
        /// <summary>Takes `csc.dll` from `Microsoft.Net.Compilers.Toolset/&lt;version&gt;` in the
        /// NuGet cache (restoring the package if it is missing) instead of the SDK's own, so the
        /// compiler is a pinned, hashed dependency in the lock. `cscpath` still overrides this.</summary>
        [<CustomOperation("toolset")>]       member __.Toolset(s:CscSettingsType, version: string) = {s with Toolset = Some version}

        /// <summary>Compiles in a fresh compiler process instead of through the Roslyn compiler
        /// server (`VBCSCompiler`); see `CompilerServer`.</summary>
        [<CustomOperation("noserver")>]   member __.NoServer(s:CscSettingsType) = {s with Server = Some InProcess}
        /// <summary>Seconds of idle time after which a compiler server this build starts exits
        /// (`/keepalive`); Roslyn's own default is 600.</summary>
        [<CustomOperation("keepalive")>]  member __.KeepAlive(s:CscSettingsType, seconds: int) = {s with Server = Some (Shared (Some seconds))}

        /// <summary>Passes custom arguments to the compiler</summary>
        [<CustomOperation("args")>]       member __.Args(s:CscSettingsType, args) =   {s with CommandArgs = args}
        /// <summary>Does not fail the build on a compile error</summary>
        [<CustomOperation("nofailonerror")>] member __.NoFailOnError(s:CscSettingsType) = {s with FailOnError = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = CscSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = CscSettingsType.Default
        /// <summary>Stops short of compiling: the block returns the resolved `Csc`
        /// (`Csc.ofSettings`) instead of a recipe that compiles it, for `Csc.run` or
        /// `Lock.build` to take. Must be the last operation -- the ones after it do not
        /// type-check. Outside a file rule it needs an explicit `out`, since the output
        /// otherwise defaults to the rule's target.</summary>
        [<CustomOperation("resolve")>]    member __.Resolve(s:CscSettingsType) = CscRequest s

        member __.Run(s:CscSettingsType) = Csc.compile s
        member __.Run(CscRequest s) = Csc.ofSettings s

    /// The csc task builder instance.
    let csc = CscSettingsBuilder()
