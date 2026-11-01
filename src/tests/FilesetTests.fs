module ``Fileset type``

open System
open System.IO

open Xake
open Xake.Fileset

open NUnit.Framework

module private impl =
    let currentDir = Path.Combine (__SOURCE_DIRECTORY__, "..")

    let tolower (s:string) = s.ToLower()

    let isAny() = Is.Not.All.Not

    let getFilesAt start f =
        let (Filelist files) = f |> toFileList start
        in files

    let getFiles = getFilesAt currentDir

    let getFiles1 vroot start f =
        let fs =
            {
            FileSystem with
                GetDisk = fun d -> vroot </> d.Substring(0,1) + "_drive"
                GetDirRoot = fun _ -> vroot
            }
        let (Filelist files) = f |> (toFileList1 fs (vroot </> start))
        in files

    let root1 = currentDir </> "testdata" </> "withdrive"

open impl

[<Test>]
let ``could list a files in folder by wildcard``() =

    let files = ls "a*" |> getFiles1 root1 @"c:\rpt"
    Assert.That (files |> List.map File.getFileName, Is.EquivalentTo (List.toSeq ["a.rdl"]))

[<Test; Platform("Win"); Explicit("Platform specific")>]
let ``follows weird DOS (on Windows platform) behavior when looking for files by '*.txt' mask``() =

    let files = ls "*.rdl" |> getFiles1 root1 @"c:\rpt"
    Assert.That (files |> List.map File.getFileName, Is.EquivalentTo (List.toSeq ["a.rdl"; "b.rdl"; "c.rdlx"; "c1.rdlx"]))

[<Test; Platform("Unix"); Explicit("Platform specific")>]
let ``does NOT follow weird DOS (on Windows platform) behavior when looking for files by '*.txt' mask``() =

    let files = ls "*.rdl" |> getFiles1 root1 @"c:\rpt"
    Assert.That (files |> List.map File.getFileName, Is.EquivalentTo (List.toSeq ["a.rdl"; "b.rdl"]))

[<Test>]
let ``exec function removes duplicates from result``() =

    Assert.That (
        fileset {includes "*.rdl"; includes "*.rdl*"}
        |> getFiles1 root1 @"c:\rpt"
        |> List.map (File.getFileName >> tolower) |> List.filter ((=) "a.rdl") |> List.length,
        Is.EqualTo (2))

[<Test>]
let ``could search recursively ('**' mask) ``() =
    Assert.That(
        ls "c:/rpt/**/e.rdl" |> getFiles1 root1 "" |> List.map File.getFullName |> List.toArray,
        Is.All.EndsWith("rpt" </> "nested" </> "nested2" </> "e.rdl").IgnoreCase)

[<Test>]
let ``allows excluding files by mask``() =

    let fileNames = (!! "*.rdl*" -- "*.rdlx") |> getFilesAt (root1 </> "c_drive" </> "rpt") |> List.map File.getFileName
    fileNames |> List.iter System.Console.WriteLine
    Assert.That (fileNames, Is.All.EndsWith("rdl"))

[<Test>]
let ``could search and return directories``() =

    let files = ls "c:/*/" |> getFiles1 root1 @"c:\rpt"
    Assert.That (files |> List.map File.getFileName, Is.EquivalentTo (List.toSeq ["bak"; "rpt"; "jparsec"]))

[<Test>]
let ``provides builder computation``() =
    let fileset = fileset {
        basedir @"c:\rpt"

        includes "*.rdl"
        includes "*.rdlx"
            
        includes @"..\jparsec\src\main/**/*.java"

        do! fileset {
            includes @"c:\bak\*.css"
        }
    }

    let files = fileset |> getFiles1 root1 "" |> List.map File.getFileName
    in
    do files |> List.iter System.Console.WriteLine
    do Assert.That(
        files,
        Constraints.Constraint.op_BitwiseOr( isAny().EndsWith(".java"), isAny().EndsWith(".css"))
        )        

