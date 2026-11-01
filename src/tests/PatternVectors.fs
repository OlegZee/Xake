/// Conformance vectors for the parser stage (PR 2) of the pattern module
/// (docs/features/pattern-module/spec.md; decisions D1-D31 in brief.md). DATA only: the tables
/// below are driven by PatternVectorTests.fs. No matcher, scanner or engine vectors here (PR 3, 4).
///
/// ## The expected AST: a compact textual rendering ("shape")
///
/// A shape renders a `Token list` of spec 4.4 / 8.2. Tokens are separated by one space; the empty
/// list is `<>`.
///
/// | token                      | shape                                   | example              |
/// |----------------------------|-----------------------------------------|----------------------|
/// | `Lit s`                    | `'s'`, with `\` as `\\` and `'` as `\'` | `'src'`, `'a(b'`     |
/// | `Sep`                      | `/`                                     |                      |
/// | `Star`                     | `*`                                     |                      |
/// | `AnyChar`                  | `?`                                     |                      |
/// | `Globstar`                 | `**`                                    |                      |
/// | `DoubleStar` (seg.-local)  | `**~`                                   | `'a' **~ 'b'`        |
/// | `Class (false, items)`     | `[i1 i2 ...]`                           | `[a b-z]`            |
/// | `Class (true, items)`      | `[^i1 i2 ...]`                          | `[^a]`               |
/// | `Alt [a1; a2; ...]`        | `{s1 , s2 , ...}`                       | `{'a' , <>}`         |
/// | `Group (name, body)`       | `(name: s)`                             | `(cfg: *)`           |
///
/// A class item is `c` (`One c`) or `c-d` (`Range (c, d)`); the characters `\ ] - ^ !` and space
/// are written with a `\` before them (`[\- a]` is the class of `-` and `a`). Each alternative and
/// each group body is itself a shape. A root is rendered from its tokens (spec 4.4): `/ 'a'` is an
/// absolute path, `'C:' / 'x'` a drive, `/ / 'srv' / 'sh'` a UNC root; a mask's trailing
/// directory mark is a trailing `/`.
///
/// Literal text is one `Lit` per maximal run of literal characters between other tokens (escapes
/// resolved): `a\(b` is `'a(b'`, `lib[x].dll` is `'lib' [x] '.dll'` (spec 5.7: `parse (escape s)`
/// is `[Lit s]` per segment).
/// The one-character classes `[*] [?] [,] [\\]` are the 4.0 escape idiom and parse to literal
/// text (`a[*]b` is `'a*b'`; D34), every other `[c]` is a `Class`.
///
/// ## Conventions
///
/// - `Warnings` are the parse-time warning codes (XP105, XP106, XP110, XP112, XP117) expected for
///   the vector's context and role, compared as a sorted set. Evidence/evaluation warnings
///   (XP104, XP113, XP114, XP119, XP120) are never parse-time: a vector whose spelling can warn on
///   evidence lists no code and says so in `Note`.
/// - `Note` gives the source and the 3.x result: "unchanged", or "changed in 4.0 (item N)" with
///   the 3.x result (section 3 numbering). A note starting "SPEC?" marks a reading the spec does
///   not settle; the expected value is this file's best reading and needs a decision.
/// - Error positions are 1-based character positions in the input (spec 7.2), pointing at the
///   start of the offending construct.
module PatternVectors

[<RequireQualifiedAccess>]
type Ctx = Rule | Mask

/// The use of a mask, which selects the 3.x meaning a warning names (MaskRole, spec 7.3, D24).
[<RequireQualifiedAccess>]
type Role = NoRole | Include | Exclude

/// Where a parse error is reported.
type ErrAt =
    | At of int            // PatternError.Position = Some n
    | NoPosition           // PatternError.Position = None
    | AnyPosition          // not asserted (the spec does not settle it; see the vector's SPEC? note)

type Expect =
    | Parses of shape: string
    | Fails of code: string * at: ErrAt

type ParseVector =
    { Id: string
      Section: string
      Context: Ctx
      Role: Role
      Input: string
      Expect: Expect
      Warnings: string list
      Note: string }
    override v.ToString() = v.Id

let private vec ctx role id section input expect warnings note =
    { Id = id; Section = section; Context = ctx; Role = role; Input = input; Expect = expect
      Warnings = warnings; Note = note }

/// a rule pattern
let private r = vec Ctx.Rule Role.NoRole
/// a mask used as an include
let private mi = vec Ctx.Mask Role.Include
/// a mask used as an exclude
let private me = vec Ctx.Mask Role.Exclude
let private ok shape = Parses shape
let private err code (pos: int) = Fails (code, At pos)
let private errNoPos code = Fails (code, NoPosition)
let private errAnyPos code = Fails (code, AnyPosition)

// =================================================================================================
// Parsing: shapes, errors, parse-time warnings
// =================================================================================================

