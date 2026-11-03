#r "nuget: Xake, 3.15.0"
// measure-windows.fsx
// Windows counterpart of measurements-dotnet.md (D cases) and measurements-engine.md (E cases),
// plus Windows-only spelling checks (W cases). The same cases also run on macOS so the
// outputs can be compared side by side.
//
// Run from any directory:   dotnet fsi measure-windows.fsx
// Writes measurements-windows.md next to this script. Fixtures live in a fresh directory under
// the temp path and are removed at the end (best effort). Nothing else is written.

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Xake

// `open Xake` brings Xake.Path and Xake.File into scope, so System.IO types are used through aliases.
type IOPath = System.IO.Path
type IOFile = System.IO.File
type IODirectory = System.IO.Directory

let started = DateTime.Now
let isWin = RuntimeInformation.IsOSPlatform OSPlatform.Windows

// ---------------------------------------------------------------- helpers

let oneLine (s: string) = if isNull s then "null" else s.Replace("\r", " ").Replace("\n", " ")
let nz (s: string) = if isNull s then "null" else s
let quoted (xs: seq<string>) = "[" + String.Join("; ", xs |> Seq.map (fun s -> "\"" + s + "\"")) + "]"
let namesOf (paths: seq<string>) = paths |> Seq.map (fun p -> IOPath.GetFileName p) |> Seq.toList
let sortedNames (xs: seq<string>) = xs |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Seq.toList
let depthOf (rel: string) = rel.Split([| '\\'; '/' |]).Length

let probe (f: unit -> string) =
    try f ()
    with ex -> sprintf "EXCEPTION %s: %s" (ex.GetType().FullName) (oneLine ex.Message)

let show (out: ResizeArray<string>) (label: string) (f: unit -> string) =
    out.Add(sprintf "%s -> %s" label (probe f))

let says (out: ResizeArray<string>) (text: string) = out.Add text

let runProc (exe: string) (args: string) : string =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use p = Process.Start psi
        let o = p.StandardOutput.ReadToEnd()
        let e = p.StandardError.ReadToEnd()
        if not (p.WaitForExit(30000)) then p.Kill()
        sprintf "exit=%d stdout=%s stderr=%s" p.ExitCode (oneLine o) (oneLine e)
    with ex -> sprintf "EXCEPTION %s: %s" (ex.GetType().FullName) (oneLine ex.Message)

/// Creates a link. Returns "created ...", "skipped: ..." or "failed: ...".
/// Symlinks try the .NET API first and, on Windows, `mklink` as a fallback.
let mkLink (kind: string) (link: string) (target: string) : string =
    let viaCmd (args: string) =
        let r = runProc "cmd.exe" ("/c " + args)
        if r.StartsWith("exit=0") then "created (cmd mklink)" else "failed (cmd): " + r
    try
        match kind with
        | "dirsymlink" | "filesymlink" ->
            let flag = if kind = "dirsymlink" then " /D" else ""
            try
                (if kind = "dirsymlink" then IODirectory.CreateSymbolicLink(link, target) |> ignore
                 else IOFile.CreateSymbolicLink(link, target) |> ignore)
                "created (API)"
            with ex ->
                let apiErr = ex.GetType().Name + ": " + oneLine ex.Message
                if isWin then
                    let c = viaCmd (sprintf "mklink%s \"%s\" \"%s\"" flag link target)
                    if c.StartsWith("created") then c else c + " [API: " + apiErr + "]"
                else "failed (API): " + apiErr
        | "junction" ->
            if isWin then viaCmd (sprintf "mklink /J \"%s\" \"%s\"" link target)
            else "skipped: junctions are Windows only"
        | _ -> "unknown kind " + kind
    with ex -> "failed: " + ex.GetType().Name + ": " + oneLine ex.Message

let fmtGroups (r: (string * string) list option) =
    match r with
    | None -> "None"
    | Some xs -> "Some [" + String.Join("; ", xs |> List.map (fun (k, v) -> sprintf "(\"%s\", \"%s\")" k v)) + "]"

// ---------------------------------------------------------------- work area and fixtures

let work = IOPath.Combine(IOPath.GetTempPath(), "xake-measure-" + Guid.NewGuid().ToString("N"))
IODirectory.CreateDirectory(work) |> ignore
printfn "fixtures under a temp directory named %s (removed at the end)" (IOPath.GetFileName work)

let dirFor (id: string) =
    let d = IOPath.Combine(work, id)
    IODirectory.CreateDirectory(d) |> ignore
    d

let put (dir: string) (rel: string) =
    let full = IOPath.Combine(dir, rel)
    IODirectory.CreateDirectory(IOPath.GetDirectoryName(full)) |> ignore
    IOFile.WriteAllText(full, "")
    full

let tryPut (out: ResizeArray<string>) (dir: string) (rel: string) : bool =
    try
        put dir rel |> ignore
        true
    with ex ->
        out.Add(sprintf "CREATE FAILED \"%s\": %s: %s" rel (ex.GetType().Name) (oneLine ex.Message))
        false

let eng (id: string) (files: string list) =
    let d = dirFor id
    for f in files do put d f |> ignore
    d