[<Test>]
let ``handles 'explicit' rules, which match file regardless actual file presense``() =
    Assert.That(
        ls "c:/rpt/aaa.rdl" |> getFiles1 root1 "" |> List.map File.getFullName |> List.toArray,
        Is.All.EndsWith("rpt" </> "aaa.rdl").IgnoreCase)

[<Test>]
let ``explicitly specified file yields non-existing file``() =

    let sampleFileName = "NonExistingFile.rdl"

    let fileset = fileset {
        basedir @"c:\rpt"

        includes "*.rdl"
        includes sampleFileName
    }

    Assert.That(
        fileset |> getFiles1 root1 "" |> List.map File.getFileName,
        isAny().EndsWith(sampleFileName)
        )

[<Test>]
let ``providers operators for short definition``() =

    let fileset =
            ls "*.rdl" + "*.rdlx" + "../jparsec/src/main/**/*.java" @@ """c:\rpt"""
    Assert.That(
        fileset |> getFiles1 root1 "" |> List.map File.getFileName,
        Constraints.Constraint.op_BitwiseOr( isAny().EndsWith(".java"), isAny().EndsWith(".rdlx"))
        )

[<Test>]
let ``could combine filesets into new one``() =
    let fs1 = fileset {includes @"c:\!\bak\*.css"}
    let fs2 = fileset {includes @"c:\!\bak\*.rdl"}

    (fs1 + fs2) |> getFiles |> ignore

[<Test>]
let ``combine filesets with different base dirs are not implemented yet``() =
    let fs1 = fileset {
        basedir @"c:\!"
        includes @"bak\*.css"
        includesif false @"debug\*.css"
    }

    Assert.Throws<Exception> (fun () ->
        let fs2 = fileset {
            basedir @"c:\!\bak"
            includes @"*.rdl"

            join fs1
        }
        fs2 |> getFiles |> ignore
    ) |> ignore

// --- `**` and exclude masks on a real tree (G8: `!**/.*/**/*` cost ~1 ms per file) ---

module private tree =

    /// A fresh temporary folder holding `files` (relative, '/'), each with empty content.
    let make (files: string list) =
        let root = Path.Combine (Path.GetTempPath (), "xake-fileset-" + Guid.NewGuid().ToString "N")
        for f in files do
            let full = Path.Combine (root, f)
            Directory.CreateDirectory (Path.GetDirectoryName full) |> ignore
            File.WriteAllText (full, "")
        root

    /// The files `fs` lists under `root`, relative and '/'-separated, sorted.
    let list root (fs: Fileset) =
        let (Filelist files) = fs |> toFileList root
        let full = (DirectoryInfo root).FullName
        files
        |> List.map (fun f -> (File.getFullName f).Substring(full.Length).TrimStart('/', '\\').Replace('\\', '/'))
        |> List.sort

    let edgeTree = [
        "a/b.cs"; "a/.git/x.cs"; ".hidden/y.cs"; "a/b/.vs/deep/z.cs"; "top.cs"
        "dir/c.cs"; "dir/sub/d.cs"; "Dir2/e.cs"; "a.b/f.cs" ]

    let withTree files f =
        let root = make files
        try f root finally Directory.Delete (root, true)

[<Test>]
let ``'**/.*/**/*' excludes files below any dot folder and nothing else``() =
    tree.withTree tree.edgeTree <| fun root ->
        Assert.That (
            (!! "**/*.cs" -- "**/.*/**/*") |> tree.list root,
            Is.EquivalentTo ["a.b/f.cs"; "a/b.cs"; "dir/c.cs"; "dir/sub/d.cs"; "Dir2/e.cs"; "top.cs"])

[<Test>]
let ``'**/.*/**/*' exclusion keeps the order of includes and excludes``() =
    tree.withTree tree.edgeTree <| fun root ->
        // an include after the exclude brings the dot-folder files back
        Assert.That (
            (!! "**/*.cs" -- "**/.*/**/*" + ".hidden/*.cs") |> tree.list root,
            Does.Contain ".hidden/y.cs")

