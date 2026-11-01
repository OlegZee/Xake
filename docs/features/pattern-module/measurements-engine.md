# Engine measurements: current 3.x behaviour (pattern edge cases)

Observations of the current engine, not a specification. Each "3.x result" is what this run produced.

## Environment

- OS: macOS (Darwin 27.2.0), APFS default (case-insensitive; confirmed in case 9)
- .NET SDK 10.0.403, `dotnet fsi`
- Commit: d53145f (branch feature/pattern-module, clean tree)
- Library: `src/core/bin/Release/netstandard2.0/Xake.dll` (`dotnet build src/core -c Release`)
- Harness: temporary `measure.fsx` in a `mktemp -d` directory outside the repo (not kept); fixture trees created under the same directory.
- APIs: `Path.parse`, `Path.parseDir`, `Path.matches pat root file`, `Path.matchGroups pat root file` (root = ""), `Fileset.toFileList root fs`, operators `!!`, `+`, `--`.
- Scan helper: `toFileList tree fs`, entries printed relative to the tree root. `%A` is F# formatting.

## 1. `a//b` (double separator)

```fsharp
parseP "a//b"                      // Path.parse "a//b"
rule "a//b" "a/b"                  // Path.matches "a//b" "" "a/b"
grp "a//b" "a/b"                   // Path.matchGroups "a//b" "" "a/b"
mask "a//b" t (!! "a//b")          // tree t holds a/b, a/c
mask "a/b" t (!! "a/b")
```

```
parse "a//b" => PathMask [Directory "a"; FileName "b"]
matches "a//b" vs "a/b" => true
matchGroups "a//b" vs "a/b" => None
matchGroups "a//b" vs "a//b" => Some [("0", "a//b")]
scan[a//b] tree=c1 => ["a/b"]
scan[a/b] tree=c1 => ["a/b"]
```

3.x result: `Path.parse` collapses `//`, so `Path.matches` and the scan treat `a//b` as `a/b`. `matchGroups` compares the raw pattern text, so it returns None for `a/b`.

## 2. Empty pattern `""`

```fsharp
parseP ""
rule "" "aa"                       // Path.matches "" "" "aa"
rule "" ""
grp "" "aa"                        // Path.matchGroups "" "" "aa"
mask "\"\"" t (!! "")              // tree t holds a/b, x.cs
mask "\"\" + *.cs" t (!! "" + "*.cs")
```

```
parse "" => PathMask []
matches "" vs "aa" => false
matches "" vs "" => true
matchGroups "" vs "aa" => None
scan[""] tree=c2 => [""]
scan["" + *.cs] tree=c2 => [""; "x.cs"]
```

3.x result: no exception. Parses to an empty mask that matches only the empty path (the root). As an include it returns the base directory itself as one entry, so `!! ""` is not empty.

## 3. `.` and `..` after a wildcard

```fsharp
parseP "src/*/./x.cs"
rule "src/*/./x.cs" "src/d/x.cs"
grp "src/*/./x.cs" "src/d/x.cs"
rule "src/*/../x.cs" "src/x.cs"
rule "src/*/../x.cs" "src/d/x.cs"
parseP "src/*/../x.cs"
mask "src/*/./x.cs" t (!! "src/*/./x.cs")     // tree t holds src/d/x.cs, src/x.cs
mask "src/*/../x.cs" t (!! "src/*/../x.cs")
mask "src/./d/x.cs" t (!! "src/./d/x.cs")
```

```
parse "src/*/./x.cs" => PathMask [Directory "src"; DirectoryMask "*"; FileName "x.cs"]
matches "src/*/./x.cs" vs "src/d/x.cs" => true
matchGroups "src/*/./x.cs" vs "src/d/x.cs" => None
matches "src/*/../x.cs" vs "src/x.cs" => false
matches "src/*/../x.cs" vs "src/d/x.cs" => false
parse "src/*/../x.cs" => PathMask [Directory "src"; DirectoryMask "*"; Parent; FileName "x.cs"]
scan[src/*/./x.cs] tree=c3 => ["src/d/x.cs"]
scan[src/*/../x.cs] tree=c3 => ["src/x.cs"]
scan[src/./d/x.cs] tree=c3 => ["src/d/x.cs"]
```