let parse : ParseVector list = [
    // ---- 4.1 / 4.2: literals, separators, wildcards --------------------------------------------
    r  "P-001" "4.2" "src/a.cs" (ok "'src' / 'a.cs'") [] "unchanged"
    r  "P-002" "4.2" "*.cs" (ok "* '.cs'") [] "unchanged"
    r  "P-003" "4.2" "mli?.dll" (ok "'mli' ? '.dll'") [] "unchanged; PathTests.MatchRule"
    r  "P-004" "4.2" "a?*b" (ok "'a' ? * 'b'") [] "unchanged; PatternAstTests corpus"
    r  "P-005" "4.2" "*" (ok "*") [] "unchanged"
    r  "P-006" "4.2" "?" (ok "?") [] "unchanged"
    r  "P-007" "4.2" "x86/win/mlib.*" (ok "'x86' / 'win' / 'mlib.' *") [] "unchanged; PathTests.MatchRule"
    r  "P-008" "4.2" "*/*/mlib.*" (ok "* / * / 'mlib.' *") [] "unchanged; PathTests.MatchRule"
    r  "P-009" "4.1" "x.y+z!=w$^" (ok "'x.y+z!=w$^'") [] "unchanged; regex metacharacters are plain characters"
    r  "P-010" "4.1" "a,b" (ok "'a,b'") [] "unchanged; ',' outside braces is a char (I5)"
    mi "P-011" "5.9" "!a.txt" (ok "'!a.txt'") [] "unchanged; example 12: a leading '!' is literal (gitignore: negation)"
    mi "P-012" "5.9" "#a.txt" (ok "'#a.txt'") [] "unchanged; example 12: '#' is literal (gitignore: comment)"
    mi "P-013" "4.2" "a b/c d.txt" (ok "'a b' / 'c d.txt'") [] "unchanged; spaces are literal"
    mi "P-014" "5.4" "café*" (ok "'café' *") [] "unchanged; example 23 (no normalization)"
    mi "P-015" "5.9" "??.txt" (ok "? ? '.txt'") [] "unchanged; example 23: two code units for a non-BMP character (D26)"
    mi "P-016" "5.9" "😀.txt" (ok "'😀.txt'") [] "unchanged; a surrogate pair inside a literal"
    r  "P-017" "4.2" "it's.txt" (ok @"'it\'s.txt'") [] "unchanged; quote in a literal (rendering check)"
    r  "P-018" "4.2" "a:b" (ok "'a:b'") [] "unchanged; a one-letter drive needs a separator after ':'"
    r  "P-019" "4.2" "C:a.txt" (ok "'C:a.txt'") [] "unchanged; 'C:' not followed by a separator is no drive root"
    r  "P-020" "4.2" "db:seed-(table:*)" (ok "'db:seed-' (table: *)") [] "unchanged; docs/*.md phony pattern"
    r  "P-021" "4.2" "clean::(b:*)::(cfg:*)" (ok "'clean::' (b: *) '::' (cfg: *)") [] "unchanged; RuleSpecificityTests"
    r  "P-022" "4.2" "lock::(name:*)::(cfg:*)" (ok "'lock::' (name: *) '::' (cfg: *)") [] "unchanged; build.fsc.fsx"
    r  "P-023" "4.2" "test::(project:*)/(slice:*)" (ok "'test::' (project: *) / (slice: *)") [] "unchanged; build.fsx"
    r  "P-024" "4.2" "run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" (ok "'run' / (cfg: *) / (fwk: *) / (name: *) / '.xake-run.txt'") [] "unchanged; build.fsx"
    mi "P-025" "4.2" "src/*/xake.project.json" (ok "'src' / * / 'xake.project.json'") [] "unchanged; build.fsx"
    me "P-026" "4.2" "src/*tests/*.fsproj" (ok "'src' / * 'tests' / * '.fsproj'") [] "unchanged; build.fsx exclude"
    mi "P-027" "4.2" "paket-files/*.*" (ok "'paket-files' / * '.' *") [] "changed in 4.0 (item 15) at match level only: '*.*' needs a '.' (XP114 on evidence, never at parse); samples"
    mi "P-028" "4.2" "*.*" (ok "* '.' *") [] "changed in 4.0 (item 15): 3.x listed any name; XP114 on evidence only; example 14"
    mi "P-029" "4.2" "**/*.rdl*" (ok "** / * '.rdl' *") [] "unchanged; example 5, FilesetTests"
    me "P-030" "5.9" "*.rdlx" (ok "* '.rdlx'") [] "unchanged; example 5: anchored at the base dir (gitignore: any depth)"
    mi "P-031" "4.2" "a*" (ok "'a' *") [] "unchanged; FilesetTests"

    // ---- 4.2: repeated separators, roots ---------------------------------------------------------
    r  "P-032" "4.2" "a//b" (ok "'a' / 'b'") [] "changed in 4.0 (item 34): 3.x Path.matches/scan read a/b, matchGroups compared the raw text (measurements-engine 1)"
    mi "P-033" "4.2" "a//b" (ok "'a' / 'b'") [] "changed in 4.0 (item 34): 3.x scan listed a/b (measurements-engine 1)"
    r  "P-034" "4.2" "a///b" (ok "'a' / 'b'") [] "item 34: a run of separators is one"
    mi "P-035" "4.2" "a//" (ok "'a' /") [] "item 34: a trailing run is one trailing separator"
    mi "P-036" "4.2" "///a" (ok "/ 'a'") [] "4.2: three or more leading separators are one '/'"
    mi "P-037" "4.2" "//a" (ok "/ 'a'") [] "D34: two leading separators followed by one name only: no UNC root (needs server and share); read here as collapsed to '/'"
    r  "P-038" "4.2" "/a/b" (ok "/ 'a' / 'b'") [] "unchanged; a leading '/' is absolute (D6)"
    me "P-039" "5.9" "/bin/**" (ok "/ 'bin' / **") [] "unchanged parse; example 22: the file-system root, not the base dir (gitignore); XP120 at evaluation, not at parse"
    mi "P-040" "4.2" "/opt/lib/*.so" (ok "/ 'opt' / 'lib' / * '.so'") [] "unchanged; example 22"
    r  "P-041" "4.2" "C:/x/y.dll" (ok "'C:' / 'x' / 'y.dll'") [] "unchanged; drive root; PatternAstTests"
    r  "P-042" "4.2" "C:/Program Files/a b/(n:*).dll" (ok "'C:' / 'Program Files' / 'a b' / (n: *) '.dll'") [] "unchanged; PatternAstTests corpus"
    mi "P-043" "4.2" "c:/rpt/**/e.rdl" (ok "'c:' / 'rpt' / ** / 'e.rdl'") [] "unchanged; FilesetTests"
    mi "P-044" "4.2" "c:/*/" (ok "'c:' / * /") [] "unchanged; FilesetTests: a trailing '/' lists directories"
    mi "P-045" "4.2" "//server/share/*.dll" (ok "/ / 'server' / 'share' / * '.dll'") [] "UNC root (S6, D24); parses on every OS"
    mi "P-046" "4.2" @"\\server\share\*.dll" (ok "/ / 'server' / 'share' / * '.dll'") ["XP110"] "UNC root with '\\' separators (D24)"
    r  "P-047" "4.2" "//server/share" (ok "/ / 'server' / 'share'") [] "UNC root alone"
    mi "P-048" "4.2" "//*/share/x" (err "XP016" 3) [] "non-literal server; D34: position: first non-literal element of the root"
    mi "P-049" "4.2" "//server/*/x" (err "XP016" 10) [] "non-literal share; D34: position as P-048"
    mi "P-050" "4.2" "//server/{a,b}/x" (err "XP016" 10) [] "braces are not a literal share"
    mi "P-051" "4.2" "/" (ok "/") [] "D34: a mask that is only a root: accepted here (the root directory); the spec states neither"
    r  "P-052" "4.2" "/" (err "XP011" 1) [] "D34: a rule that is only a root: read here as ending in a separator (XP011), not as empty (XP018)"

    // ---- 4.2: empty, '.', '..' --------------------------------------------------------------------
    r  "P-053" "4.2" "" (errNoPos "XP018") [] "changed in 4.0 (item 35): 3.x Path.matches only the empty path (measurements-engine 2)"
    mi "P-054" "4.2" "" (errNoPos "XP018") [] "changed in 4.0 (item 35): 3.x '!! \"\"' listed the base directory itself (measurements-engine 2)"
    me "P-055" "4.2" "" (errNoPos "XP018") [] "changed in 4.0 (item 35)"
    mi "P-056" "4.2" "./a.cs" (ok "'a.cs'") [] "unchanged; '.' resolved in the literal prefix"
    mi "P-057" "4.2" "a/./b" (ok "'a' / 'b'") [] "unchanged; '.' resolved in the literal prefix"
    r  "P-058" "4.2" "a/../b" (ok "'b'") [] "unchanged; '..' resolved in the literal prefix"
    r  "P-059" "4.2" @"c:/abc/../def\a.c" (ok "'c:' / 'def' / 'a.c'") ["XP110"] "unchanged meaning; PathTests.MaskTests (now warns XP110)"
    r  "P-060" "4.2" @"c:\abc\def\..\..\*.c" (ok "'c:' / * '.c'") ["XP110"] "unchanged meaning; PathTests.MaskWithParent"
    r  "P-061" "4.2" @"c:\abc\..\*.c" (ok "'c:' / * '.c'") ["XP110"] "unchanged meaning; PathTests.MaskWithParent"
    r  "P-062" "4.2" @"c:\abc\..\..\*.c" (ok "'c:' / '..' / * '.c'") ["XP110"] "D34: a '..' that climbs above the root is kept as Lit '..'; 3.x matched nothing (PathTests.MaskWithParent)"
    r  "P-063" "4.2" "../subd1/a.ss" (ok "'..' / 'subd1' / 'a.ss'") [] "D34: a leading '..' cannot be resolved in the pattern and is kept as Lit '..' (3.x: Parent; PathTests.MaskTests3)"
    mi "P-064" "4.2" "../jparsec/src/main/**/*.java" (ok "'..' / 'jparsec' / 'src' / 'main' / ** / * '.java'") [] "D34: as P-063; FilesetTests"
    mi "P-065" "4.2" "." (errNoPos "XP018") [] "SPEC? '.' alone resolves to nothing; read here as the empty pattern (3.x: not measured)"
    mi "P-066" "4.2" ".." (ok "'..'") [] "D34: '..' alone, as P-063 (3.x: not measured)"
    mi "P-067" "4.2" "src/*/../x.cs" (err "XP012" 7) [] "changed in 4.0 (item 21): 3.x scan listed src/x.cs, Path.matches never matched (measurements-engine 3)"
    mi "P-068" "4.2" "src/*/./x.cs" (err "XP012" 7) [] "changed in 4.0 (item 21, D29): 3.x dropped '.', scan listed src/d/x.cs (measurements-engine 3)"
    r  "P-069" "4.2" "src/*/./x.cs" (err "XP012" 7) [] "changed in 4.0 (item 21): 3.x Path.matches true, matchGroups no match"
    r  "P-070" "4.2" @"c:\abc\**\..\*.c" (err "XP012" 11) [] "changed in 4.0 (item 21): 3.x matched c:\\a.c (PathTests.MaskWithParent); the error wins over XP110"
    r  "P-071" "4.2" "{a,b}/../c" (err "XP012" 7) [] "braces are a non-literal element, so '..' after them is an error"
    r  "P-072" "4.2" "*/." (err "XP012" 3) [] "'.' after a wildcard (D29)"
    mi "P-073" "4.2" "**/.." (err "XP012" 4) [] "'..' after a globstar"
    mi "P-074" "5.3" "a/.hidden/*.cs" (ok "'a' / '.hidden' / * '.cs'") [] "unchanged; '.hidden' is no '.' segment"
    me "P-075" "5.3" "**/.*/**/*" (ok "** / '.' * / ** / *") [] "unchanged; FilesetTests dot-folder idiom; hermetic description masks"
    me "P-076" "5.3" "**/.*/" (ok "** / '.' * /") [] "changed in 4.0 at scan level (item 23): prunes dot directories; example 4"

    // ---- 4.2: trailing separator ------------------------------------------------------------------
    r  "P-077" "4.2" "obj/" (err "XP011" 4) [] "changed in 4.0 (item 22): 3.x matched nothing"
    r  "P-078" "4.2" @"c:\**\" (err "XP011" 6) [] "changed in 4.0 (item 22): 3.x matched nothing (PathTests.MaskTests); the error wins over XP110"
    r  "P-079" "4.2" "a/**/" (err "XP011" 5) [] "item 22"
    r  "P-080" "4.2" @"a\" (err "XP011" 2) [] "item 22: a trailing '\\' is a separator"
    me "P-081" "4.2" "obj/" (ok "'obj' /") [] "changed in 4.0 (item 23): 3.x dropped the '/' and removed nothing (measurements-engine 6); example 21"
    mi "P-082" "4.2" "a/**/" (ok "'a' / ** /") [] "unchanged; samples 'includes \"a/**/\"'; example 10"
    me "P-083" "4.2" "**/obj/" (ok "** / 'obj' /") [] "changed in 4.0 at scan level (item 23); example 21"
    mi "P-084" "4.2" @"a\" (ok "'a' /") ["XP110"] "a trailing '\\' is a separator (4.1), here the directory mark"

    // ---- 4.1: backslash -----------------------------------------------------------------------------
    r  "P-085" "4.1" @"a\(b" (ok "'a(b'") [] "changed in 4.0 (item 1, tm 55): 3.x separator, then a regex '(' (RegexParseException)"
    r  "P-086" "4.1" @"\(\)\[\]\{\}\|" (ok "'()[]{}|'") [] "changed in 4.0 (item 1): every reserved character escaped"
    r  "P-087" "4.1" @"lit\(x\)/\[y\]\|\{z\}.txt" (ok "'lit(x)' / '[y]|{z}.txt'") [] "prototype PatternAstTests (unparse round trip)"
    r  "P-088" "4.1" @"fun\(ction\).o" (ok "'fun(ction).o'") [] "prototype PatternAstTests"
    r  "P-089" "4.1" @"a\|b.txt" (ok "'a|b.txt'") [] "prototype PatternAstTests: an escaped pipe is a literal"
    r  "P-090" "4.1" @"out/(n:*)\|(m:*).txt" (ok "'out' / (n: *) '|' (m: *) '.txt'") [] "prototype PatternAstTests"
    mi "P-091" "4.1" @"sub\[1\]/*.c" (ok "'sub[1]' / * '.c'") [] "unchanged; example 6, no warning"
    mi "P-092" "4.1" @"src\(x\)\*.cs" (ok "'src(x)' / * '.cs'") ["XP110"] "changed in 4.0 (item 1): 3.x src/(x/)/*.cs; example 11"
    mi "P-093" "4.1" @"src\*.cs" (ok "'src' / * '.cs'") ["XP110"] "unchanged meaning, deprecated (item 2); example 11"
    mi "P-094" "4.1" @"debug\*.css" (ok "'debug' / * '.css'") ["XP110"] "unchanged meaning (item 2); FilesetTests 'includesif'"
    r  "P-095" "4.1" @"a\b" (ok "'a' / 'b'") ["XP110"] "item 2; 5.0: an error XP009 (not in 4.0)"
    r  "P-096" "4.1" @"a\\b" (ok "'a' / 'b'") ["XP110"] "item 34: two '\\' are two separators, collapsed"
    r  "P-097" "4.1" @"a\*b" (ok "'a' / * 'b'") ["XP110"] "4.1: '\\*' is '/*' in 4.0 (no escape for '*')"
    r  "P-098" "4.1" @"a\?b" (ok "'a' / ? 'b'") ["XP110"] "4.1: '\\?' is '/?' in 4.0"
    r  "P-099" "4.1" @"a\,b" (ok "'a' / ',b'") ["XP110"] "4.1: outside braces '\\,' is a separator then ','"
    r  "P-100" "4.1" @"c:\a.c" (ok "'c:' / 'a.c'") ["XP110"] "unchanged meaning; PathTests.MaskTests"
    r  "P-101" "4.1" @"c:\**\*.*" (ok "'c:' / ** / * '.' *") ["XP110"] "unchanged parse; PathTests.MaskTests"
    r  "P-102" "4.1" @"c:\*\?.c" (ok "'c:' / * / ? '.c'") ["XP110"] "unchanged; PathTests.MaskTests"
    r  "P-103" "4.1" @"\a" (ok "/ 'a'") ["XP110"] "a leading '\\' is an absolute root (4.2)"
    mi "P-104" "5.9" @"\*.txt" (ok "/ * '.txt'") ["XP110"] "deliberate difference: '\\*' is not an escaped '*' (POSIX, picomatch) but a root separator"
    r  "P-105" "4.1" @"src\(x)\*.cs" (err "XP003" 7) [] "changed in 4.0 (item 1, I1): example 11; prototype EscapingTests"
    mi "P-106" "4.1" @"src\(x)\*.cs" (err "XP003" 7) [] "changed in 4.0: example 11 (3.x: src/(x)/*.cs)"
    r  "P-107" "4.1" @"c:\[(]*[)].c" (err "XP003" 5) [] "changed in 4.0 (item 1): '\\[' is an escape now; 3.x matched c:\\(a).c (PathTests.MaskTests, rewritten as P-108)"
    r  "P-108" "4.1" @"c:/\(*\).c" (ok "'c:' / '(' * ').c'") [] "the 4.0 spelling of PathTests 'c:\\[(]*[)].c' (10.3)"
    r  "P-109" "4.1" @"c:\[(]*.c" (err "XP003" 5) [] "changed in 4.0 (item 1): 3.x matched c:\\(a.c (PathTests.MaskTests)"
    r  "P-110" "4.1" "c:/[(]*[)].c" (ok "'c:' / [(] * [)] '.c'") ["XP105"] "unchanged meaning, deprecated (item 10)"
    r  "P-111" "7.3" @"src\lib[x]\a**b.dll" (ok "'src' / 'lib' [x] / 'a' **~ 'b.dll'") ["XP105"; "XP110"; "XP112"] "several parse-time parts in one pattern"

    // ---- 4.3: '**' position rule ---------------------------------------------------------------------
    r  "P-112" "4.3" "**" (ok "**") [] "unchanged"
    r  "P-113" "4.3" "**/b" (ok "** / 'b'") [] "parse unchanged; match changed (item 12): XP119 on evidence only"
    r  "P-114" "4.3" "a/**" (ok "'a' / **") [] "parse unchanged"
    r  "P-115" "4.3" "a/**/b" (ok "'a' / ** / 'b'") [] "parse unchanged; example 9"
    r  "P-116" "4.3" "**/mlib.dll" (ok "** / 'mlib.dll'") [] "changed in 4.0 at match level (item 12): 3.x rule did not match c:/mlib.dll (PathTests.RecurseInRule)"
    r  "P-117" "4.3" "**/*.dll" (ok "** / * '.dll'") [] "example 9; XP119 on evidence only"
    r  "P-118" "5.5" "**/**/b" (ok "** / 'b'") [] "D34: adjacent globstars collapse to one in the AST (unparse gives '**/b')"
    r  "P-119" "4.3" "a**b" (ok "'a' **~ 'b'") ["XP112"] "changed in 4.0 (item 13): 3.x crossed '/'"
    mi "P-120" "4.3" "a**b" (ok "'a' **~ 'b'") [] "unchanged: masks read it as '*' in 3.x too; XP112 is rule-only"
    r  "P-121" "4.3" "**.cs" (ok "**~ '.cs'") ["XP112"] "item 13"
    r  "P-122" "4.3" "x/**y" (ok "'x' / **~ 'y'") ["XP112"] "item 13"
    r  "P-123" "4.3" "y**/x" (ok "'y' **~ / 'x'") ["XP112"] "item 13"
    r  "P-124" "4.1" "***" (ok "**~ *") ["XP112"] "maximal munch: dstar then star; the dstar is not followed by a separator"
    r  "P-125" "4.1" "a***b" (ok "'a' **~ * 'b'") ["XP112"] "PatternAstTests corpus"
    r  "P-126" "4.3" "(p:**)/b" (ok "(p: **) / 'b'") [] "group delimiters are transparent"
    r  "P-127" "4.3" "a/(p:**)" (ok "'a' / (p: **)") [] "4.3 example"
    r  "P-128" "4.3" "a/(p:**)/b" (ok "'a' / (p: **) / 'b'") [] "4.3"
    r  "P-129" "4.3" "(path:bin/**)/file.o" (ok "(path: 'bin' / **) / 'file.o'") [] "parse unchanged; PathTests.MatchGroupsInRule (match changed, item 12)"
    r  "P-130" "4.3" "(p:a**)" (ok "(p: 'a' **~)") ["XP112"] "4.3 example"
    r  "P-131" "4.3" "**(n:x)" (ok "**~ (n: 'x')") ["XP112"] "group_open is transparent; followed by 'x', so segment-local"
    r  "P-132" "4.3" "a/(n:**)x" (ok "'a' / (n: **~) 'x'") ["XP112"] "the rparen is transparent; followed by 'x'"
    r  "P-133" "4.3" "(x:**)" (ok "(x: **)") [] "PatternAstTests corpus"
    r  "P-134" "5.1" "*/**/file.o" (ok "* / ** / 'file.o'") [] "the one-or-more idiom (D12); example 20"
    r  "P-135" "5.1" "(path:*/**)/file.o" (ok "(path: * / **) / 'file.o'") [] "the one-or-more idiom with a capture (D12)"
    r  "P-136" "4.3" "src/**/bin/*.o" (ok "'src' / ** / 'bin' / * '.o'") [] "PatternAstTests corpus"
    mi "P-137" "4.3" "dir/**" (ok "'dir' / **") [] "changed in 4.0 at scan level (item 14): 3.x listed the directories; XP113 on evidence only"
    me "P-138" "4.3" "dir/**" (ok "'dir' / **") [] "changed in 4.0 at scan level (item 14): 3.x removed nothing (FilesetTests); XP113 on evidence"
    r  "P-139" "4.3" "src/{**,lib}/*.cs" (ok "'src' / {** , 'lib'} / * '.cs'") [] "D34: '**' as a whole brace alternative between separators: read as a globstar; 4.3 names only group delimiters as transparent"

    // ---- 4.1 class mode ---------------------------------------------------------------------------
    r  "P-140" "4.1" "a[bc]d.txt" (ok "'a' [b c] 'd.txt'") [] "unchanged (3.x rule: a regex class); prototype EscapingTests"
    mi "P-141" "4.1" "a[bc]d.txt" (ok "'a' [b c] 'd.txt'") [] "changed in 4.0 (item 8): 3.x include read the literal text; XP104 on evidence only"
    r  "P-142" "4.1" "[a-z]*.c" (ok "[a-z] * '.c'") [] "unchanged"
    r  "P-143" "4.1" "[^a]*.c" (ok "[^a] * '.c'") [] "unchanged (3.x regex negation)"
    r  "P-144" "4.1" "[!a]*.c" (ok "[^a] * '.c'") ["XP117"] "changed in 4.0 (item 11): 3.x a class of '!' and 'a'"
    me "P-145" "4.1" "[!a]*.c" (ok "[^a] * '.c'") ["XP117"] "changed in 4.0 (item 11): exclude wildcard segment"
    me "P-146" "4.1" "[!a].c" (ok "[^a] '.c'") [] "changed in 4.0 (item 8): exclude literal segment, 3.x literal text; XP104 on evidence only"
    mi "P-147" "4.1" "[!a]*.c" (ok "[^a] * '.c'") [] "changed in 4.0 (item 8): include, 3.x literal text; XP104 on evidence only"
    r  "P-148" "7.3" "lib[x].dll" (ok "'lib' [x] '.dll'") ["XP105"] "unchanged meaning, deprecated (item 10); PatternAstTests corpus"
    r  "P-149" "7.3" "[(]*.ss" (ok "[(] * '.ss'") ["XP105"] "unchanged (item 10); example 7; XakeScriptTests"
    r  "P-150" "7.3" "(name:[(]*).ss" (ok "(name: [(] *) '.ss'") ["XP105"] "unchanged (item 10); example 7; XakeScriptTests"
    mi "P-151" "7.3" "sub[1]/*.c" (ok "'sub' [1] / * '.c'") ["XP106"] "changed in 4.0 (item 9): 3.x the literal directory 'sub[1]'; example 6"
    me "P-152" "7.3" "sub[1]/*.c" (ok "'sub' [1] / * '.c'") ["XP106"] "changed in 4.0 (item 9): exclude literal segment"
    me "P-153" "7.3" "a[1]*.c" (ok "'a' [1] * '.c'") ["XP105"] "unchanged (item 10): exclude wildcard segment"
    mi "P-154" "7.3" "a[1]*.c" (ok "'a' [1] * '.c'") ["XP106"] "changed in 4.0 (item 9): include"
    r  "P-155" "4.1" "a[*]b" (ok "'a*b'") [] "the 4.0 literal '*' parses to a literal (spec 5.7: parse (escape s) is [Lit s]; D34: the escape idiom); no XP105 for '* ? , \\'"
    r  "P-156" "4.1" "a[?]b" (ok "'a?b'") [] "the 4.0 literal '?' (spec 5.7: parse (escape s) is [Lit s]; D34: the escape idiom)"
    r  "P-157" "4.1" "a[,]b" (ok "'a,b'") [] "the 4.0 literal ',' (spec 5.7: parse (escape s) is [Lit s]; D34: the escape idiom)"
    r  "P-158" "4.1" @"a[\\]b" (ok @"'a\\b'") [] "the 4.0 literal '\\' (spec 5.7: parse (escape s) is [Lit s]; D34: the escape idiom)"
    mi "P-159" "7.3" "a[*]b" (ok "'a*b'") [] "D34: the escape idiom never warns XP106 (escapePath emits '[*]', 7.3); a literal as P-155"
    r  "P-160" "4.1" @"[\-a]" (ok @"[\- a]") [] "critique class mode: '\\-' is a literal '-'"
    r  "P-161" "4.1" "[]a]" (ok @"[\] a]") [] "I4: ']' right after '[' is a literal"
    r  "P-162" "4.1" "[!]a]" (ok @"[^\] a]") ["XP117"] "I4: ']' right after '[!' is a literal"
    r  "P-163" "4.1" "[^]a]" (ok @"[^\] a]") [] "I4: ']' right after '[^' is a literal"
    r  "P-164" "4.1" "[a-z-0]" (ok @"[a-z \- 0]") [] "a '-' right after a range is a literal"
    r  "P-165" "4.1" "[-a]" (ok @"[\- a]") [] "a leading '-' is a literal"
    r  "P-166" "4.1" "[a-]" (ok @"[a \-]") [] "a trailing '-' is a literal"
    r  "P-167" "4.1" @"[a\-z]" (ok @"[a \- z]") [] "an escaped '-' forms no range"
    r  "P-168" "4.1" @"[\]]" (ok @"[\]]") ["XP105"] "a one-character class of ']'"
    r  "P-169" "4.1" "[[]" (ok "[[]") ["XP105"] "'[' is plain inside a class"
    r  "P-170" "4.1" "[*?,{}()|]" (ok "[* ? , { } ( ) |]") [] "every normal-mode metacharacter is plain inside a class"
    r  "P-171" "4.1" @"[\!a]" (ok @"[\! a]") [] "an escaped '!' does not negate"
    r  "P-172" "4.1" "[a!]" (ok @"[a \!]") [] "'!' not first is a plain item; the XP117 replacement for the 3.x meaning"
    r  "P-173" "4.1" "a[ ]b" (ok @"'a' [\ ] 'b'") ["XP105"] "a class of a space"
    r  "P-174" "4.1" "a[b.txt" (err "XP002" 2) [] "changed in 4.0 (item 5): 3.x RegexParseException; prototype EscapingTests"
    mi "P-175" "4.1" "a[b.txt" (err "XP002" 2) [] "changed in 4.0 (item 5): 3.x literal in masks"
    r  "P-176" "4.1" "[]" (err "XP002" 1) [] "item 24, I4: a ']' right after '[' is a literal, so no closing ']'"
    r  "P-177" "4.1" "[!]" (err "XP002" 1) [] "I4"
    r  "P-178" "4.1" "[^]" (err "XP002" 1) [] "I4"
    mi "P-179" "4.1" "[abc" (err "XP002" 1) [] "5.9: unclosed '[' is an error (POSIX, bash: literal)"
    r  "P-180" "4.1" @"a[\]" (err "XP002" 2) [] "'\\]' is an escaped item, so the class is not closed"
    r  "P-181" "4.1" "[a/b]" (err "XP010" 1) [] "changed in 4.0 (item 24): 3.x regex meaning; D34: class errors positioned at the '['"
    mi "P-182" "4.1" "[a/b]" (err "XP010" 1) [] "changed in 4.0 (item 24): 3.x split into segments in masks"
    r  "P-183" "4.1" @"[a\/b]" (err "XP010" 1) [] "4.1: '\\/' inside a class is XP010"
    r  "P-184" "4.1" "x[/]" (err "XP010" 2) [] "item 24"
    r  "P-185" "4.1" "[z-a]" (err "XP010" 1) [] "changed in 4.0 (item 24): reversed range (3.x rule: regex crash)"
    r  "P-186" "4.1" "[[:alpha:]]" (err "XP010" 1) [] "changed in 4.0 (item 24): POSIX class names reserved"
    r  "P-187" "4.2" "a]b.txt" (err "XP004" 2) [] "changed in 4.0: 3.x a lone ']' was a regex literal and matched (prototype EscapingTests); I9"
    mi "P-188" "4.2" "a]b" (err "XP004" 2) [] "changed in 4.0: 3.x literal; 5.9 stray ']' is an error"

    // ---- 4.2 braces ---------------------------------------------------------------------------------
    r  "P-189" "4.2" "{a,b}" (ok "{'a' , 'b'}") [] "changed in 4.0 (item 6): 3.x literal text"
    r  "P-190" "4.2" "*.{dll,pdb}" (ok "* '.' {'dll' , 'pdb'}") [] "changed in 4.0 (item 6)"
    mi "P-191" "4.2" "src/{a,b}/*.cs" (ok "'src' / {'a' , 'b'} / * '.cs'") [] "changed in 4.0 (item 6): 3.x literal; XP104 on evidence only"
    mi "P-192" "5.9" "*.{js,ts}" (ok "* '.' {'js' , 'ts'}") [] "picomatch-style braces, in every context"
    r  "P-193" "4.2" "{a,}" (ok "{'a' , <>}") [] "an empty alternative"
    r  "P-194" "4.2" "{,}" (ok "{<> , <>}") [] "two empty alternatives"
    r  "P-195" "4.2" "{a,{b,c}}" (ok "{'a' , {'b' , 'c'}}") [] "nested braces"
    r  "P-196" "4.2" "{a/b,c}" (ok "{'a' / 'b' , 'c'}") [] "an alternative may hold a separator"
    r  "P-197" "4.2" "{a/,b}c" (ok "{'a' / , 'b'} 'c'") [] "D29: a separator inside the pattern is an ordinary one"
    mi "P-198" "4.2" "src/{a/,b}x.cs" (ok "'src' / {'a' / , 'b'} 'x.cs'") [] "example 23: valid"
    mi "P-199" "4.2" "{a,b}/" (ok "{'a' , 'b'} /") [] "D29: the honest spelling of a directory alternation"
    r  "P-200" "4.1" "{a,(n:b),c}" (ok "{'a' , (n: 'b') , 'c'}") [] "I5"
    r  "P-201" "4.1" "{(n:b,c),d}" (ok "{(n: 'b,c') , 'd'}") [] "I5: the comma inside the group is a char"
    r  "P-202" "4.1" "{[,],x}" (ok "{',' , 'x'}") [] "a ',' in a class is plain; no XP105; a literal as P-157"
    r  "P-203" "4.2" "{*,abc}.cs" (ok "{* , 'abc'} '.cs'") [] "wildcards in alternatives"
    r  "P-204" "4.2" "{a,b}{c,d}" (ok "{'a' , 'b'} {'c' , 'd'}") [] "adjacent braces"
    r  "P-205" "4.1" @"{(n:a\,b),c}" (ok "{(n: 'a' / ',b') , 'c'}") ["XP110"] "inside a group in braces ',' is a char, so '\\,' is a separator then ','"
    r  "P-206" "4.2" "{x}" (err "XP005" 1) [] "changed in 4.0 (item 7): 3.x literal"
    r  "P-207" "4.2" "a{b}.txt" (err "XP005" 2) [] "changed in 4.0 (item 7): 3.x literal (prototype EscapingTests)"
    mi "P-208" "4.2" "a{b}.txt" (err "XP005" 2) [] "changed in 4.0 (item 7)"
    r  "P-209" "4.2" "{}" (err "XP005" 1) [] "no top-level comma"
    r  "P-210" "4.2" "{a{b,c}}" (err "XP005" 1) [] "the outer braces have no top-level comma"
    r  "P-211" "4.2" "x.y+z!=w{1}$^" (err "XP005" 9) [] "changed in 4.0 (item 7): PatternAstTests corpus (3.x literal braces)"
    mi "P-212" "5.9" "{1..3}" (err "XP005" 1) [] "kept open for ranges (bash: a sequence)"
    r  "P-213" "4.2" "{a,b" (err "XP004" 1) [] "changed in 4.0: 3.x literal"
    mi "P-214" "4.2" "a{b" (err "XP004" 2) [] "changed in 4.0: 3.x literal"
    r  "P-215" "4.2" "a}b" (err "XP004" 2) [] "changed in 4.0: 3.x literal; I9"
    r  "P-216" "4.1" @"{a\,b}" (err "XP017" 3) [] "D24: '\\,' directly inside braces (3.x: a separator, then ',')"
    mi "P-217" "4.1" @"{a,b\,c}" (err "XP017" 5) [] "D24"
    mi "P-218" "4.2" "{a/,b}" (err "XP019" 1) [] "changed in 4.0 (item 36): 3.x literal text"
    r  "P-219" "4.2" "{a/,b}" (err "XP019" 1) [] "XP019 in a rule too (5.1 table)"
    mi "P-220" "4.2" "src/{a/,b}" (err "XP019" 5) [] "example 23"
    mi "P-221" "4.2" "{a,b/}" (err "XP019" 1) [] "D29"
    mi "P-222" "4.2" "x/{a,{b/,c}}" (errAnyPos "XP019") [] "SPEC? nested: the spec does not say whether the position is the outer or the inner '{'"

    // ---- 4.2 groups ---------------------------------------------------------------------------------
    r  "P-223" "5.6" "out/(cfg:*)/(fwk:*)/(name:*).dll" (ok "'out' / (cfg: *) / (fwk: *) / (name: *) '.dll'") [] "unchanged; example 1; build.fsx"
    r  "P-224" "5.6" "(arch:*)/(platform:*)/autoexec.(ext:*)" (ok "(arch: *) / (platform: *) / 'autoexec.' (ext: *)") [] "unchanged; PathTests.MatchGroupsInRule"
    r  "P-225" "5.6" "(arch:*)-(platform:*)-autoexec.(ext:*)" (ok "(arch: *) '-' (platform: *) '-autoexec.' (ext: *)") [] "unchanged; PathTests.MatchGroupsInRule"
    r  "P-226" "5.6" "(filename:(arch:*)-(platform:*)-autoexec).(ext:*)" (ok "(filename: (arch: *) '-' (platform: *) '-autoexec') '.' (ext: *)") [] "unchanged; nested groups; PathTests"
    r  "P-227" "5.6" "(apl:*/*)/autoexec.*" (ok "(apl: * / *) / 'autoexec.' *") [] "unchanged parse; a group holding '/' (PathTests; C12: 3.x Path.matches crashed on it)"
    r  "P-228" "5.6" "(name:(plat:*)-a.ss)" (ok "(name: (plat: *) '-a.ss')") [] "unchanged; XakeScriptTests"
    r  "P-229" "5.6" "(root:*/*-a).ss" (ok "(root: * / * '-a') '.ss'") [] "unchanged; XakeScriptTests"
    r  "P-230" "5.6" "(root:*/(leaf:*-a)).ss" (ok "(root: * / (leaf: * '-a')) '.ss'") [] "prototype PatternAstTests (glob)"
    r  "P-231" "5.6" "gen/*/(name:*)-?.txt" (ok "'gen' / * / (name: *) '-' ? '.txt'") [] "prototype PatternAstTests (format)"
    r  "P-232" "5.6" "**/(n:*)/?*.(ext:c?)" (ok "** / (n: *) / ? * '.' (ext: 'c' ?)") [] "prototype PatternAstTests (format)"
    r  "P-233" "4.2" "*(name:x)" (ok "* (name: 'x')") [] "D28: '(name:' is a group after '*'"
    r  "P-234" "4.2" "?(name:x)" (ok "? (name: 'x')") [] "D28"
    r  "P-235" "4.1" "(_x:*)" (ok "(_x: *)") [] "D21: '_' starts a name"
    r  "P-236" "4.1" "(x1:*)" (ok "(x1: *)") [] "D21: digits after the first character"
    r  "P-237" "4.1" "(é:*)" (ok "(é: *)") [] "D21: Unicode letters"
    r  "P-238" "4.1" "(Ñame_2:*)" (ok "(Ñame_2: *)") [] "D21"
    r  "P-239" "4.2" "(n:)" (ok "(n: <>)") [] "an empty group body (group_body = { r_element })"
    r  "P-240" "4.2" "(n:(m:(k:*)))" (ok "(n: (m: (k: *)))") [] "nested three deep"
    r  "P-241" "4.2" "(x:{a,b})-{1,2}" (ok "(x: {'a' , 'b'}) '-' {'1' , '2'}") [] "example 13: the replacement for a repeated name"
    r  "P-242" "4.2" "src/(dir:*)/(p:*).fsproj" (ok "'src' / (dir: *) / (p: *) '.fsproj'") [] "example 13"
    r  "P-243" "4.2" "(pat:**/*).dll" (ok "(pat: ** / *) '.dll'") [] "example 3 (pinned)"
    r  "P-244" "4.2" "(pat:**/*)/main.xml" (ok "(pat: ** / *) / 'main.xml'") [] "example 3 (pinned)"
    r  "P-245" "4.2" "signed/(name:*).(ext:{dll,exe,nupkg})" (ok "'signed' / (name: *) '.' (ext: {'dll' , 'exe' , 'nupkg'})") [] "example 8: the 4.0 spelling"
    r  "P-246" "4.2" "signed/(name:*).(ext:dll|exe|nupkg)" (err "XP003" 25) [] "changed in 4.0 (item 4): 3.x regex alternation; example 8; hermetic Sign.fs"
    r  "P-247" "4.2" "a|b.txt" (err "XP003" 2) [] "changed in 4.0 (item 3): 3.x regex alternation over the whole pattern; example 8"
    r  "P-248" "4.2" "a(b.txt" (err "XP003" 2) [] "changed in 4.0 (item 5): 3.x RegexParseException; example 17"
    r  "P-249" "4.2" "a)b.txt" (err "XP003" 2) [] "changed in 4.0 (item 5); prototype EscapingTests"
    r  "P-250" "4.2" "x(y)z.txt" (err "XP003" 2) [] "changed in 4.0 (item 3): 3.x a numbered regex group; prototype EscapingTests"
    r  "P-251" "4.2" "fun(ction).o" (err "XP003" 4) [] "changed in 4.0 (item 3); PatternAstTests corpus"
    r  "P-252" "4.2" "lit(x)/*.txt" (err "XP003" 4) [] "changed in 4.0 (item 3): 3.x group 1, the wildcard was 2 (prototype)"
    r  "P-253" "4.2" "(name:a)b)c" (err "XP003" 10) [] "a stray ')' after a closed group; prototype EscapingTests"
    r  "P-254" "4.2" "(a b:*)" (err "XP003" 1) [] "5.6: not a group, the '(' is reserved"
    r  "P-255" "4.2" "(:*)" (err "XP003" 1) [] "5.6"
    r  "P-256" "4.2" "(a-b:*)" (err "XP003" 1) [] "5.6"
    r  "P-257" "4.2" "@(a)" (err "XP003" 2) [] "D28: extglob room"
    r  "P-258" "4.2" "+(a)" (err "XP003" 2) [] "D28"
    mi "P-259" "5.9" "!(a)" (err "XP003" 2) [] "D28; picomatch extglob negation"
    r  "P-260" "4.2" "*(a)" (err "XP003" 2) [] "D28: '*(' followed by a non-group '('"
    r  "P-261" "4.2" "?(a)" (err "XP003" 2) [] "D28"
    mi "P-262" "5.9" "@(a|b)" (err "XP003" 2) [] "picomatch extglob"
    r  "P-263" "4.2" "out/(cfg:*" (err "XP006" 5) [] "changed in 4.0 (item 20): 3.x ran to the end silently"
    r  "P-264" "4.2" "out/(cfg:*)/(fwk:*/(name:*).dll" (err "XP006" 13) [] "changed in 4.0 (item 20); prototype PatternAstTests (UnclosedGroup fwk)"
    r  "P-265" "4.1" "(1:*)" (err "XP007" 1) [] "changed in 4.0 (item 18): 3.x collided with the positional '1'; D34: position at the '(' rather than the digit"
    r  "P-266" "4.1" "out/(2x:*)" (err "XP007" 5) [] "item 18"
    r  "P-267" "4.2" "src/(p:*)/(p:*).fsproj" (err "XP015" 11) [] "changed in 4.0 (item 19): 3.x the last value won; example 13; position: the second occurrence"
    r  "P-268" "4.2" "{(x:a)-1,(x:b)-2}" (err "XP015" 10) [] "changed in 4.0 (item 19): across alternatives (D4)"
    r  "P-269" "4.2" "(x:*)/(x:*)" (err "XP015" 7) [] "D4"
    r  "P-270" "4.2" "(x:(x:a))" (err "XP015" 4) [] "D4: nested repetition"
    mi "P-271" "4.2" "out/(cfg:*)/*.dll" (err "XP008" 5) [] "changed in 4.0 (item 17): 3.x include looked for the literal directory '(cfg:*)'; example 18"
    me "P-272" "4.2" "(cfg:Debug)/**/*" (err "XP008" 1) [] "changed in 4.0 (item 17): 3.x the literal directory; example 18"
    mi "P-273" "4.2" "(name:*).c" (err "XP008" 1) [] "changed in 4.0 (item 17): prototype EscapingTests (include found nothing)"
    me "P-274" "4.2" "(name:*).c" (err "XP008" 1) [] "changed in 4.0 (item 17): 3.x exclude wildcard segment matched the body"
    mi "P-275" "4.2" "{(n:b,c),d}" (err "XP008" 2) [] "no group token in a mask, also inside braces"
    mi "P-276" "4.2" "a(b" (err "XP003" 2) [] "changed in 4.0 (item 5): 3.x literal in masks (D24: XP003 when not 'name:')"
    mi "P-277" "4.2" "fun(ction).o" (err "XP003" 4) [] "changed in 4.0 (item 3)"
    mi "P-278" "4.2" "a|b.txt" (err "XP003" 2) [] "changed in 4.0 (item 3): 3.x literal in an include"
    me "P-279" "4.2" "b|a1*.txt" (err "XP003" 2) [] "changed in 4.0 (item 3): 3.x alternation in an exclude wildcard segment (prototype EscapingTests)"
    mi "P-280" "4.2" "a)b" (err "XP003" 2) [] "changed in 4.0 (item 5)"
    mi "P-281" "4.2" "(1:*)" (err "XP003" 1) [] "D34: in a mask '(1:' does not read 'name:' (a digit cannot start a name), so XP003 per D24 rather than XP007 or XP008"
    mi "P-282" "4.2" @"C:\Program Files (x86)\Tool\*.dll" (err "XP003" 18) [] "changed in 4.0: example 19 (3.x read the parentheses literally)"
    mi "P-283" "5.7" @"C:/Program Files \(x86\)/Tool/*.dll" (ok "'C:' / 'Program Files (x86)' / 'Tool' / * '.dll'") [] "example 19: escapePath output, no warning"

    // ---- hermetic and docs corpus ------------------------------------------------------------------
    mi "P-284" "10.3" "**/*.cs" (ok "** / * '.cs'") [] "unchanged; hermetic description masks; example 4"
    me "P-285" "10.3" "obj/**/*" (ok "'obj' / ** / *") [] "unchanged; hermetic description masks"
    me "P-286" "10.3" "bin/**/*" (ok "'bin' / ** / *") [] "unchanged; hermetic description masks"
    me "P-287" "10.3" "d*/**/*" (ok "'d' * / ** / *") [] "unchanged; FilesetTests case test"
    me "P-288" "10.3" "Dir2/**/*" (ok "'Dir2' / ** / *") [] "changed in 4.0 at scan level (item 25): 3.x literal exclude segments ignored case"
]

