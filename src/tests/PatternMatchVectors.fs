/// Conformance vectors for the matcher stage (PR 3) of the pattern module
/// (docs/features/pattern-module/spec.md sections 3, 5, 7, 8, 9, 11; decisions D1-D35 in brief.md).
/// DATA only: the tables below are driven by PatternMatchVectorTests.fs. Matching and formatting
/// only; no scanner (PR 4: trees, pruning, the explicit-name rule, evidence warnings of a scan).
///
/// ## Tables
///
/// | table               | id prefix | what                                                          |
/// |---------------------|-----------|---------------------------------------------------------------|
/// | `ruleMatches`       | `RM-`     | a rule pattern, its anchor (project root), a target path      |
/// | `maskMatches`       | `MM-`     | a mask, its base directory, one path, file or directory       |
/// | `formats`           | `FO-`     | `format named positional tokens`                              |
/// | `formatMatchesV`    | `FM-`     | `formatMatches matches pattern`                               |
/// | `multiOutputs`      | `MO-`     | a multi-output rule (`*..>`): the outputs named from a match  |
/// | `selections`        | `RS-`     | several rules, one target: which rule wins (5.8)              |
///
/// ## Conventions
///
/// - `Os` selects the comparer of D11 / spec 5.4: `Unix` ordinal, `Windows` ordinal-ignore-case
///   and `\` in a **path** is a separator; `Any` means the vector must hold in both modes. The
///   mode is an explicit argument of the matcher under test, so every vector runs on every host.
/// - Paths (`Target`, `Path`) are relative to the anchor (`Root`, `Base`) unless absolute (a
///   leading `/` or `\`, or `X:`); the driver joins them with `/`. A phony anchor is `""`.
/// - A rule `Match` lists **every** capture except `"0"`: the named groups (all of them, also an
///   unmatched one, as `""`) and the positional numbers `"1"`, `"2"`, ... of every `*`, `**`, `?`
///   at any depth (5.6). `"0"` is checked by the driver: the full target path with `\` turned
///   into `/` in Windows mode (C7; `measurements-windows.md` W2).
/// - `Warnings` of a rule match are the **evidence** codes the match produces (XP119 only, 7.3);
///   parse-time codes (XP105, XP110, XP112, XP117) belong to the parser vectors and are not
///   repeated here.
/// - `Note` gives the source and the 3.x result: "unchanged", or "changed in 4.0 (item N)" with
///   the 3.x result (spec section 3 numbering). "3.x:" names `matchGroups` (the rule matcher) and
///   `Path.matches` where they differ, with the OS where it was measured (`measurements-engine.md`
///   = macOS, E-cases; `measurements-windows.md` = Windows, E/W-cases). A note starting "SPEC?"
///   marks a reading the spec does not settle; the expected value is this file's best reading.
/// - Mask vectors check the matcher side of includes and excludes only (a mask and one path, no
///   tree): one meaning for both roles (D9). A mask without a trailing `/` matches files, with it
///   directories. What the scanner does with a match (prune, list) is PR 4.
module PatternMatchVectors

/// The comparer / separator mode (D11, spec 5.4).
[<RequireQualifiedAccess>]
type Os = Unix | Windows | Any

// =================================================================================================
// (a) Rule matching (spec 5.1, 5.5, 5.6, 9.3; Path.matchGroups)
// =================================================================================================

type RuleExpect =
    | NoMatch
    | Match of captures: (string * string) list

type RuleVector =
    { Id: string
      Section: string
      Os: Os
      Root: string
      Pattern: string
      Target: string
      Expect: RuleExpect
      Warnings: string list
      Note: string }
    override v.ToString() = v.Id

let private rv os root id section pattern target expect warnings note =
    { Id = id; Section = section; Os = os; Root = root; Pattern = pattern; Target = target
      Expect = expect; Warnings = warnings; Note = note }

/// root /p, any OS
let private ra = rv Os.Any "/p"
/// root /p, Unix comparer
let private ru = rv Os.Unix "/p"
/// root /p, Windows comparer
let private rw = rv Os.Windows "/p"
/// phony: root "", any OS
let private rp = rv Os.Any ""
let private no = NoMatch
let private m (captures: (string * string) list) = Match captures
let private xp119 = [ "XP119" ]

