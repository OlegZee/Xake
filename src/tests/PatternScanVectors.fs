/// Conformance vectors for the scanner stage (PR 4) of the pattern module
/// (docs/features/pattern-module/spec.md sections 3, 5.3-5.6, 6, 7.3, 9.5, 11; decisions D1-D37 in
/// brief.md). DATA only: the tables below are driven by PatternScanVectorTests.fs. A vector is a
/// fixture tree on disk, a base directory, a fileset (includes and excludes in order), a comparer
/// mode and the expected result of the walk; the matcher of one path is PR 3 (PatternMatchVectors).
///
/// ## Tables
///
/// | table          | id prefix | what                                                             |
/// |----------------|-----------|------------------------------------------------------------------|
/// | `trees`        | `T-`      | a fixture tree, declared as a list of relative paths             |
/// | `scanVectors`  | `SC-`     | one fileset evaluated against one tree                           |
///
/// ## Trees
///
/// - An entry is a path relative to the tree root, `/`-separated: a file (created empty), a
///   directory when it ends in `/` (created empty), or a symbolic link `name -> target` (the
///   target relative to the link's directory, as `ln -s` takes it; a directory link when the
///   target is a directory of the tree, else a file link). Parent directories are implied.
/// - `Unreadable` lists directories made unreadable (`chmod 000`) after creation; Unix only.
/// - `Needs` are host conditions: a vector whose tree (or the vector itself) needs what the host
///   cannot give is ignored there, never failed (symlinks need a privilege on Windows; two names
///   that differ only in case need a case-sensitive volume; `chmod` needs Unix and a non-root
///   user; the Unicode probe vectors need a volume that does / does not normalize names, D27).
///
/// ## Vectors
///
/// - `Base` is the fileset's base directory, relative to the tree root (`At ""` = the root), or
///   `Missing` (a directory that does not exist, 6.6).
/// - `Fileset` is the ordered list of elements, as `toFileList` folds them (6.3): an include
///   appends what it finds, an exclude removes from what was found so far (only from earlier
///   includes). The text `<root>` in a mask stands for the tree root as an absolute path written
///   with `PatternAst.escapePath` (the absolute-mask vectors, D6).
/// - `Os` selects the comparer of D11 (`Unix` ordinal, `Windows` ordinal-ignore-case), passed to
///   the walker explicitly; `Any` = both modes. The comparer decides matching and duplicates; the
///   **volume** decides a literal probe (`Directory.Exists`), so a probe whose answer depends on
///   case carries a `Needs` volume condition (5.4, D25: the macOS trap).
/// - `Expect = Lists (files, warnings, log)`:
///   - `files`: the result in order, each path relative to the base directory with `/`, spelled as
///     the spec says the result is spelled: a probed literal segment keeps the mask's spelling
///     (D25), a listed name the disk's; a directory (trailing-`/` include) without a trailing `/`;
///     a path outside the base as `<root>/...`; `.` and `..` of a literal prefix resolved (6.3).
///     Order: within one include, the pre-order of the tree, names in ordinal order per
///     directory, the files of a directory before its subdirectories (6.6); includes are
///     concatenated in declaration order; a path is kept once, at its first occurrence, compared
///     with the mode's comparer (6.6, C5).
///   - `warnings`: the evidence and evaluation warnings of the scan (7.3) as
///     `(code, use, pattern)`, use = `include` | `exclude`: XP104, XP113, XP114, XP120 only, at
///     most once per (code, use, pattern) within one evaluation. Parse-time codes (XP105, XP106,
///     XP110, XP117) are the parser's vectors (PatternVectors) and are not repeated here.
///   - `log`: the build-log notes of the walk, once per directory: `LinkSkipped dir` (Message, a
///     directory symlink met under `**`, 6.5, D29) and `Unreadable dir` (Warning, 6.6), with
///     `dir` spelled like a result path.
/// - `Expect = Raises code`: a mask of the fileset does not parse; evaluating the fileset raises
///   `PatternException` with that code (the scan never sees the mask).
/// - `Note` gives the source and the 3.x result: "unchanged", or "changed in 4.0 (item N)" (spec
///   section 3 numbering) with the 3.x result and where it was measured (`measurements-engine.md`
///   = macOS 3.x engine, cases E1-E10 by section number there; `measurements-dotnet.md` = macOS
///   .NET listing; `measurements-windows.md` = Windows, D/E cases). "3.x unmeasured" marks a 3.x
///   result read from the code, not run. A note starting "SPEC?" marks a reading the spec does
///   not settle; the expected value is this file's best reading.
module PatternScanVectors

open Xake.PatternAst
open PatternMatchVectors

// =================================================================================================
// Types
// =================================================================================================

/// A host condition (see the header).
[<RequireQualifiedAccess>]
type Need =
    | Symlinks
    | CaseSensitiveVolume
    | CaseInsensitiveVolume
    | UnixPermissions
    /// the volume finds an NFD name by its NFC spelling (APFS)
    | NormalizingVolume
    /// NFC and NFD spellings are different names (ext4, NTFS)
    | NonNormalizingVolume

type Tree =
    { Id: string
      Entries: string list
      Unreadable: string list
      Needs: Need list
      Note: string }
    override t.ToString() = t.Id

/// A build-log note of the walk (6.5, 6.6); the path is a directory.
type ScanLog =
    | LinkSkipped of string
    | Unreadable of string

type Base =
    | At of string
    | Missing

type ScanExpect =
    | Lists of files: string list * warnings: (string * string * string) list * log: ScanLog list
    | Raises of code: string

type ScanVector =
    { Id: string
      Section: string
      Tree: string
      Base: Base
      Os: Os
      Needs: Need list
      Fileset: (MaskRole * string) list
      Expect: ScanExpect
      Note: string }
    override v.ToString() = v.Id

// =================================================================================================
// Trees
// =================================================================================================

let private tree id entries note = { Id = id; Entries = entries; Unreadable = []; Needs = []; Note = note }

let trees : Tree list = [
    tree "T-BASIC"
        [ "a.cs"; "b.cs"; "c.txt"; "README"; ".env"
          "src/a.cs"; "src/b.cs"; "src/c.fs"; "src/sub/d.cs"; "src/sub/deep/e.cs"
          "test/x.cs"; "test/unit/u.cs"; "test/unit/v.fs"
          "lib/x.dll"; "lib/x.exe"; "lib/x.pdb"; "lib/y.nupkg"
          "docs/a.md"; "empty/" ]
        "literals, explicit names, wildcards, classes, braces, globstars, excludes, duplicates"
    tree "T-DIRS"
        [ "x.dll"; "a/x.dll"; "dir/a"; "dir/sub/b"; "dir/sub/deep/"
          "src/hermetic/a.fs"; "src/hermetic/sub/b.fs"; "src/other.fs" ]
        "dir/** vs dir/**/* vs dir/**/ (measurements-engine §7 tree c7, example 10)"
    tree "T-OBJ"
        [ "obj/x.dll"; "obj/a/y.cs"; "obj/a.o"; "src/obj"; "src/a.cs"; "other/obj"; "other/keep.o"; "k.o" ]
        "trailing '/' in excludes (measurements-engine §6 tree c6, example 21)"
    tree "T-DOT"
        [ "a/b.cs"; "a/.git/x.cs"; ".hidden/y.cs"; "a/b/.vs/deep/z.cs"; "top.cs"
          "dir/c.cs"; "dir/sub/d.cs"; "Dir2/e.cs"; "a.b/f.cs"; ".gitignore"; "a/.env" ]
        "dotfiles and dot folders (FilesetTests edgeTree, measurements-engine §8, example 4)"
    tree "T-ORDER"
        [ "a"; "a10"; "a2"; "b"; "c"; "Z"; "_u"; "0z"; "~t"; "sub/b"; "sub/a10"; "sub/a2"; "Sub2/x" ]
        "ordinal order per directory (6.6; measurements-dotnet §5, measurements-windows §D5)"
    { tree "T-ORDERCS"
        [ "B"; "b"; "a"; "A1/x"; "a1/y" ]
        "names that differ only in case: a case-sensitive volume only (6.6, 5.4)"
      with Needs = [ Need.CaseSensitiveVolume ] }
    tree "T-CASE"
        [ "src/a.cs"; "Lib/B.cs"; "x.CS"; "y.cs" ]
        "case per comparer mode and the probe spelling (5.4, D11, D25, example 15; measurements-engine §9)"
    { tree "T-LINK"
        [ "real/x.cs"; "real/sub/z.cs"; "top.cs"; "skip/"; "link -> real"; "y.cs -> real/x.cs"; "skip/inner -> ../real" ]
        "symlinks (6.5; measurements-engine §4 tree c4)"
      with Needs = [ Need.Symlinks ] }
    { tree "T-CYCLE"
        [ "real/x.cs"; "top.cs"; "loop -> ." ]
        "a symlink cycle (6.5; measurements-engine §4 tree c4loop, measurements-windows §D4d)"
      with Needs = [ Need.Symlinks ] }
    { tree "T-LOCK"
        [ "ok/f.cs"; "locked/g.cs"; "locked/sub/h.cs"; "top.cs" ]
        "an unreadable directory (6.6; measurements-dotnet §8)"
      with Unreadable = [ "locked" ]; Needs = [ Need.UnixPermissions ] }
    tree "T-LEGACY"
        [ "[ab]"; "a"; "b"; "c"; "{a,b}"; "x[ab].cs"; "xa.cs"; "xc.cs"; "y{1,2}.cs"; "y1.cs"; "d[ab]/f.c"; "da/g.c" ]
        "entries named like 3.x literal readings of classes and braces (XP104, 7.3)"
    tree "T-ESC"
        [ "a[test.c"; "atest.c"; "b]x.c"; "bx.c"; "c{1}.c"; "c1.c"; "d(2).c"; "d2.c"
          "sub[1]/s.c"; "sub[1]/s.h"; "sub1/t.c"; "odd{dir}/q.c"; "deep/odd{dir}/o.c"; "deep/odddir/p.c" ]
        "reserved characters on disk: the corpus of the frozen prototype's EscapingDiskTests (without 'e|f.c', not a Windows name)"
    tree "T-ABS"
        [ "p/a.cs"; "p/bin/x.dll"; "p/src/b.cs"; "p/src/c.dll"; "ext/lib/a.so"; "ext/lib/libx.so"; "ext/inc/h.h"; "ext/a.cs" ]
        "absolute masks and a second tree outside the base 'p' (D6, D24, XP120, example 22)"
    tree "T-STARDOT"
        [ "README"; "a.txt"; "Makefile"; "x.y.z"; ".env"; "b"; "q.c"; "a.cs"; "a.csx"; "ab"; "noext/k.txt"; "d.d/k.txt" ]
        "'*.*' and the dropped OS-listing quirks (6.4, XP114; measurements-dotnet §1, measurements-windows §D1, §D1b)"
    tree "T-UNI"
        [ "a.txt"; "ab.txt"; "😀.txt"; "ü/x.txt"; "café/a.txt" ]
        "one character is one UTF-16 code unit (D26); no normalization (D27): 'cafe\\u0301' is the NFD spelling"
]

