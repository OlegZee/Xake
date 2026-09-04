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
        let resgen baseDir (options:ResourceSetOptions) (resxfile:string) =
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

                do files |> List.map (File.getFullName >> resgen options.BaseDir settings) |> ignore
            ()
        }