let ruleMatches : RuleVector list = [
    // ---- literals and one-segment wildcards (5.1) --------------------------------------------------
    ra "RM-001" "5.1" "src/a.cs" "src/a.cs" (m []) [] "unchanged"
    ra "RM-002" "5.1" "src/a.cs" "src/b.cs" no [] "unchanged"
    ra "RM-003" "5.1" "*.cs" "a.cs" (m [ "1", "a" ]) [] "unchanged"
    ra "RM-004" "5.1" "*.cs" "src/a.cs" no [] "unchanged: '*' does not cross '/'"
    ra "RM-005" "5.1" "*.cs" ".cs" (m [ "1", "" ]) [] "unchanged: '*' takes zero characters"
    rv Os.Any "c:/" "RM-006" "5.1" "mli?.dll" "c:/mlib.dll" (m [ "1", "b" ]) [] "unchanged; PathTests.MatchRule"
    rv Os.Any "c:/" "RM-007" "5.1" "mli?.dll" "c:/mli.dll" no [] "unchanged; PathTests.MatchRule: '?' is exactly one character"
    rv Os.Any "c:/" "RM-008" "5.1" "*/mlib.dll" "c:/abc/mlib.dll" (m [ "1", "abc" ]) [] "unchanged; PathTests.MatchRule"
    rv Os.Any "c:/" "RM-009" "5.1" "x86/win/mlib.*" "c:/x86/win/mlib.dll" (m [ "1", "dll" ]) [] "unchanged; PathTests.MatchRule"
    rv Os.Any "c:/" "RM-010" "5.1" "*/*/mlib.*" "c:/x86/win/mlib.dll" (m [ "1", "x86"; "2", "win"; "3", "dll" ]) [] "unchanged; PathTests.MatchRule"
    rv Os.Any "c:/" "RM-011" "5.1" "*/*/mlib.*" "c:/123/x86/win/mlib.dll" no [] "unchanged; PathTests.MatchRule"
    rv Os.Any "c:/" "RM-012" "5.1" "mlib.dll" "c:/mlib.dll" (m []) [] "unchanged; PathTests.MatchRule"
    ra "RM-013" "5.6" "a?*b" "axb" (m [ "1", "x"; "2", "" ]) [] "unchanged: '?' takes one, '*' the rest"
    ra "RM-014" "5.1" "a?*b" "ab" no [] "unchanged"
    ra "RM-015" "5.1" "?" "x" (m [ "1", "x" ]) [] "unchanged"
    ra "RM-016" "5.1" "?" "xy" no [] "unchanged"
    ra "RM-017" "5.1" "x.y+z!=w$^" "x.y+z!=w$^" (m []) [] "unchanged: regex metacharacters are plain characters"
    ra "RM-018" "5.1" "x.y+z!=w$^" "xay+z!=w$^" no [] "unchanged: '.' is a literal dot"
    ra "RM-019" "5.1" "a b/c d.txt" "a b/c d.txt" (m []) [] "unchanged: spaces are literal"
    ra "RM-020" "5.1" "a,b" "a,b" (m []) [] "unchanged: ',' outside braces is a character"
    ra "RM-021" "9.3" "*.txt" "a\nb.txt" (m [ "1", "a\nb" ]) [] "C1: '*' matches a newline (Linux names may hold one); 3.x the same ([^/\\\\]*)"
    ra "RM-022" "9.3" "**" "x/a\nb" (m [ "1", "x/a\nb" ]) [] "C1: '**' agrees with '*' on a newline (Singleline); 3.x '.*' stopped at the newline, no match"
    ra "RM-023" "5.1" "a.c" "xa.c" no [] "unchanged: anchored at the root, whole path"
    ra "RM-024" "5.1" "a.c" "a.c.bak" no [] "unchanged: anchored at the end"

    // ---- greedy, leftmost values (5.6) ---------------------------------------------------------------
    ra "RM-025" "5.6" "(a:*)-(b:*)" "x-y-z" (m [ "a", "x-y"; "b", "z"; "1", "x-y"; "2", "z" ]) [] "unchanged: 5.6 greedy leftmost"
    ra "RM-026" "5.6" "*-*" "x-y-z" (m [ "1", "x-y"; "2", "z" ]) [] "unchanged"
    ra "RM-027" "5.6" "*.*" "a.b.c" (m [ "1", "a.b"; "2", "c" ]) [] "unchanged"
    ra "RM-028" "5.1" "*.*" "README" no [] "unchanged for rules: the '*.*' listing quirk (item 15) was include-only"

    // ---- named groups (5.6, example 1, PathTests, XakeScriptTests) ---------------------------------
    ra "RM-030" "11" "out/(cfg:*)/(fwk:*)/(name:*).dll" "out/Release/net8.0/Core.dll"
        (m [ "cfg", "Release"; "fwk", "net8.0"; "name", "Core"; "1", "Release"; "2", "net8.0"; "3", "Core" ]) [] "unchanged; example 1"
    ra "RM-031" "11" "out/(cfg:*)/(fwk:*)/(name:*).dll" "out/Release/Core.dll" no [] "unchanged"
    rp "RM-032" "5.6" "(arch:*)/(platform:*)/autoexec.(ext:*)" "x86/win/autoexec.bat"
        (m [ "arch", "x86"; "platform", "win"; "ext", "bat"; "1", "x86"; "2", "win"; "3", "bat" ]) [] "unchanged; PathTests.MatchGroupsInRule"
    rp "RM-033" "5.6" "(arch:*)-(platform:*)-autoexec.(ext:*)" "x86-win-autoexec.bat"
        (m [ "arch", "x86"; "platform", "win"; "ext", "bat"; "1", "x86"; "2", "win"; "3", "bat" ]) [] "unchanged; PathTests.MatchGroupsInRule"
    rp "RM-034" "5.6" "(filename:(arch:*)-(platform:*)-autoexec).(ext:*)" "x86-win-autoexec.bat"
        (m [ "filename", "x86-win-autoexec"; "arch", "x86"; "platform", "win"; "ext", "bat"; "1", "x86"; "2", "win"; "3", "bat" ]) [] "unchanged; PathTests.MatchGroupsInRule (nested)"
    rp "RM-035" "5.6" "(apl:*/*)/autoexec.*" "x86/win/autoexec.bat"
        (m [ "apl", "x86/win"; "1", "x86"; "2", "win"; "3", "bat" ]) [] "unchanged for matchGroups; PathTests.MatchGroupsInRule; 3.x Path.matches threw RegexParseException on a group holding '/' (E10, macOS and Windows)"
    rp "RM-036" "5.6" "(apl:*/*)/x" "a/b/c/x" no [] "unchanged; measurements E10 (macOS and Windows)"
    rp "RM-037" "5.5" "(path:bin/**)/file.o" "bin/x86/win/3.11wg/file.o"
        (m [ "path", "bin/x86/win/3.11wg"; "1", "x86/win/3.11wg" ]) [] "unchanged for matchGroups; PathTests.MatchGroupsInRule"
    rp "RM-038" "5.5" "(path:bin/**)/file.o" "bin/file.o" (m [ "path", "bin"; "1", "" ]) xp119
        "changed in 4.0 (item 12): 3.x matchGroups None (E10, macOS and Windows); 9.3 globstar at a group edge"
    rp "RM-039" "5.5" "(path:bin/**)/file.o" "bin/x/y/file.o" (m [ "path", "bin/x/y"; "1", "x/y" ]) []
        "unchanged for matchGroups (E10: path = bin/x/y, 1 = x/y); 3.x Path.matches false (E10), item 12"
    rp "RM-040" "5.5" "(path:bin/**)/file.o" "binx/file.o" no [] "unchanged: the globstar holds whole segments"
    rp "RM-041" "5.6" "(dir:*)/(file:*).(ext:ss)" "out/abc.ss" (m [ "dir", "out"; "file", "abc"; "ext", "ss"; "1", "out"; "2", "abc" ]) [] "unchanged; XakeScriptTests: a group without a wildcard takes no number"
    rp "RM-042" "5.6" "(plat:*)-a.ss" "x86-a.ss" (m [ "plat", "x86"; "1", "x86" ]) [] "unchanged; XakeScriptTests 'Simple case'"
    rp "RM-043" "5.6" "(dir:*)/(plat:*)-a.ss" "subd1/x86-a.ss" (m [ "dir", "subd1"; "plat", "x86"; "1", "subd1"; "2", "x86" ]) [] "unchanged; XakeScriptTests"
    rp "RM-044" "5.6" "(root:*/*-a).ss" "subd1/x86-a.ss" (m [ "root", "subd1/x86-a"; "1", "subd1"; "2", "x86" ]) [] "unchanged; XakeScriptTests 'group across parts'"
    rp "RM-045" "5.6" "(name:(plat:*)-a.ss)" "x86-a.ss" (m [ "name", "x86-a.ss"; "plat", "x86"; "1", "x86" ]) [] "unchanged; XakeScriptTests 'Nested groups'"
    rp "RM-046" "11" "(name:[(]*).ss" "(abc.ss" (m [ "name", "(abc"; "1", "abc" ]) [] "unchanged; XakeScriptTests 'Escaped brackets', example 7 (XP105 at parse)"
    rp "RM-047" "5.6" "*/(plat:*)-a.ss" "subd1/x86-a.ss" (m [ "1", "subd1"; "plat", "x86"; "2", "x86" ]) [] "unchanged; XakeScriptTests getRuleMatch"
    rp "RM-048" "5.6" "gen/*/(name:*)-?.txt" "gen/x/lib-9.txt" (m [ "1", "x"; "name", "lib"; "2", "lib"; "3", "9" ]) [] "unchanged; 5.6 numbering at any depth; prototype PatternAstTests"
    rp "RM-049" "5.6" "**/(n:*)/?*.(ext:c?)" "a/b/core/x1.cs"
        (m [ "1", "a/b"; "n", "core"; "2", "core"; "3", "x"; "4", "1"; "ext", "cs"; "5", "s" ]) [] "unchanged for matchGroups; prototype PatternAstTests"
    rp "RM-050" "5.6" "lock::(name:*)::(cfg:*)" "lock::Acme.Core::Release" (m [ "name", "Acme.Core"; "cfg", "Release"; "1", "Acme.Core"; "2", "Release" ]) [] "unchanged; phony, build.fsc.fsx"
    rp "RM-051" "5.6" "test::(project:*)/(slice:*)" "test::core/unit" (m [ "project", "core"; "slice", "unit"; "1", "core"; "2", "unit" ]) [] "unchanged; phony, build.fsx"
    rp "RM-052" "5.6" "clean::(b:*)::(cfg:*)" "clean::acme::Release" (m [ "b", "acme"; "cfg", "Release"; "1", "acme"; "2", "Release" ]) [] "unchanged; RuleSpecificityTests"
    rp "RM-053" "5.6" "clean::(b:*)" "clean::acme::Release" (m [ "b", "acme::Release"; "1", "acme::Release" ]) [] "unchanged: '*' crosses ':' (RuleSpecificityTests picks the two-group rule by specificity, RS-006)"
    rp "RM-054" "5.6" "clean::(b:*)::(cfg:*)" "clean::acme" no [] "unchanged; RuleSpecificityTests"
    rp "RM-055" "5.1" "main" "main" (m []) [] "unchanged; phony"
    rp "RM-056" "5.6" "db:seed-(table:*)" "db:seed-users" (m [ "table", "users"; "1", "users" ]) [] "unchanged; docs phony pattern"
    ra "RM-057" "5.6" "run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" "run/Debug/net8.0/Core/.xake-run.txt"
        (m [ "cfg", "Debug"; "fwk", "net8.0"; "name", "Core"; "1", "Debug"; "2", "net8.0"; "3", "Core" ]) [] "unchanged; build.fsx"
    ra "RM-058" "5.6" "src/(dir:*)/(p:*).fsproj" "src/Core/Other.fsproj" (m [ "dir", "Core"; "p", "Other"; "1", "Core"; "2", "Other" ]) [] "example 13: the 4.0 spelling of the rejected repeated name"
    ra "RM-059" "5.6" "(x1:*)/(_y:*)" "a/b" (m [ "x1", "a"; "_y", "b"; "1", "a"; "2", "b" ]) [] "D21: names with digits and '_'"

    // ---- globstar (5.5, item 12, examples 9 and 20) -------------------------------------------------
    ra "RM-060" "5.5" "**/x.dll" "x.dll" (m [ "1", "" ]) xp119 "changed in 4.0 (item 12): 3.x matchGroups None, Path.matches true (E7, macOS and Windows)"
    ra "RM-061" "5.5" "**/x.dll" "a/x.dll" (m [ "1", "a" ]) [] "unchanged (E7: 1 = a, macOS and Windows)"
    ra "RM-062" "5.5" "**/x.dll" "a/b/x.dll" (m [ "1", "a/b" ]) [] "unchanged"
    rv Os.Any "c:/" "RM-063" "5.5" "**/mlib.dll" "c:/mlib.dll" (m [ "1", "" ]) xp119 "changed in 4.0 (item 12): 3.x no match (PathTests.RecurseInRule pins it; rewritten)"
    rv Os.Any "c:/" "RM-064" "5.5" "**/mlib.dll" "c:/abc/def/mlib.dll" (m [ "1", "abc/def" ]) [] "unchanged; PathTests.RecurseInRule"
    ra "RM-065" "11" "**/*.dll" "a.dll" (m [ "1", ""; "2", "a" ]) xp119 "changed in 4.0 (item 12): example 9; 3.x no match"
    ra "RM-066" "11" "**/*.dll" "x/a.dll" (m [ "1", "x"; "2", "a" ]) [] "unchanged; example 9"
    ra "RM-067" "5.5" "**/b" "xb" no [] "unchanged: 5.5 table"
    ra "RM-068" "5.5" "a/**/b" "a/b" (m [ "1", "" ]) xp119 "changed in 4.0 (item 12): 3.x matchGroups no match (regex a/(.*)/b), Path.matches true (E10 'bin/**/file.o')"
    ra "RM-069" "5.5" "a/**/b" "a/x/b" (m [ "1", "x" ]) [] "unchanged"
    ra "RM-070" "5.5" "a/**/b" "a/x/y/b" (m [ "1", "x/y" ]) [] "unchanged"
    ra "RM-071" "5.5" "a/**/b" "ab" no [] "unchanged"
    ra "RM-072" "5.5" "a/**/b" "a/xb" no [] "unchanged"
    ra "RM-073" "5.5" "a/**" "a/f" (m [ "1", "f" ]) [] "unchanged"
    ra "RM-074" "5.5" "a/**" "a/x/f" (m [ "1", "x/f" ]) [] "unchanged"
    ra "RM-075" "5.5" "a/**" "a" no [] "unchanged: 'a/**' excludes 'a' itself (picomatch: matches 'a')"
    ra "RM-076" "5.5" "**" "a/b/c" (m [ "1", "a/b/c" ]) [] "unchanged"
    ra "RM-077" "5.5" "**" "f" (m [ "1", "f" ]) [] "unchanged"
    ra "RM-078" "5.1" "*/**/b" "x/b" (m [ "1", "x"; "2", "" ]) xp119
        "SPEC? D12 idiom: 3.x needed two directories here (regex ([^/]*)/(.*)/b), so XP119's literal trigger fires on the very spelling its text recommends; read here as warning"
    ra "RM-079" "5.5" "*/**/b" "x/y/b" (m [ "1", "x"; "2", "y" ]) [] "unchanged; 5.5 table"
    ra "RM-080" "5.5" "*/**/b" "b" no [] "unchanged; 5.5 table"
    ra "RM-081" "11" "*/**/file.o" "a/b/c/file.o" (m [ "1", "a"; "2", "b/c" ]) [] "unchanged; example 20"
    ra "RM-082" "11" "(path:*/**)/file.o" "a/file.o" (m [ "path", "a"; "1", "a"; "2", "" ]) xp119 "SPEC? as RM-078 (example 20: the idiom with a capture)"
    ra "RM-083" "11" "(path:*/**)/file.o" "a/b/file.o" (m [ "path", "a/b"; "1", "a"; "2", "b" ]) [] "unchanged; example 20"
    ra "RM-084" "11" "(path:*/**)/file.o" "file.o" no [] "unchanged; example 20"
    ra "RM-085" "5.5" "(p:**)/b" "b" (m [ "p", ""; "1", "" ]) xp119 "changed in 4.0 (item 12); 9.3 globstar first in a group"
    ra "RM-086" "5.5" "(p:**)/b" "x/b" (m [ "p", "x"; "1", "x" ]) [] "unchanged"
    ra "RM-087" "5.5" "(p:**)/b" "x/y/b" (m [ "p", "x/y"; "1", "x/y" ]) [] "unchanged"
    ra "RM-088" "5.5" "a/(p:**)" "a/f" (m [ "p", "f"; "1", "f" ]) [] "unchanged"
    ra "RM-089" "5.5" "a/(p:**)/b" "a/b" (m [ "p", ""; "1", "" ]) xp119 "changed in 4.0 (item 12)"
    ra "RM-090" "5.5" "**/**/b" "x/y/b" (m [ "1", "x/y" ]) []
        "SPEC? 5.5 says the second '**' captures \"\"; D34 collapses adjacent globstars in the AST (P-118), so there is no second wildcard and no key \"2\""
    ra "RM-091" "5.5" "**/**/b" "b" (m [ "1", "" ]) xp119 "D34 collapse; item 12"
    ra "RM-092" "5.5" "a**b" "axyb" (m [ "1", "xy" ]) [] "unchanged result; XP112 at parse"
    ra "RM-093" "5.5" "a**b" "a/x/b" no [] "changed in 4.0 (item 13): 3.x crossed '/' (regex a(.*)b, 1 = /x/)"
    ra "RM-094" "5.5" "**.cs" "a.cs" (m [ "1", "a" ]) [] "unchanged result; XP112 at parse"
    ra "RM-095" "5.5" "**.cs" "x/a.cs" no [] "changed in 4.0 (item 13): 3.x matched"
    ra "RM-096" "5.5" "src/**/bin/*.o" "src/bin/a.o" (m [ "1", ""; "2", "a" ]) xp119 "changed in 4.0 (item 12): 3.x no match"
    ra "RM-097" "5.6" "src/**/bin/*.o" "src/x/bin/y/bin/a.o" (m [ "1", "x/bin/y"; "2", "a" ]) [] "unchanged: greedy, the globstar takes the most"
    ra "RM-098" "9.2" "src/**/(name:*).fs" "src/a/b/c.fs" (m [ "1", "a/b"; "name", "c"; "2", "c" ]) [] "unchanged; spec 9.2 example"
    ra "RM-099" "9.2" "src/**/(name:*).fs" "src/c.fs" (m [ "1", ""; "name", "c"; "2", "c" ]) xp119 "changed in 4.0 (item 12)"
    ra "RM-100" "11" "(pat:**/*).dll" "dd/a.dll" (m [ "pat", "dd/a"; "1", "dd"; "2", "a" ]) [] "unchanged; example 3 (XakeScriptTests 'Masks')"
    ra "RM-101" "11" "(pat:**/*).dll" "a.dll" (m [ "pat", "a"; "1", ""; "2", "a" ]) xp119 "changed in 4.0 (item 12): 3.x no match"
    ra "RM-102" "4.3" "src/{**,lib}/*.cs" "src/lib/x.cs" (m [ "1", "lib"; "2", "x" ]) [] "changed in 4.0 (item 6): 3.x literal braces, no match; D34: '**' alone in an alternative is a globstar; the first alternative wins (leftmost)"
    ra "RM-103" "4.3" "src/{**,lib}/*.cs" "src/a/b/x.cs" (m [ "1", "a/b"; "2", "x" ]) [] "changed in 4.0 (item 6)"
    ra "RM-104" "4.3" "src/{**,lib}/*.cs" "src/x.cs" (m [ "1", ""; "2", "x" ]) xp119
        "SPEC? XP119 for a globstar inside braces: 3.x read the braces literally and matched nothing anyway, so the warning text is moot; read here as the literal trigger"
    ra "RM-105" "5.5" "**/obj/**" "a/obj/b/c" (m [ "1", "a"; "2", "b/c" ]) [] "unchanged"
    ra "RM-106" "5.5" "**/obj/**" "obj/x" (m [ "1", ""; "2", "x" ]) xp119 "changed in 4.0 (item 12)"

    // ---- dotfiles (5.3, D10) ------------------------------------------------------------------------
    ra "RM-110" "5.3" "*/*.cs" ".hidden/y.cs" (m [ "1", ".hidden"; "2", "y" ]) [] "unchanged; 3.x Path.matches true (E8, macOS and Windows)"
    ra "RM-111" "5.3" "*" ".gitignore" (m [ "1", ".gitignore" ]) [] "unchanged; 5.9: picomatch/bash skip it"
    ra "RM-112" "5.3" "?bc" ".bc" (m [ "1", "." ]) [] "unchanged"
    ra "RM-113" "5.3" "**/*.cs" "a/.git/x.cs" (m [ "1", "a/.git"; "2", "x" ]) [] "unchanged"
    ra "RM-114" "5.3" "[._]*" ".env" (m [ "1", "env" ]) [] "unchanged: a class matches a leading '.'"

    // ---- classes (5.1, 5.4, 9.3 M2) -----------------------------------------------------------------
    ra "RM-120" "5.1" "a[bc]d.txt" "abd.txt" (m []) [] "unchanged (3.x rule: a regex class); prototype EscapingTests"
    ra "RM-121" "5.1" "a[bc]d.txt" "a[bc]d.txt" no [] "unchanged; prototype EscapingTests"
    ra "RM-122" "5.1" "[a-z]*.c" "x1.c" (m [ "1", "1" ]) [] "unchanged: classes take no number"
    ra "RM-123" "5.1" "[a-z]*.c" "1x.c" no [] "unchanged"
    ru "RM-124" "5.4" "[a-z]*.c" "Q.c" no [] "unchanged on Unix (3.x matchGroups case-sensitive on macOS)"
    rw "RM-125" "5.4" "[a-z]*.c" "Q.c" (m [ "1", "" ]) [] "unchanged on Windows: classes follow the comparer (D11)"
    ru "RM-126" "5.4" "[A-Z].c" "q.c" no [] "unchanged"
    rw "RM-127" "5.4" "[A-Z].c" "q.c" (m []) [] "unchanged (3.x IgnoreCase on Windows)"
    ra "RM-128" "5.1" "[^a]*.c" "b.c" (m [ "1", "" ]) [] "unchanged (3.x regex negation)"
    ra "RM-129" "5.1" "[^a]*.c" "a.c" no [] "unchanged"
    ru "RM-130" "5.4" "[^a].c" "A.c" (m []) [] "unchanged: 'A' is not 'a' under the ordinal comparer"
    rw "RM-131" "5.4" "[^a].c" "A.c" no [] "unchanged: a negated class follows the comparer too ('A' equals 'a' on Windows)"
    ra "RM-132" "5.2" "[!a].c" "b.c" (m []) [] "changed in 4.0 (item 11): 3.x a class of '!' and 'a', no match; XP117 at parse"
    ra "RM-133" "5.2" "[!a].c" "!.c" (m []) [] "same result as 3.x ('!' was in the 3.x class, and is 'not a' in 4.0)"
    ra "RM-134" "5.2" "[!a].c" "a.c" no [] "changed in 4.0 (item 11): 3.x matched"
    ra "RM-135" "5.1" "x[^a]y" "x/y" no [] "5.1: a negated class never matches '/' (3.x regex [^a] did; Path.matches split the segments)"
    ra "RM-136" "9.3" "[]a].c" "].c" (m []) [] "9.3 M2: ']' first in a class is a literal"
    ra "RM-137" "9.3" "[]a].c" "a.c" (m []) [] "9.3 M2"
    ra "RM-138" "9.3" "[]a].c" "b.c" no [] "9.3 M2"
    ra "RM-139" "9.3" "[^]a].c" "].c" no [] "9.3 M2"
    ra "RM-140" "9.3" "[^]a].c" "b.c" (m []) [] "9.3 M2"
    ra "RM-141" "9.3" @"[\-a].c" "-.c" (m []) [] "9.3 M2: an escaped '-'"
    ra "RM-142" "9.3" @"[\-a].c" "b.c" no [] "9.3 M2"
    ra "RM-143" "9.3" @"[a\-z].c" "m.c" no [] "9.3 M2: an escaped '-' forms no range"
    ra "RM-144" "9.3" @"[a\-z].c" "z.c" (m []) [] "9.3 M2"
    ra "RM-145" "9.3" "[a-z-0].c" "-.c" (m []) [] "a '-' after a range is a literal"
    ra "RM-146" "9.3" "[a-z-0].c" "0.c" (m []) [] "4.1"
    ra "RM-147" "9.3" "[a-z-0].c" "5.c" no [] "4.1"
    ra "RM-148" "9.3" "[*?,{}()|].c" "(.c" (m []) [] "metacharacters are plain inside a class"
    ra "RM-149" "9.3" "[-[]x" "[x" (m []) [] "9.3 M2: '-[' is no .NET class subtraction"
    ra "RM-150" "7.3" "lib[x].dll" "libx.dll" (m []) [] "unchanged (item 10, XP105 at parse)"
    ra "RM-151" "7.3" "lib[x].dll" "lib[x].dll" no [] "unchanged (3.x regex class)"
    ra "RM-152" "11" "[(]*.ss" "(abc.ss" (m [ "1", "abc" ]) [] "unchanged; example 7"
    ra "RM-153" "11" @"\(*.ss" "(abc.ss" (m [ "1", "abc" ]) [] "changed spelling (item 1): example 7, same match, no warning"
    ra "RM-154" "5.7" "a[*]b" "a*b" (m []) [] "D34: the escape idiom is a literal"
    ra "RM-155" "5.7" "a[*]b" "axb" no [] "D34"
    ra "RM-156" "5.7" "a[?]b" "a?b" (m []) [] "D34"
    rw "RM-157" "5.4" "[a-c]x" "Bx" (m []) [] "a range follows the comparer on Windows"
    ru "RM-158" "9.3" @"a[\\]b" @"a\b" (m []) []
        "SPEC? the literal backslash idiom against a Unix name holding '\\': read here as matching (on Unix '\\' in a path is a name character, see RM-255)"

    // ---- braces (5.1, item 6) -----------------------------------------------------------------------
    ra "RM-160" "5.1" "{a,b}" "a" (m []) [] "changed in 4.0 (item 6): 3.x literal braces, no match"
    ra "RM-161" "5.1" "{a,b}" "c" no [] "item 6"
    ra "RM-162" "5.1" "{a,b}" "{a,b}" no [] "changed in 4.0 (item 6): 3.x matched the literal text"
    ra "RM-163" "5.1" "*.{dll,pdb}" "x.pdb" (m [ "1", "x" ]) [] "changed in 4.0 (item 6)"
    ra "RM-164" "5.1" "*.{dll,pdb}" "x.exe" no [] "item 6"
    ra "RM-165" "5.1" "{a/b,c}.txt" "a/b.txt" (m []) [] "an alternative may hold '/'"
    ra "RM-166" "5.1" "{a/b,c}.txt" "c.txt" (m []) [] "5.1"
    ra "RM-167" "5.1" "{a/b,c}.txt" "a.txt" no [] "5.1"
    ra "RM-168" "5.1" "{a/,b}c" "a/c" (m []) [] "D29: a separator inside the pattern is an ordinary one"
    ra "RM-169" "5.1" "{a/,b}c" "bc" (m []) [] "D29"
    ra "RM-170" "5.1" "{a,{b,c}}.txt" "c.txt" (m []) [] "nested braces"
    ra "RM-171" "5.1" "x{,.bak}" "x" (m []) [] "an empty alternative"
    ra "RM-172" "5.1" "x{,.bak}" "x.bak" (m []) [] "an empty alternative"
    ra "RM-173" "5.6" "src/{*.cs,*.fs}" "src/a.fs" (m [ "1", ""; "2", "a" ]) [] "5.6: wildcards in braces are numbered in text order; the unmatched alternative holds \"\""
    ra "RM-174" "5.6" "src/{*.cs,*.fs}" "src/a.cs" (m [ "1", "a"; "2", "" ]) [] "5.6"
    ra "RM-175" "5.1" "{a,b}{c,d}" "bd" (m []) [] "adjacent braces"
    ra "RM-176" "5.6" "{*,abc}.cs" "abc.cs" (m [ "1", "abc" ]) [] "leftmost alternative first"
    ra "RM-177" "5.6" "{a,(n:b),c}-(m:*)" "a-x" (m [ "n", ""; "m", "x"; "1", "x" ]) []
        "SPEC? a named group in an unmatched alternative: 5.6 says \"\" for wildcards only; read here as present with \"\" (as the 3.x regex reported unmatched groups)"
    ra "RM-178" "5.6" "{a,(n:b),c}-(m:*)" "b-x" (m [ "n", "b"; "m", "x"; "1", "x" ]) [] "5.6"
    ra "RM-179" "11" "(x:{a,b})-{1,2}" "b-2" (m [ "x", "b" ]) [] "example 13: the replacement for a repeated name"
    ra "RM-180" "11" "signed/(name:*).(ext:{dll,exe,nupkg})" "signed/a.b.nupkg" (m [ "name", "a.b"; "1", "a.b"; "ext", "nupkg" ]) [] "example 8: the 4.0 spelling; greedy name"
    ra "RM-181" "11" "signed/(name:*).(ext:{dll,exe,nupkg})" "signed/a.zip" no [] "example 8"
    ra "RM-182" "5.1" "{[,],x}" "," (m []) [] "a ',' in a class inside braces"
    ra "RM-183" "5.6" "{(n:b,c),d}" "b,c" (m [ "n", "b,c" ]) [] "I5: ',' inside a group is a character"

    // ---- escapes (item 1) ---------------------------------------------------------------------------
    ra "RM-185" "5.1" @"a\(b" "a(b" (m []) [] "changed in 4.0 (item 1): 3.x a separator then a regex '(' (RegexParseException)"
    rp "RM-186" "5.1" @"lit\(x\)/*.txt" "lit(x)/a.txt" (m [ "1", "a" ]) [] "unchanged from the prototype (PatternAstTests)"
    rp "RM-187" "5.1" @"lit\(x\)/*.txt" "litx/a.txt" no [] "prototype PatternAstTests"
    rp "RM-188" "5.1" @"out/(n:*)\|(m:*).txt" "out/x|y.txt" (m [ "n", "x"; "m", "y"; "1", "x"; "2", "y" ]) [] "prototype PatternAstTests"
    rp "RM-189" "5.1" @"a\|b.txt" "a|b.txt" (m []) [] "prototype PatternAstTests"
    rp "RM-190" "5.1" @"a\|b.txt" "a.txt" no [] "prototype PatternAstTests (3.x unescaped 'a|b.txt' matched it: item 3)"
    rp "RM-191" "5.1" @"c:/\(*\).c" "c:/(a).c" (m [ "1", "a" ]) [] "the 4.0 spelling of PathTests 'c:\\[(]*[)].c' (P-108)"
    ra "RM-192" "5.1" @"src\(x\)\*.cs" "src(x)/a.cs" (m [ "1", "a" ]) [] "changed in 4.0 (item 1): 3.x src/(x/)/*.cs; example 11 (XP110 at parse)"

    // ---- case (5.4, D11, item 25) -------------------------------------------------------------------
    ru "RM-200" "5.4" "src/*.cs" "Src/a.cs" no [] "3.x macOS: matchGroups None, Path.matches true (E9); item 25"
    rw "RM-201" "5.4" "src/*.cs" "Src/a.cs" (m [ "1", "a" ]) [] "unchanged on Windows: matchGroups Some, 1 = a (E9 Windows)"
    ru "RM-202" "5.4" "Src/*.cs" "src/A.cs" no [] "3.x macOS Path.matches true (E9); item 25"
    rw "RM-203" "5.4" "SRC/a.cs" "src/a.cs" (m []) [] "unchanged on Windows (E9)"
    rv Os.Unix "" "RM-204" "5.4" "c:/a.c" "C:/A.c" no [] "changed in 4.0 on Unix (item 25): 3.x Path.matches true on every OS (PathTests.MaskTests)"
    rv Os.Windows "" "RM-205" "5.4" "c:/a.c" "C:/A.c" (m []) [] "unchanged on Windows; PathTests.MaskTests"
    rw "RM-206" "5.4" "(n:*).CS" "a.cs" (m [ "n", "a"; "1", "a" ]) [] "unchanged on Windows"
    rw "RM-207" "5.4" "src/(n:*).cs" "SRC/Main.CS" (m [ "n", "Main"; "1", "Main" ]) [] "a capture keeps the path's spelling"
    rv Os.Unix "/P" "RM-208" "5.4" "*.c" "/p/a.c" no [] "the root compares with the same comparer"
    rv Os.Windows "/P" "RM-209" "5.4" "*.c" "/p/a.c" (m [ "1", "a" ]) [] "unchanged on Windows"

    // ---- roots (5.1 anchoring, 4.2 roots, item 29) --------------------------------------------------
    ra "RM-210" "5.1" "/opt/(n:*).so" "/opt/x.so" (m [ "n", "x"; "1", "x" ]) [] "unchanged: an absolute pattern is not joined to the root"
    ra "RM-211" "5.1" "/opt/(n:*).so" "/p/opt/x.so" no [] "unchanged"
    ra "RM-212" "5.1" "/a/b" "/a/b" (m []) [] "unchanged"
    rp "RM-213" "5.1" "c:/*.c" "c:/a.c" (m [ "1", "a" ]) [] "unchanged; a drive root (PathTests run on every OS)"
    rp "RM-214" "5.1" "c:/*.c" "d:/a.c" no [] "unchanged; PathTests.MaskTests"
    rv Os.Windows "" "RM-215" "5.1" @"c:\abc\*.c" @"c:\abc\a.c" (m [ "1", "a" ]) [] "unchanged; PathTests.MaskTests (XP110 at parse)"
    rv Os.Windows "" "RM-216" "5.1" @"c:\abc\*.c" @"c:\abc\def\a.c" no [] "unchanged; PathTests.MaskTests"
    rv Os.Windows "" "RM-217" "5.1" @"c:\*\*.c" @"c:\abc\a.c" (m [ "1", "abc"; "2", "a" ]) [] "unchanged; PathTests.MaskTests"
    rv Os.Windows "" "RM-218" "5.5" @"c:\**\*.c" @"c:\abc\def\a.c" (m [ "1", "abc/def"; "2", "a" ]) [] "unchanged; PathTests.MaskTests"
    rv Os.Windows "" "RM-219" "5.5" @"c:\**\*.*" @"c:\a.c" (m [ "1", ""; "2", "a"; "3", "c" ]) xp119 "PathTests.MaskTests: 3.x Path.matches true; matchGroups would refuse it (item 12)"
    rv Os.Windows "" "RM-220" "5.1" @"c:\*\?.c" @"c:\!\a.c" (m [ "1", "!"; "2", "a" ]) [] "unchanged; PathTests.MaskTests"
    rw "RM-221" "5.1" "C:/x/(n:*).dll" @"c:\x\A.dll" (m [ "n", "A"; "1", "A" ]) [] "a drive pattern against a backslash path, case-insensitive drive"
    rv Os.Windows @"C:\proj" "RM-222" "9.2" "out/(n:*).dll" @"C:\proj\out\a.dll" (m [ "n", "a"; "1", "a" ]) [] "unchanged; a root and a target with backslashes; \"0\" = C:/proj/out/a.dll"
    rv Os.Windows "c:/proj" "RM-223" "5.4" "out/*.dll" "C:/PROJ/out/a.dll" (m [ "1", "a" ]) [] "unchanged on Windows: the root is compared ignoring case"
    rw "RM-224" "4.2" "//srv/sh/(n:*).dll" @"\\srv\sh\a.dll" (m [ "n", "a"; "1", "a" ]) [] "D24: a UNC root matches on Windows; \"0\" = //srv/sh/a.dll"
    rw "RM-225" "4.2" "//srv/sh/(n:*).dll" "//SRV/sh/a.dll" (m [ "n", "a"; "1", "a" ]) [] "D24"
    ru "RM-226" "4.2" "//srv/sh/(n:*).dll" "//srv/sh/a.dll" no [] "D24: UNC roots parse on every OS and match only on Windows"
    rw "RM-227" "4.2" "//srv/sh/*.dll" @"\\srv\other\a.dll" no [] "D24"
    ra "RM-228" "5.1" "*.c" "/q/a.c" no [] "unchanged: a relative pattern is anchored at the root"
    rv Os.Any "/tmp/p(q" "RM-229" "9.2" "(n:*).c" "/tmp/p(q/a.c" (m [ "n", "a"; "1", "a" ]) [] "changed in 4.0 (item 29): 3.x put the root into the regex unescaped (RegexParseException); prototype EscapingTests"
    rv Os.Any "/tmp/p[q]" "RM-230" "9.2" "out/*.c" "/tmp/p[q]/out/a.c" (m [ "1", "a" ]) [] "changed in 4.0 (item 29): 3.x read '[q]' as a class and missed it"
    rv Os.Any "/tmp/p[q]" "RM-231" "9.2" "*.c" "/tmp/pq/a.c" no [] "changed in 4.0 (item 29): 3.x matched (the root's '[q]' was a class)"
    rv Os.Any "/tmp/p{q}" "RM-232" "9.2" "*.c" "/tmp/p{q}/a.c" (m [ "1", "a" ]) [] "item 29; prototype EscapingTests"
    rv Os.Any "/tmp/p|q" "RM-233" "9.2" "*.c" "/tmp/p|q/a.c" (m [ "1", "a" ]) [] "changed in 4.0 (item 29): 3.x an alternation over the whole regex"
    rv Os.Any "/tmp/a.b" "RM-234" "9.2" "*.c" "/tmp/axb/a.c" no [] "changed in 4.0 (item 29): 3.x the root's '.' was any character"
    rv Os.Any "/p/" "RM-235" "9.2" "a.c" "/p/a.c" (m []) [] "a root with a trailing separator"

    // ---- '..' and '.' (4.2, D34) ----------------------------------------------------------------------
    rv Os.Any "/p/proj" "RM-240" "4.2" "../out/(name:*).txt" "/p/out/lib.txt" (m [ "name", "lib"; "1", "lib" ]) [] "unchanged; ParentDirTargetTests: '..' collapsed with the root in the literal prefix (9.2)"
    rv Os.Any "/p/proj" "RM-241" "4.2" "a/../mid/(name:*).txt" "/p/proj/mid/x.txt" (m [ "name", "x"; "1", "x" ]) [] "unchanged; ParentDirTargetTests"
    ra "RM-242" "4.2" "./(n:*).c" "a.c" (m [ "n", "a"; "1", "a" ]) [] "unchanged"
    rp "RM-243" "4.2" "c:/abc/../*.c" "c:/a.c" (m [ "1", "a" ]) [] "unchanged; PathTests.MaskWithParent"
    rv Os.Windows "" "RM-244" "4.2" @"c:\abc\def\..\..\*.c" @"c:\a.c" (m [ "1", "a" ]) [] "unchanged; PathTests.MaskWithParent"
    rv Os.Windows "" "RM-245" "4.2" @"c:\abc\..\..\*.c" @"c:\a.c" no [] "unchanged; PathTests.MaskWithParent; D34: '..' above the root stays a literal segment"
    rp "RM-246" "9.2" "c:/*.c" "c:/abc/../a.c" no []
        "SPEC? a '..' in the target path: the engine passes full paths, so the matcher compares the path as given; 3.x Path.matches resolved it (PathTests.MaskWithParent: true)"
    rp "RM-247" "4.2" "c:/abc/../def/a.c" "c:/def/a.c" (m []) [] "unchanged; PathTests.MaskTests"

    // ---- separators (4.2, item 34, W1/W2) -------------------------------------------------------------
    ra "RM-250" "4.2" "a//b" "a/b" (m []) [] "changed in 4.0 (item 34): 3.x matchGroups None, Path.matches true (E1, macOS and Windows)"
    ra "RM-251" "4.2" "a//b" "a//b" no []
        "SPEC? a doubled separator on the path side is not collapsed (paths are normalized by File.make); 3.x matchGroups Some (E1)"
    ra "RM-252" "4.1" @"src\*.cs" "src/a.cs" (m [ "1", "a" ]) [] "unchanged meaning, XP110 at parse; 3.x Path.matches true (W2, Windows measured; macOS by source)"
    rw "RM-253" "4.1" @"src\*.cs" @"src\a.cs" (m [ "1", "a" ]) [] "unchanged; W2"
    rv Os.Windows "" "RM-254" "4.1" "src/(name:*).cs" @"src\a.cs" (m [ "name", "a"; "1", "a" ]) [] "unchanged: \"0\" reported with '/' (W2 Windows)"
    ru "RM-255" "9.2" "src/*.cs" @"src\a.cs" no []
        "SPEC? 9.2 'paths are normalized to /' read as Windows-only: on Unix '\\' is a name character; 3.x normalized '\\' on every OS (Path.normalizeSlashes) and matched"

    // ---- Unicode (D26, D27) ------------------------------------------------------------------------
    ra "RM-260" "5.9" "?.txt" "\U0001F600.txt" no [] "D26: '?' is one UTF-16 code unit"
    ra "RM-261" "5.9" "??.txt" "\U0001F600.txt" (m [ "1", "\uD83D"; "2", "\uDE00" ]) [] "D26: two code units, one per '?'"
    ra "RM-262" "5.4" "caf\u00E9*" "cafe\u0301.txt" no [] "D27: no normalization; an NFC literal against an NFD name"
    ra "RM-263" "5.4" "caf*" "cafe\u0301.txt" (m [ "1", "e\u0301.txt" ]) [] "D27"
    ra "RM-264" "5.6" "(\u00E9:*).x" "a.x" (m [ "\u00E9", "a"; "1", "a" ]) [] "D21: a Unicode group name"
]

