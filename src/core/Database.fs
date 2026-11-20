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

    /// Oldest database version this Xake still reads. A database of 0.4-0.6 is read by
    /// `LegacyRead`, converted and rewritten as the current version on open.
    let XakeDbMinReadVersion = "0.4"

    /// In-memory build database: the last result per target, the last state per file and the
    /// last epoch (the largest in the file on load, the current run's after `NewRun`).
    type Database = { Status : Map<Target, BuildResult>; Files : Map<File, FileState>; Epoch : int }

    (* API *)

    /// Creates a new build result
    let makeResult target =
        { Targets = target
          Built = DateTime.Now
          Epoch = 0
          Depends = []
          Steps = []
          StaticCount = 0 }

    /// Creates a new database
    let newDatabase() = { Database.Status = Map.empty; Files = Map.empty; Epoch = 0 }

    /// Adds result to a database
    let internal addResult db result =
        { db with Status = result.Targets |> List.fold (fun m i -> Map.add i result m) db.Status
                  Epoch = max db.Epoch result.Epoch }

/// Type alias for MailboxProcessor used as a message-passing agent.
type 't Agent = 't MailboxProcessor

module Storage =
    open Xake
    open BuildLog

    type private IOFile = System.IO.File
    type private Stream = System.IO.Stream
    type private FileStream = System.IO.FileStream
    type private FileMode = System.IO.FileMode
    type private FileAccess = System.IO.FileAccess
    type private FileShare = System.IO.FileShare

    /// A record of a database file, paths resolved (the public read API).
    [<RequireQualifiedAccess>]
    type DbRecord =
        | Result of BuildResult
        | FileState of File * FileState

    /// Why a file is observed (decisions 1c, A1; content cut-off).
    type ObserveMode =
        /// by a verdict: once per run, then the cached state
        | Verdict
        /// a body records the file (`FileDep`, a fileset member): statted again; a moved stat is a
        /// change unless another body recorded the file in this run (A1)
        | Recording
        /// a rule's output right after the rule: statted again; with `cutoff` always hashed, without
        /// (`noContentCutoff`) never hashed and changed in this run
        | Output of cutoff: bool
        /// an output before its rule runs: hashed if it has no hash and its stat is unchanged
        | PreHash

    /// A file's stat: missing, or its write time and length (docs/features/idle-perf, L0).
    type FileStat =
        | Missing
        | Present of stamp: Timestamp * size: int64

    module FileStat =
        /// One stat of the file.
        let take (file: File) =
            let fi = File.getFileInfo file
            if fi.Exists then Present (fi.LastWriteTime, fi.Length) else Missing

    /// What hashing a file gave.
    type HashResult =
        /// the stat the hash was taken at (unchanged before and after hashing) and the hash
        | Hashed of stamp: Timestamp * size: int64 * hash: uint64
        /// the file could not be read, or its stat moved while it was hashed (its content was not
        /// stable): a change without a hash; the stat taken after the attempt
        | Unreadable of FileStat

    /// An observation of a file: missing, or its state with whether its content changed after
    /// the run `since`.
    type Observation =
        | Absent
        | Unchanged of FileState
        | Changed of FileState

    /// Content hashes (docs/content-cutoff.md; docs/features/idle-perf, L0).
    module Content =
        /// The file's stat and the first 8 bytes of the SHA-256 of its content, taken together:
        /// stat, hash, stat. `Unreadable` when the stat moved meanwhile, or the file cannot be read.
        let hash (file: File) =
            try
                let before = File.getFileInfo file
                if not before.Exists then Unreadable Missing
                else
                    let bytes =
                        use sha = System.Security.Cryptography.SHA256.Create()
                        use stream = new System.IO.FileStream(before.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                              System.IO.FileShare.ReadWrite ||| System.IO.FileShare.Delete)
                        sha.ComputeHash stream
                    match FileStat.take file with
                    | Present (stamp, size) when stamp = before.LastWriteTime && size = before.Length ->
                        Hashed (before.LastWriteTime, before.Length, System.BitConverter.ToUInt64(bytes, 0))
                    | after -> Unreadable after
            with _ -> Unreadable (try FileStat.take file with _ -> Missing)

    /// The decision about one observation of a file, without I/O or state (docs/features/idle-perf,
    /// L1): the rules of decision 1c, A1 and content cut-off (docs/implnotes.md, "Epochs").
    module internal FileRule =
        /// What the run knows about the file besides its record.
        type RuleContext = {
            /// The epoch a change is stamped with (the newest run handed out).
            Epoch: int
            /// The run hashes (`--content-cutoff` on).
            Hashing: bool
            /// A verdict already observed the file in this run: its record is the answer.
            SeenByVerdict: bool
            /// A body already recorded the file in this run (A1): a moved stat is not a change.
            RecordedBefore: bool }

        type Decision =
            /// answer from this state; nothing to write
            | Keep of FileState option
            /// write this state (it differs from the known one)
            | Write of FileState
            /// hash the file, then `decideHashed`
            | NeedHash

        /// Equal within 10 ms, the size equal or unknown.
        let sameStat (k: FileState) (stamp: Timestamp) size =
            abs (stamp - k.Stamp).TotalMilliseconds < 10.0 && (k.Size < 0L || k.Size = size)

        let private write (known: FileState option) state = if Some state = known then Keep known else Write state

        /// One observation of a file with the record `known` and the stat taken now: a new state,
        /// an equal stat (an unknown size filled in, `changed` kept), or a moved stat (to hash, or
        /// changed in this epoch without a hash).
        let decide (known: FileState option) (stat: FileStat) (mode: ObserveMode) (rc: RuleContext) =
            match stat with
            | Missing -> Keep None
            | Present _ when mode = Verdict && rc.SeenByVerdict -> Keep known
            | Present (stamp, size) ->
                let moved () = { Stamp = stamp; Size = size; Hash = None; Changed = rc.Epoch }
                match mode, known with
                | Output false, _ -> write known (moved ())
                | Output true, _ when rc.Hashing -> NeedHash
                | PreHash, Some k when rc.Hashing && k.Hash.IsNone && sameStat k stamp size -> NeedHash
                | PreHash, _ -> Keep known
                | _, Some k when sameStat k stamp size -> write known { k with Size = size }
                | Recording, Some k when rc.RecordedBefore -> Keep (Some k)   // another body recorded it in this run (A1)
                | (Verdict | Recording), Some _ when rc.Hashing -> NeedHash
                | _ -> write known (moved ())

        /// The decision after hashing, against the same `known` record.
        let decideHashed (known: FileState option) (hashed: HashResult) (mode: ObserveMode) (rc: RuleContext) =
            match hashed, known with
            | Unreadable _, _ when mode = PreHash -> Keep known
            | Unreadable stat, _ ->
                match stat with
                | Missing -> Keep None
                | Present (stamp, size) -> write known { Stamp = stamp; Size = size; Hash = None; Changed = rc.Epoch }
            | Hashed (stamp, size, h), _ ->
                let state = { Stamp = stamp; Size = size; Hash = Some h; Changed = rc.Epoch }
                match mode, known with
                | PreHash, Some k -> if sameStat k stamp size then write known { k with Hash = Some h } else Keep known
                // the same content: `changed` kept (cut-off)
                | _, Some k when k.Hash = Some h -> write known { state with Changed = k.Changed }
                // an output never hashed, not moved: the hash is filled in
                | Output _, Some k when k.Hash.IsNone && sameStat k stamp size -> write known { state with Changed = k.Changed }
                | _ -> write known state

        /// The reply for a state: changed after the run `since`, or not.
        let classify since = function
            | None -> Absent
            | Some (st: FileState) when st.Changed > since -> Changed st
            | Some st -> Unchanged st

    /// Messages accepted by the database agent (docs/features/idle-perf, L2). The agent keeps the
    /// records and writes them; it decides nothing and reads no project file. Reads go through
    /// the snapshot it publishes after every message (`Db.Snapshot`).
    type DatabaseApi =
        /// Writes file records, each only if the record is still the one the caller decided
        /// against (compare-and-set, per file), in one append; replies with the conflicts: the
        /// file and its current record.
        | Apply of (File * FileState option * FileState) list * AsyncReplyChannel<(File * FileState option) list>
        /// Stores a result (its `Epoch` is the run its task started in); replies once the
        /// snapshot holds it.
        | Store of BuildResult * AsyncReplyChannel<unit>
        /// Starts a new run: replies the next epoch.
        | NewRun of AsyncReplyChannel<int>
        /// Records stay in memory and nothing is appended (`--dump`, `--dryrun`, A8).
        | InMemory
        | Close
        | CloseWait of AsyncReplyChannel<unit>

    module internal Persist =
        open Pickler

        type DatabaseHeader = { XakeSign : string; XakeVer : string; ScriptDate : Timestamp }

        let dbHeader =
            wrap ((fun (sign, ver, scriptDate) -> { XakeSign = sign; XakeVer = ver; ScriptDate = scriptDate }),
                  fun h -> (h.XakeSign, h.XakeVer, h.ScriptDate))
                 (triple str str date)

        /// A record of a 0.7 file: `DirRec`/`PathRec` make the path table; of the data the last
        /// state per file and the last result per target win.
        type Rec =
            | DirRec of dirId: int * root: int * dir: string
            | PathRec of fileId: int * dirId: int * name: string
            | Data of DbRecord

        /// The path table (B1): a file is `File.display Root` split at the last '/', its directory
        /// a `DirRec` (root 1 = the project root, 0 = an absolute directory) and its name a
        /// `PathRec`. The root's value is never written.
        type PathTable =
            { Root: string; RootPrefix: string
              Ids: Map<File, int>; Files: Map<int, File>
              Dirs: Map<int * string, int>; DirPrefix: Map<int, string> }

        module PathTable =
            let empty root =
                let n = (File.make root).Name
                { Root = root; RootPrefix = (if n.EndsWith "/" then n else n + "/")
                  Ids = Map.empty; Files = Map.empty; Dirs = Map.empty; DirPrefix = Map.empty }

            let private addDir (r, dir) t =
                match Map.tryFind (r, dir) t.Dirs with
                | Some d -> t, d, []
                | None ->
                    let d = t.Dirs.Count
                    let prefix = match r, dir with 1, "" -> t.RootPrefix | 1, _ -> t.RootPrefix + dir + "/" | _ -> dir + "/"
                    { t with Dirs = Map.add (r, dir) d t.Dirs; DirPrefix = Map.add d prefix t.DirPrefix }, d, [ DirRec (d, r, dir) ]

            let private addFile f id t = { t with Ids = Map.add f id t.Ids; Files = Map.add id f t.Files }

            /// The table with the file, and its new `DirRec`/`PathRec`.
            let add (f: File) (t: PathTable) : PathTable * Rec list =
                if t.Ids.ContainsKey f then t, []
                else
                    let s = File.display t.Root f
                    let i = s.LastIndexOf '/'
                    let t, d, recs = addDir ((if s = f.Name then 0 else 1), (if i < 0 then "" else s.Substring(0, i))) t
                    let id = t.Ids.Count
                    addFile f id t, recs @ [ PathRec (id, d, s.Substring(i + 1)) ]

            /// The table after a record read from the file.
            let load (t: PathTable) = function
                | DirRec (d, r, dir) when d = t.Dirs.Count && (r = 0 || r = 1) -> let t, _, _ = addDir (r, dir) t in t
                | PathRec (id, d, name) when id = t.Ids.Count && t.DirPrefix.ContainsKey d -> addFile (File.make (t.DirPrefix.[d] + name)) id t
                | Data _ -> t
                | r -> failwithf "unexpected path record %A" r

        let private bytes =
            { pickle = (fun (b: byte[]) (st: OutState) -> st.Write(b.Length); st.Write(b))
              unpickle = fun (st: InState) -> st.ReadBytes(st.ReadInt32()) }

        /// The 0.7 record pickler; files are ids of the table `table ()` gives at the time.
        let record (table: unit -> PathTable) =
            let file =
                { pickle = (fun f st -> varint.pickle (table ()).Ids.[f] st)
                  unpickle = fun st -> let id = varint.unpickle st in match (table ()).Files.TryFind id with Some f -> f | None -> failwithf "unknown file id %d" id }
            let target =
                alt (function FileTarget _ -> 0 | PhonyAction _ -> 1)
                    [| wrap (FileTarget, fun (FileTarget f | OtherwiseFail f) -> f) file
                       wrap (PhonyAction, fun (PhonyAction a | OtherwiseFail a) -> a) str |]
            let step =
                wrap ((fun (n, s, o, w) -> { StepInfo.Name = n; Start = s; OwnTime = o * 1<ms>; WaitTime = w * 1<ms> }),
                      fun s -> (s.Name, s.Start, s.OwnTime / 1<ms>, s.WaitTime / 1<ms>)) (quad str date int int)
            let secretDigest =
                alt (function SecretAbsent -> 0 | SecretPresent -> 1 | SecretHmac _ -> 2)
                    [| wrap0 SecretAbsent
                       wrap0 SecretPresent
                       wrap (SecretHmac, fun (SecretHmac (salt, h) | OtherwiseFail (salt, h)) -> salt, h) (pair bytes bytes) |]
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
                       wrap (EnvVar, fun (EnvVar (n, v) | OtherwiseFail (n, v)) -> n, v) (pair str (option str))
                       wrap (Var, fun (Var (n, v) | OtherwiseFail (n, v)) -> n, v) (pair str (option str))
                       wrap0 AlwaysRerun
                       wrap (SecretDep, fun (SecretDep (n, src, d) | OtherwiseFail (n, src, d)) -> (n, src, d))
                           (triple str (alt (function SecretEnv -> 0 | SecretArg -> 1) [| wrap0 SecretEnv; wrap0 SecretArg |]) secretDigest) |]
            let fileState =
                wrap ((fun (stamp, size, h, changed) -> { Stamp = stamp; Size = size - 1L; Hash = h; Changed = changed }),
                      fun s -> (s.Stamp, max 0L (s.Size + 1L), s.Hash, s.Changed))
                     (quad date varint64 (option (wrap ((fun (x: int64) -> Operators.uint64 x), (fun (x: uint64) -> Operators.int64 x)) int64)) varint)
            let result =
                wrap ((fun ((epoch, built, targets, deps), (steps, staticCount)) ->
                        { Targets = targets; Built = built; Epoch = epoch; Depends = deps; Steps = steps; StaticCount = staticCount }),
                      fun r -> (r.Epoch, r.Built, r.Targets, r.Depends), (r.Steps, r.StaticCount))
                     (pair (quad varint date (vlist target) (vlist dependency)) (pair (vlist step) varint))
            alt (function DirRec _ -> 0 | PathRec _ -> 1 | Data (DbRecord.FileState _) -> 2 | Data (DbRecord.Result _) -> 3)
                [| wrap (DirRec, fun (DirRec (i, r, d) | OtherwiseFail (i, r, d)) -> i, r, d) (triple varint varint str)
                   wrap (PathRec, fun (PathRec (i, d, n) | OtherwiseFail (i, d, n)) -> i, d, n) (triple varint varint str)
                   wrap (DbRecord.FileState >> Data, fun (Data (DbRecord.FileState (f, s)) | OtherwiseFail (f, s)) -> f, s) (pair file fileState)
                   wrap (DbRecord.Result >> Data, fun (Data (DbRecord.Result r) | OtherwiseFail r) -> r) result |]

        /// The files a record refers to.
        let filesOf = function
            | DbRecord.FileState (f, _) -> [ f ]
            | DbRecord.Result r ->
                [ for t in r.Targets do match t with FileTarget f -> yield f | _ -> ()
                  for d in r.Depends do
                    match d with
                    | FileDep f | TargetDep (FileTarget f) -> yield f
                    | GetFiles (_, Filelist fs) -> yield! fs
                    | _ -> () ]

        /// The bytes of records, the `DirRec`/`PathRec`s of their new files in front, and the
        /// table with those files. Raises when a record does not pickle (the caller keeps its table).
        let encode (t: PathTable) (recs: DbRecord list) =
            let paths, t = recs |> List.collect filesOf |> List.mapFold (fun t f -> let t, rs = PathTable.add f t in rs, t) t
            let pu = record (fun () -> t)
            use ms = new System.IO.MemoryStream()
            use w = new System.IO.BinaryWriter(ms)
            for r in List.concat paths @ List.map Data recs do pu.pickle r w
            w.Flush()
            t, ms.ToArray()

        let writeHeader (stream: Stream) =
            use w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true)
            dbHeader.pickle { XakeSign = "XAKE"; XakeVer = XakeDbVersion; ScriptDate = System.DateTime.Now } w
            w.Flush()

        exception VersionTooNew of string
        exception VersionTooOld of string

        /// The records of a file in order with the table they built; `GoodEnd` is the end of the
        /// last whole record, `Torn` tells that a record after it was cut short.
        type Contents = { Version: string; Records: DbRecord list; Table: PathTable; Torn: bool; GoodEnd: int64 }

        /// Reads a database stream; an older version through `LegacyRead`.
        let read (root: string) (stream: Stream) : Contents =
            use reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, true)
            let v = (dbHeader.unpickle reader).XakeVer
            if v > XakeDbVersion then raise (VersionTooNew v)
            if v < XakeDbMinReadVersion then raise (VersionTooOld v)
            if v < XakeDbVersion then
                let files, results = LegacyRead.read root reader
                { Version = v; Table = PathTable.empty root; Torn = false; GoodEnd = stream.Position
                  Records = List.map DbRecord.FileState files @ List.map DbRecord.Result results }
            else
                let mutable t = PathTable.empty root
                let pu = record (fun () -> t)
                let records = ResizeArray<DbRecord>()
                let mutable goodEnd, torn = stream.Position, false
                while not torn && stream.Position < stream.Length do
                    try
                        match pu.unpickle reader with
                        | Data d -> records.Add d
                        | r -> t <- PathTable.load t r
                        goodEnd <- stream.Position
                    with :? System.IO.EndOfStreamException -> torn <- true
                { Version = v; Records = List.ofSeq records; Table = t; Torn = torn; GoodEnd = goodEnd }

        /// The database of records read in order.
        let toDatabase (records: DbRecord seq) =
            records |> Seq.fold (fun db -> function
                | DbRecord.Result r -> addResult db r
                | DbRecord.FileState (f, s) -> { db with Files = Map.add f s db.Files; Epoch = max db.Epoch s.Changed }) (newDatabase())

    module private impl =
        open Persist

        let makeBkpath dbpath = dbpath <.> "bak"

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

        /// Loads the database file (upgrading, scrubbing or compacting it when needed): the
        /// database, its path table and the stream to append to (None when a rewrite failed).
        let openDatabaseFile (root: string) dbpath (logger : ILogger) =
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
                        let c =
                            use fs = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            read root fs
                        if c.Torn then
                            log Level.Message "The last record of the database was incomplete (an interrupted run); it is cut off"
                            use fs = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Open, FileAccess.Write, FileShare.Read))
                            fs.SetLength c.GoodEnd
                        Some c
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
            let records = loaded |> Option.map (fun c -> c.Records) |> Option.defaultValue []
            let db0 = toDatabase records
            // a clear value recorded (by an older Xake, or before the name was declared secret)
            // for a variable now declared secret becomes its digest, and the file is rewritten
            let scrubbed =
                db0.Status |> Map.map (fun _ r ->
                    let deps = r.Depends |> List.map Secrets.scrub
                    if deps = r.Depends then r else { r with Depends = deps })
            let hasClearSecrets =
                db0.Status |> Map.exists (fun key r -> not (obj.ReferenceEquals(r, scrubbed.[key])))
            let db = { db0 with Status = scrubbed }
            let seen = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
            let live = db.Status |> Map.toList |> List.map snd |> List.filter (box >> seen.Add)
            let isOld = loaded |> Option.exists (fun c -> c.Version < XakeDbVersion)
            let resultCount = records |> List.sumBy (function DbRecord.Result _ -> 1 | _ -> 0)
            if isOld || hasClearSecrets || resultCount > live.Length * 5 then
                if isOld then log Level.Message "Upgrading database to version %s" XakeDbVersion
                elif hasClearSecrets then log Level.Message "Removing the values of secret variables from the database"
                else log Level.Message "Compacting database"
                IOFile.Move(dbpath, bkpath)
                let stream = openWithRetry (fun () -> new FileStream(dbpath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                // the live results and the states of the files they refer to
                let states = live |> List.collect (DbRecord.Result >> filesOf) |> List.distinct |> List.choose (fun f -> db.Files.TryFind f |> Option.map (fun s -> DbRecord.FileState (f, s)))
                try
                    writeHeader stream
                    let table, bytes = encode (PathTable.empty root) (states @ List.map DbRecord.Result live)
                    stream.Write(bytes, 0, bytes.Length)
                    stream.Flush()
                    IOFile.Delete(bkpath)
                    db, table, Some (stream :> Stream)
                with ex ->
                    // the backup stays: the next open restores it (R4)
                    log Error "Database rewrite failed, the backup is kept: %s" ex.Message
                    stream.Dispose()
                    db, PathTable.empty root, None
            else
                let stream = openWithRetry (fun () -> new FileStream(dbpath, FileMode.Append, FileAccess.Write, FileShare.Read))
                if stream.Position = 0L then writeHeader stream
                db, (loaded |> Option.map (fun c -> c.Table) |> Option.defaultValue (PathTable.empty root)), Some (stream :> Stream)

    /// Every record of a database file in order, paths resolved against `root` (history until
    /// the next compaction); an older version yields its converted records. No lock: a running
    /// build's database can be read; a torn last record ends the sequence, a missing or short file
    /// (a compaction in progress) gives an empty sequence.
    let readRecords (root: string) (dbpath: string) : DbRecord seq =
        try
            use fs = new FileStream(dbpath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            (Persist.read root fs).Records |> Seq.ofList
        with
        | :? System.IO.FileNotFoundException | :? System.IO.DirectoryNotFoundException
        | :? System.IO.EndOfStreamException -> Seq.empty

    /// The state the engine loads: the last result per target, the last state per file and the
    /// last epoch in the file.
    let readDatabase (root: string) (dbpath: string) : BuildLog.Database =
        readRecords root dbpath |> Persist.toDatabase

    /// Deletes the database and backup files.
    let private cleanup dbpath (logger : ILogger) =
        for path in [ dbpath; impl.makeBkpath dbpath ] do
            if IOFile.Exists(path) then
                try
                    IOFile.Delete(path)
                    logger.Log Level.Message "Deleted database file: %s" path
                with ex ->
                    logger.Log Level.Warning "Failed to delete %s: %s" path (ex.Message)

    /// The snapshot the agent publishes: the database value after its last message.
    [<Sealed>]
    type private SnapshotCell(db0: Database) =
        [<VolatileField>]
        let mutable value = db0
        member _.Value with get () = value and set v = value <- v

    /// The agent over a database state, its path table and the stream it appends to (None: in
    /// memory only); `close` releases the stream and the lock (docs/features/idle-perf, L2).
    type private WriterMsg =
        /// records to append, in the agent's order
        | Append of DbRecord list
        /// nothing more is appended (`InMemory`)
        | StopWriting
        /// appends what is queued, closes the stream and releases the lock, then replies
        | Drain of AsyncReplyChannel<unit>

    /// The writer (docs/features/idle-perf): owns the path table and the stream, encodes the
    /// records the agent hands it, in order, and appends each batch with one write. The agent
    /// publishes and replies without waiting for it; `Close` drains it.
    let private writer (logger: ILogger) (table0: Persist.PathTable) (stream0: Stream option) (close: unit -> unit) =
        // Logging the writer's own failure must not throw out of the loop again.
        let logError fmt = Printf.kprintf (fun s -> try logger.Log Error "%s" s with _ -> ()) fmt
        let mbox = new MailboxProcessor<WriterMsg>(fun mbox ->
            // A failed store is logged and nothing of it reaches the file; the result is kept in
            // memory for this run (the next run rebuilds that target), and close always replies.
            let failedStores = ref 0
            /// Appends records. A record that fails to pickle raises and leaves the table as it
            /// was; a failed write stops appending until the database is closed (A4).
            let persist (table, stream) recs =
                match stream with
                | None -> table, stream
                | Some (s: Stream) ->
                    let table', bytes = Persist.encode table recs
                    try
                        s.Write(bytes, 0, bytes.Length)
                        s.Flush()
                        table', stream
                    with ex ->
                        logError "Database write failed, no further records are saved until it is closed: %s" ex.Message
                        table', None
            let rec loop out = async {
                let! msg = mbox.Receive()
                match msg with
                | Append recs ->
                    let out =
                        try persist out recs
                        with ex ->
                            match recs with
                            | [ DbRecord.Result result ] ->
                                failedStores.Value <- failedStores.Value + 1
                                let name = try (result.Targets |> List.map (fun t -> t.Name) |> String.concat ", ") with _ -> "?"
                                logError "Database agent failed to store the result of '%s': %O" name ex
                            | _ -> logError "Database agent failed to store the state of %d file(s): %O" recs.Length ex
                            out
                    return! loop out
                | StopWriting -> return! loop (fst out, None)
                | Drain ch ->
                    do (try logger.Log Info "Closing database" with _ -> ())
                    if failedStores.Value > 0 then
                        try logger.Log Warning "Database not fully saved: %d result(s) failed to store, the next build may rebuild them" failedStores.Value
                        with _ -> ()
                    try close ()
                    with ex -> logError "Database agent failed to close: %O" ex
                    ch.Reply () }
            loop (table0, stream0))
        mbox.Error.Add (fun ex -> logError "Database writer stopped: %O" ex)
        mbox.Start()
        mbox

    /// The agent over a database state (docs/features/idle-perf, L2): it applies writes and results
    /// to the records in memory, publishes the snapshot and replies; the writer appends them to the
    /// file behind it.
    let private agent (logger: ILogger) (cell: SnapshotCell) (table0: Persist.PathTable) (stream0: Stream option) (close: unit -> unit) =
        // Logging the agent's own failure must not throw out of the loop again.
        let logError fmt = Printf.kprintf (fun s -> try logger.Log Error "%s" s with _ -> ()) fmt
        let write = writer logger table0 stream0 close
        let mbox = new MailboxProcessor<_>(fun mbox ->
            // An exception escaping the loop would kill the agent silently and every later
            // PostAndReply (Store, CloseWait) would wait forever: nothing here does I/O.
            let publish db = cell.Value <- db
            let rec loop (db: Database) =
                async {
                    let! msg = mbox.Receive()
                    match msg with
                    | Apply (writes, ch) ->
                        let accepted, conflicts = writes |> List.partition (fun (f, expected, _) -> Map.tryFind f db.Files = expected)
                        let db = { db with Files = accepted |> List.fold (fun m (f, _, st) -> Map.add f st m) db.Files }
                        publish db
                        ch.Reply [ for f, _, _ in conflicts -> f, Map.tryFind f db.Files ]
                        if not accepted.IsEmpty then write.Post (Append [ for f, _, st in accepted -> DbRecord.FileState (f, st) ])
                        return! loop db
                    | Store (result, ch) ->
                        let db = addResult db result
                        publish db
                        ch.Reply ()
                        write.Post (Append [ DbRecord.Result result ])
                        return! loop db
                    | NewRun ch ->
                        let db = { db with Epoch = db.Epoch + 1 }
                        publish db
                        ch.Reply db.Epoch
                        return! loop db
                    | InMemory ->
                        write.Post StopWriting
                        return! loop db
                    | Close | CloseWait _ ->
                        do! write.PostAndAsyncReply Drain
                        match msg with CloseWait ch -> ch.Reply() | _ -> ()
                }
            loop cell.Value)
        mbox.Error.Add (fun ex -> logError "Database agent stopped: %O" ex)
        mbox.Start()
        mbox

    type private HasherMsg =
        | HashFile of File * FileStat * onComputed: (System.TimeSpan -> unit) * AsyncReplyChannel<HashResult>
        | Hashed' of File * FileStat * HashResult

    /// Hashes behind their own mailbox (docs/features/idle-perf, L3): one computation per (file,
    /// stat) while it runs, every other asker gets the same result; at most as many hashes at
    /// once as there are cores. It knows nothing of records, epochs or runs.
    [<Sealed>]
    type Hasher() =
        let slots = new System.Threading.SemaphoreSlim(System.Environment.ProcessorCount)
        let mbox = MailboxProcessor<HasherMsg>.Start(fun mbox ->
            let running = System.Collections.Generic.Dictionary<File * FileStat, ResizeArray<AsyncReplyChannel<HashResult>>>(HashIdentity.Structural)
            let rec loop () = async {
                let! msg = mbox.Receive()
                match msg with
                | HashFile (file, stat, onComputed, ch) ->
                    match running.TryGetValue ((file, stat)) with
                    | true, waiters -> waiters.Add ch
                    | _ ->
                        running.[(file, stat)] <- ResizeArray [ ch ]
                        Async.Start (async {
                            do! slots.WaitAsync() |> Async.AwaitTask
                            let result =
                                try
                                    let watch = System.Diagnostics.Stopwatch.StartNew()
                                    let r = Content.hash file
                                    (try onComputed watch.Elapsed with _ -> ())
                                    r
                                finally slots.Release() |> ignore
                            mbox.Post (Hashed' (file, stat, result)) })
                | Hashed' (file, stat, result) ->
                    match running.TryGetValue ((file, stat)) with
                    | true, waiters ->
                        running.Remove ((file, stat)) |> ignore
                        for ch in waiters do ch.Reply result
                    | _ -> ()
                return! loop () }
            loop ())
        /// The hash of the file as it is at `stat`; `onComputed` gets the time of a hash this
        /// request computed (not of one it shared).
        member _.Hash (file: File) (stat: FileStat) (onComputed: System.TimeSpan -> unit) =
            mbox.PostAndAsyncReply(fun ch -> HashFile (file, stat, onComputed, ch))

    /// The build database: the agent that keeps and writes the records, the snapshot it
    /// publishes and the hasher (docs/features/idle-perf, L2-L3).
    [<Sealed>]
    type Db private (agent: MailboxProcessor<DatabaseApi>, cell: SnapshotCell, hasher: Hasher) =
        static member internal Create (logger: ILogger) (db0: Database) table stream close =
            let cell = SnapshotCell db0
            new Db(agent logger cell table stream close, cell, Hasher())
        /// The records after the agent's last message; reading it posts nothing.
        member _.Snapshot : Database = cell.Value
        member _.Hasher = hasher
        member _.Post (msg: DatabaseApi) = agent.Post msg
        member _.PostAndReply (build: AsyncReplyChannel<'r> -> DatabaseApi, ?timeout: int) =
            match timeout with
            | Some t -> agent.PostAndReply(build, t)
            | None -> agent.PostAndReply build
        member _.PostAndAsyncReply (build: AsyncReplyChannel<'r> -> DatabaseApi) = agent.PostAndAsyncReply build
        interface System.IDisposable with
            member _.Dispose() = (agent :> System.IDisposable).Dispose()

    /// What a run knows about files besides their records (docs/features/idle-perf, L4): the
    /// files observed in it (a verdict's later observation of one answers from the record, after
    /// the first observation has written it) and the files a body recorded (A1). One per run.
    [<Sealed>]
    type RunFiles() =
        let observed = System.Collections.Concurrent.ConcurrentDictionary<File, System.Threading.Tasks.Task>()
        let recorded = System.Collections.Concurrent.ConcurrentDictionary<File, byte>()
        /// Claims the file's first observation in this run: `Choice1Of2` the completion to set
        /// once its record is written; `Choice2Of2` the first observation's task.
        member internal _.Claim (file: File) =
            match observed.TryGetValue file with
            | true, task -> Choice2Of2 task
            | _ ->
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
                if observed.TryAdd(file, tcs.Task :> System.Threading.Tasks.Task) then Choice1Of2 tcs
                else Choice2Of2 observed.[file]
        /// Whether a body already recorded the file in this run (and marks it recorded).
        member internal _.RecordedBefore (file: File) = not (recorded.TryAdd(file, 0uy))

    /// Where an observation runs: the database, the run's files, whether the run hashes, and the
    /// counter of hashes computed.
    type ObserveContext = { Db: Db; Run: RunFiles; Hashing: bool; OnHashed: System.TimeSpan -> unit }

    /// The record of a target from the snapshot (no message).
    let tryGetResult (db: Db) (target: Target) = Map.tryFind target db.Snapshot.Status

    /// Stores a result; completes once the snapshot holds it.
    let storeResult (db: Db) (result: BuildResult) = db.PostAndAsyncReply(fun ch -> Store (result, ch))

    /// Observes a group of files (docs/features/idle-perf, L5): their stats on this thread, the
    /// rule against one snapshot, the hashes (in parallel, through the hasher), every write in one
    /// `Apply`, a conflict decided again on a fresh stat (at most 3 times). Replies per file, in
    /// the order given, whether its content changed after the run `since`. A verdict's later
    /// observation of a file in the run waits for the first one and answers from the record.
    let observeFiles (oc: ObserveContext) (mode: ObserveMode) (since: int) (files: File list) = async {
        let distinct = files |> List.distinct
        let firsts = ResizeArray<System.Threading.Tasks.TaskCompletionSource<unit>>()
        let waits = ResizeArray<File * System.Threading.Tasks.Task>()
        let todo = ResizeArray<File * bool>()
        for f in distinct do
            let seen =
                if mode = PreHash then None
                else
                    match oc.Run.Claim f with
                    | Choice1Of2 tcs -> firsts.Add tcs; None
                    | Choice2Of2 task -> if mode = Verdict then Some task else None
            match seen with
            | Some task -> waits.Add((f, task))
            | None -> todo.Add((f, mode = Recording && oc.Run.RecordedBefore f))
        let states = System.Collections.Generic.Dictionary<File, FileState option>()
        let stats = System.Collections.Generic.Dictionary<File, FileStat>()
        // the full stat for the files decided here; a verdict's later observation needs only
        // whether the file exists (below)
        for f, _ in todo do stats.[f] <- FileStat.take f
        try
            // decides the files against their records and writes the changed ones
            let rec settle (round: int) (pending: (File * bool * FileState option * FileStat) list) = async {
                if not pending.IsEmpty then
                    let epoch = oc.Db.Snapshot.Epoch
                    let rc recordedBefore = { FileRule.Epoch = epoch; FileRule.Hashing = oc.Hashing; FileRule.SeenByVerdict = false; FileRule.RecordedBefore = recordedBefore }
                    // decided here; only the files to hash go through the hasher, in parallel
                    let first = [| for f, rb, known, stat in pending -> f, rb, known, stat, FileRule.decide known stat mode (rc rb) |]
                    let! hashed =
                        [ for f, rb, known, stat, d in first do
                            if d = FileRule.NeedHash then
                                yield async {
                                    let! h = oc.Db.Hasher.Hash f stat oc.OnHashed
                                    return f, FileRule.decideHashed known h mode (rc rb) } ]
                        |> function [] -> async.Return [||] | hs -> Async.Parallel hs
                    let hashedBy = dict hashed
                    let decided = [| for f, rb, known, _, d in first -> f, rb, known, (if d = FileRule.NeedHash then hashedBy.[f] else d) |]
                    let writes = ResizeArray()
                    for f, _, known, d in decided do
                        match d with
                        | FileRule.Keep st -> states.[f] <- st
                        | FileRule.Write st -> states.[f] <- Some st; writes.Add((f, known, st))
                        | FileRule.NeedHash -> states.[f] <- known   // decideHashed never asks again
                    if writes.Count > 0 then
                        let! conflicts = oc.Db.PostAndAsyncReply(fun ch -> Apply (List.ofSeq writes, ch))
                        if round >= 3 then
                            for f, current in conflicts do states.[f] <- current
                        else
                            let byFile = decided |> Array.map (fun (f, rb, _, _) -> f, rb) |> dict
                            let again = [ for f, current in conflicts -> f, byFile.[f], current, FileStat.take f ]
                            do! settle (round + 1) again }
            let snapshot = oc.Db.Snapshot
            do! settle 1 [ for f, rb in todo -> f, rb, Map.tryFind f snapshot.Files, stats.[f] ]
        finally
            for tcs in firsts do tcs.TrySetResult () |> ignore
        // a verdict's later observation: after the first one, the record now if the file exists now
        for f, task in waits do
            if not task.IsCompleted then
                do! (try Async.AwaitTask (task :?> System.Threading.Tasks.Task<unit>) with _ -> async.Return ())
            states.[f] <- (if File.exists f then Map.tryFind f oc.Db.Snapshot.Files else None)
        return [| for f in files -> FileRule.classify since states.[f] |] }

    /// <summary>
    /// Opens the database exclusively (`<dbpath>.lock`, waiting up to `lockWait` for another run);
    /// `reset` (`--resetdb`) deletes it first, under the lock (A5).
    /// </summary>
    let internal openDbWith (lockWait: System.TimeSpan) (reset: bool) (root: string) (dbpath: string) (logger : ILogger) =
        let lockStream = impl.acquireLock dbpath lockWait logger
        try
            if reset then cleanup dbpath logger
            let db, table, stream = impl.openDatabaseFile root dbpath logger
            Db.Create logger db table stream (fun () -> try stream |> Option.iter (fun s -> s.Dispose()) finally lockStream.Dispose())
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
        openDbWith (System.TimeSpan.FromSeconds 30.0) false root dbpath logger

    /// <summary>
    /// In-memory database for use when NoPersist = true.
    /// Tracks build results within the session but does not write to disk.
    /// </summary>
    let noopDb () =
        Db.Create (CustomLogger (fun _ -> false) ignore) (newDatabase()) (Persist.PathTable.empty ".") None ignore

    /// Deletes existing database and backup files to reset the build log, under the database
    /// lock (`--resetdb` resets inside `openDb` instead, A5).
    let cleanupDb dbpath (logger : ILogger) =
        let lockStream = impl.acquireLock dbpath (System.TimeSpan.FromSeconds 30.0) logger
        try cleanup dbpath logger
        finally lockStream.Dispose()

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
    /// The milliseconds since `startTime`.
    let msSince (startTime: DateTime) = int (DateTime.Now - startTime).TotalMilliseconds * 1<ms>
    let updateTotalDuration =
        updateLastStep (fun c -> {c with OwnTime = (msSince c.Start) - c.WaitTime})
    let lastStep = function
        | {Steps = current :: _} -> current
        | _ -> start "dummy"
