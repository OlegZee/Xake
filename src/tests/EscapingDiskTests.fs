module ``Pattern escaping on disk``

// Files and directories whose names hold the reserved characters `( ) [ ] { } |`: escaped masks and
// rule patterns find exactly them (and nothing next to them), in includes, excludes and rules.

open System
open System.IO

open Xake
open Xake.Tasks
open NUnit.Framework

let private uniq () = "u" + Guid.NewGuid().ToString("N").Substring(0, 8)

/// Runs `f` with the pattern warnings captured; returns its result and the warnings naming `pattern`.
let private withWarnings (pattern: string) (f: unit -> 'a) =
    let got = ResizeArray<string>()
    let result =
        use _ = PatternAst.Warnings.attach (fun m -> lock got (fun () -> got.Add m))
        f ()
    result, (lock got (fun () -> List.ofSeq got)) |> List.filter (fun m -> m.Contains("'" + pattern + "'"))

/// Each odd name next to its plain twin (a mask must not match the twin). `|` cannot be in a
/// Windows file name.
let private files =
    [ "a[test.c"; "atest.c"
      "b]x.c"; "bx.c"
      "c{1}.c"; "c1.c"
      "d(2).c"; "d2.c"
      "sub[1]/s.c"; "sub[1]/s.h"; "sub1/t.c"
      "odd{dir}/q.c"; "deep/odd{dir}/o.c"; "deep/odddir/p.c" ]
    @ (if Env.isUnix then [ "e|f.c"; "ef.c" ] else [])

/// A temp tree with `files` under the directory `u`; `f` gets the root.
let private withTree (u: string) f =
    let root = Path.Combine(Path.GetTempPath(), "xake-escdisk-" + Guid.NewGuid().ToString "N")
    for name in files do
        let full = Path.Combine(root, u, name)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, "")
    try f root finally Directory.Delete(root, true)

/// The files of a fileset, relative to `root/u`, with '/'.
let private list root (u: string) (fs: Fileset) =
    let (Filelist found) = fs |> toFileList root
    let prefix = (DirectoryInfo (Path.Combine(root, u))).FullName
    found
    |> List.map (fun f -> (File.getFullName f).Substring(prefix.Length).TrimStart('/', '\\').Replace('\\', '/'))
    |> List.sort

/// Every way to include a mask: `!!`, `ls`, `fileset { includes }`.
let private includeForms (mask: string) =
    [ "!!", !! mask; "ls", ls mask; "fileset", fileset { includes mask } ]

/// Escaped masks with the files each must find exactly.
let private escaped =
    [ @"a\[test.c", [ "a[test.c" ]
      @"b\]x.c", [ "b]x.c" ]
      @"c\{1\}.c", [ "c{1}.c" ]
      @"d\(2\).c", [ "d(2).c" ]
      @"a\[*.c", [ "a[test.c" ]
      @"*\]*.c", [ "b]x.c" ]
      @"*\{*\}.c", [ "c{1}.c" ]
      @"?\(?\).c", [ "d(2).c" ]
      @"sub\[1\]/s.c", [ "sub[1]/s.c" ]
      @"sub\[1\]/*.c", [ "sub[1]/s.c" ]
      @"sub\[*/*", [ "sub[1]/s.c"; "sub[1]/s.h" ]
      @"odd\{dir\}/*.c", [ "odd{dir}/q.c" ]
      @"**/odd\{dir\}/*.c", [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ]
      @"**/odd\{*\}/*.c", [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ] ]
    @ (if Env.isUnix then [ @"e\|f.c", [ "e|f.c" ]; @"e\|*.c", [ "e|f.c" ] ] else [])

[<Test>]
let ``include: an escaped mask finds exactly the files with the reserved character, without a warning``() =
    let u = uniq ()
    withTree u <| fun root ->
        for mask, expected in escaped do
            for form, fs in includeForms (u + "/" + mask) do
                let found, warnings = withWarnings (u + "/" + mask) (fun () -> fs |> list root u)
                Assert.AreEqual(List.sort expected, found, sprintf "%s %s" form mask)
                Assert.IsEmpty(warnings, mask)

[<Test>]
let ``exclude: an escaped mask removes exactly the files with the reserved character, without a warning``() =
    let u = uniq ()
    withTree u <| fun root ->
        let all = !! (u + "/**/*") |> list root u
        Assert.AreEqual(List.sort files, all)
        for mask, expected in escaped do
            let pattern = u + "/" + mask
            for form, fs in [ "--", !! (u + "/**/*") -- pattern
                              "excludes", fileset { includes (u + "/**/*"); excludes pattern } ] do
                let found, warnings = withWarnings pattern (fun () -> fs |> list root u)
                Assert.AreEqual(all |> List.except expected, found, sprintf "%s %s" form mask)
                Assert.IsEmpty(warnings, mask)

[<Test>]
let ``the F# literal forms of an escaped mask are the same text``() =
    let u = uniq ()
    let regular = u + "/a\[test.c"
    let verbatim = u + @"/a\[test.c"
    let triple = u + """/a\[test.c"""
    Assert.AreEqual(verbatim, regular)
    Assert.AreEqual(verbatim, triple)
    withTree u <| fun root ->
        for mask in [ regular; verbatim; triple ] do
            Assert.AreEqual([ "a[test.c" ], !! mask |> list root u)

