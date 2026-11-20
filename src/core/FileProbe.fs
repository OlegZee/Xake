namespace Xake

/// The file system as the engine reads it (docs/features/idle-perf, L0 and L3): a stat, a content
/// hash, and the hasher that computes one hash per (file, stat). Only I/O: no records, epochs or
/// runs.
module FileProbe =

    /// A file's stat: missing, or its write time and length (docs/features/idle-perf, L0).
    type internal FileStat =
        | Missing
        | Present of stamp: Timestamp * size: int64

    module internal FileStat =
        /// One stat of the file.
        let take (file: File) =
            let fi = File.getFileInfo file
            if fi.Exists then Present (fi.LastWriteTime, fi.Length) else Missing

    /// What hashing a file gave.
    type internal HashResult =
        /// the stat the hash was taken at (unchanged before and after hashing) and the hash
        | Hashed of stamp: Timestamp * size: int64 * hash: uint64
        /// the file could not be read, or its stat moved while it was hashed (its content was not
        /// stable): a change without a hash; the stat taken after the attempt
        | Unreadable of FileStat

    /// Content hashes (docs/content-cutoff.md; docs/features/idle-perf, L0).
    module internal Content =
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

    type private HasherMsg =
        | HashFile of File * FileStat * onComputed: (System.TimeSpan -> unit) * AsyncReplyChannel<HashResult>
        | Hashed' of File * FileStat * HashResult

    /// Hashes behind their own mailbox (docs/features/idle-perf, L3): one computation per (file,
    /// stat) while it runs, every other asker gets the same result; at most as many hashes at
    /// once as there are cores. It knows nothing of records, epochs or runs.
    [<Sealed>]
    type Hasher internal () =
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
        member internal _.Hash (file: File) (stat: FileStat) (onComputed: System.TimeSpan -> unit) =
            mbox.PostAndAsyncReply(fun ch -> HashFile (file, stat, onComputed, ch))
