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

    /// <summary>
    /// Generates binary .resources files from resx, txt etc.
    /// </summary>
    /// <param name="settings">ResGen settings</param>
    /// <returns>Recipe generating the resource files</returns>
    let ResGen (settings:ResgenSettingsType) =

        // TODO rewrite everything, it's just demo code
        let generate baseDir (options:ResourceSetOptions) (resxfile:string) =
            let rcfile =
                Path.Combine(
                    settings.TargetDir.FullName,
                    Path.ChangeExtension(resxfile, ".resources") |> Impl.makeResourceName options baseDir)

#if NETFRAMEWORK
            use writer = new ResourceWriter (rcfile)
            use resxreader = new ResXResourceReader (resxfile)

            if settings.UseSourcePath then
                resxreader.BasePath <- Path.GetDirectoryName (resxfile)

            let reader = resxreader.GetEnumerator()
            while reader.MoveNext() do
                writer.AddResource (reader.Key :?> string, reader.Value)

            rcfile
#else
            // ResXResourceReader ships with the full framework only; writing the file without
            // reading the resx would silently produce an empty resource set
            ignore rcfile
            failwith "ERROR: resx compilation is not supported under netstandard target"
#endif

        recipe {
            do! trace Level.Debug "Resgen: settings=%A" settings

            for ResourceFileset (resOptions, fileset) in settings.Resources do
                let (Fileset (fsOptions, _)) = fileset
                let! (Filelist files) = getFiles fileset

                do! needFiles (Filelist files)

                for file in files do
                    let rcfile = file |> File.getFullName |> generate fsOptions.BaseDir resOptions
                    do! trace Info "[resgen] generated '%s'" rcfile
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
