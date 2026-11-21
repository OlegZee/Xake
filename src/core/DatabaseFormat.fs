namespace Xake.Storage

open Xake

/// The database in memory: the last result per target, the last state per file and the last
/// epoch (the largest in the file on load, the current run's after a new run).
type Database = { Status : Map<Target, BuildResult>; Files : Map<File, FileState>; Epoch : int }

/// A record of a database file, paths resolved (the read API).
[<RequireQualifiedAccess>]
type DbRecord =
    | Result of BuildResult
    | FileState of File * FileState

module internal Database =
    /// The empty database.
    let empty = { Database.Status = Map.empty; Files = Map.empty; Epoch = 0 }

    /// Adds a result to a database.
    let addResult db result =
        { db with Status = result.Targets |> List.fold (fun m i -> Map.add i result m) db.Status
                  Epoch = max db.Epoch result.Epoch }

/// The database file format (0.7): header, picklers, the path table, reading and encoding.
module internal Persist =
    open Pickler

    /// Current database schema version, written to the header of every database file.
    /// 0.5 adds the Dependency case ArtifactDep2 (dependee target with its change stamp).
    /// 0.6 adds the Dependency case SecretDep (a secret variable's digest, never its value).
    /// 0.7 (Xake 5.0): tagged append-only records, a path table relative to the project root,
    /// a `FileRec` per file, epochs instead of per-dependency stamps, varint ids and lengths.
    let version = "0.7"

    /// Oldest database version this Xake still reads. A database of 0.4-0.6 is read by
    /// `LegacyRead`, converted and rewritten as the current version on open.
    let minReadVersion = "0.4"

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
          Dirs: Map<int * string, int>; DirPrefix: Map<int, string>
          /// the next file id and dir id: counted here, since `Map.Count` walks the whole map
          /// (once per path record made the load quadratic, docs/features/startup-perf)
          NFiles: int; NDirs: int }

    module PathTable =
        let empty root =
            let n = (File.make root).Name
            { Root = root; RootPrefix = (if n.EndsWith "/" then n else n + "/")
              Ids = Map.empty; Files = Map.empty; Dirs = Map.empty; DirPrefix = Map.empty; NFiles = 0; NDirs = 0 }

        let private addDir (r, dir) t =
            match Map.tryFind (r, dir) t.Dirs with
            | Some d -> t, d, []
            | None ->
                let d = t.NDirs
                let prefix = match r, dir with 1, "" -> t.RootPrefix | 1, _ -> t.RootPrefix + dir + "/" | _ -> dir + "/"
                { t with Dirs = Map.add (r, dir) d t.Dirs; DirPrefix = Map.add d prefix t.DirPrefix; NDirs = d + 1 }, d, [ DirRec (d, r, dir) ]

        let private addFile f id t = { t with Ids = Map.add f id t.Ids; Files = Map.add id f t.Files; NFiles = id + 1 }

        /// The table with the file, and its new `DirRec`/`PathRec`.
        let add (f: File) (t: PathTable) : PathTable * Rec list =
            if t.Ids.ContainsKey f then t, []
            else
                let s = File.display t.Root f
                let i = s.LastIndexOf '/'
                let t, d, recs = addDir ((if s = f.Name then 0 else 1), (if i < 0 then "" else s.Substring(0, i))) t
                let id = t.NFiles
                addFile f id t, recs @ [ PathRec (id, d, s.Substring(i + 1)) ]

        /// The table after a record read from the file.
        let load (t: PathTable) = function
            | DirRec (d, r, dir) when d = t.NDirs && (r = 0 || r = 1) -> let t, _, _ = addDir (r, dir) t in t
            | PathRec (id, d, name) when id = t.NFiles && t.DirPrefix.ContainsKey d -> addFile (File.make (t.DirPrefix.[d] + name)) id t
            | Data _ -> t
            | r -> failwithf "unexpected path record %A" r

    let private bytes =
        { pickle = (fun (b: byte[]) (st: OutState) -> st.Write(b.Length); st.Write(b))
          unpickle = fun (st: InState) -> st.ReadBytes(st.ReadInt32()) }

    /// The 0.7 record pickler; a file is written as `fileId f` and read back as `fileAt id`
    /// (raising for an unknown id), both against the table at the time.
    let record (fileId: File -> int) (fileAt: int -> File) =
        let file =
            { pickle = (fun f st -> varint.pickle (fileId f) st)
              unpickle = fun st -> fileAt (varint.unpickle st) }
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
        let pu = record (fun f -> t.Ids.[f]) (fun id -> match t.Files.TryFind id with Some f -> f | None -> failwithf "unknown file id %d" id)
        use ms = new System.IO.MemoryStream()
        use w = new System.IO.BinaryWriter(ms)
        for r in List.concat paths @ List.map Data recs do pu.pickle r w
        w.Flush()
        t, ms.ToArray()

    let writeHeader (stream: System.IO.Stream) =
        use w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true)
        dbHeader.pickle { XakeSign = "XAKE"; XakeVer = version; ScriptDate = System.DateTime.Now } w
        w.Flush()

    exception VersionTooNew of string
    exception VersionTooOld of string

    /// The records of a file in order with the table they built; `GoodEnd` is the end of the
    /// last whole record, `Torn` tells that a record after it was cut short.
    type Contents = { Version: string; Records: DbRecord list; Table: PathTable; Torn: bool; GoodEnd: int64 }

    /// Reads a database stream; an older version through `LegacyRead`.
    let read (root: string) (stream: System.IO.Stream) : Contents =
        use reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, true)
        let v = (dbHeader.unpickle reader).XakeVer
        if v > version then raise (VersionTooNew v)
        if v < minReadVersion then raise (VersionTooOld v)
        if v < version then
            let files, results = LegacyRead.read root reader
            { Version = v; Table = PathTable.empty root; Torn = false; GoodEnd = stream.Position
              Records = List.map DbRecord.FileState files @ List.map DbRecord.Result results }
        else
            let mutable t = PathTable.empty root
            // the files by id while reading: an index, not a `Map` lookup per file reference
            let byId = ResizeArray<File>()
            let fileAt id = if id >= 0 && id < byId.Count then byId.[id] else failwithf "unknown file id %d" id
            let pu = record (fun f -> t.Ids.[f]) fileAt
            let records = ResizeArray<DbRecord>()
            let mutable goodEnd, torn = stream.Position, false
            while not torn && stream.Position < stream.Length do
                try
                    match pu.unpickle reader with
                    | Data d -> records.Add d
                    | r ->
                        t <- PathTable.load t r
                        match r with PathRec (id, _, _) -> byId.Add t.Files.[id] | _ -> ()
                    goodEnd <- stream.Position
                with :? System.IO.EndOfStreamException -> torn <- true
            { Version = v; Records = List.ofSeq records; Table = t; Torn = torn; GoodEnd = goodEnd }

    /// The database of records read in order.
    let toDatabase (records: DbRecord seq) =
        records |> Seq.fold (fun db -> function
            | DbRecord.Result r -> Database.addResult db r
            | DbRecord.FileState (f, s) -> { db with Files = Map.add f s db.Files; Epoch = max db.Epoch s.Changed }) Database.empty
