namespace Xake.Hermetic.Dotnet

open System.IO
open System.Runtime.InteropServices

open Xake
open Xake.Dotnet

/// <summary>
/// The file side of a run folder (`Locks.runFolderWith`): which source a path gets when several
/// want it (`plan`), and placing the planned files into the folder against the manifest of the
/// previous run (`apply`) -- a hard link, else a clone (APFS `clonefile`, Linux `FICLONE`),
/// else a copy; a placed file is never written into (a changed one is unlinked and placed
/// again), a file the folder has that no run placed is never replaced.
/// </summary>
module RunFolder =

    /// How a file is placed: the preferred way, the next ones the fallbacks (`Link`: hard link,
    /// clone, copy; `Clone`: clone, copy; `Copy`: copy).
    type Mode =
        | Link
        | Clone
        | Copy

    /// The name of the manifest, the run folder rule's target.
    [<Literal>]
    let Manifest = ".xake-run.txt"

    /// The name of the conflicts report beside the manifest (written when there is a conflict).
    [<Literal>]
    let ConflictsReport = ".xake-run.conflicts.txt"

    let private isMac = Env.isUnix && Directory.Exists "/System/Library/CoreServices"

    module private Native =
        [<DllImport("libc", SetLastError = true)>]
        extern int link (string existing, string newPath)
        [<DllImport("libc", SetLastError = true)>]
        extern int clonefile (string src, string dst, uint32 flags)
        [<DllImport("libc", EntryPoint = "stat", SetLastError = true)>]
        extern int stat (string path, byte[] buf)
        [<DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)>]
        extern int statInode64 (string path, byte[] buf)
        [<DllImport("libc", EntryPoint = "__xstat", SetLastError = true)>]
        extern int xstat (int version, string path, byte[] buf)
        [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
        extern int openFile (string path, int flags, int mode)
        [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
        extern int closeFile (int fd)
        [<DllImport("libc", EntryPoint = "ioctl", SetLastError = true)>]
        extern int ioctl (int fd, unativeint request, int arg)
        [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
        extern bool CreateHardLink (string fileName, string existingFileName, nativeint securityAttributes)
        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool GetFileInformationByHandle (Microsoft.Win32.SafeHandles.SafeFileHandle file, byte[] info)

    /// The stat call that works here, found once: macOS x64's `stat$INODE64`, else `stat`
    /// (glibc 2.33+, musl, macOS arm64), else glibc's older `__xstat`.
    let private statCall : Lazy<string -> byte[] -> int> =
        lazy (
            let probe (f: string -> byte[] -> int) =
                try f "/" (Array.zeroCreate 512) |> ignore; Some f
                with :? System.EntryPointNotFoundException | :? System.DllNotFoundException -> None
            let xstatVersion = if System.IntPtr.Size = 8 && not (Env.isUnix && File.Exists "/lib/ld-linux-aarch64.so.1") then 1 else 0
            [ (if isMac then Some (fun p b -> Native.statInode64 (p, b)) else None)
              Some (fun p b -> Native.stat (p, b))
              Some (fun p b -> Native.xstat (xstatVersion, p, b)) ]
            |> List.choose id |> List.tryPick probe
            |> Option.defaultValue (fun _ _ -> -1))

    /// <summary>
    /// The identity of a file -- (device, inode) on Unix, (volume serial, file index) on
    /// Windows -- or `None` where it cannot be read. Two names of one hard-linked file have the
    /// same identity.
    /// </summary>
    let fileId (path: string) : (uint64 * uint64) option =
        try
            if Env.isWindows then
                use stream = new FileStream (path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                let info = Array.zeroCreate<byte> 52
                if Native.GetFileInformationByHandle (stream.SafeFileHandle, info) then
                    let index = uint64 (System.BitConverter.ToUInt32 (info, 44)) * 4294967296UL + uint64 (System.BitConverter.ToUInt32 (info, 48))
                    Some (uint64 (System.BitConverter.ToUInt32 (info, 28)), index)
                else None
            else
                let buf = Array.zeroCreate<byte> 512
                if statCall.Value path buf <> 0 then None
                else
                    // macOS: st_dev int32 at 0, st_ino uint64 at 8; Linux (x64, arm64): st_dev
                    // uint64 at 0, st_ino uint64 at 8
                    let dev = if isMac then uint64 (System.BitConverter.ToUInt32 (buf, 0)) else System.BitConverter.ToUInt64 (buf, 0)
                    Some (dev, System.BitConverter.ToUInt64 (buf, 8))
        with _ -> None

    let private tryLink (src: string) (dest: string) =
        try
            if Env.isWindows then Native.CreateHardLink (dest, src, 0n)
            else Native.link (src, dest) = 0
        with _ -> false

    let private tryClone (src: string) (dest: string) =
        try
            if isMac then Native.clonefile (src, dest, 0u) = 0
            elif Env.isUnix then
                // Linux FICLONE (btrfs, XFS with reflink, bcachefs): O_RDONLY = 0;
                // O_WRONLY | O_CREAT | O_EXCL = 0x1 | 0x40 | 0x80
                let s = Native.openFile (src, 0, 0)
                if s < 0 then false else
                try
                    let d = Native.openFile (dest, 0xC1, 0o644)
                    if d < 0 then false else
                    let ok = (try Native.ioctl (d, unativeint 0x40049409u, s) = 0 finally Native.closeFile d |> ignore)
                    if not ok then (try File.Delete dest with _ -> ())
                    ok
                finally Native.closeFile s |> ignore
            else false   // Windows: CopyFile clones on ReFS / Dev Drive by itself (the copy below)
        with _ -> false

    let private modeName = function Link -> "link" | Clone -> "clone" | Copy -> "copy"

    /// <summary>
    /// Places `src` at `dest` (which must not exist): a hard link, else a clone, else a copy,
    /// starting at `preferred`; the way it was placed. A clone or a copy gets the source's
    /// write time (what `placedFrom` compares).
    /// </summary>
    let place (preferred: Mode) (src: string) (dest: string) : Mode =
        Directory.CreateDirectory (Path.GetDirectoryName dest) |> ignore
        let linked = preferred = Link && tryLink src dest
        if linked then Link
        else
            let mode = if preferred <> Copy && tryClone src dest then Clone else (File.Copy (src, dest, false); Copy)
            File.SetLastWriteTimeUtc (dest, File.GetLastWriteTimeUtc src)
            mode

    /// <summary>
    /// Whether `dest`, placed from `src` as `mode` ("link", "clone", "copy"), still is `src`'s
    /// content: a link has `src`'s identity (where identities cannot be read: its size and
    /// write time), a clone or a copy its size and write time.
    /// </summary>
    let placedFrom (mode: string) (src: string) (dest: string) : bool =
        File.Exists dest && File.Exists src &&
        let bySize () =
            let s, d = FileInfo src, FileInfo dest
            s.Length = d.Length && s.LastWriteTimeUtc = d.LastWriteTimeUtc
        match mode with
        | "link" ->
            match fileId src, fileId dest with
            | Some a, Some b -> a = b
            | _ -> bySize ()
        | _ -> bySize ()

    /// Whether two files have the same bytes.
    let sameContent (a: string) (b: string) : bool =
        a = b
        || (File.Exists a && File.Exists b
            && (fileId a |> Option.exists (fun i -> Some i = fileId b)
                || ((FileInfo a).Length = (FileInfo b).Length && Csc.sha256 a = Csc.sha256 b)))

    /// The assembly version of a managed assembly, `None` for any other file.
    let assemblyVersion (path: string) : System.Version option =
        if not (path.EndsWith (".dll", System.StringComparison.OrdinalIgnoreCase) || path.EndsWith (".exe", System.StringComparison.OrdinalIgnoreCase)) then None
        else
            try Some (System.Reflection.AssemblyName.GetAssemblyName path).Version
            with _ -> None

    /// A file a run folder may take: where it goes (`To`, relative, '/'), its source, and
    /// whether it is the root project's own (its outputs, items, copy-local files).
    type Candidate = { To: string; Source: string; Own: bool }

    /// Two sources of one path with different content, and which was kept.
    type Conflict = { Path: string; Kept: string; Dropped: string; Reason: string }

    /// <summary>
    /// The files of a run folder from its candidates, in order (the root's own first, then the
    /// closure in dependency order): the first source of a path is kept -- the root's own never
    /// replaced -- except that among the closure's sources a higher assembly version
    /// (`versionOf`, not the package version) replaces a lower one, as msbuild's conflict
    /// resolution keeps the higher assembly. Paths compare ignoring case (one folder holds one of
    /// them on Windows and macOS). Every dropped source whose content differs (`same`) is a
    /// conflict. The result keeps the first-seen order of paths.
    /// </summary>
    let plan (versionOf: string -> System.Version option) (same: string -> string -> bool) (candidates: Candidate list)
             : (string * string) list * Conflict list =
        let index = System.Collections.Generic.Dictionary<string, int> (System.StringComparer.OrdinalIgnoreCase)
        let chosen = ResizeArray<Candidate> ()
        let conflicts = ResizeArray<Conflict> ()
        let normal (p: string) = p.Replace('\\', '/').TrimStart '/'
        for c in candidates do
            let c = { c with To = normal c.To }
            match index.TryGetValue c.To with
            | false, _ -> index.[c.To] <- chosen.Count; chosen.Add c
            | true, i ->
                let kept = chosen.[i]
                if kept.Source <> c.Source && not (same kept.Source c.Source) then
                    if kept.Own then
                        conflicts.Add { Path = c.To; Kept = kept.Source; Dropped = c.Source; Reason = "the root's own" }
                    else
                        match versionOf kept.Source, versionOf c.Source with
                        | Some vk, Some vc when vc > vk ->
                            chosen.[i] <- c
                            conflicts.Add { Path = c.To; Kept = c.Source; Dropped = kept.Source; Reason = sprintf "assembly version %O > %O" vc vk }
                        | Some vk, Some vc ->
                            conflicts.Add { Path = c.To; Kept = kept.Source; Dropped = c.Source
                                            Reason = if vk = vc then sprintf "assembly version %O = %O, first in dependency order" vk vc else sprintf "assembly version %O > %O" vk vc }
                        | _ -> conflicts.Add { Path = c.To; Kept = kept.Source; Dropped = c.Source; Reason = "first in dependency order" }
        [ for c in chosen -> c.To, c.Source ], List.ofSeq conflicts

    /// The entries (path -> mode, source) of a manifest's text.
    let readManifest (text: string) : Map<string, string * string> =
        text.Split '\n'
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))
        |> Array.choose (fun l -> match l.TrimEnd('\r').Split '\t' with [| m; p; s |] -> Some (p, (m, s)) | _ -> None)
        |> Map.ofArray

    /// What `apply` did.
    type Summary = {
        Files: int
        Placed: int
        Kept: int
        Removed: int
        /// files the folder had that no run placed, with other content than planned: left as
        /// they are (never overwritten), not in the manifest
        Foreign: string list
        /// how many files each way ("link", "clone", "copy")
        Modes: (string * int) list
        /// whether the manifest was written (it is not when nothing changed)
        Written: bool
    }

    /// <summary>
    /// Brings the run folder of manifest `manifest` to `files` (path relative to the folder,
    /// source), against the previous manifest: an entry whose destination still is its source
    /// (`placedFrom`) is kept, any other placed again (the old file unlinked first, never written
    /// into), a path of the previous manifest no longer planned removed (and folders left
    /// empty). A file at a planned path that no previous run placed is taken over when it is
    /// the source's content, else left alone and reported (`Foreign`). `check` runs before a file
    /// is placed (a hash check). The manifest -- `header` lines (`#`), then mode, path, source
    /// per line -- is written only when its text changes, so a run that changes nothing touches
    /// nothing.
    /// </summary>
    let apply (preferred: Mode) (manifest: string) (header: string list) (check: string -> unit) (files: (string * string) list) : Summary =
        let folder = Path.GetDirectoryName (Path.GetFullPath manifest)
        let old = if File.Exists manifest then readManifest (File.ReadAllText manifest) else Map.empty
        let placed, kept = ref 0, ref 0
        let foreign = ResizeArray<string> ()
        let modes = System.Collections.Generic.SortedDictionary<string, int> ()
        let lines = ResizeArray<string> ()
        for path, src in files |> List.sortBy fst do
            let dest = Path.Combine (folder, path)
            let mode =
                match old.TryFind path with
                | Some (m, s) when s = src && placedFrom m src dest -> kept.Value <- kept.Value + 1; Some m
                | Some _ ->
                    if File.Exists dest then File.Delete dest                  // unlink: never write into a placed file
                    check src
                    placed.Value <- placed.Value + 1
                    Some (modeName (place preferred src dest))
                | None when File.Exists dest ->
                    // not placed by a run: taken over only when it is the source's content
                    if fileId src |> Option.exists (fun i -> Some i = fileId dest) then kept.Value <- kept.Value + 1; Some "link"
                    elif sameContent src dest then
                        File.Delete dest
                        check src
                        placed.Value <- placed.Value + 1
                        Some (modeName (place preferred src dest))
                    else foreign.Add path; None
                | None ->
                    check src
                    placed.Value <- placed.Value + 1
                    Some (modeName (place preferred src dest))
            match mode with
            | Some m ->
                modes.[m] <- (match modes.TryGetValue m with | true, n -> n | _ -> 0) + 1
                lines.Add (sprintf "%s\t%s\t%s" m path src)
            | None -> ()
        let planned = System.Collections.Generic.HashSet<string> (files |> List.map fst)
        let mutable removed = 0
        for KeyValue (path, _) in old do
            if not (planned.Contains path) then
                let dest = Path.Combine (folder, path)
                if File.Exists dest then
                    File.Delete dest
                    removed <- removed + 1
                    // the folders it leaves empty, up to the run folder
                    let mutable dir = Path.GetDirectoryName dest
                    while dir.Length > folder.Length && Directory.Exists dir && Array.isEmpty (Directory.GetFileSystemEntries dir) do
                        Directory.Delete dir
                        dir <- Path.GetDirectoryName dir
        let text = String.concat "\n" (header @ List.ofSeq lines) + "\n"
        let written = not (File.Exists manifest) || File.ReadAllText manifest <> text
        if written then
            Directory.CreateDirectory folder |> ignore
            File.WriteAllText (manifest, text)
        { Files = lines.Count; Placed = placed.Value; Kept = kept.Value; Removed = removed; Foreign = List.ofSeq foreign
          Modes = [ for kv in modes -> kv.Key, kv.Value ]; Written = written }