3.x result: `.` is dropped by `Path.parse`, so `Path.matches` and the scan accept `src/*/./x.cs`, but `matchGroups` returns None (its regex keeps the literal `./`). `..` becomes a `Parent` token. `Path.matches` rejects `src/x.cs` for `src/*/../x.cs`, while the scan returns `src/x.cs`: rule and mask disagree.

## 4. Symlinks during a scan

Tree `c4`: `real/x.cs`, directory symlink `link -> real`, file symlink `y.cs -> real/x.cs`.

```fsharp
Directory.CreateSymbolicLink(IOP(t, "link"), "real")
File.CreateSymbolicLink(IOP(t, "y.cs"), IOP("real", "x.cs"))
mask "**/*.cs" t (!! "**/*.cs")
mask "*/*.cs" t (!! "*/*.cs")
mask "link/*.cs" t (!! "link/*.cs")
mask "*.cs" t (!! "*.cs")
```

```
scan[**/*.cs] tree=c4 => ["y.cs"; "real/x.cs"; "link/x.cs"]
scan[*/*.cs] tree=c4 => ["real/x.cs"; "link/x.cs"]
scan[link/*.cs] tree=c4 => ["link/x.cs"]
scan[*.cs] tree=c4 => ["y.cs"]
```

Cycle, tree `c4loop`: `real/x.cs` plus `loop -> <tree root>`, run as `Task.Wait(30 s)`:

```
cycle scan **/*.cs terminated: count=16
["real/x.cs"; "loop/real/x.cs"; "loop/loop/real/x.cs"; ...]   (16 entries, one more loop/ per entry)
max depth of loop/ segments: 17
```

3.x result: `**` and `*/` both follow the directory symlink; the file symlink is listed as a file. The cycle terminates (inside the 30 s wait; time not recorded) after 16 entries, deepest path 17 segments. The stop reason was not determined.

## 5. Duplicates

```fsharp
mask "*.cs + *.cs" t (!! "*.cs" + "*.cs")
mask "**/*.cs + src/*.cs" t (!! "**/*.cs" + "src/*.cs")
mask "src/*.cs + **/*.cs" t (!! "src/*.cs" + "**/*.cs")
```

Tree holds top.cs, src/a.cs, src/b.cs:

```
scan[*.cs + *.cs] tree=c5 => ["top.cs"; "top.cs"]
scan[**/*.cs + src/*.cs] tree=c5 => ["top.cs"; "src/a.cs"; "src/b.cs"; "src/a.cs"; "src/b.cs"]
scan[src/*.cs + **/*.cs] tree=c5 => ["src/a.cs"; "src/b.cs"; "top.cs"; "src/a.cs"; "src/b.cs"]
```

3.x result: `toFileList` does not de-duplicate; each include adds its matches again. `Fileset.fs` and `ExecCore.fs` contain no `distinct`. The test ``exec function removes duplicates from result`` (src/tests/FilesetTests.fs) asserts a count of 2 for `a.rdl`, so it confirms duplicates are kept despite its name.

## 6. Trailing `/` in an exclude

Tree `c6`: `obj/a.o`, `other/obj` (a file named obj), `other/keep.o`, `k.o`.

```fsharp
parseP "obj/"
parseP "obj"
mask "**/* -- obj/" t (!! "**/*" -- "obj/")
mask "**/* -- obj" t (!! "**/*" -- "obj")
mask "**/* (no excl)" t (!! "**/*")
mask "-- **/obj" t (!! "**/*" -- "**/obj")
mask "-- **/obj/" t (!! "**/*" -- "**/obj/")
mask "-- obj/**" t (!! "**/*" -- "obj/**")
mask "-- obj/**/*" t (!! "**/*" -- "obj/**/*")
mask "-- **/obj/*" t (!! "**/*" -- "**/obj/*")
```