// ---------------------------------------------------------------- environment

let volumeCase =
    try
        let d = dirFor "env-volume"
        put d "a.cs" |> ignore
        if IOFile.Exists(IOPath.Combine(d, "A.CS")) then "case-insensitive (File.Exists(\"A.CS\") = true)"
        else "case-sensitive (File.Exists(\"A.CS\") = false)"
    with ex -> "unknown: " + oneLine ex.Message

let driveFormat =
    try (DriveInfo(IOPath.GetPathRoot(work))).DriveFormat
    with ex -> "unknown: " + oneLine ex.Message

let xakeVersion =
    try string (typeof<Xake.Path.PathMask>.Assembly.GetName().Version)
    with ex -> "unknown: " + oneLine ex.Message

// ---------------------------------------------------------------- case runner

type Case = { Id: string; Title: string; Inputs: string list; Lines: string list }
let cases = ResizeArray<Case>()

let runCase (id: string) (title: string) (inputs: string list) (body: ResizeArray<string> -> unit) =
    printfn "  %-5s %s" id title
    let out = ResizeArray<string>()
    try body out
    with ex -> out.Add(sprintf "EXCEPTION %s: %s" (ex.GetType().FullName) (oneLine ex.Message))
    cases.Add { Id = id; Title = title; Inputs = inputs; Lines = List.ofSeq out }

/// Lazy enumeration capped at 10000 entries and 30 s. Returns entries, stop reason and seconds.
let enumCapped (source: unit -> seq<string>) : ResizeArray<string> * string * float =
    let sw = Stopwatch.StartNew()
    let items = ResizeArray<string>()
    let mutable reason = "completed"
    let mutable go = true
    try
        use e = (source ()).GetEnumerator()
        while go do
            if items.Count >= 10000 then
                reason <- "stopped at 10000 entries"
                go <- false
            elif sw.Elapsed.TotalSeconds > 30.0 then
                reason <- "stopped after 30 s"
                go <- false
            elif e.MoveNext() then items.Add e.Current
            else go <- false
    with ex ->
        reason <- sprintf "exception %s: %s" (ex.GetType().FullName) (oneLine ex.Message)
    (items, reason, sw.Elapsed.TotalSeconds)

let summarize (root: string) (items: ResizeArray<string>) (reason: string) (secs: float) =
    let rels = items |> Seq.map (fun p -> IOPath.GetRelativePath(root, p)) |> Seq.toList
    let maxDepth = rels |> List.fold (fun m r -> max m (depthOf r)) 0
    sprintf "count=%d reason=%s seconds=%.2f maxDepth=%d first12=%s"
        items.Count reason secs maxDepth (quoted (List.truncate 12 rels))

// ---------------------------------------------------------------- D cases: System.IO enumeration

let d1Fixture = [ "a.cs"; "a.csx"; "b"; ".hidden.cs"; "x[1].cs"; "x{1}.cs"; "(x).cs" ]
let d1Patterns =
    [ "*.cs"; "*.c?"; "?.cs"; "*.*"; "*"; "x[1].cs"; "["; "(x).cs"; "\""; "<"; ">"; "a.cs"; "b?"; "*.c"; "a.cs?" ]

let d2Patterns = [ "*.CS"; "A.CS"; "*.cs"; "*.*"; "b?"; "*.c"; "a.cs?" ]

let linkCase (id: string) (kind: string) (cycle: bool) =
    let what = if cycle then "cycle back to the root" else "to real/"
    runCase id (sprintf "symlink/junction: %s, %s" kind what)
        [ sprintf "tree: real/x.cs, top.cs, y.cs (file symlink -> real/x.cs), %s (%s %s)"
              (if cycle then "loop" else "link") kind what
          "legacy EnumerateFiles(root,*.cs,AllDirectories); EnumerationOptions{RecurseSubdirectories}; same with AttributesToSkip=ReparsePoint"
          "cap 10000 entries and 30 s stopwatch" ]
        (fun out ->
            let root = dirFor id
            put root "real/x.cs" |> ignore
            put root "top.cs" |> ignore
            let linkName = if cycle then "loop" else "link"
            let linkPath = IOPath.Combine(root, linkName)
            let target =
                if kind = "junction" then (if cycle then root else IOPath.Combine(root, "real"))
                elif cycle then "."
                else "real"
            let first = mkLink kind linkPath target
            let status =
                if cycle && kind = "dirsymlink" && not (first.StartsWith "created") then
                    out.Add(sprintf "link target \".\" -> %s; retrying with the absolute root" first)
                    mkLink kind linkPath root
                else first
            out.Add(sprintf "create %s %s -> %s: %s" kind linkName target status)
            let file = IOPath.Combine(root, "y.cs")
            out.Add(sprintf "create filesymlink y.cs -> real/x.cs: %s" (mkLink "filesymlink" file (IOPath.Combine("real", "x.cs"))))
            show out "attributes link" (fun () ->
                if IODirectory.Exists linkPath || IOFile.Exists linkPath then string (IOFile.GetAttributes linkPath)
                else "not present")
            show out "LinkTarget link" (fun () -> nz (DirectoryInfo(linkPath).LinkTarget))
            show out "attributes y.cs" (fun () ->
                if IOFile.Exists file then string (IOFile.GetAttributes file) else "not present")
            show out "LinkTarget y.cs" (fun () -> nz (FileInfo(file).LinkTarget))
            show out "EnumerateFiles(link,*.cs) direct" (fun () ->
                quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(linkPath, "*.cs")))))
            let runEnum (label: string) (src: unit -> seq<string>) =
                let (items, reason, secs) = enumCapped src
                out.Add(sprintf "%s -> %s" label (summarize root items reason secs))
            runEnum "EnumerateFiles(root,*.cs,AllDirectories) legacy" (fun () ->
                IODirectory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            runEnum "EnumerationOptions{Recurse} (default AttributesToSkip)" (fun () ->
                IODirectory.EnumerateFiles(root, "*.cs", EnumerationOptions(RecurseSubdirectories = true)))
            runEnum "EnumerationOptions{Recurse, AttributesToSkip=ReparsePoint}" (fun () ->
                IODirectory.EnumerateFiles(
                    root, "*.cs", EnumerationOptions(RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint))))