// =================================================================================================
// (b) Mask matching against one path (spec 5.1 mask column, 5.3, 5.4, 5.5, 5.9)
// =================================================================================================

type MaskVector =
    { Id: string
      Section: string
      Os: Os
      Base: string
      Mask: string
      Path: string
      IsDirectory: bool
      Expect: bool
      Note: string }
    override v.ToString() = v.Id

let private mv os bse id section mask path isDirectory expect note =
    { Id = id; Section = section; Os = os; Base = bse; Mask = mask; Path = path
      IsDirectory = isDirectory; Expect = expect; Note = note }

/// base /p, any OS
let private ma = mv Os.Any "/p"
let private mu = mv Os.Unix "/p"
let private mw = mv Os.Windows "/p"
let private file = false
let private dir = true

let maskMatches : MaskVector list = [
    // ---- anchoring and files (5.1) ------------------------------------------------------------------
    ma "MM-001" "5.1" "*.cs" "a.cs" file true "unchanged"
    ma "MM-002" "5.9" "*.cs" "src/a.cs" file false "unchanged: no floating basename match (gitignore and minimatch matchBase: true)"
    ma "MM-003" "5.1" "*.cs" "a.cs" dir false "D9: a mask without a trailing '/' matches files only"
    ma "MM-004" "5.1" "src/*.cs" "src/a.cs" file true "unchanged"
    ma "MM-005" "5.5" "**/*.cs" "a.cs" file true "unchanged: zero segments (3.x scan the same, E7)"
    ma "MM-006" "5.5" "**/*.cs" "a/b/c.cs" file true "unchanged"
    ma "MM-007" "5.1" "src/*.cs" "/q/src/a.cs" file false "unchanged: anchored at the base dir"
    ma "MM-008" "5.1" "**/*" "x" dir false "D9: files only"
    ma "MM-009" "5.5" "**" "x" file true "unchanged"
    ma "MM-010" "5.5" "**" "a/b/c" file true "unchanged"

    // ---- dotfiles (5.3, D10) -------------------------------------------------------------------------
    ma "MM-011" "5.3" "**/*.cs" ".hidden/y.cs" file true "unchanged (E8, macOS and Windows); example 4"
    ma "MM-012" "5.3" "**/*.cs" "a/.git/x.cs" file true "unchanged; example 4"
    ma "MM-013" "5.3" "*/*.cs" ".hidden/y.cs" file true "unchanged (E8)"
    ma "MM-014" "5.9" "*" ".hidden" file true "unchanged; picomatch/bash dot:false skip it"
    ma "MM-015" "5.3" ".*" ".hidden" file true "unchanged"
    ma "MM-016" "5.3" ".*" "a.cs" file false "unchanged"
    ma "MM-017" "5.3" "**/.*/" "a/.git" dir true "changed in 4.0 at scan level (item 23): example 4, a directory mask"
    ma "MM-018" "5.3" "**/.*/" ".git" dir true "example 4"
    ma "MM-019" "5.3" "**/.*/" "a/.git" file false "D9: a trailing '/' matches directories only (3.x: a file named like '.git' was removed)"
    ma "MM-020" "5.3" "**/.*/**" "a/.git/x.cs" file true "changed in 4.0 (item 14): 3.x as an exclude removed nothing; example 4"
    ma "MM-021" "5.3" "**/.*/**/*" "a/.git/x/y.cs" file true "unchanged; the 3.x idiom (FilesetTests)"
    ma "MM-022" "5.3" "**/.*/**/*" "a/.git/x.cs" file true "unchanged"
    ma "MM-023" "5.3" "?bc" ".bc" file true "unchanged"

    // ---- '*.*' and the listing quirks (items 15, 16) -------------------------------------------------
    ma "MM-025" "5.2" "*.*" "README" file false "changed in 4.0 (item 15): 3.x include listed it (OS listing rule; XP114 on evidence, scanner); example 14"
    ma "MM-026" "5.2" "*.*" "a.txt" file true "unchanged; example 14"
    ma "MM-027" "5.2" "*.*" ".gitignore" file true "'*' takes zero characters before the '.'"
    ma "MM-028" "5.2" "*.rdl" "a.rdlx" file false "changed in 4.0 (item 16): the .NET Framework 3-letter extension quirk is gone"
    ma "MM-029" "5.2" "b?" "b" file false "changed in 4.0 (item 16): the legacy listing matched 'b?' to 'b' (measurements-dotnet 1)"
    ma "MM-030" "5.9" "*.rdlx" "sub/a.rdlx" file false "unchanged; example 5: anchored (gitignore: any depth)"
    ma "MM-031" "5.9" "*.rdlx" "a.rdlx" file true "unchanged; example 5"
    ma "MM-032" "5.9" "**/*.rdl*" "x/a.rdlx" file true "unchanged; example 5, FilesetTests"
    ma "MM-033" "5.9" "**/*.rdlx" "sub/a.rdlx" file true "unchanged; example 5: the Xake spelling of the gitignore rule"

    // ---- trailing '/' (D9, item 23, example 21) -------------------------------------------------------
    ma "MM-035" "5.1" "obj/" "obj" dir true "changed in 4.0 (item 23): 3.x dropped the '/' (E6, macOS and Windows: removed nothing)"
    ma "MM-036" "5.1" "obj/" "obj" file false "D9: a directory mask never matches a file"
    ma "MM-037" "5.1" "obj/" "obj/a.o" file false "D9: the matcher matches the directory; removing what is below it is the scanner's (6.2)"
    ma "MM-038" "5.1" "obj/" "src/obj" dir false "anchored at the base dir"
    ma "MM-039" "11" "**/obj/" "src/obj" dir true "example 21"
    ma "MM-040" "11" "**/obj/" "src/obj" file false "example 21: 'src/obj' is a file and is kept (3.x E6: '**/obj/' removed the file 'other/obj')"
    ma "MM-041" "11" "**/obj/" "obj" dir true "example 21"
    ma "MM-042" "5.5" "a/**/" "a" dir true "5.5: 'a' itself and every directory below"
    ma "MM-043" "5.5" "a/**/" "a/x/y" dir true "5.5"
    ma "MM-044" "5.5" "a/**/" "a/x" file false "5.5: files never match a directory mask"
    ma "MM-045" "4.2" "c:/*/" "c:/x" dir true "unchanged; FilesetTests"
    ma "MM-046" "4.2" "{a,b}/" "b" dir true "D29: the honest spelling of a directory alternation"
    ma "MM-047" "5.1" "*/" "x" dir true "unchanged"
    ma "MM-048" "5.1" "*/" "x" file false "unchanged"
    ma "MM-049" "11" "src/hermetic/**/" "src/hermetic" dir true "example 10"

    // ---- 'dir/**' (item 14, 5.5) ---------------------------------------------------------------------
    ma "MM-050" "5.5" "dir/**" "dir/a" file true "changed in 4.0 (item 14): 3.x include listed only directories, exclude matched nothing (E7, E6); XP113 on evidence (scanner)"
    ma "MM-051" "5.5" "dir/**" "dir/sub/b" file true "item 14"
    ma "MM-052" "5.5" "dir/**" "dir" dir false "5.5: 'a/**' excludes 'a' itself (3.x include listed 'dir', E7)"
    ma "MM-053" "5.5" "dir/**" "dir/sub" dir false "changed in 4.0 (item 14): 3.x include listed 'dir/sub' (E7)"
    ma "MM-054" "5.5" "dir/**/*" "dir/a" file true "unchanged (E7)"
    ma "MM-055" "5.5" "dir/**/*" "dir/sub/b" file true "unchanged (E7)"
    ma "MM-056" "5.5" "**/obj/**" "obj/a/b" file true "unchanged"
    ma "MM-057" "5.5" "a/**/b" "a/b" file true "unchanged: masks accepted zero segments in 3.x (E10 scan)"
    ma "MM-058" "11" "*/**/file.o" "file.o" file false "D12: the one-or-more idiom; example 20"
    ma "MM-059" "11" "*/**/file.o" "a/file.o" file true "D12"
    ma "MM-060" "11" "*/**/file.o" "a/b/file.o" file true "D12"
    ma "MM-061" "5.5" "a**b" "axb" file true "unchanged: masks read 'a**b' as 'a*b' in 3.x too"
    ma "MM-062" "5.5" "a**b" "a/x/b" file false "unchanged for masks"

    // ---- absolute masks, drives, UNC, '..' (D6, 4.2, example 22) -------------------------------------
    ma "MM-065" "11" "/bin/**" "/p/bin/a.dll" file false "unchanged: example 22, the file-system root (gitignore: the base dir); XP120 at evaluation (scanner)"
    ma "MM-066" "11" "/bin/**" "/bin/a.dll" file true "unchanged"
    ma "MM-067" "11" "/opt/lib/*.so" "/opt/lib/libx.so" file true "unchanged; example 22"
    ma "MM-068" "11" "/opt/lib/libx.so" "/opt/lib/libx.so" file true "unchanged; example 22"
    mw "MM-069" "5.4" "c:/rpt/**/e.rdl" @"C:\RPT\x\E.RDL" file true "unchanged on Windows; FilesetTests"
    ma "MM-070" "4.2" "c:/rpt/**/e.rdl" "c:/rpt/x/e.rdl" file true "unchanged"
    mv Os.Windows @"C:\p" "MM-071" "5.4" "*.cs" @"C:\p\a.cs" file true "a Windows base dir with backslashes"
    mv Os.Windows @"C:\p" "MM-072" "5.4" "*.cs" "c:/P/A.CS" file true "the base dir compares ignoring case on Windows"
    mw "MM-073" "4.2" "//server/share/*.dll" @"\\server\share\a.dll" file true "D24"
    mu "MM-074" "4.2" "//server/share/*.dll" "//server/share/a.dll" file false "D24: UNC roots match only on Windows"
    mv Os.Any "/p/proj" "MM-075" "4.2" "../lib/*.dll" "/p/lib/a.dll" file true "unchanged: '..' in the literal prefix (D34); FilesetTests '../jparsec/...'"
    ma "MM-076" "4.2" "./a.cs" "a.cs" file true "unchanged"
    ma "MM-077" "4.2" "a/./b" "a/b" file true "unchanged"
    ma "MM-078" "4.2" "a//b" "a/b" file true "unchanged for masks (E1 scan)"
    ma "MM-079" "4.1" @"src\*.cs" "src/a.cs" file true "unchanged meaning, XP110 at parse (W1 Windows; macOS by source)"
    mw "MM-080" "4.1" @"src\*.cs" @"src\a.cs" file true "unchanged (W1)"
    mw "MM-081" "11" @"C:/Program Files \(x86\)/Tool/*.dll" @"C:\Program Files (x86)\Tool\a.dll" file true "example 19: escapePath output"

    // ---- case (5.4, D11, D25, item 25, example 15) ---------------------------------------------------
    mu "MM-085" "5.4" "*.CS" "a.cs" file false "changed in 4.0 on macOS (item 25): 3.x found it through the .NET listing"
    mw "MM-086" "5.4" "*.CS" "a.cs" file true "unchanged on Windows"
    mu "MM-087" "5.4" "Dir2/**/*" "dir2/x.cs" file false "changed in 4.0 (item 25): 3.x literal exclude segments ignored case on every OS; example 15"
    mw "MM-088" "5.4" "Dir2/**/*" "dir2/x.cs" file true "unchanged on Windows"
    mu "MM-089" "5.4" "d*/**/*" "Dir2/e.cs" file false "unchanged; example 15 (FilesetTests: Linux keeps Dir2/e.cs)"
    mw "MM-090" "5.4" "d*/**/*" "Dir2/e.cs" file true "unchanged; example 15 (Windows removes it)"
    mu "MM-091" "5.4" "src/**" "SRC/a.cs" file false "D25: the probe-spelling trap of example 15 (the probe keeps 'SRC', the exclude is ordinal)"
    mu "MM-092" "5.4" "[a-z]*.cs" "Q.cs" file false "unchanged"
    mw "MM-093" "5.4" "[a-z]*.cs" "Q.cs" file true "D11: classes follow the comparer"
    mu "MM-094" "5.4" "src/*.cs" "Src/A.cs" file false "item 25: 3.x scan on macOS returned 'src/A.cs' (E9)"
    mw "MM-095" "5.4" "src/*.cs" "Src/A.cs" file true "unchanged (E9 Windows)"

    // ---- classes, escapes, braces (items 6, 8, 9) ----------------------------------------------------
    ma "MM-100" "11" "sub[1]/*.c" "sub1/s.c" file true "changed in 4.0 (item 9): 3.x the literal directory 'sub[1]'; example 6 (XP106 at parse)"
    ma "MM-101" "11" "sub[1]/*.c" "sub[1]/s.c" file false "changed in 4.0 (item 9): 3.x listed it; example 6"
    ma "MM-102" "11" @"sub\[1\]/*.c" "sub[1]/s.c" file true "unchanged result, no warning; example 6"
    ma "MM-103" "5.2" "a[bc]d.txt" "abd.txt" file true "changed in 4.0 (item 8): 3.x include read the literal text; XP104 on evidence (scanner)"
    ma "MM-104" "5.2" "a[bc]d.txt" "a[bc]d.txt" file false "changed in 4.0 (item 8): 3.x matched the literal text"
    ma "MM-105" "5.2" "[!a]*.c" "b.c" file true "changed in 4.0 (item 11 / 8 by role)"
    ma "MM-106" "5.2" "[!a]*.c" "a.c" file false "changed in 4.0"
    ma "MM-107" "5.7" "a[*]b" "a*b" file true "D34: the escape idiom (escapePath output)"
    ma "MM-108" "5.7" "a[*]b" "axb" file false "D34"
    ma "MM-109" "5.2" "src/{a,b}/*.cs" "src/b/x.cs" file true "changed in 4.0 (item 6): 3.x literal braces"
    ma "MM-110" "5.2" "src/{a,b}/*.cs" "src/c/x.cs" file false "item 6"
    ma "MM-111" "5.2" "src/{a,b}/*.cs" "src/{a,b}/x.cs" file false "changed in 4.0 (item 6): 3.x found the literal directory"
    ma "MM-112" "5.9" "*.{js,ts}" "a.ts" file true "picomatch-style braces (gitignore, MSFG, Ant have none)"
    ma "MM-113" "11" "src/{a/,b}x.cs" "src/a/x.cs" file true "example 23"
    ma "MM-114" "11" "src/{a/,b}x.cs" "src/bx.cs" file true "example 23"
    ma "MM-115" "11" "src/{a/,b}x.cs" "src/ax.cs" file false "example 23"
    ma "MM-116" "4.3" "{**,lib}/*.cs" "x/y/a.cs" file true "D34: '**' as an alternative is a globstar"
    ma "MM-117" "5.1" "**/{bin,obj}/" "src/obj" dir true "braces in a directory mask"

    // ---- literal characters, Unicode (5.9, D26, D27) -------------------------------------------------
    ma "MM-120" "5.9" "!a.txt" "!a.txt" file true "unchanged; example 12 (gitignore: negation)"
    ma "MM-121" "5.9" "!a.txt" "a.txt" file false "unchanged; example 12"
    ma "MM-122" "5.9" "#a.txt" "#a.txt" file true "unchanged; example 12 (gitignore: comment)"
    ma "MM-123" "5.9" "??.txt" "\U0001F600.txt" file true "D26; example 23"
    ma "MM-124" "5.9" "?.txt" "\U0001F600.txt" file false "D26; example 23 (picomatch u, globset: true)"
    ma "MM-125" "5.4" "caf\u00E9*" "cafe\u0301.txt" file false "D27; example 23"
    ma "MM-126" "5.4" "caf*" "cafe\u0301.txt" file true "D27; example 23"
    ma "MM-127" "5.4" "caf\u00E9.txt" "cafe\u0301.txt" file false "D27: the matcher compares code units; a literal include finds the file through the probe (scanner, example 23)"
    ma "MM-128" "5.1" "a b/c d.txt" "a b/c d.txt" file true "unchanged"
]