// =================================================================================================
// Error message texts (spec 7.2): `PatternError.ToString()` and the `PatternException` message
// =================================================================================================

type MessageVector =
    { Id: string; Context: Ctx; Input: string; Message: string; Note: string }
    override v.ToString() = v.Id

let private msg id ctx input message note = { Id = id; Context = ctx; Input = input; Message = message; Note = note }

let messages : MessageVector list = [
    msg "M-001" Ctx.Rule "a(b.txt"
        @"Invalid pattern 'a(b.txt' (rule) at character 2: '(' is reserved (Xake 3.x: a regex group); write \( for a literal '(' [XP003]"
        "example 17"
    msg "M-002" Ctx.Mask "a(b"
        @"Invalid pattern 'a(b' (mask) at character 2: '(' is reserved (Xake 3.x: a literal character in masks); write \( for a literal '(', or build the mask from a real path with PatternAst.escapePath [XP003]"
        "7.2 XP003, mask text"
    msg "M-003" Ctx.Rule "signed/(name:*).(ext:dll|exe|nupkg)"
        "Invalid pattern 'signed/(name:*).(ext:dll|exe|nupkg)' (rule) at character 25: '(ext:dll|exe|nupkg)': '|' is reserved (Xake 3.x: a regex alternation); write (ext:{dll,exe,nupkg}) [XP003]"
        "example 8; D34: the position is the '|' itself (25), not the group's '(' (17)"
    msg "M-004" Ctx.Rule "a[b.txt"
        @"Invalid pattern 'a[b.txt' (rule) at character 2: an unescaped '[' has no matching ']' (Xake 3.x read it as a literal in masks and failed in rules); write \[ for a literal '[' [XP002]"
        "7.2 XP002"
    msg "M-005" Ctx.Rule "{a,b"
        @"Invalid pattern '{a,b' (rule) at character 1: an unescaped '{' has no matching '}' (Xake 3.x: a literal); write \{ for a literal '{' [XP004]"
        "7.2 XP004 '{'"
    msg "M-006" Ctx.Rule "a}b"
        @"Invalid pattern 'a}b' (rule) at character 2: an unescaped '}' closes no '{' (Xake 3.x: a literal); write \} for a literal '}' [XP004]"
        "7.2 XP004 '}'"
    msg "M-007" Ctx.Mask "a]b"
        @"Invalid pattern 'a]b' (mask) at character 2: an unescaped ']' closes no '[' (Xake 3.x: a literal); write \] for a literal ']' [XP004]"
        "7.2 XP004 ']'"
    msg "M-008" Ctx.Rule "{x}"
        @"Invalid pattern '{x}' (rule) at character 1: '{x}' is not an alternation (no ','; Xake 3.x read it as literal text); write \{x\} for literal braces, or {a,b} for alternatives [XP005]"
        "7.2 XP005 (the input is the text the reason quotes)"
    msg "M-009" Ctx.Rule "out/(name:*"
        "Invalid pattern 'out/(name:*' (rule) at character 5: the group '(name:' is not closed (Xake 3.x: in a string rule it ran to the end of the pattern); add ')' [XP006]"
        "7.2 XP006 (the group is named 'name', as in the reason text)"
    msg "M-010" Ctx.Rule "(1:*)"
        "Invalid pattern '(1:*)' (rule) at character 1: the group name '1' starts with a digit; it would collide with the positional match '1' (Xake 3.x accepted it); rename it [XP007]"
        "7.2 XP007"
    msg "M-011" Ctx.Mask "out/(cfg:*)/*.dll"
        @"Invalid pattern 'out/(cfg:*)/*.dll' (mask) at character 5: named groups '(name:...)' exist only in rule patterns (Xake 3.x: an include read the group as literal text, an exclude wildcard segment matched its body); write the body alone, or \( \) for literal parentheses; PatternAst.escapePath escapes a real path [XP008]"
        "example 18"
    msg "M-012" Ctx.Rule "[a/b]"
        "Invalid pattern '[a/b]' (rule) at character 1: invalid character class '[a/b]': a class never matches '/' [XP010]"
        "7.2 XP010"
    msg "M-013" Ctx.Rule "[z-a]"
        "Invalid pattern '[z-a]' (rule) at character 1: invalid character class '[z-a]': the range 'z-a' is reversed [XP010]"
        "7.2 XP010"
    msg "M-014" Ctx.Rule "obj/"
        "Invalid pattern 'obj/' (rule) at character 4: a rule pattern names a file or a phony target and cannot end with '/' (in Xake 3.x it matched nothing) [XP011]"
        "7.2 XP011"
    msg "M-015" Ctx.Rule "src/*/../x.cs"
        "Invalid pattern 'src/*/../x.cs' (rule) at character 7: '..' is allowed only before the first wildcard (Xake 3.x applied it as a parent step in masks and never matched it in rules) [XP012]"
        "7.2 XP012 '..'"
    msg "M-016" Ctx.Mask "src/*/./a.cs"
        "Invalid pattern 'src/*/./a.cs' (mask) at character 7: '.' is allowed only before the first wildcard; remove the segment [XP012]"
        "example 23"
    msg "M-017" Ctx.Rule "src/(p:*)/(p:*).fsproj"
        "Invalid pattern 'src/(p:*)/(p:*).fsproj' (rule) at character 11: the group name 'p' appears 2 times; a name may appear once per pattern (Xake 3.x let each occurrence capture and the last one win); rename one occurrence, or put one group around the braces: (x:{a,b}) [XP015]"
        "example 13"
    msg "M-018" Ctx.Mask "//*/share"
        "Invalid pattern '//*/share' (mask) at character 3: a UNC root '//server/share' needs a literal server and share name [XP016]"
        "SPEC? whether '//server/share' in the reason is literal text or the pattern's root; position as P-048"
    msg "M-019" Ctx.Rule @"{a\,b}"
        @"Invalid pattern '{a\,b}' (rule) at character 3: '\,' is not an escaped comma in Xake 4.0 (Xake 3.x: a path separator, then ','); write [,] for a literal comma [XP017]"
        "7.2 XP017"
    msg "M-020" Ctx.Rule ""
        "Invalid pattern '' (rule): an empty pattern names nothing; write the file or directory path, or '*' for the files of the base directory [XP018]"
        "7.2 XP018: no position, so no 'at character'"
    msg "M-021" Ctx.Mask "{a/,b}"
        "Invalid pattern '{a/,b}' (mask) at character 1: '{a/,b}' ends with '/' in one alternative only; write {a,b}/ when every alternative is a directory, or two patterns [XP019]"
        "7.2 XP019 (the input is the text the reason quotes)"
]

