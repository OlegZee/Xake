namespace Xake

/// Keeps the records of files in the database in line with the disk and answers whether a file
/// changed after a run (docs/features/idle-perf, L1, L4, L5). The pure rule `FileRule` decides;
/// this module takes the stats, asks the hasher, writes through `Apply` and decides again on a
/// conflict. A file is observed once per run; later observers wait for the first one.
module FileTracker =
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

    /// What a run knows about files besides their records (docs/features/idle-perf, L4): the
    /// files observed in it (a verdict's later observation of one answers from the record, after
    /// the first observation has written it) and the files a body recorded (A1). One per run.
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
    /// hashes, and the counter of hashes computed.
    type internal Context = { Db: Db; Hasher: Hasher; Run: RunFiles; Hashing: bool; OnHashed: System.TimeSpan -> unit }

    /// Observes a group of files (docs/features/idle-perf, L5): their stats on this thread, the
    /// rule against one snapshot, the hashes (in parallel, through the hasher), every write in one
    /// `Apply`, a conflict decided again on a fresh stat (at most 3 times). Replies per file, in
    /// the order given, whether its content changed after the run `since`. A verdict's later
    /// observation of a file in the run waits for the first one and answers from the record.
    let internal observe (oc: Context) (mode: ObserveMode) (since: int) (files: File list) = async {
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
                                    let! h = oc.Hasher.Hash f stat oc.OnHashed
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
                        let! conflicts = Db.apply oc.Db (List.ofSeq writes)
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