// =================================================================================================
// (c) format (spec 5.5, 5.7)
// =================================================================================================

type FormatExpect =
    | Formats of string
    | FormatFails of code: string

/// `format named positional (Rule.parse Pattern)`; `Positional` is keyed by the 1-based ordinal of
/// the top-level bare wildcard (5.7: wildcards inside groups do not count). A missing key gives "".
type FormatVector =
    { Id: string
      Pattern: string
      Named: (string * string) list
      Positional: (int * string) list
      Expect: FormatExpect
      Note: string }
    override v.ToString() = v.Id

let private fo id pattern named positional expect note =
    { Id = id; Pattern = pattern; Named = named; Positional = positional; Expect = expect; Note = note }
let private gives s = Formats s
let private xp014 = FormatFails "XP014"

let formats : FormatVector list = [
    fo "FO-001" "out/(cfg:*)/(fwk:*)/(name:*).dll" [ "cfg", "Release"; "fwk", "net8.0"; "name", "Core" ] [] (gives "out/Release/net8.0/Core.dll") "example 1 inverse"
    fo "FO-002" "**/(n:*)/?*.(ext:c?)" [ "n", "N"; "ext", "EXT" ] [ 1, "<1>"; 2, "<2>"; 3, "<3>" ] (gives "<1>/N/<2><3>.EXT") "prototype PatternAstTests: top-level ordinals 1..3"
    fo "FO-003" "gen/*/(name:*)-?.txt" [ "name", "core" ] [ 1, "a"; 2, "7" ] (gives "gen/a/core-7.txt") "5.7: the '?' is the second top-level wildcard"
    fo "FO-004" "a/**/b" [] [ 1, "" ] (gives "a/b") "5.5: an empty globstar drops one adjacent '/'"
    fo "FO-005" "a/**/b" [] [ 1, "x/y" ] (gives "a/x/y/b") "5.5"
    fo "FO-006" "**/b" [] [ 1, "" ] (gives "b") "5.5"
    fo "FO-007" "a/**" [] [ 1, "" ] (gives "a") "5.5: drops the '/' before"
    fo "FO-008" "**" [] [ 1, "" ] (gives "") "5.5"
    fo "FO-009" "/**/a" [] [ 1, "" ] (gives "/a") "5.5: one '/' dropped, the root kept"
    fo "FO-010" "*/**/file.o" [] [ 1, "a"; 2, "" ] (gives "a/file.o") "D12 idiom"
    fo "FO-011" "*/**/file.o" [] [ 1, "a"; 2, "b" ] (gives "a/b/file.o") "D12 idiom"
    fo "FO-012" "(path:*/**)/file.o" [ "path", "a/b" ] [] (gives "a/b/file.o") "D12 idiom with a capture"
    fo "FO-013" "(path:bin/**)/file.o" [ "path", "bin" ] [] (gives "bin/file.o") "5.5 inverse of RM-038"
    fo "FO-014" "(p:**)/b" [ "p", "" ] [] (gives "b")
        "SPEC? 5.5 drops a '/' for an empty globstar slot; a group whose body is a globstar edge (5.5 '(p:**)/b' -> p = \"\") read as such a slot, else '/b' (absolute)"
    fo "FO-015" "a/(p:**)/b" [ "p", "" ] [] (gives "a/b") "SPEC? as FO-014"
    fo "FO-016" "a/(p:**)" [ "p", "" ] [] (gives "a") "SPEC? as FO-014"
    fo "FO-017" "**/**/b" [] [ 1, "" ] (gives "b") "D34: one globstar after the collapse"
    fo "FO-018" "out/version.txt" [] [] (gives "out/version.txt") "tm 14: literals only"
    fo "FO-019" "C:/x/y.dll" [] [] (gives "C:/x/y.dll") "prototype PatternAstTests"
    fo "FO-020" "//srv/sh/(n:*)" [ "n", "a" ] [] (gives "//srv/sh/a") "a UNC root prints as two '/'"
    fo "FO-021" @"lit\(x\)/\[y\].txt" [] [] (gives "lit(x)/[y].txt") "escapes resolved; prototype PatternAstTests"
    fo "FO-022" "a[*]b" [] [] (gives "a*b") "D34: the escape idiom is a literal"
    fo "FO-023" @"a[\\]b" [] [] (gives @"a\b") "D34"
    fo "FO-024" "*.{dll,pdb}" [] [ 1, "a" ] xp014 "changed in 4.0 (item 32): 3.x printed the source text"
    fo "FO-025" "lib[ab].dll" [] [] xp014 "item 32"
    fo "FO-026" "lib[x].dll" [] [] xp014
        "SPEC? a one-character class '[x]' is a Class token (4.4), so XP014; a legacy rule 'lib[x].dll' in a multi-output rule then fails (3.x printed 'lib[x].dll' as text)"
    fo "FO-027" "(x:{a,b})-{1,2}" [ "x", "a" ] [] xp014 "item 32: the braces outside the group"
    fo "FO-028" "(n:*).(ext:{dll,pdb})" [ "n", "a"; "ext", "pdb" ] [] (gives "a.pdb") "braces inside a group are fine"
    fo "FO-029" "(n:[ab]).c" [ "n", "b" ] [] (gives "b.c") "a class inside a group is fine"
    fo "FO-030" "(n:*).c" [ "n", "a(b)" ] [] (gives "a(b).c") "changed in 4.0 (item 30): values are not escaped; 3.x broke on ')'"
    fo "FO-031" "(n:*).c" [ "n", "x/y" ] [] (gives "x/y.c") "no check: a caller re-matches if needed (prototype)"
    fo "FO-032" "out/(fwk:*)/(lib:*).xml" [ "fwk", ""; "lib", "c" ] [] (gives "out//c.xml") "prototype PatternAstTests: only a globstar slot drops a '/'"
    fo "FO-033" "a**b" [] [ 1, "x" ] (gives "axb") "a segment-local '**' is a wildcard slot"
    fo "FO-034" "src/{**,lib}/*.cs" [] [ 1, ""; 2, "x" ] xp014 "item 32"
    fo "FO-035" "(a:*)-(b:*)" [ "a", "x-y"; "b", "z" ] [] (gives "x-y-z") "inverse of RM-025"
    fo "FO-036" @"a\|b.txt" [] [] (gives "a|b.txt") "escapes resolved"
    fo "FO-037" "clean::(b:*)::(cfg:*)" [ "b", "acme"; "cfg", "Release" ] [] (gives "clean::acme::Release") "phony"
    fo "FO-038" "*/(a:*)/*" [ "a", "y" ] [ 1, "x"; 2, "z" ] (gives "x/y/z") "5.7: the group's '*' does not count"
]