// =================================================================================================
// Parse-time warning texts (spec 7.3): the `Issue.Text` of one part
// =================================================================================================

type IssueTextVector =
    { Id: string; Context: Ctx; Role: Role; Input: string; Code: string; Text: string; Note: string }
    override v.ToString() = v.Id

let private iss id ctx role input code text note =
    { Id = id; Context = ctx; Role = role; Input = input; Code = code; Text = text; Note = note }

let issueTexts : IssueTextVector list = [
    iss "I-001" Ctx.Rule Role.NoRole @"src\*.cs" "XP110"
        @"'\' as a path separator is deprecated and is only an escape from Xake 5.0; use '/'" "7.3 XP110"
    iss "I-002" Ctx.Mask Role.Include @"src\*.cs" "XP110"
        @"'\' as a path separator is deprecated and is only an escape from Xake 5.0; use '/'" "example 11"
    iss "I-003" Ctx.Rule Role.NoRole "a**b" "XP112"
        "'a**b' matches within one segment from Xake 4.0, like 'a*b'; in Xake 3.x it crossed '/'; write '*' for one segment, or '/**/' between segments for any depth"
        "7.3 XP112 (the input is the text the part quotes)"
    iss "I-004" Ctx.Rule Role.NoRole "[(]*.ss" "XP105"
        @"'[(]' is the literal '(' (as in Xake 3.x); write \( instead" "example 7"
    iss "I-005" Ctx.Rule Role.NoRole "lib[x].dll" "XP105"
        "'[x]' is the literal 'x'; write x instead" "7.3 XP105, ordinary character"
    iss "I-006" Ctx.Mask Role.Include "sub[1]/*.c" "XP106"
        @"'[1]' matches the single character '1' from Xake 4.0; Xake 3.x read the literal text '[1]'; write \[1\] for the brackets, or 1 for the character"
        "example 6"
    iss "I-007" Ctx.Rule Role.NoRole "[!a]" "XP117"
        "'[!a]' is a negated class (any character but 'a') from Xake 4.0; in Xake 3.x it was a class of '!' and 'a'; write [^a] for \"not a\" (same meaning in 3.x and 4.0), or [a!] for the 3.x meaning"
        "7.3 XP117"
    iss "I-008" Ctx.Mask Role.Exclude "a[(]*.c" "XP105"
        @"'[(]' is the literal '(' (as in Xake 3.x); write \( instead" "exclude wildcard segment"
]

