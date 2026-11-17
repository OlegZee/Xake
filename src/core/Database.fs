namespace Xake

module BuildLog = 
    open Xake
    open System
    
    /// Current database schema version, written to the header of every database file.
    /// 0.5 adds the Dependency case ArtifactDep2 (dependee target with its change stamp).
    /// 0.6 adds the Dependency case SecretDep (a secret variable's digest, never its value).
    /// 0.7 stores paths once, in a directory/file table relative to roots, and frames every record
    /// with its length (docs/database.md).
    let XakeDbVersion = "0.7"

    /// Oldest database version this Xake still reads. 0.4-0.6 are read with the 0.6 pickler (0.4 and
    /// 0.5 lack ArtifactDep2/SecretDep, a subset) and rewritten as 0.7 on open. Older ones are discarded.
    let XakeDbMinReadVersion = "0.4"

    /// In-memory build database mapping targets to their last build results.
    type Database = { Status : Map<Target, BuildResult> }
    
    (* API *)

    /// Creates a new build result
    let makeResult target = 
        { Targets = target
          Built = DateTime.Now
          Depends = []
          Steps = []
          Content = ContentRecord.Fresh }
    
    /// Creates a new database
    let newDatabase() = { Database.Status = Map.empty }
    
    /// Adds result to a database
    let internal addResult db result =
        { db with Status = result.Targets |> List.fold (fun m i -> Map.add i result m) db.Status }

/// Type alias for MailboxProcessor used as a message-passing agent.
type 't Agent = 't MailboxProcessor

