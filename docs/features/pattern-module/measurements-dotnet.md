# .NET directory enumeration measurements (macOS)

Measured 2026-11-01 with `dotnet fsi`. Scripts and fixtures live outside the repo in
`/var/folders/dd/0rj9tr1d4jzg32250z27r2lw0000gn/T/tmp.J1NJ2yh1eE/` (`scripts/`, `fx/`, `out/`).
Nothing was committed.

## Environment

- macOS 27.2 (build 26B123), Darwin 27.2.0, arm64 (Apple M5 Pro)
- Temp volume: `/var/folders/...` on the boot volume, APFS, case-insensitive (default personality).
  Also tested a second volume: APFS "Case-sensitive APFS" disk image (`hdiutil`, mounted read-write,
  detached afterwards).
- .NET SDK 10.0.403 (`dotnet fsi`, net10.0 runtime)
- Running as uid 501 (not root), so `chmod 000` blocks listing.

## Case 1: search pattern semantics (EnumerateFiles, default overload)

Code: `scripts/c1.fsx` calls `Directory.EnumerateFiles(dir, p)` for each pattern (legacy overload).
Fixture names: `a.cs a.csx A.CS b .hidden.cs x[1].cs x{1}.cs (x).cs`.

Output (case-insensitive APFS):
```
NOTE: creating A.CS, but a case-variant already exists (name collision)
on disk: [|"(x).cs"; ".hidden.cs"; "a.cs"; "a.csx"; "b"; "x[1].cs"; "x{1}.cs"|]
*.cs      -> ["(x).cs"; "x{1}.cs"; "a.cs"; ".hidden.cs"; "x[1].cs"]
*.c?      -> ["(x).cs"; "x{1}.cs"; "a.cs"; ".hidden.cs"; "x[1].cs"]
?.cs      -> ["a.cs"]
*.*       -> ["(x).cs"; "x{1}.cs"; "a.cs"; "a.csx"; ".hidden.cs"; "x[1].cs"; "b"]
*         -> ["(x).cs"; "x{1}.cs"; "a.cs"; "a.csx"; ".hidden.cs"; "x[1].cs"; "b"]
x[1].cs   -> ["x[1].cs"]
[         -> []
(x).cs    -> ["(x).cs"]
"         -> []
<         -> []
>         -> []
a.cs      -> ["a.cs"]
```

Probe (`scripts/c1probe.fsx`, files `b q.c " < > a<b>.cs a"b.cs`), explicit MatchType:
```
MatchType.Win32:  b? -> ["b"]   b* -> ["b"]   ?  -> [">"; """; "b"; "<"]   "  -> ["""]
                  <  -> ["<"]   >  -> [">"]   *"* -> ["""; "a"b.cs"]   a?b?.cs -> ["a<b>.cs"; "a"b.cs"]
                  q.c? -> ["q.c"]   q? -> []
MatchType.Simple: b? -> []   b* -> ["b"]   ?  -> [">"; """; "b"; "<"]   "  -> ["""]
                  q.c? -> []   a?b?.cs -> ["a<b>.cs"]
```

Conclusions:
- Matching is case-insensitive by default here (`*.cs` returns `a.cs`; the legacy overload is PlatformDefault, see case 2).
- `*.*` does NOT match `b` in Simple mode, but DOES match `b` in Win32 mode (`*.*` in Win32 is "all names").
- `?` matches exactly one character in Simple mode. In Win32 mode a trailing `?` matched zero characters
  (`b?` matched `b`, `q.c?` matched `q.c`) on macOS.
- `[`, `(`, `{` are literal. `x[1].cs` matched only the literal name `x[1].cs`, so brackets are not classes.
- `"`, `<`, `>` as whole patterns matched only a file literally named `"`, `<`, `>`. In Win32 mode `?` matched
  those characters as ordinary characters.
- On the case-insensitive volume the `A.CS` fixture could not coexist with `a.cs`: creating `A.CS` silently
  reused the `a.cs` entry (its content was overwritten). The fixture therefore has no `A.CS`.

## Case 2: EnumerationOptions MatchCasing x MatchType

