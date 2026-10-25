// Probe: how special characters behave TODAY in rule patterns (Path.matchGroups) and fileset masks.
// Run: dotnet fsi probe.fsx   (needs src/core built: dotnet build src/core -c Release -f netstandard2.0)
#r "../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
open System
open System.IO
open Xake

let jobTmp = Path.Combine(Path.GetTempPath(), "xake-escaping-probe")

let show (gs: (string*string) list option) =
    match gs with
    | None -> "NO MATCH"
    | Some g -> "MATCH " + (g |> List.filter (fun (k,_) -> k <> "0") |> List.map (fun (k,v) -> sprintf "%s=%s" k v) |> String.concat ";")
let tryM (pat: string) root (file: string) =
    try Xake.Path.matchGroups pat root file |> show with e -> "EXN " + e.GetType().Name + ": " + e.Message.Split('\n').[0]

printfn "=== A. rule patterns: Path.matchGroups pattern root file  (root = /proj) ==="
let a (pat: string) (file: string) = printfn "%-26s vs %-22s -> %s" pat file (tryM pat "/proj" ("/proj/" + file))
// single characters in the literal position, file contains the char literally
for c in ["(";")";"[";"]";"|";"{";"}";"+";".";"^";"$";"\\";"!";"#"] do
    a ("a" + c + "b.txt") ("a" + c + "b.txt")
    a ("a" + c + "b.txt") ("aXb.txt")
printfn "--- extra: what a char as regex syntax would match ---"
a "a|b.txt" "a.txt"; a "a|b.txt" "b.txt"; a "a|b.txt" "a|b.txt"
a "a.b" "aXb"; a "a+b" "aab"; a "a+b" "a+b"; a "a{2}" "aa"; a "a{2}" "a{2}"
a "a^b" "a^b"; a "a$b" "a$b"; a "a\\b.txt" "a/b.txt"
printfn "--- required cases ---"
a "[(]x[)]" "(x)"; a "[(]x[)]" "[(]x[)]"; a "[(]x[)]" "(x["
a "[abc]*.txt" "a1.txt"; a "[abc]*.txt" "[abc]1.txt"; a "[abc]*.txt" "d1.txt"
a "lib[x].dll" "libx.dll"; a "lib[x].dll" "lib[x].dll"
a "fun(ction).o" "fun(ction).o"; a "fun(ction).o" "function.o"
a "(foo)/*.c" "foo/a.c"; a "(foo)/*.c" "(foo)/a.c"
a "(foo)/(name:*).c" "foo/a.c"; a "(foo)/(name:*).c" "(foo)/a.c"
a "(name:*).(ext:*)" "a.txt"
a "(name:*)(x).c" "a(x).c"; a "(name:*)(x).c" "ax.c"
a "((name:*)).c" "(a).c"; a "(name:*)[(]x[)].c" "a(x).c"
a "(name:[(]*).ss" "(abc.ss"; a "[(]*.ss" "(abc.ss"
a "(name:a)b)c" "ab)c"
a "(a:b" "a:b"; a "(name:*" "xyz"
a "a(b" "a(b"; a "a)b" "a)b"
a "(1:*).c" "x.c"; a "(a-b:*).c" "x.c"; a "(a b:*).c" "x.c"
printfn "--- positional numbering ---"
a "*/*.c" "d/x.c"; a "(n:*)/*.c" "d/x.c"; a "*/(n:*).c" "d/x.c"; a "(n:*)/(m:*).c" "d/x.c"
a "(foo)/*.c" "foo/x.c"; a "(n:(m:*)x)" "abx"
a "src/**/*.c" "src/a/b/x.c"; a "?/*.c" "d/x.c"
a "(a)(b)*" "abz"
printfn "--- case: ! # in rules ---"
a "!a.txt" "!a.txt"; a "#a.txt" "#a.txt"; a "a#b" "a#b"
// verbatim copy of internal ExecCore.applyWildcards (module is internal)
module ExecCore =
    open System.Text.RegularExpressions
    let wildcardsRegex = Regex(@"\*\*|\*|\?")
    let patternTagRegex = Regex(@"\((?'tag'\w+?)\:[^)]+\)")
    let applyWildcards = function
        | None -> id
        | Some matches ->
            fun pat ->
                let mutable i = 0
                let evaluator (m: Match) =
                    i <- i + 1
                    matches |> Map.tryFind (string i) |> Option.defaultValue ""
                let evaluatorTag (m: Match) = matches |> Map.tryFind m.Groups.["tag"].Value |> Option.defaultValue ""
                pat |> (fun t -> wildcardsRegex.Replace(t, evaluator)) |> (fun t -> patternTagRegex.Replace(t, evaluatorTag))
printfn "--- ExecCore.applyWildcards (multi-file target name generation, positional) ---"
let gen pat file =
    match Xake.Path.matchGroups pat "/proj" ("/proj/" + file) with
    | Some g -> ExecCore.applyWildcards (Some (Map.ofList g)) "out/*.o"
    | None -> "NO MATCH"
