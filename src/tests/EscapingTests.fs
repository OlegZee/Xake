module ``Pattern escaping``

// Reserved characters `( ) [ ] { } |` in rule patterns and fileset masks: `\c` is the literal c;
// unescaped they keep their former meaning and are reported once per pattern; `[c]` is read as the
// literal c (reported); `\` that escapes nothing is a separator (reported).

open System
open System.IO

open Xake
open Xake.Tasks
open NUnit.Framework

/// A fresh prefix, so that every case has its own pattern texts (warnings are reported once per
/// pattern text for the whole process).
let private uniq () = "u" + Guid.NewGuid().ToString("N").Substring(0, 8)

/// Runs `f` with the pattern warnings captured; returns its result and the warnings naming `pattern`.
let private withWarnings (pattern: string) (f: unit -> 'a) =
    let got = ResizeArray<string>()
    let result =
        use _ = PatternAst.Warnings.attach (fun m -> lock got (fun () -> got.Add m))
        f ()
    result, (lock got (fun () -> List.ofSeq got)) |> List.filter (fun m -> m.Contains("'" + pattern + "'"))

let private matches pattern file = (Path.matchGroups pattern "" file).IsSome

// `|` cannot be in a Windows file name
let private fileChars = if Env.isUnix then "()[]{}|" else "()[]{}"

module private tree =
    let make (files: string list) =
        let root = Path.Combine(Path.GetTempPath(), "xake-escaping-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory root |> ignore
        for f in files do
            let full = Path.Combine(root, f)
            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
            File.WriteAllText(full, "")
        root

    let list root (fs: Fileset) =
        let (Filelist files) = fs |> toFileList root
        let full = (DirectoryInfo root).FullName
        files
        |> List.map (fun f -> (File.getFullName f).Substring(full.Length).TrimStart('/', '\\').Replace('\\', '/'))
        |> List.sort

    let withTree files f =
        let root = make files
        try f root finally Directory.Delete(root, true)

// ------------------------------------------------------------------------------------- rules

[<TestCase("x(y)z.txt", "xyz.txt", true, "'(', ')'")>]          // a regex group
[<TestCase("x(y)z.txt", "x(y)z.txt", false, "'(', ')'")>]
[<TestCase("a]b.txt", "a]b.txt", true, "']'")>]                 // a lone ']' is a regex literal
[<TestCase("a[bc]d.txt", "abd.txt", true, "'[', ']'")>]         // a character class
[<TestCase("a[bc]d.txt", "a[bc]d.txt", false, "'[', ']'")>]
[<TestCase("a{b}.txt", "a{b}.txt", true, "'{', '}'")>]          // `{ }` were always literal
[<TestCase("a|b.txt", "zb.txt", true, "'|'")>]                  // an alternation over the whole regex
[<TestCase("a|b.txt", "a|b.txt", true, "'|'")>]
let ``rule: an unescaped reserved character keeps its former meaning, with one warning`` (body: string, file: string, expected: bool, chars: string) =
    let u = uniq ()
    let pattern = u + "/" + body
    let result, warnings = withWarnings pattern (fun () -> matches pattern (u + "/" + file))
    Assert.AreEqual(expected, result)
    Assert.AreEqual(1, warnings.Length, sprintf "%A" warnings)
    StringAssert.Contains(chars + " will be an error in a future release", warnings.Head)

[<Test>]
let ``rule: an escaped reserved character is a literal, without a warning``() =
    for c in "()[]{}|" do
        let u = uniq ()
        let pattern = sprintf @"%s/a\%cb*.txt" u c
        let (literal, other), warnings =
            withWarnings pattern (fun () ->
                matches pattern (sprintf "%s/a%cb1.txt" u c), matches pattern (sprintf "%s/ab1.txt" u))
        Assert.IsTrue(literal, pattern)
        Assert.IsFalse(other, pattern)
        Assert.IsEmpty(warnings, pattern)

[<Test>]
let ``rule: [c] is the literal c, with a warning suggesting the escape``() =
    for c in "()[{}|" do
        let u = uniq ()
        let pattern = sprintf "%s/a[%c]b*.txt" u c
        let (literal, groups), warnings =
            withWarnings pattern (fun () ->
                matches pattern (sprintf "%s/a%cb1.txt" u c),
                Path.matchGroups pattern "" (sprintf "%s/a%cb1.txt" u c) |> Option.map Map.ofList)
        Assert.IsTrue(literal, pattern)
        Assert.AreEqual(Some "1", groups |> Option.bind (Map.tryFind "1"), pattern)
        Assert.AreEqual(1, warnings.Length, pattern)
        StringAssert.Contains(sprintf @"'[%c]' is read as the literal '%c' for compatibility; write \%c instead" c c c, warnings.Head)

[<Test>]
let ``rule: [c] of an ordinary character suggests the character itself``() =
    let pattern = uniq () + "/lib[x].dll"
    let _, warnings = withWarnings pattern (fun () -> matches pattern "lib.dll")
    StringAssert.Contains("'[x]' is read as the literal 'x' for compatibility; write x instead", warnings.Head)

[<TestCase("a(b.txt")>]
[<TestCase("a)b.txt")>]
[<TestCase("a[b.txt")>]
[<TestCase("(name:a)b)c")>]
let ``rule: a lone paren or bracket is a clear error naming the pattern`` (pattern: string) =
    let ex = Assert.Throws<PatternAst.PatternException>(fun () -> Path.matchGroups pattern "/proj" "/proj/x" |> ignore)
    StringAssert.Contains(sprintf "Invalid pattern '%s'" pattern, ex.Message)
    StringAssert.Contains(@"Write \( \) \[ \] for a literal", ex.Message)

[<Test>]
let ``rule: a lone paren fails the build with a XakeException naming the pattern``() =
    let root = tree.make []
    try
        let errors = Collections.Generic.List<string>()
        let ex =
            Assert.Throws<XakeException>(fun () ->
                xake { ExecOptions.Default with
                        ProjectRoot = root; Targets = [ "a(b.txt" ]; ThrowOnError = true; NoPersist = true
                        ConLogLevel = Silent; FileLog = ""
                        CustomLogger = CustomLogger ((=) Level.Error) errors.Add } {
                    rule ("a(b.txt" ..> recipe { do! writeText "x" })
                })
        StringAssert.Contains("Invalid pattern 'a(b.txt'", ex.Message)
    finally Directory.Delete(root, true)

// ------------------------------------------------------------------------------------- masks

let private maskTree (u: string) =
    [ for c in fileChars -> sprintf "%s/a%c1.txt" u c ] @ [ u + "/a1.txt"; u + "/b.txt" ]

[<Test>]
let ``include: an unescaped reserved character is a literal as before, with one warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        for c in fileChars do
            let mask = sprintf "%s/a%c*.txt" u c
            let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
            Assert.AreEqual([ sprintf "%s/a%c1.txt" u c ], files, mask)
            Assert.AreEqual(1, warnings.Length, mask)
            StringAssert.Contains(sprintf "'%c' will be an error in a future release; write \\%c for a literal" c c, warnings.Head)
        // a literal segment (no wildcard) is a literal too
        let mask = sprintf "%s/a(1.txt" u
        let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
        Assert.AreEqual([ sprintf "%s/a(1.txt" u ], files)
        Assert.AreEqual(1, warnings.Length)

[<Test>]
let ``include: an escaped reserved character is a literal, without a warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        for c in fileChars do
            for mask in [ sprintf @"%s/a\%c*.txt" u c; sprintf @"%s/a\%c1.txt" u c ] do
                let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
                Assert.AreEqual([ sprintf "%s/a%c1.txt" u c ], files, mask)
                Assert.IsEmpty(warnings, mask)

[<Test>]
let ``include: [c] is the literal c, with a warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        for c in fileChars |> Seq.filter ((<>) ']') do
            let mask = sprintf "%s/a[%c]*.txt" u c
            let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
            Assert.AreEqual([ sprintf "%s/a%c1.txt" u c ], files, mask)
            Assert.AreEqual(1, warnings.Length, mask)
            StringAssert.Contains(sprintf "'[%c]' is read as the literal" c, warnings.Head)

[<Test>]
let ``exclude: an unescaped reserved character keeps its former meaning, with one warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        let all = !! (u + "/*.txt") |> tree.list root
        // a segment without a wildcard is compared as plain text: a literal
        for c in fileChars do
            let mask = sprintf "%s/a%c1.txt" u c
            let files, warnings = withWarnings mask (fun () -> !! (u + "/*.txt") -- mask |> tree.list root)
            Assert.AreEqual(all |> List.filter ((<>) (sprintf "%s/a%c1.txt" u c)), files, mask)
            Assert.AreEqual(1, warnings.Length, mask)
        // a segment with a wildcard goes through the mask regex: `{ }` literal, '|' an alternation,
        // a lone '(' an error
        let files, warnings = withWarnings (u + "/a{*.txt") (fun () -> !! (u + "/*.txt") -- (u + "/a{*.txt") |> tree.list root)
        Assert.AreEqual(all |> List.filter ((<>) (u + "/a{1.txt")), files)
        Assert.AreEqual(1, warnings.Length)
        let files, _ = withWarnings (u + "/b|a1*.txt") (fun () -> !! (u + "/*.txt") -- (u + "/b|a1*.txt") |> tree.list root)
        Assert.AreEqual(all |> List.filter (fun f -> not (f.EndsWith "/a1.txt" || f.EndsWith "/b.txt")), files)
        Assert.Throws<PatternAst.PatternException>(fun () -> !! (u + "/*.txt") -- (u + "/a(*.txt") |> tree.list root |> ignore) |> ignore

[<Test>]
let ``exclude: an escaped reserved character is a literal, without a warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        let all = !! (u + "/*.txt") |> tree.list root
        for c in fileChars do
            for mask in [ sprintf @"%s/a\%c*.txt" u c; sprintf @"%s/a\%c1.txt" u c ] do
                let files, warnings = withWarnings mask (fun () -> !! (u + "/*.txt") -- mask |> tree.list root)
                Assert.AreEqual(all |> List.filter ((<>) (sprintf "%s/a%c1.txt" u c)), files, mask)
                Assert.IsEmpty(warnings, mask)

[<Test>]
let ``exclude: [c] is the literal c, with a warning``() =
    let u = uniq ()
    tree.withTree (maskTree u) <| fun root ->
        let all = !! (u + "/*.txt") |> tree.list root
        for c in fileChars |> Seq.filter ((<>) ']') do
            for mask in [ sprintf "%s/a[%c]*.txt" u c; sprintf "%s/a[%c]1.txt" u c ] do
                let files, warnings = withWarnings mask (fun () -> !! (u + "/*.txt") -- mask |> tree.list root)
                Assert.AreEqual(all |> List.filter ((<>) (sprintf "%s/a%c1.txt" u c)), files, mask)
                Assert.AreEqual(1, warnings.Length, mask)

// ------------------------------------------------------------------------------------- groups in masks

let private groupWarning = "only meaningful in rule patterns; in a mask they are not groups"

[<Test>]
let ``include: a named group in a mask is literal text as before (finds nothing), with one warning``() =
    let u = uniq ()
    tree.withTree [ u + "/a.c"; u + "/b.c"; u + "/sub/c.c" ] <| fun root ->
        for mask in [ u + "/(name:*).c"; u + "/(name:*.c)"; u + "/(d:*)/*.c" ] do
            for fs in [ !! mask; ls mask; fileset { includes mask } ] do
                let files, warnings = withWarnings mask (fun () -> fs |> tree.list root)
                Assert.IsEmpty(files, mask)
                Assert.LessOrEqual(warnings.Length, 1, mask)
        // once per mask, naming the group
        let mask = u + "/(n:?).c"
        let _, warnings = withWarnings mask (fun () -> for _ in 1 .. 3 do !! mask |> tree.list root |> ignore)
        Assert.AreEqual(1, warnings.Length)
        StringAssert.Contains("named groups ('(n:...)') are " + groupWarning, warnings.Head)
        StringAssert.Contains(@"will be an error in a future release; write \( \) for literal parentheses", warnings.Head)

[<Test>]
let ``exclude: a named group in a wildcard segment matches its body as before, with one warning``() =
    let u = uniq ()
    tree.withTree [ u + "/a.c"; u + "/b.c"; u + "/x.h"; u + "/sub/c.c"; u + "/sub/d.h" ] <| fun root ->
        let all = !! (u + "/**/*") |> tree.list root
        let cases =
            [ u + "/(name:*).c", [ u + "/a.c"; u + "/b.c" ]
              u + "/(name:a*).c", [ u + "/a.c" ]
              u + "/(d:*)/*.h", [ u + "/sub/d.h" ]
              u + "/**/(n:*).c", [ u + "/a.c"; u + "/b.c"; u + "/sub/c.c" ]
              // a segment without a wildcard is plain text: the group is literal there
              u + "/(name:a).c", [] ]
        for mask, removed in cases do
            let files, warnings = withWarnings mask (fun () -> !! (u + "/**/*") -- mask |> tree.list root)
            Assert.AreEqual(all |> List.except removed, files, mask)
            Assert.AreEqual(1, warnings.Length, mask)
            StringAssert.Contains(groupWarning, warnings.Head)

[<Test>]
let ``a named group warns in a mask, not in the same rule pattern``() =
    let u = uniq ()
    let pattern = u + "/(n:*).c"
    let _, ruleWarnings = withWarnings pattern (fun () -> matches pattern (u + "/a.c"))
    Assert.IsEmpty ruleWarnings
    let _, maskWarnings = withWarnings pattern (fun () -> Empty -- pattern |> ignore)
    Assert.AreEqual(1, maskWarnings.Length)

// ------------------------------------------------------------------------------------- backslash

[<Test>]
let ``a backslash separator still works, with a warning to use a slash``() =
    let u = uniq ()
    let rule = u + @"\*.c"
    let matched, warnings = withWarnings rule (fun () -> matches rule (u + "/x.c"))
    Assert.IsTrue matched
    Assert.AreEqual(1, warnings.Length)
    StringAssert.Contains(@"'\' as a path separator is deprecated; use '/'", warnings.Head)
    tree.withTree [ u + "/f.txt" ] <| fun root ->
        let mask = u + @"\*.txt"
        let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
        Assert.AreEqual([ u + "/f.txt" ], files)
        Assert.AreEqual(1, warnings.Length)

[<Test>]
let ``paths read from disk and paths joined by Xake give no warning``() =
    let u = uniq ()
    let got = ResizeArray<string>()
    use _ = PatternAst.Warnings.attach (fun m -> lock got (fun () -> got.Add m))
    // the file side of a match and the project root are paths, not patterns
    Assert.IsTrue((Path.matchGroups "*.c" (@"c:\" + u) (@"c:\" + u + @"\x.c")).IsSome)
    Assert.IsTrue(Path.matches "*.c" (@"c:\" + u) (@"c:\" + u + @"\x.c"))
    // `</>` joins with '/' on every OS
    let joined = u </> "src" </> "*.cs"
    Assert.AreEqual(u + "/src/*.cs", joined)
    Path.parse joined |> ignore
    Assert.IsEmpty(got |> Seq.filter (fun m -> m.Contains u))

[<Test>]
let ``a Windows-style src\(x) now escapes the paren (intended change)``() =
    let u = uniq ()
    tree.withTree [ u + "/src/(x)/a.cs"; u + "/src(x)/b.cs" ] <| fun root ->
        // the mask is now the directory "src(x)", no longer "src/(x)"
        let mask = u + @"/src\(x)\*.cs"
        let files, warnings = withWarnings mask (fun () -> !! mask |> tree.list root)
        Assert.AreEqual([ u + "/src(x)/b.cs" ], files)
        Assert.AreEqual(1, warnings.Length)     // the unescaped ')' and the '\' separator, in one warning
        StringAssert.Contains("')'", warnings.Head)
        StringAssert.Contains(@"'\' as a path separator", warnings.Head)
        // the slash form keeps the old meaning
        Assert.AreEqual([ u + "/src/(x)/a.cs" ], !! (u + @"/src/\(x\)/*.cs") |> tree.list root)
    // as a rule the unescaped ')' is a lone regex paren now: a clear error
    Assert.Throws<PatternAst.PatternException>(fun () -> Path.matchGroups @"src\(x)\*.cs" "" "src/(x)/a.cs" |> ignore) |> ignore

// ------------------------------------------------------------------------------------- root

[<Test>]
let ``a project root with reserved characters matches like any other``() =
    let specials = if Env.isUnix then [ "p(q"; "p)q"; "p(q)"; "p[q]"; "p[q"; "p{q}"; "p|q" ] else [ "p(q"; "p)q"; "p(q)"; "p[q]"; "p[q"; "p{q}" ]
    for s in specials do
        let root = "/tmp/" + s
        let groups = Path.matchGroups "(n:*).c" root (root + "/a.c") |> Option.map Map.ofList
        Assert.AreEqual(Some "a", groups |> Option.bind (Map.tryFind "n"), root)
        Assert.IsTrue((Path.matchGroups "out/*.c" root (root + "/out/a.c")).IsSome, root)
        Assert.IsTrue((Path.matchGroups "*.c" root "/tmp/pq/a.c").IsNone, root)
        Assert.IsTrue((Path.matchGroups "*.c" root "/elsewhere/a.c").IsNone, root)

[<Test>]
let ``a build in a project root with reserved characters finds its rules``() =
    let name = if Env.isUnix then "xake-root-(a)[b]{c}|d" else "xake-root-(a)[b]{c}"
    let root = Path.Combine(Path.GetTempPath(), name + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore
    try
        xake { ExecOptions.Default with
                ProjectRoot = root; Targets = [ "a.txt" ]; ThrowOnError = true; NoPersist = true
                ConLogLevel = Silent; FileLog = "" } {
            rule ("(n:*).txt" ..> recipe { do! writeText "x" })
        }
        Assert.IsTrue(File.Exists(Path.Combine(root, "a.txt")))
    finally Directory.Delete(root, true)

// ------------------------------------------------------------------------------------- once

[<Test>]
let ``a pattern is reported once, however often it is used``() =
    let u = uniq ()
    let pattern = u + "/a{1}*.txt"
    let _, warnings =
        withWarnings pattern (fun () ->
            for _ in 1 .. 3 do
                matches pattern (u + "/a{1}x.txt") |> ignore
                Path.matchGroups pattern "/root" "/root/x" |> ignore
                !! pattern |> ignore
                Empty -- pattern |> ignore)
    Assert.AreEqual(1, warnings.Length)

[<Test>]
let ``warnings reach the build log, once per pattern``() =
    let root = tree.make []
    let u = uniq ()
    try
        let log = Collections.Generic.List<string>()
        xake { ExecOptions.Default with
                ProjectRoot = root; Targets = [ u + "/a.txt"; u + "/b.txt" ]; ThrowOnError = true; NoPersist = true
                ConLogLevel = Silent; FileLog = ""
                CustomLogger = CustomLogger ((=) Level.Message) (fun m -> lock log (fun () -> log.Add m)) } {
            rule ((u + "/(n:*).t{x}t") ..> recipe { do! writeText "never" })
            rule ((u + "/(n:*).txt") ..> recipe {
                let! files = (!! (u + "/x[{]*.txt")) |> getFiles
                do! writeText (string files) })
        }
        let mentions (p: string) = log |> Seq.filter (fun m -> m.Contains("'" + p + "'")) |> Seq.length
        Assert.AreEqual(1, mentions (u + "/(n:*).t{x}t"), String.Join("\n", log))
        Assert.AreEqual(1, mentions (u + "/x[{]*.txt"), String.Join("\n", log))
        Assert.AreEqual(0, mentions (u + "/(n:*).txt"), String.Join("\n", log))
    finally Directory.Delete(root, true)
