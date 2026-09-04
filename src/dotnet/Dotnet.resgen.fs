namespace Xake.Dotnet

open ResourceFileset
[<AutoOpen>]
module ResgenImpl =

    open System.IO
    open System.Resources
    open Xake

    /// ResGen task settings.
    // TODO single file mode
    // TODO extra command-line args
    type ResgenSettingsType = {
        /// Resource sets to compile.
        Resources: ResourceFileset list
        /// Directory the generated .resources files are written to.
        TargetDir: DirectoryInfo
        /// Resolve relative paths inside a resx against the resx file location.
        UseSourcePath: bool
    } with static member Default = {
            Resources = []
            TargetDir = DirectoryInfo "."
            UseSourcePath = true
        }

    /// Default settings for the ResGen task.
    let ResgenSettings = ResgenSettingsType.Default

    /// Generates binary resource files from resx, txt etc
    let ResGen (settings:ResgenSettingsType) =

        // TODO rewrite everything, it's just demo code
        let generate baseDir (options:ResourceSetOptions) (resxfile:string) =
            let rcfile =
                Path.Combine(
                    settings.TargetDir.FullName,
                    Path.ChangeExtension(resxfile, ".resource") |> Impl.makeResourceName options baseDir)

            use writer = new ResourceWriter (rcfile)

#if NET46
            use resxreader = new ResXResourceReader (resxfile)

            if settings.UseSourcePath then
                resxreader.BasePath <- Path.GetDirectoryName (resxfile)

            let reader = resxreader.GetEnumerator()
            while reader.MoveNext() do
                writer.AddResource (reader.Key :?> string, reader.Value)
#endif
            rcfile

        recipe {
            for r in settings.Resources do
                let (ResourceFileset (settings,fileset)) = r
                let (Fileset (options,_)) = fileset
                let! (Filelist files) = getFiles fileset

                do files |> List.map (File.getFullName >> generate options.BaseDir settings) |> ignore
            ()
        }

    /// Computation expression builder for the resgen task.
    type ResgenSettingsBuilder() =

        /// <summary>Adds a resource set to compile</summary>
        [<CustomOperation("resources")>]     member __.Resources(s:ResgenSettingsType, value) = {s with Resources = value :: s.Resources}
        /// <summary>Adds several resource sets to compile</summary>
        [<CustomOperation("resourceslist")>] member __.ResourcesList(s:ResgenSettingsType, values) = {s with Resources = values @ s.Resources}
        /// <summary>Sets the directory the generated .resources files are written to</summary>
        [<CustomOperation("targetdir")>]     member __.TargetDir(s:ResgenSettingsType, (value:string)) = {s with TargetDir = DirectoryInfo value}
        /// <summary>Do not resolve relative paths inside a resx against the resx file location</summary>
        [<CustomOperation("nosourcepath")>]  member __.NoSourcePath(s:ResgenSettingsType) = {s with UseSourcePath = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = ResgenSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = ResgenSettingsType.Default
        member __.Run(s:ResgenSettingsType) = ResGen s

    /// The resgen task builder instance.
    let resgen = ResgenSettingsBuilder()