let rec clearLinks (dir: string) =
    for sub in IODirectory.GetDirectories(dir) do
        let attrs = try IOFile.GetAttributes(sub) with _ -> FileAttributes.Normal
        if attrs.HasFlag(FileAttributes.ReparsePoint) then
            try IODirectory.Delete(sub, false)
            with _ -> (try IOFile.Delete(sub) with _ -> ())
        else clearLinks sub

// D cases ---------------------------------------------------------

runCase "D1" "search pattern semantics, legacy Directory.EnumerateFiles(dir, pattern)"
    [ "fixture: a.cs a.csx b .hidden.cs x[1].cs x{1}.cs (x).cs"
      "patterns: *.cs *.c? ?.cs *.* * x[1].cs [ (x).cs \" < > a.cs b? *.c a.cs?"
      "names only, sorted" ]
    (fun out ->
        let d = dirFor "d1"
        for n in d1Fixture do tryPut out d n |> ignore
        says out ("on disk -> " + quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(d)))))
        for p in d1Patterns do
            show out (sprintf "EnumerateFiles(dir,%s)" p) (fun () ->
                quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(d, p))))))

runCase "D1b" "Win32 vs Simple probe (macOS probe fixture; invalid Windows names are skipped)"
    [ "fixture: b q.c \" < > a<b>.cs a\"b.cs"
      "options: EnumerationOptions(MatchType = Win32 | Simple), default casing and skip options" ]
    (fun out ->
        let d = dirFor "d1b"
        for n in [ "b"; "q.c"; "\""; "<"; ">"; "a<b>.cs"; "a\"b.cs" ] do tryPut out d n |> ignore
        says out ("on disk -> " + quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(d)))))
        for typ in [ MatchType.Win32; MatchType.Simple ] do
            for p in [ "b?"; "b*"; "?"; "\""; "<"; ">"; "*\"*"; "a?b?.cs"; "q.c?"; "q?" ] do
                show out (sprintf "%A EnumerateFiles(dir,%s)" typ p) (fun () ->
                    quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(d, p, EnumerationOptions(MatchType = typ)))))))

runCase "D2" "EnumerationOptions MatchCasing x MatchType (default AttributesToSkip, no recursion)"
    [ "fixture: as D1 (no A.CS)"
      sprintf "patterns: %s" (String.Join(" ", d2Patterns)) ]
    (fun out ->
        let d = dirFor "d2"
        for n in d1Fixture do tryPut out d n |> ignore
        for cas in [ MatchCasing.CaseSensitive; MatchCasing.CaseInsensitive; MatchCasing.PlatformDefault ] do
            for typ in [ MatchType.Win32; MatchType.Simple ] do
                for p in d2Patterns do
                    show out (sprintf "%A/%A %s" cas typ p) (fun () ->
                        let o = EnumerationOptions(MatchCasing = cas, MatchType = typ)
                        quoted (sortedNames (namesOf (IODirectory.EnumerateFiles(d, p, o))))))

runCase "D3" "volume case sensitivity; spelling returned by GetFiles and FileInfo"
    [ "fixture: a.cs only"
      "probe: File.Exists, GetFiles, EnumerateFiles with MatchCasing, FileInfo, ReadAllText on A.CS" ]
    (fun out ->
        let d = dirFor "d3"
        put d "a.cs" |> ignore
        let a = IOPath.Combine(d, "A.CS")
        show out "File.Exists(dir/A.CS)" (fun () -> string (IOFile.Exists a))
        show out "File.Exists(dir/a.cs)" (fun () -> string (IOFile.Exists(IOPath.Combine(d, "a.cs"))))
        show out "Directory.GetFiles(dir,A.CS)" (fun () -> quoted (namesOf (IODirectory.GetFiles(d, "A.CS"))))
        show out "Directory.GetFiles(dir,*.CS)" (fun () -> quoted (namesOf (IODirectory.GetFiles(d, "*.CS"))))
        show out "EnumerateFiles(dir,A.CS,CaseSensitive)" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "A.CS", EnumerationOptions(MatchCasing = MatchCasing.CaseSensitive)))))
        show out "EnumerateFiles(dir,A.CS,CaseInsensitive)" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "A.CS", EnumerationOptions(MatchCasing = MatchCasing.CaseInsensitive)))))
        show out "FileInfo(dir/A.CS).Name" (fun () -> FileInfo(a).Name)
        show out "FileInfo(dir/A.CS).Exists" (fun () -> string (FileInfo(a).Exists))
        show out "File.ReadAllText(dir/A.CS)" (fun () -> "\"" + IOFile.ReadAllText(a) + "\""))

