namespace Xake.Dotnet

open Xake

[<AutoOpen>]
module ResourceFileset =

    /// Options of a resource set: the resource name prefix and whether it is derived from the
    /// file location.
    type ResourceSetOptions = {Prefix:string option; DynamicPrefix:bool}
      with static member Default = {Prefix = None; DynamicPrefix = true}
    type ResourceFileset = ResourceFileset of ResourceSetOptions * Fileset

    /// Default resource set options.
    let DefaultOptions = ResourceSetOptions.Default
    /// An empty resource set.
    let Empty = ResourceFileset (DefaultOptions, Fileset.Empty)

    module private Impl =

        let changePrefix value (ResourceFileset (opts,ff)) = ResourceFileset ({opts with Prefix = value}, ff)
        let changeDynamicPrefix value (ResourceFileset (opts,ff)) = ResourceFileset ({opts with DynamicPrefix = value}, ff)
        let changeFileset value (ResourceFileset (opts,ff)) = ResourceFileset (opts, value)
        
    /// Computation expression builder for a resource set.
    type ResourceFilesetBuilder() =

        [<CustomOperation("prefix")>]      member __.Prefix(fs,prefix) = fs |> Impl.changePrefix (Some prefix)
        [<CustomOperation("dynamic")>]     member __.DynamicPrefix(resset,d) = resset |> Impl.changeDynamicPrefix d
        [<CustomOperation("files")>]       member __.ResourceFiles(resset,fs) = resset |> Impl.changeFileset fs

        // the following methods duplicate fileset operations
        [<CustomOperation("basedir")>]     member __.BaseDir (ResourceFileset(opts,fs), (value:string)) = ResourceFileset (opts, fs @@ value)
        [<CustomOperation("includes")>]    member __.Includes(ResourceFileset(opts,fs), value) = ResourceFileset (opts, fs ++ value)
        [<CustomOperation("excludes")>]    member __.Excludes(ResourceFileset(opts,fs), value) = ResourceFileset (opts, fs -- value)

        member __.Yield(())   = Empty
        member __.Delay(f)    = f()
        member __.Zero()      = __.Yield ( () )

        member __.Return(a) = __.Yield(a)


    /// The resourceset builder instance.
    let resourceset = ResourceFilesetBuilder()
