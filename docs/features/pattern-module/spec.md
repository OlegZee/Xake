# Spec: the pattern module (complete, pending approval)

> Revision 2026-11-01 (Windows measurements pinned): the "Still unmeasured (Windows)" list of 6.4 is
> replaced by the facts measured on Windows 11 ARM64, NTFS, .NET 10, Xake 3.15.0 from nuget
> (`measurements-windows.md`, produced by `measure-windows.fsx`); 6.5, 9.5, 10.1, 5.2 (the `\`
> notes) and section 13 (C10 rewritten, S5, new records C14-C16) carry the facts. Per D30 only
> measured facts changed; no decision changed. **Nothing is unmeasured except what 6.4 lists
> under "Still unmeasured"** (file and directory symlinks and symlink cycles: the script lacked the
> privilege to create them; the .NET Framework 3-letter extension rule; an unreadable directory
> on Windows; cloud-sync placeholders; mixed-case NTFS order).
>
> Revision 2026-11-01 (Phase-2 measurements, macOS): every "pinned in / measured in Phase 2" item
> is replaced by the measured fact and a reference to `measurements-engine.md` (3.x pattern
> engine) or `measurements-dotnet.md` (.NET directory enumeration), both run on macOS. No
> decision changed. Added: 6.7 and 9.5 (implementation notes from the measurements), the
> "Still unmeasured (Windows)" list (6.4), behaviour-change additions in section 3 (items 12, 21,
> 23, 25, 26, 34, 35) and the records C12, C13 (section 13). **Windows items stay open and are
> marked unverified documentation**: Win32 kernel `?` and `*.*`, the DOS wildcard mapping of
> `"` `<` `>`, junctions and mount points under `**`, 8.3 names (6.4).
>
> Revision 2026-11-01 (final pass): decisions D24–D29 applied; the spec is complete pending
> approval. The choices derived in the first pass are decided (D24: `(` in a mask, `MaskRole`,
> XP120 after 5.0, behaviour change 33, separator collapsing, UNC roots, XP017). Documented
> limits: a probed literal keeps the mask's spelling (D25), `?` and classes match one UTF-16 code
> unit (D26), no Unicode normalization (D27) (5.4, 5.9). `(name:` is a group wherever it appears,
> a constraint on any future extglob (D28, 4.2). New errors: the empty pattern (XP018), `.` after
> a wildcard (XP012, as `..`), a brace alternative ending in a separator at the end of the
> pattern (XP019); a symlink skipped under `**` is logged at Message level (D29, 6.5). The
> review's deferred suggestions are resolved (section 12, "Resolved suggestions").
>
> Revision 2026-11-01: decisions D3–D23 applied (brief, "Decisions from the spec review") and the
> findings of the glob-conformance review (`critique-glob.md`: M1–M3, S1–S7, I1–I9, the "consider"
> items) addressed. Main changes: the regex translation is non-normative (section 9); two
> contexts, `Rule` and `Mask`, with entry points `Rule.parse` / `Mask.parse` (4, 8); a trailing
> `/` in a mask matches a directory (5.1, 6.2); a repeated group name, a named group in a mask and
> an unclosed group in a string rule are errors (XP015, XP008, XP006; XP111 and XP116 dropped);
> `escapePath` added; specificity no longer counts the `/` next to `**`; the open items are
> decided (section 12).
>
> Revision 2026-10-31: decision D1 applied: one semantics only, shipped in Xake 4.0. The
> second semantics level, its environment switch and the intermediate 3.x release are removed;
> legacy inputs warn or fail naming their 3.x meaning (sections 2, 3, 7).

Status: **complete, pending approval** (2026-11-01). Written 2026-10-26 from `brief.md` (sections a–f are decisions and are not
reopened here), the frozen prototype `feature/typed-matches` (`src/core/{PatternAst,Path,Fileset}.fs`,
`src/tests/{EscapingTests,EscapingDiskTests,PatternAstTests}.fs`, `escaping-today.md`) and the
engine on `dev` (`src/core/Path.fs`, `Fileset.fs`, `ExecCore.fs`); revised 2026-10-31 for D1 and
2026-11-01 for D3–D29. The 15 "Open for design" items of the brief, C11 and the review's deferred
suggestions are decided; section 12 records each decision and the alternatives considered.

**What is normative.** The requirements are the grammar (section 4), the semantics tables
(section 5) and the conformance vectors (Phase 2), together with the scanning rules (6), the
error and warning model (7) and the public API (8) that the vectors exercise. Section 9 (matcher
implementation) is not normative: it records how Phase 3 is expected to implement the
semantics and may change on measurement.

Conventions: "rule pattern" = the text of `..>`, `*..>`, `=>`, `target`, `targets`, `command`,
`phony` and of the typed layer. "Mask" = the text of `!!`, `ls`, `++`, `--`, `+`, `-`,
`fileset { includes / excludes / includefile / excludefile }`, `cp`/`rm` masks. The two parse
contexts are `Rule` and `Mask`. "Include" and "exclude" are the two **roles** of a mask: they
differ only in how the scanner uses the mask (6.2) and in which 3.x meaning a legacy warning
names (7.3), never in what a mask element means. "3.x" = the engine as released up to
3.12/3.15 (pattern code unchanged since `d002fe4`). "4.0" = what this spec defines.
"Prototype" = the frozen branch (#90/#91).

---

## 1. Scope and goals

In scope:

- One grammar for rule patterns and masks, with two start symbols (`rule`, `mask`); a mask is a
  rule pattern without named groups and with a meaningful trailing `/`.
- One parser (with a context argument) producing one public AST (`Xake.PatternAst`), and one
  matcher derived from it that serves rules and masks (brief c).
- Generation from the AST: `format` (replaces `ExecCore.applyWildcards`), `glob` (rule pattern
  to mask), `unparse`, `escape`, `escapePath`.
- How `Fileset` walks directories with that matcher.
- An error and warning model, the signals for legacy (3.x) spellings, the 4.0 behaviour-change
  list and the deprecation calendar up to 5.0.

Out of scope: the typed layer (`Xake.Typed`, brief d; it only needs the API in section 8),
hermetic path functions (tm 9), "rules with arguments" (tm 11), the phony `=>` phase-out
mechanics (brief e) beyond what the grammar needs.

Goals, in priority order:

1. **No unsignalled change of meaning.** Every 3.x spelling whose meaning changes in 4.0 either
   fails with an error that names the 3.x meaning and the replacement, or warns (at parse time,
   or on evidence that the old meaning mattered), or, where neither is practical, is an entry of
   the 4.0 behaviour-change list (section 3). This is brief (b)'s break-softening principle as
   amended by D1: 4.0 may break, it may not break quietly.
2. **Mainstream glob as the target** (brief a): a reader who knows `.gitignore`, Rust `globset`
   or `picomatch` can read a Xake mask; where Xake differs it is on purpose and documented
   (5.9 lists the deliberate differences).
3. **One implementation**: the three encodings that exist in 3.x (`PatternAst` regex for rules
   and wildcard exclude segments, `Path.parse` + OS listing for includes, the `applyWildcards`
   regexes for multi-file names) collapse into one AST and one matcher, with one meaning per
   element in every context.
4. `getRuleMatch` / `getRuleMatches` keep their interface, and return the same values as in 3.x
   for every pattern that is valid in both 3.x and 4.0 and every target both match (brief e).

## 2. Semantics and migration

The module has **one semantics**, the one of section 5, and it ships directly in **Xake 4.0**
(D1). There is no second semantics level, no switch between meanings and no intermediate 3.x
release of the module; `dev` is already the 4.0 line (the variable-model PRs #111–#115), so breaking changes are
allowed.

What softens the break:

- **Errors that teach.** Inputs that 4.0 rejects (`( ) |` outside a named group, a named group
  in a mask, a repeated group name, `{x}`, a group name starting with a digit, `.` or `..`
  after a wildcard, an empty pattern, ...) fail with a `PatternException` whose reason names what the spelling meant in
  3.x and what to write instead (7.2).
- **Warnings on legacy inputs.** Inputs that 4.0 accepts with a *different* meaning, or that are
  deprecated, warn once per pattern in the build log (7.3). A warning is keyed to the legacy
  input. It fires either **at parse time**, when the spelling has a better 4.0 form so the
  warning is never noise for new code (`\` as a separator, `[c]`, `a**b` in a rule, `[!a]` in a
  rule), or **on evidence**, when the spelling is also ordinary 4.0 syntax and should warn only
  if the old meaning would have mattered (a scan meets an entry literally named `[ab]`; a rule's
  `**/` matched zero directories; `dir/**` lists or removes something; `*.*` meets a name
  without a `.`). Every warning names the 3.x meaning and the replacement.
- **A behaviour-change list** for the 4.0 release notes (section 3), one line per change.
- **All legacy warnings stop at 5.0** (section 10): what still warns in 4.0 becomes either the
  plain 4.0/5.0 meaning without a message, or an error. The one non-legacy warning, XP120 (an
  absolute exclude that can remove nothing, D6), stays after 5.0 (D24).

## 3. 4.0 behaviour changes (release notes)

One line per change: 3.x meaning → 4.0 meaning [signal]. "Rule" covers every rule-pattern form;
"mask" covers includes and excludes. Details in 5.2.

1. `\` before `( ) [ ] { } |`: a path separator → an escape (`src\(x\)\*.cs` names the
   directory `src(x)`, tm 55) [no warning; the other `\` in such a pattern warn XP110].
2. `\` before any other character: a separator → still a separator, **deprecated**, an escape
   only from 5.0 [XP110, parse time].
3. `(`, `)`, `|` outside a named group: a regex group/alternation in rules (a numbered capture),
   a literal in masks → an error [XP003].
4. `|` inside a named group, `(ext:dll|exe|nupkg)`: a regex alternation → an error whose text
   gives `(ext:{dll,exe,nupkg})` [XP003].
5. A lone `(`/`)` or an unbalanced `[` in a rule: `RegexParseException` → `PatternException`
   [XP003, XP002]; in a mask: literal text → the same errors.
6. `{a,b}` (with a comma): literal text → an alternation, in every context [XP104 on evidence
   in masks].
7. `{x}` (no comma): literal text → an error [XP005].
8. `[abc]`, `[a-z]`, `[^a]` in an include or a literal exclude segment: literal text → a
   character class [XP104 on evidence].
9. `[c]` (one character) in an include or a literal exclude segment: the literal text `[c]` →
   the character `c` (`sub[1]/*.c` finds `sub1`) [XP106, parse time].
10. `[c]` in a rule or a wildcard exclude segment: unchanged (`c`), deprecated in favour of `\c`
    or `c` [XP105, parse time].
11. `[!a]` in a rule or a wildcard exclude segment: a class of `!` and `a` → a negated class
    [XP117, parse time].
12. `**/` and `/**/` in a rule: one or more directories → zero or more; a rule like `**/*.dll`
    now also matches `a.dll` at the root (a rule `*.dll`, if present, still wins: 5.8); write
    `*/**/*.dll` to require one or more directories [XP119 on evidence]. Measured: in 3.x the
    rule matcher (`matchGroups`) refuses `x.dll` for `**/x.dll`, while `Path.matches` and the scan
    already accept zero directories (`measurements-engine.md` §7); likewise
    `(path:bin/**)/file.o` captured `bin/x/y` (no trailing `/`) and did not match `bin/file.o`,
    and `Path.matches` refused `bin/x/y/file.o` that `matchGroups` captured (§10).
13. Segment-local `**` (`a**b`) in a rule: crosses `/` → stays within a segment, like `*`; its
    specificity becomes that of `*` [XP112, parse time].
14. A mask ending in `/**`: an include listed the directories below, an exclude matched nothing
    → every file below [XP113 on evidence].
15. `*.*` as an include segment: any name (OS-listing rule) → names containing a `.` [XP114 on
    evidence].
16. Other OS-listing quirks in includes: `?` matching zero characters before `.` and, on .NET
    Framework, `*.rdl` finding `.rdlx` → gone; the module decides every name [release note;
    measured on macOS: the legacy listing overloads match `b?` to `b` and `*.*` to the dotless
    `b`, `measurements-dotnet.md` §1; the .NET Framework 3-letter extension rule and the Windows
    kernel behaviour are unverified, 6.4].
17. A named group in a mask: an include read it as literal text, an exclude wildcard segment as
    its body → an error [XP008].
18. A group name starting with a digit, `(1:*)`: a group colliding with the positional `"1"` →
    an error [XP007].
19. A repeated group name, `src/(p:*)/(p:*).fsproj` or `{(x:a)-1,(x:b)-2}`: each occurrence
    captured, the last won → an error [XP015].
20. An unclosed `(name:` in a string rule: ran to the end of the pattern silently → an error
    [XP006].
21. `..` after a wildcard: in masks a parent step the scan applies (`src/*/../x.cs` lists
    `src/x.cs`), in rules a segment `Path.matches` never satisfies, so rule and mask disagree
    (`measurements-engine.md` §3) → an error; `.` after a wildcard: dropped by the parser, so
    `Path.matches` and the scan accept `src/*/./x.cs` while `matchGroups` returns no match
    (§3) → an error likewise [XP012, D29].
22. A trailing `/` in a rule pattern: matched nothing → an error [XP011].
23. A trailing `/` in an exclude: ignored (the parser dropped it, `-- "obj/"` read as `obj`, a
    file-name exclude anchored at the base directory) → matches the directory `obj` only, and
    every file below it is removed (an excluded directory is not entered, 6.2) [release note].
    Measured: in 3.x `-- "obj/"`, `-- "obj"` and `-- "obj/**"` all removed **nothing** (neither
    `obj/a.o` nor `other/obj`); only `-- "obj/**/*"` and `-- "**/obj/*"` removed `obj/a.o`
    (`measurements-engine.md` §6). So 4.0 pruning by `obj/` is a real change, not a
    clarification; it, like `dir/**` (item 14), changes what a fileset returns without any
    spelling change.
24. `/` inside a class (`[a/b]`), `[z-a]`, `[[:alpha:]]`: split into segments in masks, regex
    meaning or a crash in rules → an error [XP010]; `[]` without a later `]` → an error
    [XP002].
25. Case: a literal exclude segment was case-insensitive on every OS, and wildcard include
    segments followed the .NET listing (insensitive on macOS) → one comparer everywhere, ordinal
    on Linux and macOS, ignore-case on Windows [release note]. Measured on macOS: 3.x had two
    comparers in the rule path, `Path.matches` insensitive and `matchGroups` sensitive
    (`src/*.cs` matched `Src/a.cs` in the first only), D11 unifies them
    (`measurements-engine.md` §9).
26. `**` in includes followed directory symlinks and junctions → does not follow symlinks;
    each skipped link is logged once at Message level (D29) [release note]. Measured on macOS:
    3.x follows directory symlinks under `**` and `*/`, lists file symlinks as files, and a
    symlink cycle ends only at the OS limit (ELOOP, depth 32; `measurements-engine.md` §4,
    `measurements-dotnet.md` §4). Whether Windows junctions, mount points and cloud-sync
    placeholders count as links is unverified (6.4, 6.5).
27. Fileset results: file-system enumeration order, duplicates kept → ordinal order per
    directory, duplicates removed [release note; C5]. Measured: 3.x keeps duplicates
    (`measurements-engine.md` §5) and the order is the raw APFS `readdir` order, stable but not
    alphabetical (`measurements-dotnet.md` §5).
28. An unreadable directory met in a scan: `UnauthorizedAccessException` ended the build →
    skipped with a build-log warning [release note].
29. The project root is a literal in rule matching: a root containing `( ) [ ] { } |`
    mismatched or crashed → matches [bug fix].
30. Multi-file target names (`*..>`): an empty group value left `(lib:)` in the name, a `)` in
    a value broke it → the right name [bug fix].
31. Multi-output string rules: outputs with different named groups produced names like
    `out/net8.0/.xml` → an error when the rule is created [XP013, tm 50].
32. A class or braces outside a named group in a multi-output pattern or in `format`: printed as
    source text → an error [XP014].
33. Rule specificity: a `/` next to `**` counted as a literal character → counts 0 (D5, D24); a
    globstar rule can lose a tie it won in 3.x, e.g. `*/?.dll` now beats `**/x.dll` for
    `a/x.dll` [release note].
34. Repeated separators inside a pattern (`a//b`, `a\\b`): collapsed by the parser, so `Path.matches`
    and the scan treat `a//b` as `a/b`, while `matchGroups` compares the raw text and returns no
    match for `a/b` (`measurements-engine.md` §1) → one separator in every context; a leading `//` or `\\` followed by two
    names is a UNC root (4.2; D24) [release note].
35. An empty pattern `""`, as a rule or a mask: no error; it parses to an empty mask that
    matches only the empty path, and `!! ""` lists the base directory itself as one entry
    (`!! "" + "*.cs"` lists it too; `measurements-engine.md` §2) → an error [XP018, D29].
36. Braces that end the pattern with an alternative ending in a separator (`{a/,b}`): literal
    text → an error; write `{a,b}/` [XP019, D29].

## 4. Grammar

One grammar with two start symbols, `rule` and `mask` (D8, D9). One parser implements it, with
the context (`Rule` or `Mask`) as an argument; the public entry points are `Rule.parse` and
`Mask.parse` (8.2). The context changes exactly two things: in `Mask` the lexer does not produce
the `group_open` token (so every `(` in a mask is an error, 4.2), and in `Rule` a trailing
separator is an error (XP011).

### 4.1 Lexical rules (characters to tokens)

The lexer reads the pattern text left to right in one of two **modes**. It is the only place
where escapes are resolved and where `*` and `**` are told apart. Its output is a token stream;
whether a token is legal where it appears is decided by the syntax (4.2).

**Normal mode** (the start mode):

```ebnf
(* lexical, normal mode *)
token        = escape | legacy_sep | dstar | star | qmark | slash
             | group_open | lparen | rparen | pipe
             | class | rbrack | lbrace | rbrace | comma
             | char ;

escape       = "\" , reserved ;                     (* the reserved character, as a literal *)
reserved     = "(" | ")" | "[" | "]" | "{" | "}" | "|" ;
legacy_sep   = "\" ;                                (* a "\" not followed by a reserved character:
                                                       a separator, deprecated (XP110); 5.0: see 10.1 *)
dstar        = "**" ;                               (* maximal munch: "***" = dstar, star *)
star         = "*" ;
qmark        = "?" ;
slash        = "/" ;
group_open   = "(" , name , ":" ;                   (* one token, longest match; Rule context only *)
name         = name_start , { name_char } ;
name_start   = letter | "_" ;                       (* a leading digit is an error, XP007 *)
name_char    = letter | digit | "_" ;               (* letter/digit = Unicode letter/digit *)
lparen       = "(" ;                                (* a "(" that does not start group_open;
                                                       in Mask context every "(" *)
rparen       = ")" ;
pipe         = "|" ;
class        = "[" , ? class mode, below ? , "]" ;   (* one token *)
rbrack       = "]" ;                                (* a "]" in normal mode is stray: XP004 *)
lbrace       = "{" ;   rbrace = "}" ;
comma        = "," ;                                (* only when the innermost open construct is
                                                       a brace pair, see below *)
char         = ? any other character ? ;
```

**Class mode** (M3): an unescaped `[` in normal mode starts a class, and the lexer reads the
whole class as **one** `class` token, up to the first unescaped `]` that closes it:

```ebnf
(* lexical, class mode: the part between "[" and the closing "]" *)
class_body   = [ negation ] , class_items ;
negation     = "!" | "^" ;                           (* only as the first character *)
class_items  = class_item , { class_item } ;        (* a "]" right after "[", "[!" or "[^" is a literal item *)
class_item   = class_char , [ "-" , class_char ] ;   (* a range *)
class_char   = ? any character except "/", "\" and an unescaped "]" ?
             | "\" , ? any character except "/" ? ;  (* inside a class "\" escapes the next character *)
```

- Inside a class every character is ordinary except `]` (closes), `\` (escapes the next
  character), `-` between two items (a range) and a leading `!`/`^` (negation): `*`, `?`, `,`,
  `{`, `}`, `(`, `)`, `|` and `[` are plain characters there. So `[*]`, `[,]`, `[(]` are
  one-character classes, `{[,],x}` has two alternatives, and `[\-a]` is a class of `-` and `a`.
- A `]` right after `[`, `[!` or `[^` is a literal item, so a class is never empty: `[]a]` is a
  class of `]` and `a`; `[]` alone, `[!]` and `[^]` have no closing `]` and are XP002 (I4).
- A `-` that cannot form a range (first, last, or right after a range: `[a-z-0]`) is a literal.
- A class that starts with `[:` (`[[:alpha:]]`) is XP010: POSIX class names are reserved for
  later.
- If the end of the pattern comes before the closing `]`: XP002. An unescaped `/` between `[`
  and the closing `]`, or `\/`: XP010 (a class never matches `/`). A reversed range `[z-a]`:
  XP010.

Lexical decisions:

- **`\` is the escape character for the reserved characters** `( ) [ ] { } |` (tm 55). A `\`
  before any other character, or at the end of the pattern, is still a **path separator** in
  4.0, deprecated and reported once per pattern (XP110), because masks built with `</>` on
  Windows contain `\` (tm 56, 57; D22). This includes `\*` and `\?`, which mean `/*` and `/?` as
  in 3.x, and `\\`, which is two separators (4.2: one separator, or a UNC root at the start).
- Consequently 4.0 has no backslash escape for `*`, `?`, `,` and `\`. The one-character class
  is the literal spelling: `[*]`, `[?]`, `[,]`, `[\\]` (inside a class `\` escapes the next
  character). In 5.0 `\` also escapes `* ? , ! ^ - \` and any other `\x` is an error (XP009,
  10.1).
- `\,` where a `,` would be a `comma` token (directly inside braces) is an error (XP017): it
  would otherwise be a separator followed by an alternative break, which is never meant. Outside
  braces `\,` is a legacy separator followed by a literal `,` (XP110), as in 3.x.
- `**` is one token wherever it appears; whether it is a *globstar* (zero or more segments) or
  a *segment-local* double star is decided by the position rule in 4.3.
- `,` is a `comma` token only when the innermost open construct is a brace pair (the lexer
  keeps a stack of open `{` and `(name:`): in `{a,b}` and `{a,(n:b),c}` the commas split
  alternatives, in `{(n:b,c),d}` the first comma is inside the group and is a `char`. Elsewhere
  `,` is a `char` (I5).
- `!` is special only as the first character inside a class (`[!a]`); elsewhere it is a `char`.
  A leading `!` in a mask is a literal file-name character, **not** a negation as in
  `.gitignore` (5.9, example 12). `#` is never special.
- F# string literals: `"a\(b"`, `@"a\(b"` and `"""a\(b"""` are the same text, because F# keeps an
  unknown escape `\(` as two characters (checked by the prototype, `EscapingDiskTests`
  "the F# literal forms ... are the same text"). `"a\\b"` is `a\b`. Docs recommend `@"..."` for
  any pattern with `\`.

### 4.2 Syntax (tokens to AST)

```ebnf
(* syntactic; [ ] optional, { } repetition; two start symbols *)
rule         = [ root ] , r_body ;                  (* the last token must not be a separator: XP011;
                                                       not empty: XP018 *)
mask         = [ root ] , m_body , [ dir_mark ] ;   (* not empty: XP018 *)
dir_mark     = sep ;                                (* a trailing separator: the mask matches directories (5.1) *)

r_body       = { r_element } ;
m_body       = { m_element } ;
m_element    = literal | star | qmark | dstar | sep | class | braces ;
r_element    = m_element | group ;

root         = unc | drive , sep | sep ;            (* an absolute pattern; not joined to an anchor *)
unc          = sep , sep , literal , sep , literal ; (* //server/share or \\server\share; server and
                                                       share must be literals: XP016 *)
sep          = slash | legacy_sep ;
drive        = letter , ":" ;                        (* recognised only at the very start *)

literal      = ( char | escape ) , { char | escape } ;

braces       = lbrace , alternative , comma , alternative , { comma , alternative } , rbrace ;
alternative  = { element } ;                        (* may be empty; may contain separators and nested braces;
                                                       in braces that end the pattern it must not end
                                                       with a separator: XP019 *)

group        = group_open , group_body , rparen ;   (* Rule only; an unclosed group is XP006 *)
group_body   = { r_element } ;                      (* any element, nested groups included *)
```

`element` inside `alternative` stands for `r_element` under `rule` and for `m_element` under
`mask`.

Syntactic decisions:

- **One grammar, two contexts** (D8, D9). In `Mask` context there is no `group_open` token,
  so a `(` is an `lparen` and is rejected at once. The error code names what the user most
  likely meant: XP008 when the text at the `(` reads `name:` (a named group, which exists only
  in rule patterns, D23), XP003 otherwise (D24). Either way the mask is rejected at the `(`.
- **The empty pattern** `""` is an error in both contexts (XP018, D29): it names no file, and
  "the anchor itself" is better written as the anchor's path.
- **Braces need a top-level comma.** `{a,b}` is an alternation; `{x}` (no comma) is an error
  (XP005), which keeps `{1..3}` ranges open for later. Bash and picomatch read `{x}` as a
  literal, `globset` and Java's `PathMatcher` as the single alternative `x`; Xake rejects it to
  keep the syntax open (brief b; 5.9).
- `( | )` outside `group` stay **reserved** (tm 60, D7): an unescaped one is an error (XP003),
  also a `(` that does not start a group (`(a b:*)`, `(:*)`; `(1:*)` gives XP007). A stray `)`
  outside a group is XP003; a stray `}` or `]` is XP004 (I9).
- A group's closing `)` is the first unescaped `)` at the group's nesting level. An unclosed
  group is an error (XP006) in every rule pattern, string or typed (D23).
- **A repeated group name is an error** (XP015, D4): a name may appear at most once in a
  pattern, also across brace alternatives (`{(x:a)-1,(x:b)-2}` and `(x:*)/(x:*)` are both
  rejected). Two captures are never required to hold equal text.
- **Root** (S6, D24): a leading `/` or `X:/` (also `\`, `X:\`, which warn XP110) makes the pattern
  absolute; a leading `//server/share` or `\\server\share` (exactly two separators, then two
  literal segments) is a **UNC root**, absolute as well; a non-literal server or share is
  XP016. UNC roots are meaningful on Windows; on Linux and macOS no normalized disk path starts
  with `//`, so a UNC pattern matches nothing there. A leading `/` is the file-system root, not
  the base directory as in `.gitignore` (D6, 5.9). A pattern is otherwise relative to its
  anchor: the project root for rule patterns (phony patterns: the empty root), the fileset base
  directory for masks.
- **Repeated separators** (S6, D24): after the root, a run of separators is one separator (`a//b` =
  `a/b`, `a\\b` = `a/b` with XP110); three or more leading separators are one `/`. A trailing
  run is one trailing separator.
- **A separator at the end of a brace alternative** is an error when the braces end the pattern
  (`{a/,b}`, also nested: `x/{a,{b/,c}}`): a directory mark would apply to one alternative only
  (XP019, D29). Write `{a,b}/` when every alternative is a directory, or two patterns. Inside a
  pattern the separator is an ordinary one (`{a/,b}c` = `a/c` or `bc`).
- `.` and `..` segments are resolved in the **literal prefix** (the segments before the first
  non-literal element), as `Path.normalizeLiteralPrefix` does in 3.x. After a wildcard, `..` and
  `.` are errors (XP012, D29; 3.x applied `..` as a parent step in masks and dropped `.`,
  `measurements-engine.md` §3).
- **Constraint on a future extglob** (D28; brief a reserves `( | )` for it; C6): `(name:` is a
  group wherever it appears, also after `*` or `?`: `*(name:x)` and `?(name:x)` are valid 4.0
  rule syntax (a wildcard, then a group) and keep that reading. Nothing reserves `*(` or `?(`.
  A future extglob may use only `@(`, `+(`, `!(` (followed by a non-group `(`, they are XP003
  today, as are `*(` and `?(` followed by a non-group `(`).

### 4.3 Position rule for `**`

A `dstar` token is a **globstar** when, ignoring `group_open` and its `rparen` (group delimiters
are transparent), it is preceded by the start of the pattern or a separator, and followed by the
end of the pattern or a separator. Examples of globstars: `**`, `**/b`, `a/**`, `a/**/b`,
`(p:**)/b`, `(path:bin/**)/file.o`, `a/(p:**)`. Every other `dstar` is **segment-local**
(`a**b`, `**.cs`, `x/**y`, `(p:a**)`) and matches like `*` (5.1); in a rule pattern it warns
(XP112), because 3.x let it cross `/`.

### 4.4 The AST

```fsharp
type ClassItem =
    | One of char
    | Range of char * char

type Token =
    | Lit of string                      // literal text, escapes resolved, no separator
    | Sep                                // '/' (or a deprecated '\' separator)
    | Star                               // '*'
    | AnyChar                            // '?'
    | Globstar                           // '**' as a whole segment (4.3)
    | DoubleStar                         // '**' inside a segment: matches as '*'; kept for unparse and XP112
    | Class of negated: bool * items: ClassItem list
    | Alt of Token list list             // {a,b,...}; each alternative is a token list
    | Group of name: string * body: Token list
```

A UNC root is two leading `Sep` followed by the server and share `Lit` segments; a drive is a
leading `Lit "X:"`. Differences from the prototype's `Token` (`Lit | Star | DoubleStar | AnyChar
| Group | Reserved`): `Sep` is split out of `Lit` (segment boundaries are needed by the walker
and the globstar rule), `Globstar` is distinguished from `DoubleStar`, `Class` and `Alt` are
added, `Reserved` is dropped (unescaped reserved characters are errors in 4.0). `[c]` parses to
`Class (false, [One c])`; its warning (XP105/XP106) is an `Issue` of the parse, not a token.
Whether a `Sep` came from a `\` is recorded only as an `Issue` (XP110). A mask's `dir_mark` is
a trailing `Sep`.

## 5. Semantics

### 5.1 The 4.0 semantics

One meaning per element; the two columns are the two contexts (D9). Include and exclude are not
columns: they use the same mask meaning, and differ only in the scanner (6.2).

"One character" is one UTF-16 code unit (a .NET `char`), as in 3.x: a character outside the
Basic Multilingual Plane (an emoji) needs `??` (C2, D26; a deliberate difference, 5.9).

| element | rule | mask |
|---|---|---|
| literal | matches itself (case per 5.4) | same (an include probes it on disk, 6.2) |
| `*` | 0+ characters other than `/`, leading `.` included (5.3) | same |
| `?` | exactly 1 character other than `/` | same |
| `**` globstar | 0+ whole segments (5.5) | same; walked recursively (6.3) |
| `**` segment-local (`a**b`) | same as `*` (XP112) | same as `*` |
| `[abc]` `[a-z]` | one character from the set (case per 5.4) | same |
| `[!abc]` `[^abc]` | one character not in the set and not `/` | same |
| `[c]` | the character `c` (XP105 suggests `\c` or `c`) | same (XP105 or XP106 by role, 7.3) |
| `{a,b}` | one of the alternatives; alternatives may hold `/`, wildcards, nested braces | same; walked as the union (6.3 step 2) |
| `{x}` (no comma) | error XP005 | error XP005 |
| `(name:body)` | named capture of what `body` matches (5.6) | error XP008 (no `(name:` token in a mask) |
| `( ) \|` outside a group | error XP003 | error XP003 |
| `\c`, `c` reserved | the literal `c` | same |
| `/` | separator; a run of separators is one (4.2) | same |
| `\` before a non-reserved character | separator, deprecated (XP110) | same |
| trailing `/` | error XP011 (a rule names a file or a phony name) | matches a **directory** only, never a file |
| `{a/,b}` ending the pattern | error XP019 | error XP019 (write `{a,b}/`) |
| `.` or `..` segment after a wildcard | error XP012 | error XP012 |
| empty pattern `""` | error XP018 | error XP018 |
| leading `/`, `X:/`, `//server/share` | absolute: the project root is not joined | absolute: the base dir is not joined |
| anchoring | whole target path, joined to the project root (phony: to `""`) | whole path, joined to the base dir |

Without a trailing `/` a mask matches files; with it, directories (D9). What a match does
(an include yields the matched files or directories; an exclude removes the matched files, or
everything below a matched directory) is the scanner's business (6.2).

"Anchoring" is where Xake deliberately differs from `.gitignore`: a mask without `/` matches only
directly in its base directory, it does not float to any depth (example 5, 5.9).

**One or more segments** (D12). `**` is zero or more segments everywhere. To require at least
one directory, put a one-segment wildcard before the globstar: `*/**/file.o` matches
`a/file.o` and `a/b/file.o`, not `file.o`; with a capture, `(path:*/**)/file.o` gives
`path = a`, `a/b`. This is the replacement for the 3.x rule meaning of `**/` (XP119, example
20).

### 5.2 Legacy spellings: 3.x meaning, 4.0 meaning, signal

Rows are spellings whose 3.x meaning differs from 5.1, or depended on how the mask was used.
Everything else behaves in 3.x as in 5.1. "Rule" here also covers phony/command patterns. In 3.x
an exclude treated a segment as a regex when it contained `*` or `?` ("wildcard segment") and as
literal text otherwise ("literal segment"), and includes went through the OS listing; 4.0 drops
those distinctions, but the 3.x column and the warnings use them to name the 3.x meaning
correctly. Signal: an error code, a warning code with its trigger (7.3), or "release note"
(section 3).

| spelling | 3.x meaning | 4.0 meaning | signal |
|---|---|---|---|
| `\` + reserved char | separator, then the reserved char's 3.x meaning | escape (tm 55) | release note |
| `\` + other char | separator | separator, deprecated | XP110 at parse |
| `\,` directly inside braces | separator, then `,` (braces were literal text) | error | XP017 |
| `(` `)` (not a group) | rule, exclude wildcard segment: a regex group (a numbered capture); include, exclude literal segment: literal | error | XP003 |
| `\|` | rule: regex alternation over the whole pattern or the enclosing group body; masks as `(` | error; inside a group the text suggests braces | XP003 |
| lone `(` or `)` | rule: `RegexParseException`; masks: literal | error | XP003 |
| unbalanced `[` | rule: `RegexParseException`; include, exclude literal segment: literal | error | XP002 |
| `[abc]`, `[a-z]`, `[^a]` | rule, exclude wildcard segment: class; include, exclude literal segment: literal text | class | XP104 on evidence (masks) |
| `[!a]` | rule, exclude wildcard segment: class of `!` and `a`; include, exclude literal segment: literal text | negated class | XP117 at parse (rule, exclude wildcard segment); XP104 on evidence (otherwise) |
| `[c]` (one character) | rule, exclude wildcard segment: `c`; include, exclude literal segment: literal text `[c]` | `c` | XP105 at parse (meaning unchanged); XP106 at parse (meaning changed) |
| `{a,b}` | literal text | alternation | XP104 on evidence (masks); release note |
| `{x}` | literal text | error | XP005 |
| `**/b`, `a/**/b` in a rule | 1+ directories (regex `(.*)/`) | 0+ (`*/**/b` for 1+) | XP119 on evidence |
| `a/**` at the end | rule: 1+ characters below `a`; include: the **directories** below `a` (and `a`); exclude: no file (removed nothing, `measurements-engine.md` §6, §7) | every file below `a` (rule: unchanged in practice) | XP113 on evidence (masks) |
| `a**b` (segment-local) | rule: crosses `/` (regex `(.*)`); masks: like `*` | like `*` | XP112 at parse (rule) |
| `*.*` as an include segment | **any name** (OS listing rule) | needs a `.` | XP114 on evidence |
| `(name:body)` in a mask | include: literal text (finds nothing with a wildcard); exclude wildcard segment: `body`; exclude literal segment: literal | error | XP008 |
| unclosed `(name:` | string rule: runs to the end, silently | error | XP006 |
| group name `(1:*)` | a group named `1`, colliding with positional `1` | error | XP007 |
| repeated name `(n:*)/(n:*)` | both capture; the **last** value wins, values may differ | error | XP015 |
| `..` after a wildcard | rule: `Path.matches` never satisfies it; masks: applied by the scan as a parent step (`src/*/../x.cs` lists `src/x.cs`), so the two disagree (`measurements-engine.md` §3) | error | XP012 |
| `.` after a wildcard | rule: `Path.matches` true (the parser drops `.`), `matchGroups` no match; masks: dropped, the scan lists `src/d/x.cs` for `src/*/./x.cs` (§3) | error | XP012 |
| empty pattern `""` | rule: `Path.matches` only the empty path; mask: parses to an empty mask, `!! ""` lists the base directory itself (§2) | error | XP018 |
| `a//b` | `Path.matches` and the scan: as `a/b`; `matchGroups`: no match for `a/b` (raw text, §1) | one separator | release note |
| `{a/,b}` ending the pattern | literal text | error | XP019 |
| trailing `/` | rule: matches nothing; include: directories only; exclude: ignored (`obj/` = `obj`, a base-directory file name; it removed nothing, nor did `obj/**`, §6) | rule: error; mask: a directory (include: unchanged; exclude: the directory, so everything below it) | XP011 (rule); release note (exclude) |
| `/` in a class, `[z-a]`, `[[:` | masks: split at `/` or literal; rule: regex meaning or a crash | error | XP010 |
| `[]` (no later `]`) | rule: a crash; masks: literal | error | XP002 |
| case of a literal segment | rule: per OS; include: as the file system resolves it; exclude: **always insensitive** | per OS (5.4) | release note |
| case of a wildcard segment | rule, exclude: per OS; include: as the .NET listing (insensitive on Windows and macOS) | per OS | release note |

Notes:

- `\` in 3.x (measured on Windows, `measurements-windows.md` §W1, §W2): in a mask, a rule and a `</>`-built mask
  `\` is a separator, every one of them equal to the `/` spelling (`src\*.cs`, `src\(name:*).cs`
  against `src\a.cs` or `src/a.cs`); the group text in `matchGroups` is reported with `/`. The two
  `\` rows above (`\` + other char: XP110; `\` + reserved char: escape) change this; the vectors pin
  the Windows facts as 3.x results for all `\` rows, tagged "Windows measured; macOS by source".
- The prototype (#90) already read `[c]` in includes as the character `c` (`sub[1]/*.c` found
  `sub1`). That is now simply the 4.0 meaning, announced by XP106 (13, C1).
- Rows with a parse-time warning have a 4.0 spelling that never warns (`/`, `\(`, `c`, `*`,
  `[^a]`), so new code is not penalised. Rows with an evidence warning are ordinary 4.0 syntax;
  they warn only when the 3.x meaning would have given a different result in this build (7.3).

### 5.3 Dotfiles

`*`, `?`, `**` and classes match a leading `.` in every context (D10). `**/*.cs` lists
`.hidden/y.cs`; excluding dot folders is written explicitly: `-- "**/.*/"` (the directories),
`-- "**/.*/**"`, or the 3.x idiom `-- "**/.*/**/*"` (pinned by `FilesetTests`; all prune). This
is 3.x behaviour (Ant, `.gitignore`, Rust `globset`); it differs from picomatch / bash
(`dot: false`) and POSIX `FNM_PERIOD`. Ant matches dotfiles too but ships default excludes
(`**/.git/**`, `**/.svn/**`, ...); that is the precedent for a later `dot`/default-excludes
fileset option, which is not part of the module (C10). The directory entries `.` and `..` are
never produced by a listing. Measured: 3.x already matched dot-prefixed directories under `*/`
and `**` in both `Path.matches` and the scan (`measurements-engine.md` §8); the only 4.0 risk is
the OS listing, whose default options skip them (9.5). File-system attributes are not pattern
semantics (D33): a file with the Windows `Hidden`, `System` or any other attribute matches like
any other name, and the walker never filters a listing by attribute (the `ReparsePoint` check of
6.5 decides only whether to descend, not whether a name is listed).

### 5.4 Case sensitivity

- One comparer for all contexts (D11): ordinal on Linux and macOS (`Env.isUnix`),
  ordinal-ignore-case on Windows. It is the comparer `File` equality and rule matching already
  use (`Types.fs`, `Fingerprints.fs`). Classes and ranges follow it: on Windows `[a-z]` matches
  `Q`. The implementation must agree with this comparer in every code path (9.3).
- A literal segment of an include is a probe of the file system (6.2): on a case-insensitive
  file system (Windows, default macOS) it finds the file whatever the case, and the result keeps
  the mask's spelling (D25). This is a property of the file system, not of the matcher.
- **Determinism principle** (D25): the result of a scan depends on the mask text and the list of
  files, not on how the volume stores the case of names. The module never substitutes the disk's
  spelling for a probed segment; an option to take the disk spelling may come later without a
  grammar change.
- **Known limit: the probe spelling trap** (D25, C5). On default macOS `!! "SRC/a.cs"` yields
  `SRC/a.cs`, which a later ordinal exclude `src/**` or a rule `src/*.cs` does not match,
  although it is the same file (example 15). Write masks in the disk's spelling.
- **Known limit: no Unicode normalization** (D27, C3). Names and pattern literals compare by
  UTF-16 code units, without NFC/NFD normalization, as every mainstream globber does. On macOS a
  name stored in NFD and a pattern literal in NFC compare unequal in a wildcard segment
  (`café*`), while a literal probe finds the file (the file system normalizes; example 23).
- **Known limit: one character is one code unit** (D26): `?` and a class match one UTF-16 code
  unit, so a character outside the Basic Multilingual Plane needs `??` (5.9, example 23).
- Measured on macOS (3.x): `Path.matches` is case-insensitive, `matchGroups` is case-sensitive
  (two comparers; D11 unifies them), and the scan returns the mask's spelling, not the disk's
  (`src/*.cs` over `Src/A.cs` returns `src/A.cs`; a literal `SRC/a.cs` is returned as typed),
  which confirms D25 (`measurements-engine.md` §9).
- 4.0 changes from 3.x (release notes, section 3 item 25): on macOS a wildcard include becomes
  case sensitive (`!! "*.CS"` no longer finds `a.cs`); on Linux and macOS a literal exclude
  segment becomes case sensitive (`-- "Bin/**/*"` no longer removes `bin/...`). No warning
  (D11).

### 5.5 `**` edge cases

Both contexts unless stated:

| pattern | matches | does not match | capture of the `**` |
|---|---|---|---|
| `**` | every path below the anchor, any depth | — | the whole path |
| `**/b` | `b`, `x/b`, `x/y/b` | `xb` | `""`, `x`, `x/y` |
| `a/**/b` | `a/b`, `a/x/b`, `a/x/y/b` | `ab`, `a/xb` | `""`, `x`, `x/y` |
| `a/**` | `a/f`, `a/x/f` (every file below `a`) | `a` itself | `f`, `x/f` |
| `a/**/` (mask) | `a` and every directory below it | files | — |
| `*/**/b` | `x/b`, `x/y/b` (one or more directories, 5.1) | `b` | `""`, `y` |
| `a**b` | as `a*b` (one segment) | `a/x/b` | as `*` |
| `**/**/b` | as `**/b` (adjacent globstars collapse to one in the AST, D34) | — | one capture, as for `**/b`: `""`, `x`, `x/y` (D37) |
| `(p:**)/b` | `b`, `x/b` | — | `p` = `""`, `x` |
| `(path:bin/**)/file.o` | `bin/file.o`, `bin/x/y/file.o` | — | `path` = `bin`, `bin/x/y` (no trailing `/`) |
| `(path:*/**)/file.o` | `a/file.o`, `a/b/file.o` | `file.o` | `path` = `a`, `a/b` |

Rules for the value of a globstar capture: it never starts or ends with `/`; zero segments give
`""`. A group around a globstar at the group's edge (`(p:**)/b`, `(path:bin/**)/file.o`) holds
the matched segments without the adjacent `/` (pinned by `PathTests`, `(path:bin/**)`).
Formatting (5.7) is the inverse: a globstar slot with value `""` drops one adjacent `/`, so
`a/**/b` formats to `a/b`, not `a//b`.

3.x differences (section 3 items 12–14): in rule patterns a globstar before a `/` needed at least
one directory (`**/mlib.dll` did not match `mlib.dll` at the root: `PathTests` pins it; XP119
on evidence; `*/**/mlib.dll` keeps the 3.x meaning); segment-local `a**b` in a rule crossed `/`
(XP112); in an include `a/**` listed directories, in an exclude it matched nothing
(`FilesetTests` pins it; XP113 on evidence). Measured (`measurements-engine.md` §7, §10): as an
include `dir/**` listed the directories `dir` and `dir/sub`; `(path:bin/**)/file.o` captured `bin/x/y`
without a trailing `/` and did not match `bin/file.o`.

### 5.6 Named groups and captures

- **Names** (D21): `name_start name_char*` with Unicode letters/digits allowed (F# record fields
  can be Unicode). A leading digit is an error (XP007). A `(` followed by anything else
  (`(a-b:*)`, `(a b:*)`, `(:*)`) is not a group: the `(` is reserved (XP003).
- **Body**: any element: wildcards, globstars, classes, braces, `/`, nested groups
  (`(name:(plat:*)-a.ss)` and `(root:*/*-a).ss` are pinned by `XakeScriptTests`).
- **A name appears at most once** in a pattern (D4): a repeated name is an error (XP015) at
  parse time, also when the occurrences are in different brace alternatives. 3.x let each
  occurrence capture and the last one win.
- **Groups exist only in rule patterns** (D23): a group in a mask is XP008.
- **Multi-output** (`*..>`, `targets`): every output has the same set of named groups, checked
  when the rule is created (tm 50; prototype `checkSameGroups`, error XP013). Bare wildcards stay
  allowed in string multi-output rules and pair **by position** (`["dd/*.dll"; "dd/*.xml"]`, as
  in 3.x); the typed layer keeps rejecting them (its own rule). A class or brace outside a group
  in a multi-output pattern is an error (XP014): the other outputs could not be named.
- **What `getRuleMatches` holds** (unchanged interface, brief e):
  - every named group by name;
  - every *unnamed* capture by its **positional number** `"1"`, `"2"`, ... in left-to-right
    order **at any depth**: each `*`, `**`, `?` is one, including those inside named groups
    (`gen/*/(name:*)-?.txt`: `"1"` = the first `*`, `"2"` = the group's own `*`, `"3"` = `?`).
    3.x also numbered every unescaped `(` of a rule; that spelling is an error in 4.0 (XP003),
    so no valid pattern changes its numbering;
  - classes and braces take **no** number (they are non-capturing); wildcards inside braces are
    numbered in text order, and those of an alternative that did not match hold `""`;
  - `"0"`, the whole matched path (root included), which exists in 3.x and is kept (C7).
  - Values are **greedy, leftmost**: `(a:*)-(b:*)` on `x-y-z` gives `a = x-y`, `b = z`, as the
    .NET regex gives in 3.x. Any implementation must reproduce this (9).
- `getRuleMatch` of an unknown key returns `""` (tm 7).

### 5.7 Generation: `format`, `formatMatches`, `glob`, `escape`, `escapePath`

- `format named positional tokens`: a named group becomes `named name`; the i-th **top-level**
  bare wildcard (1-based, wildcards inside groups do not count) becomes `positional i`;
  literals and `Sep` print as text with escapes resolved, `Sep` as `/`. A pattern made only of
  literals formats to its text (tm 14). A globstar slot with `""` drops one adjacent `/` (5.5).
  A `Class` or `Alt` outside a group cannot be formatted: XP014.
- `formatMatches (matches: Map<string,string>) (pattern: string)`: `format` fed from a rule match:
  named groups by name, the i-th top-level bare wildcard by its positional number (5.6) in the
  **pattern being formatted** (`wildcards` computes it). A missing key gives `""` (as
  `getRuleMatch`). This replaces `ExecCore.applyWildcards` (tm 14) and fixes its two bugs
  (brief c): an empty group value left `(lib:)` in the name; a `)` in a value broke the name.
- `glob named positional tokens`: a mask for partly known values: a bound group or wildcard
  becomes its value **escaped** (`escape`); an unbound group becomes its own body (recursively);
  an unbound wildcard stays. The result has no named groups, so it is a valid mask.
  `glob` of `src/(dir:*)/**/*.fs` with `dir = core` is `src/core/**/*.fs`.
- `unparse tokens` is the inverse of `parse`: reserved characters in literals escaped with `\`,
  `*` `?` in literals as `[*]` `[?]`, a `\` in a literal as `[\\]`, `Sep` as `/`. `escape s`
  applies the same rules to a plain string, so that `parse (escape s)` is `[Lit s]` per
  segment; every `\` in `s` becomes the literal `[\\]`. In 5.0 both switch to `\*`, `\?`, `\\`
  (10.1).
- `escapePath p` (D7, S3) turns a real file-system path into a mask that names exactly that
  path: every `\` is first normalized to `/` (a separator, not a literal), a drive letter
  (`C:`) and a UNC root (`\\server\share`) are kept as the root, and every metacharacter of the
  segments (`( ) [ ] { } |` with `\`, `* ?` as `[*]` `[?]`) is escaped. `escapePath
  @"C:\Program Files (x86)\Tool"` = `C:/Program Files \(x86\)/Tool`. Typical use:
  `!! (escapePath toolDir + "/*.dll")` (example 19). It is the answer the XP003/XP008 texts give
  for masks built from real paths.

### 5.8 Specificity

Formula as in `Path.Specificity` (#88/#89): `(literal characters, -globstars, -wildcard
weight)`, compared lexicographically, the larger wins. Token contributions:

- `Lit s`: its length in literal characters.
- `Sep`: one literal character, **except a `Sep` adjacent to a `Globstar`, which counts 0**
  (D5): the `/` of `**/`, `/**` and `/**/` is part of the globstar, not a literal the target
  must contain. (3.x counted it; section 3 item 33.)
- `Globstar`: one globstar. `DoubleStar` counts as `*` (3.x counted it as `**`, since it crossed
  `/`; section 3 item 13).
- `Star`: wildcard weight as in 3.x; `AnyChar` and `Class`: wildcard weight 1 (a class like `?`).
- `Alt`: the specificity of its **least specific** alternative.
- `Group`: the sum of its body.

Specificity is computed from the AST, never from text. Effect: for the root file `a.dll`,
`*.dll` = `(4, 0, -w)` beats `**/*.dll` = `(4, -1, -w)` on the globstar count (example 9).

### 5.9 Deliberate differences from mainstream glob

One line each, with the convention Xake departs from (S4). Everything not listed follows the
mainstream reading (POSIX, bash `globstar`, `.gitignore`, picomatch, `globset`).

| Xake 4.0 | mainstream | reason |
|---|---|---|
| A leading `/` is the file-system root (D6) | `.gitignore`: the directory of the `.gitignore` file | indistinguishable from an absolute path on Unix; masks are anchored at the base dir anyway; XP120 warns when an absolute exclude can remove nothing |
| No floating basename match: a mask without `/` matches only in its base dir | `.gitignore` (no `/`), minimatch `matchBase`: any depth | one anchoring rule for rules and masks; write `**/x` |
| Dotfiles matched by `*`, `?`, `**`, classes (D10) | picomatch/bash (`dot: false`), POSIX `FNM_PERIOD`: skipped | 3.x, Ant, `.gitignore`, `globset` |
| `{x}` without a comma is an error (XP005) | bash/picomatch: literal `{x}`; `globset`/Java: `x` | keeps `{1..3}` open |
| Unclosed `[`/`{`, stray `]`/`}` are errors | POSIX, bash, picomatch: literal | reserved set (brief b); `globset` and Java also reject unclosed constructs |
| `a**b` is `a*b` (XP112 in rules) | `.gitignore`, picomatch, bash agree; Java: crosses `/` | majority reading |
| `\` is a separator (deprecated) before non-reserved characters until 5.0 (D22) | POSIX, bash, `.gitignore`, Java: an escape for any character | masks built with `</>` on Windows (tm 56, 57) |
| A trailing `/` in a mask matches directories; an excluded directory is not entered (D9) | `.gitignore` (exclude), bash/picomatch `*/` (lists dirs) | one meaning for both roles |
| Named groups `(name:...)` capture in rule patterns; `( \| )` are otherwise reserved | no equivalent; extglob uses `( \| )` | rule captures (brief a); extglob stays possible (4.2) |
| `!` and `#` at the start of a mask are literal | `.gitignore`: negation, comment; picomatch: leading `!` negates | `++`/`--` do negation |
| Excludes apply in order, only to earlier includes | MSFG, Ant, fast-glob: excludes are global; `.gitignore`: last match wins | 3.x semantics (6.3) |
| A mask without non-literal elements is listed even if absent (D13) | MSFG, Ant, fast-glob: existing files only | `needFiles` builds missing files (6.2) |
| Case per OS (D11) | MSFG: insensitive everywhere; picomatch/`globset`: sensitive, with an option | same comparer as `File` equality and rule matching |
| Braces in every context | not in `.gitignore`, MSFG, Ant | brief a |
| `?` and a class match one UTF-16 code unit (D26): `😀.txt` needs `??.txt`, `?.txt` does not match it | picomatch (`u` flag), `globset`, bash in a UTF-8 locale: one code point | .NET strings are UTF-16, as in 3.x; a scalar-value matcher can come later without breaking any mask that has no surrogate pairs |
| `**` does not follow symlinks (D17) | fast-glob, Ant, MSFG follow; bash ≥ 4.3, ripgrep, Java `Files.walk` do not | no cycle detection needed |

## 6. Fileset scanning

### 6.1 What stays

`Fileset`, `FilesetElement = Includes of PathMask | Excludes of PathMask`, `FilesetOptions`, the
operators and the builder keep their public shapes in 4.0 (D16, C8). `Path.Part` values keep
the **segment text** of the mask (escapes kept, as the prototype does) so that the pickled
`GetFiles` dependency in `.xake` keeps its format (DB version stays `0.5`). The module parses each
part's text at scan time (`Mask.parse`). Hermetic code that builds `Includes (Path.parse m)`
keeps compiling.

`Path.parse` / `parseDir` split at `/` but **not inside `[...]` or `{...}`** (3.x splits
`[a/b]*.c` into `[a` and `b]*.c`); they also split at a `\` that is a legacy separator (XP110),
and never at a `\` inside `[...]`, where it is an escape (`[\\]`, `[\-]`; M3, I6). They use the
lexer of 4.1, so both split points are decided by the same rules as the parser.

### 6.2 Includes and excludes

A mask matches files, or directories when it ends in `/` (5.1). The two roles use that match
differently (D9):

- **Include**: yields the matched files, or, with a trailing `/`, the matched directories
  (`a/**/` = `a` and every directory below it).
- **Exclude**: removes from the results so far every file the mask matches; with a trailing
  `/`, it matches directories, and an excluded directory is **not entered**: every file and
  directory below it is removed (and pruned from later walking, 6.3 step 3). "Everything below"
  is a consequence of not entering the directory, not a separate meaning. `-- "obj/"` removes
  `obj/x.dll` and `obj/a/b.cs`; `-- "obj"` removes only a file named `obj` at the base directory (as in 3.x, where `-- "obj/"`
  and `-- "obj/**"` also removed nothing; `measurements-engine.md` §6).

**Literal segments and explicit names** (D13). A mask segment without any non-literal element
is a **probe**, not a listing:

- If the whole mask has no non-literal element (no `* ? ** [..] {..}`), it is an **explicit
  name**: it is listed whether or not the file exists (3.x rule, `Fileset.listFiles`
  `isExplicitRule`). This is what lets `needFiles (!! "out/x.dll")` build a missing file; it is
  load-bearing (hermetic `Locks.fs` build-from-lock, the compiler tasks' `Ref` filesets, four
  tests; see `explicit-name-history.md`). The rule is documented in `docs/overview.md` in
  Phase 5, and revisited after 4.0, once the compiler tasks and hermetic demand generated files
  through `need` instead of filesets.
- Otherwise a literal directory segment is kept if `Directory.Exists`, a literal last segment if
  `File.Exists` (or `Directory.Exists` for a trailing `/`). No listing is done, so a literal
  segment costs one `stat`, and the path keeps the mask's spelling, not the disk's (5.4, D25,
  trap C5).
- Braces count as non-literal: `src/{a,b}.cs` lists only the existing ones. Brace expansion in
  the bash sense (both names listed regardless) is not done.

**An absolute exclude that can remove nothing** (D6, S1). When a fileset is evaluated, an
exclude whose root is absolute and whose literal prefix lies neither below the fileset's base
directory nor below the anchor of any earlier include can never remove anything; it warns
XP120 once (7.3). Typical cause: a `.gitignore` habit, `-- "/bin/**"` meant as "bin in the base
dir" (write `-- "bin/**"`).

### 6.3 Algorithm

`toFileList` folds the elements in order, as in 3.x: an include appends what it finds, an
exclude removes from what was found so far. Excludes apply only to earlier includes (pinned by
`FilesetTests` "keeps the order of includes and excludes").

An include is walked from its anchor (base dir, or the root of an absolute mask):

1. The **literal prefix** (segments before the first non-literal one, `.`/`..` resolved) is
   joined without listing.
2. For each following segment:
   - literal: probe (6.2), unless the directory is listed anyway for another continuation (a
     globstar or a wildcard segment applying there): then the literal is matched against that
     listing with the comparer and takes the disk's spelling (`**/b.cs` in Unix mode does not
     find `Lib/B.cs` on a case-insensitive volume; vectors SC-227, SC-228; 8.6);
   - one-segment pattern (`*`, `?`, class, literal parts, braces without `/`): list the
     directory (subdirectories for a middle segment, files for the last one, subdirectories for
     a last segment followed by the trailing `/`), and keep the names the segment's matcher
     accepts;
   - globstar: apply the rest of the pattern at this directory (zero segments) and then recurse
     into each subdirectory, depth first (not into symlinks, 6.5);
   - braces holding `/` (`{src,test/unit}/*.cs`): the mask is expanded into its alternatives
     before walking and the union is walked (each alternative with its own literal prefix).
3. **Pruning**: a subdirectory is not entered when an exclude that comes *after* this include
   covers it entirely, i.e. the exclude is `P/`, `P/**` or `P/**/*` and `P` matches the
   subdirectory's path. Sound because a file removed by a later exclude cannot be brought back
   by this include (a later include brings it back by its own walk). This is what makes
   `!! "**/*.cs" -- "**/.*/" -- "bin/**"` skip `.git`, `bin` without reading them. Pruning is
   an optimisation only: the result is the same without it.
4. An exclude is a whole-path match: the exclude mask is anchored at the base dir and matched
   against each found file's full path (normalized to `/`); a directory exclude (trailing `/`)
   is matched against each found path's ancestor directories.
5. **Evidence checks** for the legacy warnings (7.3) piggyback on the walk and cost nothing when
   the mask has no legacy-sensitive spelling: XP104 (a listed entry's name equals the literal
   reading of a `[...]`/`{...}` segment), XP113 (a `dir/**` include yields a result, or a
   `dir/**` exclude removes one), XP114 (a `*.*` segment rejects a listed name without `.`).
   XP120 is checked once per fileset evaluation (6.2).

### 6.4 The OS listing's role

The .NET listing (`Directory.EnumerateFiles/Directories(dir, filter)`) knows only `*` and `?`,
with Win32 quirks (`*.*` = `*`; `?` can match zero characters before `.` or the end; on .NET
Framework a 3-letter extension also matches longer ones, `*.rdl` finds `c.rdlx`; on macOS the
listing is case-insensitive by platform default, even on a case-sensitive volume). Measured
on macOS (`measurements-dotnet.md` §1–2) and on Windows (`measurements-windows.md`, below).
The 3-letter extension rule was **not** reproduced on .NET 10 (below); it stays documented for
.NET Framework only. Therefore:

- the module's matcher decides every name; the OS filter is only a **pre-filter** that may return
  a superset;
- the filter passed is the segment with escapes resolved when the segment holds only literals,
  `*` and `?` and every literal character is in a safe set (letters, digits, `. _ - +` and
  space); `"*"` otherwise (classes, braces, and characters such as `"`, `<`, `>` that Windows
  maps to DOS wildcards; C7);
- all Win32 quirks are thus dropped, including `*.*` = any name (section 3 items 15–16; XP114
  on evidence). The `cp` task's `Dir` mode, which used `dirMask </> "**/*.*"`, builds
  `dirMask + "/**/*"` (with `/`, as the prototype did).

**Measured (macOS, D17, C10).** The legacy `Directory.Enumerate*` overloads behave as the Win32
match type: `*.*` returned the dotless name `b`, a trailing `?` matched zero characters (`b?` →
`b`, `q.c?` → `q.c`), while the Simple match type needs one character and a dot for `*.*`
(`measurements-dotnet.md` §1). `[ ( {` and `x[1].cs` are literal (no classes). `"`, `<`, `>` as
patterns matched only files literally named so. The search pattern is case-insensitive under
`PlatformDefault` even on a case-sensitive APFS volume; only `MatchCasing.CaseSensitive` is exact (§2,
§3). All of it fits the rule above: the OS filter is a superset pre-filter and the module decides.

**Measured (Windows 11 ARM64, NTFS, .NET 10.0.13, Xake 3.15.0 from nuget, 2026-11-01).** Case ids refer
to `measurements-windows.md`; the legacy `Directory.EnumerateFiles(dir, pattern)` overloads are the
Win32 match type here too.

- `?` at the end of a name matches zero characters: `b?` finds `b`, `q.c?` finds `q.c`, and `a.cs?`
  finds `a.cs` and `a.csx` (measurements-windows.md §D1, §D1b). Same as macOS; the Simple match type needs one
  character (`b?` finds nothing, §D1b, §D2).
- `*.*` matches dotless names in Win32 mode (`b` listed); the Simple match type needs a dot (`b`
  absent) (§D1, §D2). Same as macOS.
- The 3-letter extension rule did **not** reproduce: `*.cs` does not list `a.csx`, `*.c` lists
  nothing, and `*.c?` lists `a.cs` but not `a.csx` (§D1). Documented behaviour of .NET Framework only; **still
  unmeasured on .NET Framework** (the script ran on .NET 10; Xake's net462 build was not run).
- `"`, `<`, `>` are DOS wildcards in Win32 mode, unlike macOS: `<` and `>` as patterns listed the
  dotless one-character `b` and no name with a dot, and `*"*` listed `b` and `q.c`; the Simple
  match type lists nothing for any of them. `"` alone listed nothing. These characters cannot occur
  in Windows file names (creating them fails, §D1b), so a pre-filter that contained them could
  only return a different set, never find them literally: the safe-set rule above stays and is
  now justified on Windows (§D1, §D1b).
- `[`, `{` and `(` are literal: `x[1].cs`, `x{1}.cs`, `(x).cs` are found by their own spelling, the
  pattern `[` finds nothing (§D1). There are no classes in the OS pattern.
- `MatchCasing`: `PlatformDefault` and `CaseInsensitive` fold case (`A.CS` finds `a.cs`, `*.CS` finds
  every `.cs` name); `CaseSensitive` folds nothing (`*.CS`, `A.CS` find nothing). `MatchType`
  changes only `*.*`, `?`, `<`, `>`, `"` as above (§D2). Same as macOS apart from the DOS wildcards.
- The volume is case-insensitive (`File.Exists("A.CS")` true). `Directory.GetFiles(dir, "A.CS")` returns the
  **on-disk** spelling `a.cs`; `FileInfo(dir/A.CS).Name` keeps the requested spelling `A.CS` (§D3). Same as
  macOS, so D25 (a probed segment keeps the mask's spelling) is unaffected.
- **Junctions** are followed by the legacy `AllDirectories` overload and by `EnumerationOptions` with
  default `AttributesToSkip` (`link\x.cs` and `real\x.cs` both listed); `AttributesToSkip =
  ReparsePoint` skips them. A junction has `FileAttributes.Directory | ReparsePoint` and a
  `LinkTarget` (§D4b). Directory and file **symlinks** could not be created without the
  privilege (see below).
- **A junction cycle does not end by itself in useful time**: with a junction pointing at the root,
  `AllDirectories` produced 10 000 entries (the harness cap) at depth 5001 in 0.5 s and was still
  going (§D4d); unlike macOS, which stops at the 32-link ELOOP limit (6.5). The cycle is cut only by
  `AttributesToSkip = ReparsePoint` (2 entries). A module that skips every `ReparsePoint` directory
  under `**` (D17) therefore needs no cycle detection on Windows either. The natural end of an
  unbounded recursion (path length limit) was not measured.
- **Order**: on NTFS the listing came back in name order (`a a10 a2 b c` for files created
  `c a b a2 a10`), the same on every call and for `GetFiles`, `EnumerateFiles` and `cmd dir /b` (§D5).
  Only lower-case ASCII names were tried; mixed-case and non-ASCII order is not measured.
- **Hidden**: a name starting with a dot has attribute `Archive` only, not `Hidden`, on Windows
  (§D6). The legacy overloads return both `.hidden.cs` and a file with `FileAttributes.Hidden`;
  default `EnumerationOptions()` (`Hidden | System`) skips only the attribute-hidden file and **returns**
  the dotfile; `AttributesToSkip = None` returns both. This is the opposite of macOS, where default
  options skip dotfiles (6.7, 9.5).
- **8.3 short names**: neither the legacy overload nor `MatchType=Simple` matches the short name of
  `LongFileName.cs` (`LONGFI~1.cs`, `*~1*` list nothing), while `File.Exists(dir/LONGFI~1.CS)` is true: the short
  name resolves as a path but is not a listing pattern (§D9). Enumerated names are always long
  names. `fsutil 8dot3name query` was denied, so whether 8.3 generation is on for the volume is not
  recorded.
- **Unicode** (§D7): NFC and NFD spellings are two distinct files on NTFS (APFS collapses them); each
  spelling as a pattern finds its own file, `File.Exists` is true for both. Consistent with D27 (no
  normalization); a Windows tree can hold both.

All of it fits the rule above: the OS filter is a superset pre-filter and the module decides. **No
case was found where the OS filter returned less than the module would** (checked: `?`, `*.*`, `[`,
`{`, `(`, case, hidden, short names, Unicode). The one effect that would reduce the result is
`EnumerationOptions` with default `AttributesToSkip`, which drops attribute-hidden files on Windows
and dotfiles on macOS; the walker must not use it (9.5).

**Still unmeasured (Windows)**, with the reason:

- **Directory and file symlinks**: `mklink /D`, `mklink` and the API all failed with "a required
  privilege is not held by the client" (the script ran without administrator rights or
  Developer Mode), so measurements-windows.md §D4a, §D4c and the symlink parts of §E4 have no data: whether
  the walker follows a symlink to a directory, a file symlink's attributes (`ReparsePoint`,
  `LinkTarget`), a symlink cycle and how it ends. The junction results above are the only
  Windows link data.
- the 3-letter extension rule on .NET Framework (net462 build not run);
- an unreadable directory (§D8 skipped: needs an ACL change);
- volume mount points and cloud-sync placeholders under `**` (6.5);
- the 3.x engine on a Windows junction **cycle** (§E4 created no cycle); only the .NET level is
  measured (§D4d).

### 6.5 Symlinks

- Literal segments and one-segment patterns go through a symlinked directory as the OS resolves
  it (`src/*/*.cs` enters `src/link/`).
- **Symlinks are not followed under `**`** (D17): globstar recursion does not descend into a
  symbolic link to a directory, which prevents cycles without cycle detection. A link can still
  be reached explicitly (`node_modules/x/**`). 3.x followed them (`SearchOption.AllDirectories`;
  section 3 item 26). Consequence, as in bash ≥ 4.3: `**/*.cs` can find fewer files than
  `*/*.cs` when a directory is a link.
- A directory skipped under `**` because it is a link is **logged once** per directory in the
  build log, at level **Message** (D29), as an unreadable directory is reported once (6.6, at
  level Warning): the skip is the documented behaviour, not a problem.
- **Measured on macOS** (D17, S5, D29): 3.x follows directory symlinks under `**` and `*/` and lists
  file symlinks as files; a cycle ends at the OS symlink limit (ELOOP, depth 32), which the
  recursive enumeration swallows silently (`measurements-engine.md` §4, `measurements-dotnet.md`
  §4). `FileAttributes.ReparsePoint` is set for directory and file symlinks, so a link is
  recognised by attribute on netstandard2.0/net462, which lack `LinkTarget`; the walker tests the
  attribute of each subdirectory (9.5). A file symlink is a file and is listed.
- **Measured on Windows** (`measurements-windows.md` §D4b, §D4d, §E4): a junction carries
  `FileAttributes.Directory | ReparsePoint` and a `LinkTarget`, so the attribute test of 9.5 finds
  it; the 3.x scan follows it under `**` and `*/` (`jct\x.cs` and `real\x.cs` both listed). A
  junction cycle is **not** stopped by the OS (10 000 entries at depth 5001 within 0.5 s, then the
  harness cap): skipping `ReparsePoint` directories under `**` is what ends it, so D17 needs no
  cycle detection on Windows. Whether a directory symlink behaves the same could not be measured
  (no privilege, 6.4 "Still unmeasured"); `ReparsePoint` is set for it by the platform contract,
  as on macOS, but that is not a measurement. Volume mount points and cloud-sync placeholders
  stay unmeasured.

### 6.6 Ordering, duplicates, determinism

- Within one directory, entries are processed in **ordinal order** of their names
  (`StringComparer.Ordinal`, on every OS), files of a directory before its subdirectories
  (pre-order, as `find`). The include's results come out in that order; includes are
  concatenated in declaration order. In 3.x the order is whatever the file system enumerates
  (alphabetical on NTFS, hash order on ext4), which matters for F# compile order. Measured on APFS: raw `readdir` order, stable across runs
  but neither alphabetical nor creation order (`measurements-dotnet.md` §5), so the ordinal sort is
  required.
- Duplicates (the same full path from two includes, or `**/**`) are kept once, at the first
  occurrence, compared with the OS comparer (5.4). 3.x keeps them twice (13, C5: the
  `FilesetTests` case titled "removes duplicates" asserts 2 and is rewritten, D17).
- The result is a function of the file tree and the fileset only: no dependence on enumeration
  order, thread timing or caches. `compareFileList` (incremental check) compares sets, so ordering
  never triggers a rebuild.
- An unreadable directory met during a walk is skipped and reported once per directory as a
  build-log warning (in 3.x an `UnauthorizedAccessException` ends the build). A missing base
  directory gives an empty list (`failonempty` turns it into an error).

### 6.7 Implementation notes from the measurements

Non-normative; the full list is 9.5. The three that bear on the scanning rules: the walker must not
use the default `EnumerationOptions` (they skip dotfiles, D10); a link is detected by
`FileAttributes.ReparsePoint` (6.5); an unreadable directory is caught per directory (6.6).

## 7. Error and warning model

### 7.1 What throws, what warns

- **Errors** (exception, 7.2): input the matcher cannot give a meaning to, or that 4.0 rejects.
  Raised when the pattern is **parsed**:
  - masks: at construction (`!!`, `ls`, `++`, `--`, the builder's operations), i.e. usually when
    the script is loaded;
  - rule patterns: when the rule is created by `..>`, `*..>`, `=>`, `target`, `targets`,
    `command`, `phony` (eager check), and again (cached) when the engine first matches with it,
    for rules built from the `Rule` DU cases directly;
  - inside a build, the engine reports a `PatternException` as a `XakeException` with the same
    message (prototype `ExecCore`), so the build fails like any other error.
  Where the rejected input had a meaning in 3.x, the reason says what it was.
- **Warnings** (7.3): a legacy input that 4.0 accepts with a different meaning, or that is
  deprecated. Never an exception, never a change of what is matched. Removed in 5.0 (10.1).
  The one exception is XP120 (D6), which flags a mask that can never have an effect; it is not
  a legacy warning and stays after 5.0 (D24).

### 7.2 Exceptions and errors

```fsharp
/// An invalid pattern, or a rule whose outputs disagree. The message names the pattern;
/// ToString() is the message alone, so a script failing at load shows no stack trace.
exception PatternException of string

[<RequireQualifiedAccess>]
type PatternContext = Rule | Mask

type PatternError =
    { Pattern: string
      Context: PatternContext
      /// 1-based character position, when the error has one.
      Position: int option
      Code: string                      // "XP002" ...
      Reason: string }
    override ToString : unit -> string  // the message below
```

One exception type, the prototype's `PatternException` (tm 64; the typed layer raises it with its
own record-mismatch messages, which are not parse errors). `tryParse` returns a `PatternError`
instead of throwing (D15). Message format:

```
Invalid pattern '<text>' (<rule|mask>) at character <n>: <reason> [<code>]
```

(`at character <n>` is left out when there is no position). The prefix `Invalid pattern '<text>'`
is what the prototype's tests assert. Codes `XP0nn` are errors, `XP1nn` warnings; the numbers of
earlier drafts are kept and retired codes are not reused (retired: XP001, XP101, XP103, XP108,
XP109, XP115, XP118 folded into the errors below in the 2026-10-26 draft; XP111 and XP116 dropped
2026-11-01 by D23 and D4). XP018 and XP019 were added 2026-11-01 by D29.

| code | from | condition | reason text |
|---|---|---|---|
| XP002 | 4.0 | a `[` without a closing `]` (also `[]`, `[!]`, `[^]` with no later `]`, since a `]` right after `[` is a literal) | `an unescaped '[' has no matching ']' (Xake 3.x read it as a literal in masks and failed in rules); write \[ for a literal '['` |
| XP003 | 4.0 | `(`, `)` or `\|` outside a named group (in a mask: every `(` not reported as XP008) | rule: `'(' is reserved (Xake 3.x: a regex group); write \( for a literal '('`; mask: `'(' is reserved (Xake 3.x: a literal character in masks); write \( for a literal '(', or build the mask from a real path with PatternAst.escapePath`; for `\|` inside a group whose alternatives are literals: `'(ext:dll\|exe\|nupkg)': '\|' is reserved (Xake 3.x: a regex alternation); write (ext:{dll,exe,nupkg})` |
| XP004 | 4.0 | a `{` without `}`; a stray `}`; a stray `]` | `{`: `an unescaped '{' has no matching '}' (Xake 3.x: a literal); write \{ for a literal '{'`; `}`: `an unescaped '}' closes no '{' (Xake 3.x: a literal); write \} for a literal '}'`; `]`: `an unescaped ']' closes no '[' (Xake 3.x: a literal); write \] for a literal ']'` |
| XP005 | 4.0 | `{...}` without a top-level `,` | `'{x}' is not an alternation (no ','; Xake 3.x read it as literal text); write \{x\} for literal braces, or {a,b} for alternatives` |
| XP006 | 4.0 | an unclosed `(name:` in a rule pattern, string or typed (D23) | `the group '(name:' is not closed (Xake 3.x: in a string rule it ran to the end of the pattern); add ')'` |
| XP007 | 4.0 | a group name starting with a digit | `the group name '1' starts with a digit; it would collide with the positional match '1' (Xake 3.x accepted it); rename it` |
| XP008 | 4.0 | a `(` followed by `name:` in a mask (D23) | `named groups '(name:...)' exist only in rule patterns (Xake 3.x: an include read the group as literal text, an exclude wildcard segment matched its body); write the body alone, or \( \) for literal parentheses; PatternAst.escapePath escapes a real path` |
| XP009 | 5.0 | `\` at the end, or before a character that is not escapable | `'\x' escapes nothing; use '/' as the separator, '\\' for a backslash` |
| XP010 | 4.0 | a `/` inside a class, `[z-a]`, a class starting with `[:` | `invalid character class '[...]': <detail>` (details: `a class never matches '/'`, `the range 'z-a' is reversed`, `POSIX class names '[:...:]' are reserved`) |
| XP011 | 4.0 | a rule pattern ending in a separator | `a rule pattern names a file or a phony target and cannot end with '/' (in Xake 3.x it matched nothing)` |
| XP012 | 4.0 | `.` or `..` as a segment after a wildcard (D29 for `.`) | `'..' is allowed only before the first wildcard (Xake 3.x applied it as a parent step in masks and never matched it in rules)`; for `.`: `'.' is allowed only before the first wildcard; remove the segment` |
| XP013 | 4.0 | multi-output rule, outputs with different named groups | prototype text: `output "<p>" has groups [..], the first output [..] (missing [..], extra [..]); every output needs the same named groups, or the others cannot be named from a match` |
| XP014 | 4.0 | a class or braces outside a group in a pattern that must be formatted (multi-output, `format`) | `'{dll,pdb}' cannot be filled in from a match; put it in a named group: (ext:{dll,pdb})` |
| XP015 | 4.0 | a group name that appears more than once in a pattern, also across brace alternatives (D4) | `the group name 'p' appears 2 times; a name may appear once per pattern (Xake 3.x let each occurrence capture and the last one win); rename one occurrence, or put one group around the braces: (x:{a,b})` |
| XP016 | 4.0 | a UNC root whose server or share is not a literal | `a UNC root '//server/share' needs a literal server and share name` |
| XP017 | 4.0 | `\,` directly inside braces (D24) | `'\,' is not an escaped comma in Xake 4.0 (Xake 3.x: a path separator, then ','); write [,] for a literal comma` |
| XP018 | 4.0 | an empty pattern `""`, rule or mask (D29) | `an empty pattern names nothing; write the file or directory path, or '*' for the files of the base directory` |
| XP019 | 4.0 | braces that end the pattern with an alternative ending in a separator, `{a/,b}` (D29) | `'{a/,b}' ends with '/' in one alternative only; write {a,b}/ when every alternative is a directory, or two patterns` |

### 7.3 Warnings on legacy inputs

Sent to the build log at level **Warning** (D15; the prototype used `Message`, 13 C2), text:

```
Pattern '<text>' (<rule|include|exclude>): <part>; <part>. [<codes>]
```

Warnings name the **use** of the pattern: `rule`, or, for a mask, its role `include` or
`exclude`, because the 3.x meaning a warning names depends on it (D20: XP105 vs XP106). The parse
context is still `Mask` for both roles; `Fileset` passes the role to the check (8.2,
`Mask.check`).

Parts, in a fixed order (the codes in brackets, comma-separated). Trigger "parse" = when the
pattern is parsed (7.1); "evidence" = only when the stated observation is made, during a scan
(6.3 step 5) or a rule match; "evaluation" = when a fileset is evaluated. Every legacy text
names the 3.x meaning and the replacement.

| code | condition | trigger | part text |
|---|---|---|---|
| XP104 | mask: `[ab]` or `{a,b}` where 3.x read literal text (include, exclude literal segment) | evidence: a scanned entry is named exactly as the literal text | `'[ab]' is a character class from Xake 4.0 (one of 'a', 'b'); Xake 3.x read the literal text '[ab]', and '<entry>' exists; write \[ab\] for the literal text` (braces: `'{a,b}' is an alternation from Xake 4.0; Xake 3.x read the literal text '{a,b}', and '<entry>' exists; write \{a,b\} for the literal text`) |
| XP105 | `[c]` where 3.x meant `c` too (rule, exclude wildcard segment); `c` not one of `* ? , \` | parse | `'[(]' is the literal '(' (as in Xake 3.x); write \( instead` / for an ordinary character: `'[x]' is the literal 'x'; write x instead` |
| XP106 | `[c]` where 3.x read literal text (include, exclude literal segment) | parse | `'[1]' matches the single character '1' from Xake 4.0; Xake 3.x read the literal text '[1]'; write \[1\] for the brackets, or 1 for the character` |
| XP110 | `\` used as a separator | parse | `'\' as a path separator is deprecated and is only an escape from Xake 5.0; use '/'` |
| XP112 | rule: segment-local `**` | parse | `'a**b' matches within one segment from Xake 4.0, like 'a*b'; in Xake 3.x it crossed '/'; write '*' for one segment, or '/**/' between segments for any depth` |
| XP113 | mask ending in `/**` | evidence: an include yields a result, an exclude removes a file | `'dir/**' is every file below 'dir' from Xake 4.0; in Xake 3.x an include listed the directories below 'dir' and an exclude matched nothing; write 'dir/**/' for the directories` |
| XP114 | include: `*.*` segment | evidence: a listed name without `.` is rejected | `'*.*' needs a '.' in the name from Xake 4.0; Xake 3.x also listed '<name>'; write '*' for every name` |
| XP117 | `[!a]` in a rule or an exclude wildcard segment | parse | `'[!a]' is a negated class (any character but 'a') from Xake 4.0; in Xake 3.x it was a class of '!' and 'a'; write [^a] for "not a" (same meaning in 3.x and 4.0), or [a!] for the 3.x meaning` |
| XP119 | rule: a globstar before a separator | evidence: a rule match where it took zero directories | `'**/' matched zero directories for '<target>'; in Xake 3.x '**/' in a rule needed at least one directory, so this rule did not match it; to require one or more directories write '*/**/' (with a capture: '(path:*/**)/')` |
| XP120 | exclude: absolute, its literal prefix below neither the base dir nor any earlier include's anchor (D6) | evaluation | `the exclude is an absolute path outside the fileset's base directory '<base>' and every include, so it removes nothing; a leading '/' is the file-system root in Xake (in .gitignore it is the base directory); drop the leading '/' to anchor it at the base directory` |

Rules:

- **Once per pattern**: the key is `(use, pattern text)` (use = rule, include or exclude),
  process-wide (static `ConcurrentDictionary`), so the same text used as a rule and as a mask
  can warn twice (the 3.x meaning differs, so does the text). All parse-time parts of one
  pattern go in one line; an evidence or evaluation warning is one more line for the same key,
  at most once per code.
- Warnings raised while no build log is attached (masks built at script load) wait in a buffer
  (at most 1000) and are delivered when a build attaches its log (prototype
  `Warnings.attach`). Several attached logs all receive them.
- Paths read from disk, the project root and `BaseDir`/`@@` values are paths, never patterns:
  they produce no warnings (prototype test "paths read from disk give no warning"). A mask built
  with `</>` on Windows contains `\` and **does** warn XP110 (tm 57, by design); `escapePath`
  output never warns.
- No warning for the case changes (5.4), the dropped OS-listing quirks, symlinks, ordering and
  duplicates: release notes only (section 3). A link skipped under `**` is a Message, not a
  warning (6.5).
- **All legacy warnings (XP104–XP119) are removed in 5.0** (10.1); XP120 stays.

## 8. Public API

### 8.1 Placement and name

- New file `src/core/PatternAst.fs`, compiled before `Path.fs`, holding the public module
  **`Xake.PatternAst`** (D16; tm 13: `PatternAst` becomes public; the name is the one the
  prototype, its tests, `docs/overview.md` and tm 64 use). In 3.x it is `internal` inside
  `Path.fs`.
- Not opened by `open Xake` (a plain module, no `[<AutoOpen>]`), so `Lit`, `Star`, ... do not
  enter scripts' scope. `Xake.Patterns` is avoided: it was the type-provider package name.
- The matcher and the regex translation stay **internal** (section 9); scripts and the typed
  layer match through the existing public functions, whose signatures do not change:
  `Path.matchGroups`, `Path.matches`, `Path.matchesPattern`, `Path.parse`, `Path.parseDir`,
  `Fileset.*`. `Includes of PathMask` is kept in 4.0 (C8).

### 8.2 Signatures

```fsharp
module Xake.PatternAst

// ---- AST (4.4)
type ClassItem = One of char | Range of char * char
type Token =
    | Lit of string | Sep | Star | AnyChar | Globstar | DoubleStar
    | Class of negated: bool * items: ClassItem list
    | Alt of Token list list
    | Group of name: string * body: Token list
[<RequireQualifiedAccess>] type PatternContext = Rule | Mask
[<RequireQualifiedAccess>] type MaskRole = Include | Exclude   // selects the 3.x meaning a warning names (7.3; D24)
type Issue = { Code: string; Text: string }          // one parse-time warning part (7.3)
type PatternError = { Pattern: string; Context: PatternContext; Position: int option; Code: string; Reason: string }
exception PatternException of string

// ---- parsing: one parser, two entry points (D9)
module Rule =
    val parse    : string -> Token list                                   // raises PatternException; no warnings
    val tryParse : string -> Result<Token list * Issue list, PatternError>
    val check    : string -> unit                                         // parse, report parse-time warnings once, raise on error
module Mask =
    val parse    : string -> Token list
    val tryParse : string -> Result<Token list * Issue list, PatternError>
    val check    : MaskRole -> string -> unit

val unparse    : Token list -> string
val escape     : string -> string                                  // a plain string as a literal (5.7)
val escapePath : string -> string                                  // a real path as a mask (5.7, D7)
val reservedChars : string                                         // "()[]{}|"

// ---- reading the AST
val groupNames : Token list -> string list                       // first-appearance order, nested included
val wildcards  : Token list -> (Token * int) list                // top-level bare wildcards with their positional numbers (5.6)
val specificity: Token list -> int * int * int                   // = Path.Specificity (5.8)

// ---- generation (5.7)
val format        : named: (string -> string) -> positional: (int -> string) -> Token list -> string
val formatMatches : Map<string, string> -> string -> string      // replaces ExecCore.applyWildcards
val glob          : named: (string -> string option) -> positional: (int -> string option) -> Token list -> string

// ---- warnings (7.3)
module Warnings =
    val internal attach : (string -> unit) -> System.IDisposable
```

`Rule.parse` replaces the prototype's `parse : string -> Token list`.
The module `Rule` and the case `PatternContext.Rule` do not clash because `PatternContext` is
`RequireQualifiedAccess`; inside `Xake` the name `Rule` also denotes the engine's rule type,
so scripts write `PatternAst.Rule.parse`.

Kept internal (implementation, may change if the matcher changes): the regex translation and
its cache (9), the evidence checks, `checkSameGroups` (used by `*..>`, `targets` and the typed
layer through `InternalsVisibleTo` or a public wrapper, decided in Phase 3), `plainText`, the
OS-filter helper. Not added (D16): a public compiled-pattern type (possible in a 4.x minor), a
public `toRegex`, a replacement of `FilesetElement`.

### 8.3 Engine wiring

- `ExecCore.explainMatch` and `makeTarget` keep calling `Path.matchGroups pattern root`; only its
  implementation moves onto the module (it also runs the XP119 evidence check).
  **`getRuleMatch` is unchanged** (brief e), including `""` for an unknown key and the `"0"` key.
- `ExecCore.applyWildcards (Some matches) pat` becomes `PatternAst.formatMatches matches pat`;
  `applyWildcards None` (identity) disappears with its only caller. `wildcardsRegex` and
  `patternTagRegex` are deleted (the third grammar encoding).
- `..>`, `*..>`, `=>`, `target(s)`, `command`, `phony` call `PatternAst.Rule.check` on their
  patterns (and `*..>` / `targets` the group-set check, XP013).
- `Fileset` construction calls `PatternAst.Mask.check MaskRole.Include|Exclude`; `toFileList` walks with
  the module (6) and runs the XP120 check.

### 8.4 PR 2 API notes (the implemented form of D34)

What `src/core/PatternAst.fs` implements in PR 2, where it differs from 8.2:

- `Mask.warnings : MaskRole -> string -> Issue list` (pure): every parse-time warning part of a
  mask for its role (XP105, XP106, XP110, XP117), empty for an invalid mask. It takes the
  **pattern text**, not the token list as D34 wrote (`MaskRole -> Ast -> PatternWarning list`):
  `[!a]` and `[^a]` parse to the same `Class`, and whether a `Sep` came from `\`, are not in the
  AST (4.4), so XP117 and XP110 cannot be computed from tokens. The part type is `Issue`.
- `Mask.tryParse` returns only the role-independent parts (XP110); `Rule.tryParse` returns every
  rule part (XP105, XP110, XP112, XP117). `Mask.check role` reports `Mask.warnings role`.
- `Rule.check` / `Mask.check` report once per (use, text) through `Warnings` (internal `attach`,
  `report`, `format`); the line format is 7.3's, the codes joined with `", "`.
- Parse-level reading added with D34: a non-negated one-character class of `*`, `?`, `,` or `\`
  (`[*] [?] [,] [\\]`) is the escape idiom and parses to literal text (`a[*]b` is `Lit "a*b"`), as
  5.7 requires (`parse (escape s)` is `[Lit s]`); every other `[c]` is `Class (false, [One c])`
  (4.4). 4.4 and 5.7 contradicted each other for these four characters.
- Not in PR 2: `format`, `formatMatches`, `glob` (their vectors come with the matcher, PR 3).
- `PatternException` overrides `Message` with the error text. The engine's internal
  `Path.PatternAst` is untouched until Phase 4.

### 8.5 PR 3 API notes (the implemented matcher)

What PR 3 implements (`src/core/PatternAst.fs`, new internal `src/core/PatternMatch.fs`):

```fsharp
// Xake.PatternAst (public, 8.2)
val format        : named: (string -> string) -> positional: (int -> string) -> Token list -> string
val formatMatches : Map<string, string> -> string -> string
val internal checkOutputs : string list -> unit      // multi-output check: XP014, then XP013

// Xake.PatternMatch (internal)
type RuleMatch = { Captures: Map<string, string>; Evidence: string list }
val matchRule  : windows: bool -> root: string -> pattern: string -> target: string -> RuleMatch option
val matchMask  : windows: bool -> baseDir: string -> mask: string -> path: string -> isDirectory: bool -> bool
val regexText  : PatternContext -> text: string -> anchor: string -> windows: bool -> string option
```

- `windows` is the comparer of D11 made explicit (ignore-case with `CultureInvariant`, `\` in a
  path is a separator); the Phase-4 adapters pass `not Env.isUnix`. `Path.matchGroups` and
  `Path.matches` are unchanged until Phase 4.
- `Captures` holds every named group (`""` when it took no part, D37), every positional number
  at any depth and `"0"` (the path, `\` turned into `/` in Windows mode). `Evidence` is
  `["XP119"]` when a globstar before a separator took zero directories, except after a `*`
  segment or inside braces (D37); reporting it once per pattern is the engine's (Phase 4).
- `checkOutputs` is the `checkSameGroups` of 8.2 (XP013 compares name sets); the XP014 text
  suggests `(name:...)`.
- Differences from the 9.2 table: a globstar is `(.+)` and owns one adjacent separator,
  `(?:/(.+))?` or `(?:(.+)/)?`, also when it is the whole body of a group or a whole brace
  alternative (the owning wrapper then sits outside the group or the braces, 9.3); a globstar
  ending a file pattern keeps its separator mandatory (`a/**` does not match `a`), ending a
  directory mask it is optional (`a/**/` matches `a`); class items are emitted as `\uXXXX`;
  the end anchor is `\z` (`$` would accept a trailing newline); `ExplicitCapture`, with the
  regex group names generated (`w1`, `g1`, ...), so user names never reach the regex.
- Cache: one `ConcurrentDictionary` keyed by (context, text, anchor, windows); rule patterns are
  `Compiled`, masks interpreted (PR 4 decides for the walker). No fast paths yet. Measured:
  the 236 rule vectors in both modes, 100 passes (47 200 matches): 57 ms cached, after a
  169 ms first pass that compiles.

### 8.6 PR 4 API notes (the implemented walker)

What PR 4 implements (new internal `src/core/PatternScan.fs`, after `PatternMatch.fs`); the
engine's `Fileset` keeps its 3.x scanner until Phase 4 (D31):

```fsharp
// Xake.PatternScan (internal)
type ScanNote    = LinkSkipped of dir: string | Unreadable of dir: string
type ScanWarning = { Code: string; Use: string; Pattern: string; Text: string }
type ScanResult  = { Files: string list; Warnings: ScanWarning list; Notes: ScanNote list }
val scan           : windows: bool -> baseDir: string -> fileset: (MaskRole * string) list -> ScanResult
val reportWarnings : ScanResult -> unit

// Xake.PatternMatch (internal), added
val maskMatcher : windows: bool -> baseDir: string -> mask: string -> (string -> bool -> bool)
// Xake.PatternAst.Warnings (internal), added
val reportEvidence : usage: string -> pattern: string -> Issue -> unit
```

- `scan` evaluates the ordered elements as `toFileList` will (6.3) and raises
  `PatternException` when a mask does not parse (every mask is parsed before the walk). `Files`
  are full paths: the base directory as passed (or the absolute mask's root), probed segments in
  the mask's spelling, listed names in the disk's, joined with the OS separator; a directory
  (trailing-`/` include) has no trailing separator. `Warnings` and `Notes` are those of this one
  evaluation (once per (code, use, pattern), once per directory). `reportWarnings` sends the
  warnings through the process-wide registry of 7.3 (`reportEvidence`: key (use, pattern),
  once per code); logging the notes (Message for `LinkSkipped`, Warning for `Unreadable`) is the
  engine's, in Phase 4. Phase 4 passes `windows = not Env.isUnix`.
- The walk: the mask's root is split off, braces holding `/` or a globstar are expanded, and the
  alternatives are walked together from their anchor as a set of continuations (the remaining
  segments, interned), so one directory is read once and the union comes out in tree order
  (SC-044). A globstar adds the rest of the mask at its own directory; ending a file mask it is
  `*` here and `**` below (5.5).
- **Probe or listing** (reading of 6.3 step 2 forced by SC-227/SC-228): a directory is listed
  when any continuation at it has a wildcard or globstar head; every head there, literal ones
  included, is then decided by the matcher against the listing, so a literal after `**` takes
  the comparer and the disk's spelling (`**/b.cs` does not find `Lib/B.cs` in Unix mode on a
  case-insensitive volume, and finds it spelled `B.cs` in Windows mode). A directory whose
  continuations all have literal heads is not listed: each literal is probed (`File.Exists` for a
  last file segment, `Directory.Exists` otherwise) and keeps the mask's spelling (D25).
- Listing: `DirectoryInfo.EnumerateFileSystemInfos(filter)` (legacy overload, dotfiles
  included, no attribute filtering, D10, D33), sorted with `String.CompareOrdinal`, files of a
  directory before its subdirectories. The filter is the segment itself only when it is the
  only head at the directory and holds just `*`, `?` and ASCII letters, digits, `. _ - +` and
  space, and not `*.*` (XP114 needs the dotless names); `"*"` otherwise, and always `"*"` in
  Windows mode on a Unix host (a case-sensitive OS filter is not a superset there).
- Links: a subdirectory with `FileAttributes.ReparsePoint` is not entered by globstar
  recursion (`LinkSkipped` once per directory); a one-segment pattern or a literal enters it.
- Unreadable: `UnauthorizedAccessException`, `IOException` or `SecurityException` from a listing
  logs the directory (`DirectoryNotFoundException` is silent: a missing base is an empty list); a
  probe that answers false is checked with `File.GetAttributes`, and an
  `UnauthorizedAccessException` logs the directory being probed in (D39, SC-275: `locked`).
- Pruning (6.3 step 3): `P/` prunes for every include; `P/**` and `P/**/*` prune for file
  includes only (a directory result is not removed by a file mask). A `P/**` exclude prunes only
  once a found file is known to match it, so that its XP113 follows the unpruned result
  (SC-122: no warning when the pruned directory holds nothing the include would find).
- Evidence (7.3): XP104 when a listed name equals the 3.x literal reading of a wildcard-free
  segment holding a class (other than a one-character class) or braces (both `[^` and `[!`
  spellings of a negated class), and for an exclude when a found path has such a segment
  (relative to the base); XP113 when a `dir/**` include yields a file or a `dir/**` exclude
  removes one; XP114 when a `*.*` segment rejects a listed name without `.`; XP120 when an
  absolute exclude's literal prefix is neither below nor above the base directory and every
  earlier include's literal prefix (an exclude whose prefix contains the base, `/**/obj/`, can
  remove files, so "above" counts as overlap; the 6.2 wording names "below" only).
- Matching a segment uses `maskMatcher windows "" <segment text>` (the cached, interpreted
  regex of PR 3; a literal segment through `escape`). Measured with the `FilesetTests`
  benchmark (1360 files, macOS): `**/*.cs` 4.1 ms (3.x) vs 4.2 ms (walker); with
  `**/.*/**/*`, `bin/**/*`, `obj/**/*` excludes 12.0 ms vs 5.5 ms (pruning). No fast paths.

## 9. Matcher implementation (non-normative)

### 9.1 Status

This section is **not normative** (D3). The requirements are the grammar (4), the semantics
tables (5) and the conformance vectors; any implementation that produces the matches and
captures those define is conforming. What follows is the Phase-3 starting point (D18), revisable
on measurement. Nothing in the public API exposes `Regex`.

### 9.2 Translation to a .NET regex

The AST is translated to a .NET regex (`t` = translate):

| token | regex |
|---|---|
| `Lit s` | `Regex.Escape`-equivalent of `s` (all of `. $ ^ + ! = { } ( ) [ ] \| \ * ? #` and white space escaped) |
| `Sep` | `/` |
| `Star`, `DoubleStar` | `([^/]*)` |
| `AnyChar` | `([^/])` |
| `Globstar` at the start, followed by a separator | `(?:(.*)/)?` (the `/` is consumed) |
| `Globstar` between separators | `(?:(.*)/)?` (after the preceding `/`) |
| `Globstar` at the end, after a separator | `(.*)` |
| `Globstar` alone | `(.*)` |
| `Class (false, items)` | `[e(items)]` |
| `Class (true, items)` | `[^/e(items)]` |
| `Alt alts` | `(?:t a1\|t a2\|...)` |
| `Group (n, body)` | `(?<n>t body)`, with the globstar edge cases of 9.3 |

`e(items)`: each `One c` emitted escaped (`\c` for `] \ ^ - [`), each `Range (a, b)` as
`\a-\b` with both ends escaped the same way.

Anchoring: `^` + escaped anchor (project root, base dir; `..`/`.` collapsed in the literal
prefix) + `/` + body + `$`, options `CultureInvariant` and `Singleline` (+ `IgnoreCase` on
Windows). Paths are normalized to `/` before matching (paths from disk in 3.x too, tm 56), so
`[^/]` replaces 3.x's `[^/\\]`. The project root is escaped (3.x did not escape it: bug fix,
section 3 item 29). A mask with a trailing `/` is matched against directory paths, the trailing
`Sep` not emitted.

Example: `src/**/(name:*).fs` under root `/p` →
`^/p/src/(?:(.*)/)?(?<name>([^/]*))\.fs$`.

The 3.x translation (`*` → `([^/\\]*)`, every `**` → `(.*)`, reserved characters raw), pinned by
the prototype's `PatternAstTests` "AST regex matches the former substitution byte for byte", is
not kept; those vectors become implementation tests of this table, not conformance vectors
(10.3).

### 9.3 Implementation notes

- **Globstar at a group edge** (critique M1). The implementation must produce the captures of
  table 5.5; a naive translation fails for a globstar at a group edge. With the rows above,
  `(path:bin/**)/file.o` would either let the group swallow the `/` (`path = bin/x/y/`) or emit
  the `/` twice (and no longer match `bin/file.o`). Translate a globstar last in a group body
  with a separator after the group as `(?<path>bin(?:/(.*))?)/file\.o`, and a globstar first in
  a group body with a separator after it as `(?:(?<p>.+)/)?b`, with `p` = `""` when the optional
  part is skipped. Both are conformance vectors (5.5).
- **Class items must be escaped for .NET** (critique M2). Classes may contain `]`, `\`, `^`,
  `-`, `[` (`[]a]`, `[\\]`, `[a^]`, `[-[]`); emitted raw they change meaning, and `-[` inside a
  .NET class is character-class subtraction. Hence `e(items)` above.
- **Newlines** (C1): `Singleline` makes `.` (globstar) match `\n` like `[^/]` (star) does, so
  `*` and `**` agree on Linux names that contain a newline.
- **One comparer, several code paths** (C4): regex `IgnoreCase | CultureInvariant` and the fast
  paths' `StringComparer.OrdinalIgnoreCase` can disagree on a few non-ASCII letters (Kelvin sign,
  dotted/dotless i; unverified per .NET version). Phase 3 must make every code path agree with
  the comparer of 5.4; how (proof that the fast paths are a subset, or comparing by
  `ToUpperInvariant` in both) is decided there.
- **Captures without a regex**: an own matcher would have to reproduce greedy leftmost values,
  positional numbering and the `"0"` key (5.6).

### 9.4 Backend choice and performance

Options:

| | AST → .NET regex, cached | own matcher over the AST |
|---|---|---|
| exact `getRuleMatch` values (greedy leftmost, numbering, `"0"`) | by construction (the 3.x engine is a regex) | must re-implement backtracking for multiple wildcards to reproduce greedy captures |
| alternation, classes, case-insensitive culture rules | built in (`(?:a\|b)`, `[...]`, `IgnoreCase`) | to write and test |
| cost to build | `RegexOptions.Compiled`: ~1 ms + JIT per pattern (prototype comment); interpreted: ~20–50 µs | ~0 |
| cost per match | ~0.2–1 µs per path (compiled) | similar for simple globs, worse with backtracking captures |
| code | ~100 lines translation + cache | ~400+ lines plus a second test corpus |
| risk | ReDoS on pathological patterns (`**/**/**/...`): patterns are written by the script author; paths are short | bugs in captures |

Starting point (D18): **regex generated from the AST, cached**, plus fast paths that skip the
regex where the AST makes it trivial:

1. Cache key `(context, pattern text, anchor, case mode)`, process-wide `ConcurrentDictionary`
   (3.x: per mask and per (pattern, root)); regexes are thread-safe for matching.
2. `RegexOptions.Compiled` for rule patterns and exclude masks (few, hot: matched against every
   target / every found file); **interpreted** for one-segment walker matchers and for one-off
   patterns built at run time in large numbers (e.g. `glob` results in a loop).
3. Fast paths: a literal segment is a string comparison; a segment `*` matches everything; a
   segment `*<literal>` (`*.cs`) is an `EndsWith` with the OS comparer. These cover the bulk of
   real masks (`**/*.cs`, `src/*.fs`) without a regex per name.
4. Walking cost is dominated by the listing, not the matching; pruning (6.3) is the large win
   (`.git`, `bin`, `obj`). The G8 measurement (`!**/.*/**/*` at ~1 ms per file before caching)
   is the regression to guard: the `FilesetTests` benchmark runs in Phase 3 before/after, with
   a target of no regression on the include-only case and a measurable win on the exclude
   cases. The evidence checks (6.3 step 5) must not show up in it.

Rejected for now (D18): an own matcher; no cache; always `Compiled`; `NonBacktracking` (.NET 7+
only; Core targets netstandard2.0). An own matcher stays possible later behind the same AST.

### 9.5 Implementation notes from the measurements

Non-normative. Measured on macOS (APFS, .NET SDK 10), `measurements-dotnet.md` unless stated;
the Windows facts (NTFS, .NET 10, `measurements-windows.md`) are in the last bullets of this list
and in 6.4.

- **Hidden files** (§6). .NET reports a leading-dot name on macOS as `FileAttributes.Hidden`
  (name-derived, no BSD flag), so `EnumerationOptions()` with its default `AttributesToSkip`
  (`Hidden | System`) skips dotfiles. The legacy overloads return them. To honour D10 the walker
  uses the legacy overloads or sets `AttributesToSkip = 0`.
- **API availability.** `EnumerationOptions`, `MatchCasing`, `LinkTarget` and `IgnoreInaccessible`
  do not exist on netstandard2.0/net462 (Core's targets); only the legacy `Directory.Enumerate*`
  overloads and `FileSystemInfo.Attributes` are available. Links are therefore detected by
  attribute: `ReparsePoint` is set for directory and file symlinks on macOS (§4).
- **Inaccessible directories** (§8). The legacy `AllDirectories` overload throws
  `UnauthorizedAccessException` at enumeration time, possibly after partial results. The walker is
  depth first and catches the exception per directory (6.6), never relying on all-or-nothing.
- **The OS filter is a superset pre-filter, the module decides** (6.4). Measured reasons: the
  search pattern is case-insensitive by platform default even on a case-sensitive volume (§2, §3);
  `*.*` does not match dotless names outside Win32 mode (§1); Win32 `b?` matched `b`, a zero-character
  `?` (§1); `[ ( {` are literal.
- **Order** (§5). Enumeration order is raw `readdir` order, stable but not alphabetical, so the ordinal
  sort of D17 is required.
- **Unicode** (§7). NFC and NFD spellings collapse to one file on APFS (the first spelling is
  kept); `File.Exists` finds it in either form; the enumeration pattern does not normalize (the NFD
  pattern matched nothing). Consistent with D27.
- **Search patterns with `"`, `<`, `>`** matched only literal names on macOS (§1); on Windows they
  are DOS wildcards in Win32 mode (`measurements-windows.md` §D1, §D1b), so the safe-set rule of 6.4
  stays, now measured on both OSes.
- **Case of the disk spelling** (§3). `GetFiles(dir, "A.CS")` returns the on-disk `a.cs`, and
  `FileInfo(...).Name` keeps the requested spelling: the walker takes names from the listing only
  where it intends to (D25 keeps the mask's spelling for probed segments).
- **Symlink cycles** (§4). A direct call on a path past 32 levels throws `IOException` (ELOOP);
  under the legacy recursion it is swallowed. Skipping links under `**` makes this irrelevant.
- **Windows: links by attribute** (`measurements-windows.md` §D4b). A junction has `Directory |
  ReparsePoint` and `LinkTarget`; with the legacy `AllDirectories` overload and with default
  `EnumerationOptions` it is followed, so the walker tests `FileAttributes.ReparsePoint` on each
  subdirectory itself (as on macOS). The same attribute test skips junctions and, expected, directory
  symlinks (not measured, no privilege). A file with `ReparsePoint` is not a directory and is
  listed (file symlinks unmeasured).
- **Windows: cycles** (§D4d). A junction cycle is not stopped by the OS (10 000 entries, depth 5001, 0.5 s,
  then the cap); the attribute test is the only protection, so it must run before descending, not
  after listing. Counting depth or entries is not needed with it.
- **Windows: order** (§D5). NTFS lists in name order (lower-case ASCII names measured), but the
  ordinal sort of D17 is still applied; mixed-case order is not measured.
- **Windows: Hidden** (§D6). A dotfile is not `Hidden` on NTFS (attribute `Archive`), an attribute-hidden file is. Legacy
  overloads return both; default `EnumerationOptions` skip only the attribute-hidden file; so the
  rule of the first bullet (legacy overloads or `AttributesToSkip = 0`) is right on both OSes, and a
  file with the `Hidden` attribute is listed like any other name (the module has no rule about
  that attribute).
- **Windows: the pre-filter is still a superset** (§D1-§D3, §D9). No pattern in the safe set returned less than the
  module would: `?` and `*.*` match more, not fewer, in Win32 mode; classes, `{`, `(`, `[` are literal and sent as
  `"*"`; case folds; 8.3 short names are never listing matches, so no name is missed through them.
  The 3-letter extension rule is absent on .NET 10 (more matches would only have been a superset).
  If a future runtime returned fewer names than the module for a safe-set pattern, the "superset
  pre-filter" rule of 6.4 would break; none was found.
- **Windows: Unicode** (§D7). NFC and NFD are two files on NTFS; the walker lists both, the matcher compares
  code units (D27).

## 10. Deprecation calendar, version, conformance corpus

### 10.1 Calendar

D22: `\` as a separator warns in 4.0 and is removed in 5.0.

| input | 3.x | 4.0 | 5.0 |
|---|---|---|---|
| `\` before `( ) [ ] { } \|` | separator | escape (tm 55) | escape |
| `\` before anything else | separator | separator, XP110 | escape for `* ? , ! ^ - \`; any other `\x` error XP009 |
| `\,` directly inside braces | separator | error XP017 | escape (a literal `,`) |
| named group in a mask | per use (5.2) | error XP008 | error XP008 |
| unclosed `(name:` in a string rule | runs to the end, silently | error XP006 | error XP006 |
| repeated group name | last value wins | error XP015 | error XP015 |
| `[c]` | per use (5.2) | `c`, XP105/XP106 | `c`, no warning (valid POSIX glob) |
| evidence warnings XP104, XP113, XP114, XP119 and parse warnings XP112, XP117 | — | warn | removed; the 4.0 meaning stays |
| XP120 (absolute exclude that removes nothing) | — | warn | warn (not a legacy warning) |
| the 3.x clauses in error reasons (7.2) | — | present | may be dropped |

**Measured on Windows (D22 rationale).** In 3.x on Windows `\` and `/` are interchangeable separators in
masks and rules (`measurements-windows.md` §W1, §W2): `!! "src\\*.cs"` lists `src\a.cs` and
`src\b.cs` exactly as `!! "src/*.cs"`, `Path.parse "src\\*.cs"` gives `[Directory "src"; FileMask
"*.cs"]`, the mask built with `"src" </> "*.cs"` lists the same files, and the rule `src\(name:*).cs`
matches both `src\a.cs` and `src/a.cs` with `name = a`. `Path.parse` and `matchGroups` replace `\` with `/`
unconditionally in the source (`Path.fs`), so the same result is expected on macOS; it was not measured
there. This is the calendar's premise: `</>` output on Windows contains `\`, and a `\` that stopped
being a separator would change `src\*.cs` silently. D22 is unchanged.

Paths read from disk keep being normalized (`\` → `/`) in every release (tm 56). `</>` is not
changed (tm 57). `unparse`/`escape`/`escapePath` move from `[*]`, `[?]`, `[\\]` to `\*`, `\?`,
`\\` in 5.0.

### 10.2 Version (brief f)

**4.0.0** (D22): the pattern module, the engine migration and the typed layer ship in the 4.0
line on `dev` (D1), through the integration branch `feature/patterns2` (D2), next to the
variable model. No 3.x release carries any of it. The hermetic package's Xake range must admit
4.0 (C8).

### 10.3 Conformance corpus (what pins behaviour)

D19: Phase 2 turns these into conformance vectors, each tagged **unchanged** (same result in 3.x
and 4.0) or **changed in 4.0** (the 4.0 result, the 3.x result, the expected error/warning code
and the release-notes entry, section 3). Changed vectors are rewritten, never deleted.

- **Engine pattern tests on `dev`**: `PathTests.fs` (all `MaskTests`, `MaskWithParent`, the
  group cases `(apl:*/*)`, `(path:bin/**)`, the root-relative `**/mlib.dll` cases — changed,
  item 12; lines 18–19 `c:\[(]*[)].c` / `c:\[(]*.c` change meaning under tm 55 and are rewritten
  as `c:/\(*\).c`-style vectors), `FilesetTests.fs` (dot-folder idiom, `dir/**` vs `dir/**/*` —
  changed, item 14; case per OS — changed, item 25; duplicates — rewritten, C5),
  `XakeScriptTests.fs` (group-in-rule cases incl. `(name:[(]*).ss`, `[(]*.ss` — unchanged with
  XP105; multi-target name generation), `RuleMatchTests.fs`, `RuleSpecificityTests.fs` (the
  `/`-next-to-`**` cases, item 33), `ParentDirTargetTests.fs`, `PatternAstTests.fs` (the regex
  byte-identity vectors become implementation tests of 9.2, not conformance vectors, D3).
- **Prototype tests** (frozen branch), ported: `EscapingTests.fs`, `EscapingDiskTests.fs`,
  `PatternAstTests.fs` — the include `[c]` cases stay as the prototype wrote them (they are the
  4.0 meaning) and gain XP106; warning level changed (C2); the 4.0 warnings added.
- **Samples and build scripts**: every pattern and mask string in `samples/*.fsx`
  (`samples/Research/*` excluded: experiments with their own regex code), `build.fsx`,
  `build.fsc.fsx`: e.g. `out/(cfg:*)/(fwk:*)/(name:*).dll`, `lock::(name:*)::(cfg:*)`,
  `test::(project:*)/(slice:*)`, `run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt`, `src/*/xake.project.json`,
  `-- "src/*tests/*.fsproj"`, `includes "a/**/"`, `paket-files/*.*` (item 15, XP114 on
  evidence; the sample should become `paket-files/*` in the same change).
- **Docs code blocks**: every fenced F# block in `docs/*.md` (e.g. `db:seed-(table:*)`,
  `signed/(name:*).(ext:dll|exe|nupkg)` — becomes `(ext:{dll,exe,nupkg})`,
  `out/pkg/%s{...}/...(ver:*).nupkg`).
- **Hermetic's direct uses**: `src/hermetic` and its tests use `Path.parse`,
  `Path.matchesPattern`, `Includes/Excludes` directly, the description masks `["**/*.cs",
  "!obj/**/*", "!bin/**/*", "!**/.*/**/*"]` (the `!` is hermetic's own exclude marker, stripped
  before Xake sees the mask), the `dir/**` → `dir/**/*` workaround (`Import.fs`, `Lock.fs`,
  `Locks.fs`; can go in 4.0), and `signed/(name:*).(ext:dll|exe|nupkg)` (`Sign.fs` docs,
  `SignTests.fs`): XP003 in 4.0, so it is replaced by `(ext:{dll,exe,nupkg})` in the same change
  that ships the module (C3).
- **Glob-style vectors** added in Phase 2 from the critique: the element table of
  `critique-glob.md` section 1, the class-mode cases (`[\-a]`, `[]a]`, `{[,],x}`, `[a-z-0]`),
  `//`, UNC roots, `\,` in braces, `escapePath @"C:\Program Files (x86)\Tool"`, and the D29
  errors and limits: `""` (XP018), `*/./x` (XP012), `{a/,b}` (XP019), `x/{a/,b}c` (valid), a
  symlinked directory under `**` (skipped, one Message), `??` against a non-BMP name (D26).

## 11. Worked examples

Root `/p` for rules; base dir `/p` for masks; Linux unless stated. Results are 4.0; "3.x:" notes
the old result where it differs.

1. **Rule with captures.** `"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> ...`, target
   `/p/out/Release/net8.0/Core.dll`. `getRuleMatches` = `cfg=Release, fwk=net8.0, name=Core,
   1=Release, 2=net8.0, 3=Core, 0=/p/out/Release/net8.0/Core.dll` (unchanged).
   `getRuleMatch "nme"` = `""`.

2. **Multi-output, named.** `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] *..> ...`,
   target `out/net8.0/Core.xml` → targets `out/net8.0/Core.dll`, `out/net8.0/Core.xml`
   (`formatMatches`). `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml"]` → XP013 when the
   rule is created (3.x: silently `out/net8.0/.xml`).

3. **Multi-output, positional.** `["dd/*.dll"; "dd/*.xml"]`, target `dd/a.dll` → `dd/a.xml`
   (pinned). `["(pat:**/*).dll"; "(pat:**/*)/main.xml"]`, target `dd/a.dll` → `dd/a/main.xml`
   (pinned).

4. **Include with dotfiles and pruning.** Tree: `a/b.cs`, `a/.git/x.cs`, `.hidden/y.cs`,
   `top.cs`. `!! "**/*.cs"` → all four (as 3.x; picomatch default would skip the dot ones).
   `!! "**/*.cs" -- "**/.*/**/*"` → `a/b.cs`, `top.cs`; the walker never lists `.git` or
   `.hidden` (pruned, 6.3). `-- "**/.*/**"` and `-- "**/.*/"` give the same (3.x: the first
   removed nothing, item 14; the second removed only files named like `.git`, item 23).

5. **Anchoring vs `.gitignore`.** `!! "**/*.rdl*" -- "*.rdlx"` removes only `.rdlx` files directly
   in the base dir. In `.gitignore`, `*.rdlx` (no `/`) matches at any depth; in Xake write
   `-- "**/*.rdlx"` (5.9).

6. **Escapes.** File `sub[1]/s.c`, no directory `sub1`. `!! @"sub\[1\]/*.c"` → it, no warning
   (same in 3.x). `!! "sub[1]/*.c"` → the directory `sub1`, so nothing here, and XP106 at parse:
   `'[1]' matches the single character '1' from Xake 4.0; Xake 3.x read the literal text '[1]';
   write \[1\] for the brackets, or 1 for the character` (3.x: `sub[1]/s.c`).

7. **Legacy rule escape.** `@"[(]*.ss" ..> ...`, target `(abc.ss` → matches, `1 = abc`, XP105
   `'[(]' is the literal '(' (as in Xake 3.x); write \( instead`. `@"\(*.ss"` → same match, no
   warning. `@"(name:[(]*).ss"` → `name = (abc` (pinned), XP105.

8. **Alternation migration.** `target "signed/(name:*).(ext:dll|exe|nupkg)"` → XP003 when the rule
   is created: `'(ext:dll|exe|nupkg)': '|' is reserved (Xake 3.x: a regex alternation); write
   (ext:{dll,exe,nupkg})` (3.x: worked). `target "signed/(name:*).(ext:{dll,exe,nupkg})"` → no
   warning, `getRuleMatch "ext"` = `dll`. Unescaped `a|b.txt` as a rule → XP003 (3.x: regex
   `^/p/a|b\.txt$`, any path starting with `/p/a` or ending in `b.txt`).

9. **Globstar, zero segments.** Rule `"**/*.dll"`, target `/p/a.dll` → match, `1 = ""`, `2 = a`,
   and XP119 once: `'**/' matched zero directories for '/p/a.dll'; in Xake 3.x '**/' in a rule
   needed at least one directory, so this rule did not match it; to require one or more
   directories write '*/**/' (with a capture: '(path:*/**)/')` (3.x: no match, pinned by
   `PathTests`). Rule `"a/**/b.txt"` formats with `1 = ""` to `a/b.txt`. Rule selection: with
   rules `*.dll` and `**/*.dll` both present, `a.dll` matches both and `*.dll` wins: both have
   4 literal characters (`.dll`; the `/` next to `**` counts 0, 5.8), and `*.dll` has no
   globstar. For `x/a.dll` only `**/*.dll` matches.

10. **`dir/**` in masks.** `!! "**/*.cs" -- "dir/**"` → removes everything below `dir`, XP113 once
    (3.x: removed nothing, pinned). `!! "src/hermetic/**"` → every file below it, XP113 (3.x: the
    directories below `src/hermetic` as `File` values). `!! "src/hermetic/**/"` → the
    directories (`src/hermetic` itself included), no warning.

11. **Backslash.** `!! @"src\*.cs"` → `src/*.cs`, XP110 (as 3.x, now deprecated; in 5.0 the file
    literally named `src*.cs`). `!! @"src\(x\)\*.cs"` → directory `src(x)`, files `*.cs`, XP110
    for the last `\` (tm 55, deliberate; 3.x: `src/(x/)/*.cs`, a different path). `!!
    @"src\(x)\*.cs"` → XP003 at the unescaped `)` (I1). To write a literal `*` in 4.0: `[*]`.

12. **`!` and `#` are literal.** `!! "!a.txt"` lists the file named `!a.txt`. In `.gitignore` a
    leading `!` re-includes; Xake uses `++`/`--` for that. `#a.txt` likewise.

13. **Repeated group.** Rule `"src/(p:*)/(p:*).fsproj"` → XP015 when the rule is created: `the
    group name 'p' appears 2 times; a name may appear once per pattern (Xake 3.x let each
    occurrence capture and the last one win); rename one occurrence, ...` (3.x: matched
    `src/Core/Other.fsproj` with `p = Other`). Write `src/(dir:*)/(p:*).fsproj` and compare
    `dir` and `p` in the recipe if they must be equal. `{(x:a)-1,(x:b)-2}` → XP015 too; write
    `(x:{a,b})-{1,2}` or two rules.

14. **`*.*`.** Files `README`, `a.txt`. `!! "*.*"` → `a.txt`, XP114 naming `README` (3.x: both).
    `!! "*" -- "*.*"` → `README` (unchanged: excludes always needed the dot).

15. **Case.** Files `Dir2/e.cs`, `dir/c.cs`; `!! "**/*.cs" -- "d*/**/*"`: Linux keeps `Dir2/e.cs`,
    Windows removes it (pinned, unchanged). `-- "Dir2/**/*"` on Linux removes only `Dir2/...`
    (3.x: also `dir2/...`, literal exclude segments ignored case). macOS `!! "*.CS"` does not find
    `a.cs` (3.x: found it, as the .NET listing does). No warning; section 3 item 25. Trap on
    default macOS: `!! "SRC/a.cs" -- "src/**"` keeps `SRC/a.cs` (the probe keeps the mask's
    spelling, the exclude compares ordinally; a known limit, 5.4, D25, C5).

16. **From a rule pattern to a mask.** `PatternAst.Rule.parse "src/(lib:*)/(name:*).cs" |>
    PatternAst.glob (function "lib" -> Some "core" | _ -> None) (fun _ -> None)` = `src/core/*.cs`;
    with the value `a(b` it is `src/a\(b/*.cs` (values escaped).

17. **A clear error instead of a crash.** Rule `"a(b.txt"` → `Invalid pattern 'a(b.txt' (rule) at
    character 2: '(' is reserved (Xake 3.x: a regex group); write \( for a literal '(' [XP003]`,
    raised when the rule is created; inside a build reported as a `XakeException` with that
    message (3.x: `RegexParseException`).

18. **Named group in a mask.** `!! "out/(cfg:*)/*.dll"` → `Invalid pattern 'out/(cfg:*)/*.dll'
    (mask) at character 5: named groups '(name:...)' exist only in rule patterns ... [XP008]`
    (3.x: looked for a directory literally named `(cfg:*)` with a wildcard segment and found
    nothing). Write `out/*/*.dll`. `-- "(cfg:Debug)/**/*"` → XP008 too (3.x: the literal
    directory `(cfg:Debug)`); write `Debug/**/*`.

19. **A real path with parentheses** (S3, D7). Windows, `toolDir = @"C:\Program Files
    (x86)\Tool"`. `!! (toolDir </> "*.dll")` → XP003 at the unescaped `(` (3.x: read the
    parentheses literally in a mask and worked). `!! (PatternAst.escapePath toolDir + "/*.dll")`
    = `!! @"C:/Program Files \(x86\)/Tool/*.dll"` → the DLLs, no warning.
    `PatternAst.escape toolDir` would be wrong here: it keeps every `\` as a literal backslash
    (`[\\]`), not a separator.

20. **One or more directories** (D12). Tree `file.o`, `a/file.o`, `a/b/file.o`. Rule
    `"**/file.o"` matches all three (3.x: the last two; XP119 for `file.o`). Rule
    `"*/**/file.o"` matches `a/file.o`, `a/b/file.o` (the 3.x meaning of `**/file.o`), and
    `"(path:*/**)/file.o"` gives `path = a`, `a/b`. The same works in masks: `!! "*/**/file.o"`.

21. **Directory exclude** (D9). Tree `obj/x.dll`, `obj/a/y.cs`, `src/obj` (a file), `src/a.cs`.
    `!! "**/*" -- "obj/"` → `src/obj`, `src/a.cs`; the walker does not enter `obj` (3.x: the
    slash was ignored, `obj` removed nothing since `obj` is a directory; item 23).
    `!! "**/*" -- "**/obj/"` also keeps `src/obj` (a file, not a directory).

22. **Absolute exclude** (D6). Base dir `/p`. `!! "**/*.dll" -- "/bin/**"` → removes nothing
    (`/bin` is the file-system root's `bin`) and warns XP120 when the fileset is evaluated; write
    `-- "bin/**"`. `!! "/opt/lib/*.so" -- "/opt/lib/libx.so"` → no warning (the exclude lies
    below the anchor of an earlier include).

23. **Edge errors and limits** (D26, D27, D29). `!! ""` and rule `""` → XP018. `!! "src/*/./a.cs"`
    → XP012 at the `.` (write `src/*/a.cs`). `!! "src/{a/,b}"` → XP019 (write `!! "src/a/" ++
    "src/b"`, or `src/{a,b}/` if `b` is a directory too); `!! "src/{a/,b}x.cs"` is valid
    (`src/a/x.cs` or `src/bx.cs`). A file `😀.txt` (one character, two UTF-16 code units):
    `!! "?.txt"` does not find it, `!! "??.txt"` does. On macOS a file `café.txt` stored in NFD:
    `!! "café.txt"` (NFC literal) finds it through the probe, spelled as in the mask; `!!
    "café*"` does not (the wildcard segment compares code units); `!! "caf*"` does.

## 12. Open items (brief "Open for design" 1–15, C11): decisions

Each entry records the decision (brief, "Decisions from the spec review") in one line, the
alternatives considered, and the spec sections that implement it. Decisions are not reopened
here.

### 1. Exact EBNF of masks and rule patterns

**Decision (D8, 2026-11-01).** One grammar, two contexts (a mask is a rule pattern without named
groups); `(name:` is one lexical token; `{x}` without a top-level comma is an error.

Alternatives considered:
- Two grammars (mask, rule) sharing a lexer: simpler to read separately, but the brief asks for
  one grammar with rules as a superset (brief a), and the include/exclude asymmetry came exactly
  from separate encodings.
- Treat `(name:` syntactically (`(` then name then `:`): equivalent, but a lexical token makes
  `(a b:*)` and `(:*)` unambiguous "not a group" without backtracking in the parser.
- Accept `{x}` as a literal (picomatch, and 3.x): friendlier, but closes no syntax and keeps
  `{1..3}` ambiguous later; rejected (XP005, whose text names the 3.x literal reading).
- Make `\` a full escape already in 4.0 (`\*` = literal `*`): see item 15.

Depends: 4 (all), 7.2 (XP002–XP019), 8.2 (`Rule.tryParse`, `Mask.tryParse`, `Token`).

### 2. Semantics table: rule vs mask, per element

**Decision (D9, 2026-11-01).** One meaning per element, two contexts (`Rule`, `Mask`), one parser
with a context argument and two entry points; a trailing `/` means "a directory" in every mask;
include vs exclude differ only in the scanner.

Alternatives considered:
- A Compat level that keeps the 3.x meanings by default (the 2026-10-26 draft): superseded by
  D1; it kept two semantics alive for a whole release line.
- Three contexts (rule, include, exclude) with per-role meanings (the 2026-10-31 draft: a
  trailing `/` meant "directories" in includes and "everything below" in excludes): rejected,
  the difference is a consequence of the scanner, not a second meaning.
- Two independent parsers: the 3.x drift came from separate encodings.
- Parse, then validate per context: errors should arise where the problem is (the lexer knows
  there is no `(name:` in a mask).
- Switch silently (no warnings, release notes only): against brief (b)'s break-softening
  principle for `[...]`, `**/` in rules and `dir/**` in masks.

Depends: 4 (contexts), 5.1, 5.2, 6.2, 7.3 (roles in warnings), 8.2, section 3 item 23.

### 3. Dotfiles

**Decision (D10, 2026-11-01).** A leading `.` is not special: `*`, `?`, `**` and classes match
it; hidden folders are excluded explicitly and pruned by the walker.

Alternatives considered:
- picomatch / bash style (`*` skips a leading `.` unless the pattern starts with `.`): what
  JavaScript users expect, and it would hide `.git` by default; but it silently drops files that
  `**/*.cs` finds in 3.x (`.hidden/y.cs` in `FilesetTests`), in rules as well as masks.
- A fileset option `dot = false` (opt-in picomatch behaviour, or Ant-style default excludes):
  possible later without grammar change; not part of the module.

Depends: 5.3, 6.3 (pruning example), 11 example 4.

### 4. Case sensitivity per OS; case of classes

**Decision (D11, 2026-11-01).** One comparer everywhere: ordinal on Linux/macOS, ignore-case on
Windows; classes follow it; the two 3.x quirks are release-note entries without a warning.

Alternatives considered:
- macOS insensitive everywhere (matches the default APFS volume): friendlier on macOS, but
  rules and `File` equality are ordinal there, so a target and its rule could disagree;
  changing `File` equality is out of scope.
- An evidence warning for a segment that accepts a name only when case is ignored: exact, but
  it needs a second, case-insensitive comparison on every rejected name, which is most names in
  a scan; not worth it for two narrow cases.
- A per-fileset / per-script option `caseSensitive`: flexible, but every cache key and the
  rule matcher would carry it; can be added later as an `ExecOptions` field.
- Detect the file system's sensitivity per directory: correct but slow and surprising across
  machines (a build behaves differently on a case-sensitive APFS volume).

Depends: 5.4, 9.2 (regex options), 9.3 (C4), 9.4 (cache key), section 3 item 25, 11 example 15.

### 5. `**` edge cases

**Decision (D12, 2026-11-01).** `**` is zero or more whole segments everywhere; captures have no
edge `/`; `a/**` excludes `a`; `a**b` = `a*b` (XP112); XP119 and XP113 on evidence; the
"one or more" idiom `*/**/file.o` / `(path:*/**)/file.o` is documented.

Alternatives considered:
- Warn at parse time on every `**/` in a rule: catches every affected rule before a build, but
  `**/` is the ordinary 4.0 spelling and nearly every rule set has one; the warning would be
  noise for new code until 5.0. The evidence warning fires exactly when the widening mattered.
- Keep 1+ segments for rules (a rule-only exception): no rule-selection change, but it
  contradicts brief (c) and the mainstream reading (bash `globstar`, `.gitignore`, picomatch,
  `globset`; Java's `PathMatcher` is the exception, its `**/b` does not match `b`).
- Segment-local `**` as an error (globstar must be a whole segment, as old `globset` did):
  stricter; picomatch and `.gitignore` read it as `*`; XP112 already tells the user to write `*`.
- `a/**` matching `a` itself too (some globbers): useless for files and inconsistent with
  `.gitignore`.

Depends: 4.3, 5.1 (idiom), 5.2, 5.5, 5.7 (format), 5.8 (specificity, D5), 6.3, 7.3
(XP112/XP113/XP119), 9.3 (M1), 11 examples 9, 10, 20; hermetic's `dir/**` → `dir/**/*`
workaround can go in 4.0.

### 6. What an include segment without wildcards means

**Decision (D13, 2026-11-01).** The explicit-name rule stays (a mask with no non-literal element
is listed even if absent); literal prefixes are probed; braces are matched against listings;
document the rule in `docs/overview.md` (Phase 5) and revisit it after 4.0.

Alternatives considered:
- Literal segments matched against a listing (the disk's spelling in the result): uniform with
  wildcard segments and case-exact, but lists every directory on the way (slow for deep literal
  prefixes) and changes the spelling of results on case-insensitive file systems.
- Explicit names filtered by existence too: simpler mental model ("a fileset lists what
  exists"), but breaks `need`-ing a file that a rule will build through a fileset (hermetic
  `Locks.fs`, `Csc`/`Fsc` `Ref`; `explicit-name-history.md`).
- Bash-style brace expansion into explicit names (`src/{a,b}.cs` lists both even if absent):
  handy for `need`, but braces would then be literal-like in one role only.

Depends: 6.2, 6.3 steps 1–2, 11 example 4.

### 7. Class and brace rollout timing; the reserved-character signal

**Decision (D14, 2026-11-01).** Classes and braces ship in 4.0 in every context; legacy readings
signalled by XP104 (evidence), XP105/XP106 (parse), XP117, XP005 and XP003.

Alternatives considered:
- Warn at parse time on every class or brace in a mask: catches every legacy mask, but punishes
  new 4.0 code that uses classes on purpose, until 5.0.
- Classes and braces in 5.0 only (3.x readings kept through 4.x with warnings): the brief's
  original rollout (tm 52/60), written for a minor release; with 4.0 allowed to break, it only
  delays mainstream glob by a major and keeps the per-context code.
- Braces only inside named groups (where `|` is used in 3.x): minimal change, but an odd rule
  to document.

Depends: 5.1, 5.2 (rows `[...]`, `[c]`, `{a,b}`), 7.3, section 3 items 6–11, 11 examples 6–8.

### 8. Error vs warning model

**Decision (D15, 2026-11-01).** `PatternException of string` at parse time with the message
`Invalid pattern '<text>' (<context>) at character <n>: <reason> [XPnnn]`, `tryParse` returns
`PatternError`, the engine rethrows as `XakeException`; warnings for legacy input only, level
Warning, codes XP1nn, keyed by context and pattern text, at parse or on evidence, buffered,
removed in 5.0.

Alternatives considered:
- Errors as `XakeException` directly: one exception less, but the typed layer and scripts that
  load patterns outside a build would see a build-error type for a syntax error; tm 64 already
  chose `PatternException`.
- Parse-time warnings only (no evidence checks): simpler and all warnings appear at load, but
  either the ordinary 4.0 spellings (`**/`, `[ab]`, `dir/**`, `*.*`) warn for everyone until
  5.0, or their changes go unsignalled.
- Errors for every changed meaning (no warnings at all): loud and simple, but rejects valid,
  mainstream 4.0 spellings.
- Warning key = pattern text only (prototype): one warning even if the same text is a rule and a
  mask, but the 3.x meaning named in the text depends on the use, so one of them would be
  wrong.
- Warnings at level Message (prototype): less noise, but users filtering on warnings would miss
  the migration hints.
- No codes: shorter messages; codes cost little and let docs link from a warning to its fix.

Depends: 7 (all; the warning key uses the mask role, 7.3), 8.2 (`PatternException`,
`PatternError`, `check`), 8.3, 6.3 step 5, 11 example 17.

### 9. Minimal public API, name, namespace; relation to the public `PatternAst`

**Decision (D16, 2026-11-01).** Public module `Xake.PatternAst` (own file, not auto-opened): AST
types, `PatternException`/`PatternError`, `Rule.parse`/`Mask.parse` (+ `tryParse`, `check`),
`unparse`/`escape`/`escapePath`, `groupNames`/`wildcards`/`specificity`,
`format`/`formatMatches`/`glob`; matcher and regex internal; `Path`/`Fileset` signatures and
`Includes of PathMask` unchanged.

Alternatives considered:
- A new name (`Xake.Pattern`, `Xake.Glob`): reads better, but tm 13/64, the prototype tests and
  the docs already use `PatternAst`; the typed layer has an internal `Pattern` type.
- A public compiled-pattern type (`Pattern.compile : PatternContext -> string -> Matcher` with
  `IsMatch`/`Match`): useful for library authors (hermetic), but it fixes the matcher's shape
  before Phase 3 has measured anything; can be added in a 4.x minor without breaking.
- Make `toRegex` public (prototype): exposes the backend and blocks an own matcher later.
- Change `FilesetElement` to carry the mask text now: 4.0 allows the break and it is cleaner,
  but it moves the DB version and breaks hermetic's direct `Includes (Path.parse m)` code for no
  user-visible gain; possible in 5.0.

Depends: 8 (all), 6.1, 10.2.

### 10. How `Fileset` scanning uses the matcher

**Decision (D17, 2026-11-01).** Depth-first walker driven by the AST (probes, listings as a
superset pre-filter, `**` recursion, brace union, pruning, ordinal order, dedup, unreadable
directories skipped with a warning, evidence checks); symlinks not followed under `**`;
links detected by attribute and the OS-listing quirks (C10) measured on macOS in Phase 2
(`measurements-dotnet.md`); junctions/mount points/cloud placeholders (S5) and the Windows listing
semantics stay unverified (6.4).

Alternatives considered:
- Keep `SearchOption.AllDirectories` for `**` and filter afterwards (3.x): simple, but lists
  `.git`/`node_modules` in full before any exclude applies (the G8 cost), and follows symlink
  cycles.
- Match every file's full path against one whole-pattern regex after a full recursive listing:
  one code path, but no pruning and no early stop for literal prefixes.
- Follow symlinks with cycle detection: needs the link target (`LinkTarget`, .NET 6+) or
  P/Invoke; Core targets netstandard2.0/net462.
- Keep enumeration order (no sort): no cost, but non-deterministic across file systems, which
  matters for F# compile order.
- Keep duplicates: 3.x (tested) behaviour; but a file twice in `Csc`/`Fsc` inputs is an error
  or warning in the compiler (the C5 test is rewritten).

Depends: 6 (all), 9.4 (fast paths), section 3 items 26–28.

### 11. Performance: regex compile cache vs an own matcher

**Decision (D18, 2026-11-01).** Phase-3 starting point, an implementation detail per D3: regex
from the AST, cached by (context, text, anchor, case), `Compiled` for rule/exclude patterns,
interpreted for walker/one-off matchers, fast paths for literal, `*`, `*.ext`; measured with the
`FilesetTests` benchmark.

Alternatives considered:
- An own matcher (two-pointer glob with backtracking and captures): no compile cost and no
  ReDoS, but must re-create .NET's greedy capture values and case rules exactly (brief e:
  `getRuleMatch` unchanged), and needs its own test corpus.
- Regex without cache: the pre-G8 behaviour, ~1 ms per mask and file; ruled out.
- Always `RegexOptions.Compiled`: fastest matching, but ~1 ms per distinct pattern adds up when
  scripts generate many masks (typed `Glob` in a loop).
- Non-backtracking regex (`RegexOptions.NonBacktracking`): .NET 7+ only; Core targets
  netstandard2.0.

Depends: 9 (all), 6.4, 8.2 (nothing regex-shaped is public).

### 12. Conformance corpus

**Decision (D19, 2026-11-01).** The engine pattern tests, the prototype's escaping and AST tests,
every pattern in `samples/*.fsx` (not `samples/Research`), `build.fsx`, `build.fsc.fsx`, fenced
F# blocks in `docs/*.md` and hermetic's direct uses; vectors tagged unchanged / changed-in-4.0,
changed vectors rewritten, never deleted.

Alternatives considered:
- Only the unit tests: cheapest, but the docs and samples are what users copy; the `|`-in-group
  case lives only in hermetic docs/tests.
- Also scan public GitHub scripts using Xake: broader, but out of reach for a conformance suite
  and not reproducible.
- Pin `samples/Research` too: those files carry their own regex experiments
  (`a?/(TAG:*.*)/*.txt/**/+`), not supported usage.

Depends: 10.3, Phase 2 vector format, section 3.

### 13. Migration of `[(]` and `sub[1]`

**Decision (D20, 2026-11-01).** `[c]` is the character `c` everywhere; XP105 at parse where 3.x
meant `c` too (suggests `\(` or plain `c`), XP106 at parse where 3.x read the literal text
(suggests `\[1\]` or `1`); both stop in 5.0.

Alternatives considered:
- XP106 on evidence only (a scan meets an entry literally named `sub[1]`): quieter, but `[c]` in
  a mask is almost never 4.0-intended (`1` is shorter), so parse time costs nothing and catches
  masks whose directories do not exist on this machine.
- An error for `[c]` in masks in 4.0: forces the fix, but `[c]` is legal glob and the prototype
  tests already use it with the 4.0 meaning.
- Auto-detect (in an include, use the literal `sub[1]` if it exists, else `sub1`): no surprise
  for either reading, but the result depends on the tree, which breaks determinism.

Depends: 5.2 rows `[c]`/`[...]`, 7.3 XP104–XP106, 8.2 (`Mask.check` takes the role), 10.3
(prototype tests), 11 examples 6–7.

### 14. Named group details

**Decision (D21 with D4, 2026-11-01).** Names: Unicode letter or `_`, then letters/digits/`_`
(leading digit XP007); body: any element; a repeated name is an error (XP015); multi-output: the
same name set in every output (XP013), positional pairing of bare wildcards stays; positional
numbers, `"0"` and greedy values as in 3.x.

Alternatives considered:
- A repeated name as a backreference, every occurrence matching the same text (the 2026-10-31
  draft, with an evidence warning): backreference semantics were never intended (D4), and the
  natural spelling `{(x:a)-1,(x:b)-2}` could then never match its second alternative.
- Repeated names as independent captures (3.x): keeps the "last wins" surprise; `format` would
  have one value for two possibly different texts.
- A leading digit as a warning in 4.0 (3.x meaning kept): softer, but the collision with the
  positional key is a latent bug in any script that has it.
- ASCII-only names: matches .NET regex group-name rules more tightly, but F# records allow
  Unicode field names and the typed layer maps fields to groups.
- Forbid `/` or globstars inside groups: simpler walker, but `(apl:*/*)` and `(path:bin/**)` are
  pinned by `PathTests`.

Depends: 4.1 (`name`), 4.2 (`group`, repeated names), 5.6, 5.7, 7.2 XP006/XP007/XP013/XP015,
11 example 13.

### 15. Version number (brief f) and the `\` separator calendar

**Decision (D22, 2026-11-01).** Version 4.0.0; `\` as a separator warns in 4.0 (XP110 at parse)
and is removed in 5.0 (then `\` escapes `* ? , ! ^ - \`, any other `\x` is XP009); in 4.0 `\`
before a reserved character is an escape, and literal `*`, `?`, `,`, `\` are one-character
classes.

Reasons kept for the record: brief (b) tm 56 already says "still works in this release, warns;
a later major makes `\` an escape only"; masks built with `</>` on Windows contain `\` (tm 57),
so removing it in 4.0 would turn `dir </> "*.cs"` into the literal file `dir*.cs`, a silent
empty result; and the warning is exact (parse time, no noise for new code that writes `/`).

Alternatives considered:
- Remove in 4.0 (`\` escapes `* ? , ! ^ - \`, any other `\x` is XP009): one major sooner, a
  backslash escape for `*`/`?` right away (4.0 needs `[*]`/`[?]`), and no deprecated spelling in
  the grammar; but every Windows script that joins masks with `</>` breaks, partly silently
  (`\*`, `\?` would become literals), and tm 56 would have to be reopened.
- Remove in 4.0 but keep `\` as a separator when the pattern contains no `/` at all (a heuristic
  for `</>`-built masks): fewer breaks, but two meanings of `\` decided by the rest of the text.
- In 5.0 make `\x` = `x` for any character (picomatch): `src\x.cs` would silently become
  `srcx.cs`; the error is safer.

Depends: 2, 4.1 (`legacy_sep`, XP017), 5.7 (`escape`/`escapePath`/`unparse`), 7.3 XP110, 10.1,
10.2.

### C11. Named group in a mask, unclosed group in a string rule

**Decision (D23, 2026-11-01).** Option B: both are errors in 4.0 (XP008, XP006); tm 58 and tm 61
are superseded; XP111 is dropped; consistent with D9 (the mask lexer has no `(name:` token).

Alternatives considered:
- Option A (the brief as written for a minor release): XP111 at parse and the group matched as
  its body in 4.0, error XP008 in 5.0; an unclosed `(name:` in a string rule silent in 4.0,
  XP006 in 5.0. Softer, but keeps one more warning and one more legacy grammar path, and string
  and typed rules would disagree.

Depends: 4.2, 5.1, 5.2, 7.2 (XP006, XP008), 10.1, section 3 items 17 and 20, 11 example 18.

### Derived choices and resolved suggestions (D24–D29)

**Derived choices (D24, 2026-11-01)**, made while applying D3–D23 and now decided: `(` in a mask
is XP008 when followed by `name:`, XP003 otherwise (4.2); `MaskRole = Include | Exclude` on
`Mask.check`, so that XP105/XP106 and the warning key know the role, while errors print
`(rule|mask)` (7.2, 7.3, 8.2); XP120 is not a legacy warning and stays after 5.0 (7.1, 10.1); D5
can change rule selection (section 3 item 33); repeated separators collapse, UNC roots parse on
every OS and match only on Windows (4.2, item 34); `\,` directly inside braces is XP017 (4.1).

**Resolved suggestions** of the glob-conformance review (previously deferred), one line each:

1. C2, `?` and classes match one UTF-16 code unit, not a scalar value: kept and documented
   (D26; 5.1, 5.4, 5.9, example 23). A scalar-value matcher can come later without breaking any
   mask that has no surrogate pairs.
2. C3, Unicode normalization on macOS: not done, a known limit (D27; 5.4, example 23).
3. C5, disk spelling for probed literal segments: not taken; a probed segment keeps the mask's
   spelling, the trap is a known limit, and the scan result depends on the mask text and the
   file list only (D25; 5.4, 6.2, example 15). A disk-spelling option may come later.
4. C6, reserving `*(` and `?(` for extglob: not reserved; `(name:` is a group wherever it
   appears, and a future extglob may use only `@(`, `+(`, `!(` (D28; 4.2).
5. C8, the empty pattern `""`: an error in both contexts (D29; XP018, 4.2, item 35).
6. C8, `.` as a segment after a wildcard: an error, as `..` (D29; XP012, 4.2, item 21).
7. S5, a directory skipped by `**` because it is a link: logged once at Message level (D29;
   6.5, item 26).
8. S5, detecting links through `LinkTarget`: not available on Core's targets; links are detected by
   `FileAttributes.ReparsePoint`, measured on macOS (D29; 6.5, 9.5).
9. `{a/,b}` as the last element: an error; `{a,b}/` covers the honest case (D29; XP019, 4.2,
   item 36).

## 13. Conflicts and findings (record)

Each finding is resolved by a decision or by a Phase-2 measurement (macOS and Windows, 2026-11-01);
only the items listed under "Still unmeasured" in 6.4 stay open.

- **C1 — `[c]` in includes is a plain 4.0 change.** #90 already reads `[c]` as `c` in includes
  (`sub[1]/*.c` finds `sub1`, test "an unescaped sub[1] is the [c] form"); 3.x reads the literal
  text. Under D1 this is the 4.0 meaning, announced by XP106 at parse and section 3 item 9; the
  prototype's tests are ported as they are, plus the warning (D20).
- **C2 — resolved (D15): warning level.** The prototype attaches pattern warnings at
  `Level.Message` (`ExecCore.fs:388` on the frozen branch); 4.0 uses `Level.Warning`.
- **C3 — resolved: braces ship in 4.0** (D14). `(ext:dll|exe|nupkg)` is XP003 in 4.0, so
  hermetic (`Sign.fs` docs, `SignTests.fs`) and `docs/*.md` switch to `(ext:{dll,exe,nupkg})` in
  the same change that ships the module.
- **C4 — resolved (D12): rules vs masks on `**`.** In 3.x a rule's `**/` needs at least one
  directory (`PathTests`: `**/mlib.dll` does not match `c:/mlib.dll` with root `c:/`); masks
  accept zero. One matcher makes rules accept zero too; signalled by XP119 when a rule match
  takes zero directories, by section 3 item 12, and the `*/**/` idiom keeps the 3.x meaning.
  With D5 the rule-selection effect is limited: `*.dll` still beats `**/*.dll` (example 9).
  Measured: in 3.x only `matchGroups` (the rule matcher) needs the directory; `Path.matches` and the
  scan accept zero (`measurements-engine.md` §7).
- **C5 — a test whose title and assertion disagree.** `FilesetTests` "exec function removes
  duplicates from result" asserts the duplicate count is 2. The spec removes duplicates (6.6);
  the test is rewritten (D17).
- **C6 — `PatternAst` visibility on `dev`.** It is `internal` inside `Path.fs` today; tm 13 makes
  it public; the prototype moved it to its own file. The spec follows the prototype (8.1, D16).
- **C7 — the `"0"` key.** `getRuleMatches` contains `"0"` (the whole match, from
  `Regex.GetGroupNames`). Not documented, not tested, but "unchanged" (brief e) means it stays
  (D21); an own matcher would have to produce it.
- **C8 — hermetic depends on mask internals.** `Path.parse`, `Path.matchesPattern`,
  `Includes/Excludes of PathMask` and the `dir/**` → `dir/**/*` workaround (`Import.fs`,
  `Lock.fs`, `Locks.fs`). The spec keeps those public shapes in 4.0 (6.1, D16); the
  `Xake.Hermetic.Dotnet` Xake range has to admit 4.0 (release-process decision).
- **C9 — observed, out of scope.** `File.fs` `compareNames` passes `ignoreCase = Env.isUnix`
  (case-insensitive on Unix, sensitive on Windows), the inverse of every other comparer; worth a
  separate issue.
- **C10 — measured on macOS and Windows (D17): OS listing quirks.** Both OSes: the legacy
  overloads match `*.*` to a dotless name and a trailing `?` to zero characters; `[ ( {` are literal;
  the search pattern is case-insensitive by platform default (`CaseSensitive` is exact);
  `GetFiles` returns the on-disk spelling; links are detected by `ReparsePoint`. Differences:
  `"` `<` `>` are DOS wildcards on Windows and literal on macOS; macOS follows symlinks and stops a
  cycle at ELOOP (depth 32), Windows follows junctions and a junction cycle runs on (10 000 entries,
  depth 5001); default `EnumerationOptions` skip dotfiles on macOS but attribute-hidden files on
  Windows; NFC/NFD are one file on APFS, two on NTFS; NTFS lists in name order, APFS in `readdir`
  order (`measurements-dotnet.md` §1–6, `measurements-windows.md`, 6.4). The 3-letter extension rule
  was not reproduced on .NET 10 (unmeasured on .NET Framework).
- **S5 — measured on macOS (D17): what counts as a link.** Symlinks carry `ReparsePoint` and are
  detected by attribute, since netstandard2.0/net462 lack `LinkTarget` (6.5, 9.5). Windows
  junctions carry `Directory | ReparsePoint` and are followed by the 3.x scan, so the same attribute
  test skips them (`measurements-windows.md` §D4b, §D4d); a junction cycle is not stopped by the OS.
  Windows symlinks, volume mount points and cloud-sync placeholders are unmeasured (6.4).
- **C11 — resolved (D23):** a named group in a mask (XP008) and an unclosed group in a string
  rule (XP006) are errors in 4.0; XP111 dropped (12, C11).
- **C12 — a 3.x bug, recorded (not a decision).** `Path.matches "(apl:*/*)/x"` throws
  `RegexParseException` (`^([^/\\]*))$`, too many `)`), while `matchGroups` captures
  `apl = "a/b"`: a named group whose body holds `/` breaks one of the two matchers. In 4.0 there is
  one matcher (9.2); whether a group body may hold `/` is decided by 4.2, not by this record
  (`measurements-engine.md` §10).
- **C13 — 3.x has several matchers that disagree, recorded.** `Path.matches`, `matchGroups` and the
  scan differ on `a//b`, `.`, `..`, `**/x.dll`, `(path:bin/**)/file.o` and case on macOS
  (`measurements-engine.md` §1, §3, §7, §9, §10). The conformance vectors tag each such case with
  the matcher that produced the 3.x result.
- **C14 — 3.x engine results on macOS and Windows compared (record).** Sources: `measurements-engine.md`
  (macOS, `Path`/`Fileset` of 3.x) against `measurements-windows.md` §E1–§E10 (Windows, Xake 3.15.0 from nuget;
  the macOS run of `measure-windows.fsx` itself is not recorded). Windows prints `\` in scan results; every
  comparison below normalizes it to `/`.
  - **Agree (one 3.x result, pin it in the vectors):** E1 `a//b` (parse collapses, `matchGroups` raw text),
    E3 `.` dropped and `..` a `Parent` token, with `Path.matches`/scan disagreeing, E5 duplicates kept, E6
    trailing `/` in an exclude, E7 `**` zero directories in `matches` and scan but not in `matchGroups`,
    `dir/**` lists directories, E8 dotfiles not skipped, E10 `matchGroups` captures, `matches`
    throws `RegexParseException` for `(apl:*/*)/x` (same message).
  - **Differ by OS:**
    - E2 `!! ""` lists `""` on macOS and `"."` on Windows. The harness prints Windows paths with
      `Path.GetRelativePath(root, full)`, which returns `"."` for the root itself, so this is probably the
      harness, not the engine; both stated, the engine fact is "the base directory itself, one entry".
      The vector pins the entry as the base directory and does not pin its printed spelling.
    - E9 `matchGroups "src/*.cs"` against `Src/a.cs`: **None on macOS**, **a match on Windows**
      (`matchGroups` is case-sensitive only when `Env.isUnix`, Path.fs:264); `Path.matches` is true on
      both. The vector pins the `Path.matches` and scan results as OS-independent and carries the
      `matchGroups` result with an OS tag (4.0 makes one matcher per-OS, 5.4).
    - E4 symlinks: macOS follows a directory symlink under `**`/`*/` and lists a file symlink; Windows
      measured a junction (followed under `**` and `*/`, `jct\x.cs`) and could not create symlinks. Cycle:
      macOS terminates (engine run: 16 entries, deepest 17 `loop/` segments; .NET level: 95 entries, depth 33,
      ELOOP at 32 links); Windows created no cycle in the engine run (E4loop: 1 entry, no symlink) and
      at the .NET level a junction cycle does not terminate (10 000 entries, depth 5001). The two
      macOS numbers differ because the engine run was capped by its own recursion, the .NET run by
      ELOOP; neither is a conformance fact. The vectors pin only that the 4.0 scan skips a link under
      `**` and terminates (6.5), with 3.x results as OS-tagged notes.
    - E9 scan spelling: both return the spelling of the mask (`src\A.cs` for `src/*.cs` on `Src/A.cs`);
      agree.
  - **`\` (W1, W2, Windows):** in 3.x `\` is a separator in masks (`src\*.cs` = `src/*.cs`), rules
    (`src\(name:*).cs` matches `src\a.cs` and `src/a.cs`, groups reported with `/`) and `</>`-built masks
    (5.2, 10.1). macOS: not measured, same by source.
  - **For the vectors:** pin the OS-independent column; mark with an OS tag `[windows]` or `[macos]` only
    E4 (links), the E2 spelling and the E9 `matchGroups` case result.
- **C15 — a 3.x scan on Windows lists junction targets twice (record).** `**/*.cs` on `real/x.cs` plus a
  junction `jct -> real` lists `jct\x.cs` and `real\x.cs`; in 4.0 `**` skips `jct` (D17) and lists only
  `real/x.cs`. Section 3 item 26 already names junctions, so its wording is confirmed by the measurement
  (`measurements-windows.md` §E4).
- **C16 — default `EnumerationOptions` differ by OS (record).** The default `AttributesToSkip`
  (`Hidden | System`) drops dotfiles on macOS and attribute-hidden files on Windows; a dotfile is listed on
  Windows. Walker rule (9.5): never rely on them. Decided (D33): a file with the Windows `Hidden` attribute
  is listed like any other name; attributes are not pattern semantics (5.3).