linkCase "D4a" "dirsymlink" false
linkCase "D4b" "junction" false
linkCase "D4c" "dirsymlink" true
linkCase "D4d" "junction" true

runCase "D5" "enumeration order (created as c a b a2 a10)"
    [ "files created in this order: c, a, b, a2, a10"
      "two EnumerateFiles calls, GetFiles, and the OS listing (cmd dir /b on Windows, ls -f elsewhere)" ]
    (fun out ->
        let d = dirFor "d5"
        for n in [ "c"; "a"; "b"; "a2"; "a10" ] do tryPut out d n |> ignore
        show out "EnumerateFiles(dir,*) call 1" (fun () -> quoted (namesOf (IODirectory.EnumerateFiles(d, "*"))))
        show out "EnumerateFiles(dir,*) call 2" (fun () -> quoted (namesOf (IODirectory.EnumerateFiles(d, "*"))))
        show out "GetFiles(dir,*)" (fun () -> quoted (namesOf (IODirectory.GetFiles(d, "*"))))
        show out "EnumerateFiles(dir,*,default options)" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "*", EnumerationOptions()))))
        says out "sorted (ordinal) -> [\"a\"; \"a10\"; \"a2\"; \"b\"; \"c\"] (reference)"
        if isWin then says out ("cmd dir /b -> " + runProc "cmd.exe" ("/c dir /b \"" + d + "\""))
        else says out ("ls -f -> " + runProc "ls" ("-f \"" + d + "\"")))

runCase "D6" "hidden files: dotfile and Hidden attribute, legacy vs default EnumerationOptions"
    [ "fixture: .hidden.cs, a.cs, b, hidden2.cs (File.SetAttributes Hidden)" ]
    (fun out ->
        let d = dirFor "d6"
        for n in [ ".hidden.cs"; "a.cs"; "b"; "hidden2.cs" ] do tryPut out d n |> ignore
        show out "SetAttributes(hidden2.cs, Hidden)" (fun () ->
            IOFile.SetAttributes(IOPath.Combine(d, "hidden2.cs"), FileAttributes.Hidden)
            "ok")
        show out "legacy EnumerateFiles(dir,*)" (fun () -> quoted (namesOf (IODirectory.EnumerateFiles(d, "*"))))
        show out "EnumerationOptions() default, *" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "*", EnumerationOptions()))))
        show out "EnumerationOptions AttributesToSkip=None, *" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "*", EnumerationOptions(AttributesToSkip = FileAttributes.None)))))
        show out "EnumerateFiles(dir,.hidden.cs,default options)" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, ".hidden.cs", EnumerationOptions()))))
        for n in [ ".hidden.cs"; "a.cs"; "b"; "hidden2.cs" ] do
            show out (sprintf "Attributes %s" n) (fun () -> string (IOFile.GetAttributes(IOPath.Combine(d, n)))))

runCase "D7" "Unicode normalization (NFC and NFD spelling of the same name)"
    [ "creates \"caf\\u00E9.cs\" (NFC) then \"cafe\\u0301.cs\" (NFD) with File.Create"
      "file names are written with \\u escapes, so the script is ASCII only" ]
    (fun out ->
        let d = dirFor "d7"
        let nfc = "café.cs"
        let nfd = "café.cs"
        tryPut out d nfc |> ignore
        out.Add(sprintf "entries after NFC create: %d" (Seq.length (IODirectory.EnumerateFileSystemEntries(d))))
        tryPut out d nfd |> ignore
        out.Add(sprintf "entries after NFD create: %d" (Seq.length (IODirectory.EnumerateFileSystemEntries(d))))
        let codes (s: string) = String.Join(" ", s |> Seq.map (fun c -> sprintf "%04X" (int c)))
        show out "on-disk name codepoints" (fun () ->
            String.Join(" | ", IODirectory.EnumerateFiles(d) |> Seq.map (fun p -> codes (IOPath.GetFileName p))))
        show out "EnumerateFiles(dir,caf*)" (fun () -> quoted (namesOf (IODirectory.EnumerateFiles(d, "caf*"))))
        show out "EnumerateFiles(dir,NFC name)" (fun () -> string (Seq.length (IODirectory.EnumerateFiles(d, nfc))))
        show out "EnumerateFiles(dir,NFD name)" (fun () -> string (Seq.length (IODirectory.EnumerateFiles(d, nfd))))
        show out "File.Exists(NFC path)" (fun () -> string (IOFile.Exists(IOPath.Combine(d, nfc))))
        show out "File.Exists(NFD path)" (fun () -> string (IOFile.Exists(IOPath.Combine(d, nfd)))))