// =================================================================================================
// (d) formatMatches (spec 5.7; replaces ExecCore.applyWildcards)
// =================================================================================================

type FormatMatchesVector =
    { Id: string
      Matches: (string * string) list
      Pattern: string
      Expect: FormatExpect
      Note: string }
    override v.ToString() = v.Id

let private fm id matches pattern expect note = { Id = id; Matches = matches; Pattern = pattern; Expect = expect; Note = note }

let formatMatchesV : FormatMatchesVector list = [
    fm "FM-001" [ "1", "a"; "2", "core"; "3", "7"; "name", "core" ] "gen/*/(name:*)-?.txt" (gives "gen/a/core-7.txt") "prototype PatternAstTests: positional numbers as getRuleMatches holds them (5.6)"
    fm "FM-002" [ "fwk", "net8.0"; "lib", "" ] "out/(fwk:*)/(lib:*).xml" (gives "out/net8.0/.xml") "changed in 4.0 (item 30): 3.x left '(lib:)' in the name"
    fm "FM-003" [ "fwk", "a)b"; "lib", "c" ] "out/(fwk:*)/(lib:*).xml" (gives "out/a)b/c.xml") "changed in 4.0 (item 30): 3.x broke on ')'"
    fm "FM-004" [ "lib", "c" ] "out/(fwk:*)/(lib:*).xml" (gives "out//c.xml") "a missing key gives \"\" (prototype)"
    fm "FM-005" [ "1", ""; "2", "a" ] "**/*.pdb" (gives "a.pdb") "5.5: empty globstar"
    fm "FM-006" [ "1", "x/y"; "2", "a" ] "**/*.pdb" (gives "x/y/a.pdb") "5.7"
    fm "FM-007" [ "1", "" ] "a/**/b.txt" (gives "a/b.txt") "example 9"
    fm "FM-008" [ "pat", "dd/a"; "1", "dd"; "2", "a" ] "(pat:**/*)/main.xml" (gives "dd/a/main.xml") "example 3 (pinned)"
    fm "FM-009" [ "1", "a" ] "dd/*.xml" (gives "dd/a.xml") "example 3 (pinned)"
    fm "FM-010" [ "cfg", "R"; "1", "R"; "2", "a" ] "out/(cfg:*)/*.xml" (gives "out/R/a.xml") "5.7: the bare '*' is number 2 (wildcards)"
    fm "FM-011" [ "1", "a" ] "*.{dll,pdb}" xp014 "item 32"
    fm "FM-012" [] @"lit\(x\)/\[y\].txt" (gives "lit(x)/[y].txt") "prototype PatternAstTests"
    fm "FM-013" [ "n", "x"; "m", "y"; "1", "x"; "2", "y" ] @"out/(n:*)\|(m:*).txt" (gives "out/x|y.txt") "prototype PatternAstTests"
    fm "FM-014" [ "0", "/p/a.c"; "1", "a" ] "*.c" (gives "a.c") "the \"0\" key is not used"
    fm "FM-015" [] "a(b" (FormatFails "XP003") "the pattern is parsed: a PatternException"
    fm "FM-016" [ "path", "a"; "1", "a"; "2", "" ] "(path:*/**)/file.o" (gives "a/file.o") "D12 idiom"
    fm "FM-017" [ "1", "a"; "2", "" ] "*/**/file.o" (gives "a/file.o") "D12 idiom"
    fm "FM-018" [ "1", "a/b"; "n", "core"; "2", "core"; "3", "x"; "4", "1"; "ext", "cs"; "5", "s" ] "**/(n:*)/?*.(ext:c?)" (gives "a/b/core/x1.cs") "inverse of RM-049"
    fm "FM-019" [] "out/version.txt" (gives "out/version.txt") "tm 14"
    fm "FM-020" [ "b", "acme"; "cfg", "Release" ] "clean::(b:*)::(cfg:*)" (gives "clean::acme::Release") "phony"
    fm "FM-021" [ "1", "x"; "2", "y"; "a", "y"; "3", "z" ] "*/(a:*)/*" (gives "x/y/z") "5.6: the second bare '*' is number 3"
    fm "FM-022" [ "1", "a" ] "{a,*}x?" xp014 "item 32: braces outside a group"
]