// =================================================================================================
// Vector builders
// =================================================================================================

let private sc tree (os: Os) id section fileset expect note : ScanVector =
    { Id = id; Section = section; Tree = tree; Base = At ""; Os = os; Needs = []
      Fileset = fileset; Expect = expect; Note = note }

let private needs (ns: Need list) (v: ScanVector) = { v with Needs = ns }
let private at (b: Base) (v: ScanVector) = { v with Base = b }

let private inc (m: string) = MaskRole.Include, m
let private exc (m: string) = MaskRole.Exclude, m
/// one include
let private i1 (m: string) = [ inc m ]
/// one include, then one exclude
let private ie (i: string) (e: string) = [ inc i; exc e ]

let private ok (files: string list) = Lists (files, [], [])
let private okw (files: string list) warnings = Lists (files, warnings, [])
let private okl (files: string list) log = Lists (files, [], log)
let private fails code = Raises code
let private wi code (m: string) = code, "include", m
let private we code (m: string) = code, "exclude", m

let private basic = sc "T-BASIC" Os.Any
let private basicU = sc "T-BASIC" Os.Unix
let private basicW = sc "T-BASIC" Os.Windows
let private dirs = sc "T-DIRS" Os.Any
let private obj = sc "T-OBJ" Os.Any
let private dot = sc "T-DOT" Os.Any
let private dotU = sc "T-DOT" Os.Unix
let private dotW = sc "T-DOT" Os.Windows
let private order = sc "T-ORDER" Os.Any
let private ordercsU = sc "T-ORDERCS" Os.Unix
let private ordercsW = sc "T-ORDERCS" Os.Windows
let private case = sc "T-CASE" Os.Any
let private caseU = sc "T-CASE" Os.Unix
let private caseW = sc "T-CASE" Os.Windows
let private link = sc "T-LINK" Os.Any
let private cycle = sc "T-CYCLE" Os.Any
let private lock = sc "T-LOCK" Os.Any
let private legacy = sc "T-LEGACY" Os.Any
let private esc = sc "T-ESC" Os.Any
let private abs id section fileset expect note = sc "T-ABS" Os.Any id section fileset expect note |> at (At "p")
let private stardot = sc "T-STARDOT" Os.Any
let private uni = sc "T-UNI" Os.Any

