/// The path table of the build database (format 0.7): directories and files by id, paths split by roots.
/// Owned by the database agent, which serialises every access; nothing here is thread-safe.
module internal Xake.PathTable

open System
open System.IO
open System.Collections.Generic

/// 7-bit encoded non-negative integers.
module Varint =
    let write (w: BinaryWriter) (value: int) =
        if value < 0 then invalidArg "value" "a varint is non-negative"
        let mutable u = uint32 value
        while u >= 0x80u do
            w.Write(byte (u ||| 0x80u))
            u <- u >>> 7
        w.Write(byte u)

    let read (r: BinaryReader) =
        let mutable result = 0
        let mutable shift = 0
        let mutable more = true
        while more do
            if shift > 28 then raise (InvalidDataException "varint is too long")
            let b = r.ReadByte()
            result <- result ||| (int (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            more <- b &&& 0x80uy <> 0uy
        if result < 0 then raise (InvalidDataException "varint is negative")
        result

    let pu : Pickler.PU<int> = { pickle = (fun v w -> write w v); unpickle = read }

/// A root of this run: `Name` is `ProjectRoot` or a declared name; `Path` is full and normalised;
/// `Recorded` is the text written to `RootRec` (relative roots as written).
type Root = { Name: string; Path: string; Recorded: string }

/// Raised for a record that contradicts the table (a corrupt file).
exception CorruptRecord of string

module Roots =
    /// Paths compare as `Target` does: ordinal on Unix, ignoring case on Windows.
    let comparison = if Env.isUnix then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase
    let separators = if Env.isUnix then [| '/' |] else [| '\\'; '/' |]
    let private sep = Path.DirectorySeparatorChar
    let private isSep c = Array.contains c separators

    /// Trims trailing separators, except from a file-system root (`/`, `C:\`).
    let trimSep (p: string) =
        let root = Path.GetPathRoot p
        let root = if isNull root then "" else root
        if p.Length > root.Length then p.TrimEnd separators
        else p

    let normalize (p: string) = Path.GetFullPath p |> trimSep

    let private endsWithSep (p: string) = p.Length > 0 && isSep p.[p.Length - 1]

    /// True if `dir` is `root` or below it, on a separator boundary.
    let isUnder (root: string) (dir: string) =
        if dir.Equals(root, comparison) then true
        elif dir.Length > root.Length && dir.StartsWith(root, comparison) then
            endsWithSep root || isSep dir.[root.Length]
        else false

    let key (name: string) = "$(" + name + ")"
    let isNamedKey (k: string) = k.StartsWith "$(" && k.EndsWith ")"
    let nameOfKey (k: string) = k.Substring(2, k.Length - 3)

    /// Splits a full directory path into its root key and the segments below it. `roots` are sorted
    /// longest first; a directory under none is split at its file-system root.
    let split (roots: Root list) (dir: string) : string * string list =
        let segmentsBelow (rootPath: string) =
            if dir.Length <= rootPath.Length then []
            else dir.Substring(rootPath.Length).Split(separators, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        match roots |> List.tryFind (fun r -> isUnder r.Path dir) with
        | Some r -> key r.Name, segmentsBelow r.Path
        | None ->
            let fsRoot = Path.GetPathRoot dir
            let fsRoot = if String.IsNullOrEmpty fsRoot then raise (ArgumentException (sprintf "not a full path: '%s'" dir)) else fsRoot
            fsRoot, segmentsBelow fsRoot

    /// Joins a resolved root path and a segment.
    let join (parent: string) (segment: string) =
        if endsWithSep parent then parent + segment else parent + string sep + segment

    /// The longest-first order `split` expects.
    let sorted (roots: Root list) = roots |> List.sortByDescending (fun r -> r.Path.Length)

/// One `DirRec` (Parent = -1 for a root, Name = the root key) or `FileRec` (Parent = the directory).
[<Struct>]
type Entry = { Parent: int; Name: string }

/// The tags of the 0.7 records.
module Tag =
    let [<Literal>] Dir = 0uy
    let [<Literal>] File = 1uy
    let [<Literal>] Result = 2uy
    let [<Literal>] RootValue = 3uy

/// The table of one database file. `roots` are this run's roots (any order).
type Table(roots: Root list) =
    let roots = Roots.sorted roots
    let rootPaths = roots |> List.map (fun r -> r.Name, r.Path) |> dict
    let dirs = Dictionary<int, Entry>()
    let files = Dictionary<int, Entry>()
    let dirByKey = Dictionary<struct (int * string), int>()
    let fileByKey = Dictionary<struct (int * string), int>()
    /// ids whose records are in the current file
    let inFile = HashSet<int>()
    let mutable nextId = 0
    // resolution caches
    let dirPath = Dictionary<int, string voption>()
    let dirRoot = Dictionary<int, string>()
    let fileObj = Dictionary<int, Xake.File voption>()
    let byPath = Dictionary<string, Xake.File>(StringComparer.Ordinal)
    let idByPath = Dictionary<string, int>(StringComparer.Ordinal)

    let resolveRootKey (k: string) =
        if Roots.isNamedKey k then
            match rootPaths.TryGetValue(Roots.nameOfKey k) with
            | true, p -> ValueSome p
            | _ -> ValueNone
        else
            // a file-system root of another system (`C:\` read on Unix) does not resolve here
            let ok =
                try Path.IsPathRooted k && Path.GetPathRoot k = k
                with _ -> false
            if ok then ValueSome k else ValueNone

    let note id = if id >= nextId then nextId <- id + 1

    member _.Roots = roots

    /// Hands out a fresh id; ids are never reused, even when the record that took one is not written.
    member _.Allocate() =
        let id = nextId
        nextId <- nextId + 1
        id

    member _.DirCount = dirs.Count
    member _.FileCount = files.Count
    member _.InFile = inFile

    member _.TryDir id = match dirs.TryGetValue id with | true, e -> ValueSome e | _ -> ValueNone
    member _.TryFileEntry id = match files.TryGetValue id with | true, e -> ValueSome e | _ -> ValueNone
    member _.DirByKey key = match dirByKey.TryGetValue key with | true, id -> ValueSome id | _ -> ValueNone
    member _.FileByKey key = match fileByKey.TryGetValue key with | true, id -> ValueSome id | _ -> ValueNone
    member _.IsDir id = dirs.ContainsKey id

    /// Adds a record read from the file or written by a committed transaction.
    member t.AddDir(id, parent, name: string, fromFile: bool) =
        match dirs.TryGetValue id with
        | true, e when e.Parent = parent && e.Name = name -> ()
        | true, _ -> raise (CorruptRecord (sprintf "directory id %d defined twice" id))
        | _ ->
            if files.ContainsKey id then raise (CorruptRecord (sprintf "id %d is a file and a directory" id))
            if parent <> -1 && not (dirs.ContainsKey parent) then
                raise (CorruptRecord (sprintf "directory %d names unknown parent %d" id parent))
            dirs.[id] <- { Parent = parent; Name = name }
            let key = struct (parent, name)
            if not (dirByKey.ContainsKey key) then dirByKey.[key] <- id
            note id
        if fromFile then inFile.Add id |> ignore

    member t.AddFile(id, dir, name: string, fromFile: bool) =
        match files.TryGetValue id with
        | true, e when e.Parent = dir && e.Name = name -> ()
        | true, _ -> raise (CorruptRecord (sprintf "file id %d defined twice" id))
        | _ ->
            if dirs.ContainsKey id then raise (CorruptRecord (sprintf "id %d is a file and a directory" id))
            if not (dirs.ContainsKey dir) then
                raise (CorruptRecord (sprintf "file %d names unknown directory %d" id dir))
            files.[id] <- { Parent = dir; Name = name }
            let key = struct (dir, name)
            if not (fileByKey.ContainsKey key) then fileByKey.[key] <- id
            note id
        if fromFile then inFile.Add id |> ignore

    /// The root key at the top of a directory's chain.
    member t.RootKeyOfDir(id: int) : string =
        match dirRoot.TryGetValue id with
        | true, k -> k
        | _ ->
            let e = dirs.[id]
            let k = if e.Parent = -1 then e.Name else t.RootKeyOfDir e.Parent
            dirRoot.[id] <- k
            k

    member t.RootKeyOfFile(id: int) = t.RootKeyOfDir files.[id].Parent

    /// The full path of a directory in this run, none when its root is not declared here.
    member t.DirPath(id: int) : string voption =
        match dirPath.TryGetValue id with
        | true, p -> p
        | _ ->
            let e = dirs.[id]
            let p =
                if e.Parent = -1 then resolveRootKey e.Name
                else t.DirPath e.Parent |> ValueOption.map (fun parent -> Roots.join parent e.Name)
            dirPath.[id] <- p
            p

    /// The path as stored (`$(ROOT)/a/b.txt`): a key that does not depend on this run's roots.
    member t.StoredPath(fileId: int) =
        let rec dir id =
            let e = dirs.[id]
            if e.Parent = -1 then e.Name else Roots.join (dir e.Parent) e.Name
        let e = files.[fileId]
        Roots.join (dir e.Parent) e.Name

    /// The `File` of an id: one value per distinct path; none when its root is not declared in this run.
    /// An unknown id is a corrupt record.
    member t.Resolve(id: int) : Xake.File voption =
        match fileObj.TryGetValue id with
        | true, f -> f
        | _ ->
            let e =
                match files.TryGetValue id with
                | true, e -> e
                | _ -> raise (CorruptRecord (sprintf "unknown file id %d" id))
            let f =
                t.DirPath e.Parent
                |> ValueOption.map (fun d ->
                    let full = Roots.join d e.Name
                    match byPath.TryGetValue full with
                    | true, f -> f
                    | _ ->
                        let f = Xake.File.make full
                        byPath.[full] <- f
                        f)
            fileObj.[id] <- f
            f

    /// True when a resolved file's stored root is not its longest root in this run.
    member t.IsMisRooted(fileId: int) =
        match t.Resolve fileId with
        | ValueNone -> false
        | ValueSome f ->
            let dir = Path.GetDirectoryName f.FullName
            let dir = if isNull dir then f.FullName else dir
            let k, _ = Roots.split roots dir
            k <> t.RootKeyOfFile fileId

    /// The cached id of a full path (filled by commits and by walks).
    member _.TryIdByPath(full: string) = match idByPath.TryGetValue full with | true, id -> ValueSome id | _ -> ValueNone
    member _.RememberPath(full: string, id: int) = idByPath.[full] <- id

    /// Forgets which records are in the file (before a rewrite to a new file).
    member _.ResetInFile() = inFile.Clear()

/// The paths one write adds to the file: ids are taken from the table or handed out, and the
/// records the file still lacks are collected. Nothing reaches the table before `Commit`.
type Tx(table: Table) =
    let pending = ResizeArray<struct (bool * int * Entry)>()
    let pendingIds = HashSet<int>()
    let pendingDirs = Dictionary<struct (int * string), int>()
    let pendingFiles = Dictionary<struct (int * string), int>()
    let pendingPaths = ResizeArray<struct (string * int)>()

    /// Makes sure the records of an existing id (and its parents) are in the file or in this write.
    member tx.Ensure(isDir: bool, id: int) =
        if not (table.InFile.Contains id) && not (pendingIds.Contains id) then
            let e =
                match (if isDir then table.TryDir id else table.TryFileEntry id) with
                | ValueSome e -> e
                | ValueNone -> raise (CorruptRecord (sprintf "unknown id %d" id))
            if e.Parent <> -1 then tx.Ensure(true, e.Parent)
            pendingIds.Add id |> ignore
            pending.Add(struct (isDir, id, e))

    /// Ensures an id of unknown kind (the ids a carried record names are files).
    member tx.EnsureFile(id: int) = tx.Ensure(false, id)

    member private tx.Dir(parent: int, name: string) =
        let key = struct (parent, name)
        match table.DirByKey key with
        | ValueSome id -> tx.Ensure(true, id); id
        | ValueNone ->
            match pendingDirs.TryGetValue key with
            | true, id -> id
            | _ ->
                let id = table.Allocate()
                pendingDirs.[key] <- id
                pendingIds.Add id |> ignore
                pending.Add(struct (true, id, { Parent = parent; Name = name }))
                id

    /// The id of a file in this run's roots, new records collected as needed.
    member tx.IdOf(file: Xake.File) : int =
        let full = file.FullName
        if String.IsNullOrEmpty full then invalidArg "file" "an undefined file cannot be stored"
        match table.TryIdByPath full with
        | ValueSome id -> tx.Ensure(false, id); id
        | ValueNone ->
            let dir = Path.GetDirectoryName full
            let name = Path.GetFileName full
            if isNull dir || String.IsNullOrEmpty name then invalidArg "file" (sprintf "not a file path: '%s'" full)
            let rootKey, segments = Roots.split table.Roots dir
            let dirId = segments |> List.fold (fun parent seg -> tx.Dir(parent, seg)) (tx.Dir(-1, rootKey))
            let key = struct (dirId, name)
            let id =
                match table.FileByKey key with
                | ValueSome id -> tx.Ensure(false, id); id
                | ValueNone ->
                    match pendingFiles.TryGetValue key with
                    | true, id -> id
                    | _ ->
                        let id = table.Allocate()
                        pendingFiles.[key] <- id
                        pendingIds.Add id |> ignore
                        pending.Add(struct (false, id, { Parent = dirId; Name = name }))
                        id
            pendingPaths.Add(struct (full, id))
            id

    member _.IsEmpty = pending.Count = 0

    /// Writes the collected `DirRec`/`FileRec` records, parents first.
    member _.WriteRecords(w: BinaryWriter) =
        for struct (isDir, id, e) in pending do
            use body = new MemoryStream()
            use bw = new BinaryWriter(body)
            bw.Write(if isDir then Tag.Dir else Tag.File)
            Varint.write bw id
            Varint.write bw (e.Parent + 1)
            bw.Write(e.Name)
            bw.Flush()
            Varint.write w (int body.Length)
            w.Write(body.GetBuffer(), 0, int body.Length)

    /// Called after the write reached the file.
    member _.Commit() =
        for struct (isDir, id, e) in pending do
            if isDir then table.AddDir(id, e.Parent, e.Name, true)
            else table.AddFile(id, e.Parent, e.Name, true)
        for struct (full, id) in pendingPaths do table.RememberPath(full, id)