runCase "D8" "unreadable directory"
    [ "not measured: needs an ACL change (icacls) on a directory owned by another account" ]
    (fun out -> says out "SKIPPED: an unreadable directory needs ACL changes; not run here (see the macOS chmod case)")

runCase "D9" "8.3 short names"
    [ "fixture: LongFileName.cs"
      "probes: LONGFI~1.cs and *~1* through the legacy overload and MatchType=Simple; dir /x and fsutil on Windows" ]
    (fun out ->
        let d = dirFor "d9"
        tryPut out d "LongFileName.cs" |> ignore
        show out "EnumerateFiles(dir,LONGFI~1.cs) legacy" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "LONGFI~1.cs"))))
        show out "EnumerateFiles(dir,*~1*) legacy" (fun () -> quoted (namesOf (IODirectory.EnumerateFiles(d, "*~1*"))))
        show out "EnumerateFiles(dir,LONGFI~1.cs,MatchType=Simple)" (fun () ->
            quoted (namesOf (IODirectory.EnumerateFiles(d, "LONGFI~1.cs", EnumerationOptions(MatchType = MatchType.Simple)))))
        show out "GetFiles(dir,LONGFI~1.CS)" (fun () -> quoted (namesOf (IODirectory.GetFiles(d, "LONGFI~1.CS"))))
        show out "File.Exists(dir/LONGFI~1.CS)" (fun () -> string (IOFile.Exists(IOPath.Combine(d, "LONGFI~1.CS"))))
        if isWin then
            says out ("cmd dir /x /b -> " + runProc "cmd.exe" ("/c dir /x /b \"" + d + "\""))
            let drive = IOPath.GetPathRoot(work).TrimEnd('\\')
            says out ("fsutil 8dot3name query -> " + runProc "fsutil.exe" ("8dot3name query " + drive))
        else says out "dir /x and fsutil: Windows only, not run here")

// ---------------------------------------------------------------- E cases: Xake 3.15 engine

let scanRel (root: string) fs =
    let (Filelist files) = toFileList root fs
    let full = IOPath.GetFullPath(root)
    files |> List.map (fun f -> IOPath.GetRelativePath(full, IOPath.GetFullPath(Xake.File.getFullName f)))

let pm (s: string) = oneLine (sprintf "%A" (Xake.Path.parse s))
let mt (pat: string) (root: string) (file: string) = string (Xake.Path.matches pat root file)
let grp (pat: string) (root: string) (file: string) = fmtGroups (Xake.Path.matchGroups pat root file)

let cycleScan (root: string) =
    let t = Task.Run<string list>(fun () -> scanRel root (!! "**/*.cs"))
    if t.Wait(30000) then
        if t.IsFaulted then
            let inner = t.Exception.InnerException
            "faulted: " + oneLine (inner.GetType().FullName + ": " + inner.Message)
        else
            let xs = t.Result
            sprintf "finished: count=%d maxDepth=%d first12=%s"
                xs.Length (xs |> List.fold (fun m r -> max m (depthOf r)) 0) (quoted (List.truncate 12 xs))
    else "NOT finished after 30 s (task left running)"

runCase "E1" "a//b (double separator)"
    [ "tree e1: a/b, a/c" ]
    (fun out ->
        let r = eng "e1" [ "a/b"; "a/c" ]
        show out "Path.parse \"a//b\"" (fun () -> pm "a//b")
        show out "Path.parse \"a/b\"" (fun () -> pm "a/b")
        show out "matches \"a//b\" vs \"a/b\"" (fun () -> mt "a//b" "" "a/b")
        show out "matchGroups \"a//b\" vs \"a/b\"" (fun () -> grp "a//b" "" "a/b")
        show out "matchGroups \"a//b\" vs \"a//b\"" (fun () -> grp "a//b" "" "a//b")
        show out "scan !! \"a//b\"" (fun () -> quoted (scanRel r (!! "a//b")))
        show out "scan !! \"a/b\"" (fun () -> quoted (scanRel r (!! "a/b"))))

runCase "E2" "empty pattern \"\""
    [ "tree e2: a/b, x.cs" ]
    (fun out ->
        let r = eng "e2" [ "a/b"; "x.cs" ]
        show out "Path.parse \"\"" (fun () -> pm "")
        show out "matches \"\" vs \"aa\"" (fun () -> mt "" "" "aa")
        show out "matches \"\" vs \"\"" (fun () -> mt "" "" "")
        show out "matchGroups \"\" vs \"aa\"" (fun () -> grp "" "" "aa")
        show out "scan !! \"\"" (fun () -> quoted (scanRel r (!! "")))
        show out "scan !! \"\" + \"*.cs\"" (fun () -> quoted (scanRel r (!! "" + "*.cs"))))