// =================================================================================================
// unparse (spec 5.7): from a shape, from a pattern, and round trips
// =================================================================================================

type UnparseInput =
    /// an AST given as a shape (see the header); expected: `unparse ast`
    | FromShape of shape: string
    /// a pattern text; expected: `unparse (parse text)`
    | FromText of ctx: Ctx * text: string

type UnparseExpect =
    /// exactly this text
    | Text of string
    /// `parse (unparse ast) = ast`; the text itself is not asserted (the spec leaves its form open)
    | RoundTrip

type UnparseVector =
    { Id: string; Input: UnparseInput; Expect: UnparseExpect; Note: string }
    override v.ToString() = v.Id

let private ush id shape expected note = { Id = id; Input = FromShape shape; Expect = Text expected; Note = note }
let private utx id ctx text expected note = { Id = id; Input = FromText (ctx, text); Expect = Text expected; Note = note }
let private urt id ctx text note = { Id = id; Input = FromText (ctx, text); Expect = RoundTrip; Note = note }

let unparse : UnparseVector list = [
    ush "U-001" "'src' / * '.cs'" "src/*.cs" "5.7"
    ush "U-002" "'a(b'" @"a\(b" "5.7: reserved characters escaped with '\\'"
    ush "U-003" "'a*b'" "a[*]b" "5.7: '*' in a literal as '[*]' (5.0: '\\*')"
    ush "U-004" "'a?b'" "a[?]b" "5.7"
    ush "U-005" @"'a\\b'" @"a[\\]b" "5.7: '\\' in a literal as '[\\\\]'"
    ush "U-006" "'out' / (fwk: *) / (lib: *) '.dll'" "out/(fwk:*)/(lib:*).dll" "prototype PatternAstTests"
    ush "U-007" "** / 'b'" "**/b" "globstar"
    ush "U-008" "'a' **~ 'b'" "a**b" "a segment-local double star is kept for unparse (4.4)"
    ush "U-009" "{'a' , 'b'} '.c'" "{a,b}.c" "braces"
    ush "U-010" "[a-z] '.c'" "[a-z].c" "a class"
    ush "U-011" "[^a] '.c'" "[^a].c" "D34: a negated class unparses as '[^...]' (the spelling that never warns, 5.2 notes) rather than '[!...]'"
    ush "U-012" "'C:' / 'x'" "C:/x" "a drive root"
    ush "U-013" "/ / 'srv' / 'sh' / *" "//srv/sh/*" "a UNC root"
    ush "U-014" "'a,b'" "a,b" "',' outside braces needs no escape"
    ush "U-015" "{'a,b' , 'c'}" "{a[,]b,c}" "D34: a ',' in a literal inside braces must be written '[,]'; 5.7 does not list ','"
    ush "U-016" "'a' /" "a/" "a mask's directory mark"
    ush "U-017" "(n: <>)" "(n:)" "an empty group body"
    ush "U-018" "{<> , 'x'}" "{,x}" "an empty alternative"
    ush "U-019" "'[1]'" @"\[1\]" "the literal text '[1]' (XP106's suggested spelling)"
    ush "U-020" "'{x}'" @"\{x\}" "the literal text '{x}' (XP005's suggested spelling)"
    ush "U-021" "'café' *" "café*" "non-ASCII literal text is not escaped"
    utx "U-022" Ctx.Rule @"fun\(ction\).o" @"fun\(ction\).o" "prototype PatternAstTests: canonical text is stable"
    utx "U-023" Ctx.Rule @"lit\(x\)/\[y\]\|\{z\}.txt" @"lit\(x\)/\[y\]\|\{z\}.txt" "prototype PatternAstTests"
    utx "U-024" Ctx.Mask @"src\(x\)\*.cs" @"src\(x\)/*.cs" "5.7: Sep unparses as '/' (the XP110 '\\' is not kept)"
    utx "U-025" Ctx.Rule "a//b" "a/b" "repeated separators collapsed (item 34)"
    urt "U-026" Ctx.Rule "lit[(]x[)].txt" "changed from the prototype: '[c]' is a Class in 4.0, no longer a Lit (prototype: unparse gave 'lit\\(x\\).txt')"
    urt "U-027" Ctx.Rule @"[\-a]" "class items that need escaping inside a class"
    urt "U-028" Ctx.Rule "[]a]" "a literal ']' first in a class"
    urt "U-029" Ctx.Rule "[a-z-0]" "a literal '-' after a range"
    urt "U-030" Ctx.Rule "{[,],x}" "a ',' class inside braces"
    urt "U-031" Ctx.Rule "{(n:b,c),d}" "a ',' char inside a group inside braces"
    urt "U-032" Ctx.Rule "(filename:(arch:*)-(platform:*)-autoexec).(ext:*)" "nested groups"
    urt "U-033" Ctx.Mask "//server/share/*.dll" "a UNC root"
    urt "U-034" Ctx.Rule "***" "a segment-local double star followed by a star"
    urt "U-035" Ctx.Rule @"a[\\]b" "a backslash class"
    urt "U-036" Ctx.Mask "x/{a/,b}c" "a separator at the end of an alternative, inside the pattern"
    urt "U-037" Ctx.Rule "{a,}" "an empty last alternative"
    urt "U-038" Ctx.Rule "[!]a]" "a negated class whose first item is ']'"
    urt "U-039" Ctx.Rule "a[ ]b" "a class of a space"
]

