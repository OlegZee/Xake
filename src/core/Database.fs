namespace Xake

module BuildLog = 
    open Xake
    open System
    
    /// Current database schema version, written to the header of every database file.
    /// 0.5 adds the Dependency case ArtifactDep2 (dependee target with its change stamp).
    /// 0.6 adds the Dependency case SecretDep (a secret variable's digest, never its value).
    let XakeDbVersion = "0.6"

    /// Oldest database version this Xake still reads. A database of 0.4 or 0.5 has no
    /// ArtifactDep2/SecretDep records (its pickles are a subset of 0.6), so it loads as is and
    /// is rewritten with the current header on open. Older databases are discarded.
    let XakeDbMinReadVersion = "0.4"

    /// In-memory build database mapping targets to their last build results.
    type Database = { Status : Map<Target, BuildResult> }
    
    (* API *)

    /// Creates a new build result
    let makeResult target = 
        { Targets = target
          Built = DateTime.Now
          Depends = []
          Steps = [] }
    
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
    
    module private Persist = 
        open Pickler

        type DatabaseHeader = 
            { XakeSign : string
              XakeVer : string
              ScriptDate : Timestamp }
        
        let file = wrap (File.make, fun a -> a.FullName) str
        
        let target = 
            alt (function 
                | FileTarget _ -> 0
                | PhonyAction _ -> 1) 
                [|  wrap (File.make >> FileTarget, fun (FileTarget f | OtherwiseFail f) -> f.Name) str
                    wrap (PhonyAction, (fun (PhonyAction a | OtherwiseFail a) -> a)) str |]
        
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

        // Fileset of FilesetOptions * FilesetElement list
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
                   wrap (GetFiles, fun (GetFiles(fs, fi)| OtherwiseFail (fs,fi)) -> fs, fi)  (pair filesetPickler filelistPickler)
                   wrap (ArtifactDep2, fun (ArtifactDep2(t, ts) | OtherwiseFail (t, ts)) -> (t, ts)) (pair target date)
                   wrap (SecretDep, fun (SecretDep(n, src, d) | OtherwiseFail (n, src, d)) -> (n, src, d)) (triple str secretSource secretDigest) |]
        
        let result = 
            wrap 
                ((fun (r, built, deps, steps) -> 
                 { Targets = r
                   Built = built
                   Depends = deps
                   Steps = steps }), 
                 fun r -> (r.Targets, r.Built, r.Depends, r.Steps)) 
                (quad (list target) date (list dependency) (list step))
        
        let dbHeader = 
            wrap 
                ((fun (sign, ver, scriptDate) -> 
                 { DatabaseHeader.XakeSign = sign
                   XakeVer = ver
                   ScriptDate = scriptDate }), 
                 fun h -> (h.XakeSign, h.XakeVer, h.ScriptDate)) 
                (triple str str date)
    
    module private impl = 
        open System.IO
        open Persist

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

        let openDatabaseFile dbpath (logger : ILogger) =
            let log = logger.Log
            let resultPU = Persist.result
            let bkpath = makeBkpath dbpath
            // if exists backup restore
            if File.Exists(bkpath) then 
                log Level.Message "Backup file found ('%s'), restoring db" 
                    bkpath
                try 
                    File.Delete(dbpath)
                with _ -> ()
                File.Move(bkpath, dbpath)
            let db = ref (newDatabase())
            let recordCount = ref 0
            let isOldVersion = ref false
            // read database
            if File.Exists(dbpath) then 
                try 
                    use reader = new BinaryReader(openReadWithRetry dbpath)
                    let stream = reader.BaseStream
                    let header = Persist.dbHeader.unpickle reader
                    if header.XakeVer < XakeDbMinReadVersion then 
                        failwith "Database version is old."
                    isOldVersion := header.XakeVer < XakeDbVersion
                    while stream.Position < stream.Length do
                        let result = resultPU.unpickle reader
                        db := result |> addResult !db
                        recordCount := !recordCount + 1
                // if fails create new
                with ex -> 
                    isOldVersion := false
                    log Warning "Failed to read database, so recreating. Got \"%s\"" <| ex.ToString()
                    try 
                        File.Delete(dbpath)
                    with _ -> ()
            // a clear value recorded (by an older Xake, or before the name was declared secret)
            // for a variable now declared secret becomes its digest, and the file is rewritten
            let scrubbed =
                (!db).Status |> Map.map (fun _ r ->
                    let deps = r.Depends |> List.map Secrets.scrub
                    if deps = r.Depends then r else { r with Depends = deps })
            let hasClearSecrets =
                (!db).Status |> Map.exists (fun key r -> not (obj.ReferenceEquals(r, scrubbed.[key])))
            db := { Status = scrubbed }
            // check if we can cleanup db; a database of an older (still readable) version is
            // rewritten so that the header carries the current version of the records appended next
            if !isOldVersion || hasClearSecrets || !recordCount > (!db).Status.Count * 5 then 
                if !isOldVersion then log Level.Message "Upgrading database to version %s" XakeDbVersion
                elif hasClearSecrets then log Level.Message "Removing the values of secret variables from the database"
                else log Level.Message "Compacting database"
                File.Move(dbpath, bkpath)
                use writer =
                    new BinaryWriter(openWithRetry (fun () -> File.Open(dbpath, FileMode.CreateNew) :> Stream))
                writeHeader writer
                (!db).Status
                |> Map.toSeq
                |> Seq.map snd
                |> Seq.iter (fun r -> resultPU.pickle r writer)
                File.Delete(bkpath)
            let dbwriter =
                new BinaryWriter(openWithRetry (fun () -> File.Open (dbpath, FileMode.Append, FileAccess.Write) :> Stream))
            if dbwriter.BaseStream.Position = 0L then writeHeader dbwriter
            db, dbwriter
    
    /// Messages accepted by the database agent.
    type DatabaseApi =
        | GetResult of Target * AsyncReplyChannel<Option<BuildResult>>
        | Store of BuildResult
        | Close
        | CloseWait of AsyncReplyChannel<unit>
    
    /// <summary>
    /// Build result pickler.
    /// </summary>
    let resultPU = Persist.result
    
    /// <summary>
    /// Opens database.
    /// </summary>
    /// <param name="dbpath">Full xake database file name</param>
    /// <param name="logger"></param>
    let openDb dbpath (logger : ILogger) = 
        let db, dbwriter = impl.openDatabaseFile dbpath logger
        // Logging the agent's own failure must not throw out of the loop again.
        let logError fmt = Printf.kprintf (fun s -> try logger.Log Error "%s" s with _ -> ()) fmt
        let agent = new MailboxProcessor<_>(fun mbox ->
            // An exception escaping the loop would kill the agent silently and every later
            // PostAndReply (GetResult, CloseWait) would wait forever. So each message is
            // guarded: a failed store is logged, nothing of it reaches the file and the result
            // is kept in memory for this run (the next run rebuilds that target), a failed
            // lookup answers "no record" (a rebuild), and close always replies.
            let failedStores = ref 0
            let warnUnsaved () =
                if failedStores.Value > 0 then
                    try logger.Log Warning "Database not fully saved: %d result(s) failed to store, the next build may rebuild them" failedStores.Value
                    with _ -> ()
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
                        // pickle into memory first, so a failure writes nothing and the file
                        // stays a sequence of whole records
                        try
                            use buffer = new System.IO.MemoryStream()
                            use bufWriter = new System.IO.BinaryWriter(buffer)
                            Persist.result.pickle result bufWriter
                            bufWriter.Flush()
                            dbwriter.Write(buffer.ToArray())
                        with ex ->
                            failedStores.Value <- failedStores.Value + 1
                            let name = try (result.Targets |> List.map (fun t -> t.ShortName) |> String.concat ", ") with _ -> "?"
                            logError "Database agent failed to store the result of '%s': %O" name ex
                        let db' =
                            try result |> addResult db
                            with ex -> logError "Database agent failed to record a result: %O" ex; db
                        return! loop db'
                    | Close ->
                        do (try logger.Log Info "Closing database" with _ -> ())
                        do warnUnsaved ()
                        try dbwriter.Dispose()
                        with ex -> logError "Database agent failed to close: %O" ex
                        return ()
                    | CloseWait ch ->
                        do (try logger.Log Info "Closing database" with _ -> ())
                        do warnUnsaved ()
                        try dbwriter.Dispose()
                        with ex -> logError "Database agent failed to close: %O" ex
                        ch.Reply()
                        return ()
                }
            loop (!db))
        agent.Error.Add (fun ex -> logError "Database agent stopped: %O" ex)
        agent.Start()
        agent

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
