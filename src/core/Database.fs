namespace Xake.Storage

open Xake

/// Messages accepted by the database agent (docs/features/idle-perf, L2). The agent keeps the
/// records and writes them; it decides nothing and reads no project file. Reads go through
/// the snapshot it publishes after every message (`Db.Snapshot`).
type internal DatabaseApi =
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

/// The database file: the lock, the backup, the load with upgrade, scrubbing and compaction.
module internal DbFile =
    type private IOFile = System.IO.File
    type private Stream = System.IO.Stream
    type private FileStream = System.IO.FileStream
    type private FileMode = System.IO.FileMode
    type private FileAccess = System.IO.FileAccess
    type private FileShare = System.IO.FileShare

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
        let isOld = loaded |> Option.exists (fun c -> c.Version < version)
        let resultCount = records |> List.sumBy (function DbRecord.Result _ -> 1 | _ -> 0)
        if isOld || hasClearSecrets || resultCount > live.Length * 5 then
            if isOld then log Level.Message "Upgrading database to version %s" version
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

    /// Deletes the database and backup files.
    let cleanup dbpath (logger : ILogger) =
        for path in [ dbpath; makeBkpath dbpath ] do
            if IOFile.Exists(path) then
                try
                    IOFile.Delete(path)
                    logger.Log Level.Message "Deleted database file: %s" path
                with ex ->
                    logger.Log Level.Warning "Failed to delete %s: %s" path (ex.Message)

/// The database agent and its writer (docs/features/idle-perf, L2).
module internal DbAgent =
    type private IOFile = System.IO.File
    type private Stream = System.IO.Stream
    type private FileStream = System.IO.FileStream
    type private FileMode = System.IO.FileMode
    type private FileAccess = System.IO.FileAccess
    type private FileShare = System.IO.FileShare


    /// The snapshot the agent publishes: the database value after its last message.
    [<Sealed>]
    type SnapshotCell(db0: Database) =
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
    let agent (logger: ILogger) (cell: SnapshotCell) (table0: Persist.PathTable) (stream0: Stream option) (close: unit -> unit) =
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
                        let db = Database.addResult db result
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

/// The build database (docs/features/idle-perf, L2): the agent that keeps and writes the records,
/// and the snapshot it publishes. An opaque handle: the engine reaches it through `Db`'s functions.
[<Sealed>]
type Db internal (agent: MailboxProcessor<DatabaseApi>, cell: DbAgent.SnapshotCell) =
    /// The records after the agent's last message; reading it posts nothing.
    member _.Snapshot : Database = cell.Value
    member internal _.Post (msg: DatabaseApi) = agent.Post msg
    member internal _.PostAndReply (build: AsyncReplyChannel<'r> -> DatabaseApi, ?timeout: int) =
        match timeout with
        | Some t -> agent.PostAndReply(build, t)
        | None -> agent.PostAndReply build
    member internal _.PostAndAsyncReply (build: AsyncReplyChannel<'r> -> DatabaseApi) = agent.PostAndAsyncReply build
    interface System.IDisposable with
        member _.Dispose() = (agent :> System.IDisposable).Dispose()

/// The database's functions: the read API (public) and the engine's operations (internal).
module Db =
    /// The format version this Xake writes.
    let formatVersion = Persist.version


    /// Every record of a database file in order, paths resolved against `root` (history until
    /// the next compaction); an older version yields its converted records. No lock: a running
    /// build's database can be read; a torn last record ends the sequence, a missing or short file
    /// (a compaction in progress) gives an empty sequence.
    let readRecords (root: string) (dbpath: string) : DbRecord seq =
        try
            use fs = new System.IO.FileStream(dbpath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite ||| System.IO.FileShare.Delete)
            (Persist.read root fs).Records |> Seq.ofList
        with
        | :? System.IO.FileNotFoundException | :? System.IO.DirectoryNotFoundException
        | :? System.IO.EndOfStreamException -> Seq.empty

    /// The state the engine loads: the last result per target, the last state per file and the
    /// last epoch in the file.
    let readDatabase (root: string) (dbpath: string) : Database =
        readRecords root dbpath |> Persist.toDatabase

    /// The record of a target from the snapshot (no message).
    let tryGetResult (db: Db) (target: Target) = Map.tryFind target db.Snapshot.Status

    let private create (logger: ILogger) (db0: Database) table stream close =
        let cell = DbAgent.SnapshotCell db0
        new Db(DbAgent.agent logger cell table stream close, cell)

    /// Opens the database exclusively (`<dbpath>.lock`, waiting up to `lockWait` for another run);
    /// `reset` (`--resetdb`) deletes it first, under the lock (A5). `root` is the project root:
    /// paths under it are stored relative to it; its value is never written.
    let internal openWith (lockWait: System.TimeSpan) (reset: bool) (root: string) (dbpath: string) (logger : ILogger) =
        let lockStream = DbFile.acquireLock dbpath lockWait logger
        try
            if reset then DbFile.cleanup dbpath logger
            let db, table, stream = DbFile.openDatabaseFile root dbpath logger
            create logger db table stream (fun () -> try stream |> Option.iter (fun s -> s.Dispose()) finally lockStream.Dispose())
        with _ ->
            lockStream.Dispose()
            reraise ()

    /// Opens the database, waiting up to 30 s for another run.
    let internal openFile (root: string) (dbpath: string) (logger : ILogger) =
        openWith (System.TimeSpan.FromSeconds 30.0) false root dbpath logger

    /// A database that keeps its records in memory only (`NoPersist`).
    let internal memoryOnly () =
        create (CustomLogger (fun _ -> false) ignore) Database.empty (Persist.PathTable.empty ".") None ignore

    /// Deletes the database and backup files, under the database lock (`--resetdb` resets
    /// inside `openWith` instead, A5).
    let internal delete dbpath (logger : ILogger) =
        let lockStream = DbFile.acquireLock dbpath (System.TimeSpan.FromSeconds 30.0) logger
        try DbFile.cleanup dbpath logger
        finally lockStream.Dispose()

    /// Writes file records, each only if its record is still `expected` (compare-and-set); the
    /// conflicts with their current records.
    let internal apply (db: Db) (writes: (File * FileState option * FileState) list) =
        db.PostAndAsyncReply(fun ch -> Apply (writes, ch))

    /// Stores a result; completes once the snapshot holds it.
    let internal store (db: Db) (result: BuildResult) = db.PostAndAsyncReply(fun ch -> Store (result, ch))

    /// Starts a new run: the next epoch.
    let internal newRun (db: Db) = db.PostAndAsyncReply NewRun

    /// From now on records stay in memory and nothing is appended (`--dump`, `--dryrun`, A8).
    let internal keepInMemory (db: Db) = db.Post InMemory

    /// Drains the writer, closes the file and releases the lock.
    let internal close (db: Db) = db.PostAndAsyncReply CloseWait
