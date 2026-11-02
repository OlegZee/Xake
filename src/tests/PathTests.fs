module ``Path tests``

open Xake
open NUnit.Framework

// Rewritten for 4.0 (Phase 4a): `Path.matches` and `Path.matchGroups` are adapters over the
// pattern module (docs/features/pattern-module/spec.md 8.3). Changes against 3.x:
// - the target path: `\` is a separator only on Windows (D37), so the paths below are written
//   with `/`; a `\` in a pattern is still a separator, deprecated (section 3 item 2, XP110);
// - `\[` is an escape (item 1), so `c:\[(]...` is rewritten with `/` before the class;
// - case: one comparer per OS (item 25; 3.x `Path.matches` ignored case everywhere);
// - an empty pattern is an error (item 35), `..` after a wildcard too (item 21);
// - a rule's `**/` takes zero directories (item 12).

[<TestCase("c:\\**\\*.*", "c:/", ExpectedResult = false)>]
[<TestCase("c:\\**\\*.*", "",     ExpectedResult = false)>]
[<TestCase("c:\\**\\*.*", "c:/a.c",   ExpectedResult = true)>]
[<TestCase("c:\\**\\", "c:/a.c",      ExpectedResult = false)>]
[<TestCase("c:\\*\\?.c", "c:/!/a.c", ExpectedResult = true)>]
[<TestCase("c:\\*\\?.c*", "c:/!/a.c",ExpectedResult = true)>]

[<TestCase("c:\\a.c", "d:/a.c",  ExpectedResult = false)>]
[<TestCase("c:\\*.c", "d:/a.c",  ExpectedResult = false)>]
[<TestCase("c:\\*.c", "c:/a.c",  ExpectedResult = true)>]
// item 1: `\[` is an escape in 4.0; `[(]` is the literal '(' (XP105), `\(` the spelling without a warning
[<TestCase("c:/[(]*[)].c", "c:/(a).c",  ExpectedResult = true)>]
[<TestCase("c:/[(]*.c", "c:/(a.c",  ExpectedResult = true)>]
[<TestCase("c:/\\(*\\).c", "c:/(a).c",  ExpectedResult = true)>]

[<TestCase("c:\\abc\\*.c", "c:/a.c",           ExpectedResult = false)>]
[<TestCase("c:\\abc\\*.c", "c:/def/a.c",      ExpectedResult = false)>]
[<TestCase("c:\\abc\\*.c", "c:/abc/def/a.c", ExpectedResult = false)>]
[<TestCase("c:\\abc\\*.c", "c:/abc/a.c",      ExpectedResult = true)>]

[<TestCase("c:\\*\\*.c", "c:/abc/a.c",        ExpectedResult = true)>]
[<TestCase("c:\\*\\*.c", "c:/abc/def/a.c",   ExpectedResult = false)>]
[<TestCase("c:\\**\\*.c", "c:/abc/def/a.c",  ExpectedResult = true)>]
[<TestCase("c:/abc/../def/a.c", "c:/def/a.c", ExpectedResult = true)>]
[<TestCase("c:/def/a.c", "c:/abc/../def/a.c", ExpectedResult = true)>]
let MaskTests (m,t) = Path.matches m "" t

/// item 35: an empty pattern is an error (3.x: matched only the empty path)
[<Test>]
let ``empty mask is an error`` () =
    let e = Assert.Throws<PatternAst.PatternException>(fun () -> Path.matches "" "" "aa" |> ignore)
    StringAssert.Contains("XP018", e.Message)

/// item 25: one comparer per OS, ignore-case on Windows only (3.x: ignore-case on every OS)
[<Test>]
let ``mask case follows the OS`` () =
    Assert.AreEqual(not Env.isUnix, Path.matches "c:\\a.c" "" "C:/A.c")

[<TestCase("../subd1/a.ss", "C:/projects/Xake/bin/Debug/subd1", "C:/projects/Xake/bin/Debug/subd1/../subd1/a.ss", ExpectedResult = true)>]
[<TestCase("subd2/a.ss", "C:/projects/Xake/bin/Debug/subd1", "C:/projects/Xake/bin/Debug/subd1/subd2/a.ss", ExpectedResult = true)>]

let MaskTests3(m,root,t) = Path.matches m root t


[<TestCase("c:\\*\\*.c", "c:/abc/def/../a.c",  ExpectedResult = true)>]
[<TestCase("c:\\*.c", "c:/abc/../a.c",          ExpectedResult = true)>]