printfn "pat *.c, file x.c, out/*.o -> %s" (gen "*.c" "x.c")
printfn "pat (n:*).c, file x.c, out/*.o -> %s" (gen "(n:*).c" "x.c")
printfn "pat [(]*.c, file (x.c, out/*.o -> %s" (gen "[(]*.c" "(x.c")
printfn "applyWildcards tag: out/(n:q).o with n=x -> %s" (ExecCore.applyWildcards (Some (Map.ofList ["n","x"])) "out/(n:q).o")

printfn "\n=== A2. project root containing special chars (root literal goes into the regex) ==="
for root in ["/tmp/p(q)"; "/tmp/p[q]"; "/tmp/p+q"; "/tmp/p.q"; "/tmp/p{1}"; "/tmp/p|q"; "/tmp/p$q"; "/tmp/p^q"; "/tmp/p!q"; "/tmp/p#q"] do
    printfn "root %-12s *.c vs %s/x.c -> %s" root root (tryM "*.c" root (root + "/x.c"))
printfn "root /tmp/p[q] vs /tmp/pq/x.c (regex class reading) -> %s" (tryM "*.c" "/tmp/p[q]" "/tmp/pq/x.c")
printfn "root /tmp/p|q vs /tmp/p/x.c -> %s" (tryM "*.c" "/tmp/p|q" "/tmp/p/x.c")
printfn "root /tmp/p.q vs /tmp/pXq/x.c -> %s" (tryM "*.c" "/tmp/p.q" "/tmp/pXq/x.c")
printfn "root /tmp/p(q vs same -> %s" (tryM "*.c" "/tmp/p(q" "/tmp/p(q/x.c")
printfn "root /tmp/p)q vs same -> %s" (tryM "*.c" "/tmp/p)q" "/tmp/p)q/x.c")
printfn "root /tmp/p(q) groups -> %s" (tryM "(n:*).c" "/tmp/p(q)" "/tmp/p(q)/x.c")
printfn "root /tmp/p(q) with positional * -> %s" (tryM "*.c" "/tmp/p(q)" "/tmp/p(q)/x.c")
printfn "root /tmp/a\\b (backslash in root) -> %s" (tryM "*.c" "/tmp/a\\b" "/tmp/a\\b/x.c")

printfn "\n=== A3. backslash / Windows separators ==="
printfn "Path.parse \"a\\\\b\\\\*.c\"  -> %A" (Xake.Path.parse "a\\b\\*.c")
printfn "pattern a\\*.c vs a/x.c -> %s" (tryM "a\\*.c" "/proj" "/proj/a/x.c")
printfn "pattern a/*.c vs a\\x.c (file given with backslash) -> %s" (tryM "a/*.c" "/proj" "/proj/a\\x.c")
printfn "pattern a\\.c vs a/.c -> %s" (tryM "a\\.c" "/proj" "/proj/a/.c")
printfn "pattern [\\\\]x vs \\x -> %s" (tryM "[\\\\]x" "/proj" "/proj/\\x")
printfn "pattern a\\(b vs a/(b -> %s" (tryM "a\\(b" "/proj" "/proj/a/(b")
printfn "pattern a\\*b vs a*b -> %s" (tryM "a\\*b" "/proj" "/proj/a*b")
printfn "Path.parse \"a\\\\(b\" -> %A" (Xake.Path.parse "a\\(b")

printfn "\n=== B. fileset masks over a real temp tree ==="
let tmp = Path.Combine(jobTmp, "tree")
if Directory.Exists tmp then Directory.Delete(tmp, true)
Directory.CreateDirectory tmp |> ignore
let names =
    [ "(x).txt"; "(a.txt"; "a).txt"; "[x].txt"; "[abc]1.txt"; "a1.txt"; "b2.txt"; "d3.txt"; "a|b.txt"; "a.txt"; "b.txt"
      "{x}.txt"; "a+b.txt"; "aab.txt"; "aXb.txt"; "a^b.txt"; "a$b.txt"; "a!b.txt"; "!a.txt"; "#a.txt"; "a#b.txt"; "a\\b.txt"
      "libx.dll"; "lib[x].dll"; "fun(ction).o"; "function.o"; "x.c"; "(n).c" ]
for n in names do
    try File.WriteAllText(Path.Combine(tmp, n), "") with e -> printfn "cannot create %s: %s" n e.Message
Directory.CreateDirectory(Path.Combine(tmp, "foo")) |> ignore
File.WriteAllText(Path.Combine(tmp, "foo", "a.c"), "")
Directory.CreateDirectory(Path.Combine(tmp, "(foo)")) |> ignore
File.WriteAllText(Path.Combine(tmp, "(foo)", "a.c"), "")
printfn "files: %s" (Directory.GetFileSystemEntries tmp |> Array.map Path.GetFileName |> Array.sort |> String.concat " ")

