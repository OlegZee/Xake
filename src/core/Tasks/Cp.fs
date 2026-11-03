namespace Xake.Tasks

open Xake
open System.IO
open Xake.FileTasksImpl

[<AutoOpen>]
module CpImpl =

    /// One copy source: a directory, a file mask or a fileset.
    type CpSource =
        | Dir of string
        | File of string
        | Files of Fileset

    /// Configuration for the Cp task: specifies source and destination files.
    /// Sources are copied in declaration order.
    type CpArgs = {
        sources: CpSource list
        todir: string
        flatten: bool
        verbose: bool
        overwrite: bool
        dryrun: bool
    } with static member Default = {
            sources = []
            todir = null
            flatten = false
            verbose = false
            overwrite = false
            dryrun = false
        }

    module internal impl =
        let makeRelPath (root: string) =
            let rootLen = root.Length
            if rootLen <= 0 then id
            else
                let root', rootLen' =
                    if root.[rootLen - 1] = Path.DirectorySeparatorChar then
                        root, rootLen
                    else
                        root + (System.String (Path.DirectorySeparatorChar, 1)), rootLen + 1

                fun (path: string) ->
                    if path.StartsWith root' then
                        path.Substring rootLen'
                    else
                        path

        let getBasedirFullPath projectRoot fileset =
            let (Fileset ({BaseDir = basedir'}, _)) = fileset

            let basedir = basedir' |> function | None -> projectRoot | Some s -> Path.Combine(projectRoot, s)
            FileInfo(basedir).FullName

    /// Copies files matching the patterns specified in CpArgs to the target directory.
    let Cp (args: CpArgs) = recipe {
        do! trace Level.Debug "Copy: args=%A" args

        let! ctx = getCtx()

        let copyFile target getRelativePath file =
            let fullname = file |> Xake.File.getFullName
            let tofile = target </> (getRelativePath file)

            ctx.Logger.Log
                (if args.verbose then Level.Message else Level.Debug)
                "[copy] '%A' -> '%s'" fullname tofile

            if not args.dryrun then
                ensureDirCreated tofile

                if args.overwrite || not (System.IO.File.Exists tofile) then
                    System.IO.File.Copy(fullname, tofile, true)
                else
                    ctx.Logger.Log Level.Info "[copy] skipped ('%s' already exists while overwrite option is off)" tofile

        let projectRoot = ctx.Options.ProjectRoot
        let targetDir = args.todir |> function | null -> projectRoot | s -> System.IO.Path.Combine(projectRoot, s)

        // each source: its files and how a file maps to a path under todir
        let materialize source =
            let fileset, flatten =
                match source with
                | Files f ->
                    ctx.Logger.Log Level.Message "[copy] fileset"
                    f, args.flatten
                | File fileMask ->
                    ctx.Logger.Log Level.Message "[copy] %A" fileMask
                    // in single file mode always flatten
                    !!fileMask, true
                | Dir dirMask ->
                    ctx.Logger.Log Level.Message "[copy] %A" dirMask
                    // every file below the directory (spec 6.4: '*.*' needs a '.' in 4.0)
                    let dir = dirMask.TrimEnd('/', '\\')
                    !!(if dir = "" then "**/*" else dir + "/**/*"), args.flatten

            let getRelativePath =
                if flatten then Xake.File.getFileName
                else
                    let baseFullPath = impl.getBasedirFullPath projectRoot fileset
                    Xake.File.getFullName >> (impl.makeRelPath baseFullPath)

            ctx.Logger.Log Level.Debug "[copy] materializing fileset %A at folder %s" fileset projectRoot
            let (Filelist files) = fileset |> (toFileList projectRoot)
            files, getRelativePath

        let sources = args.sources |> List.map materialize

        do! needFiles (Filelist (sources |> List.collect fst))

        for files, getRelativePath in sources do
            for file in files do
                copyFile targetDir getRelativePath file

        do! trace Level.Info "[copy] Completed"    
        return ()
    }

    /// <summary>
    /// Copies one file to another location.
    /// </summary>
    /// <param name="src">Source file name</param>
    /// <param name="tgt">Target file location and name.</param>
    let copyFile (src: string) tgt = recipe {
        do! need [src]
        do! trace Level.Info "[copyFile] '%A' -> '%s'" src tgt

        do ensureDirCreated tgt

        System.IO.File.Copy(src, tgt, true)
    }

    /// <summary>
    /// Requests the file and writes under specific name
    /// </summary>
    let copyFrom (src: string) = recipe {
        let! tgt = getTargetFullName()
        do! copyFile src tgt
    }

    /// Computation expression builder for the copy task.
    /// `file`, `dir` and `files` may repeat and combine; each adds a source.
    /// A fileset or a string on its own line is a source too: `cp { !!"bin/*.dll"; todir "deploy" }`,
    /// `cp { "README.md"; todir "deploy" }` (a string is a file mask, as `file`).
    type CopyBuilder() =

        let add (a: CpArgs) source = { a with sources = a.sources @ [source] }
        let combine (a: CpArgs) (b: CpArgs) =
            { sources = a.sources @ b.sources
              todir = (match b.todir with null -> a.todir | d -> d)
              flatten = a.flatten || b.flatten
              verbose = a.verbose || b.verbose
              overwrite = a.overwrite || b.overwrite
              dryrun = a.dryrun || b.dryrun }

        [<CustomOperation("file")>]    member this.File(a :CpArgs, value) =   add a (File value)
        [<CustomOperation("dir")>]     member this.Dir(a :CpArgs, value) =    add a (Dir value)
        [<CustomOperation("files")>]   member this.Fileset(a :CpArgs, value)= add a (Files value)
        [<CustomOperation("todir")>]   member this.ToDir(a :CpArgs, value)=   {a with todir = value}
        [<CustomOperation("verbose")>] member this.Verbose(a :CpArgs) =        {a with verbose = true}
        [<CustomOperation("overwrite")>] member this.Overwrite(a :CpArgs) =    {a with overwrite = true}
        [<CustomOperation("flatten")>] member this.Flatten(a :CpArgs) =        {a with flatten = true}
        [<CustomOperation("dryrun")>] member this.Dryrun(a :CpArgs) =          {a with dryrun = true}

        member this.Bind(x, f) = f x
        member this.Yield(()) = CpArgs.Default
        /// A fileset entry: `cp { !!"bin/*.dll"; todir "deploy" }`.
        member this.Yield(fileset: Fileset) = { CpArgs.Default with sources = [Files fileset] }
        /// A file mask entry: `cp { "README.md"; todir "deploy" }`, same as `file`.
        member this.Yield(fileMask: string) = { CpArgs.Default with sources = [File fileMask] }
        member this.Combine(a: CpArgs, b: CpArgs) = combine a b
        member this.Delay(f: unit -> CpArgs) = f()
        member x.For(sq, b) = for e in sq do b e
        /// Lets an entry follow an operation: `cp { todir "deploy"; !!"bin/*.dll" }`.
        member x.For(a: CpArgs, f: unit -> CpArgs) = combine a (f())

        member this.Zero() = CpArgs.Default
        member this.Run(args:CpArgs) = Cp args

    /// The copy task builder instance.
    let copy = CopyBuilder()
    /// Alias for copy.
    let cp = copy