runCase "E3" "'.' and '..' after a wildcard"
    [ "tree e3: src/d/x.cs, src/x.cs" ]
    (fun out ->
        let r = eng "e3" [ "src/d/x.cs"; "src/x.cs" ]
        show out "Path.parse \"src/*/./x.cs\"" (fun () -> pm "src/*/./x.cs")
        show out "matches \"src/*/./x.cs\" vs \"src/d/x.cs\"" (fun () -> mt "src/*/./x.cs" "" "src/d/x.cs")
        show out "matchGroups \"src/*/./x.cs\" vs \"src/d/x.cs\"" (fun () -> grp "src/*/./x.cs" "" "src/d/x.cs")
        show out "matches \"src/*/../x.cs\" vs \"src/x.cs\"" (fun () -> mt "src/*/../x.cs" "" "src/x.cs")
        show out "matches \"src/*/../x.cs\" vs \"src/d/x.cs\"" (fun () -> mt "src/*/../x.cs" "" "src/d/x.cs")
        show out "Path.parse \"src/*/../x.cs\"" (fun () -> pm "src/*/../x.cs")
        show out "scan !! \"src/*/./x.cs\"" (fun () -> quoted (scanRel r (!! "src/*/./x.cs")))
        show out "scan !! \"src/*/../x.cs\"" (fun () -> quoted (scanRel r (!! "src/*/../x.cs")))
        show out "scan !! \"src/./d/x.cs\"" (fun () -> quoted (scanRel r (!! "src/./d/x.cs"))))

runCase "E4" "symlinks, junctions and a cycle during a scan"
    [ "tree e4: real/x.cs, link -> real (dir symlink), jct -> real (junction, Windows), y.cs -> real/x.cs (file symlink)"
      "tree e4loop: real/x.cs, loop -> root (dir symlink, then absolute target), cycle run with a 30 s Task.Wait" ]
    (fun out ->
        let r = eng "e4" [ "real/x.cs" ]
        out.Add("create dirsymlink link -> real: " + mkLink "dirsymlink" (IOPath.Combine(r, "link")) "real")
        out.Add("create junction jct -> real: " + mkLink "junction" (IOPath.Combine(r, "jct")) (IOPath.Combine(r, "real")))
        out.Add("create filesymlink y.cs -> real/x.cs: "
                + mkLink "filesymlink" (IOPath.Combine(r, "y.cs")) (IOPath.Combine("real", "x.cs")))
        show out "mask **/*.cs" (fun () -> quoted (scanRel r (!! "**/*.cs")))
        show out "mask */*.cs" (fun () -> quoted (scanRel r (!! "*/*.cs")))
        show out "mask link/*.cs" (fun () -> quoted (scanRel r (!! "link/*.cs")))
        show out "mask jct/*.cs" (fun () -> quoted (scanRel r (!! "jct/*.cs")))
        show out "mask *.cs" (fun () -> quoted (scanRel r (!! "*.cs")))
        let rl = eng "e4loop" [ "real/x.cs" ]
        let first = mkLink "dirsymlink" (IOPath.Combine(rl, "loop")) "."
        let status = if first.StartsWith "created" then first else mkLink "dirsymlink" (IOPath.Combine(rl, "loop")) rl
        out.Add("create dirsymlink loop -> \".\": " + status)
        show out "cycle mask **/*.cs" (fun () -> cycleScan rl))

runCase "E5" "duplicates across includes"
    [ "tree e5: top.cs, src/a.cs, src/b.cs" ]
    (fun out ->
        let r = eng "e5" [ "top.cs"; "src/a.cs"; "src/b.cs" ]
        show out "mask *.cs + *.cs" (fun () -> quoted (scanRel r (!! "*.cs" + "*.cs")))
        show out "mask **/*.cs + src/*.cs" (fun () -> quoted (scanRel r (!! "**/*.cs" + "src/*.cs")))
        show out "mask src/*.cs + **/*.cs" (fun () -> quoted (scanRel r (!! "src/*.cs" + "**/*.cs"))))

runCase "E6" "trailing '/' in an exclude"
    [ "tree e6: obj/a.o, other/obj (a file), other/keep.o, k.o" ]
    (fun out ->
        let r = eng "e6" [ "obj/a.o"; "other/obj"; "other/keep.o"; "k.o" ]
        show out "Path.parse \"obj/\"" (fun () -> pm "obj/")
        show out "Path.parse \"obj\"" (fun () -> pm "obj")
        show out "mask **/* -- obj/" (fun () -> quoted (scanRel r (!! "**/*" -- "obj/")))
        show out "mask **/* -- obj" (fun () -> quoted (scanRel r (!! "**/*" -- "obj")))
        show out "mask **/* (no exclude)" (fun () -> quoted (scanRel r (!! "**/*")))
        show out "mask **/* -- **/obj" (fun () -> quoted (scanRel r (!! "**/*" -- "**/obj")))
        show out "mask **/* -- **/obj/" (fun () -> quoted (scanRel r (!! "**/*" -- "**/obj/")))
        show out "mask **/* -- obj/**" (fun () -> quoted (scanRel r (!! "**/*" -- "obj/**")))
        show out "mask **/* -- obj/**/*" (fun () -> quoted (scanRel r (!! "**/*" -- "obj/**/*")))
        show out "mask **/* -- **/obj/*" (fun () -> quoted (scanRel r (!! "**/*" -- "**/obj/*"))))

