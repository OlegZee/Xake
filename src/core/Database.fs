namespace Xake

module BuildLog =
    open Xake
    open System

    /// Current database schema version, written to the header of every database file.
    /// 0.5 adds the Dependency case ArtifactDep2 (dependee target with its change stamp).
    /// 0.6 adds the Dependency case SecretDep (a secret variable's digest, never its value).
    /// 0.7 (Xake 5.0): tagged append-only records, a path table relative to the project root,
    /// a `FileRec` per file, epochs instead of per-dependency stamps, varint ids and lengths.
    let XakeDbVersion = "0.7"

    /// Oldest database version this Xake still reads. A database of 0.4-0.6 is read with the
    /// legacy picklers, converted and rewritten as the current version on open.
    let XakeDbMinReadVersion = "0.4"

    /// In-memory build database: the last result per target and the last state per file.
    type Database = { Status : Map<Target, BuildResult>; Files : Map<File, FileState> }

    (* API *)

    /// Creates a new build result
    let makeResult target =
        { Targets = target
          Built = DateTime.Now
          Epoch = 0L
          Depends = []
          Steps = []
          StaticCount = 0 }

    /// Creates a new database
    let newDatabase() = { Database.Status = Map.empty; Files = Map.empty }

    /// Adds result to a database
    let internal addResult db result =
        { db with Status = result.Targets |> List.fold (fun m i -> Map.add i result m) db.Status }

/// Type alias for MailboxProcessor used as a message-passing agent.
type 't Agent = 't MailboxProcessor

