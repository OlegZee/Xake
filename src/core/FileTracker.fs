namespace Xake

/// Keeps the records of files in the database in line with the disk and answers whether a file
/// changed after a run (docs/features/idle-perf, L1, L4, L5). The pure rule `FileRule` decides;
/// this module takes the stats, asks the hasher, writes through `Apply` and decides again on a
/// conflict. A file is observed once per run; later observers wait for the first one.
module internal FileTracker =
    open Xake.Storage
    open FileProbe

    /// Why a file is observed (decisions 1c, A1; content cut-off).
    type internal ObserveMode =
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

    /// An observation of a file: missing, or its state with whether its content changed after
    /// the run `since`.
    type internal Observation =
        | Absent
        | Unchanged of FileState
        | Changed of FileState

    /// The decision about one observation of a file, without I/O or state (docs/features/idle-perf,
    /// L1): the rules of decision 1c, A1 and content cut-off (docs/implnotes.md, "Epochs"). One
    /// pair of functions per kind of observation, the rule before hashing and the one after it;
    /// no rule depends on another mode's (docs/features/arch-review/decide.md).
    module internal FileRule =
        /// What the run knows about the file besides its record.
        type RuleContext = {
            /// The epoch a change is stamped with: the newest handed out, or a fresh one when the
            /// observing run is not the newest (overlapping engine runs).
            ChangeEpoch: int
            /// The run hashes (`--content-cutoff` on).
            Hashing: bool
            /// A body already recorded the file in this run (A1): a moved stat is not a change.
            /// Only `Recording` reads it.
            RecordedBefore: bool }

        /// What an observation ends in.
        type Outcome =
            /// answer from this state; nothing to write
            | Keep of FileState option
            /// write this state (it differs from the known one)
            | Write of FileState

        /// The decision before hashing: an outcome, or hash the file and then `decideHashed`
        /// (which always ends in an outcome).
        type Decision =
            | Decided of Outcome
            | NeedHash

        /// Equal within 10 ms, the size equal or unknown.
        let sameStat (k: FileState) (stamp: Timestamp) size =
            abs (stamp - k.Stamp).TotalMilliseconds < 10.0 && (k.Size < 0L || k.Size = size)

        let private write (known: FileState option) state = if Some state = known then Keep known else Write state

        /// A change in this run, without a hash.
        let private changed stamp size (rc: RuleContext) = { Stamp = stamp; Size = size; Hash = None; Changed = rc.ChangeEpoch }

        /// After hashing failed: absent when the file is gone, else a change without a hash.
        let private unreadable known stat rc =
            match stat with
            | Missing -> Keep None
            | Present (stamp, size) -> write known (changed stamp size rc)

        // -- input: a file the run reads (`Verdict`, `Recording`)

        /// An equal stat keeps the record (an unknown size filled in); a moved stat is not a change
        /// when another body recorded the file in this run (A1), else it is hashed (the run hashes)
        /// or a change.
        let private input recordedBefore known stamp size rc =
            match known with
            | Some k when sameStat k stamp size -> Decided (write known { k with Size = size })
            | Some _ when recordedBefore -> Decided (Keep known)
            | Some _ when rc.Hashing -> NeedHash
            | _ -> Decided (write known (changed stamp size rc))

        /// The same content keeps the run it changed in (cut-off); other content is a change.
        let private inputHashed known hashed rc =
            match hashed with
            | Unreadable stat -> unreadable known stat rc
            | Hashed (stamp, size, h) ->
                let state = { Stamp = stamp; Size = size; Hash = Some h; Changed = rc.ChangeEpoch }
                match known with
                | Some k when k.Hash = Some h -> write known { state with Changed = k.Changed }
                | _ -> write known state

        // -- output: a rule's output right after the rule

        /// Without cut-off a change; with cut-off hashed when the run hashes, else as an input.
        let private output cutoff known stamp size rc =
            if not cutoff then Decided (write known (changed stamp size rc))
            elif rc.Hashing then NeedHash
            else input false known stamp size rc

        /// As an input, and a record never hashed with an equal stat gets its hash, its run kept.
        let private outputHashed known hashed rc =
            match hashed, known with
            | Hashed (stamp, size, h), Some k when k.Hash.IsNone && sameStat k stamp size ->
                write known { Stamp = stamp; Size = size; Hash = Some h; Changed = k.Changed }
            | _ -> inputHashed known hashed rc

        // -- pre-hash: a rule's output before the rule runs

        /// A record without a hash and an equal stat is hashed (the run hashes); else kept.
        let private preHash known stamp size rc =
            match known with
            | Some k when rc.Hashing && k.Hash.IsNone && sameStat k stamp size -> NeedHash
            | _ -> Decided (Keep known)

        /// Only the hash is written, and only for an equal stat; anything else keeps the record.
        let private preHashed known hashed =
            match hashed, known with
            | Hashed (stamp, size, h), Some k when sameStat k stamp size -> write known { k with Hash = Some h }
            | _ -> Keep known

        /// One observation of a file with the record `known` and the stat taken now.
        let decide (known: FileState option) (stat: FileStat) (mode: ObserveMode) (rc: RuleContext) =
            match stat with
            | Missing -> Decided (Keep None)
            | Present (stamp, size) ->
                match mode with
                | Verdict -> input false known stamp size rc
                | Recording -> input rc.RecordedBefore known stamp size rc
                | Output cutoff -> output cutoff known stamp size rc
                | PreHash -> preHash known stamp size rc

        /// The decision after hashing, against the same `known` record.
        let decideHashed (known: FileState option) (hashed: HashResult) (mode: ObserveMode) (rc: RuleContext) =
            match mode with
            | Verdict | Recording -> inputHashed known hashed rc
            | Output _ -> outputHashed known hashed rc
            | PreHash -> preHashed known hashed

        /// The reply for a state: changed after the run `since`, or not.
        let classify since = function
            | None -> Absent
            | Some (st: FileState) when st.Changed > since -> Changed st
            | Some st -> Unchanged st

    /// What a run knows about files besides their records (docs/features/idle-perf, L4): the
    /// files observed in it and the files a body recorded (A1). One per run.
    ///
    /// A claim is an ordering barrier, not a cache of answers: the first observation of a file in
    /// the run holds it until its record is written; a later verdict waits for it and then answers
    /// from the record as it is then (a rule may have rewritten the file in this run since), taking
    /// only `File.exists` instead of a full stat. `Recording` and `Output` claim too, so that a
    /// verdict after them waits for their write, but never wait themselves: they follow writes made
    /// in this run and must stat again (docs/features/arch-review/observe.md).
    [<Sealed>]
    type RunFiles internal () =
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

    /// Where an observation runs: the database, the hasher, the run's files, whether the run
    /// hashes, the counter of hashes computed, and the log.
    type internal Context = { Db: Db; Hasher: Hasher; Run: RunFiles; RunEpoch: int; Hashing: bool; OnHashed: System.TimeSpan -> unit; Log: ILogger }

    /// A file this observation decides: whether another body recorded it in this run (A1), the
    /// record it is decided against, and its stat.
    type internal Pending = { File: File; RecordedBefore: bool; Known: FileState option; Stat: FileStat }

    /// The rounds of deciding and writing before the current record is taken as the answer: a
    /// conflict needs another observer to write the same file between our read and our write.
    let private maxRounds = 3

    /// Claims the files (step 1-2): the completions this call holds, the verdicts that wait for an
    /// earlier observation, and the files decided here with their stats. When a stat fails, the
    /// claims taken are completed before the exception leaves. `PreHash` claims nothing:
    /// its record is the one before the rule runs, and a verdict waiting for it would read a stale
    /// record.
    let internal claim (oc: Context) (mode: ObserveMode) (files: File list) =
        let firsts = ResizeArray<System.Threading.Tasks.TaskCompletionSource<unit>>()
        let waits = ResizeArray<File * System.Threading.Tasks.Task>()
        let pending = ResizeArray<Pending>()
        let snapshot = oc.Db.Snapshot
        try
            for f in List.distinct files do
                let earlier =
                    if mode = PreHash then None
                    else
                        match oc.Run.Claim f with
                        | Choice1Of2 tcs -> firsts.Add tcs; None
                        | Choice2Of2 task -> if mode = Verdict then Some task else None
                match earlier with
                | Some task -> waits.Add((f, task))
                | None ->
                    pending.Add { File = f; RecordedBefore = (mode = Recording && oc.Run.RecordedBefore f)
                                  Known = Map.tryFind f snapshot.Files; Stat = FileStat.take f }
        with _ ->
            // a stat that throws (a path `FileInfo` rejects) must not leave the claims taken so far
            // open: a later verdict would wait for them forever (C12)
            for tcs in firsts do tcs.TrySetResult () |> ignore
            reraise ()
        List.ofSeq firsts, List.ofSeq waits, List.ofSeq pending

    /// The epoch a change observed now is stamped with. Normally the newest handed out, which is
    /// the observing run's own. When a newer run has started (overlapping engine runs), a fresh one:
    /// the newest may be the epoch of a result that run built from the file's previous content,
    /// and `changed > epoch` would miss the change (docs/features/arch-review/epochs.md). A context
    /// without a run epoch (0) keeps the newest.
    let private changeEpoch (oc: Context) =
        let newest = oc.Db.Snapshot.Epoch
        if oc.RunEpoch > 0 && newest > oc.RunEpoch then Db.newRun oc.Db else async.Return newest

    /// Decides the files and writes the changed ones (step 3): the rule against each record, the
    /// hashes the rule asks for (in parallel), every write in one `Db.apply`; a conflict is decided
    /// again on a fresh stat against the current record, for at most `maxRounds` rounds. The
    /// answer per file.
    let internal settle (oc: Context) (mode: ObserveMode) (pending: Pending list) = async {
        let answers = System.Collections.Generic.Dictionary<File, FileState option>()
        let rec round n (pending: Pending list) = async {
            if not pending.IsEmpty then
                let! epoch = changeEpoch oc
                let rc (p: Pending) = { FileRule.ChangeEpoch = epoch; FileRule.Hashing = oc.Hashing; FileRule.RecordedBefore = p.RecordedBefore }
                let decided = [ for p in pending -> p, FileRule.decide p.Known p.Stat mode (rc p) ]
                // only the files to hash go through the hasher, in parallel
                let! hashed =
                    [ for p, d in decided do
                        if d = FileRule.NeedHash then
                            yield async {
                                let! h = oc.Hasher.Hash p.File p.Stat oc.OnHashed
                                return p.File, FileRule.decideHashed p.Known h mode (rc p) } ]
                    |> function [] -> async.Return [||] | hs -> Async.Parallel hs
                let hashedBy = dict hashed
                let outcomes = [ for p, d in decided -> p, (match d with FileRule.Decided o -> o | FileRule.NeedHash -> hashedBy.[p.File]) ]
                let writes =
                    [ for p, o in outcomes do
                        match o with
                        | FileRule.Keep st -> answers.[p.File] <- st
                        | FileRule.Write st -> answers.[p.File] <- Some st; yield p.File, p.Known, st ]
                if not writes.IsEmpty then
                    let! conflicts = Db.apply oc.Db writes
                    if n >= maxRounds then
                        for f, current in conflicts do
                            oc.Log.Log Debug "%s: still conflicting after %d rounds, the current record is the answer (%A)" f.Name n current
                            answers.[f] <- current
                    else
                        let byFile = pending |> List.map (fun p -> p.File, p) |> dict
                        do! round (n + 1) [ for f, current in conflicts -> { byFile.[f] with Known = current; Stat = FileStat.take f } ] }
        do! round 1 pending
        return answers }

    /// A verdict's later observation (step 5): after the first one, the record now if the file
    /// exists now.
    let private answerLater (oc: Context) (waits: (File * System.Threading.Tasks.Task) list) = async {
        let answers = System.Collections.Generic.Dictionary<File, FileState option>()
        for f, task in waits do
            if not task.IsCompleted then
                do! (try Async.AwaitTask (task :?> System.Threading.Tasks.Task<unit>) with _ -> async.Return ())
            answers.[f] <- (if File.exists f then Map.tryFind f oc.Db.Snapshot.Files else None)
        return answers }

    /// Observes a group of files (docs/features/idle-perf, L5): claims them, decides and writes the
    /// ones decided here, completes the claims (also on failure, so no verdict waits forever),
    /// answers the later verdicts. Replies per file, in the order given, whether its content
    /// changed after the run `since`.
    let internal observe (oc: Context) (mode: ObserveMode) (since: int) (files: File list) = async {
        let firsts, waits, pending = claim oc mode files
        let! decided =
            async {
                try return! settle oc mode pending
                finally for tcs in firsts do tcs.TrySetResult () |> ignore }
        let! later = answerLater oc waits
        let answerOf f = match decided.TryGetValue f with | true, st -> st | _ -> later.[f]
        return [| for f in files -> FileRule.classify since (answerOf f) |] }