runCase "E7" "globstar: zero vs one directory"
    [ "tree e7: x.dll, a/x.dll, dir/a, dir/sub/b" ]
    (fun out ->
        let r = eng "e7" [ "x.dll"; "a/x.dll"; "dir/a"; "dir/sub/b" ]
        show out "matches \"**/x.dll\" vs \"x.dll\"" (fun () -> mt "**/x.dll" "" "x.dll")
        show out "matches \"**/x.dll\" vs \"a/x.dll\"" (fun () -> mt "**/x.dll" "" "a/x.dll")
        show out "matchGroups \"**/x.dll\" vs \"x.dll\"" (fun () -> grp "**/x.dll" "" "x.dll")
        show out "matchGroups \"**/x.dll\" vs \"a/x.dll\"" (fun () -> grp "**/x.dll" "" "a/x.dll")
        show out "Path.parse \"dir/**\"" (fun () -> pm "dir/**")
        show out "mask **/x.dll" (fun () -> quoted (scanRel r (!! "**/x.dll")))
        show out "mask dir/**" (fun () -> quoted (scanRel r (!! "dir/**")))
        show out "mask dir/**/*" (fun () -> quoted (scanRel r (!! "dir/**/*"))))

runCase "E8" "dotfiles"
    [ "tree e8: .hidden/y.cs, vis/z.cs" ]
    (fun out ->
        let r = eng "e8" [ ".hidden/y.cs"; "vis/z.cs" ]
        show out "mask **/*.cs" (fun () -> quoted (scanRel r (!! "**/*.cs")))
        show out "mask */*.cs" (fun () -> quoted (scanRel r (!! "*/*.cs")))
        show out "matches \"*/*.cs\" vs \".hidden/y.cs\"" (fun () -> mt "*/*.cs" "" ".hidden/y.cs"))

runCase "E9" "case sensitivity of paths"
    [ "tree e9a: Src/A.cs; tree e9b: src/a.cs (the two trees are separate directories)" ]
    (fun out ->
        let ra = eng "e9a" [ "Src/A.cs" ]
        let rb = eng "e9b" [ "src/a.cs" ]
        show out "mask src/*.cs on tree e9a" (fun () -> quoted (scanRel ra (!! "src/*.cs")))
        show out "mask Src/*.cs on tree e9a" (fun () -> quoted (scanRel ra (!! "Src/*.cs")))
        show out "mask SRC/a.cs (literal) on tree e9b" (fun () -> quoted (scanRel rb (!! "SRC/a.cs")))
        show out "mask SRC/*.cs on tree e9b" (fun () -> quoted (scanRel rb (!! "SRC/*.cs")))
        show out "matches \"src/*.cs\" vs \"Src/a.cs\"" (fun () -> mt "src/*.cs" "" "Src/a.cs")
        show out "matches \"Src/*.cs\" vs \"src/A.cs\"" (fun () -> mt "Src/*.cs" "" "src/A.cs")
        show out "matches \"SRC/a.cs\" vs \"src/a.cs\"" (fun () -> mt "SRC/a.cs" "" "src/a.cs")
        show out "matchGroups \"src/*.cs\" vs \"Src/a.cs\"" (fun () -> grp "src/*.cs" "" "Src/a.cs")
        show out "matchGroups \"src/*.cs\" vs \"src/a.cs\"" (fun () -> grp "src/*.cs" "" "src/a.cs"))

runCase "E10" "group captures with '**' and named groups"
    [ "tree e10: bin/file.o, bin/x/y/file.o" ]
    (fun out ->
        let r = eng "e10" [ "bin/file.o"; "bin/x/y/file.o" ]
        show out "matchGroups \"(path:bin/**)/file.o\" vs \"bin/file.o\"" (fun () -> grp "(path:bin/**)/file.o" "" "bin/file.o")
        show out "matches \"(path:bin/**)/file.o\" vs \"bin/x/y/file.o\"" (fun () -> mt "(path:bin/**)/file.o" "" "bin/x/y/file.o")
        show out "matchGroups \"(path:bin/**)/file.o\" vs \"bin/x/y/file.o\"" (fun () -> grp "(path:bin/**)/file.o" "" "bin/x/y/file.o")
        show out "matches \"bin/**/file.o\" vs \"bin/file.o\"" (fun () -> mt "bin/**/file.o" "" "bin/file.o")
        show out "matches \"(path:bin/**/file.o)\" vs \"bin/file.o\"" (fun () -> mt "(path:bin/**/file.o)" "" "bin/file.o")
        show out "matchGroups \"(apl:*/*)/x\" vs \"a/b/x\"" (fun () -> grp "(apl:*/*)/x" "" "a/b/x")
        show out "matchGroups \"(apl:*/*)/x\" vs \"a/b/c/x\"" (fun () -> grp "(apl:*/*)/x" "" "a/b/c/x")
        show out "matches \"(apl:*/*)/x\" vs \"a/b/x\"" (fun () -> mt "(apl:*/*)/x" "" "a/b/x")
        show out "matches \"(d:*)/x\" vs \"a/x\"" (fun () -> mt "(d:*)/x" "" "a/x")
        show out "scan !! \"bin/**/file.o\"" (fun () -> quoted (scanRel r (!! "bin/**/file.o"))))