[<Test>]
let ``'dir/**/*' excludes every file below dir, 'dir/**' excludes none (current semantics)``() =
    tree.withTree tree.edgeTree <| fun root ->
        let all = (!! "**/*.cs") |> tree.list root
        Assert.That ((!! "**/*.cs" -- "dir/**/*") |> tree.list root,
                     Is.EqualTo (all |> List.filter (fun f -> not (f.StartsWith "dir/"))))
        // a mask ending in "**" names folders, not files: it matches no file
        Assert.That ((!! "**/*.cs" -- "dir/**") |> tree.list root, Is.EqualTo all)

[<Test>]
let ``directory masks in excludes follow the OS case rules``() =
    tree.withTree tree.edgeTree <| fun root ->
        let excluded = (!! "**/*.cs" -- "d*/**/*") |> tree.list root
        // "dir" always goes; "Dir2" goes only where masks ignore case (Windows)
        Assert.That (excluded, Does.Not.Contain "dir/c.cs")
        if Env.isUnix then Assert.That (excluded, Does.Contain "Dir2/e.cs")
        else Assert.That (excluded, Does.Not.Contain "Dir2/e.cs")

[<Test>]
let ``excludes match the same after a mask was used many times``() =
    // the mask regexes are cached: every use after the first goes through the cached regex
    // and must still give the known answer
    tree.withTree tree.edgeTree <| fun root ->
        let expected = ["a.b/f.cs"; "a/b.cs"; "Dir2/e.cs"; "top.cs"]
        for _ in 1 .. 3 do
            let fs = !! "**/*.cs" -- "**/.*/**/*" -- "dir/**/*"
            Assert.That (fs |> tree.list root, Is.EquivalentTo expected)

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
    tree.withTree files <| fun root ->
        let time name (fs: Fileset) =
            fs |> tree.list root |> ignore // warm-up
            let sw = Diagnostics.Stopwatch.StartNew ()
            let runs = 5
            let mutable count = 0
            for _ in 1 .. runs do count <- (fs |> tree.list root).Length
            TestContext.Progress.WriteLine (sprintf "%-40s %5d files  %8.1f ms/expansion" name count (sw.Elapsed.TotalMilliseconds / float runs))
        TestContext.Progress.WriteLine (sprintf "tree: %d files" files.Length)
        time "**/*.cs" (!! "**/*.cs")
        time "**/*.cs !**/.*/**/*" (!! "**/*.cs" -- "**/.*/**/*")
        time "**/*.cs !bin/**/* !obj/**/*" (!! "**/*.cs" -- "bin/**/*" -- "obj/**/*")
        time "**/*.cs !**/.*/**/* !bin !obj" (!! "**/*.cs" -- "**/.*/**/*" -- "bin/**/*" -- "obj/**/*")
        // the pattern module's walker (PR 4) on the same filesets
        let walk name (fileset: (Xake.PatternAst.MaskRole * string) list) =
            let scan () = Xake.PatternScan.scan (OperatingSystem.IsWindows()) root fileset
            scan () |> ignore // warm-up
            let sw = Diagnostics.Stopwatch.StartNew ()
            let runs = 5
            let mutable count = 0
            for _ in 1 .. runs do count <- (scan ()).Files.Length
            TestContext.Progress.WriteLine (sprintf "walker %-33s %5d files  %8.1f ms/expansion" name count (sw.Elapsed.TotalMilliseconds / float runs))
        let inc m = Xake.PatternAst.MaskRole.Include, m
        let exc m = Xake.PatternAst.MaskRole.Exclude, m
        walk "**/*.cs" [ inc "**/*.cs" ]
        walk "**/*.cs !**/.*/**/*" [ inc "**/*.cs"; exc "**/.*/**/*" ]
        walk "**/*.cs !bin/**/* !obj/**/*" [ inc "**/*.cs"; exc "bin/**/*"; exc "obj/**/*" ]
        walk "**/*.cs !**/.*/**/* !bin !obj" [ inc "**/*.cs"; exc "**/.*/**/*"; exc "bin/**/*"; exc "obj/**/*" ]