// =================================================================================================
// escape and escapePath (spec 5.7, D7)
// =================================================================================================

type EscapeVector =
    { Id: string; Input: string; Expected: string; Note: string }
    override v.ToString() = v.Id

let private esc id input expected note = { Id = id; Input = input; Expected = expected; Note = note }

let escape : EscapeVector list = [
    esc "X-001" "abc" "abc" "nothing to escape"
    esc "X-002" "a(b)" @"a\(b\)" "reserved characters"
    esc "X-003" "[x]" @"\[x\]" "reserved characters"
    esc "X-004" "{a,b}" @"\{a,b\}" "',' outside braces is a char"
    esc "X-005" "a|b" @"a\|b" "reserved character"
    esc "X-006" "a]b" @"a\]b" "a lone ']'"
    esc "X-007" "a*b" "a[*]b" "5.7 (5.0: '\\*')"
    esc "X-008" "a?b" "a[?]b" "5.7"
    esc "X-009" "**" "[*][*]" "5.7"
    esc "X-010" @"a\b" @"a[\\]b" "5.7: every '\\' a literal backslash"
    esc "X-011" "a/b" "a/b" "5.7: 'parse (escape s)' is '[Lit s]' per segment, so '/' stays a separator"
    esc "X-012" @"C:\Program Files (x86)\Tool" @"C:[\\]Program Files \(x86\)[\\]Tool" "example 19: why escape is wrong for a real path"
    esc "X-013" "a(b" @"a\(b" "example 16: a bound value"
    esc "X-014" "{7}" @"\{7\}" "prototype PatternAstTests (glob)"
    esc "X-015" "!#.,;" "!#.,;" "no other character is special"
    esc "X-016" "" "" "D34: the empty string escapes to itself (it is not parsed)"
]