Code: `scripts/c2.fsx`, `EnumerationOptions(MatchCasing = cas, MatchType = typ)`.
Fixture: `a.cs a.csx b .hidden.cs x[1].cs x{1}.cs (x).cs` (no A.CS). Full table in `out/c123.txt`.
Key rows (case-insensitive APFS):
```
== CS/Win32   *.CS -> []     A.CS -> []     *.* -> [(x).cs x{1}.cs a.cs a.csx x[1].cs b]
== CS/Simple  *.CS -> []     *.* -> [(x).cs x{1}.cs a.cs a.csx x[1].cs]     *.cs -> [(x).cs x{1}.cs a.cs x[1].cs]
== CI/Win32   *.CS -> [(x).cs x{1}.cs a.cs x[1].cs]    A.CS -> [a.cs]    a.CS -> [a.cs]
== CI/Simple  *.CS -> [(x).cs x{1}.cs a.cs x[1].cs]    *.* -> [(x).cs x{1}.cs a.cs a.csx x[1].cs]
== PD/Win32   *.CS -> [(x).cs x{1}.cs a.cs x[1].cs]    A.CS -> [a.cs]
== PD/Simple  *.CS -> [(x).cs x{1}.cs a.cs x[1].cs]    A.CS -> [a.cs]
```
Same table on the case-sensitive APFS volume (`out/cs.txt`, `out/cs_pd.txt`): CS modes still give `[]` for
`*.CS`, and PD modes still match `a.cs` for `*.CS`.

Conclusions:
- `MatchCasing.CaseSensitive` makes pattern letter case count; `CaseInsensitive` and `PlatformDefault` fold case.
- On macOS `PlatformDefault` folds case even on the case-sensitive APFS volume (see `*.CS` -> `a.cs`).
- Win32 vs Simple: only `*.*` (matches dotless `b`) and trailing-`?`-zero-chars differ; `.hidden.cs` is excluded
  in all modes because of default `AttributesToSkip` (case 6).

## Case 3: is the volume case-insensitive?

Code: `scripts/c3.fsx`, fixture holds only `a.cs`.

Output (case-insensitive APFS):
```
File.Exists(A.CS)            = true
File.Exists(a.cs)            = true
Directory.GetFiles(dir,A.CS) = [|"a.cs"|]
Directory.GetFiles(dir,*.CS) = [|"a.cs"|]
EnumerateFiles(dir,A.CS,CI/Win32)  = ["a.cs"]
EnumerateFiles(dir,A.CS,CS)        = []
FileInfo(A.CS).Name          = A.CS
FileInfo(A.CS).Exists        = true
File.ReadAllText(A.CS)       = a
```
Output (case-sensitive APFS, same script):
```
File.Exists(A.CS)            = false
File.Exists(a.cs)            = true
Directory.GetFiles(dir,A.CS) = [|"a.cs"|]        <- still returns on-disk spelling
FileInfo(A.CS).Name          = A.CS
FileInfo(A.CS).Exists        = false
File.ReadAllText(A.CS)       -> FileNotFoundException
```

Conclusions:
- Default APFS is case-insensitive: `File.Exists` and `File.ReadAllText` accept the other spelling.
- `Directory.GetFiles(dir, "A.CS")` returns the on-disk spelling `a.cs` (legacy overload = PlatformDefault), on both
  volumes. `FileInfo(...).Name` keeps the requested spelling and is not the on-disk name.
- On the case-sensitive volume the legacy `GetFiles` still matches case-insensitively, so a scanner using it cannot
  tell `a.cs` and `A.CS` apart by pattern alone.

## Case 4: symlinks, cycles, attributes

Fixture (`ls -la`): `real/x.cs`; `link -> real` (dir); `y.cs -> real/x.cs` (file); `loop -> .` (cycle).
Code: `scripts/c4.fsx`, `scripts/c4probe.fsx`. Counts capped at `Seq.truncate 10000`.