```
parse "obj/" => PathMask [FileName "obj"]
parse "obj" => PathMask [FileName "obj"]
scan[**/* -- obj/] tree=c6 => ["k.o"; "obj/a.o"; "other/obj"; "other/keep.o"]
scan[**/* -- obj] tree=c6 => ["k.o"; "obj/a.o"; "other/obj"; "other/keep.o"]
scan[**/* (no excl)] tree=c6 => ["k.o"; "obj/a.o"; "other/obj"; "other/keep.o"]
scan[-- **/obj] tree=c6 => ["k.o"; "obj/a.o"; "other/keep.o"]
scan[-- **/obj/] tree=c6 => ["k.o"; "obj/a.o"; "other/keep.o"]
scan[-- obj/**] tree=c6 => ["k.o"; "obj/a.o"; "other/obj"; "other/keep.o"]
scan[-- obj/**/*] tree=c6 => ["k.o"; "other/obj"; "other/keep.o"]
scan[-- **/obj/*] tree=c6 => ["k.o"; "other/obj"; "other/keep.o"]
```

3.x result: `--` uses `Path.parse`, which drops the trailing `/`, so `-- "obj/"` equals `-- "obj"`: a file-name exclude for `<root>/obj`, which matches nothing here. Both `obj/a.o` and `other/obj` are kept. `-- "obj/**"` also excludes nothing. Removing a directory's contents needs `obj/**/*` or `**/obj/*`, which drops `obj/a.o`.

## 7. Globstar: zero vs one directory

```fsharp
rule "**/x.dll" "x.dll"
rule "**/x.dll" "a/x.dll"
grp "**/x.dll" "x.dll"
grp "**/x.dll" "a/x.dll"
```

Tree `c7`: `x.dll`, `a/x.dll`, `dir/a`, `dir/sub/b`.

```fsharp
mask "**/x.dll" t (!! "**/x.dll")
mask "dir/**" t (!! "dir/**")
mask "dir/**/*" t (!! "dir/**/*")
parseP "dir/**"
```

```
matches "**/x.dll" vs "x.dll" => true
matches "**/x.dll" vs "a/x.dll" => true
matchGroups "**/x.dll" vs "x.dll" => None
matchGroups "**/x.dll" vs "a/x.dll" => Some [("0", "a/x.dll"); ("1", "a")]
scan[**/x.dll] tree=c7 => ["x.dll"; "a/x.dll"]
scan[dir/**] tree=c7 => ["dir"; "dir/sub"]
scan[dir/**/*] tree=c7 => ["dir/a"; "dir/sub/b"]
parse "dir/**" => PathMask [Directory "dir"; Recurse]
```

3.x result: `**` matches zero directories in `Path.matches` and in the scan, but `matchGroups` does not match `x.dll` (its regex needs a `/`). As a file mask, `dir/**` returns the directories `dir` and `dir/sub`, not the files; `dir/**/*` returns the files.

## 8. Dotfiles

Tree `c8`: `.hidden/y.cs`, `vis/z.cs`.

```fsharp
mask "**/*.cs" t (!! "**/*.cs")
mask "*/*.cs" t (!! "*/*.cs")
rule "*/*.cs" ".hidden/y.cs"
```

```
scan[**/*.cs] tree=c8 => [".hidden/y.cs"; "vis/z.cs"]
scan[*/*.cs] tree=c8 => [".hidden/y.cs"; "vis/z.cs"]
matches "*/*.cs" vs ".hidden/y.cs" => true
```

3.x result: wildcards do not skip dot-prefixed directories in either the scan or `Path.matches`.

## 9. Case sensitivity (macOS)

Tree `c9a`: `Src/A.cs`. Tree `c9b`: `src/a.cs`. `Directory.Exists("<c9a>/src")` returned true, confirming a case-insensitive file system.

