namespace Xake.Tasks

open Xake
open System.IO

[<AutoOpen>]
module RmImpl =

    /// One thing to delete: a directory mask, a file mask or a fileset.
    type RmTarget =
        | Dir of string
        | File of string
        | Files of Fileset

    /// Configuration for the Rm task: specifies files or directories to delete.
    /// Targets are applied in declaration order.
    type RmArgs = {
        targets: RmTarget list
        verbose: bool
    } with static member Default = {
            targets = []
            verbose = false
        }

    /// Deletes files or directories matching the targets specified in RmArgs, in order.
    let Rm (args: RmArgs) =

        recipe {
            do! trace Level.Debug "Rm: args=%A" args

            let! ctx = getCtx()
            let projectRoot = ctx.Options.ProjectRoot

            let reportDeleting file =
                if args.verbose then
                    ctx.Logger.Log Level.Message "[rm] deleting '%A'" file
                ctx.Logger.Log Level.Debug "deleting '%A'" file

            // full paths already handled by an earlier target
            let seen = System.Collections.Generic.HashSet<string>()
            let firstTime (path: string) = seen.Add (Path.GetFullPath path)

            // an earlier target may have removed the path already: check right before deleting
            let deleteFile file =
                if firstTime file && System.IO.File.Exists file then
                    do reportDeleting file
                    do System.IO.File.Delete file

            let deleteDir dir =
                if firstTime dir && Directory.Exists dir then
                    do reportDeleting dir
                    do Directory.Delete (dir, true)

            let apply target =
                match target with
                | Files f ->
                    ctx.Logger.Log Level.Message "[rm] %A" f
                    let (Filelist files) = toFileList projectRoot f
                    files |> List.iter (fun f -> deleteFile f.FullName)

                | File fileMask ->
                    ctx.Logger.Log Level.Message "[rm] %A" fileMask
                    fileMask |> Path.parse |> Fileset.listByMask projectRoot
                    |> List.ofSeq
                    // TODO !!fileMask |> (toFileList projectRoot)
                    |> List.iter deleteFile

                | Dir dirMask ->
                    ctx.Logger.Log Level.Message "[rm] %A" dirMask
                    dirMask |> Path.parseDir |> Fileset.listByMask projectRoot
                    |> List.ofSeq
                    |> List.iter deleteDir

            do args.targets |> List.iter apply

            do! trace Level.Info "[rm] Completed"

            return ()
        }

    /// Computation expression builder for the rm/del task.
    /// `file`, `dir` and `files` may repeat and combine; each adds a target.
    type RmArgsBuilder() =

        let add (a: RmArgs) target = { a with targets = a.targets @ [target] }

        [<CustomOperation("file")>]    member __.File(a :RmArgs, value) =    add a (File value)
        [<CustomOperation("dir")>]     member __.Dir(a :RmArgs, value) =     add a (Dir value)
        [<CustomOperation("files")>]   member __.Fileset(a :RmArgs, value) = add a (Files value)
        [<CustomOperation("verbose")>] member __.Verbose(a:RmArgs) =         {a with verbose = true}

        member __.Yield(()) = RmArgs.Default
        member __.Zero() = RmArgs.Default
        member __.Run(args:RmArgs) = Rm args

    /// The rm task builder instance.
    let rm = RmArgsBuilder()
    /// Alias for rm.
    let del = rm