Output:
```
A. Directory.EnumerateFiles(root,*.cs,AllDirectories)
  count=95  exception=none    first: y.cs, real/x.cs, link/x.cs, loop/y.cs, loop/real/x.cs, ...
  max path depth-segments: 33
B. EnumerationOptions{RecurseSubdirectories=true} (default AttributesToSkip)   count=95, exception=none (same list)
C. same, AttributesToSkip=ReparsePoint   count=1  first: real/x.cs
D. same, AttributesToSkip=None           count=95 (same as B)
E. EnumerateFileSystemEntries(root,*,AllDirectories)  count=191 (includes link, loop, real, y.cs at each level)
attrs link  Attributes=Directory, ReparsePoint  LinkTarget="real"
attrs y.cs  Attributes=ReparsePoint             LinkTarget="real/x.cs"
attrs loop  Attributes=Directory, ReparsePoint  LinkTarget="."
```
Deep-loop probe (direct enumeration of `root/loop/loop/...`):
```
x1  count=1  exception=none
x31 count=1  exception=none
x32 count=0  exception=System.IO.IOException: Too many levels of symbolic links
x33 count=0  exception=System.IO.IOException: Too many levels of symbolic links
```

Conclusions:
- Recursion follows directory symlinks by default (`link/x.cs`, `loop/...`). File symlinks are listed (`y.cs`).
- The cycle terminates at the OS 32-symlink resolution limit (max depth 33 segments). The recursive enumeration
  swallowed the `IOException` silently (default IgnoreInaccessible=true, see case 8). A direct call on the same
  path throws `IOException` (ELOOP).
- `FileAttributes.ReparsePoint` is set on macOS for all symlinks; `FileSystemInfo.LinkTarget` works on this SDK.
- `AttributesToSkip = ReparsePoint` skips directory symlinks (no recursion) and file symlinks: only `real/x.cs` remains.
- The default `Enumerate*` overloads do not skip links; a scanner must set `AttributesToSkip` itself if it wants
  to ignore links.

## Case 5: ordering

Code: `scripts/c5.fsx`, creates `c a b B a2 a10` in that order (on the case-insensitive volume `B` collides with `b`,
so `B` was not created; see the case-sensitive run). Two calls per run, two runs, plus `ls -f`.

Output (case-insensitive APFS, runs 1 and 2 identical):
```
call 1: ["a2"; "a10"; "a"; "c"; "b"]
call 2: ["a2"; "a10"; "a"; "c"; "b"]
Directory.GetFiles: [|"a2"; "a10"; "a"; "c"; "b"|]
ls -f:  a2 a10 a c b        (readdir order)
```
Output (case-sensitive APFS, with `B` created):
```
call 1: ["a2"; "a10"; "B"; "a"; "c"; "b"]
ls -f:  a2 a10 B a c b
```

Conclusion: order is neither alphabetical nor creation order. It equals the raw `readdir` order of APFS
(hash-based) and is stable across calls and runs, but it is not a contract. Sort explicitly if a deterministic
order matters.

## Case 6: hidden files

Code: `scripts/c6.fsx`. Fixture `.hidden.cs a.cs b`.

Output:
```
EnumerateFiles(dir,*) legacy overload : ["a.cs"; ".hidden.cs"; "b"]
default EnumerationOptions.AttributesToSkip = Hidden, System, RecurseSubdirectories=false, IgnoreInaccessible=true
EnumerateFiles(dir,*,default options)  : ["a.cs"; "b"]
EnumerateFiles(dir,*,AttributesToSkip=None): ["a.cs"; ".hidden.cs"; "b"]
EnumerateFiles(dir,.hidden.cs,default) : []
Attributes .hidden.cs = Hidden
Attributes a.cs       = Normal
```
Conclusion: the legacy overloads return dotfiles; `EnumerationOptions()` skips them because .NET reports a
leading-dot name on macOS as `FileAttributes.Hidden`. (`ls -laO` shows no `hidden` BSD flag on the dotfile, so the
attribute is name-derived.)

## Case 7: Unicode normalization

Code: `scripts/c7.fsx`. Creates `café.cs` (NFC) then `café.cs` (NFD), each with `File.Create`.