```fsharp
mask "src/*.cs" t9a (!! "src/*.cs")
mask "Src/*.cs" t9a (!! "Src/*.cs")
rule "src/*.cs" "Src/a.cs"
rule "Src/*.cs" "src/A.cs"
mask "SRC/a.cs (literal)" t9b (!! "SRC/a.cs")
mask "SRC/*.cs" t9b (!! "SRC/*.cs")
grp "src/*.cs" "Src/a.cs"
grp "src/*.cs" "src/a.cs"
```

```
scan[src/*.cs] tree=c9a => ["src/A.cs"]
scan[Src/*.cs] tree=c9a => ["Src/A.cs"]
matches "src/*.cs" vs "Src/a.cs" => true
matches "Src/*.cs" vs "src/A.cs" => true
scan[SRC/a.cs (literal)] tree=c9b => ["SRC/a.cs"]
scan[SRC/*.cs] tree=c9b => ["SRC/a.cs"]
matches "SRC/a.cs" vs "src/a.cs" => true
matchGroups "src/*.cs" vs "Src/a.cs" => None
matchGroups "src/*.cs" vs "src/a.cs" => Some [("0", "src/a.cs"); ("1", "a")]
```

3.x result: on macOS `Path.matches` is case-insensitive (literal comparison uses OrdinalIgnoreCase, Path.fs:257). `matchGroups` is case-sensitive (Path.fs:264 enables IgnoreCase only when not `Env.isUnix`). The scan returns the spelling from the mask (`src/A.cs`, not the on-disk `Src/A.cs`). Literal masks are returned as typed even when the on-disk name differs in case.

## 10. Group captures with `**`

```fsharp
grp "(path:bin/**)/file.o" "bin/file.o"
grp "(path:bin/**)/file.o" "bin/x/y/file.o"
rule "(path:bin/**)/file.o" "bin/x/y/file.o"
rule "bin/**/file.o" "bin/file.o"
rule "(path:bin/**/file.o)" "bin/file.o"
grp "(apl:*/*)/x" "a/b/x"
grp "(apl:*/*)/x" "a/b/c/x"
rule "(apl:*/*)/x" "a/b/x"
rule "(d:*)/x" "a/x"
mask "bin/**/file.o" t10 (!! "bin/**/file.o")   // tree holds bin/file.o, bin/x/y/file.o
```

```
matchGroups "(path:bin/**)/file.o" vs "bin/file.o" => None
matches "(path:bin/**)/file.o" vs "bin/x/y/file.o" => false
matchGroups "(path:bin/**)/file.o" vs "bin/x/y/file.o" => Some [("0", "bin/x/y/file.o"); ("1", "x/y"); ("path", "bin/x/y")]
matches "bin/**/file.o" vs "bin/file.o" => true
matches "(path:bin/**/file.o)" vs "bin/file.o" => false
matchGroups "(apl:*/*)/x" vs "a/b/x" => Some [("0", "a/b/x"); ("1", "a"); ("2", "b"); ("apl", "a/b")]
matchGroups "(apl:*/*)/x" vs "a/b/c/x" => None
matches "(apl:*/*)/x" vs "a/b/x" => EXCEPTION RegexParseException: Invalid pattern '^([^/\\]*))$' at offset 11. Too many )'s.
matches "(d:*)/x" vs "a/x" => true
scan[bin/**/file.o] tree=c10 => ["bin/file.o"; "bin/x/y/file.o"]
```

3.x result: the captured `path` has no trailing `/` (`bin/x/y`); for `bin/file.o` the group does not match at all. `Path.matches` disagrees with `matchGroups`: it returns false for `(path:bin/**)/file.o` against `bin/x/y/file.o`, which `matchGroups` captures. `Path.matches` throws a `RegexParseException` for `(apl:*/*)/x` (a named group containing `/`), while `matchGroups` captures `apl = "a/b"`.

## Notes

- Every requested case was run. The cycle case's time was not recorded, only that it finished inside a 30 s wait.
- Case 9 also showed that a literal absent file is returned: `!! "src/nothere.cs"` yields `src/nothere.cs` (no existence check for explicit entries; the code comment in `Fileset.fs` `listFiles` says so).
- Only netstandard2.0 on macOS was run; Windows and net462 were not measured.