let lsr (label: string) (fs: unit -> Fileset) =
    try
        let (Filelist l) = Fileset.toFileList tmp (fs ())
        printfn "%-34s -> [%s]" label (l |> List.map (File.getFullName >> fun f -> Path.GetRelativePath(tmp, f)) |> List.sort |> String.concat " ")
    with e -> printfn "%-34s -> EXN %s: %s" label (e.GetType().Name) (e.Message.Split('\n').[0])
let inc (m: string) = lsr ("!! " + m) (fun () -> !! m)
let exc (m: string) = lsr ("!! *.txt -- " + m) (fun () -> !! "*.txt" -- m)
printfn "--- includes (!! / ls) ---"
for m in ["[(]x[)].txt"; "[abc]*.txt"; "[abc]1.txt"; "a|b.txt"; "(x).txt"; "(foo)/*.c"; "(n:*).c"; "*(x)*"; "lib[x].dll"; "fun(ction).o"
          "{x}.txt"; "a+b.txt"; "a^b.txt"; "a$b.txt"; "a!b.txt"; "!a.txt"; "#a.txt"; "a#b.txt"; "a\\b.txt"; "a.txt"; "?.txt"; "*"; "[x].txt"; "(a.txt"; "a).txt"; "*.[ch]"]
    do inc m
printfn "--- includes of literal names (no wildcard => explicit, listed even if absent) ---"
inc "(nosuch).txt"
inc "[nosuch].txt"
printfn "--- excludes (--) on *.txt ---"
for m in ["[(]x[)].txt"; "[abc]*.txt"; "a|b.txt"; "(x).txt"; "(foo)/*.c"; "(n:*).txt"; "lib[x].dll"; "{x}.txt"; "a+b.txt"; "a^b.txt"; "a$b.txt"; "a!b.txt"; "#a.txt"; "[x].txt"; "a.txt"; "a?b.txt"; "*(x)*"; "(a.txt"; "a).txt"; "a\\b.txt"]
    do exc m
printfn "--- fileset { } CE ---"
lsr "fileset includes [abc]*.txt" (fun () -> fileset { includes "[abc]*.txt" })
lsr "fileset inc *.txt exc [abc]*.txt" (fun () -> fileset { includes "*.txt"; excludes "[abc]*.txt" })
lsr "fileset inc *.txt exc a|b.txt" (fun () -> fileset { includes "*.txt"; excludes "a|b.txt" })
printfn "--- Path.parse results ---"
for m in ["[(]x[)].txt"; "(foo)/*.c"; "a|b.txt"; "(n:*)/x.c"; "a\\b\\*.c"; "[a/b]*.c"] do printfn "%-14s -> %A" m (Xake.Path.parse m)
printfn "--- fileset with basedir containing ( and [ ---"
let weird = Path.Combine(jobTmp, "w(1)[2]")
Directory.CreateDirectory weird |> ignore
File.WriteAllText(Path.Combine(weird, "x.c"), ""); File.WriteAllText(Path.Combine(weird, "y.h"), "")
let lsw label fs =
    try
        let (Filelist l) = Fileset.toFileList weird fs
        printfn "%-34s -> [%s]" label (l |> List.map (File.getFullName >> Path.GetFileName) |> List.sort |> String.concat " ")
    with e -> printfn "%-34s -> EXN %s: %s" label (e.GetType().Name) e.Message
lsw "root w(1)[2]: !! *.*" (!! "*.*")
lsw "root w(1)[2]: !! *.* -- *.h" (!! "*.*" -- "*.h")
lsw "root w(1)[2]: !! *.* -- x.c" (!! "*.*" -- "x.c")
lsw "root w(1)[2]: !! *.* -- *.c" (!! "*.*" -- "*.c")

printfn "--- extra: wildcard + class in include vs exclude; phony names (root empty) ---"
inc "[(]*.txt"
inc "[a]*.txt"
exc "[(]*.txt"
exc "[x]*.txt"
printfn "phony lock::(name:*) vs lock::foo -> %s" (tryM "lock::(name:*)" "" "lock::foo")
printfn "phony a|b vs xb -> %s" (tryM "a|b" "" "xb")
printfn "phony a|b vs a -> %s" (tryM "a|b" "" "a")
printfn "phony a|b vs c -> %s" (tryM "a|b" "" "c")
printfn "phony build(x) vs build(x) -> %s" (tryM "build(x)" "" "build(x)")
printfn "phony build[x] vs build[x] -> %s" (tryM "build[x]" "" "build[x]")
printfn "phony build[x] vs buildx -> %s" (tryM "build[x]" "" "buildx")
printfn "phony a\\b vs a/b -> %s" (tryM "a\\b" "" "a/b")
printfn "phony a\\b vs a\\b -> %s" (tryM "a\\b" "" "a\\b")
printfn "rule [x]/*.c vs x/a.c -> %s" (tryM "[x]/*.c" "/proj" "/proj/x/a.c")
printfn "rule [x]/*.c vs [x]/a.c -> %s" (tryM "[x]/*.c" "/proj" "/proj/[x]/a.c")
printfn "rule a||b.c (empty alternative) vs z.c -> %s" (tryM "a||b.c" "/proj" "/proj/z.c")