// frequently expected lists
let private basicCs = [ "a.cs"; "b.cs"; "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ]
let private basicAll =
    [ ".env"; "README"; "a.cs"; "b.cs"; "c.txt"; "docs/a.md"; "lib/x.dll"; "lib/x.exe"; "lib/x.pdb"; "lib/y.nupkg"
      "src/a.cs"; "src/b.cs"; "src/c.fs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs"; "test/unit/v.fs" ]
let private basicDirs = [ "docs"; "empty"; "lib"; "src"; "src/sub"; "src/sub/deep"; "test"; "test/unit" ]
let private dirsAll = [ "x.dll"; "a/x.dll"; "dir/a"; "dir/sub/b"; "src/other.fs"; "src/hermetic/a.fs"; "src/hermetic/sub/b.fs" ]
let private objAll = [ "k.o"; "obj/a.o"; "obj/x.dll"; "obj/a/y.cs"; "other/keep.o"; "other/obj"; "src/a.cs"; "src/obj" ]
let private objNoObj = [ "k.o"; "other/keep.o"; "other/obj"; "src/a.cs"; "src/obj" ]
let private dotCs =
    [ "top.cs"; ".hidden/y.cs"; "Dir2/e.cs"; "a/b.cs"; "a/.git/x.cs"; "a/b/.vs/deep/z.cs"; "a.b/f.cs"; "dir/c.cs"; "dir/sub/d.cs" ]
let private dotCsNoDot = [ "top.cs"; "Dir2/e.cs"; "a/b.cs"; "a.b/f.cs"; "dir/c.cs"; "dir/sub/d.cs" ]
let private orderFiles = [ "0z"; "Z"; "_u"; "a"; "a10"; "a2"; "b"; "c"; "~t" ]
let private linkCs = [ "top.cs"; "y.cs"; "real/x.cs"; "real/sub/z.cs" ]
let private escAll =
    [ "a[test.c"; "atest.c"; "b]x.c"; "bx.c"; "c1.c"; "c{1}.c"; "d(2).c"; "d2.c"
      "deep/odddir/p.c"; "deep/odd{dir}/o.c"; "odd{dir}/q.c"; "sub1/t.c"; "sub[1]/s.c"; "sub[1]/s.h" ]
let private legacyAll = [ "[ab]"; "a"; "b"; "c"; "x[ab].cs"; "xa.cs"; "xc.cs"; "y1.cs"; "y{1,2}.cs"; "{a,b}"; "d[ab]/f.c"; "da/g.c" ]
let private except (drop: string list) (all: string list) = all |> List.filter (fun p -> not (List.contains p drop))

// =================================================================================================
// Scan vectors
// =================================================================================================

let scanVectors : ScanVector list = [
    // ---- T-BASIC: literal segments and explicit names (6.2, D13) ------------------------------------
    basic "SC-001" "6.2" (i1 "src/a.cs") (ok [ "src/a.cs" ]) "unchanged: an explicit name, present"
    basic "SC-002" "6.2" (i1 "out/app.dll") (ok [ "out/app.dll" ])
        "unchanged: an explicit name is listed although absent (D13; FilesetTests 'explicitly specified file yields non-existing file'; measurements-engine §9 note: src/nothere.cs listed)"
    basic "SC-003" "6.2" (i1 "out/*.dll") (ok [])
        "unchanged: a mask with a wildcard lists only what exists; the literal prefix 'out' is probed (absent), nothing is listed"
    basic "SC-004" "6.2" [ inc "*.cs"; inc "out/app.dll" ] (ok [ "a.cs"; "b.cs"; "out/app.dll" ]) "unchanged; FilesetTests 'explicitly specified file yields non-existing file'"
    basic "SC-005" "6.2" (ie "out/app.dll" "out/*.dll") (ok [])
        "unchanged: an exclude removes an explicit name like any other found path"
    basic "SC-006" "6.2" (i1 "src/{a,z}.cs") (ok [ "src/a.cs" ])
        "changed in 4.0 (item 6): braces are non-literal, only existing alternatives are listed (no bash expansion); 3.x: the literal 'src/{a,z}.cs' as an explicit name (3.x unmeasured)"
    basic "SC-007" "6.2" (i1 "src") (ok [ "src" ])
        "SPEC? 6.2 lists an explicit name 'whether or not the file exists'; a directory named by a file mask is read as listed as is (3.x isExplicitRule made no existence or kind check)"
    basic "SC-008" "6.2" (i1 "empty/") (ok [ "empty" ]) "D9: an explicit directory mask, present (an empty directory)"
    basic "SC-009" "6.2" (i1 "nothere/") (ok [ "nothere" ])
        "SPEC? D13 makes no exception for a trailing '/': an explicit directory mask is read as listed although absent"
    basic "SC-010" "6.2" (i1 "src/sub/") (ok [ "src/sub" ]) "D9: an explicit directory mask"
    basic "SC-011" "6.3" (i1 "./src/*.cs") (ok [ "src/a.cs"; "src/b.cs" ])
        "D34: '.' in the literal prefix is resolved (6.3 step 1); 3.x the same (Path.parse dropped '.', measurements-engine §3)"
    basic "SC-012" "6.3" (i1 "src/../*.cs") (ok [ "a.cs"; "b.cs" ])
        "D34: '..' in the literal prefix is resolved; 3.x the same (a Parent step, measurements-engine §3)"
    basic "SC-013" "6.3" (i1 "src//*.cs") (ok [ "src/a.cs"; "src/b.cs" ]) "unchanged: '//' collapses (D24; measurements-engine §1, measurements-windows E1)"
    basic "SC-014" "6.6" (i1 "**/*.cs") (ok []) "unchanged: a missing base directory gives an empty list (6.6)" |> at Missing
    basic "SC-015" "6.6" (i1 "a.cs") (ok [ "a.cs" ])
        "SPEC? a missing base directory and an explicit name: read as listed (D13 lists it whether or not it exists; 6.6 'empty list' read as about listings)" |> at Missing

    // ---- T-BASIC: directories (trailing '/' in an include, D9) ------------------------------------
    basic "SC-016" "6.2" (i1 "*/") (ok [ "docs"; "empty"; "lib"; "src"; "test" ]) "unchanged; FilesetTests 'could search and return directories'"
    basic "SC-017" "6.2" (i1 "*/*/") (ok [ "src/sub"; "test/unit" ]) "unchanged (3.x unmeasured)"
    basic "SC-018" "6.2" (i1 "src/*/") (ok [ "src/sub" ]) "unchanged (3.x unmeasured)"
    basic "SC-019" "6.2" (i1 "**/*/") (ok basicDirs) "D9: every directory below the base, pre-order (3.x unmeasured)"
    basic "SC-020" "5.5" (i1 "src/**/") (ok [ "src"; "src/sub"; "src/sub/deep" ]) "5.5: 'a/**/' is 'a' and every directory below it (example 10)"
    basic "SC-021" "5.5" (i1 "**/") (ok basicDirs)
        "SPEC? '**/' with zero segments would name the base directory itself; read as not listed (a result names an entry below the base; 4.0 has no spelling for the base, XP018), so as '**/*/'"
    basic "SC-022" "6.2" (i1 "**/sub/") (ok [ "src/sub" ]) "D9"
    basic "SC-023" "6.2" (i1 "**/*/" @ [ exc "src/" ]) (ok [ "docs"; "empty"; "lib"; "test"; "test/unit" ])
        "D9: a directory exclude removes the found directory and everything below it"
    basic "SC-024" "6.2" (ie "*/" "src/") (ok [ "docs"; "empty"; "lib"; "test" ]) "D9"

    // ---- T-BASIC: one-segment wildcards, classes, braces (5.1, 6.3) ------------------------------------
    basic "SC-030" "5.1" (i1 "*.cs") (ok [ "a.cs"; "b.cs" ]) "unchanged"
    basic "SC-031" "5.3" (i1 "*") (ok [ ".env"; "README"; "a.cs"; "b.cs"; "c.txt" ]) "unchanged: D10, '*' matches a leading '.'; ordinal order ('.' < 'R' < 'a')"
    basic "SC-032" "5.1" (i1 "src/*.cs") (ok [ "src/a.cs"; "src/b.cs" ]) "unchanged"
    basic "SC-033" "5.1" (i1 "?.cs") (ok [ "a.cs"; "b.cs" ]) "unchanged"
    basic "SC-034" "5.1" (i1 "src/?.??") (ok [ "src/a.cs"; "src/b.cs"; "src/c.fs" ]) "unchanged"
    basic "SC-035" "5.2" (i1 "[ab].cs") (ok [ "a.cs"; "b.cs" ])
        "changed in 4.0 (item 8): a class; 3.x: the literal '[ab].cs' as an explicit name, listed although absent (3.x unmeasured); no entry '[ab].cs', so no XP104"
    basic "SC-036" "5.2" (i1 "[^a].cs") (ok [ "b.cs" ]) "changed in 4.0 (item 8): a negated class; 3.x: an explicit literal name (3.x unmeasured)"
    basic "SC-037" "5.2" (i1 "[!a].cs") (ok [ "b.cs" ])
        "changed in 4.0 (item 8): '[!a]' is a negated class in an include too (XP117 is for rules and exclude wildcard segments only, D14)"
    basic "SC-038" "5.2" (i1 "[a-c].*") (ok [ "a.cs"; "b.cs"; "c.txt" ])
        "changed in 4.0 (item 8): 3.x passed '[a-c].*' to the OS listing, where '[' is literal: nothing (measurements-dotnet §1)"
    basic "SC-039" "5.2" (i1 "src/[a-b].cs") (ok [ "src/a.cs"; "src/b.cs" ]) "changed in 4.0 (item 8)"
    basic "SC-040" "5.2" (i1 "{a,b}.cs") (ok [ "a.cs"; "b.cs" ]) "changed in 4.0 (item 6): 3.x an explicit literal name (3.x unmeasured)"
    basic "SC-041" "5.2" (i1 "lib/x.{dll,exe}") (ok [ "lib/x.dll"; "lib/x.exe" ]) "changed in 4.0 (item 6)"
    basic "SC-042" "5.2" (i1 "lib/*.{dll,nupkg}") (ok [ "lib/x.dll"; "lib/y.nupkg" ])
        "changed in 4.0 (item 6): 3.x passed the segment to the OS listing, '{' literal: nothing (measurements-dotnet §1)"
    basic "SC-043" "6.3" (i1 "{src,test/unit}/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "test/unit/u.cs" ])
        "6.3: braces holding '/' are expanded and the union is walked; changed in 4.0 (item 6)"
    basic "SC-044" "6.3" (i1 "{test/unit,src}/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "test/unit/u.cs" ])
        "SPEC? the union of brace alternatives holding '/' is read as coming out in tree order (6.6 'the include's results come out in that order'), not in alternative order"
    basic "SC-045" "6.3" (i1 "{src,test}/**/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ]) "changed in 4.0 (item 6)"
    basic "SC-046" "6.6" (i1 "{src,src/sub}/**/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs" ])
        "6.6: overlapping brace alternatives yield each path once"
    basic "SC-047" "6.3" (i1 "{a,src/a}.cs") (ok [ "a.cs"; "src/a.cs" ]) "6.3: a brace with '/' in one alternative only"
    basic "SC-048" "6.3" (i1 "{src/sub,test}/*.cs") (ok [ "src/sub/d.cs"; "test/x.cs" ]) "6.3"

    // ---- T-BASIC: globstar positions (5.5, D12) --------------------------------------------------------
    basic "SC-050" "5.5" (i1 "**") (ok basicAll) "unchanged: every file below the base, pre-order, files before subdirectories (6.6)"
    basic "SC-051" "5.5" (i1 "**/*.cs") (ok basicCs) "unchanged: zero or more directories (measurements-engine §7: the scan accepted zero)"
    basic "SC-052" "5.5" (i1 "**/*") (ok basicAll) "unchanged"
    basic "SC-053" "5.5" (i1 "src/**/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs" ]) "unchanged"
    basic "SC-054" "5.5" (i1 "src/**") (okw [ "src/a.cs"; "src/b.cs"; "src/c.fs"; "src/sub/d.cs"; "src/sub/deep/e.cs" ] [ wi "XP113" "src/**" ])
        "changed in 4.0 (item 14): every file below 'src'; 3.x listed the directories src, src/sub, src/sub/deep (measurements-engine §7, measurements-windows E7)"
    basic "SC-055" "5.5" (i1 "**/sub/*.cs") (ok [ "src/sub/d.cs" ]) "unchanged"
    basic "SC-056" "5.5" (i1 "**/deep/*") (ok [ "src/sub/deep/e.cs" ]) "unchanged"
    basic "SC-057" "5.5" (i1 "src/**/deep/*.cs") (ok [ "src/sub/deep/e.cs" ]) "unchanged"
    basic "SC-058" "5.5" (i1 "**/**/*.cs") (ok basicCs) "D34: adjacent globstars collapse; no path twice (6.6)"
    basic "SC-059" "5.1" (i1 "*/**/*.cs") (ok [ "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ])
        "D12: the one-or-more idiom, the root files are not listed (example 20)"
    basic "SC-060" "5.5" (i1 "**/*.{cs,fs}")
        (ok [ "a.cs"; "b.cs"; "src/a.cs"; "src/b.cs"; "src/c.fs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs"; "test/unit/v.fs" ])
        "changed in 4.0 (item 6)"
    basic "SC-061" "5.5" (i1 "src/a**.cs") (ok [ "src/a.cs" ]) "5.5: a segment-local '**' in a mask is '*' (XP112 is for rules only)"
    basic "SC-062" "5.5" (i1 "**/x.*") (ok [ "lib/x.dll"; "lib/x.exe"; "lib/x.pdb"; "test/x.cs" ]) "unchanged"
    basic "SC-063" "5.5" (i1 "nothere/**/*.cs") (ok []) "unchanged: the literal prefix is probed, absent"
    basic "SC-064" "5.5" (i1 "src/sub/deep/**/*.cs") (ok [ "src/sub/deep/e.cs" ]) "unchanged: zero segments at the end of the prefix"
    basic "SC-065" "5.5" (i1 "**/e.cs") (ok [ "src/sub/deep/e.cs" ]) "unchanged"
    basic "SC-066" "5.5" (i1 "nothere/**") (ok []) "7.3: XP113 needs a result; nothing found, no warning"

    // ---- T-BASIC: excludes (6.2, 6.3) ------------------------------------------------------------------
    basic "SC-070" "6.2" (ie "**/*.cs" "src/**") (okw [ "a.cs"; "b.cs"; "test/x.cs"; "test/unit/u.cs" ] [ we "XP113" "src/**" ])
        "changed in 4.0 (item 14): 3.x 'dir/**' as an exclude removed nothing (FilesetTests pins it; example 10)"
    basic "SC-071" "6.2" (ie "**/*.cs" "src/**/*") (ok [ "a.cs"; "b.cs"; "test/x.cs"; "test/unit/u.cs" ]) "unchanged (FilesetTests 'dir/**/*' excludes every file below dir)"
    basic "SC-072" "6.2" (ie "**/*.cs" "src/") (ok [ "a.cs"; "b.cs"; "test/x.cs"; "test/unit/u.cs" ])
        "changed in 4.0 (item 23): the directory is pruned; 3.x dropped the '/' and removed nothing (measurements-engine §6)"
    basic "SC-073" "6.2" (ie "**/*.cs" "**/sub/") (ok [ "a.cs"; "b.cs"; "src/a.cs"; "src/b.cs"; "test/x.cs"; "test/unit/u.cs" ]) "changed in 4.0 (item 23)"
    basic "SC-074" "5.9" (ie "**/*.cs" "*.cs") (ok [ "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ])
        "unchanged: an exclude is anchored at the base, not a floating basename (example 5)"
    basic "SC-075" "6.2" (ie "**/*.cs" "**/*.cs") (ok []) "unchanged"
    basic "SC-076" "6.2" (ie "**/*.cs" "src/a.cs") (ok [ "a.cs"; "b.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ]) "unchanged"
    basicU "SC-077" "5.4" (ie "**/*.cs" "SRC/a.cs") (ok basicCs)
        "changed in 4.0 (item 25): a literal exclude segment is ordinal on Unix; 3.x compared literal exclude segments ignoring case on every OS"
    basicW "SC-078" "5.4" (ie "**/*.cs" "SRC/a.cs") (ok [ "a.cs"; "b.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ]) "unchanged on Windows (ignore-case)"
    basic "SC-079" "6.3" [ inc "**/*.cs"; exc "src/"; inc "src/sub/*.cs" ] (ok [ "a.cs"; "b.cs"; "test/x.cs"; "test/unit/u.cs"; "src/sub/d.cs" ])
        "unchanged: an exclude applies to earlier includes only; a later include brings a path back, at its own position (FilesetTests 'keeps the order of includes and excludes')"
    basic "SC-080" "6.3" [ exc "a.cs"; inc "*.cs" ] (ok [ "a.cs"; "b.cs" ]) "unchanged: an exclude before every include removes nothing"
    basic "SC-081" "6.3" [ inc "**/*"; exc "lib/"; exc "**/*.md" ]
        (ok [ ".env"; "README"; "a.cs"; "b.cs"; "c.txt"; "src/a.cs"; "src/b.cs"; "src/c.fs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs"; "test/unit/v.fs" ])
        "6.3: two later excludes, the directory one prunes 'lib'"
    basic "SC-082" "6.2" (ie "**/*.cs" "**/deep/") (ok [ "a.cs"; "b.cs"; "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "test/x.cs"; "test/unit/u.cs" ]) "D9"
    basic "SC-083" "6.2" (ie "**/*.cs" "src/*/") (ok [ "a.cs"; "b.cs"; "src/a.cs"; "src/b.cs"; "test/x.cs"; "test/unit/u.cs" ]) "D9: a wildcard directory exclude prunes"
    basic "SC-084" "6.2" (ie "**/*.cs" "test/unit") (ok basicCs)
        "unchanged: a mask without '/' matches files only; 'test/unit' is a directory, nothing removed (item 23)"
    basic "SC-085" "6.2" (ie "**" "**/*.{fs,md}")
        (ok [ ".env"; "README"; "a.cs"; "b.cs"; "c.txt"; "lib/x.dll"; "lib/x.exe"; "lib/x.pdb"; "lib/y.nupkg"; "src/a.cs"; "src/b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ])
        "changed in 4.0 (item 6): braces in an exclude"
    basic "SC-086" "6.2" (ie "*.cs" "[ab].cs") (ok []) "changed in 4.0 (item 8): a class in an exclude; no entry '[ab].cs', no XP104"
    basic "SC-087" "6.2" (ie "*.cs" "?.cs") (ok []) "unchanged"
    basic "SC-088" "6.3" [ inc "**/*.cs"; exc "**/*.cs"; inc "a.cs" ] (ok [ "a.cs" ]) "unchanged"

    // ---- T-BASIC: duplicates (6.6, C5) --------------------------------------------------------------------
    basic "SC-090" "6.6" [ inc "*.cs"; inc "*.cs" ] (ok [ "a.cs"; "b.cs" ])
        "changed in 4.0 (item 27): duplicates removed; 3.x kept both (measurements-engine §5, measurements-windows E5; FilesetTests 'removes duplicates' asserts 2, C5)"
    basic "SC-091" "6.6" [ inc "**/*.cs"; inc "src/*.cs" ] (ok basicCs) "changed in 4.0 (item 27): 3.x listed src/a.cs, src/b.cs twice (E5)"
    basic "SC-092" "6.6" [ inc "src/*.cs"; inc "**/*.cs" ]
        (ok [ "src/a.cs"; "src/b.cs"; "a.cs"; "b.cs"; "src/sub/d.cs"; "src/sub/deep/e.cs"; "test/x.cs"; "test/unit/u.cs" ])
        "changed in 4.0 (item 27): kept at the first occurrence (E5)"
    basic "SC-093" "6.6" [ inc "src/a.cs"; inc "src/*.cs" ] (ok [ "src/a.cs"; "src/b.cs" ]) "changed in 4.0 (item 27): an explicit name and a listing"
    basic "SC-094" "6.6" [ inc "*.cs"; inc "a.cs" ] (ok [ "a.cs"; "b.cs" ]) "changed in 4.0 (item 27)"

    // ---- T-BASIC: masks that do not parse (the scan never sees them) ----------------------------------
    basic "SC-095" "7.2" (i1 "") (fails "XP018") "changed in 4.0 (item 35): 3.x listed the base directory as one entry (measurements-engine §2, measurements-windows E2)"
    basic "SC-096" "7.2" [ inc ""; inc "*.cs" ] (fails "XP018") "changed in 4.0 (item 35): 3.x [\"\"; \"x.cs\"] (measurements-engine §2)"
    basic "SC-097" "7.2" (i1 ".") (fails "XP018") "D35: '.' alone is XP018"
    basic "SC-098" "7.2" (i1 "src/*/./a.cs") (fails "XP012") "changed in 4.0 (item 21): 3.x dropped '.', the scan listed src/d/x.cs (measurements-engine §3)"
    basic "SC-099" "7.2" (i1 "src/*/../a.cs") (fails "XP012") "changed in 4.0 (item 21): 3.x applied '..' after the wildcard, the scan listed src/x.cs (measurements-engine §3)"
    basic "SC-100" "7.2" (i1 "out/(cfg:*)/*.dll") (fails "XP008") "changed in 4.0 (item 17): 3.x looked for a directory literally named '(cfg:*)' (example 18)"
    basic "SC-101" "7.2" (ie "**/*.cs" "(cfg:Debug)/**/*") (fails "XP008") "changed in 4.0 (item 17): example 18, in an exclude"
    basic "SC-102" "7.2" (i1 "src/{a/,b}") (fails "XP019") "changed in 4.0 (item 36): D29"
    basic "SC-103" "7.2" (i1 "[a/b]*.c") (fails "XP010") "changed in 4.0 (item 24): 3.x split it into the segments '[a' and 'b]*.c'"
    basic "SC-104" "7.2" (ie "**/*.cs" "") (fails "XP018") "changed in 4.0 (item 35): an empty exclude"

    // ---- T-DIRS: dir/** vs dir/**/* vs dir/**/ (5.5, D12, XP113) -------------------------------------------
    dirs "SC-110" "5.5" (i1 "dir/**") (okw [ "dir/a"; "dir/sub/b" ] [ wi "XP113" "dir/**" ])
        "changed in 4.0 (item 14): every file below 'dir'; 3.x listed the directories dir, dir/sub (measurements-engine §7 and measurements-windows E7, on a tree without dir/sub/deep)"
    dirs "SC-111" "5.5" (i1 "dir/**/*") (ok [ "dir/a"; "dir/sub/b" ]) "unchanged (measurements-engine §7, measurements-windows E7)"
    dirs "SC-112" "5.5" (i1 "dir/**/") (ok [ "dir"; "dir/sub"; "dir/sub/deep" ]) "5.5: 'dir' itself and every directory below (the warning-free spelling of the 3.x 'dir/**')"
    dirs "SC-113" "5.5" (i1 "dir/*/") (ok [ "dir/sub" ]) "unchanged"
    dirs "SC-114" "5.5" (i1 "dir/**/*/") (ok [ "dir/sub"; "dir/sub/deep" ]) "5.5: the directories strictly below 'dir'"
    dirs "SC-115" "7.3" (i1 "dir/sub/deep/**") (ok []) "7.3: XP113 needs a result; an empty directory yields none"
    dirs "SC-116" "11" (i1 "src/hermetic/**") (okw [ "src/hermetic/a.fs"; "src/hermetic/sub/b.fs" ] [ wi "XP113" "src/hermetic/**" ])
        "changed in 4.0 (item 14): example 10; 3.x the directories below src/hermetic as File values"
    dirs "SC-117" "11" (i1 "src/hermetic/**/") (ok [ "src/hermetic"; "src/hermetic/sub" ]) "example 10: no warning"
    dirs "SC-118" "5.5" (i1 "**/x.dll") (ok [ "x.dll"; "a/x.dll" ]) "unchanged (measurements-engine §7, measurements-windows E7)"
    dirs "SC-119" "5.1" (i1 "*/**/x.dll") (ok [ "a/x.dll" ]) "D12: one or more directories (example 20)"
    dirs "SC-120" "5.5" (ie "**/*" "dir/**") (okw [ "x.dll"; "a/x.dll"; "src/other.fs"; "src/hermetic/a.fs"; "src/hermetic/sub/b.fs" ] [ we "XP113" "dir/**" ])
        "changed in 4.0 (item 14): example 10; 3.x removed nothing (FilesetTests)"
    dirs "SC-121" "7.3" (ie "**/*" "dir/sub/deep/**") (ok dirsAll) "7.3: the exclude removes no file, no XP113"
    dirs "SC-122" "7.3" (ie "**/*.fs" "dir/**") (ok [ "src/other.fs"; "src/hermetic/a.fs"; "src/hermetic/sub/b.fs" ])
        "SPEC? the walker may prune 'dir' for the later 'dir/**' without reading it; read as no XP113 because no file the include would find is removed (6.3: pruning is an optimisation only, the evidence follows the unpruned result)"
    dirs "SC-123" "7.3" (ie "dir/**" "dir/sub/**") (okw [ "dir/a" ] [ wi "XP113" "dir/**"; we "XP113" "dir/sub/**" ])
        "7.3: the key is (use, text); both uses warn"
    dirs "SC-124" "5.5" (i1 "a/**") (okw [ "a/x.dll" ] [ wi "XP113" "a/**" ]) "changed in 4.0 (item 14)"
    dirs "SC-125" "5.5" (i1 "**") (ok dirsAll) "unchanged: '**' alone does not end in '/**', no XP113"
    dirs "SC-126" "6.2" (ie "dir/**/" "dir/sub/") (ok [ "dir" ]) "D9: the excluded directory and the directories below it are removed"
    dirs "SC-127" "5.5" (ie "**/*" "**/sub/**/*") (ok [ "x.dll"; "a/x.dll"; "dir/a"; "src/other.fs"; "src/hermetic/a.fs" ]) "unchanged"
    dirs "SC-128" "5.5" (i1 "**/sub/**/") (ok [ "dir/sub"; "dir/sub/deep"; "src/hermetic/sub" ]) "5.5"
    dirs "SC-129" "6.2" (i1 "**/deep/") (ok [ "dir/sub/deep" ]) "D9: an empty directory is found"
    dirs "SC-130" "6.2" (i1 "dir/sub/deep/*") (ok []) "unchanged: an empty directory has no files"

    // ---- T-OBJ: trailing '/' in an exclude (D9, D32, item 23) ---------------------------------------------
    obj "SC-140" "6.6" (i1 "**/*") (ok objAll) "unchanged set (measurements-engine §6, measurements-windows E6); changed order (item 27): ordinal, files first"
    obj "SC-141" "6.2" (ie "**/*" "obj/") (ok objNoObj)
        "changed in 4.0 (item 23, D32): example 21; 3.x read 'obj/' as 'obj' and removed nothing (measurements-engine §6, measurements-windows E6)"
    obj "SC-142" "6.2" (ie "**/*" "obj") (ok objAll) "unchanged: a file named 'obj' at the base; 'obj' is a directory (E6)"
    obj "SC-143" "6.2" (ie "**/*" "**/obj") (ok [ "k.o"; "obj/a.o"; "obj/x.dll"; "obj/a/y.cs"; "other/keep.o"; "src/a.cs" ])
        "unchanged: files named 'obj' (E6)"
    obj "SC-144" "6.2" (ie "**/*" "**/obj/") (ok objNoObj)
        "changed in 4.0 (item 23): example 21, the files named 'obj' stay; 3.x read it as '**/obj' and removed other/obj (E6)"
    obj "SC-145" "6.2" (ie "**/*" "obj/**") (okw objNoObj [ we "XP113" "obj/**" ]) "changed in 4.0 (item 14): 3.x removed nothing (E6)"
    obj "SC-146" "6.2" (ie "**/*" "obj/**/*") (ok objNoObj) "unchanged (E6)"
    obj "SC-147" "6.2" (ie "**/*" "**/obj/*") (ok [ "k.o"; "obj/a/y.cs"; "other/keep.o"; "other/obj"; "src/a.cs"; "src/obj" ])
        "unchanged: only the files directly in 'obj' (E6)"
    obj "SC-148" "6.2" (ie "**/*" "obj/*/") (ok [ "k.o"; "obj/a.o"; "obj/x.dll"; "other/keep.o"; "other/obj"; "src/a.cs"; "src/obj" ]) "D9"
    obj "SC-149" "6.2" (ie "**/*" "*/") (ok [ "k.o" ]) "D9: every directory at the base is pruned"
    obj "SC-150" "6.2" (i1 "**/obj/") (ok [ "obj" ]) "D9: 'src/obj' and 'other/obj' are files"
    obj "SC-151" "6.2" (i1 "*/") (ok [ "obj"; "other"; "src" ]) "unchanged"
    obj "SC-152" "6.2" (i1 "**/obj") (ok [ "other/obj"; "src/obj" ]) "unchanged: files only"
    obj "SC-153" "6.2" (i1 "obj") (ok [ "obj" ]) "SPEC? as SC-007: an explicit name that is a directory is listed (3.x the same, unmeasured)"
    obj "SC-154" "6.3" [ inc "**/*.o"; exc "obj/"; exc "other/" ] (ok [ "k.o" ]) "6.3: two directory excludes, both prune"
    obj "SC-155" "6.2" (ie "**/*" "{obj,other}/") (ok [ "k.o"; "src/a.cs"; "src/obj" ]) "D9: braces in a directory exclude"
    obj "SC-156" "6.2" (ie "**/*" "**/{bin,obj}/") (ok objNoObj) "D9"
    obj "SC-157" "6.2" (ie "obj/**/*" "obj/") (ok []) "D9"
    obj "SC-158" "6.3" [ inc "**/*"; exc "obj/"; inc "obj/a/*" ] (ok (objNoObj @ [ "obj/a/y.cs" ]))
        "6.3: a later include walks a directory an earlier exclude pruned"

    // ---- T-DOT: dotfiles and the '**/.*/**' idiom (5.3, D10) ----------------------------------------------
    dot "SC-160" "5.3" (i1 "**/*.cs") (ok dotCs) "unchanged: D10 (measurements-engine §8, measurements-windows E8); order changed (item 27)"
    dot "SC-161" "5.3" (ie "**/*.cs" "**/.*/**/*") (ok dotCsNoDot) "unchanged: FilesetTests pins it; example 4 (now pruned)"
    dot "SC-162" "5.3" (ie "**/*.cs" "**/.*/**") (okw dotCsNoDot [ we "XP113" "**/.*/**" ]) "changed in 4.0 (item 14): example 4; 3.x removed nothing"
    dot "SC-163" "5.3" (ie "**/*.cs" "**/.*/") (ok dotCsNoDot) "changed in 4.0 (item 23): example 4; 3.x removed only files named like a dot folder (none here)"
    dot "SC-164" "6.3" [ inc "**/*.cs"; exc "**/.*/**/*"; inc ".hidden/*.cs" ] (ok (dotCsNoDot @ [ ".hidden/y.cs" ]))
        "unchanged: FilesetTests 'keeps the order of includes and excludes'"
    dot "SC-165" "5.3" (i1 "*") (ok [ ".gitignore"; "top.cs" ]) "unchanged: D10"
    dot "SC-166" "5.3" (i1 ".*") (ok [ ".gitignore" ]) "unchanged"
    dot "SC-167" "5.3" (i1 "**/.*") (ok [ ".gitignore"; "a/.env" ]) "unchanged"
    dot "SC-168" "5.3" (i1 "*/*.cs") (ok [ ".hidden/y.cs"; "Dir2/e.cs"; "a/b.cs"; "a.b/f.cs"; "dir/c.cs" ]) "unchanged (measurements-engine §8)"
    dot "SC-169" "5.3" (i1 "**/.*/") (ok [ ".hidden"; "a/.git"; "a/b/.vs" ]) "D9, D10"
    dotU "SC-170" "5.4" (ie "**/*.cs" "d*/**/*") (ok [ "top.cs"; ".hidden/y.cs"; "Dir2/e.cs"; "a/b.cs"; "a/.git/x.cs"; "a/b/.vs/deep/z.cs"; "a.b/f.cs" ])
        "unchanged: FilesetTests 'directory masks in excludes follow the OS case rules'; example 15"
    dotW "SC-171" "5.4" (ie "**/*.cs" "d*/**/*") (ok [ "top.cs"; ".hidden/y.cs"; "a/b.cs"; "a/.git/x.cs"; "a/b/.vs/deep/z.cs"; "a.b/f.cs" ])
        "unchanged: FilesetTests (Windows removes Dir2)"
    dotU "SC-172" "5.4" (ie "**/*.cs" "dir2/**/*") (ok dotCs) "changed in 4.0 (item 25): 3.x literal exclude segments ignored case, Dir2/e.cs was removed on every OS"
    dotW "SC-173" "5.4" (ie "**/*.cs" "dir2/**/*") (ok (dotCs |> except [ "Dir2/e.cs" ])) "unchanged on Windows"
    dot "SC-174" "5.3" [ inc "**/*.cs"; exc "**/.*/**/*"; exc "dir/**/*" ] (ok [ "top.cs"; "Dir2/e.cs"; "a/b.cs"; "a.b/f.cs" ])
        "unchanged: FilesetTests 'excludes match the same after a mask was used many times'"
    dot "SC-175" "5.3" (i1 "a/.git/*") (ok [ "a/.git/x.cs" ]) "unchanged"
    dot "SC-176" "5.3" (i1 "**/.git/") (ok [ "a/.git" ]) "D9"
    dot "SC-177" "5.3" (ie "**/*" "**/.*")
        (ok [ "top.cs"; ".hidden/y.cs"; "Dir2/e.cs"; "a/b.cs"; "a/.git/x.cs"; "a/b/.vs/deep/z.cs"; "a.b/f.cs"; "dir/c.cs"; "dir/sub/d.cs" ])
        "unchanged: a file mask removes dotfiles, not the contents of dot folders"
    dot "SC-178" "5.1" (i1 "?ir/*.cs") (ok [ "dir/c.cs" ]) "unchanged"

    // ---- T-ORDER: ordinal order per directory (6.6, D17) ------------------------------------------------
    order "SC-180" "6.6" (i1 "*") (ok orderFiles)
        "changed in 4.0 (item 27): ordinal on every OS and in both modes (not ignore-case order, which would put 'Z' after 'c'); 3.x readdir order (APFS 'a2 a10 a c b', measurements-dotnet §5; NTFS name order, measurements-windows §D5)"
    order "SC-181" "6.6" (i1 "**") (ok (orderFiles @ [ "Sub2/x"; "sub/a10"; "sub/a2"; "sub/b" ])) "changed in 4.0 (item 27): files before subdirectories, 'Sub2' < 'sub'"
    order "SC-182" "6.6" (i1 "a*") (ok [ "a"; "a10"; "a2" ]) "changed in 4.0 (item 27): no natural sort"
    order "SC-183" "6.6" (i1 "*/*") (ok [ "Sub2/x"; "sub/a10"; "sub/a2"; "sub/b" ]) "changed in 4.0 (item 27)"
    order "SC-184" "6.6" [ inc "b"; inc "a*" ] (ok [ "b"; "a"; "a10"; "a2" ]) "6.6: includes are concatenated in declaration order"
    order "SC-185" "6.6" [ inc "sub/*"; inc "*" ] (ok ([ "sub/a10"; "sub/a2"; "sub/b" ] @ orderFiles)) "6.6"
    order "SC-186" "6.6" (i1 "{c,a}") (ok [ "a"; "c" ]) "6.3: a one-segment brace is matched against the listing, so listing order"
    order "SC-187" "6.6" (i1 "{sub/b,a}") (ok [ "a"; "sub/b" ]) "SPEC? as SC-044: a brace union holding '/' in tree order"
    order "SC-188" "6.6" (i1 "*/") (ok [ "Sub2"; "sub" ]) "6.6"
    order "SC-189" "6.6" (i1 "[a-c]*") (ok [ "a"; "a10"; "a2"; "b"; "c" ]) "changed in 4.0 (item 8)"
    order "SC-190" "6.6" (ie "**" "a*") (ok [ "0z"; "Z"; "_u"; "b"; "c"; "~t"; "Sub2/x"; "sub/a10"; "sub/a2"; "sub/b" ]) "unchanged set"
    order "SC-191" "5.1" (i1 "?") (ok [ "Z"; "a"; "b"; "c" ]) "unchanged set"
    order "SC-192" "6.4" (i1 "a?") (ok [ "a2" ])
        "changed in 4.0 (item 16): '?' is one character; 3.x Win32 listing also matched 'a' (a trailing '?' matched zero characters, measurements-dotnet §1, measurements-windows §D1b)"
    order "SC-193" "5.1" (i1 "a??") (ok [ "a10" ]) "unchanged"

    // ---- T-ORDERCS: names that differ only in case (case-sensitive volume) ------------------------------
    ordercsU "SC-200" "6.6" (i1 "*") (ok [ "B"; "a"; "b" ]) "changed in 4.0 (item 27): ordinal, 'B' < 'a' (3.x readdir on case-sensitive APFS 'B a b' in another order, measurements-dotnet §5)"
    ordercsW "SC-201" "6.6" (i1 "*") (ok [ "B"; "a" ])
        "SPEC? 6.6 compares duplicates with the mode's comparer: in Windows mode 'b' equals 'B' and is read as a duplicate (Xake's File equality agrees); only a case-sensitive directory can hold both"
    ordercsU "SC-202" "5.4" (i1 "b*") (ok [ "b" ]) "unchanged on Linux; changed on macOS (item 25)"
    ordercsU "SC-203" "6.6" (i1 "*/*") (ok [ "A1/x"; "a1/y" ]) "6.6"
    ordercsU "SC-204" "6.6" [ inc "B"; inc "b" ] (ok [ "B"; "b" ]) "unchanged: two explicit names, distinct under the ordinal comparer"
    ordercsU "SC-205" "5.4" (i1 "[A-Z]") (ok [ "B" ]) "changed in 4.0 (item 8)"
    ordercsW "SC-206" "5.4" (i1 "[a-z]") (ok [ "B"; "a" ]) "SPEC? as SC-201: on Windows a class follows the comparer ('[a-z]' matches 'B', 5.4), then 'b' is a duplicate of 'B'"

    // ---- T-CASE: case per mode, probe spelling (5.4, D11, D25) -------------------------------------------
    caseU "SC-210" "5.4" (i1 "*.cs") (ok [ "y.cs" ]) "changed in 4.0 on macOS (item 25): 3.x found x.CS through the case-insensitive .NET listing (measurements-dotnet §2)"
    caseW "SC-211" "5.4" (i1 "*.cs") (ok [ "x.CS"; "y.cs" ]) "unchanged on Windows"
    caseU "SC-212" "5.4" (i1 "*.CS") (ok [ "x.CS" ]) "changed in 4.0 on macOS (item 25)"
    caseW "SC-213" "5.4" (i1 "*.CS") (ok [ "x.CS"; "y.cs" ]) "unchanged on Windows"
    caseU "SC-214" "5.4" (i1 "**/*.cs") (ok [ "y.cs"; "Lib/B.cs"; "src/a.cs" ]) "changed in 4.0 on macOS (item 25): x.CS no longer listed; 'B.cs' ends in '.cs' and matches in every mode"
    caseW "SC-215" "5.4" (i1 "**/*.cs") (ok [ "x.CS"; "y.cs"; "Lib/B.cs"; "src/a.cs" ]) "unchanged on Windows"
    caseU "SC-216" "5.4" (i1 "SRC/*.cs") (ok [ "SRC/a.cs" ])
        "D25: the macOS trap; the probe of 'SRC' succeeds on a case-insensitive volume and keeps the mask's spelling; 3.x the same (measurements-engine §9, measurements-windows E9: SRC/a.cs)"
        |> needs [ Need.CaseInsensitiveVolume ]
    caseU "SC-217" "5.4" (i1 "SRC/*.cs") (ok []) "D25: on a case-sensitive volume (Linux) the probe of 'SRC' fails" |> needs [ Need.CaseSensitiveVolume ]
    caseW "SC-218" "5.4" (i1 "SRC/*.cs") (ok [ "SRC/a.cs" ]) "D25: the mask's spelling (measurements-windows E9)" |> needs [ Need.CaseInsensitiveVolume ]
    case "SC-219" "5.4" (i1 "SRC/a.cs") (ok [ "SRC/a.cs" ]) "unchanged: an explicit name, as typed (measurements-engine §9)"
    caseU "SC-220" "11" (ie "SRC/a.cs" "src/**") (ok [ "SRC/a.cs" ])
        "example 15: the trap, the ordinal exclude does not remove the probe's spelling; no XP113 (nothing removed)"
    caseW "SC-221" "11" (ie "SRC/a.cs" "src/**") (okw [] [ we "XP113" "src/**" ]) "example 15 in Windows mode: removed, and XP113"
    caseU "SC-222" "5.4" (ie "SRC/*.cs" "src/**/*") (ok [ "SRC/a.cs" ]) "D25: the trap with a probe" |> needs [ Need.CaseInsensitiveVolume ]
    caseU "SC-223" "6.6" [ inc "src/*.cs"; inc "SRC/*.cs" ] (ok [ "src/a.cs"; "SRC/a.cs" ])
        "D25, 6.6: two spellings of one file stay under the ordinal comparer (the trap)" |> needs [ Need.CaseInsensitiveVolume ]
    caseW "SC-224" "6.6" [ inc "src/*.cs"; inc "SRC/*.cs" ] (ok [ "src/a.cs" ]) "6.6: a duplicate under the Windows comparer" |> needs [ Need.CaseInsensitiveVolume ]
    caseU "SC-225" "5.4" (i1 "[a-z].cs") (ok [ "y.cs" ]) "D11: classes follow the comparer"
    caseW "SC-226" "5.4" (i1 "[a-z].cs") (ok [ "x.CS"; "y.cs" ]) "D11: on Windows '[a-z]' matches upper case too"
    caseU "SC-227" "5.4" (i1 "**/b.cs") (ok []) "D11"
    caseW "SC-228" "5.4" (i1 "**/b.cs") (ok [ "Lib/B.cs" ]) "D11: a listed name keeps the disk's spelling"
    caseU "SC-229" "5.4" (ie "**/*.cs" "lib/") (ok [ "y.cs"; "Lib/B.cs"; "src/a.cs" ]) "D11: the ordinal exclude does not match 'Lib', so Lib/B.cs stays"
    caseW "SC-230" "5.4" (ie "**/*.cs" "lib/") (ok [ "x.CS"; "y.cs"; "src/a.cs" ]) "D11: the Windows exclude prunes 'Lib'"
    caseW "SC-231" "5.4" (i1 "lib/*.cs") (ok [ "lib/B.cs" ])
        "D25: the probed segment keeps the mask's spelling, the listed name the disk's (measurements-engine §9: src/*.cs over Src/A.cs gave src/A.cs)"
        |> needs [ Need.CaseInsensitiveVolume ]
    caseU "SC-232" "5.4" (i1 "Lib/*") (ok [ "Lib/B.cs" ]) "unchanged"

    // ---- T-LINK: symlinks (6.5, D17, D29) ------------------------------------------------------------------
    link "SC-240" "6.5" (i1 "**/*.cs") (Lists (linkCs, [], [ LinkSkipped "link"; LinkSkipped "skip/inner" ]))
        "changed in 4.0 (item 26): '**' does not enter a directory link, each skipped link logged once; the file link y.cs is listed; 3.x followed the link: y.cs, real/x.cs, link/x.cs (measurements-engine §4)"
    link "SC-241" "6.5" (i1 "*/*.cs") (ok [ "link/x.cs"; "real/x.cs" ]) "unchanged: a one-segment pattern goes through a directory link (6.5; measurements-engine §4)"
    link "SC-242" "6.5" (i1 "link/*.cs") (ok [ "link/x.cs" ]) "unchanged (measurements-engine §4)"
    link "SC-243" "6.5" (i1 "link/**/*.cs") (ok [ "link/x.cs"; "link/sub/z.cs" ]) "6.5: a link reached explicitly by the literal prefix is walked"
    link "SC-244" "6.5" (i1 "*.cs") (ok [ "top.cs"; "y.cs" ]) "unchanged: a file link is a file (measurements-engine §4)"
    link "SC-245" "6.5" (i1 "*") (ok [ "top.cs"; "y.cs" ]) "unchanged: a directory link is not a file"
    link "SC-246" "6.5" (i1 "*/") (ok [ "link"; "real"; "skip" ]) "6.5: a one-segment directory pattern lists the directory link"
    link "SC-247" "6.5" (i1 "**/") (okl [ "real"; "real/sub"; "skip" ] [ LinkSkipped "link"; LinkSkipped "skip/inner" ])
        "SPEC? read as: a directory link met under '**' is neither entered nor yielded (6.3 yields a directory when the globstar applies the rest at it, which a skipped link never gets); the base itself not listed (SC-021)"
    link "SC-248" "6.3" (ie "**/*.cs" "skip/") (okl linkCs [ LinkSkipped "link" ])
        "SPEC? 6.3 calls pruning an optimisation; read as observable in the log: a pruned directory is not read, so the link inside it is not met and not logged"
    link "SC-249" "6.5" (i1 "*/*/*.cs") (ok [ "link/sub/z.cs"; "real/sub/z.cs"; "skip/inner/x.cs" ]) "6.5: one-segment patterns go through links at any position"
    link "SC-250" "6.5" (i1 "**/x.cs") (okl [ "real/x.cs" ] [ LinkSkipped "link"; LinkSkipped "skip/inner" ]) "changed in 4.0 (item 26)"
    link "SC-251" "6.5" [ inc "**/*.cs"; inc "link/*.cs" ] (Lists (linkCs @ [ "link/x.cs" ], [], [ LinkSkipped "link"; LinkSkipped "skip/inner" ]))
        "6.5: the later include reaches the link by name"
    link "SC-252" "6.5" [ inc "**/*.cs"; inc "**/*.cs" ] (Lists (linkCs, [], [ LinkSkipped "link"; LinkSkipped "skip/inner" ]))
        "D29: logged once per directory, also when two includes meet the link"

    // ---- T-CYCLE: a link cycle terminates (6.5) ----------------------------------------------------------
    cycle "SC-260" "6.5" (i1 "**/*.cs") (okl [ "top.cs"; "real/x.cs" ] [ LinkSkipped "loop" ])
        "changed in 4.0 (item 26): the cycle is not entered; 3.x macOS listed 16 entries, one more 'loop/' each, until ELOOP (measurements-engine §4); a Windows junction cycle does not end by itself (measurements-windows §D4d)"
    cycle "SC-261" "6.5" (i1 "*/*.cs") (ok [ "loop/top.cs"; "real/x.cs" ]) "6.5: one segment through the link to the root"
    cycle "SC-262" "6.5" (i1 "loop/loop/*/x.cs") (ok [ "loop/loop/real/x.cs" ]) "6.5: literal segments resolved by the OS"
    cycle "SC-263" "6.5" (i1 "**/") (okl [ "real" ] [ LinkSkipped "loop" ]) "SPEC? as SC-247"
    cycle "SC-264" "6.5" (i1 "loop/**/*.cs") (okl [ "loop/top.cs"; "loop/real/x.cs" ] [ LinkSkipped "loop/loop" ])
        "6.5: the walk starts inside the link (the literal prefix); the link met again under '**' is skipped"

    // ---- T-LOCK: an unreadable directory (6.6, item 28) ------------------------------------------------------
    lock "SC-270" "6.6" (i1 "**/*.cs") (okl [ "top.cs"; "ok/f.cs" ] [ Unreadable "locked" ])
        "changed in 4.0 (item 28): skipped with a warning; 3.x UnauthorizedAccessException ended the build (measurements-dotnet §8)"
    lock "SC-271" "6.6" (ie "**/*.cs" "locked/") (ok [ "top.cs"; "ok/f.cs" ]) "SPEC? as SC-248: a pruned unreadable directory is not read, no warning"
    lock "SC-272" "6.6" (i1 "*/*.cs") (okl [ "ok/f.cs" ] [ Unreadable "locked" ]) "changed in 4.0 (item 28)"
    lock "SC-273" "6.6" (i1 "locked/*.cs") (okl [] [ Unreadable "locked" ]) "changed in 4.0 (item 28): the probe succeeds, the listing fails"
    lock "SC-274" "6.6" (i1 "locked/g.cs") (ok [ "locked/g.cs" ]) "unchanged: an explicit name needs no listing"
    lock "SC-275" "6.6" (i1 "locked/sub/*.cs") (okl [] [ Unreadable "locked" ])
        "D39: the probe of 'locked/sub' fails for lack of search permission on 'locked': absent, and 'locked' is logged as unreadable so that the empty result is explainable"
    lock "SC-276" "6.6" [ inc "**/*.cs"; inc "*/*.cs" ] (okl [ "top.cs"; "ok/f.cs" ] [ Unreadable "locked" ]) "6.6: reported once per directory"
    lock "SC-277" "6.6" (i1 "*/") (ok [ "locked"; "ok" ]) "6.6: the parent is readable, the directory is listed"
    lock "SC-278" "6.6" (i1 "**/") (okl [ "locked"; "ok" ] [ Unreadable "locked" ])
        "SPEC? read as: the globstar yields 'locked' (zero segments at it), then fails to list it (warning)"

    // ---- T-LEGACY: XP104 evidence (7.3, items 6, 8) ---------------------------------------------------------
    legacy "SC-280" "7.3" (i1 "[ab]") (okw [ "a"; "b" ] [ wi "XP104" "[ab]" ])
        "changed in 4.0 (item 8): a class; the entry literally named '[ab]' exists: XP104; 3.x an explicit literal name, '[ab]' (3.x unmeasured)"
    legacy "SC-281" "7.3" (i1 "x[ab].cs") (okw [ "xa.cs" ] [ wi "XP104" "x[ab].cs" ]) "changed in 4.0 (item 8): 3.x 'x[ab].cs' (3.x unmeasured)"
    legacy "SC-282" "7.3" (i1 "{a,b}") (okw [ "a"; "b" ] [ wi "XP104" "{a,b}" ]) "changed in 4.0 (item 6): 3.x '{a,b}' (3.x unmeasured)"
    legacy "SC-283" "7.3" (i1 "y{1,2}.cs") (okw [ "y1.cs" ] [ wi "XP104" "y{1,2}.cs" ]) "changed in 4.0 (item 6)"
    legacy "SC-284" "7.3" (i1 "d[ab]/*.c") (okw [ "da/g.c" ] [ wi "XP104" "d[ab]/*.c" ])
        "changed in 4.0 (item 8): the listed directory 'd[ab]' is the evidence; 3.x d[ab]/f.c (3.x unmeasured)"
    legacy "SC-285" "7.3" (i1 @"\[ab\]") (ok [ "[ab]" ]) "tm 55: the escaped spelling, an explicit name, no warning"
    legacy "SC-286" "7.3" (i1 @"d\[ab\]/*.c") (ok [ "d[ab]/f.c" ]) "tm 55: no warning"
    legacy "SC-287" "7.3" (i1 "[cd]") (ok [ "c" ]) "7.3: no entry named '[cd]', no XP104"
    legacy "SC-288" "7.3" (ie "*" "[ab]") (okw [ "[ab]"; "c"; "x[ab].cs"; "xa.cs"; "xc.cs"; "y1.cs"; "y{1,2}.cs"; "{a,b}" ] [ we "XP104" "[ab]" ])
        "SPEC? XP104 for an exclude: the evidence is read as a found path whose segment is named as the literal text; 3.x removed only '[ab]' (a literal exclude segment)"
    legacy "SC-289" "7.3" (i1 "x[ab]*.cs") (ok [ "xa.cs" ])
        "SPEC? XP104 is read as comparing a whole entry name with the literal text of a segment without other wildcards; 'x[ab]*.cs' has no such reading, so no warning (3.x OS listing found 'x[ab].cs', '[' literal)"
    legacy "SC-290" "7.3" (i1 "{a,b}*") (ok [ "a"; "b" ]) "SPEC? as SC-289"
    legacy "SC-291" "5.3" (i1 "*") (ok [ "[ab]"; "a"; "b"; "c"; "x[ab].cs"; "xa.cs"; "xc.cs"; "y1.cs"; "y{1,2}.cs"; "{a,b}" ]) "unchanged set; ordinal ('[' < 'a' < '{')"
    legacy "SC-292" "7.3" (i1 "{a,c}") (ok [ "a"; "c" ]) "no entry '{a,c}', no XP104"
    legacy "SC-293" "6.6" (i1 "**") (ok legacyAll) "unchanged set"

    // ---- T-ESC: the prototype's on-disk corpus (EscapingDiskTests; tm 55, D7, D20, D22) ---------------------
    esc "SC-300" "5.2" (i1 @"a\[test.c") (ok [ "a[test.c" ]) "tm 55: an explicit name; 3.x read '\\' as a separator: the explicit 'a/[test.c' (3.x unmeasured)"
    esc "SC-301" "5.2" (i1 @"b\]x.c") (ok [ "b]x.c" ]) "tm 55"
    esc "SC-302" "5.2" (i1 @"c\{1\}.c") (ok [ "c{1}.c" ]) "tm 55"
    esc "SC-303" "5.2" (i1 @"d\(2\).c") (ok [ "d(2).c" ]) "tm 55"
    esc "SC-304" "5.2" (i1 @"a\[*.c") (ok [ "a[test.c" ]) "tm 55"
    esc "SC-305" "5.2" (i1 @"*\]*.c") (ok [ "b]x.c" ]) "tm 55"
    esc "SC-306" "5.2" (i1 @"*\{*\}.c") (ok [ "c{1}.c" ]) "tm 55"
    esc "SC-307" "5.2" (i1 @"?\(?\).c") (ok [ "d(2).c" ]) "tm 55"
    esc "SC-308" "11" (i1 @"sub\[1\]/*.c") (ok [ "sub[1]/s.c" ]) "example 6: no warning"
    esc "SC-309" "5.2" (i1 @"sub\[*/*") (ok [ "sub[1]/s.c"; "sub[1]/s.h" ]) "tm 55"
    esc "SC-310" "5.2" (i1 @"odd\{dir\}/*.c") (ok [ "odd{dir}/q.c" ]) "tm 55"
    esc "SC-311" "5.2" (i1 @"**/odd\{dir\}/*.c") (ok [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ]) "tm 55; tree order"
    esc "SC-312" "5.2" (i1 @"**/odd\{*\}/*.c") (ok [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ]) "tm 55"
    esc "SC-313" "5.2" (ie "**/*" @"d\(2\).c") (ok (escAll |> except [ "d(2).c" ])) "tm 55: in an exclude"
    esc "SC-314" "5.2" (ie "**/*" @"sub\[1\]/") (ok (escAll |> except [ "sub[1]/s.c"; "sub[1]/s.h" ])) "tm 55, D9"
    esc "SC-315" "5.2" (ie "**/*" @"**/odd\{dir\}/*.c") (ok (escAll |> except [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ])) "tm 55"
    esc "SC-316" "11" (i1 "sub[1]/*.c") (ok [ "sub1/t.c" ])
        "SPEC? no XP104 although an entry 'sub[1]' exists (items 8/9 give a one-character class XP106 at parse only); changed in 4.0 (item 9): example 6; 3.x sub[1]/s.c"
    esc "SC-317" "5.2" (i1 "d[(]2[)].c") (ok [ "d(2).c" ])
        "changed in 4.0 (item 9): one-character classes, XP106 at parse; 3.x the explicit literal 'd[(]2[)].c', absent (3.x unmeasured)"
    esc "SC-318" "5.2" (i1 "c[{]*.c") (ok [ "c{1}.c" ]) "changed in 4.0 (item 9): 3.x OS listing of 'c[{]*.c', '[' literal: nothing"
    esc "SC-319" "5.2" (i1 "a[[]test.c") (ok [ "a[test.c" ]) "changed in 4.0 (item 9): 3.x the explicit literal 'a[[]test.c'"
    esc "SC-320" "5.2" (ie "**/*" "sub[1]/**/*") (ok (escAll |> except [ "sub1/t.c" ]))
        "changed in 4.0 (item 9): XP106 at parse; 3.x removed sub[1]/s.c, sub[1]/s.h (a literal exclude segment)"
    esc "SC-321" "5.3" (i1 "**/*.c")
        (ok [ "a[test.c"; "atest.c"; "b]x.c"; "bx.c"; "c1.c"; "c{1}.c"; "d(2).c"; "d2.c"; "deep/odddir/p.c"; "deep/odd{dir}/o.c"; "odd{dir}/q.c"; "sub1/t.c"; "sub[1]/s.c" ])
        "unchanged set"
    esc "SC-322" "7.2" (i1 "a[test.c") (fails "XP002") "changed in 4.0 (item 5): 3.x the literal name (prototype: a warning)"
    esc "SC-323" "7.2" (i1 "b]x.c") (fails "XP004") "changed in 4.0 (item 5)"
    esc "SC-324" "7.2" (i1 "c{1}.c") (fails "XP005") "changed in 4.0 (item 7)"
    esc "SC-325" "7.2" (i1 "d(2).c") (fails "XP003") "changed in 4.0 (item 3)"
    esc "SC-326" "7.2" (i1 "odd{dir}/*.c") (fails "XP005") "changed in 4.0 (item 7)"
    esc "SC-327" "7.2" (ie "**/*" "c{1}.c") (fails "XP005") "changed in 4.0 (item 7): in an exclude"

    // ---- T-ABS: absolute masks (D6, D24, XP120, example 22); base 'p' ------------------------------------------
    abs "SC-330" "6.3" (i1 "<root>/ext/lib/*.so") (ok [ "<root>/ext/lib/a.so"; "<root>/ext/lib/libx.so" ]) "unchanged: an absolute include walks from its own root"
    abs "SC-331" "11" (ie "<root>/ext/lib/*.so" "<root>/ext/lib/libx.so") (ok [ "<root>/ext/lib/a.so" ])
        "example 22: below the anchor of an earlier include, no XP120"
    abs "SC-332" "11" (ie "**/*.dll" "/bin/**") (okw [ "bin/x.dll"; "src/c.dll" ] [ we "XP120" "/bin/**" ])
        "example 22: the file-system root's 'bin', removes nothing; XP120 (and no XP113: nothing removed)"
    abs "SC-333" "6.2" (ie "**/*.cs" "<root>/ext/**/*") (okw [ "a.cs"; "src/b.cs" ] [ we "XP120" "<root>/ext/**/*" ]) "D6: outside the base and every include"
    abs "SC-334" "6.2" (ie "**/*.cs" "<root>/p/src/**/*") (ok [ "a.cs" ]) "D6: an absolute exclude below the base works, no warning"
    abs "SC-335" "6.2" [ inc "**/*.cs"; inc "<root>/ext/*.cs"; exc "<root>/ext/a.cs" ] (ok [ "a.cs"; "src/b.cs" ]) "D6: below an earlier include's anchor"
    abs "SC-336" "6.3" (i1 "../ext/lib/*.so") (ok [ "<root>/ext/lib/a.so"; "<root>/ext/lib/libx.so" ]) "D34: '..' in the literal prefix (3.x the same, a Parent step)"
    abs "SC-337" "6.2" (ie "**/*.cs" "../ext/**/*") (ok [ "a.cs"; "src/b.cs" ])
        "SPEC? XP120 is defined for an absolute exclude; a relative exclude reaching outside the base with '..' is read as no warning"
    abs "SC-338" "6.2" (i1 "<root>/ext/**/") (ok [ "<root>/ext"; "<root>/ext/inc"; "<root>/ext/lib" ]) "D9"
    abs "SC-339" "6.2" (i1 "<root>/p/*.cs") (ok [ "a.cs" ]) "unchanged: an absolute mask into the base names the same files"
    abs "SC-340" "7.3" [ inc "**/*.dll"; exc "/bin/**"; exc "/bin/**" ] (okw [ "bin/x.dll"; "src/c.dll" ] [ we "XP120" "/bin/**" ]) "7.3: once per (use, text)"
    abs "SC-341" "6.2" (i1 "<root>/nothere/*.so") (ok []) "unchanged"
    abs "SC-342" "6.2" (i1 "<root>/ext/missing.so") (ok [ "<root>/ext/missing.so" ]) "unchanged: an absolute explicit name (D13)"
    abs "SC-343" "6.2" (ie "<root>/ext/lib/*.so" "<root>/ext/inc/**/*") (okw [ "<root>/ext/lib/a.so"; "<root>/ext/lib/libx.so" ] [ we "XP120" "<root>/ext/inc/**/*" ])
        "SPEC? an absolute include's 'anchor' (7.3) is read as its literal prefix, not the file-system root (else no absolute exclude could warn after an absolute include)"
    abs "SC-344" "6.3" (i1 "<root>/ext/**/*.so") (ok [ "<root>/ext/lib/a.so"; "<root>/ext/lib/libx.so" ]) "unchanged"
    abs "SC-345" "6.2" (ie "<root>/ext/**/*" "<root>/ext/lib/") (ok [ "<root>/ext/a.cs"; "<root>/ext/inc/h.h" ]) "D9"
    abs "SC-346" "6.6" [ inc "**/*"; inc "<root>/ext/a.cs" ] (ok [ "a.cs"; "bin/x.dll"; "src/b.cs"; "src/c.dll"; "<root>/ext/a.cs" ]) "6.6"
    abs "SC-347" "6.6" [ inc "*.cs"; inc "<root>/p/a.cs" ] (ok [ "a.cs" ]) "6.6: the same full path from a relative and an absolute include is a duplicate"

    // ---- T-STARDOT: '*.*' and the dropped OS-listing quirks (6.4, XP114) -----------------------------------
    stardot "SC-350" "6.4" (i1 "*.*") (okw [ ".env"; "a.cs"; "a.csx"; "a.txt"; "q.c"; "x.y.z" ] [ wi "XP114" "*.*" ])
        "changed in 4.0 (item 15): example 14; 3.x every name (Win32 '*.*', measurements-dotnet §1, measurements-windows §D1)"
    stardot "SC-351" "6.4" (ie "*" "*.*") (ok [ "Makefile"; "README"; "ab"; "b" ]) "unchanged: example 14, excludes always needed the dot"
    stardot "SC-352" "6.4" (i1 "*.*/*.txt") (okw [ "d.d/k.txt" ] [ wi "XP114" "*.*/*.txt" ]) "changed in 4.0 (item 15): a middle segment rejects 'noext'"
    stardot "SC-353" "6.4" (i1 "**/*.*") (okw [ ".env"; "a.cs"; "a.csx"; "a.txt"; "q.c"; "x.y.z"; "d.d/k.txt"; "noext/k.txt" ] [ wi "XP114" "**/*.*" ]) "changed in 4.0 (item 15)"
    stardot "SC-354" "6.4" (i1 "*.txt") (ok [ "a.txt" ]) "unchanged"
    stardot "SC-355" "6.4" (i1 "b?") (ok [])
        "changed in 4.0 (item 16): 3.x 'b' (Win32 trailing '?' matched zero characters; measurements-dotnet §1, measurements-windows §D1b)"
    stardot "SC-356" "6.4" (i1 "q.c?") (ok []) "changed in 4.0 (item 16): 3.x 'q.c' (same measurements)"
    stardot "SC-357" "6.4" (i1 "a.cs?") (ok [ "a.csx" ]) "changed in 4.0 (item 16): 3.x a.cs and a.csx (measurements-windows §D1b)"
    stardot "SC-358" "6.4" (i1 "*.cs") (ok [ "a.cs" ]) "unchanged on .NET 10 (measurements-windows §D1); on .NET Framework 3.x also listed a.csx (unmeasured)"
    stardot "SC-359" "6.4" (i1 "*.c?") (ok [ "a.cs" ]) "unchanged (measurements-windows §D1)"
    stardot "SC-360" "6.4" (i1 "?") (ok [ "b" ]) "unchanged"
    stardot "SC-361" "6.4" (i1 "a*.*") (ok [ "a.cs"; "a.csx"; "a.txt" ])
        "SPEC? XP114 is defined for the segment '*.*' only; 'a*.*' rejecting 'ab' gives no warning (3.x Win32 '.*' at the end could match nothing: 'ab' listed, unmeasured)"
    stardot "SC-362" "5.3" (i1 ".*") (ok [ ".env" ]) "unchanged"
    stardot "SC-363" "6.4" (i1 "*.") (ok []) "changed in 4.0 (item 16): a name ending in '.'; 3.x Win32 '*.' listed the dotless names (unmeasured)"
    stardot "SC-364" "7.3" (i1 "d.d/*.*") (ok [ "d.d/k.txt" ]) "7.3: XP114 needs a rejected dotless name"

    // ---- T-UNI: Unicode (D26, D27) -------------------------------------------------------------------------
    uni "SC-370" "5.4" (i1 "?.txt") (ok [ "a.txt" ]) "D26: '😀' is two UTF-16 code units (example 23)"
    uni "SC-371" "5.4" (i1 "??.txt") (ok [ "ab.txt"; "😀.txt" ]) "D26: example 23; ordinal by code unit"
    uni "SC-372" "5.4" (i1 "?/*.txt") (ok [ "ü/x.txt" ]) "D26: an NFC 'ü' is one code unit"
    uni "SC-373" "5.4" (i1 "caf*/*.txt") (ok [ "café/a.txt" ]) "D27: a listed name keeps the disk's (NFD) spelling"
    uni "SC-374" "5.4" (i1 "café*/*.txt") (ok [])
        "D27: an NFC literal in a wildcard segment does not match the NFD name on any volume (example 23; measurements-dotnet §7, measurements-windows §D7)"
    uni "SC-375" "5.4" (i1 "café/*.txt") (ok [ "café/a.txt" ])
        "D27, platform-dependent: the NFC probe finds the NFD directory on a normalizing volume (APFS) and keeps the mask's spelling (example 23)"
        |> needs [ Need.NormalizingVolume ]
    uni "SC-376" "5.4" (i1 "café/*.txt") (ok [])
        "D27, platform-dependent: NFC and NFD are different names (ext4, NTFS; measurements-windows §D7)" |> needs [ Need.NonNormalizingVolume ]
    uni "SC-377" "6.6" (i1 "**/*.txt") (ok [ "a.txt"; "ab.txt"; "😀.txt"; "café/a.txt"; "ü/x.txt" ]) "6.6: ordinal by UTF-16 code unit"
    uni "SC-378" "5.4" (i1 "ü/*") (ok [ "ü/x.txt" ]) "unchanged"
]