module Storage = 
    open Xake
    open BuildLog
    open System.Collections.Generic
    open PathTable

    /// A root of this run as the database sees it: `Name` without `$()`, `Path` the full folder,
    /// `Recorded` the text a `RootRec` keeps (a relative root as written).
    type DbRoot = { Name: string; Path: string; Recorded: string }

    /// How the database is opened.
    type DbOpenOptions = {
        /// `$(ProjectRoot)`
        ProjectRoot: string
        /// The declared roots of this run (not `ProjectRoot`).
        Roots: DbRoot list
        /// No rewrite and no append (`--dump`, `--dryrun`).
        ReadOnly: bool
    } with
        static member Default = {
            ProjectRoot = System.IO.Directory.GetCurrentDirectory()
            Roots = []
            ReadOnly = false }

    /// What the open found that the caller may act on.
    type DbOpenReport = {
        /// Root keys (`$(NAME)`, or a file-system root of another system) that records name and this
        /// run does not declare, with the number of such records.
        UndeclaredRoots: (string * int) list
    }

    module private Persist = 
        open Pickler

        type DatabaseHeader = 
            { XakeSign : string
              XakeVer : string
              ScriptDate : Timestamp }

        let step = 
            wrap 
                ((fun (n, s, o, w) -> {StepInfo.Name = n; Start = s; OwnTime = o * 1<ms>; WaitTime = w * 1<ms>}), 
                 fun ({StepInfo.Name = n; Start = s; OwnTime = o; WaitTime = w}) -> (n, s, o / 1<ms>, w / 1<ms>)) (quad str date int int)
        
        let bytes =
            { pickle = (fun (b: byte[]) (st: OutState) -> st.Write(b.Length); st.Write(b))
              unpickle = fun (st: InState) -> st.ReadBytes(st.ReadInt32()) }

        let secretSource =
            alt (function SecretEnv -> 0 | SecretArg -> 1) [| wrap0 SecretEnv; wrap0 SecretArg |]

        let secretDigest =
            alt (function SecretAbsent -> 0 | SecretPresent -> 1 | SecretHmac _ -> 2)
                [| wrap0 SecretAbsent
                   wrap0 SecretPresent
                   wrap (SecretHmac, fun (SecretHmac (salt, h) | OtherwiseFail (salt, h)) -> salt, h) (pair bytes bytes) |]

        /// The result pickler over a pickler of files: `file` for dependencies and file lists,
        /// `fileTarget` for targets (0.6 kept a target by its `Name`, a dependency by its `FullName`).
        let makeResult (file: PU<File>) (fileTarget: PU<File>) (filelist: PU<Fileset.Filelist>) =
            let target = 
                alt (function 
                    | FileTarget _ -> 0
                    | PhonyAction _ -> 1) 
                    [|  wrap (FileTarget, fun (FileTarget f | OtherwiseFail f) -> f) fileTarget
                        wrap (PhonyAction, (fun (PhonyAction a | OtherwiseFail a) -> a)) str |]
            let dependency = 
                alt (function 
                    | ArtifactDep _ -> 0
                    | FileDep _ -> 1
                    | EnvVar _ -> 2
                    | Var _ -> 3
                    | AlwaysRerun -> 4
                    | GetFiles _ -> 5
                    | ArtifactDep2 _ -> 6
                    | SecretDep _ -> 7) 
                    [| wrap (ArtifactDep, fun (ArtifactDep f | OtherwiseFail f) -> f) target                   
                       wrap (FileDep, fun (FileDep(f, ts) | OtherwiseFail (f, ts)) -> (f, ts))  (pair file date)                   
                       wrap (EnvVar, fun (EnvVar(n, v) | OtherwiseFail (n,v)) -> n, v)  (pair str (option str))
                       wrap (Var, fun (Var(n, v)| OtherwiseFail (n,v)) -> n, v)        (pair str (option str))
                       wrap0 AlwaysRerun                   
                       wrap (GetFiles, fun (GetFiles(fs, fi)| OtherwiseFail (fs,fi)) -> fs, fi)  (pair Fileset.filesetPickler filelist)
                       wrap (ArtifactDep2, fun (ArtifactDep2(t, ts) | OtherwiseFail (t, ts)) -> (t, ts)) (pair target date)
                       wrap (SecretDep, fun (SecretDep(n, src, d) | OtherwiseFail (n, src, d)) -> (n, src, d)) (triple str secretSource secretDigest) |]
            wrap 
                ((fun (r, built, deps, steps) -> 
                 { Targets = r
                   Built = built
                   Depends = deps
                   Steps = steps
                   // dep-cutoff step 3: the content data is not persisted until the 0.7 layout
                   // (feature/db-paths) gives it its record; a result read here has none
                   Content = ContentRecord.Empty }), 
                 fun r -> (r.Targets, r.Built, r.Depends, r.Steps)) 
                (quad (list target) date (list dependency) (list step))

        /// 0.4-0.6: paths as strings.
        let result06 =
            makeResult
                (wrap (File.make, fun a -> a.FullName) str)
                (wrap (File.make, fun f -> f.Name) str)
                Fileset.filelistPickler

        /// 0.7: files are ids handed out and resolved by `codec`.
        type FileCodec() =
            member val Write : File -> int = (fun _ -> invalidOp "no path transaction") with get, set
            member val Read : int -> File = (fun _ -> invalidOp "no path table") with get, set

        let result07 (codec: FileCodec) =
            let file = { pickle = (fun f w -> PathTable.Varint.write w (codec.Write f)); unpickle = fun r -> codec.Read (PathTable.Varint.read r) }
            makeResult file file (wrap (Fileset.Filelist, fun (Fileset.Filelist l) -> l) (list file))
        
        let dbHeader = 
            wrap 
                ((fun (sign, ver, scriptDate) -> 
                 { DatabaseHeader.XakeSign = sign
                   XakeVer = ver
                   ScriptDate = scriptDate }), 
                 fun h -> (h.XakeSign, h.XakeVer, h.ScriptDate)) 
                (triple str str date)

    /// A record kept as written: it names a root this run does not declare.
    type private Carried = {
        /// the record as framed in the file: tag and body
        Bytes: byte[]
        /// the file ids it names
        Ids: int list
        /// the stored paths of its file targets and the names of its phony ones
        Keys: string list
        /// the undeclared root keys it names
        RootKeys: string list }

    /// The state of an open database: what the agent works with.
    type private Loaded = {
        Db: Database
        Table: Table
        Carried: Carried list
        /// the last value each root had, by root name (`ProjectRoot`, a declared name) or key
        RootValues: Map<string, string> }

    module private impl = 
        open System.IO
        open Persist
        type File = System.IO.File

        let makeBkpath dbpath = dbpath <.> "bak"
        
        let writeHeader w = 
            let h = 
                { DatabaseHeader.XakeSign = "XAKE"
                  XakeVer = XakeDbVersion
                  ScriptDate = System.DateTime.Now }
            Persist.dbHeader.pickle h w

        // Retries `openFile` briefly on a transient sharing violation before giving up.
        // A just-closed handle (this process's own previous run, or another tool briefly
        // touching the file) can take the OS a moment to fully release after Dispose()
        // returns (seen back-to-back across quick test runs); without this a perfectly
        // good database gets mistaken for a corrupt one and thrown away, which in turn
        // makes the next build think nothing was ever built.
        let openWithRetry (openFile: unit -> #Stream) =
            let maxAttempts = 5
            let rec attempt n =
                try
                    openFile ()
                with :? IOException when n < maxAttempts ->
                    System.Threading.Thread.Sleep(20)
                    attempt (n + 1)
            attempt 1
        let openReadWithRetry (path: string) = openWithRetry (fun () -> File.OpenRead(path))

        let toRoots (opts: DbOpenOptions) : Root list =
            let projectRoot = PathTable.Roots.normalize opts.ProjectRoot
            { Root.Name = "ProjectRoot"; Path = projectRoot; Recorded = projectRoot }
            :: (opts.Roots |> List.map (fun r -> { Root.Name = r.Name; Path = PathTable.Roots.normalize r.Path; Recorded = r.Recorded }))

        /// Frames one record: its length, then tag and body.
        let writeFrame (w: BinaryWriter) (record: byte[]) =
            Varint.write w record.Length
            w.Write record

        let rootRecord (name: string) (value: string) =
            use body = new MemoryStream()
            use bw = new BinaryWriter(body)
            bw.Write Tag.RootValue
            bw.Write name
            bw.Write value
            bw.Flush()
            body.ToArray()

        /// Pickles a result as a framed record body (tag + result) with the codec writing through `tx`.
        let resultRecord (codec: FileCodec) (pu: Pickler.PU<BuildResult>) (tx: Tx) (result: BuildResult) =
            codec.Write <- tx.IdOf
            try
                use body = new MemoryStream()
                use bw = new BinaryWriter(body)
                bw.Write Tag.Result
                pu.pickle result bw
                bw.Flush()
                body.ToArray()
            finally
                codec.Write <- (fun _ -> invalidOp "no path transaction")

        /// The bytes of one stored result: the path records it still needs, then the result.
        let storeBytes codec pu (table: Table) (result: BuildResult) =
            let tx = Tx(table)
            let record = resultRecord codec pu tx result
            use out = new MemoryStream()
            use w = new BinaryWriter(out)
            tx.WriteRecords w
            writeFrame w record
            w.Flush()
            out.ToArray(), tx

        let resultKeysOfIds (table: Table) (result: BuildResult) (ids: int list) =
            // targets come first in a result record, in order
            let rec go targets ids acc =
                match targets, ids with
                | [], _ -> List.rev acc
                | PhonyAction n :: rest, _ -> go rest ids (("phony:" + n) :: acc)
                | FileTarget _ :: rest, id :: ids -> go rest ids (table.StoredPath id :: acc)
                | FileTarget _ :: rest, [] -> go rest [] acc
            go result.Targets ids []

        /// Everything read from a file, before the decision to rewrite.
        type ReadState = {
            mutable Db: Database
            mutable Carried: Carried list
            mutable RootValues: Map<string, string>
            mutable Records: int
            mutable IsOld: bool
            /// a damaged tail or record was dropped
            mutable Damaged: bool
            /// a live record names a path whose root is not its longest root in this run
            mutable MisRooted: bool
            LiveIds: HashSet<int> }

        let read07 (st: ReadState) (table: Table) (codec: FileCodec) (pu: Pickler.PU<BuildResult>) (reader: BinaryReader) (logger: ILogger) =
            let stream = reader.BaseStream
            let mutable stop = false
            while not stop && stream.Position < stream.Length do
                let frameStart = stream.Position
                let length = try ValueSome (Varint.read reader) with _ -> ValueNone
                match length with
                | ValueSome n when n > 0 && stream.Position + int64 n <= stream.Length ->
                    let bytes = reader.ReadBytes n
                    try
                        use r = new BinaryReader(new MemoryStream(bytes))
                        match r.ReadByte() with
                        | Tag.Dir ->
                            let id = Varint.read r
                            let parent = Varint.read r - 1
                            table.AddDir(id, parent, r.ReadString(), true)
                        | Tag.File ->
                            let id = Varint.read r
                            let dir = Varint.read r - 1
                            table.AddFile(id, dir, r.ReadString(), true)
                        | Tag.RootValue ->
                            let name = r.ReadString()
                            st.RootValues <- st.RootValues |> Map.add name (r.ReadString())
                        | Tag.Result ->
                            let ids = ResizeArray<int>()
                            let stale = HashSet<string>()
                            codec.Read <- (fun id ->
                                ids.Add id
                                match table.Resolve id with
                                | ValueSome f -> f
                                | ValueNone -> stale.Add(table.RootKeyOfFile id) |> ignore; Xake.File.undefined)
                            let result = pu.unpickle r
                            let ids = List.ofSeq ids
                            let keys = resultKeysOfIds table result ids
                            // a newer record of the same targets supersedes a carried one
                            st.Carried <- st.Carried |> List.filter (fun c -> not (c.Keys |> List.exists (fun k -> List.contains k keys)))
                            if stale.Count = 0 then
                                st.Db <- result |> addResult st.Db
                                for id in ids do
                                    if st.LiveIds.Add id && table.IsMisRooted id then st.MisRooted <- true
                            else
                                let resolvable = result.Targets |> List.filter (function FileTarget f -> f <> Xake.File.undefined | PhonyAction _ -> true)
                                st.Db <- { Status = resolvable |> List.fold (fun m t -> Map.remove t m) st.Db.Status }
                                st.Carried <- st.Carried @ [ { Bytes = bytes; Ids = ids; Keys = keys; RootKeys = List.ofSeq stale } ]
                        | tag ->
                            // reserved for records this version does not know: dropped at the next rewrite
                            logger.Log Level.Verbose "Skipping a database record with tag %d" (int tag)
                        st.Records <- st.Records + 1
                    with ex ->
                        logger.Log Level.Warning "Database record at offset %d is damaged, the rest of the file is dropped: %s" frameStart ex.Message
                        st.Damaged <- true
                        stop <- true
                | _ ->
                    logger.Log Level.Warning "Database ends with a torn record at offset %d, it is dropped" frameStart
                    st.Damaged <- true
                    stop <- true
            codec.Read <- (fun _ -> invalidOp "no path table")

        let readOld (st: ReadState) (reader: BinaryReader) (logger: ILogger) =
            let stream = reader.BaseStream
            try
                while stream.Position < stream.Length do
                    let result = result06.unpickle reader
                    st.Db <- result |> addResult st.Db
                    st.Records <- st.Records + 1
            with ex ->
                logger.Log Level.Warning "Database record %d is damaged, the rest of the file is dropped: %s" (st.Records + 1) ex.Message
                st.Damaged <- true

        /// Writes a fresh file: the roots, the carried records with their paths, the live results.
        let rewrite dbpath (table: Table) codec pu (rootRecords: (string * string) list) (st: ReadState) =
            let bkpath = makeBkpath dbpath
            File.Move(dbpath, bkpath)
            do
                use writer =
                    new BinaryWriter(openWithRetry (fun () -> File.Open(dbpath, FileMode.CreateNew) :> Stream))
                writeHeader writer
                table.ResetInFile()
                for (name, value) in rootRecords do writeFrame writer (rootRecord name value)
                for c in st.Carried do
                    let tx = Tx(table)
                    for id in c.Ids do tx.EnsureFile id
                    tx.WriteRecords writer
                    tx.Commit()
                    writeFrame writer c.Bytes
                // a result of several targets is in the map once per target: write it once
                let written = HashSet<BuildResult>(HashIdentity.Reference)
                st.Db.Status
                |> Map.toSeq |> Seq.map snd |> Seq.filter written.Add
                |> Seq.iter (fun r ->
                    let bytes, tx = storeBytes codec pu table r
                    writer.Write bytes
                    tx.Commit())
            File.Delete(bkpath)

        let openDatabaseFile (opts: DbOpenOptions) dbpath (logger : ILogger) =
            let log = logger.Log
            let roots = toRoots opts
            let table = Table(roots)
            let codec = FileCodec()
            let pu = result07 codec
            let bkpath = makeBkpath dbpath
            // if exists backup restore
            if File.Exists(bkpath) && not opts.ReadOnly then 
                log Level.Message "Backup file found ('%s'), restoring db" 
                    bkpath
                try 
                    File.Delete(dbpath)
                with _ -> ()
                File.Move(bkpath, dbpath)
            let st = { Db = newDatabase(); Carried = []; RootValues = Map.empty; Records = 0; IsOld = false
                       Damaged = false; MisRooted = false; LiveIds = HashSet<int>() }
            // read database
            if File.Exists(dbpath) then 
                try 
                    use reader = new BinaryReader(openReadWithRetry dbpath)
                    let header = Persist.dbHeader.unpickle reader
                    if header.XakeSign <> "XAKE" || header.XakeVer < XakeDbMinReadVersion then 
                        failwith "Database version is old."
                    if header.XakeVer > XakeDbVersion then
                        failwithf "Database version %s is newer than this Xake reads (%s)." header.XakeVer XakeDbVersion
                    st.IsOld <- header.XakeVer < XakeDbVersion
                    if st.IsOld then readOld st reader logger
                    else read07 st table codec pu reader logger
                // if fails create new
                with ex -> 
                    st.IsOld <- false
                    st.Damaged <- false
                    st.Db <- newDatabase()
                    st.Carried <- []
                    log Warning "Failed to read database, so recreating. Got \"%s\"" <| ex.ToString()
                    if not opts.ReadOnly then
                        try 
                            File.Delete(dbpath)
                        with _ -> ()
            // a clear value recorded (by an older Xake, or before the name was declared secret)
            // for a variable now declared secret becomes its digest, and the file is rewritten
            let scrubbed =
                st.Db.Status |> Map.map (fun _ r ->
                    let deps = r.Depends |> List.map Secrets.scrub
                    if deps = r.Depends then r else { r with Depends = deps })
            let hasClearSecrets =
                st.Db.Status |> Map.exists (fun key r -> not (obj.ReferenceEquals(r, scrubbed.[key])))
            st.Db <- { Status = scrubbed }

            // diagnostics
            let declaredNames = roots |> List.map (fun r -> r.Name) |> Set.ofList
            for r in roots do
                match st.RootValues.TryFind r.Name with
                | Some old when old <> r.Recorded -> log Info "root %s moved: %s -> %s" (Roots.key r.Name) old r.Recorded
                | _ -> ()
            let undeclared =
                st.Carried
                |> List.collect (fun c -> c.RootKeys |> List.distinct)
                |> List.countBy id
            for (key, count) in undeclared do
                if Roots.isNamedKey key then
                    let name = Roots.nameOfKey key
                    let last = st.RootValues.TryFind name |> Option.defaultValue "unknown"
                    log Error "database: %d records name the root %s (last value %s), which this run does not declare; their targets rebuild. Declare it (pathRoot \"%s\" ...) or set its variable."
                        count key last name
                else
                    log Error "database: %d records name the file-system root %s, which does not exist on this system; their targets rebuild."
                        count key
            let outside =
                st.LiveIds |> Seq.filter (fun id ->
                    match table.TryFileEntry id with
                    | ValueSome _ -> not (Roots.isNamedKey (table.RootKeyOfFile id))
                    | ValueNone -> false) |> Seq.length
            if outside > 0 then
                if opts.Roots.IsEmpty then
                    log Info "%d files outside all roots: they are stored absolute and not relocatable; declare a root for them (pathRoot \"NAME\" \"path\")" outside
                else
                    log Info "%d files outside all roots" outside
            if File.Exists dbpath then
                log Info "database: %d records, %d dirs, %d files, %d bytes" st.Records table.DirCount table.FileCount (FileInfo(dbpath).Length)

            let currentRootRecords = roots |> List.map (fun r -> r.Name, r.Recorded)
            let missingRootRecords = currentRootRecords |> List.filter (fun (n, v) -> st.RootValues.TryFind n <> Some v)
            let needed = st.Db.Status.Count + st.Carried.Length + st.LiveIds.Count
            let compact = st.Records > 5 * needed + 16
            if opts.ReadOnly then
                ()
            elif File.Exists dbpath && (st.IsOld || hasClearSecrets || st.Damaged || st.MisRooted || compact) then
                if st.IsOld then log Level.Message "Upgrading database to version %s" XakeDbVersion
                elif st.Damaged then log Level.Message "Recovering database: %d records kept, damaged tail dropped" st.Records
                elif hasClearSecrets then log Level.Message "Removing the values of secret variables from the database"
                elif st.MisRooted then log Level.Message "Re-rooting database paths"
                else log Level.Message "Compacting database"
                // records under undeclared roots keep their last root values
                let carriedRootRecords =
                    st.RootValues |> Map.toList
                    |> List.filter (fun (n, _) -> not (declaredNames.Contains n)
                                                  && st.Carried |> List.exists (fun c -> c.RootKeys |> List.contains (Roots.key n)))
                rewrite dbpath table codec pu (currentRootRecords @ carriedRootRecords) st
            let writer =
                if opts.ReadOnly then None
                else
                    let w = new BinaryWriter(openWithRetry (fun () -> File.Open (dbpath, FileMode.Append, FileAccess.Write) :> Stream))
                    if w.BaseStream.Position = 0L then
                        writeHeader w
                        for (n, v) in currentRootRecords do writeFrame w (rootRecord n v)
                    elif not (st.IsOld || hasClearSecrets || st.Damaged || st.MisRooted || compact) then
                        for (n, v) in missingRootRecords do writeFrame w (rootRecord n v)
                    w.Flush()
                    Some w
            let report = { UndeclaredRoots = undeclared }
            st.Db, table, codec, pu, writer, report
    
    /// Messages accepted by the database agent.
    type DatabaseApi =
        | GetResult of Target * AsyncReplyChannel<Option<BuildResult>>
        | Store of BuildResult
        | Close
        | CloseWait of AsyncReplyChannel<unit>
    
    /// <summary>
    /// The result pickler of databases 0.4-0.6 (paths as strings), kept to read and upgrade them.
    /// </summary>
    let resultPU = Persist.result06
    
    /// <summary>
    /// Opens the database with roots and options; returns the agent and what the open found.
    /// </summary>
    /// <param name="opts">Project root, declared roots, read-only.</param>
    /// <param name="dbpath">Full xake database file name</param>
    /// <param name="logger"></param>
    let openDbWith (opts: DbOpenOptions) dbpath (logger : ILogger) = 
        let db, table, codec, pu, dbwriter, report = impl.openDatabaseFile opts dbpath logger
        // Logging the agent's own failure must not throw out of the loop again.
        let logError fmt = Printf.kprintf (fun s -> try logger.Log Error "%s" s with _ -> ()) fmt
        let agent = new MailboxProcessor<_>(fun mbox ->
            // An exception escaping the loop would kill the agent silently and every later
            // PostAndReply (GetResult, CloseWait) would wait forever. So each message is
            // guarded: a failed store is logged, nothing of it reaches the file and the result
            // is kept in memory for this run (the next run rebuilds that target), a failed
            // lookup answers "no record" (a rebuild), and close always replies.
            let failedStores = ref 0
            // after a failed write the file may end in a partial record: nothing more is appended
            let writer = ref (dbwriter: System.IO.BinaryWriter option)
            let warnUnsaved () =
                if failedStores.Value > 0 then
                    try logger.Log Warning "Database not fully saved: %d result(s) failed to store, the next build may rebuild them" failedStores.Value
                    with _ -> ()
            let close () =
                match writer.Value with
                | Some w ->
                    try w.Dispose()
                    with ex -> logError "Database agent failed to close: %O" ex
                | None -> ()
            let store (result: BuildResult) =
                match writer.Value with
                | Some w ->
                    let name () = try (result.Targets |> List.map (fun t -> t.ShortName) |> String.concat ", ") with _ -> "?"
                    // pickle into memory first (path records + result), so a failure writes nothing
                    // and the file stays a sequence of whole records; the new ids are committed
                    // only after the write
                    match (try Choice1Of2 (impl.storeBytes codec pu table result) with ex -> Choice2Of2 ex) with
                    | Choice1Of2 (bytes, tx) ->
                        try
                            w.Write bytes
                            w.Flush()
                            tx.Commit()
                        with ex ->
                            failedStores.Value <- failedStores.Value + 1
                            writer.Value <- None
                            (try w.Dispose() with _ -> ())
                            logError "Database agent failed to write the result of '%s', nothing more is written in this run: %O" (name ()) ex
                    | Choice2Of2 ex ->
                        failedStores.Value <- failedStores.Value + 1
                        logError "Database agent failed to store the result of '%s': %O" (name ()) ex
                | None -> ()
            let rec loop (db) =
                async {
                    let! msg = mbox.Receive()
                    match msg with
                    | GetResult(key, chnl) ->
                        let found =
                            try db.Status |> Map.tryFind key
                            with ex ->
                                logError "Database agent failed to look up '%s': %O" key.ShortName ex
                                None
                        chnl.Reply found
                        return! loop (db)
                    | Store result ->
                        store result
                        let db' =
                            try result |> addResult db
                            with ex -> logError "Database agent failed to record a result: %O" ex; db
                        return! loop db'
                    | Close ->
                        do (try logger.Log Info "Closing database" with _ -> ())
                        do warnUnsaved ()
                        close ()
                        return ()
                    | CloseWait ch ->
                        do (try logger.Log Info "Closing database" with _ -> ())
                        do warnUnsaved ()
                        close ()
                        ch.Reply()
                        return ()
                }
            loop db)
        agent.Error.Add (fun ex -> logError "Database agent stopped: %O" ex)
        agent.Start()
        agent, report

    /// <summary>
    /// Opens database with the current directory as the project root and no declared roots.
    /// </summary>
    /// <param name="dbpath">Full xake database file name</param>
    /// <param name="logger"></param>
    let openDb dbpath (logger : ILogger) = openDbWith DbOpenOptions.Default dbpath logger |> fst

    /// <summary>
    /// Reads a database without changing it: its header version and the results it holds
    /// (the ones under undeclared roots left out). For tools and tests.
    /// </summary>
    let readDatabase (opts: DbOpenOptions) dbpath =
        let ver =
            use reader = new System.IO.BinaryReader(impl.openReadWithRetry dbpath)
            let header = Persist.dbHeader.unpickle reader
            header.XakeVer
        let db, _, _, _, _, _ = impl.openDatabaseFile { opts with ReadOnly = true } dbpath (CustomLogger (fun _ -> false) ignore)
        ver, db.Status |> Map.toList |> List.map snd |> List.distinct

    /// <summary>
    /// In-memory database for use when NoPersist = true.
    /// Tracks build results within the session but does not write to disk.
    /// </summary>
    let noopDb () =
        MailboxProcessor.Start(fun mbox ->
            let rec loop (db: Database) = async {
                let! msg = mbox.Receive()
                match msg with
                | GetResult (key, ch) ->
                    db.Status |> Map.tryFind key |> ch.Reply
                    return! loop db
                | Store result ->
                    return! loop (result |> addResult db)
                | Close -> ()
                | CloseWait ch -> ch.Reply ()
            }
            loop { Status = Map.empty })

    /// Deletes existing database and backup files to reset the build log. Should be called before starting the build.
    let cleanupDb dbpath (logger : ILogger) = 
        let bkpath = impl.makeBkpath dbpath
        [dbpath; bkpath]
        |> List.iter (fun path ->
            if System.IO.File.Exists(path) then
                try
                    System.IO.File.Delete(path)
                    logger.Log Level.Message "Deleted database file: %s" path
                with ex ->
                    logger.Log Level.Warning "Failed to delete %s: %s" path (ex.Message)
        )
            
/// Utility methods to manipulate build stats
module internal Step =

    type DateTime = System.DateTime

    let start name = {StepInfo.Empty with Name = name; Start = DateTime.Now}

    /// <summary>
    /// Updated last (current) build step
    /// </summary>
    let updateLastStep fn = function
        | {Steps = current :: rest} as result -> {result with Steps = (fn current) :: rest}
        | result -> result

    /// <summary>
    /// Adds specific amount to a wait time
    /// </summary>
    let updateWaitTime delta = updateLastStep (fun c -> {c with WaitTime = c.WaitTime + delta})
    let updateTotalDuration =
        let durationSince (startTime: DateTime) = int (DateTime.Now - startTime).TotalMilliseconds * 1<ms>
        updateLastStep (fun c -> {c with OwnTime = (durationSince c.Start) - c.WaitTime})
    let lastStep = function
        | {Steps = current :: _} -> current
        | _ -> start "dummy"