[<TestCase("c:/abc/../*.c", "c:/a.c",          ExpectedResult = true)>]
[<TestCase("c:/abc/def/../../*.c", "c:/a.c", ExpectedResult = true)>]
[<TestCase("c:/abc/../../*.c", "c:/a.c",      ExpectedResult = false)>]
[<TestCase("c:/abc/../*.c", "c:/abc/../a.c", ExpectedResult = true)>]

let MaskWithParent(m,t) = Path.matches m "" t

/// item 21: `..` after a wildcard is an error (3.x: a parent step, `c:\abc\**\..\*.c` matched `c:\a.c`)
[<Test>]
let ``parent step after a wildcard is an error`` () =
    let e = Assert.Throws<PatternAst.PatternException>(fun () -> Path.matches "c:/abc/**/../*.c" "" "c:/a.c" |> ignore)
    StringAssert.Contains("XP012", e.Message)

let omitNumberedGroups (groupName: string, _) = not <| System.Char.IsDigit groupName.[0]

[<TestCase("(arch:*)/(platform:*)/autoexec.(ext:*)", "x86/win/autoexec.bat", ExpectedResult = "arch-x86 platform-win ext-bat")>]
[<TestCase("(arch:*)-(platform:*)-autoexec.(ext:*)", "x86-win-autoexec.bat", ExpectedResult = "arch-x86 platform-win ext-bat")>]
[<TestCase("(filename:(arch:*)-(platform:*)-autoexec).(ext:*)", "x86-win-autoexec.bat", ExpectedResult = "filename-x86-win-autoexec arch-x86 platform-win ext-bat")>]
[<TestCase("(apl:*/*)/autoexec.*", "x86/win/autoexec.bat", ExpectedResult = "apl-x86/win")>]
[<TestCase("(path:bin/**)/file.o", "bin/x86/win/3.11wg/file.o", ExpectedResult = "path-bin/x86/win/3.11wg")>]

let MatchGroupsInRule(m,t) =
    Path.matchGroups m "" t
    |> function
    | Some list ->
        list
        |> List.filter omitNumberedGroups
        |> List.map (fun (n,v) -> sprintf "%s-%s" n v)
        |> String.concat " "
    | None -> ""

[<TestCase("mlib.dll", "c:/mlib.dll", ExpectedResult = true)>]
[<TestCase("mli?.dll", "c:/mlib.dll", ExpectedResult = true)>]
[<TestCase("mli?.dll", "c:/mli.dll", ExpectedResult = false)>]
[<TestCase("*/mlib.dll", "c:/abc/mlib.dll", ExpectedResult = true)>]
[<TestCase("x86/win/mlib.*", "c:/x86/win/mlib.dll", ExpectedResult = true)>]
[<TestCase("*/*/mlib.*", "c:/x86/win/mlib.dll", ExpectedResult = true)>]
[<TestCase("*/*/mlib.*", "c:/123/x86/win/mlib.dll", ExpectedResult = false)>]

let MatchRule(m,t) =
    Path.matchGroups m "c:/" t
    |> Option.map (List.filter omitNumberedGroups) |> Option.isSome

[<TestCase("**/mlib.dll", "c:/abc/mlib.dll", ExpectedResult = true)>]
[<TestCase("**/mlib.dll", "c:/abc/def/mlib.dll", ExpectedResult = true)>]
// item 12: a rule's `**/` takes zero directories in 4.0 (3.x: false; XP119 on this evidence)
[<TestCase("**/mlib.dll", "c:/mlib.dll", ExpectedResult = true)>]
// the 4.0 spelling of the 3.x meaning: one or more directories (D12)
[<TestCase("*/**/mlib.dll", "c:/mlib.dll", ExpectedResult = false)>]
[<TestCase("*/**/mlib.dll", "c:/abc/def/mlib.dll", ExpectedResult = true)>]

let RecurseInRule(m,t) =
    Path.matchGroups m "c:/" t
    |> Option.map (List.filter omitNumberedGroups) |> Option.isSome

/// C7: `matchGroups` keeps the 3.x keys: "0" (the whole path), the positional numbers of every
/// wildcard at any depth, then the named groups in pattern order.
[<Test>]
let ``matchGroups keeps the 3.x keys`` () =
    let groups = Path.matchGroups "gen/*/(name:*)-?.txt" "" "gen/a/b-c.txt" |> Option.get
    Assert.AreEqual([ "0", "gen/a/b-c.txt"; "1", "a"; "2", "b"; "3", "c"; "name", "b" ], groups)