[<Test>]
let ``include and exclude: an unescaped mask finds the literal as before, with one warning``() =
    let u = uniq ()
    withTree u <| fun root ->
        let all = !! (u + "/**/*") |> list root u
        let cases =
            [ "a[test.c", [ "a[test.c" ]
              "b]x.c", [ "b]x.c" ]
              "c{1}.c", [ "c{1}.c" ]
              "d(2).c", [ "d(2).c" ]
              "odd{dir}/*.c", [ "odd{dir}/q.c" ]
              "**/odd{dir}/*.c", [ "deep/odd{dir}/o.c"; "odd{dir}/q.c" ] ]
            @ (if Env.isUnix then [ "e|f.c", [ "e|f.c" ] ] else [])
        for mask, expected in cases do
            let pattern = u + "/" + mask
            let (included, excluded), warnings =
                withWarnings pattern (fun () ->
                    (!! pattern |> list root u), (!! (u + "/**/*") -- pattern |> list root u))
            Assert.AreEqual(expected, included, "include " + mask)
            Assert.AreEqual(all |> List.except expected, excluded, "exclude " + mask)
            Assert.AreEqual(1, warnings.Length, mask)
            StringAssert.Contains("will be an error in a future release", warnings.Head)
        // in an include an unescaped wildcard segment goes to the OS glob: literal there too
        let pattern = u + "/*{*}.c"
        let found, warnings = withWarnings pattern (fun () -> !! pattern |> list root u)
        Assert.AreEqual([ "c{1}.c" ], found)
        Assert.AreEqual(1, warnings.Length)

[<Test>]
let ``include and exclude: [c] matches the literal c, with one warning``() =
    let u = uniq ()
    withTree u <| fun root ->
        let all = !! (u + "/**/*") |> list root u
        for mask, expected in [ "a[[]test.c", [ "a[test.c" ]
                                "d[(]2[)].c", [ "d(2).c" ]
                                "c[{]*.c", [ "c{1}.c" ]
                                "sub[[]1]/*.c", [ "sub[1]/s.c" ] ] do
            let pattern = u + "/" + mask
            let (included, excluded), warnings =
                withWarnings pattern (fun () ->
                    (!! pattern |> list root u), (!! (u + "/**/*") -- pattern |> list root u))
            Assert.AreEqual(expected, included, "include " + mask)
            Assert.AreEqual(all |> List.except expected, excluded, "exclude " + mask)
            Assert.AreEqual(1, warnings.Length, mask)
            StringAssert.Contains("is read as the literal", warnings.Head)

[<Test>]
let ``an unescaped sub[1] is the [c] form: it finds sub1, not the directory sub[1]``() =
    // a one-character class is the literal character (the former rule idiom), in masks as well;
    // the directory named `sub[1]` needs `sub\[1\]`
    let u = uniq ()
    withTree u <| fun root ->
        let pattern = u + "/sub[1]/*.c"
        let found, warnings = withWarnings pattern (fun () -> !! pattern |> list root u)
        Assert.AreEqual([ "sub1/t.c" ], found)
        Assert.AreEqual(1, warnings.Length)
        StringAssert.Contains("'[1]' is read as the literal '1' for compatibility; write 1 instead, or \\[1\\] for the brackets themselves", warnings.Head)

// ------------------------------------------------------------------------------------- rules

[<Test>]
let ``rule: an escaped pattern matches a target with the reserved character, and only it``() =
    let root = "/proj"
    let groups pattern file = Path.matchGroups pattern root (root + "/" + file) |> Option.map Map.ofList
    let name pattern file = groups pattern file |> Option.bind (Map.tryFind "name")
    Assert.AreEqual(Some "x", name @"out/a\[(name:*).c" "out/a[x.c")
    Assert.AreEqual(None, name @"out/a\[(name:*).c" "out/ax.c")
    Assert.AreEqual(Some "1", name @"out/c\{(name:*)\}.c" "out/c{1}.c")
    Assert.AreEqual(Some "2", name @"out/d\((name:*)\).c" "out/d(2).c")
    Assert.AreEqual(None, name @"out/d\((name:*)\).c" "out/d2.c")
    Assert.AreEqual(Some "s", name @"sub\[1\]/(name:*).c" "sub[1]/s.c")
    Assert.AreEqual(None, name @"sub\[1\]/(name:*).c" "sub1/s.c")
    Assert.AreEqual(Some "q", name @"**/odd\{dir\}/(name:*).c" "deep/odd{dir}/q.c")
    Assert.AreEqual(None, name @"**/odd\{dir\}/(name:*).c" "deep/odddir/q.c")
    Assert.AreEqual(Some "f", name @"e\|(name:*).c" "e|f.c")
    Assert.AreEqual(None, name @"e\|(name:*).c" "zf.c")
    // positional wildcards keep their numbers: an escape opens no capture
    Assert.AreEqual(Some "test", groups @"a\[*.c" "a[test.c" |> Option.bind (Map.tryFind "1"))

[<Test>]
let ``rule: a build makes a target with the reserved character and needs a source with it``() =
    let u = uniq ()
    withTree u <| fun root ->
        let srcName = if Env.isUnix then "e|f.c" else "d(2).c"
        xake { ExecOptions.Default with
                ProjectRoot = root; Targets = [ u + "/out/a[x.c"; "main" ]; ThrowOnError = true; NoPersist = true
                ConLogLevel = Silent; FileLog = "" } {
            rule ((u + @"/out/a\[(name:*).c") ..> recipe {
                let! name = getRuleMatch "name"
                do! need [ u + "/a[test.c" ]
                do! writeText name })
            rule ((u + @"/out/(name:*)\{1\}.o") ..> recipe {
                let! name = getRuleMatch "name"
                do! writeText name })
            phony "main" (recipe {
                do! need [ u + "/out/b{1}.o"; u + "/" + srcName; u + "/sub[1]/s.c" ] })
        }
        Assert.AreEqual("x", File.ReadAllText(Path.Combine(root, u, "out", "a[x.c")))
        Assert.AreEqual("b", File.ReadAllText(Path.Combine(root, u, "out", "b{1}.o")))
