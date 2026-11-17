namespace Xake

open System
open System.Collections.Concurrent

/// Where the content histories are kept between runs. The 0.7 database implements it (one record
/// per file, the last one wins); until then the histories live as long as the engine.
type IContentPersistence =
    /// Every file's history, as saved.
    abstract Load: unit -> (File * ContentEntry list) seq
    /// A file's new history.
    abstract Save: File * ContentEntry list -> unit

/// The content histories of the files the build depends on (sources and rule outputs alike):
/// the last `HistoryLength` `(time, size) -> hash option`, newest first, the head being the file's
/// state when Xake last looked. Hashes are computed lazily and once per file and state.
[<Sealed>]
type ContentStore(persistence: IContentPersistence option) =
    static let comparer = if Env.isUnix then StringComparer.Ordinal else StringComparer.OrdinalIgnoreCase
    let table = ConcurrentDictionary<string, ContentEntry list>(comparer)
    // one hashing per file and state, shared by concurrent dependents
    let hashing = ConcurrentDictionary<string, Lazy<byte[] option>>(comparer)
    do
        persistence |> Option.iter (fun p -> for f, h in p.Load () do table.[f.FullName] <- h)

    /// How many states a history keeps.
    static member HistoryLength = 8
    /// Times closer than this are the same time (as Xake 4.0 compares times).
    static member ToleranceMs = 10.0

    /// Whether two `(time, size)` pairs are the same state; a size below 0 (unknown, a record of
    /// Xake 4.x) compares by time alone.
    static member Same (stamp1: DateTime, size1: int64) (stamp2: DateTime, size2: int64) =
        abs (stamp1 - stamp2).TotalMilliseconds < ContentStore.ToleranceMs
        && (size1 < 0L || size2 < 0L || size1 = size2)

    /// The time and size of a file now; None when it does not exist.
    static member StateOf (file: File) : (DateTime * int64) option =
        let info = IO.FileInfo file.FullName
        if info.Exists then Some (info.LastWriteTime, info.Length) else None

    /// SHA-256 of a file, taken between two reads of its state: None when the state moved while
    /// hashing (the content is then not known) or the file is gone.
    static member HashFile (file: File) (stamp: DateTime, size: int64) : byte[] option =
        try
            use stream = IO.File.OpenRead file.FullName
            use sha = Security.Cryptography.SHA256.Create ()
            let hash = sha.ComputeHash stream
            stream.Dispose ()
            match ContentStore.StateOf file with
            | Some now when ContentStore.Same now (stamp, size) -> Some hash
            | _ -> None
        with :? IO.IOException | :? UnauthorizedAccessException -> None

    /// The file's history, newest first.
    member _.History (file: File) : ContentEntry list =
        match table.TryGetValue file.FullName with
        | true, h -> h
        | _ -> []

    /// The hash recorded for a state of the file, if any.
    member this.HashOf (file: File) (state: DateTime * int64) : byte[] option =
        this.History file
        |> List.tryFind (fun e -> ContentStore.Same (e.Stamp, e.Size) state)
        |> Option.bind (fun e -> e.Hash)

    /// Records a state of the file as its newest. A state already in the history is replaced (one
    /// state, one content: a different hash behind a known state drops the old entry); a hash
    /// already known for the state is kept when none is given.
    member _.Record (file: File) (stamp: DateTime, size: int64) (hash: byte[] option) =
        let history =
            table.AddOrUpdate(file.FullName,
                (fun _ -> [ { Stamp = stamp; Size = size; Hash = hash } ]),
                (fun _ old ->
                    let same, rest = old |> List.partition (fun e -> ContentStore.Same (e.Stamp, e.Size) (stamp, size))
                    let hash =
                        match hash, same with
                        | Some h, _ -> Some h
                        | None, e :: _ -> e.Hash
                        | None, [] -> None
                    { Stamp = stamp; Size = size; Hash = hash } :: rest |> List.truncate ContentStore.HistoryLength))
        persistence |> Option.iter (fun p -> p.Save (file, history))

    /// The current state of the file and the hash of its content, hashing it if the history has no
    /// hash for that state (once per file and state; `hashed` is told the time each hashing took).
    /// None when the file is missing or moved while being hashed. The state is recorded.
    member this.Current (file: File) (hashed: TimeSpan -> unit) : ((DateTime * int64) * byte[]) option =
        match ContentStore.StateOf file with
        | None -> None
        | Some state ->
            match this.HashOf file state with
            | Some h -> Some (state, h)
            | None ->
                let stamp, size = state
                let key = sprintf "%s|%d|%d" file.FullName stamp.Ticks size
                let computed =
                    hashing.GetOrAdd(key, fun _ -> lazy (
                        let watch = Diagnostics.Stopwatch.StartNew ()
                        let h = ContentStore.HashFile file state
                        hashed watch.Elapsed
                        h)).Value
                match computed with
                | Some h ->
                    this.Record file state (Some h)
                    Some (state, h)
                | None -> None