let escapePath : EscapeVector list = [
    esc "Y-001" @"C:\Program Files (x86)\Tool" @"C:/Program Files \(x86\)/Tool" "5.7, example 19"
    esc "Y-002" "/home/u/src" "/home/u/src" "nothing to escape"
    esc "Y-003" @"\\server\share\dir" "//server/share/dir" "5.7: a UNC root is kept as the root"
    esc "Y-004" "/tmp/p[q]" @"/tmp/p\[q\]" "reserved characters"
    esc "Y-005" "/tmp/a*b?" "/tmp/a[*]b[?]" "'*' and '?' as classes"
    esc "Y-006" "/tmp/p{q}|r" @"/tmp/p\{q\}\|r" "reserved characters"
    esc "Y-007" "C:/x/y" "C:/x/y" "a drive with '/'"
    esc "Y-008" @"rel\dir\a.cs" "rel/dir/a.cs" "every '\\' is a separator"
    esc "Y-009" "/tmp/a,b" "/tmp/a,b" "',' outside braces is a char"
    esc "Y-010" "/tmp/xake-root-(a)[b]{c}|d" @"/tmp/xake-root-\(a\)\[b\]\{c\}\|d" "prototype EscapingTests root names"
    esc "Y-011" @"C:\a\" @"C:/a/" "D34: a trailing separator is kept (a directory mask)"
]

// =================================================================================================
// groupNames and wildcards (spec 8.2, 5.6)
// =================================================================================================

type GroupNamesVector =
    { Id: string; Input: string; Expected: string list; Note: string }
    override v.ToString() = v.Id