module Storage =
    open Xake
    open BuildLog
    open System.Collections.Generic

    type private IOFile = System.IO.File
    type private FileStream = System.IO.FileStream
    type private FileMode = System.IO.FileMode
    type private FileAccess = System.IO.FileAccess
    type private FileShare = System.IO.FileShare

    /// A record of a database file, paths resolved (the public read API).
    [<RequireQualifiedAccess>]
    type DbRecord =
        | Result of BuildResult
        | FileState of File * FileState

    /// Two stamps within this many milliseconds are equal.
    let internal TimeToleranceMs = 10.0

    module internal Persist =
        open Pickler

        type DatabaseHeader =
            { XakeSign : string
              XakeVer : string
              ScriptDate : Timestamp }

        let dbHeader =
            wrap
                ((fun (sign, ver, scriptDate) -> { DatabaseHeader.XakeSign = sign; XakeVer = ver; ScriptDate = scriptDate }),
                 fun h -> (h.XakeSign, h.XakeVer, h.ScriptDate))
                (triple str str date)

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

        let envVar = wrap (EnvVar, fun (EnvVar(n, v) | OtherwiseFail (n,v)) -> n, v) (pair str (option str))
        let var = wrap (Var, fun (Var(n, v)| OtherwiseFail (n,v)) -> n, v) (pair str (option str))
        let secretDep = wrap (SecretDep, fun (SecretDep(n, src, d) | OtherwiseFail (n, src, d)) -> (n, src, d)) (triple str secretSource secretDigest)

        // ---- 0.7 --------------------------------------------------------------------------

        /// A record of a 0.7 file. `DirRec`/`PathRec` make the path table; the last `FileRec`
        /// per file and the last `ResultRec` per target win.
        type Rec =
            | DirRec of dirId: int * root: int * dir: string
            | PathRec of fileId: int * dirId: int * name: string
            | FileRec of File * FileState
            | ResultRec of BuildResult

        /// The path table: a file is `File.display root` split at the last '/', its directory a
        /// `DirRec` (root 1 = the project root, 0 = an absolute directory) and its name a
        /// `PathRec`. The root's value is never written. Ids allocated by pickling are kept as
        /// pending records until the writer puts them out (or rolls them back).
        type PathTable(root: string) =
            let show = File.display root
            let rootName = (File.make root).Name
            let fileIds = Dictionary<File, int>()
            let files = ResizeArray<File>()
            let dirIds = Dictionary<string, int>(System.StringComparer.Ordinal)
            let dirs = ResizeArray<int * string>()
            let pending = ResizeArray<Rec>()
            let dirKey (r: Core.int) (d: string) = Operators.string r + d

            member _.Id (f: File) : int =
                match fileIds.TryGetValue f with
                | true, id -> id
                | _ ->
                    let s = show f
                    let r = if System.String.Equals(s, f.Name, System.StringComparison.Ordinal) then 0 else 1
                    let i = s.LastIndexOf '/'
                    let dir, name = if i < 0 then "", s else s.Substring(0, i), s.Substring(i + 1)
                    let dirId =
                        match dirIds.TryGetValue (dirKey r dir) with
                        | true, d -> d
                        | _ ->
                            let d = dirs.Count
                            dirs.Add((r, dir))
                            dirIds.[dirKey r dir] <- d
                            pending.Add(DirRec (d, r, dir))
                            d
                    let id = files.Count
                    files.Add f
                    fileIds.[f] <- id
                    pending.Add(PathRec (id, dirId, name))
                    id

            member _.File (id: int) =
                if id < 0 || id >= files.Count then failwithf "unknown file id %d" id
                files.[id]

            member _.AddDir (id: int, r: int, dir: string) =
                if id <> dirs.Count || (r <> 0 && r <> 1) then failwithf "unexpected directory record %d" id
                dirs.Add((r, dir))
                dirIds.[dirKey r dir] <- id

            member _.AddPath (id: int, dirId: int, name: string) =
                if id <> files.Count || dirId < 0 || dirId >= dirs.Count then failwithf "unexpected path record %d" id
                let r, dir = dirs.[dirId]
                let full =
                    match r, dir with
                    | 1, "" -> rootName + "/" + name
                    | 1, d -> rootName + "/" + d + "/" + name
                    | _, d -> d + "/" + name
                let f = File.make full
                files.Add f
                fileIds.[f] <- id

            /// The DirRec/PathRec records allocated since the last call.
            member _.TakePending () = let xs = List.ofSeq pending in pending.Clear(); xs

            /// Forgets the ids allocated since the last `TakePending` (a record failed to pickle).
            member _.Rollback () =
                for p in Seq.rev (List.ofSeq pending) do
                    match p with
                    | PathRec (id, _, _) -> fileIds.Remove files.[id] |> ignore; files.RemoveAt id
                    | DirRec (id, r, d) -> dirIds.Remove (dirKey r d) |> ignore; dirs.RemoveAt id
                    | _ -> ()
                pending.Clear()

        /// The 0.7 record pickler over a path table.
        let record (table: PathTable) =
            let file = { pickle = (fun f st -> varint.pickle (table.Id f) st); unpickle = fun st -> table.File (varint.unpickle st) }
            let target =
                alt (function FileTarget _ -> 0 | PhonyAction _ -> 1)
                    [| wrap (FileTarget, fun (FileTarget f | OtherwiseFail f) -> f) file
                       wrap (PhonyAction, fun (PhonyAction a | OtherwiseFail a) -> a) str |]
            let dependency =
                alt (function
                    | FileDep _ -> 0
                    | TargetDep (FileTarget _) -> 1
                    | TargetDep (PhonyAction _) -> 2
                    | GetFiles _ -> 3
                    | EnvVar _ -> 4
                    | Var _ -> 5
                    | AlwaysRerun -> 6
                    | SecretDep _ -> 7)
                    [| wrap (FileDep, fun (FileDep f | OtherwiseFail f) -> f) file
                       wrap (FileTarget >> TargetDep, fun (TargetDep (FileTarget f) | OtherwiseFail f) -> f) file
                       wrap (PhonyAction >> TargetDep, fun (TargetDep (PhonyAction p) | OtherwiseFail p) -> p) str
                       wrap (GetFiles, fun (GetFiles (fs, fl) | OtherwiseFail (fs, fl)) -> fs, fl)
                           (pair filesetPickler (wrap (Filelist, fun (Filelist l) -> l) (vlist file)))
                       envVar
                       var
                       wrap0 AlwaysRerun
                       secretDep |]
            let hash = option (wrap ((fun (x: int64) -> Operators.uint64 x), (fun (x: uint64) -> Operators.int64 x)) int64)
            let fileState =
                wrap ((fun (stamp, size, h, changed) -> { Stamp = stamp; Size = size - 1L; Hash = h; Changed = changed }),
                      fun s -> (s.Stamp, max 0L (s.Size + 1L), s.Hash, s.Changed))
                     (quad date varint64 hash varint64)
            let result =
                wrap ((fun ((epoch, built, targets, deps), (steps, staticCount)) ->
                        { Targets = targets; Built = built; Epoch = epoch; Depends = deps; Steps = steps; StaticCount = staticCount }),
                      fun r -> (r.Epoch, r.Built, r.Targets, r.Depends), (r.Steps, r.StaticCount))
                     (pair (quad varint64 date (vlist target) (vlist dependency)) (pair (vlist step) varint))
            alt (function DirRec _ -> 0 | PathRec _ -> 1 | FileRec _ -> 2 | ResultRec _ -> 3)
                [| wrap (DirRec, fun (DirRec (i, r, d) | OtherwiseFail (i, r, d)) -> i, r, d) (triple varint varint str)
                   wrap (PathRec, fun (PathRec (i, d, n) | OtherwiseFail (i, d, n)) -> i, d, n) (triple varint varint str)
                   wrap (FileRec, fun (FileRec (f, s) | OtherwiseFail (f, s)) -> f, s) (pair file fileState)
                   wrap (ResultRec, fun (ResultRec r | OtherwiseFail r) -> r) result |]

        // ---- 0.4-0.6 (read, and written by the tests' fixture) ---------------------------

        /// A dependency as databases 0.4-0.6 recorded it.
        type OldDep =
            | Plain of Dependency
            /// FileDep (file, stamp)
            | OldFile of File * Timestamp
            /// ArtifactDep (0.4, no stamp)
            | OldArtifact of Target
            /// ArtifactDep2 (target, stamp)
            | OldArtifact2 of Target * Timestamp

        type OldResult = { OTargets: Target list; OBuilt: Timestamp; ODepends: OldDep list; OSteps: StepInfo list }

        /// The 0.4-0.6 result pickler: paths are strings, joined with the root (an absolute one
        /// stays as is); a pickled file is its absolute path.
        let legacyResult (root: string) =
            let file = wrap ((fun s -> File.make (root </> s)), fun (f: File) -> f.FullName) str
            let target =
                alt (function FileTarget _ -> 0 | PhonyAction _ -> 1)
                    [| wrap (FileTarget, fun (FileTarget f | OtherwiseFail f) -> f) file
                       wrap (PhonyAction, fun (PhonyAction a | OtherwiseFail a) -> a) str |]
            let plain (pu: PU<Dependency>) = wrap (Plain, fun (Plain d | OtherwiseFail d) -> d) pu
            let dependency =
                alt (function
                    | OldArtifact _ -> 0
                    | OldFile _ -> 1
                    | Plain (EnvVar _) -> 2
                    | Plain (Var _) -> 3
                    | Plain AlwaysRerun -> 4
                    | Plain (GetFiles _) -> 5
                    | OldArtifact2 _ -> 6
                    | Plain (SecretDep _) -> 7
                    | d -> failwithf "not a 0.6 dependency: %A" d)
                    [| wrap (OldArtifact, fun (OldArtifact t | OtherwiseFail t) -> t) target
                       wrap (OldFile, fun (OldFile (f, ts) | OtherwiseFail (f, ts)) -> (f, ts)) (pair file date)
                       plain envVar
                       plain var
                       plain (wrap0 AlwaysRerun)
                       plain (wrap (GetFiles, fun (GetFiles (fs, fl) | OtherwiseFail (fs, fl)) -> fs, fl)
                                   (pair filesetPickler6 (wrap (Filelist, fun (Filelist l) -> l) (list file))))
                       wrap (OldArtifact2, fun (OldArtifact2 (t, ts) | OtherwiseFail (t, ts)) -> (t, ts)) (pair target date)
                       plain secretDep |]
            wrap ((fun (t, b, d, s) -> { OTargets = t; OBuilt = b; ODepends = d; OSteps = s }),
                  fun r -> (r.OTargets, r.OBuilt, r.ODepends, r.OSteps))
                 (quad (list target) date (list dependency) (list step))

        /// The distinct results of a target -> result map (a multi-target result once).
        let distinctResults (status: Map<Target, 'r>) =
            let seen = HashSet<obj>(HashIdentity.Reference)
            status |> Map.toList |> List.map snd |> List.filter (fun r -> seen.Add (box r))

        /// Converts the live results of a 0.4-0.6 file (decision 1b, "Upgrade"): a `FileRec` per
        /// file with the latest stamp recorded for it (size unknown, `changed = 1`); `Epoch = 1`
        /// for a result whose every stamp is that latest one and whose phony dependees finished no
        /// later than recorded, else 0.
        let convert (results: OldResult list) : (File * FileState) list * BuildResult list =
            let latest = Dictionary<File, Timestamp>()
            for r in results do
                for d in r.ODepends do
                    match d with
                    | OldFile (f, t) | OldArtifact2 (FileTarget f, t) ->
                        match latest.TryGetValue f with
                        | true, l when l >= t -> ()
                        | _ -> latest.[f] <- t
                    | _ -> ()
            let phonyBuilt =
                results |> List.collect (fun r -> r.OTargets |> List.choose (function PhonyAction _ as t -> Some (t, r.OBuilt) | _ -> None)) |> Map.ofList
            let current = function
                | OldFile (f, t) | OldArtifact2 (FileTarget f, t) -> abs (t - latest.[f]).TotalMilliseconds < TimeToleranceMs
                | OldArtifact2 (t, stamp) -> (match phonyBuilt.TryFind t with Some b -> b.Ticks <= stamp.Ticks | None -> false)
                | OldArtifact _ -> false
                | Plain _ -> true
            let toDep = function
                | Plain d -> d
                | OldFile (f, _) -> FileDep f
                | OldArtifact t | OldArtifact2 (t, _) -> TargetDep t
            let files = [ for kv in latest -> kv.Key, { Stamp = kv.Value; Size = -1L; Hash = None; Changed = 1L } ]
            files,
            results |> List.map (fun r ->
                { Targets = r.OTargets; Built = r.OBuilt
                  Epoch = (if List.forall current r.ODepends then 1L else 0L)
                  Depends = r.ODepends |> List.map toDep; Steps = r.OSteps; StaticCount = 0 })

        // ---- reading -----------------------------------------------------------------------

        exception VersionTooNew of string
        exception VersionTooOld of string

        type ReadResult =
            { Version: string
              /// the records in file order (an older version: converted)
              Records: DbRecord list
              Table: PathTable
              /// the number of `Result` records in the file
              ResultCount: int
              /// the end of the last whole record
              GoodEnd: int64
              Torn: bool }

        /// Reads a database stream. A torn last record (end of file inside it) ends the records;
        /// any other failure raises.
        let read (root: string) (stream: System.IO.Stream) : ReadResult =
            use reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, true)
            let header = dbHeader.unpickle reader
            let v = header.XakeVer
            if v > XakeDbVersion then raise (VersionTooNew v)
            if v < XakeDbMinReadVersion then raise (VersionTooOld v)
            let table = PathTable root
            let records = ResizeArray<DbRecord>()
            let mutable goodEnd = stream.Position
            let mutable torn = false
            let mutable count = 0
            if v < XakeDbVersion then
                let pu = legacyResult root
                let mutable status = Map.empty
                while not torn && stream.Position < stream.Length do
                    try
                        let r = pu.unpickle reader
                        status <- r.OTargets |> List.fold (fun m t -> Map.add t r m) status
                        count <- count + 1
                        goodEnd <- stream.Position
                    with :? System.IO.EndOfStreamException -> torn <- true
                let files, results = convert (distinctResults status)
                files |> List.iter (DbRecord.FileState >> records.Add)
                results |> List.iter (DbRecord.Result >> records.Add)
            else
                let pu = record table
                while not torn && stream.Position < stream.Length do
                    try
                        match pu.unpickle reader with
                        | DirRec (i, r, d) -> table.AddDir (i, r, d)
                        | PathRec (i, d, n) -> table.AddPath (i, d, n)
                        | FileRec (f, s) -> records.Add (DbRecord.FileState (f, s))
                        | ResultRec r -> records.Add (DbRecord.Result r); count <- count + 1
                        goodEnd <- stream.Position
                    with :? System.IO.EndOfStreamException -> torn <- true
            { Version = v; Records = List.ofSeq records; Table = table; ResultCount = count; GoodEnd = goodEnd; Torn = torn }

        /// Applies a record to the in-memory database.
        let apply (db: Database) = function
            | DbRecord.Result r -> addResult db r
            | DbRecord.FileState (f, s) -> { db with Files = Map.add f s db.Files }

        /// The largest epoch of the records (0 for none).
        let lastEpoch (records: DbRecord seq) =
            records |> Seq.fold (fun m r -> match r with DbRecord.Result r -> max m r.Epoch | DbRecord.FileState (_, s) -> max m s.Changed) 0L

        // ---- writing -----------------------------------------------------------------------

        /// Appends records to a 0.7 stream: each call is pickled into a buffer, its new
        /// DirRec/PathRec in front, and written with one Write. A record that fails to pickle
        /// raises and writes nothing (its ids are rolled back); a failed write stops every later
        /// append (logged once by the caller through `onStop`).
        type Writer(table: PathTable, stream: System.IO.Stream, onStop: exn -> unit) =
            let pu = record table
            let mutable stopped = false
            member _.Stopped = stopped
            member _.Append (recs: Rec list) =
                if not stopped then
                    use body = new System.IO.MemoryStream()
                    use bw = new System.IO.BinaryWriter(body)
                    try
                        recs |> List.iter (fun r -> pu.pickle r bw)
                        bw.Flush()
                    with _ ->
                        table.Rollback()
                        reraise ()
                    use all = new System.IO.MemoryStream()
                    use aw = new System.IO.BinaryWriter(all)
                    table.TakePending () |> List.iter (fun r -> pu.pickle r aw)
                    aw.Write(body.ToArray())
                    aw.Flush()
                    try
                        stream.Write(all.ToArray(), 0, Operators.int all.Length)
                        stream.Flush()
                    with ex ->
                        stopped <- true
                        onStop ex
            member _.Dispose () = stream.Dispose()

        let writeHeader (stream: System.IO.Stream) =
            use w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true)
            dbHeader.pickle { XakeSign = "XAKE"; XakeVer = XakeDbVersion; ScriptDate = System.DateTime.Now } w
            w.Flush()

        /// The files a result refers to (targets and dependencies).
        let filesOf (r: BuildResult) =
            [ for t in r.Targets do match t with FileTarget f -> yield f | _ -> ()
              for d in r.Depends do
                match d with
                | FileDep f | TargetDep (FileTarget f) -> yield f
                | GetFiles (_, Filelist fs) -> yield! fs
                | _ -> () ]

    module private impl =
        open Persist

        let makeBkpath dbpath = dbpath <.> "bak"

        // Retries `openFile` briefly on a transient sharing violation before giving up.
        // A just-closed handle (this process's own previous run, or another tool briefly
        // touching the file) can take the OS a moment to fully release after Dispose()
        // returns (seen back-to-back across quick test runs); without this a perfectly
        // good database gets mistaken for a corrupt one and thrown away, which in turn
        // makes the next build think nothing was ever built.
        let openWithRetry (openFile: unit -> #System.IO.Stream) =
            let maxAttempts = 5
            let rec attempt n =
                try
                    openFile ()
                with :? System.IO.IOException when n < maxAttempts ->
                    System.Threading.Thread.Sleep(20)
                    attempt (n + 1)
            attempt 1

        /// Takes `<db>.lock` exclusively; waits up to `wait` for another run, then fails.
        let acquireLock (dbpath: string) (wait: System.TimeSpan) (logger: ILogger) : FileStream =
            let lockPath = dbpath + ".lock"
            let started = System.Diagnostics.Stopwatch.StartNew()
            let rec attempt logged =
                let res =
                    try Choice1Of2 (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    with :? System.IO.IOException as ex -> Choice2Of2 ex
                match res with
                | Choice1Of2 s -> s
                | Choice2Of2 _ when started.Elapsed < wait ->
                    if not logged then logger.Log Level.Message "waiting for another Xake run on this database"
                    System.Threading.Thread.Sleep 50
                    attempt true
                | Choice2Of2 _ ->
                    raise (XakeException (sprintf "another Xake run uses this database (%s)" (System.IO.Path.GetFileName lockPath)))
            attempt false

        /// Writes a fresh 0.7 file: the header, the `FileRec`s of the files the results refer
        /// to, the results. Returns the table and the stream open for appending.
        let rewrite (root: string) (dbpath: string) (db: Database) (results: BuildResult list) onStop =
            let stream = openWithRetry (fun () -> new FileStream(dbpath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            writeHeader stream
            let table = PathTable root
            let writer = new Writer(table, stream, onStop)
            let files = results |> List.collect filesOf |> List.distinct
            for f in files do
                match db.Files.TryFind f with
                | Some s -> writer.Append [ FileRec (f, s) ]
                | None -> ()
            for r in results do writer.Append [ ResultRec r ]
            writer

        let openDatabaseFile (root: string) dbpath (logger : ILogger) onStop =
            let log = logger.Log
            let bkpath = makeBkpath dbpath
            // if exists backup restore
            if IOFile.Exists(bkpath) then
                log Level.Message "Backup file found ('%s'), restoring db" bkpath
                try IOFile.Delete(dbpath) with _ -> ()
                IOFile.Move(bkpath, dbpath)
            let recreate () = try IOFile.Delete(dbpath) with _ -> ()
            let loaded =
                if not (IOFile.Exists dbpath) then None
                else
                    try
                        let r =
                            use fs = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            read root fs
                        if r.Torn then
                            log Level.Message "The last record of the database was incomplete (an interrupted run); it is cut off"
                            use fs = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Open, FileAccess.Write, FileShare.Read))
                            fs.SetLength r.GoodEnd
                        Some r
                    with
                    | VersionTooNew v ->
                        log Level.Message "database written by a newer Xake (version %s), recreating" v
                        recreate (); None
                    | VersionTooOld v ->
                        log Level.Message "database version %s is no longer read, recreating" v
                        recreate (); None
                    | ex ->
                        log Warning "Failed to read database, so recreating. Got \"%s\"" <| ex.ToString()
                        recreate (); None
            let db0 = match loaded with Some r -> r.Records |> List.fold apply (newDatabase()) | None -> newDatabase()
            let epoch = match loaded with Some r -> lastEpoch r.Records | None -> 0L
            // a clear value recorded (by an older Xake, or before the name was declared secret)
            // for a variable now declared secret becomes its digest, and the file is rewritten
            let scrubbed =
                db0.Status |> Map.map (fun _ r ->
                    let deps = r.Depends |> List.map Secrets.scrub
                    if deps = r.Depends then r else { r with Depends = deps })
            let hasClearSecrets =
                db0.Status |> Map.exists (fun key r -> not (obj.ReferenceEquals(r, scrubbed.[key])))
            let db = { db0 with Status = scrubbed }
            let live = distinctResults db.Status
            let isOld = match loaded with Some r -> r.Version < XakeDbVersion | None -> false
            let resultCount = match loaded with Some r -> r.ResultCount | None -> 0
            let writer =
                if isOld || hasClearSecrets || resultCount > live.Length * 5 then
                    if isOld then log Level.Message "Upgrading database to version %s" XakeDbVersion
                    elif hasClearSecrets then log Level.Message "Removing the values of secret variables from the database"
                    else log Level.Message "Compacting database"
                    IOFile.Move(dbpath, bkpath)
                    let w = rewrite root dbpath db live onStop
                    IOFile.Delete(bkpath)
                    w
                else
                    let stream = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    if stream.Position = 0L then writeHeader stream
                    let table = match loaded with Some r -> r.Table | None -> PathTable root
                    new Writer(table, stream, onStop)
            db, epoch, writer

        /// One observation of a file (decision 1c): a new record, an equal stat (within the
        /// tolerance, size equal or unknown: an unknown size is filled in, `changed` kept) or a
        /// moved stat (`changed = epoch`; no hash in phase 1). Returns the state and whether it
        /// is to be appended.
        let observe (epoch: int64) (known: FileState option) (stamp: Timestamp, size: int64) =
            match known with
            | Some k when abs (stamp - k.Stamp).TotalMilliseconds < TimeToleranceMs && (k.Size < 0L || k.Size = size) ->
                if k.Size < 0L then { k with Size = size }, true else k, false
            | _ -> { Stamp = stamp; Size = size; Hash = None; Changed = epoch }, true

        let stat (f: File) =
            let fi = System.IO.FileInfo f.FullName
            if fi.Exists then Some (fi.LastWriteTime, fi.Length) else None

    /// Messages accepted by the database agent.
    type DatabaseApi =
        | GetResult of Target * AsyncReplyChannel<Option<BuildResult>>
        /// Stores a result; the agent gives it the epoch of the current run.
        | Store of BuildResult
        /// Observes a file (at most once per run unless `force`: a rule's output right after
        /// the rule); replies its state, None for a missing file.
        | Observe of File * force: bool * AsyncReplyChannel<FileState option>
        /// Starts a new run: the next epoch, observations afresh.
        | NewRun
        | Close
        | CloseWait of AsyncReplyChannel<unit>

    /// Every record of a database file in order, paths resolved against `root` (history until
    /// the next compaction); an older version yields its converted records. No lock: a running
    /// build's database can be read; a torn last record ends the sequence.
    let readRecords (root: string) (dbpath: string) : DbRecord seq =
        if not (IOFile.Exists dbpath) then Seq.empty
        else
            use fs = new FileStream(dbpath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            (Persist.read root fs).Records |> Seq.ofList

    /// The state the engine loads (the last result per target, the last state per file) and the
    /// last epoch in the file.
    let readDatabase (root: string) (dbpath: string) : BuildLog.Database * int64 =
        let records = readRecords root dbpath |> List.ofSeq
        records |> List.fold Persist.apply (newDatabase()), Persist.lastEpoch records

    /// The agent over a database state; `append` writes a record (persistent) or does nothing.
    let private agent (logger: ILogger) (db0: Database) (epoch0: int64) (append: Persist.Rec list -> unit) (close: unit -> unit) =
        // Logging the agent's own failure must not throw out of the loop again.
        let logError fmt = Printf.kprintf (fun s -> try logger.Log Error "%s" s with _ -> ()) fmt
        let mbox = new MailboxProcessor<_>(fun mbox ->
            // An exception escaping the loop would kill the agent silently and every later
            // PostAndReply (GetResult, CloseWait) would wait forever. So each message is
            // guarded: a failed store is logged, nothing of it reaches the file and the result
            // is kept in memory for this run (the next run rebuilds that target), a failed
            // lookup answers "no record" (a rebuild), and close always replies.
            let failedStores = ref 0
            let observed = HashSet<File>()
            let warnUnsaved () =
                if failedStores.Value > 0 then
                    try logger.Log Warning "Database not fully saved: %d result(s) failed to store, the next build may rebuild them" failedStores.Value
                    with _ -> ()
            let closeNow () =
                do (try logger.Log Info "Closing database" with _ -> ())
                do warnUnsaved ()
                try close ()
                with ex -> logError "Database agent failed to close: %O" ex
            let rec loop (db: Database) (epoch: int64) (started: bool) =
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
                        return! loop db epoch started
                    | Store result ->
                        let result = { result with Epoch = epoch }
                        try append [ Persist.ResultRec result ]
                        with ex ->
                            failedStores.Value <- failedStores.Value + 1
                            let name = try (result.Targets |> List.map (fun t -> t.ShortName) |> String.concat ", ") with _ -> "?"
                            logError "Database agent failed to store the result of '%s': %O" name ex
                        let db' =
                            try result |> addResult db
                            with ex -> logError "Database agent failed to record a result: %O" ex; db
                        return! loop db' epoch started
                    | Observe (file, force, chnl) ->
                        let db' =
                            try
                                if not force && observed.Contains file then
                                    chnl.Reply (if File.exists file then db.Files.TryFind file else None)
                                    db
                                else
                                    observed.Add file |> ignore
                                    match impl.stat file with
                                    | None -> chnl.Reply None; db
                                    | Some s ->
                                        let state, changed = impl.observe epoch (db.Files.TryFind file) s
                                        if changed then
                                            try append [ Persist.FileRec (file, state) ]
                                            with ex -> logError "Database agent failed to store the state of '%s': %O" file.Name ex
                                        chnl.Reply (Some state)
                                        { db with Files = Map.add file state db.Files }
                            with ex ->
                                logError "Database agent failed to observe '%s': %O" file.Name ex
                                (try chnl.Reply None with _ -> ())
                                db
                        return! loop db' epoch started
                    | NewRun ->
                        observed.Clear()
                        return! loop db (if started then epoch + 1L else epoch) true
                    | Close ->
                        closeNow ()
                        return ()
                    | CloseWait ch ->
                        closeNow ()
                        ch.Reply()
                        return ()
                }
            loop db0 epoch0 false)
        mbox.Error.Add (fun ex -> logError "Database agent stopped: %O" ex)
        mbox.Start()
        mbox

    /// <summary>
    /// Opens the database exclusively (`<dbpath>.lock`, waiting up to `lockWait` for another run).
    /// </summary>
    let internal openDbWith (lockWait: System.TimeSpan) (root: string) (dbpath: string) (logger : ILogger) =
        let lockStream = impl.acquireLock dbpath lockWait logger
        try
            let stopped = ref false
            let onStop (ex: exn) =
                if not stopped.Value then
                    stopped.Value <- true
                    try logger.Log Error "Database write failed, no further records are saved in this run: %s" ex.Message with _ -> ()
            let db, epoch, writer = impl.openDatabaseFile root dbpath logger onStop
            agent logger db (epoch + 1L) writer.Append (fun () -> try writer.Dispose() finally lockStream.Dispose())
        with _ ->
            lockStream.Dispose()
            reraise ()

    /// <summary>
    /// Opens database.
    /// </summary>
    /// <param name="root">The project root: paths under it are stored relative to it; its value is never written.</param>
    /// <param name="dbpath">Full xake database file name</param>
    /// <param name="logger"></param>
    let openDb (root: string) (dbpath: string) (logger : ILogger) =
        openDbWith (System.TimeSpan.FromSeconds 30.0) root dbpath logger

    /// <summary>
    /// In-memory database for use when NoPersist = true.
    /// Tracks build results within the session but does not write to disk.
    /// </summary>
    let noopDb () =
        agent (CustomLogger (fun _ -> false) ignore) (newDatabase()) 1L ignore ignore

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