// Windows spelling checks: what a backslash means in 3.x on this platform.
runCase "W1" "backslash in a mask: src\\*.cs versus src/*.cs"
    [ "tree w1: src/a.cs, src/b.cs, top.cs"
      "\"src\" </> \"*.cs\" uses the Xake.PathExt operator (present in 3.15.0)" ]
    (fun out ->
        let r = eng "w1" [ "src/a.cs"; "src/b.cs"; "top.cs" ]
        show out "scan !! \"src/*.cs\"" (fun () -> quoted (scanRel r (!! "src/*.cs")))
        show out "scan !! \"src\\\\*.cs\"" (fun () -> quoted (scanRel r (!! "src\\*.cs")))
        show out "Path.parse \"src\\\\*.cs\"" (fun () -> pm "src\\*.cs")
        show out "Path.parseDir \"src\\\\\"" (fun () -> oneLine (sprintf "%A" (Xake.Path.parseDir "src\\")))
        show out "scan !! (\"src\" </> \"*.cs\")" (fun () -> quoted (scanRel r (!! ("src" </> "*.cs")))))

runCase "W2" "backslash in a rule: src\\(name:*).cs versus src/(name:*).cs"
    [ "no tree; file names are passed as strings" ]
    (fun out ->
        show out "matches \"src/(name:*).cs\" vs \"src/a.cs\"" (fun () -> mt "src/(name:*).cs" "" "src/a.cs")
        show out "matches \"src\\\\(name:*).cs\" vs \"src\\\\a.cs\"" (fun () -> mt "src\\(name:*).cs" "" "src\\a.cs")
        show out "matches \"src\\\\(name:*).cs\" vs \"src/a.cs\"" (fun () -> mt "src\\(name:*).cs" "" "src/a.cs")
        show out "matchGroups \"src/(name:*).cs\" vs \"src/a.cs\"" (fun () -> grp "src/(name:*).cs" "" "src/a.cs")
        show out "matchGroups \"src\\\\(name:*).cs\" vs \"src\\\\a.cs\"" (fun () -> grp "src\\(name:*).cs" "" "src\\a.cs")
        show out "matchGroups \"src\\\\(name:*).cs\" vs \"src/a.cs\"" (fun () -> grp "src\\(name:*).cs" "" "src/a.cs")
        show out "matches \"src\\\\*.cs\" vs \"src/a.cs\"" (fun () -> mt "src\\*.cs" "" "src/a.cs"))

// ---------------------------------------------------------------- cleanup

let cleanupNote =
    try
        clearLinks work
        IODirectory.Delete(work, true)
        "removed"
    with ex -> "cleanup incomplete: " + oneLine ex.Message

// ---------------------------------------------------------------- report

let elapsed = (DateTime.Now - started).TotalSeconds
let md = System.Text.StringBuilder()
let line (s: string) = md.AppendLine(s) |> ignore

line "# Windows measurements: .NET enumeration and Xake 3.15 engine"
line ""
line (sprintf "Generated by `measure-windows.fsx` on %s (run time %.1f s). Case ids: D1-D9 mirror the case"
        (started.ToString("yyyy-MM-dd HH:mm")) elapsed)
line "numbers of measurements-dotnet.md (D1b is an extra probe; D8 is not run here); E1-E10 mirror"
line "measurements-engine.md; W1-W2 are Windows spelling checks. Paths in results are relative to the fixture."
line "Compare with the macOS sections by id; on Windows the separator in relative paths is a backslash."
line ""
line "## Environment"
line ""
line (sprintf "- RuntimeInformation.OSDescription: %s" RuntimeInformation.OSDescription)
line (sprintf "- Environment.OSVersion: %s" (string Environment.OSVersion))
line (sprintf "- Environment.Version: %s" (string Environment.Version))
line (sprintf "- RuntimeInformation.FrameworkDescription: %s" RuntimeInformation.FrameworkDescription)
line (sprintf "- RuntimeInformation.ProcessArchitecture: %s" (string RuntimeInformation.ProcessArchitecture))
line (sprintf "- Temp volume: DriveFormat = %s" driveFormat)
line (sprintf "- Temp volume case sensitivity (a.cs, then File.Exists(\"A.CS\")): %s" volumeCase)
line (sprintf "- Xake assembly version (nuget Xake 3.15.0): %s" xakeVersion)
line ""

for c in cases do
    line (sprintf "## %s: %s" c.Id c.Title)
    line ""
    line "Inputs:"
    for i in c.Inputs do line (sprintf "- %s" i)
    line ""
    line "Results:"
    line "```"
    for r in c.Lines do line r
    line "```"
    line ""

let mdPath = IOPath.Combine(__SOURCE_DIRECTORY__, "measurements-windows.md")
IOFile.WriteAllText(mdPath, md.ToString())

printfn "cases run: %d; %s" cases.Count cleanupNote
printfn "run time: %.1f s" elapsed
printfn "Wrote %s" mdPath
