module ``Fileset type``

open System
open System.IO

open Xake
open Xake.Fileset

open NUnit.Framework

// Fileset scanning on the pattern module (Phase 4b; spec sections 3 and 6, D9, D12, D13, D17,
// D25, D39, D41). The trees are real temporary folders; case tests depend on the mode
// (ignore-case on Windows, ordinal elsewhere, D11), never on how the volume stores case (D25).

module private tree =

    /// A fresh temporary folder holding `files` (relative, '/'), each with empty content.
    let make (files: string list) =
        let root = Path.Combine (Path.GetTempPath (), "xake-fileset-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory root |> ignore
        for f in files do
            let full = Path.Combine (root, f)
            Directory.CreateDirectory (Path.GetDirectoryName full) |> ignore
            File.WriteAllText (full, "")
        root

    let relative root (files: Xake.File list) =
        let full = (DirectoryInfo root).FullName
        files |> List.map (fun f -> (File.getFullName f).Substring(full.Length).TrimStart('/', '\\').Replace('\\', '/'))

    /// The files `fs` lists under `root`, relative and '/'-separated, in result order.
    let ordered root (fs: Fileset) =
        let (Filelist files) = fs |> toFileList root
        relative root files

    /// The files `fs` lists under `root`, relative and '/'-separated, sorted.
    let list root (fs: Fileset) = ordered root fs |> List.sort

    let edgeTree = [
        "a/b.cs"; "a/.git/x.cs"; ".hidden/y.cs"; "a/b/.vs/deep/z.cs"; "top.cs"
        "dir/c.cs"; "dir/sub/d.cs"; "Dir2/e.cs"; "a.b/f.cs" ]

    let reports = [
        "rpt/a.rdl"; "rpt/b.rdl"; "rpt/c.rdlx"; "rpt/c1.rdlx"
        "rpt/nested/d.rdl"; "rpt/nested/nested2/d.rdl"; "rpt/nested/nested2/e.rdl"; "rpt/nested/nested2/f.rdlx"
        "bak/desert.css"; "bak/bubbleberry.css"
        "jparsec/src/main/EofParser.java"; "jparsec/src/main/Sequence2Parser.java" ]

    let withTree files f =
        let root = make files
        try f root finally Directory.Delete (root, true)

open tree

// --- listing ---------------------------------------------------------------------------------

[<Test>]
let ``lists the files of a folder by wildcard``() =
    withTree reports <| fun root ->
        Assert.That (ls "rpt/a*" |> list root, Is.EqualTo ["rpt/a.rdl"])

[<Test>]
let ``the module decides every name: '*.rdl' does not find '.rdlx' on any OS``() =
    // 3.x on .NET Framework followed the Win32 3-letter extension rule (spec 3 item 16)
    withTree reports <| fun root ->
        Assert.That (ls "rpt/*.rdl" |> list root, Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"])

[<Test>]
let ``'*.*' needs a dot in the name``() =
    // spec 3 item 15: 3.x listed dotless names too (OS-listing rule)
    withTree [ "x/a.cs"; "x/b"; "x/.c" ] <| fun root ->
        Assert.That (ls "x/*.*" |> list root, Is.EqualTo ["x/.c"; "x/a.cs"])
        Assert.That (ls "x/*" |> list root, Is.EqualTo ["x/.c"; "x/a.cs"; "x/b"])

[<Test>]
let ``searches recursively with '**'``() =
    withTree reports <| fun root ->
        Assert.That (ls "rpt/**/e.rdl" |> list root, Is.EqualTo ["rpt/nested/nested2/e.rdl"])
        Assert.That (ls "rpt/**/d.rdl" |> list root, Is.EqualTo ["rpt/nested/d.rdl"; "rpt/nested/nested2/d.rdl"])

[<Test>]
let ``'**' takes zero or more folders``() =
    withTree reports <| fun root ->
        Assert.That (ls "rpt/**/*.rdl" |> list root,
                     Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"; "rpt/nested/d.rdl"; "rpt/nested/nested2/d.rdl"; "rpt/nested/nested2/e.rdl"])

[<Test>]
let ``a mask ending in '**' names every file below the folder``() =
    // spec 3 item 14 (D12): 3.x listed the folders below
    withTree reports <| fun root ->
        Assert.That (ls "rpt/nested/**" |> list root,
                     Is.EqualTo ["rpt/nested/d.rdl"; "rpt/nested/nested2/d.rdl"; "rpt/nested/nested2/e.rdl"; "rpt/nested/nested2/f.rdlx"])

[<Test>]
let ``a trailing '/' lists folders``() =
    withTree reports <| fun root ->
        Assert.That (ls "*/" |> list root, Is.EqualTo ["bak"; "jparsec"; "rpt"])
        Assert.That (ls "rpt/**/" |> list root, Is.EqualTo ["rpt"; "rpt/nested"; "rpt/nested/nested2"])

[<Test>]
let ``braces and classes are wildcards``() =
    withTree reports <| fun root ->
        Assert.That (ls "rpt/{a,b}.rdl" |> list root, Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"])
        Assert.That (ls "rpt/[ab].rdl" |> list root, Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"])
        Assert.That (ls "{bak,jparsec/src/main}/*" |> list root,
                     Is.EqualTo ["bak/bubbleberry.css"; "bak/desert.css"; "jparsec/src/main/EofParser.java"; "jparsec/src/main/Sequence2Parser.java"])

[<Test>]
let ``an explicit name is listed whether or not the file exists``() =
    // D13: what lets `needFiles (!! "out/x.dll")` build a missing file
    withTree reports <| fun root ->
        Assert.That (ls "rpt/aaa.rdl" |> list root, Is.EqualTo ["rpt/aaa.rdl"])
        Assert.That (fileset { basedir (root </> "rpt"); includes "*.rdl"; includes "NonExisting.rdl" } |> list (root </> "rpt"),
                     Is.EqualTo ["NonExisting.rdl"; "a.rdl"; "b.rdl"])

[<Test>]
let ``a literal folder before a wildcard is probed: a missing one gives nothing``() =
    withTree reports <| fun root ->
        Assert.That (ls "missing/*.rdl" |> list root, Is.Empty)

[<Test>]
let ``a missing base folder gives an empty list``() =
    withTree reports <| fun root ->
        Assert.That (ls "*.rdl" |> list (root </> "nowhere"), Is.Empty)

// --- order and duplicates (spec 6.6) ---------------------------------------------------------

[<Test>]
let ``removes duplicates, keeping the first``() =
    // spec 3 item 27 (C5): 3.x kept both
    withTree reports <| fun root ->
        Assert.That (fileset { includes "rpt/*.rdl"; includes "rpt/*.rdl*" } |> ordered root,
                     Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"; "rpt/c.rdlx"; "rpt/c1.rdlx"])

[<Test>]
let ``ordinal order per folder, files before subfolders, includes in declaration order``() =
    withTree [ "s/b.cs"; "s/B2.cs"; "s/a.cs"; "s/a/z.cs"; "s/Z/y.cs"; "t/k.cs" ] <| fun root ->
        Assert.That ((!! "t/*.cs" + "s/**/*.cs") |> ordered root,
                     Is.EqualTo ["t/k.cs"; "s/B2.cs"; "s/a.cs"; "s/b.cs"; "s/Z/y.cs"; "s/a/z.cs"])

[<Test>]
let ``the result is the same on every evaluation``() =
    withTree edgeTree <| fun root ->
        let fs = !! "**/*.cs" -- "**/.*/" -- "dir/**"
        let first = fs |> ordered root
        for _ in 1 .. 3 do
            Assert.That (fs |> ordered root, Is.EqualTo first)
        Assert.That (first, Is.EqualTo ["top.cs"; "Dir2/e.cs"; "a/b.cs"; "a.b/f.cs"])

// --- excludes --------------------------------------------------------------------------------

[<Test>]
let ``excludes files by mask``() =
    withTree reports <| fun root ->
        Assert.That ((!! "rpt/*.rdl*" -- "rpt/*.rdlx") |> list root, Is.EqualTo ["rpt/a.rdl"; "rpt/b.rdl"])

[<Test>]
let ``'**/.*/**/*' excludes files below any dot folder and nothing else``() =
    withTree edgeTree <| fun root ->
        Assert.That (
            (!! "**/*.cs" -- "**/.*/**/*") |> list root,
            Is.EqualTo ["Dir2/e.cs"; "a.b/f.cs"; "a/b.cs"; "dir/c.cs"; "dir/sub/d.cs"; "top.cs"])

[<Test>]
let ``an exclude applies to the includes before it``() =
    withTree edgeTree <| fun root ->
        // an include after the exclude brings the dot-folder files back
        Assert.That ((!! "**/*.cs" -- "**/.*/**/*" + ".hidden/*.cs") |> list root, Does.Contain ".hidden/y.cs")
        Assert.That ((!! "**/*.cs" -- "**/.*/" + ".hidden/*.cs") |> list root, Does.Contain ".hidden/y.cs")

[<Test>]
let ``'dir/**', 'dir/**/*' and 'dir/' exclude every file below dir``() =
    // spec 3 items 14 and 23 (D12, D32): in 3.x 'dir/**' and 'dir/' excluded nothing
    withTree edgeTree <| fun root ->
        let all = (!! "**/*.cs") |> list root
        let expected = all |> List.filter (fun f -> not (f.StartsWith "dir/"))
        for mask in [ "dir/**/*"; "dir/**"; "dir/" ] do
            Assert.That ((!! "**/*.cs" -- mask) |> list root, Is.EqualTo expected, mask)

[<Test>]
let ``'obj/' prunes the folder and everything below; 'obj' removes only a file named so``() =
    withTree [ "a.cs"; "obj/x.cs"; "obj/deep/y.cs"; "src/obj/z.cs"; "src/k.cs" ] <| fun root ->
        Assert.That ((!! "**/*.cs" -- "obj/") |> list root, Is.EqualTo ["a.cs"; "src/k.cs"; "src/obj/z.cs"])
        Assert.That ((!! "**/*.cs" -- "**/obj/") |> list root, Is.EqualTo ["a.cs"; "src/k.cs"])
        Assert.That ((!! "**/*.cs" -- "obj") |> list root, Is.EqualTo ((!! "**/*.cs") |> list root))

[<Test>]
let ``a trailing '/' is kept by '++', '--' and the builder``() =
    withTree [ "a.cs"; "obj/x.cs" ] <| fun root ->
        Assert.That ((Xake.Fileset.Empty ++ "**/*.cs" -- "obj/") |> list root, Is.EqualTo ["a.cs"])
        Assert.That (fileset { includes "**/*.cs"; excludes "obj/" } |> list root, Is.EqualTo ["a.cs"])
        Assert.That ((Xake.Fileset.Empty ++ "*/") |> list root, Is.EqualTo ["obj"])

// --- case (D11): ignore-case on Windows, ordinal elsewhere -----------------------------------

[<Test>]
let ``wildcard and exclude segments follow the OS case rules``() =
    withTree edgeTree <| fun root ->
        let excluded = (!! "**/*.cs" -- "d*/**/*") |> list root
        // "dir" always goes; "Dir2" goes only where masks ignore case (Windows)
        Assert.That (excluded, Does.Not.Contain "dir/c.cs")
        if Env.isUnix then Assert.That (excluded, Does.Contain "Dir2/e.cs")
        else Assert.That (excluded, Does.Not.Contain "Dir2/e.cs")
        // a literal exclude segment is no longer case-insensitive on every OS (spec 3 item 25)
        let literal = (!! "**/*.cs" -- "dir2/**") |> list root
        if Env.isUnix then Assert.That (literal, Does.Contain "Dir2/e.cs")
        else Assert.That (literal, Does.Not.Contain "Dir2/e.cs")

[<Test>]
let ``a wildcard include follows the OS case rules, not the volume``() =
    // 3.x listed through .NET, case-insensitive on macOS even for '*.CS' (spec 3 item 25)
    withTree [ "x/a.cs"; "x/B.CS" ] <| fun root ->
        let found = ls "x/*.cs" |> list root
        if Env.isUnix then Assert.That (found, Is.EqualTo ["x/a.cs"])
        else Assert.That (found, Is.EqualTo ["x/B.CS"; "x/a.cs"])

[<Test>]
let ``duplicates are removed with the OS comparer``() =
    withTree [ "x/a.cs" ] <| fun root ->
        // two explicit names differing in case: one file on Windows, two names elsewhere
        let found = (!! "x/a.cs" + "x/A.cs") |> ordered root
        if Env.isUnix then Assert.That (found, Is.EqualTo ["x/a.cs"; "x/A.cs"])
        else Assert.That (found, Is.EqualTo ["x/a.cs"])

// --- symlinks (D17): not followed under '**' -------------------------------------------------

[<Test>]
let ``'**' does not enter a folder link, a one-segment pattern does``() =
    withTree [ "real/x.cs"; "top.cs" ] <| fun root ->
        try Directory.CreateSymbolicLink (root </> "link", root </> "real") |> ignore
        with e -> Assert.Ignore (sprintf "cannot create a directory symlink here: %s" e.Message)
        Assert.That (ls "**/*.cs" |> list root, Is.EqualTo ["real/x.cs"; "top.cs"])
        Assert.That (ls "*/*.cs" |> list root, Is.EqualTo ["link/x.cs"; "real/x.cs"])
        Assert.That (ls "link/**/*.cs" |> list root, Is.EqualTo ["link/x.cs"])

[<Test>]
let ``a folder link cycle ends``() =
    withTree [ "a/x.cs" ] <| fun root ->
        try Directory.CreateSymbolicLink (root </> "a" </> "loop", root) |> ignore
        with e -> Assert.Ignore (sprintf "cannot create a directory symlink here: %s" e.Message)
        Assert.That (ls "**/*.cs" |> list root, Is.EqualTo ["a/x.cs"])

// --- construction ----------------------------------------------------------------------------

[<Test>]
let ``an invalid mask fails when the fileset is built``() =
    // spec 3 items 3, 7, 35
    for mask in [ "src/(x).cs"; "a{x}.cs"; "" ] do
        Assert.Throws<PatternAst.PatternException> (fun () -> ls mask |> ignore) |> ignore
        Assert.Throws<PatternAst.PatternException> (fun () -> (Xake.Fileset.Empty -- mask) |> ignore) |> ignore

[<Test>]
let ``an escaped reserved character is a literal``() =
    withTree [ "src(x)/a.cs"; "src/b.cs" ] <| fun root ->
        Assert.That (ls @"src\(x\)/*.cs" |> list root, Is.EqualTo ["src(x)/a.cs"])
        Assert.That (ls (PatternAst.escapePath "src(x)" + "/*.cs") |> list root, Is.EqualTo ["src(x)/a.cs"])

[<Test>]
let ``provides builder computation``() =
    withTree reports <| fun root ->
        let fs = fileset {
            basedir (root </> "rpt")
            includes "*.rdl"
            includes "*.rdlx"
            includes @"..\jparsec\src\main/**/*.java"
            do! fileset {
                includes (root </> "bak" </> "*.css")
            }
        }
        let names = fs |> toFileList root |> fun (Filelist l) -> l |> List.map File.getFileName
        Assert.That (names,
                     Is.EqualTo ["a.rdl"; "b.rdl"; "c.rdlx"; "c1.rdlx"; "EofParser.java"; "Sequence2Parser.java"; "bubbleberry.css"; "desert.css"])

[<Test>]
let ``provides operators for short definition``() =
    withTree reports <| fun root ->
        let fs = ls "*.rdl" + "*.rdlx" + "../jparsec/src/main/**/*.java" @@ (root </> "rpt")
        let names = fs |> toFileList root |> fun (Filelist l) -> l |> List.map File.getFileName
        Assert.That (names, Is.EqualTo ["a.rdl"; "b.rdl"; "c.rdlx"; "c1.rdlx"; "EofParser.java"; "Sequence2Parser.java"])

[<Test>]
let ``conditional operators``() =
    withTree reports <| fun root ->
        let fs = ls "rpt/*.rdl" +? (false, "rpt/*.rdlx") -? (true, "rpt/b.rdl")
        Assert.That (fs |> list root, Is.EqualTo ["rpt/a.rdl"])

[<Test>]
let ``includefile reads one mask per line and skips blank lines``() =
    withTree reports <| fun root ->
        let masks = root </> "masks.txt"
        File.WriteAllLines (masks, [| "rpt/*.rdl"; ""; "  "; "bak/*.css" |])
        let fs = fileset { includefile (FileInfo masks); excludes "bak/desert.css" }
        Assert.That (fs |> list root, Is.EqualTo ["bak/bubbleberry.css"; "rpt/a.rdl"; "rpt/b.rdl"])

[<Test>]
let ``combine filesets with different base dirs are not implemented yet``() =
    let fs1 = fileset {
        basedir @"c:\!"
        includes @"bak/*.css"
        includesif false @"debug/*.css"
    }
    Assert.Throws<Exception> (fun () ->
        fileset {
            basedir @"c:\!\bak"
            includes @"*.rdl"
            join fs1
        } |> ignore
    ) |> ignore

[<Test>]
let ``listByMask lists one mask from a folder``() =
    withTree reports <| fun root ->
        let files = Path.parse "rpt/*.rdl" |> Fileset.listByMask root |> List.ofSeq |> List.map (fun f -> f.Replace('\\', '/'))
        Assert.That (files, Is.EqualTo [ root.Replace('\\', '/') + "/rpt/a.rdl"; root.Replace('\\', '/') + "/rpt/b.rdl" ])
        let dirs = Path.parseDir "rpt/*" |> Fileset.listByMask root |> List.ofSeq |> List.map (fun f -> f.Replace('\\', '/'))
        Assert.That (dirs, Is.EqualTo [ root.Replace('\\', '/') + "/rpt/nested" ])

// --- PathMask keeps the mask text (spec 6.1) -------------------------------------------------

[<TestCase("src/*.cs", "src/*.cs")>]
[<TestCase("obj/", "obj/")>]
[<TestCase(@"obj\", "obj/")>]
[<TestCase("a/**", "a/**")>]
[<TestCase("a/**/", "a/**/")>]
[<TestCase("/abs/x/*.cs", "/abs/x/*.cs")>]
[<TestCase("//server/share/x.cs", "//server/share/x.cs")>]
[<TestCase("c:/x/*.cs", "c:/x/*.cs")>]
[<TestCase(@"src\(x\)\*.cs", @"src\(x\)/*.cs")>]
[<TestCase("[a/b]*.c", "[a/b]*.c")>]
[<TestCase("{src,test/unit}/*.cs", "{src,test/unit}/*.cs")>]
[<TestCase("./a/../b/*.cs", "b/*.cs")>]
[<TestCase("src/*/../x.cs", "src/*/../x.cs")>]
[<TestCase("a//b", "a/b")>]
let ``Path.parse keeps the segment text`` (mask: string, text: string) =
    Assert.That (Path.maskText (Path.parse mask), Is.EqualTo text)

[<Test>]
let ``Path.parseDir names directories``() =
    Assert.That (Path.maskText (Path.parseDir "a/*"), Is.EqualTo "a/*/")
    Assert.That (Path.maskText (Path.parseDir "a/**"), Is.EqualTo "a/**/")

// --- benchmark -------------------------------------------------------------------------------

[<Test; Explicit("Benchmark: prints timings")>]
let ``benchmark: '**/*.cs' with dot-folder, bin and obj excludes``() =
    // ~1300 files in ~60 folders, three levels deep, plus two dot folders, bin and obj
    let files = [
        for a in 1 .. 4 do
            for b in 1 .. 3 do
                for c in 1 .. 5 do
                    for f in 1 .. 22 do
                        yield sprintf "Area%d/Part%d/Leaf%d/File%d.cs" a b c f
        for f in 1 .. 10 do
            yield sprintf ".git/objects/o%d.cs" f
            yield sprintf "Area1/.vs/cache/c%d.cs" f
            yield sprintf "bin/Debug/b%d.cs" f
            yield sprintf "obj/Debug/o%d.cs" f ]
    withTree files <| fun root ->
        let time name (fs: Fileset) =
            fs |> list root |> ignore // warm-up
            let sw = Diagnostics.Stopwatch.StartNew ()
            let runs = 5
            let mutable count = 0
            for _ in 1 .. runs do count <- (fs |> list root).Length
            TestContext.Progress.WriteLine (sprintf "%-40s %5d files  %8.1f ms/expansion" name count (sw.Elapsed.TotalMilliseconds / float runs))
        TestContext.Progress.WriteLine (sprintf "tree: %d files" files.Length)
        time "**/*.cs" (!! "**/*.cs")
        time "**/*.cs !**/.*/**/*" (!! "**/*.cs" -- "**/.*/**/*")
        time "**/*.cs !bin/**/* !obj/**/*" (!! "**/*.cs" -- "bin/**/*" -- "obj/**/*")
        time "**/*.cs !**/.*/ !bin/ !obj/" (!! "**/*.cs" -- "**/.*/" -- "bin/" -- "obj/")