Output:
```
after NFC create: 1 entries
after NFD create: 1 entries
  on-disk name codepoints: 0063 0061 0066 00E9 002E 0063 0073   (NFC)
EnumerateFiles(dir,caf*)     = [NFC name]
EnumerateFiles(dir,nfc name) = 1
EnumerateFiles(dir,nfd name) = 0
File.Exists(NFC path) = true
File.Exists(NFD path) = true
```
Conclusion: one file on disk, keeping the first (NFC) spelling. Lookup is normalization-insensitive (`File.Exists`
finds it with either form), but enumeration pattern matching is not normalized (the NFD pattern matches nothing).
A scanner must normalize names itself if it compares strings.

## Case 8: unreadable directory

Code: `scripts/c8.fsx`. Fixture: `ok/f.cs`, `locked/g.cs`, `locked/sub/h.cs`; `chmod 000 locked`, restored to 755.

Output:
```
A. Directory.EnumerateFiles(root,*,AllDirectories) -> count=0 exception=UnauthorizedAccessException
B. Directory.EnumerateFiles(root,*,EnumerationOptions{Recurse,IgnoreInaccessible=true}) -> count=1 exception=none
C. ... IgnoreInaccessible=false -> count=0 exception=UnauthorizedAccessException
D. Directory.GetFiles(root,*,AllDirectories) -> UnauthorizedAccessException
E. Directory.EnumerateFiles(locked,*) -> UnauthorizedAccessException
```
Conclusion: the legacy AllDirectories overload throws `UnauthorizedAccessException` (at enumeration time, not at
the call). With `IgnoreInaccessible=true` the locked subtree is skipped silently (`ok/f.cs` still returned). Note
the exception can arrive after partial results, so a scanner must not assume all-or-nothing.

## Not measured (Windows)

Items below are not measurable on this machine (macOS). The documentation text is quoted and marked UNVERIFIED.

- Win32 `?` at end of name: macOS managed emulation matched zero characters (`b?` -> `b`). The Windows kernel
  behaviour is not measured here. Docs (MatchType.Win32): "`file.??t` will match `file.t`, `file.at`, and `file.txt`.
  `?` matches collapse to periods." UNVERIFIED.
- `*.*` matching names without a dot: macOS Win32 mode returned `b`, which agrees with docs ("`*.*` matches all
  files" in Win32 mode). Recorded as macOS measurement only. UNVERIFIED for Windows.
- `"`, `<`, `>` DOS mapping: docs say they are wildcards in Win32 mode ("'*', '?', '<', '>', and '"' are all
  considered wildcards"). On macOS, `"`, `<`, `>` as patterns matched only literal names, and `?` matched them as
  ordinary characters. Not reproduced here; the Windows mapping is UNVERIFIED.
- Junctions (Windows directory reparse points): not measurable. Docs on `AttributesToSkip` only state the default
  `FileAttributes.Hidden | FileAttributes.System` (verified text from learn.microsoft.com, net-10.0). Whether junctions
  are followed on Windows is UNVERIFIED.
- Windows default case handling and 8.3 short names: not measured.

## Digest of conclusions

1. Pattern: `*`, `?`, `*.*` (Win32 matches dotless names), `[ ( { " < >` literal; Win32 `?` matched zero chars on macOS.
2. Options: CaseSensitive folds nothing; PlatformDefault and CaseInsensitive fold case, even on a case-sensitive volume.
3. Volume: default APFS is case-insensitive; `File.Exists` finds `A.CS`; `GetFiles` returns the on-disk spelling `a.cs`.
4. Symlinks: default recursion follows directory links, cycle stops at the 32-link ELOOP silently; ReparsePoint set; `AttributesToSkip = ReparsePoint` skips them.
5. Order: APFS readdir order, neither alphabetical nor creation order; stable across runs.
6. Hidden: legacy overloads include dotfiles; default EnumerationOptions skip them (Hidden attribute).
7. Unicode: one file (NFC kept); lookup normalization-insensitive; pattern matching is not.
8. Unreadable: legacy AllDirectories throws UnauthorizedAccessException; IgnoreInaccessible=true skips silently.