// =================================================================================================
// (e) Multi-output rules (spec 5.6, 5.7, examples 2 and 3; XP013, XP014)
// =================================================================================================

type MultiExpect =
    | Outputs of string list           // the outputs, formatted (not yet joined with the root)
    | NoOutputs                        // no pattern matches the target
    | OutputsFail of code: string      // rejected when the rule is created

/// The engine's `MultiFileRule`: the first pattern that matches the target gives the captures;
/// every pattern is formatted with them (`formatMatches`). Run in both comparer modes.
type MultiOutputVector =
    { Id: string
      Root: string
      Patterns: string list
      Target: string
      Expect: MultiExpect
      Note: string }
    override v.ToString() = v.Id

let private mo id patterns target expect note = { Id = id; Root = "/p"; Patterns = patterns; Target = target; Expect = expect; Note = note }

let multiOutputs : MultiOutputVector list = [
    mo "MO-001" [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ] "out/net8.0/Core.xml" (Outputs [ "out/net8.0/Core.dll"; "out/net8.0/Core.xml" ]) "unchanged; example 2"
    mo "MO-002" [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml" ] "out/net8.0/Core.dll" (OutputsFail "XP013") "changed in 4.0 (item 31): 3.x named 'out/net8.0/.xml'; example 2"
    mo "MO-003" [ "dd/*.dll"; "dd/*.xml" ] "dd/a.dll" (Outputs [ "dd/a.dll"; "dd/a.xml" ]) "unchanged; example 3 (positional pairing)"
    mo "MO-004" [ "dd/*.dll"; "dd/*.xml" ] "dd/a.xml" (Outputs [ "dd/a.dll"; "dd/a.xml" ]) "unchanged: the second pattern matches"
    mo "MO-005" [ "(pat:**/*).dll"; "(pat:**/*)/main.xml" ] "dd/a.dll" (Outputs [ "dd/a.dll"; "dd/a/main.xml" ]) "unchanged; example 3, XakeScriptTests 'Masks'"
    mo "MO-006" [ "(pat:**/*).dll"; "(pat:**/*)/main.xml" ] "a.dll" (Outputs [ "a.dll"; "a/main.xml" ]) "changed in 4.0 (item 12): 3.x no match (XP119 on the match)"
    mo "MO-007" [ "**/*.dll"; "**/*.pdb" ] "a.dll" (Outputs [ "a.dll"; "a.pdb" ]) "changed in 4.0 (item 12): the empty globstar slot drops one '/' (5.5)"
    mo "MO-008" [ "**/*.dll"; "**/*.pdb" ] "x/y/a.dll" (Outputs [ "x/y/a.dll"; "x/y/a.pdb" ]) "unchanged"
    mo "MO-009" [ "out/(cfg:*)/*.dll"; "out/(cfg:*)/*.xml" ] "out/Release/a.xml" (Outputs [ "out/Release/a.dll"; "out/Release/a.xml" ]) "5.6: positional by number across outputs"
    mo "MO-010" [ "dd/*.dll"; "dd/*/*.xml" ] "dd/a.dll" (Outputs [ "dd/a.dll"; "dd/a/.xml" ]) "unchanged: a missing positional value gives \"\" (3.x applyWildcards the same)"
    mo "MO-011" [ "out/*.{dll,pdb}"; "out/*.xml" ] "out/a.dll" (OutputsFail "XP014") "changed in 4.0 (item 32): braces outside a group"
    mo "MO-012" [ "lib[ab].dll"; "lib[ab].xml" ] "liba.dll" (OutputsFail "XP014") "changed in 4.0 (item 32): a class outside a group"
    mo "MO-013" [ "out/(n:*).(ext:{dll,exe})"; "out/(n:*).(ext:{dll,exe}).config" ] "out/a.exe" (Outputs [ "out/a.exe"; "out/a.exe.config" ]) "braces inside a group can be formatted"
    mo "MO-014" [ "a/*.dll"; "b/*.dll" ] "c/x.dll" NoOutputs "unchanged"
    mo "MO-015" [ "out/(fwk:*)/(lib:*).dll"; "out/(lib:*)/(fwk:*).xml" ] "out/net8.0/Core.dll" (Outputs [ "out/net8.0/Core.dll"; "out/Core/net8.0.xml" ]) "same name set in another order (XP013 compares sets)"
]

// =================================================================================================
// (f) Rule selection by specificity (spec 5.8, examples 9 and 20, item 33)
// =================================================================================================

/// `Rules` in declaration order. The winner is the most specific matching rule; equal
/// specificity goes to the last declared (ExecCore.explainMatch). `None`: no rule matches.
type SelectionVector =
    { Id: string
      Os: Os
      Root: string
      Rules: string list
      Target: string
      Winner: string option
      Note: string }
    override v.ToString() = v.Id

let private rs id root rules target winner note =
    { Id = id; Os = Os.Any; Root = root; Rules = rules; Target = target; Winner = winner; Note = note }

let selections : SelectionVector list = [
    rs "RS-001" "/p" [ "**/*.dll"; "*.dll" ] "a.dll" (Some "*.dll") "example 9: both match, '*.dll' has no globstar (3.x: '**/*.dll' did not match)"
    rs "RS-002" "/p" [ "*.dll"; "**/*.dll" ] "a.dll" (Some "*.dll") "example 9: regardless of declaration order"
    rs "RS-003" "/p" [ "*.dll"; "**/*.dll" ] "x/a.dll" (Some "**/*.dll") "example 9: only the globstar rule matches"
    rs "RS-004" "/p" [ "*/?.dll"; "**/x.dll" ] "a/x.dll" (Some "*/?.dll") "changed in 4.0 (item 33): 3.x '**/x.dll' won (the '/' counted)"
    rs "RS-005" "/p" [ "out/(b:*)/obf/(fwk:*)/(asm:*).dll"; "out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll" ] "out/acme/obf/net472/A.B.dll" (Some "out/(b:*)/obf/(fwk:*)/(asm:*).dll") "unchanged; RuleSpecificityTests"
    rs "RS-006" "" [ "clean::(b:*)"; "clean::(b:*)::(cfg:*)" ] "clean::acme::Release" (Some "clean::(b:*)::(cfg:*)") "unchanged; RuleSpecificityTests"
    rs "RS-007" "" [ "clean::(b:*)::(cfg:*)"; "clean::(b:*)" ] "clean::acme" (Some "clean::(b:*)") "unchanged; RuleSpecificityTests"
    rs "RS-008" "" [ "build::special"; "build::(x:*)" ] "build::special" (Some "build::special") "unchanged; RuleSpecificityTests"
    rs "RS-009" "" [ "(a:*)::common"; "(b:*)::common" ] "pre::common" (Some "(b:*)::common") "unchanged; RuleSpecificityTests: a tie goes to the last declared"
    rs "RS-010" "/p" [ "a/**/x.dll"; "a/*/x.dll" ] "a/b/x.dll" (Some "a/*/x.dll") "unchanged; RuleSpecificityTests"
    rs "RS-011" "/p" [ "a/?.dll"; "a/*.dll" ] "a/b.dll" (Some "a/?.dll") "unchanged; RuleSpecificityTests"
    rs "RS-012" "/p" [ "a/?.dll"; "a/*.dll" ] "a/bc.dll" (Some "a/*.dll") "unchanged"
    rs "RS-013" "/p" [ "*/**/file.o"; "**/file.o" ] "a/file.o" (Some "**/file.o") "example 20, C-016: fewer wildcards"
    rs "RS-014" "/p" [ "*/**/file.o"; "**/file.o" ] "file.o" (Some "**/file.o") "example 20: only '**/file.o' matches (3.x: none)"
    rs "RS-015" "/p" [ "[ab].c"; "*.c" ] "a.c" (Some "[ab].c") "5.8: a class weighs 1"
    rs "RS-016" "/p" [ "a.c"; "{a,b}.c" ] "a.c" (Some "{a,b}.c") "5.8: braces count as the least specific alternative, a tie, the last declared wins"
    rs "RS-017" "/p" [ "{a,b}.c"; "a.c" ] "a.c" (Some "a.c") "5.8"
    rs "RS-018" "/p" [ "out/r1/(cfg:*)/(name:*).dll"; "out/r2/(cfg:*)/(name:*).dll"; "out/(cfg:*)/(fwk:*)/(name:*).dll" ] "out/Release/net8.0/Core.dll"
        (Some "out/(cfg:*)/(fwk:*)/(name:*).dll") "unchanged; RuleMatchTests"
    rs "RS-019" "/p" [ "out/r1/(cfg:*)/(name:*).dll"; "out/r2/(cfg:*)/(name:*).dll"; "out/(cfg:*)/(fwk:*)/(name:*).dll" ] "out/r1/Release/Core.dll"
        (Some "out/r1/(cfg:*)/(name:*).dll") "unchanged: both match, more literals win"
    rs "RS-020" "/p" [ "a*b"; "a**b" ] "axb" (Some "a**b") "changed in 4.0 (item 13): a tie now, the last declared wins; 3.x 'a*b' won"
    rs "RS-021" "/p" [ "*.c" ] "a.h" None "no rule"
    rs "RS-022" "/p" [ "src/a.cs"; "src/(name:*).cs" ] "src/a.cs" (Some "src/a.cs") "unchanged: the literal rule wins"
    rs "RS-023" "" [ "main"; "main" ] "main" (Some "main") "unchanged: identical patterns, the override idiom"
]