let private gn id input expected note = { Id = id; Input = input; Expected = expected; Note = note }

/// Rule patterns; `groupNames (Rule.parse input)`, first-appearance order, nested included.
let groupNames : GroupNamesVector list = [
    gn "G-001" "out/(cfg:*)/(fwk:*)/(name:*).dll" [ "cfg"; "fwk"; "name" ] "example 1"
    gn "G-002" "(filename:(arch:*)-(platform:*)-autoexec).(ext:*)" [ "filename"; "arch"; "platform"; "ext" ] "nested: outer first"
    gn "G-003" "**/(n:*)/?*.(ext:c?)" [ "n"; "ext" ] "prototype PatternAstTests"
    gn "G-004" "src/*.cs" [] "no groups"
    gn "G-005" "{a,(n:b),c}-(m:*)" [ "n"; "m" ] "a group inside braces"
    gn "G-006" "(x:{a,b})-{1,2}" [ "x" ] "example 13"
    gn "G-007" "(root:*/(leaf:*-a)).ss" [ "root"; "leaf" ] "prototype PatternAstTests"
    gn "G-008" "lock::(name:*)::(cfg:*)" [ "name"; "cfg" ] "build.fsc.fsx"
    gn "G-009" "(n:(m:(k:*)))" [ "n"; "m"; "k" ] "nested three deep"
    gn "G-010" "out/(fwk:*)/(lib:*).dll" [ "fwk"; "lib" ] "example 2: same set as G-011 required by XP013 (multi-output, rule creation; not a parse check)"
    gn "G-011" "out/(fwk:*)/(lib:*).xml" [ "fwk"; "lib" ] "example 2"
    gn "G-012" "out/(fwk:*)/(name:*).xml" [ "fwk"; "name" ] "example 2: differs from G-010, so the pair is XP013 when the rule is created"
]

type WildcardsVector =
    { Id: string; Input: string; Expected: string; Note: string }
    override v.ToString() = v.Id

let private wc id input expected note = { Id = id; Input = input; Expected = expected; Note = note }

/// Rule patterns; `wildcards (Rule.parse input)` rendered as "<token shape> <number>" joined by "; ".
let wildcards : WildcardsVector list = [
    wc "W-001" "gen/*/(name:*)-?.txt" "* 1; ? 3" "prototype PatternAstTests: the group's '*' takes 2"
    wc "W-002" "**/(n:*)/?*.(ext:c?)" "** 1; ? 3; * 4" "prototype PatternAstTests (DoubleStar there, Globstar in 4.0)"
    wc "W-003" "out/(cfg:*)/(fwk:*)/(name:*).dll" "" "only grouped wildcards"
    wc "W-004" "dd/*.dll" "* 1" "example 3"
    wc "W-005" "**/*.dll" "** 1; * 2" "example 9: 1 = \"\", 2 = a"
    wc "W-006" "a**b" "**~ 1" "a segment-local double star is one wildcard"
    wc "W-007" "[ab]*.c" "* 1" "5.6: classes take no number"
    wc "W-008" "(p:**)/*.c" "* 2" "the group's '**' takes 1"
    wc "W-009" "*/(a:*)/*" "* 1; * 3" "5.6"
    wc "W-010" "(pat:**/*).dll" "" "example 3: all inside a group"
    wc "W-011" "a/?/*/**" "? 1; * 2; ** 3" "every wildcard kind"
    wc "W-012" "{a,*}x?" "* 1; ? 2" "D34: wildcards inside braces are numbered (5.6), but whether 'wildcards' lists them as top-level is not stated"
]

// =================================================================================================
// specificity (spec 5.8)
// =================================================================================================

type SpecificityVector =
    { Id: string; Input: string; Expected: int * int * int; Note: string }
    override v.ToString() = v.Id

let private sp id input expected note = { Id = id; Input = input; Expected = expected; Note = note }

/// Rule patterns; `specificity (Rule.parse input)` = (literal characters, -globstars, -wildcard
/// weight) with weight 2 per `*` and `**~`, 1 per `?` and class (Path.specificity, #88/#89).
let specificity : SpecificityVector list = [
    sp "S-001" "*.dll" (4, 0, -2) "example 9"
    sp "S-002" "**/*.dll" (4, -1, -2) "changed in 4.0 (D5, item 33): 3.x (5, -1, -2), the '/' counted"
    sp "S-003" "*/?.dll" (5, 0, -3) "item 33"
    sp "S-004" "**/x.dll" (5, -1, 0) "changed in 4.0 (item 33): 3.x (6, -1, 0)"
    sp "S-005" "a/*/x.dll" (8, 0, -2) "unchanged"
    sp "S-006" "a/**/x.dll" (6, -1, 0) "changed in 4.0 (D5): 3.x (8, -1, 0)"
    sp "S-007" "a/?.dll" (6, 0, -1) "unchanged"
    sp "S-008" "a/*.dll" (6, 0, -2) "unchanged"
    sp "S-009" "out/(cfg:*)/(fwk:*)/(name:*).dll" (10, 0, -6) "unchanged; a group is the sum of its body"
    sp "S-010" "a**b" (2, 0, -2) "changed in 4.0 (item 13): 3.x (2, -1, 0)"
    sp "S-011" "**" (0, -1, 0) "unchanged"
    sp "S-012" "a/**" (1, -1, 0) "changed in 4.0 (D5): 3.x (2, -1, 0)"
    sp "S-013" "{a,bc}.txt" (5, 0, 0) "5.8: an alternation counts as its least specific alternative ('a')"
    sp "S-014" "{*,abc}.cs" (3, 0, -2) "5.8: the least specific alternative is '*'"
    sp "S-015" "[abc].x" (2, 0, -1) "5.8: a class weighs as '?'"
    sp "S-016" "//srv/sh/*.c" (11, 0, -2) "a UNC root: two Sep, the server and share literals"
    sp "S-017" "C:/a/*.c" (7, 0, -2) "a drive root: Lit 'C:' and Sep"
    sp "S-018" "(p:**)/b" (1, -1, 0) "D34: a Sep next to a group whose body ends in a Globstar counts 0 (group delimiters transparent, as in 4.3)"
    sp "S-019" @"a\(b" (3, 0, 0) "an escaped character is one literal character"
    sp "S-020" "clean::(b:*)::(cfg:*)" (9, 0, -4) "unchanged; RuleSpecificityTests phony patterns"
    sp "S-021" "**/**/b" (1, -1, 0) "D34: follows P-118 (adjacent globstars collapse to one)"
    sp "S-022" "lib[x].dll" (7, 0, -1) "changed in 4.0: 3.x (10, 0, 0) read '[x]' as three literal characters; the prototype read it as the literal 'x' (8, 0, 0)"
    sp "S-023" "a/**/b" (2, -1, 0) "D5"
    sp "S-024" "*/**/file.o" (6, -1, -2) "D5: both separators touch the globstar"
    sp "S-025" "{a/b,c}" (1, 0, 0) "least specific alternative 'c'"
    sp "S-026" "/a" (2, 0, 0) "a root Sep counts one literal character"
]

type SpecificityOrder =
    | FirstWins
    | Tie

type SpecificityComparison =
    { Id: string; First: string; Second: string; Order: SpecificityOrder; Note: string }
    override v.ToString() = v.Id

let private beats id first second note = { Id = id; First = first; Second = second; Order = FirstWins; Note = note }
let private ties id first second note = { Id = id; First = first; Second = second; Order = Tie; Note = note }

/// Pairs of rule patterns compared by `specificity`: `FirstWins` means first > second strictly.
let specificityComparisons : SpecificityComparison list = [
    beats "C-001" "*.dll" "**/*.dll" "D5, example 9 (3.x: '**/*.dll' scored higher, but did not match a root file)"
    beats "C-002" "*/?.dll" "**/x.dll" "changed in 4.0 (item 33): 3.x '**/x.dll' won for a/x.dll"
    beats "C-003" "out/(b:*)/obf/(fwk:*)/(asm:*).dll" "out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll" "unchanged; RuleSpecificityTests"
    beats "C-004" "a/*/x.dll" "a/**/x.dll" "unchanged; RuleSpecificityTests"
    beats "C-005" "a/?.dll" "a/*.dll" "unchanged; RuleSpecificityTests"
    ties  "C-006" "*/x.dll" "(cfg:*)/x.dll" "unchanged; RuleSpecificityTests: a group scores like a bare wildcard"
    beats "C-007" "clean::(b:*)::(cfg:*)" "clean::(b:*)" "unchanged; RuleSpecificityTests"
    beats "C-008" "build::special" "build::(x:*)" "unchanged; RuleSpecificityTests"
    ties  "C-009" "(a:*)::common" "(b:*)::common" "unchanged; RuleSpecificityTests (ambiguity warning)"
    ties  "C-010" "a*b" "a**b" "changed in 4.0 (item 13): 3.x 'a*b' won (no '**')"
    beats "C-011" "[ab].c" "*.c" "5.8: a class weighs 1"
    ties  "C-012" "?.c" "[ab].c" "5.8"
    ties  "C-013" "{a,b}.c" "a.c" "5.8: least specific alternative"
    beats "C-014" "{a,bcd}.c" "*.c" "5.8"
    ties  "C-015" "(p:**)/b" "**/b" "D34: see S-018"
    beats "C-016" "**/file.o" "*/**/file.o" "5.8: same literals and globstars, fewer wildcards"
    beats "C-017" "a/*/b" "a/**/b" "5.8"
    beats "C-018" "src/**/bin/*.o" "src/**/*.o" "5.8"
    beats "C-019" "x/a.dll" "**/*.dll" "5.8"
    beats "C-020" "a/?.dll" "a/**/?.dll" "5.8"
]
