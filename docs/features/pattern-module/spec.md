# Spec: the pattern module (draft for review)

> Revision 2026-10-31: decision D1 applied (brief, "Decisions from the spec review"): Standard semantics only, shipped in Xake 4.0.
> The Compat level, `XAKE_PATTERNS` and the 3.13 release are removed; legacy inputs warn or fail naming their 3.x meaning (sections 2, 3, 7).

Status: **draft**, written 2026-10-26 from `brief.md` (sections a–f are decisions and are not
reopened here), the frozen prototype `feature/typed-matches` (`src/core/{PatternAst,Path,Fileset}.fs`,
`src/tests/{EscapingTests,EscapingDiskTests,PatternAstTests}.fs`, `escaping-today.md`) and the
engine on `dev` (`src/core/Path.fs`, `Fileset.fs`, `ExecCore.fs`); revised 2026-10-31 for D1. The
15 "Open for design" items of the brief are answered in section 12, each with a recommendation
and the alternatives; the body of the spec is written as if every recommendation were accepted,
and each open item lists which body sections change if the user decides otherwise.

Conventions: "rule pattern" = the text of `..>`, `*..>`, `=>`, `target`, `targets`, `command`,
`phony` and of the typed layer. "Mask" = the text of `!!`, `ls`, `++`, `--`, `+`, `-`,
`fileset { includes / excludes / includefile / excludefile }`, `cp`/`rm` masks. "Include" and
"exclude" are the two mask roles. "3.x" = the engine as released up to 3.12/3.15 (pattern code
unchanged since `d002fe4`). "4.0" = what this spec defines. "Prototype" = the frozen branch
(#90/#91).

---

## 1. Scope and goals

In scope:

- One grammar for rule patterns and masks (masks are the subset without named groups).
- One parser producing one public AST (`Xake.PatternAst`), and one matcher derived from it
  that serves rules, includes and excludes (brief c).
- Generation from the AST: `format` (replaces `ExecCore.applyWildcards`), `glob` (rule pattern
  to mask), `unparse`, `escape`.
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
   (section 11 lists the differences).
3. **One implementation**: the three encodings that exist in 3.x (`PatternAst` regex for rules
   and wildcard exclude segments, `Path.parse` + OS listing for includes, the `applyWildcards`
   regexes for multi-file names) collapse into one AST and one matcher, with one meaning per
   element in every context.
4. `getRuleMatch` / `getRuleMatches` keep their interface, and return the same values as in 3.x
   for every pattern that is valid in both 3.x and 4.0 and every target both match (brief e).

## 2. Semantics and migration

The module has **one semantics**, the one of section 5, and it ships directly in **Xake 4.0**
(D1). There is no Compat level, no `XAKE_PATTERNS` switch and no intermediate 3.x release of the
module; `dev` is already the 4.0 line (the variable-model PRs #111–#115), so breaking changes are
allowed.

What softens the break:

- **Errors that teach.** Inputs that 4.0 rejects (`( ) |` outside a named group, `{x}`, a group
  name starting with a digit, `..` after a wildcard, ...) fail with a `PatternException` whose
  reason names what the spelling meant in 3.x and what to write instead (7.2).
- **Warnings on legacy inputs.** Inputs that 4.0 accepts with a *different* meaning, or that are
  deprecated, warn once per pattern in the build log (7.3). A warning is keyed to the legacy
  input, not to a level. It fires either **at parse time**, when the spelling has a better 4.0
  form so the warning is never noise for new code (`\` as a separator, `[c]`, `a**b` in a rule,
  `[!a]` in a rule, a named group in a mask), or **on evidence**, when the spelling is also
  ordinary 4.0 syntax and should warn only if the old meaning would have mattered (a scan meets
  an entry literally named `[ab]`; a rule's `**/` matched zero directories; `dir/**` lists or
  removes something; `*.*` meets a name without a `.`; a repeated group name rejected a target
  that 3.x matched). Every warning names the 3.x meaning and the replacement.
- **A behaviour-change list** for the 4.0 release notes (section 3), one line per change.
- **All legacy warnings stop at 5.0** (section 10): what still warns in 4.0 becomes either the
  plain 4.0/5.0 meaning without a message, or an error.

## 3. 4.0 behaviour changes (release notes)

One line per change: 3.x meaning → 4.0 meaning [signal]. "Rule" covers every rule-pattern form;
"mask" covers includes and excludes. Details in 5.2.

1. `\` before `( ) [ ] { } |`: a path separator → an escape (`src\(x)\*.cs` names the directory
   `src(x)`, tm 55) [no warning; the other `\` in such a pattern warn XP110].
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
    now also matches `a.dll` at the root and can win over `*.dll` [XP119 on evidence].
13. Segment-local `**` (`a**b`) in a rule: crosses `/` → stays within a segment, like `*`; its
    specificity becomes that of `*` [XP112, parse time].
14. A mask ending in `/**`: an include listed the directories below, an exclude matched nothing
    → every file below [XP113 on evidence].
15. `*.*` as an include segment: any name (OS-listing rule) → names containing a `.` [XP114 on
    evidence].
16. Other OS-listing quirks in includes: `?` matching zero characters before `.` and, on .NET
    Framework, `*.rdl` finding `.rdlx` → gone; the module decides every name [release note].
17. A named group in a mask: an include read it as literal text, an exclude wildcard segment as
    its body → its body, not captured, in every mask; an error from 5.0 [XP111, parse time;
    see C11].
18. A group name starting with a digit, `(1:*)`: a group colliding with the positional `"1"` →
    an error [XP007].
19. A repeated group name, `src/(p:*)/(p:*).fsproj`: each occurrence captured, the last won →
    a backreference, every occurrence matches the same text [XP116 on evidence].
20. `..` after a wildcard: dropped in masks, a literal in rules → an error [XP012].
21. A trailing `/` in a rule pattern: matched nothing → an error [XP011].
22. A trailing `/` in an exclude: ignored → the directory and everything below it [release note].
23. `/` inside a class (`[a/b]`), `[]`, `[z-a]`, `[[:alpha:]]`: split into segments in masks,
    regex meaning or a crash in rules → an error [XP010].
24. Case: a literal exclude segment was case-insensitive on every OS, and wildcard include
    segments followed the .NET listing (insensitive on macOS) → one comparer everywhere, ordinal
    on Linux and macOS, ignore-case on Windows [release note].
25. `**` in includes followed directory symlinks and junctions → does not descend into reparse
    points [release note].
26. Fileset results: file-system enumeration order, duplicates kept → ordinal order per
    directory, duplicates removed [release note; C5].
27. An unreadable directory met in a scan: `UnauthorizedAccessException` ended the build →
    skipped with a build-log warning [release note].
28. The project root is a literal in rule matching: a root containing `( ) [ ] { } |`
    mismatched or crashed → matches [bug fix].
29. Multi-file target names (`*..>`): an empty group value left `(lib:)` in the name, a `)` in
    a value broke it → the right name [bug fix].
30. Multi-output string rules: outputs with different named groups produced names like
    `out/net8.0/.xml` → an error when the rule is created [XP013, tm 50].
31. A class or braces outside a named group in a multi-output pattern or in `format`: printed as
    source text → an error [XP014].

## 4. Grammar

### 4.1 Lexical rules (characters to tokens)

The lexer reads the pattern text left to right. It is the only place where escapes are resolved
and where `*` and `**` are told apart. Its output is a token stream; whether a token is legal
where it appears is decided by the syntax (4.2) and by the context (rule / include / exclude).

```ebnf
(* lexical *)
token        = escape | legacy_sep | dstar | star | qmark | slash
             | group_open | lparen | rparen | pipe
             | lbrack | rbrack | lbrace | rbrace | comma
             | char ;

escape       = "\" , reserved ;                     (* the reserved character, as a literal *)
reserved     = "(" | ")" | "[" | "]" | "{" | "}" | "|" ;
legacy_sep   = "\" ;                                (* a "\" not followed by a reserved character:
                                                       a separator, deprecated (XP110); 5.0: see 10.1 *)
dstar        = "**" ;                               (* maximal munch: "***" = dstar, star *)
star         = "*" ;
qmark        = "?" ;
slash        = "/" ;
group_open   = "(" , name , ":" ;                   (* one token, longest match *)
name         = name_start , { name_char } ;
name_start   = letter | "_" ;                       (* a leading digit is an error, XP007 *)
name_char    = letter | digit | "_" ;               (* letter/digit = Unicode letter/digit *)
lparen       = "(" ;                                (* a "(" that does not start group_open *)
rparen       = ")" ;
pipe         = "|" ;
lbrack       = "[" ;   rbrack = "]" ;
lbrace       = "{" ;   rbrace = "}" ;
comma        = "," ;                                (* special only directly inside braces *)
char         = ? any other character ? ;
```

Lexical decisions:

- **`\` is the escape character for the reserved characters** `( ) [ ] { } |` (tm 55). A `\`
  before any other character, or at the end of the pattern, is still a **path separator** in
  4.0, deprecated and reported once per pattern (XP110), because masks built with `</>` on
  Windows contain `\` (tm 56, 57; open item 15). This includes `\*`, `\?` and `\\`, which mean
  `/*`, `/?` and `//` as in 3.x.
- Consequently 4.0 has no backslash escape for `*`, `?`, `,` and `\`. The one-character class
  is the literal spelling: `[*]`, `[?]`, `[,]`, `[\\]` (inside a class `\` escapes the next
  character, 4.2). In 5.0 `\` also escapes `* ? , ! ^ - \` and any other `\x` is an error
  (XP009, 10.1).
- `**` is one token wherever it appears; whether it is a *globstar* (crosses `/`, zero or more
  segments) or a *segment-local* double star is decided by the position rule in 4.3.
- `,` is a token only directly inside a brace pair; elsewhere it is a `char`.
- `!` is special only as the first character inside a class (`[!a]`); elsewhere it is a `char`.
  A leading `!` in a mask is a literal file-name character, **not** a negation as in
  `.gitignore` (section 11, example 12). `#` is never special.
- F# string literals: `"a\(b"`, `@"a\(b"` and `"""a\(b"""` are the same text, because F# keeps an
  unknown escape `\(` as two characters (checked by the prototype, `EscapingDiskTests`
  "the F# literal forms ... are the same text"). `"a\\b"` is `a\b`. Docs recommend `@"..."` for
  any pattern with `\`.

### 4.2 Syntax (tokens to AST)

```ebnf
(* syntactic; [ ] optional, { } repetition *)
pattern      = [ root ] , body ;
root         = sep | drive , sep ;                   (* an absolute pattern; not joined to a base *)
sep          = slash | legacy_sep ;
drive        = letter , ":" ;                        (* recognised only at the very start *)
body         = { element } ;

element      = literal | star | qmark | dstar | sep
             | class | braces | group ;              (* group: rule context; masks see below *)

literal      = ( char | escape | comma_outside ) , { char | escape | comma_outside } ;

class        = lbrack , [ negation ] , class_items , rbrack ;
negation     = "!" | "^" ;
class_items  = class_item , { class_item } ;        (* a "]" right after "[", "[!" or "[^" is a literal *)
class_item   = class_char , [ "-" , class_char ] ;   (* a range; "-" first or last is a literal *)
class_char   = ? any character except "/", "\" and an unescaped "]" ?
             | "\" , ? any character except "/" ? ;  (* inside a class "\" escapes the next character *)

braces       = lbrace , alternative , comma , alternative , { comma , alternative } , rbrace ;
alternative  = { element } ;                        (* may be empty; may contain "/" and nested braces *)

group        = group_open , group_body , rparen ;   (* string rules: rparen may be missing, see below *)
group_body   = { element } ;                        (* any element, nested groups included *)
```

Syntactic decisions:

- **One grammar, two contexts.** A rule pattern may contain `group`. In a mask a `group` is
  parsed (so that the warning can name it), matched as its body without capturing, and reported
  once (XP111); it is an error (XP008) from 5.0 (tm 58; C11 asks whether it should be an error
  already in 4.0).
- **Braces need a top-level comma.** `{a,b}` is an alternation; `{x}` (no comma) is an error
  (XP005), which keeps `{1..3}` ranges open for later. picomatch reads `{x}` as a literal; Xake
  rejects it to keep the syntax open (brief b).
- **Classes never match `/`.** A `/` inside `[...]` is an error (XP010); a negated class
  excludes `/` implicitly. Empty classes `[]`, reversed ranges `[z-a]` and POSIX names
  `[[:alpha:]]` are errors (XP010).
- `( | )` outside `group` stay **reserved** (tm 60): an unescaped one is an error (XP003), also
  a `(` that does not start a group (`(a b:*)`, `(:*)`, `(1:*)` gives XP007).
- A group's closing `)` is the first unescaped `)` at the group's nesting level. An unclosed
  group in an untyped **string rule** runs to the end of the pattern **without a warning**, as
  in 3.x (tm 61); the typed layer rejects it (XP006). C11 asks whether string rules should
  reject it too.
- Root: a leading `/` or `X:/` (also `\`, `X:\`, which warn XP110) makes the pattern absolute.
  A pattern is otherwise relative to its anchor: the project root for rule patterns (phony
  patterns: the empty root), the fileset base directory for masks (5.1, "anchoring").
- `.` and `..` segments are resolved in the **literal prefix** (the segments before the first
  non-literal element), as `Path.normalizeLiteralPrefix` does in 3.x. After a wildcard, `..` is
  an error (XP012; 3.x dropped it silently in masks).

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

Differences from the prototype's `Token` (`Lit | Star | DoubleStar | AnyChar | Group | Reserved`):
`Sep` is split out of `Lit` (segment boundaries are needed by the walker and the globstar rule),
`Globstar` is distinguished from `DoubleStar`, `Class` and `Alt` are added, `Reserved` is
dropped (unescaped reserved characters are errors in 4.0). `[c]` parses to `Class (false,
[One c])`; its warning (XP105/XP106) is an `Issue` of the parse, not a token. Whether a `Sep`
came from a `\` is recorded only as an `Issue` (XP110).

## 5. Semantics

### 5.1 The 4.0 semantics

One meaning in every context, except where a column says otherwise.

| element | rule pattern | include | exclude |
|---|---|---|---|
| literal | matches itself (case per 5.4) | an explicit name (5.6) | matches itself |
| `*` | 0+ characters other than `/`, leading `.` included (5.3) | same, per segment | same |
| `?` | exactly 1 character other than `/` | same | same |
| `**` globstar | 0+ whole segments (5.5) | same; walked recursively (6) | same |
| `**` segment-local (`a**b`) | same as `*` (XP112) | same as `*` | same as `*` |
| `[abc]` `[a-z]` | one character from the set (case per 5.4) | same | same |
| `[!abc]` `[^abc]` | one character not in the set and not `/` | same | same |
| `[c]` | the character `c` (XP105 suggests `\c` or `c`) | same (XP106: 3.x read the literal text) | same |
| `{a,b}` | one of the alternatives; alternatives may hold `/`, wildcards, nested braces | same; walked as the union (6.3) | same |
| `{x}` (no comma) | error XP005 | error XP005 | error XP005 |
| `(name:body)` | named capture of what `body` matches (5.7) | `body`, not captured, XP111 (error from 5.0; C11) | same as include |
| `( ) \|` outside a group | error XP003 | error XP003 | error XP003 |
| `\c`, `c` reserved | the literal `c` | same | same |
| `/` | separator | separator | separator |
| `\` before a non-reserved character | separator, deprecated (XP110) | same | same |
| trailing `/` | error XP011 (a rule names a file or a phony name) | directories only (5.5) | the directory and everything below it |
| leading `/`, `X:/` | absolute: the project root is not joined | absolute: the base dir is not joined | same |
| anchoring | whole target path, joined to the project root (phony: to `""`) | from the base dir | whole path, joined to the base dir |

"Anchoring" is where Xake deliberately differs from `.gitignore`: a mask without `/` matches only
directly in its base directory, it does not float to any depth (example 5).

### 5.2 Legacy spellings: 3.x meaning, 4.0 meaning, signal

Rows are spellings whose 3.x meaning differs from 5.1, or depended on the context. Everything
else behaves in 3.x as in 5.1. "Rule" here also covers phony/command patterns. In 3.x an exclude
treated a segment as a regex when it contained `*` or `?` ("wildcard segment") and as literal
text otherwise ("literal segment"); 4.0 drops that distinction, but the warnings use it to name
the 3.x meaning correctly. Signal: an error code, a warning code with its trigger (7.3), or
"release note" (section 3).

| spelling | 3.x meaning | 4.0 meaning | signal |
|---|---|---|---|
| `\` + reserved char | separator, then the reserved char's 3.x meaning | escape (tm 55) | release note |
| `\` + other char | separator | separator, deprecated | XP110 at parse |
| `(` `)` (not a group) | rule, exclude wildcard segment: a regex group (a numbered capture); include, exclude literal segment: literal | error | XP003 |
| `\|` | rule: regex alternation over the whole pattern or the enclosing group body; masks as `(` | error; inside a group the text suggests braces | XP003 |
| lone `(` or `)` | rule: `RegexParseException`; masks: literal | error | XP003 |
| unbalanced `[` | rule: `RegexParseException`; include, exclude literal segment: literal | error | XP002 |
| `[abc]`, `[a-z]`, `[^a]` | rule, exclude wildcard segment: class; include, exclude literal segment: literal text | class | XP104 on evidence (masks) |
| `[!a]` | rule, exclude wildcard segment: class of `!` and `a`; include, exclude literal segment: literal text | negated class | XP117 at parse (rule, exclude wildcard segment); XP104 on evidence (otherwise) |
| `[c]` (one character) | rule, exclude wildcard segment: `c`; include, exclude literal segment: literal text `[c]` | `c` | XP105 at parse (meaning unchanged); XP106 at parse (meaning changed) |
| `{a,b}` | literal text | alternation | XP104 on evidence (masks); release note |
| `{x}` | literal text | error | XP005 |
| `**/b`, `a/**/b` in a rule | 1+ directories (regex `(.*)/`) | 0+ | XP119 on evidence |
| `a/**` at the end | rule: 1+ characters below `a`; include: the **directories** below `a` (and `a`); exclude: no file | every file below `a` (rule: unchanged in practice) | XP113 on evidence (masks) |
| `a**b` (segment-local) | rule: crosses `/` (regex `(.*)`); masks: like `*` | like `*` | XP112 at parse (rule) |
| `*.*` as an include segment | **any name** (OS listing rule) | needs a `.` | XP114 on evidence |
| `(name:body)` in a mask | include: literal text (finds nothing with a wildcard); exclude wildcard segment: `body`; exclude literal segment: literal | `body`, not captured | XP111 at parse; C11 |
| unclosed `(name:` | string rule: runs to the end, silently (tm 61) | same; typed layer: error XP006 | —; C11 |
| group name `(1:*)` | a group named `1`, colliding with positional `1` | error | XP007 |
| repeated name `(n:*)/(n:*)` | both capture; the **last** value wins, values may differ | **backreference**: every occurrence matches the same text | XP116 on evidence |
| `..` after a wildcard | rule: literal (no file has it); masks: dropped | error | XP012 |
| trailing `/` | rule: matches nothing; include: directories only; exclude: ignored | rule: error; include: unchanged; exclude: the directory and below | XP011 (rule); release note (exclude) |
| `/` in a class, `[]`, `[z-a]`, `[[:` | masks: split at `/` or literal; rule: regex meaning or a crash | error | XP010 |
| case of a literal segment | rule: per OS; include: as the file system resolves it; exclude: **always insensitive** | per OS (5.4) | release note |
| case of a wildcard segment | rule, exclude: per OS; include: as the .NET listing (insensitive on Windows and macOS) | per OS | release note |

Notes:

- The prototype (#90) already read `[c]` in includes as the character `c` (`sub[1]/*.c` found
  `sub1`). That is now simply the 4.0 meaning, announced by XP106 (13, C1).
- Rows with a parse-time warning have a 4.0 spelling that never warns (`/`, `\(`, `c`, `*`,
  `[^a]`, the group body), so new code is not penalised. Rows with an evidence warning are
  ordinary 4.0 syntax; they warn only when the 3.x meaning would have given a different result
  in this build (7.3).

### 5.3 Dotfiles

`*`, `?`, `**` and classes match a leading `.` in every context. `**/*.cs` lists
`.hidden/y.cs`; excluding dot folders is written explicitly: `-- "**/.*/**"` or the 3.x idiom
`-- "**/.*/**/*"` (pinned by `FilesetTests`; both prune). This is 3.x behaviour (Ant,
`.gitignore`, Rust `globset`); it differs from picomatch / bash (`dot: false`). The directory
entries `.` and `..` are never produced by a listing.

### 5.4 Case sensitivity

- One comparer for all contexts: ordinal on Linux and macOS (`Env.isUnix`), ordinal-ignore-case
  on Windows. It is the comparer `File` equality and rule matching already use (`Types.fs`,
  `Fingerprints.fs`). Classes and ranges follow it: on Windows `[a-z]` matches `Q`. Regex
  options: `RegexOptions.CultureInvariant`, plus `IgnoreCase` on Windows.
- A literal segment of an include is a probe of the file system (5.6): on a case-insensitive
  file system (Windows, default macOS) it finds the file whatever the case, and the result keeps
  the mask's spelling. This is a property of the file system, not of the matcher.
- 4.0 changes from 3.x (release notes, section 3 item 24): on macOS a wildcard include becomes
  case sensitive (`!! "*.CS"` no longer finds `a.cs`); on Linux and macOS a literal exclude
  segment becomes case sensitive (`-- "Bin/**/*"` no longer removes `bin/...`). No warning
  (open item 4).

### 5.5 `**` edge cases

All contexts unless stated:

| pattern | matches | does not match | capture of the `**` |
|---|---|---|---|
| `**` | every path below the anchor, any depth | — | the whole path |
| `**/b` | `b`, `x/b`, `x/y/b` | `xb` | `""`, `x`, `x/y` |
| `a/**/b` | `a/b`, `a/x/b`, `a/x/y/b` | `ab`, `a/xb` | `""`, `x`, `x/y` |
| `a/**` | `a/f`, `a/x/f` (every file below `a`) | `a` itself | `f`, `x/f` |
| `a/**/` (include) | `a` and every directory below it | files | — |
| `a/**` (exclude) | every file below `a` | — | — |
| `a**b` | as `a*b` (one segment) | `a/x/b` | as `*` |
| `**/**/b` | as `**/b` (adjacent globstars collapse) | — | first `**` takes the path, second `""` |
| `(p:**)/b` | `b`, `x/b` | — | `p` = `""`, `x` |
| `(path:bin/**)/file.o` | `bin/file.o`, `bin/x/y/file.o` | — | `path` = `bin`, `bin/x/y` (no trailing `/`) |

Rules for the value of a globstar capture: it never starts or ends with `/`; zero segments give
`""`. Formatting (5.9) is the inverse: a globstar slot with value `""` drops one adjacent `/`, so
`a/**/b` formats to `a/b`, not `a//b`.

3.x differences (section 3 items 12–14): in rule patterns a globstar before a `/` needed at least
one directory (`**/mlib.dll` did not match `mlib.dll` at the root: `PathTests` pins it; XP119
on evidence); segment-local `a**b` in a rule crossed `/` (XP112); in an include `a/**` listed
directories, in an exclude it matched nothing (`FilesetTests` pins it; XP113 on evidence).

### 5.6 Literal segments in includes

A mask segment without any non-literal element is a **probe**, not a listing:

- If the whole mask has no non-literal element (no `* ? ** [..] {..}`), it is an **explicit
  name**: it is listed whether or not the file exists (3.x rule, `Fileset.listFiles`
  `isExplicitRule`). This is what lets `needFiles (!! "out/x.dll")` build a missing file.
- Otherwise a literal directory segment is kept if `Directory.Exists`, a literal last segment if
  `File.Exists` (or `Directory.Exists` for a trailing `/`). No listing is done, so a literal
  segment costs one `stat`, and the path keeps the mask's spelling, not the disk's.
- Braces count as non-literal: `src/{a,b}.cs` lists only the existing ones. Brace expansion in
  the bash sense (both names listed regardless) is not done (open item 6).

### 5.7 Named groups and captures

- **Names**: `name_start name_char*` with Unicode letters/digits allowed (F# record fields can be
  Unicode). A leading digit is an error (XP007). A `(` followed by anything else (`(a-b:*)`,
  `(a b:*)`, `(:*)`) is not a group: the `(` is reserved (XP003).
- **Body**: any element: wildcards, globstars, classes, braces, `/`, nested groups
  (`(name:(plat:*)-a.ss)` and `(root:*/*-a).ss` are pinned by `XakeScriptTests`).
- **Repeated name** in one pattern: a backreference (all occurrences match the same text;
  `src/(p:*)/(p:*).fsproj` matches `src/Core/Core.fsproj` with `p = Core`, not
  `src/Core/Other.fsproj`). 3.x let each occurrence capture and the last one win; XP116 warns
  on evidence. The wildcards inside a repeated occurrence get no positional numbers.
- **Multi-output** (`*..>`, `targets`): every output has the same set of named groups, checked
  when the rule is created (tm 50; prototype `checkSameGroups`, error XP013). Bare wildcards stay
  allowed in string multi-output rules and pair **by position** (`["dd/*.dll"; "dd/*.xml"]`, as
  in 3.x); the typed layer keeps rejecting them (its own rule). A class or brace outside a group
  in a multi-output pattern is an error (XP014): the other outputs could not be named.
- **What `getRuleMatches` holds** (unchanged interface, brief e):
  - every named group by name;
  - every *unnamed* capture by its number `"1"`, `"2"`, ... in left-to-right order **at any depth**:
    each `*`, `**`, `?` is one, including those inside named groups (`gen/*/(name:*)-?.txt`:
    `"1"` = the first `*`, `"2"` = the group's own `*`, `"3"` = `?`). 3.x also numbered every
    unescaped `(` of a rule; that spelling is an error in 4.0 (XP003), so no valid pattern
    changes its numbering;
  - classes and braces take **no** number (they are non-capturing); wildcards inside braces are
    numbered in text order, and those of an alternative that did not match hold `""`;
  - `"0"`, the whole matched path (root included), a by-product of .NET regex numbering that
    exists in 3.x and is kept.
  - Values are **greedy, leftmost**: `(a:*)-(b:*)` on `x-y-z` gives `a = x-y`, `b = z`, as the
    .NET regex gives in 3.x. A matcher that is not regex-based must reproduce this (9).
- `getRuleMatch` of an unknown key returns `""` (tm 7).

### 5.8 Regex equivalents

The matcher is the AST translated to a .NET regex (section 9 explains why). The translation is
part of the spec so that conformance tests can pin it (`t` = translate):

| token | regex |
|---|---|
| `Lit s` | `Regex.Escape`-equivalent of `s` (all of `. $ ^ + ! = { } ( ) [ ] \| \ * ?` escaped) |
| `Sep` | `/` |
| `Star`, `DoubleStar` | `([^/]*)` |
| `AnyChar` | `([^/])` |
| `Globstar` at the start, followed by a separator | `(?:(.*)/)?` (the `/` is consumed) |
| `Globstar` between separators | `(?:(.*)/)?` (after the preceding `/`) |
| `Globstar` at the end, after a separator | `(.*)` |
| `Globstar` alone | `(.*)` |
| `Class (false, items)` | `[items]` |
| `Class (true, items)` | `[^/items]` |
| `Alt alts` | `(?:t a1\|t a2\|...)` |
| `Group (n, body)` in a rule, first occurrence | `(?<n>t body)` |
| `Group (n, _)` in a rule, later occurrence | `\k<n>` |
| `Group (_, body)` in a mask | `(?:t body)` |

Anchoring: `^` + escaped anchor (project root, base dir; `..`/`.` collapsed in the literal
prefix) + `/` + body + `$`, options `CultureInvariant` (+ `IgnoreCase` on Windows). Paths are
normalized to `/` before matching (paths from disk in 3.x too, tm 56), so `[^/]` replaces 3.x's
`[^/\\]`. The project root is escaped (3.x did not escape it: bug fix, section 3 item 28).

Example: `src/**/(name:*).fs` under root `/p` →
`^/p/src/(?:(.*)/)?(?<name>([^/]*))\.fs$`.

The 3.x translation (`*` → `([^/\\]*)`, every `**` → `(.*)`, reserved characters raw), pinned by
the prototype's `PatternAstTests` "AST regex matches the former substitution byte for byte", is
not kept; those vectors are rewritten to this table (10.3).

### 5.9 Generation: `format`, `formatMatches`, `glob`

- `format named positional tokens`: a named group becomes `named name`; the i-th **top-level**
  bare wildcard (1-based, wildcards inside groups do not count) becomes `positional i`;
  literals and `Sep` print as text with escapes resolved, `Sep` as `/`. A pattern made only of
  literals formats to its text (tm 14). A globstar slot with `""` drops one adjacent `/` (5.5).
  A `Class` or `Alt` outside a group cannot be formatted: XP014.
- `formatMatches (matches: Map<string,string>) (pattern: string)`: `format` fed from a rule match:
  named groups by name, the i-th top-level bare wildcard by the regex number it is captured
  under in the **pattern being formatted** (`wildcards` computes it). A missing key gives `""`
  (as `getRuleMatch`). This replaces `ExecCore.applyWildcards` (tm 14) and fixes its two bugs
  (brief c): an empty group value left `(lib:)` in the name; a `)` in a value broke the name.
- `glob named positional tokens`: a mask for partly known values: a bound group or wildcard
  becomes its value **escaped**; an unbound group becomes its own body (recursively); an unbound
  wildcard stays. The result has no named groups, so it is a valid mask without XP111.
  `glob` of `src/(dir:*)/**/*.fs` with `dir = core` is `src/core/**/*.fs`.
- `unparse tokens` is the inverse of `parse`: reserved characters in literals escaped with `\`,
  `*` `?` in literals as `[*]` `[?]`, a `\` in a literal as `[\\]`, `Sep` as `/`. `escape s`
  applies the same rules to a plain string, so that `parse (escape s)` is `[Lit s]` per
  segment. In 5.0 both switch to `\*`, `\?`, `\\` (10.1).

### 5.10 Specificity

Unchanged formula (`Path.Specificity`, #88/#89): `(literal characters, -globstars, -wildcard
weight)`. New tokens: `Class` adds wildcard weight 1 (like `?`); `Alt` contributes the
specificity of its **least specific** alternative; `Globstar` counts as `**`; `DoubleStar` now
counts as `*` (3.x counted it as `**`, since it crossed `/`; section 3 item 13); `Sep` counts as
one literal character (as `/` inside `Lit` does in 3.x). Specificity is computed from the AST,
never from text.

## 6. Fileset scanning

### 6.1 What stays

`Fileset`, `FilesetElement = Includes of PathMask | Excludes of PathMask`, `FilesetOptions`, the
operators and the builder keep their public shapes in 4.0 (open item 9). `Path.Part` values keep
the **segment text** of the mask (escapes kept, as the prototype does) so that the pickled
`GetFiles` dependency in `.xake` keeps its format (DB version stays `0.5`). The module parses each
part's text at scan time. Hermetic code that builds `Includes (Path.parse m)` keeps compiling.

`Path.parse` / `parseDir` split at `/` but **not inside `[...]` or `{...}`** (3.x splits
`[a/b]*.c` into `[a` and `b]*.c`); they also split at a `\` that escapes nothing (XP110).

### 6.2 Algorithm

`toFileList` folds the elements in order, as in 3.x: an include appends what it finds, an
exclude removes from what was found so far. Excludes apply only to earlier includes (pinned by
`FilesetTests` "keeps the order of includes and excludes").

An include is walked from its anchor (base dir, or the root of an absolute mask):

1. The **literal prefix** (segments before the first non-literal one, `.`/`..` resolved) is
   joined without listing.
2. For each following segment:
   - literal: probe (5.6);
   - one-segment pattern (`*`, `?`, class, literal parts, braces without `/`): list the
     directory (subdirectories for a middle segment, files for the last one; both for a trailing
     `/` that asks for directories), and keep the names the segment's matcher accepts;
   - globstar: apply the rest of the pattern at this directory (zero segments) and then recurse
     into each subdirectory, depth first;
   - braces holding `/` (`{src,test/unit}/*.cs`): the mask is expanded into its alternatives
     before walking and the union is walked (each alternative with its own literal prefix).
3. **Pruning**: a subdirectory is not entered when an exclude that comes *after* this include
   covers it entirely, i.e. the exclude is `P/**` or `P/**/*` and `P` matches the
   subdirectory's path. Sound because a file removed by a later exclude cannot be brought back
   by this include (a later include brings it back by its own walk). This is what makes
   `!! "**/*.cs" -- "**/.*/**/*" -- "bin/**/*"` skip `.git`, `bin` without reading them.
   Pruning is an optimisation only: the result is the same without it.
4. An exclude is a whole-path match: the exclude mask is anchored at the base dir (5.8) and
   matched against each found file's full path (normalized to `/`).
5. **Evidence checks** for the legacy warnings (7.3) piggyback on the walk and cost nothing when
   the mask has no legacy-sensitive spelling: XP104 (a listed entry's name equals the literal
   reading of a `[...]`/`{...}` segment), XP113 (a `dir/**` include yields a result, or a
   `dir/**` exclude removes one), XP114 (a `*.*` segment rejects a listed name without `.`).

### 6.3 The OS listing's role

The .NET listing (`Directory.EnumerateFiles/Directories(dir, filter)`) knows only `*` and `?`,
with Win32 quirks (`*.*` = `*`; `?` can match zero characters before `.` or the end; on .NET
Framework a 3-letter extension also matches longer ones, `*.rdl` finds `c.rdlx`). Therefore:

- the module's matcher decides every name; the OS filter is only a **pre-filter** that may return
  a superset;
- the filter passed is the segment with escapes resolved when the segment holds only literals,
  `*` and `?`, and `"*"` otherwise (classes, braces);
- all Win32 quirks are thus dropped, including `*.*` = any name (section 3 items 15–16; XP114
  on evidence). The `cp` task's `Dir` mode, which used `dirMask </> "**/*.*"`, builds
  `dirMask + "/**/*"` (with `/`, as the prototype did).

### 6.4 Symlinks and junctions

- Literal segments and one-segment patterns go through a symlinked directory as the OS resolves
  it.
- Globstar recursion does not descend into a directory whose attributes include `ReparsePoint`
  (symlinks, junctions), which prevents cycles without an API that netstandard2.0 lacks
  (`LinkTarget`). A link can still be reached explicitly (`node_modules/x/**`). 3.x followed
  them (`SearchOption.AllDirectories`; section 3 item 25). Open item 10.

### 6.5 Ordering, duplicates, determinism

- Within one directory, entries are processed in **ordinal order** of their names
  (`StringComparer.Ordinal`, on every OS), files of a directory before its subdirectories
  (pre-order, as `find`). The include's results come out in that order; includes are
  concatenated in declaration order. In 3.x the order is whatever the file system enumerates
  (alphabetical on NTFS, hash order on ext4), which matters for F# compile order.
- Duplicates (the same full path from two includes, or `**/**`) are kept once, at the first
  occurrence, compared with the OS comparer (5.4). 3.x keeps them twice (see 13, C5: the
  `FilesetTests` case titled "removes duplicates" asserts 2).
- The result is a function of the file tree and the fileset only: no dependence on enumeration
  order, thread timing or caches. `compareFileList` (incremental check) compares sets, so ordering
  never triggers a rebuild.
- An unreadable directory met during a walk is skipped and reported once per directory as a
  build-log warning (in 3.x an `UnauthorizedAccessException` ends the build). A missing base
  directory gives an empty list (`failonempty` turns it into an error).

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

### 7.2 Exceptions and errors

```fsharp
/// An invalid pattern, or a rule whose outputs disagree. The message names the pattern;
/// ToString() is the message alone, so a script failing at load shows no stack trace.
exception PatternException of string

type PatternContext = Rule | Include | Exclude

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
instead of throwing. Message format:

```
Invalid pattern '<text>' (<rule|include|exclude>) at character <n>: <reason> [<code>]
```

(`at character <n>` is left out when there is no position). The prefix `Invalid pattern '<text>'`
is what the prototype's tests assert. Codes `XP0nn` are errors, `XP1nn` warnings; the numbers of
the 2026-10-26 draft are kept, retired codes are not reused (XP001, XP101, XP103, XP108, XP109,
XP115, XP118 folded into the errors below).

| code | from | condition | reason text |
|---|---|---|---|
| XP002 | 4.0 | a `[` without `]` | `an unescaped '[' has no matching ']' (Xake 3.x read it as a literal in masks and failed in rules); write \[ for a literal '['` |
| XP003 | 4.0 | `(`, `)` or `\|` outside a named group | `'(' is reserved (Xake 3.x: a regex group in rule patterns, a literal character in masks); write \( for a literal '('`; for `\|` inside a group whose alternatives are literals: `'(ext:dll\|exe\|nupkg)': '\|' is reserved (Xake 3.x: a regex alternation); write (ext:{dll,exe,nupkg})` |
| XP004 | 4.0 | `{` without `}`, a stray `}` or `]` | `an unescaped '{' has no matching '}' (Xake 3.x: a literal); write \{ for a literal '{'` |
| XP005 | 4.0 | `{...}` without a top-level `,` | `'{x}' is not an alternation (no ','; Xake 3.x read it as literal text); write \{x\} for literal braces, or {a,b} for alternatives` |
| XP006 | 4.0 | unclosed `(name:` in a typed pattern (string rules: C11) | `the group '(name:' is not closed` |
| XP007 | 4.0 | a group name starting with a digit | `the group name '1' starts with a digit; it would collide with the positional match '1' (Xake 3.x accepted it); rename it` |
| XP008 | 5.0 (4.0 if C11 option B) | a named group in a mask | `named groups exist only in rule patterns; write the group's body, or \( \) for literal parentheses` |
| XP009 | 5.0 | `\` at the end, or before a character that is not escapable | `'\x' escapes nothing; use '/' as the separator, '\\' for a backslash` |
| XP010 | 4.0 | `[]`, `[z-a]`, `/` in a class, `[[:` | `invalid character class '[...]': <detail>` |
| XP011 | 4.0 | a rule pattern ending in `/` | `a rule pattern names a file or a phony target and cannot end with '/' (in Xake 3.x it matched nothing)` |
| XP012 | 4.0 | `..` after a wildcard | `'..' is allowed only before the first wildcard (Xake 3.x ignored it in masks)` |
| XP013 | 4.0 | multi-output rule, outputs with different named groups | prototype text: `output "<p>" has groups [..], the first output [..] (missing [..], extra [..]); every output needs the same named groups, or the others cannot be named from a match` |
| XP014 | 4.0 | a class or braces outside a group in a pattern that must be formatted (multi-output, `format`) | `'{dll,pdb}' cannot be filled in from a match; put it in a named group: (ext:{dll,pdb})` |

### 7.3 Warnings on legacy inputs

Sent to the build log at level **Warning** (the prototype used `Message`, see 13, C2), text:

```
Pattern '<text>' (<rule|include|exclude>): <part>; <part>. [<codes>]
```

Parts, in a fixed order (the codes in brackets, comma-separated). Trigger "parse" = when the
pattern is parsed (7.1); "evidence" = only when the stated observation is made, during a scan
(6.2 step 5) or a rule match. Every text names the 3.x meaning and the replacement.

| code | condition | trigger | part text |
|---|---|---|---|
| XP104 | mask: `[ab]` or `{a,b}` where 3.x read literal text (include, exclude literal segment) | evidence: a scanned entry is named exactly as the literal text | `'[ab]' is a character class from Xake 4.0 (one of 'a', 'b'); Xake 3.x read the literal text '[ab]', and '<entry>' exists; write \[ab\] for the literal text` (braces: `'{a,b}' is an alternation from Xake 4.0; Xake 3.x read the literal text '{a,b}', and '<entry>' exists; write \{a,b\} for the literal text`) |
| XP105 | `[c]` where 3.x meant `c` too (rule, exclude wildcard segment); `c` not one of `* ? , \` | parse | `'[(]' is the literal '(' (as in Xake 3.x); write \( instead` / for an ordinary character: `'[x]' is the literal 'x'; write x instead` |
| XP106 | `[c]` where 3.x read literal text (include, exclude literal segment) | parse | `'[1]' matches the single character '1' from Xake 4.0; Xake 3.x read the literal text '[1]'; write \[1\] for the brackets, or 1 for the character` |
| XP110 | `\` used as a separator | parse | `'\' as a path separator is deprecated and is only an escape from Xake 5.0; use '/'` |
| XP111 | named group in a mask | parse | `named groups ('(name:...)') belong in rule patterns; in a mask Xake 4.0 matches the group's body without capturing (Xake 3.x: an include read '(name:' and ')' as literal text, an exclude wildcard segment matched the body); an error from Xake 5.0; write the body alone, or \( \) for literal parentheses` |
| XP112 | rule: segment-local `**` | parse | `'a**b' matches within one segment from Xake 4.0, like 'a*b'; in Xake 3.x it crossed '/'; write '*' for one segment, or '/**/' between segments for any depth` |
| XP113 | mask ending in `/**` | evidence: an include yields a result, an exclude removes a file | `'dir/**' is every file below 'dir' from Xake 4.0; in Xake 3.x an include listed the directories below 'dir' and an exclude matched nothing; write 'dir/**/' for the directories` |
| XP114 | include: `*.*` segment | evidence: a listed name without `.` is rejected | `'*.*' needs a '.' in the name from Xake 4.0; Xake 3.x also listed '<name>'; write '*' for every name` |
| XP116 | rule: repeated group name | evidence: a target is rejected by the backreference but accepted by the same pattern without it (checked only for such patterns, only on rejection) | `the group 'n' appears 2 times; from Xake 4.0 every occurrence must match the same text; Xake 3.x let the last value win and matched '<target>'; rename one occurrence to keep the 3.x match` |
| XP117 | `[!a]` in a rule or an exclude wildcard segment | parse | `'[!a]' is a negated class (any character but 'a') from Xake 4.0; in Xake 3.x it was a class of '!' and 'a'; write [^a] for "not a" (same meaning in 3.x and 4.0), or [a!] for the 3.x meaning` |
| XP119 | rule: a globstar before a separator | evidence: a rule match where it took zero directories | `'**/' matched zero directories for '<target>'; in Xake 3.x '**/' in a rule needed at least one directory, so this rule did not match it; write '*/**/' to require one` |

Rules:

- **Once per pattern**: the key is `(context, pattern text)`, process-wide (static
  `ConcurrentDictionary`), so the same text used as a rule and as a mask can warn twice (the
  3.x meaning differs, so does the text). All parse-time parts of one pattern go in one line;
  an evidence warning is one more line for the same key, at most once per code.
- Warnings raised while no build log is attached (masks built at script load) wait in a buffer
  (at most 1000) and are delivered when a build attaches its log (prototype
  `Warnings.attach`). Several attached logs all receive them.
- Paths read from disk, the project root and `BaseDir`/`@@` values are paths, never patterns:
  they produce no warnings (prototype test "paths read from disk give no warning"). A mask built
  with `</>` on Windows contains `\` and **does** warn XP110 (tm 57, by design).
- No warning for the case changes (5.4), the dropped OS-listing quirks, symlinks, ordering and
  duplicates: release notes only (section 3).
- **All XP1nn warnings are removed in 5.0** (10.1).

## 8. Public API

### 8.1 Placement and name

- New file `src/core/PatternAst.fs`, compiled before `Path.fs`, holding the public module
  **`Xake.PatternAst`** (tm 13: `PatternAst` becomes public; the name is the one the prototype,
  its tests, `docs/overview.md` and tm 64 use). In 3.x it is `internal` inside `Path.fs`.
- Not opened by `open Xake` (a plain module, no `[<AutoOpen>]`), so `Lit`, `Star`, ... do not
  enter scripts' scope. `Xake.Patterns` is avoided: it was the type-provider package name.
- The matcher stays **internal** (`Path.matchImpl`); scripts and the typed layer match through
  the existing public functions, whose signatures do not change: `Path.matchGroups`,
  `Path.matches`, `Path.matchesPattern`, `Path.parse`, `Path.parseDir`, `Fileset.*`.

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
type PatternContext = Rule | Include | Exclude
type Issue = { Code: string; Text: string }          // one parse-time warning part (7.3)
type PatternError = { Pattern: string; Context: PatternContext; Position: int option; Code: string; Reason: string }
exception PatternException of string

// ---- parsing
val tryParse   : PatternContext -> string -> Result<Token list * Issue list, PatternError>
val parseIn    : PatternContext -> string -> Token list          // raises PatternException; no warnings
val parse      : string -> Token list                            // = parseIn Rule (prototype signature)
val check      : PatternContext -> string -> unit                // parse, report parse-time warnings once (7.3), raise on error
val unparse    : Token list -> string
val escape     : string -> string
val reservedChars : string                                       // "()[]{}|"

// ---- reading the AST
val groupNames : Token list -> string list                       // first-appearance order, nested included
val wildcards  : Token list -> (Token * int) list                // top-level bare wildcards with their regex numbers
val specificity: Token list -> int * int * int                   // = Path.Specificity

// ---- generation (5.9)
val format        : named: (string -> string) -> positional: (int -> string) -> Token list -> string
val formatMatches : Map<string, string> -> string -> string      // replaces ExecCore.applyWildcards
val glob          : named: (string -> string option) -> positional: (int -> string option) -> Token list -> string

// ---- warnings (7.3)
module Warnings =
    val internal attach : (string -> unit) -> System.IDisposable
```

Kept internal (implementation, may change if the matcher changes): `toRegex`, the regex cache,
the evidence checks, `checkSameGroups` (used by `*..>`, `targets` and the typed layer through
`InternalsVisibleTo` or a public wrapper, decided in Phase 3), `plainText`, the OS-filter helper.

### 8.3 Engine wiring

- `ExecCore.explainMatch` and `makeTarget` keep calling `Path.matchGroups pattern root`; only its
  implementation moves onto the module (it also runs the XP116/XP119 evidence checks).
  **`getRuleMatch` is unchanged** (brief e), including `""` for an unknown key and the `"0"` key.
- `ExecCore.applyWildcards (Some matches) pat` becomes `PatternAst.formatMatches matches pat`;
  `applyWildcards None` (identity) disappears with its only caller. `wildcardsRegex` and
  `patternTagRegex` are deleted (the third grammar encoding).
- `..>`, `*..>`, `=>`, `target(s)`, `command`, `phony` call `PatternAst.check Rule` on their
  patterns (and `*..>` / `targets` the group-set check, XP013).
- `Fileset` construction calls `PatternAst.check Include|Exclude`; `toFileList` walks with the
  module (6).

## 9. Performance

Options:

| | AST → .NET regex, cached | own matcher over the AST |
|---|---|---|
| exact `getRuleMatch` values (greedy leftmost, numbering, `"0"`) | by construction (the 3.x engine is a regex) | must re-implement backtracking for multiple wildcards to reproduce greedy captures |
| backreferences (repeated groups), alternation, classes, case-insensitive culture rules | built in (`\k<n>`, `(?:a\|b)`, `[...]`, `IgnoreCase`) | to write and test |
| cost to build | `RegexOptions.Compiled`: ~1 ms + JIT per pattern (prototype comment); interpreted: ~20–50 µs | ~0 |
| cost per match | ~0.2–1 µs per path (compiled) | similar for simple globs, worse with backtracking captures |
| code | ~100 lines translation + cache | ~400+ lines plus a second test corpus |
| risk | ReDoS on pathological patterns (`**/**/**/...`): patterns are written by the script author; paths are short | bugs in captures |

Recommendation: **regex backend, generated from the AST, cached**, plus two fast paths that skip
the regex where the AST makes it trivial:

1. Cache key `(context, pattern text, anchor, case mode)`, process-wide `ConcurrentDictionary`
   (3.x: per mask and per (pattern, root)); regexes are thread-safe for matching.
2. `RegexOptions.Compiled` for rule patterns and exclude masks (few, hot: matched against every
   target / every found file); **interpreted** for one-segment walker matchers, for patterns
   built at run time in large numbers (e.g. `glob` results in a loop), and for the second regex
   of the XP116 evidence check (used only on rejection, only for patterns with a repeated name).
3. Fast paths: a literal segment is a string comparison; a segment `*` matches everything; a
   segment `*<literal>` (`*.cs`) is an `EndsWith` with the OS comparer. These cover the bulk of
   real masks (`**/*.cs`, `src/*.fs`) without a regex per name.
4. Walking cost is dominated by the listing, not the matching; pruning (6.2) is the large win
   (`.git`, `bin`, `obj`). The G8 measurement (`!**/.*/**/*` at ~1 ms per file before caching)
   is the regression to guard: the Explicit benchmark in `FilesetTests` runs in Phase 3
   before/after, with a target of no regression on the include-only case and a measurable win
   on the exclude cases. The evidence checks (6.2 step 5) must not show up in it.

An own matcher stays possible later behind the same AST; nothing in the public API exposes
`Regex`.

## 10. Deprecation calendar, version, compat corpus

### 10.1 Calendar

| input | 3.x | 4.0 | 5.0 |
|---|---|---|---|
| `\` before `( ) [ ] { } \|` | separator | escape (tm 55) | escape |
| `\` before anything else | separator | separator, XP110 | escape for `* ? , ! ^ - \`; any other `\x` error XP009 |
| named group in a mask | per context (5.2) | its body, XP111 | error XP008 |
| `[c]` | per context (5.2) | `c`, XP105/XP106 | `c`, no warning (valid POSIX glob) |
| evidence warnings XP104, XP113, XP114, XP116, XP119 and XP112, XP117 | — | warn | removed; the 4.0 meaning stays |
| the 3.x clauses in error reasons (7.2) | — | present | may be dropped |
| unclosed `(name:` in a string rule | silent | silent (tm 61; C11) | error XP006 (to confirm in 5.0) |

Paths read from disk keep being normalized (`\` → `/`) in every release (tm 56). `</>` is not
changed (tm 57). `unparse`/`escape` move from `[*]`, `[?]`, `[\\]` to `\*`, `\?`, `\\` in 5.0.

### 10.2 Version (brief f)

**4.0.0**: the pattern module, the engine migration and the typed layer ship in the 4.0 line on
`dev` (D1), through the integration branch `feature/patterns2` (D2), next to the variable model.
No 3.x release carries any of it. The hermetic package's Xake range must admit 4.0 (C8).

### 10.3 Compat corpus (what pins behaviour)

Phase 2 turns these into conformance vectors, each tagged **unchanged** (same result in 3.x and
4.0) or **changed in 4.0** (the 4.0 result, the 3.x result as a comment, the expected
error/warning code, the section 3 item):

- **Engine tests on `dev`**: `PathTests.fs` (all `MaskTests`, `MaskWithParent`, the group cases
  `(apl:*/*)`, `(path:bin/**)`, the root-relative `**/mlib.dll` cases — changed, item 12; lines
  18–19 `c:\[(]*[)].c` / `c:\[(]*.c` change meaning under tm 55 and are rewritten as
  `c:/\(*\).c`-style vectors), `FilesetTests.fs` (dot-folder idiom, `dir/**` vs `dir/**/*` —
  changed, item 14; case per OS — changed, item 24; duplicates — see C5), `XakeScriptTests.fs`
  (group-in-rule cases incl. `(name:[(]*).ss`, `[(]*.ss` — unchanged with XP105; multi-target
  name generation), `RuleMatchTests.fs`, `RuleSpecificityTests.fs`, `ParentDirTargetTests.fs`,
  `PatternAstTests.fs` (regex byte identity: rewritten to 5.8).
- **Prototype tests** (frozen branch), ported: `EscapingTests.fs`, `EscapingDiskTests.fs`,
  `PatternAstTests.fs` — the include `[c]` cases stay as the prototype wrote them (they are the
  4.0 meaning) and gain XP106; warning level changed (C2).
- **Samples and build scripts**: every pattern and mask string in `samples/*.fsx`
  (`samples/Research/*` excluded: experiments with their own regex code), `build.fsx`,
  `build.fsc.fsx`: e.g. `out/(cfg:*)/(fwk:*)/(name:*).dll`, `lock::(name:*)::(cfg:*)`,
  `test::(project:*)/(slice:*)`, `run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt`, `src/*/xake.project.json`,
  `-- "src/*tests/*.fsproj"`, `includes "a/**/"`, `paket-files/*.*` (item 15, XP114 on
  evidence; the sample should become `paket-files/*` in the same change).
- **Docs code blocks**: every fenced F# block in `docs/*.md` (e.g. `db:seed-(table:*)`,
  `signed/(name:*).(ext:dll|exe|nupkg)` — becomes `(ext:{dll,exe,nupkg})`,
  `out/pkg/%s{...}/...(ver:*).nupkg`).
- **Hermetic**: `src/hermetic` and its tests use `Path.parse`, `Path.matchesPattern`,
  `Includes/Excludes` directly, the description masks `["**/*.cs", "!obj/**/*", "!bin/**/*",
  "!**/.*/**/*"]` (the `!` is hermetic's own exclude marker, stripped before Xake sees the mask),
  the `dir/**` → `dir/**/*` workaround (`Import.fs`, `Lock.fs`, `Locks.fs`; can go in 4.0), and
  `signed/(name:*).(ext:dll|exe|nupkg)` (`Sign.fs` docs, `SignTests.fs`): XP003 in 4.0, so it
  is replaced by `(ext:{dll,exe,nupkg})` in the same change that ships the module (C3).

## 11. Worked examples

Root `/p` for rules; base dir `/p` for masks; Linux unless stated. Results are 4.0; "3.x:" notes
the old result where it differs.

1. **Rule with captures.** `"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> ...`, target
   `/p/out/Release/net8.0/Core.dll`. Regex:
   `^/p/out/(?<cfg>([^/]*))/(?<fwk>([^/]*))/(?<name>([^/]*))\.dll$` (3.x: `[^/\\]`).
   `getRuleMatches` = `cfg=Release, fwk=net8.0, name=Core, 1=Release, 2=net8.0, 3=Core,
   0=/p/out/Release/net8.0/Core.dll` (unchanged). `getRuleMatch "nme"` = `""`.

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
   `.hidden` (pruned, 6.2). `-- "**/.*/**"` gives the same (3.x: removed nothing, item 14).

5. **Anchoring vs `.gitignore`.** `!! "**/*.rdl*" -- "*.rdlx"` removes only `.rdlx` files directly
   in the base dir. In `.gitignore`, `*.rdlx` (no `/`) matches at any depth; in Xake write
   `-- "**/*.rdlx"`.

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
   warning, `getRuleMatch "ext"` = `dll`. Regex:
   `^/p/signed/(?<name>([^/]*))\.(?<ext>(?:dll|exe|nupkg))$`. Unescaped `a|b.txt` as a rule →
   XP003 (3.x: regex `^/p/a|b\.txt$`, any path starting with `/p/a` or ending in `b.txt`).

9. **Globstar, zero segments.** Rule `"**/*.dll"`, target `/p/a.dll` → match, `1 = ""`, `2 = a`,
   and XP119 once: `'**/' matched zero directories for '/p/a.dll'; in Xake 3.x '**/' in a rule
   needed at least one directory, so this rule did not match it; write '*/**/' to require one`
   (3.x: no match, pinned by `PathTests`). Rule `"a/**/b.txt"` formats with `1 = ""` to
   `a/b.txt`. Rule selection: with rules `*.dll` and `**/*.dll` both present, `a.dll` now matches
   both and `**/*.dll` wins on literal count (`/.dll` = 5 vs `.dll` = 4) — section 3 item 12.

10. **`dir/**` in masks.** `!! "**/*.cs" -- "dir/**"` → removes everything below `dir`, XP113 once
    (3.x: removed nothing, pinned). `!! "src/hermetic/**"` → every file below it, XP113 (3.x: the
    directories below `src/hermetic` as `File` values). `!! "src/hermetic/**/"` → the
    directories, no warning.

11. **Backslash.** `!! @"src\*.cs"` → `src/*.cs`, XP110 (as 3.x, now deprecated; in 5.0 the file
    literally named `src*.cs`). `!! @"src\(x)\*.cs"` → directory `src(x)`, files `*.cs`, XP110
    for the second `\` (tm 55, deliberate; 3.x: `src/(x)/*.cs`). To write a literal `*` in 4.0:
    `[*]`.

12. **`!` and `#` are literal.** `!! "!a.txt"` lists the file named `!a.txt`. In `.gitignore` a
    leading `!` re-includes; Xake uses `++`/`--` for that. `#a.txt` likewise.

13. **Repeated group.** Rule `"src/(p:*)/(p:*).fsproj"`: target `src/Core/Core.fsproj` →
    `p = Core`; target `src/Core/Other.fsproj` → no match, and XP116 once: `the group 'p' appears
    2 times; from Xake 4.0 every occurrence must match the same text; Xake 3.x let the last value
    win and matched 'src/Core/Other.fsproj'; ...` (3.x: match, `p = Other`).

14. **`*.*`.** Files `README`, `a.txt`. `!! "*.*"` → `a.txt`, XP114 naming `README` (3.x: both).
    `!! "*" -- "*.*"` → `README` (unchanged: excludes always needed the dot).

15. **Case.** Files `Dir2/e.cs`, `dir/c.cs`; `!! "**/*.cs" -- "d*/**/*"`: Linux keeps `Dir2/e.cs`,
    Windows removes it (pinned, unchanged). `-- "Dir2/**/*"` on Linux removes only `Dir2/...`
    (3.x: also `dir2/...`, literal exclude segments ignored case). macOS `!! "*.CS"` does not find
    `a.cs` (3.x: found it, as the .NET listing does). No warning; section 3 item 24.

16. **From a rule pattern to a mask.** `PatternAst.parse "src/(lib:*)/(name:*).cs" |>
    PatternAst.glob (function "lib" -> Some "core" | _ -> None) (fun _ -> None)` = `src/core/*.cs`;
    with the value `a(b` it is `src/a\(b/*.cs` (values escaped).

17. **A clear error instead of a crash.** Rule `"a(b.txt"` → `Invalid pattern 'a(b.txt' (rule) at
    character 2: '(' is reserved (Xake 3.x: a regex group in rule patterns, a literal character
    in masks); write \( for a literal '(' [XP003]`, raised when the rule is created; inside a
    build reported as a `XakeException` with that message (3.x: `RegexParseException`).

18. **Named group in a mask.** `!! "out/(cfg:*)/*.dll"` → `out/*/*.dll` matched, nothing captured,
    XP111 at parse (3.x: looked for a directory literally named `(cfg:*)` with a wildcard
    segment and found nothing). `-- "(cfg:Debug)/**/*"` removes `Debug/...`, XP111 (3.x: the
    literal directory `(cfg:Debug)`).

## 12. Open items (brief "Open for design" 1–15)

Each entry is self-contained: recommendation, alternatives, and the spec sections that depend on
the decision.

### 1. Exact EBNF of masks and rule patterns

**Recommendation.** One grammar (4.1–4.3) with two contexts: a rule pattern is the grammar with
`group`; a mask is the same grammar in which a group is parsed, matched as its body and warned
about (XP111; an error from 5.0). The lexer resolves escapes (`\` before a reserved character),
the deprecated `\` separator, `*` vs `**` (maximal munch), and recognises `(name:` as one token;
the syntax handles classes, braces (only with a top-level comma), groups and the globstar
position rule. There is one semantics over it (section 2, D1).

Alternatives considered:
- Two grammars (mask, rule) sharing a lexer: simpler to read separately, but the brief asks for
  one grammar with rules as a superset (brief a), and the include/exclude asymmetry came exactly
  from separate encodings.
- Treat `(name:` syntactically (`(` then name then `:`): equivalent, but a lexical token makes
  `(a b:*)` and `(:*)` unambiguous "not a group" without backtracking in the parser.
- Accept `{x}` as a literal (picomatch, and 3.x): friendlier, but closes no syntax and keeps
  `{1..3}` ambiguous later; rejected (XP005, whose text names the 3.x literal reading).
- Make `\` a full escape already in 4.0 (`\*` = literal `*`): see open item 15.

Depends: 4 (all), 7.2 (XP002–XP012), 8.2 (`tryParse`, `Token`).

### 2. Semantics table: rule vs include vs exclude, per element

**Recommendation.** One meaning per element in every context (5.1); the only context differences
are named groups (captured in rules; matched as their body, with XP111, in masks), the trailing
`/` (directories for includes, "everything below" for excludes, an error for rules) and anchoring
(project root vs base dir). The 3.x per-context meanings are not reproduced; table 5.2 lists
each of them with its 4.0 signal (error, warning at parse or on evidence, or release note).

Alternatives considered:
- A Compat level that keeps the 3.x meanings by default (the 2026-10-26 draft): superseded by
  D1; it kept two semantics alive for a whole release line.
- Keep per-context meanings in 4.0 as well (e.g. includes never get classes): contradicts
  "one matcher, the asymmetry disappears" (brief c).
- Switch silently (no warnings, release notes only): least code, but against brief (b)'s
  break-softening principle for `[...]`, `**/` in rules and `dir/**` in masks.

Depends: 5.1, 5.2, 7.3 (which rows warn), section 3.

### 3. Dotfiles

**Recommendation.** Wildcards and classes match a leading `.` (Ant, `.gitignore`, `globset`,
and 3.x in every context). Hidden folders are excluded explicitly with `-- "**/.*/**/*"` or
`-- "**/.*/**"`, which the walker prunes cheaply.

Alternatives considered:
- picomatch / bash style (`*` skips a leading `.` unless the pattern starts with `.`): what
  JavaScript users expect, and it would hide `.git` by default; but it silently drops files that
  `**/*.cs` finds in 3.x (`.hidden/y.cs` in `FilesetTests`), in rules as well as masks.
- A fileset option `dot = false` (opt-in picomatch behaviour): possible later without grammar
  change; not needed for the module.

Depends: 5.3, 6.2 (pruning example), 11 example 4.

### 4. Case sensitivity per OS; case of classes

**Recommendation.** One comparer everywhere, ordinal on Linux and macOS (`Env.isUnix`),
ignore-case on Windows, identical to `File` equality and rule matching; classes and ranges follow
it (`CultureInvariant`). The two 3.x quirks it removes (literal exclude segments insensitive on
every OS; wildcard includes insensitive on macOS through the .NET listing) are 4.0 release-note
entries without a warning. Literal include segments stay file-system probes.

Alternatives considered:
- macOS insensitive everywhere (matches the default APFS volume): friendlier on macOS, but
  rules and `File` equality are ordinal there, so a target and its rule could disagree;
  changing `File` equality is out of scope.
- An evidence warning (XP120: an exclude or include segment that accepts a name only when case
  is ignored): exact, but it needs a second, case-insensitive comparison on every rejected name,
  which is most names in a scan; not worth it for two narrow cases.
- A per-fileset / per-script option `caseSensitive`: flexible, but every cache key and the
  rule matcher would carry it; can be added later as an `ExecOptions` field.
- Detect the file system's sensitivity per directory: correct but slow and surprising across
  machines (a build behaves differently on a case-sensitive APFS volume).

Depends: 5.4, 5.8 (regex options), 9 (cache key), section 3 item 24, 11 example 15.

### 5. `**` edge cases

**Recommendation.** A globstar is `**` forming a whole segment, group delimiters transparent
(4.3); it matches zero or more whole segments in every context; its capture never has a
leading/trailing `/`, `""` for zero; `format` drops one `/` next to an empty globstar; `a/**` =
every file below `a` (not `a`); `a/**/` in an include = `a` and its subdirectories;
segment-local `a**b` = `a*b`. The 3.x differences are 4.0 changes with warnings: rule `**/`
zero segments (XP119 on evidence, when a match took zero directories), rule `a**b` (XP112 at
parse), mask `dir/**` (XP113 on evidence).

Alternatives considered:
- Warn at parse time on every `**/` in a rule: catches every affected rule before a build, but
  `**/` is the ordinary 4.0 spelling and nearly every rule set has one; the warning would be
  noise for new code until 5.0. The evidence warning fires exactly when the widening mattered.
- Keep 1+ segments for rules (a rule-only exception): no rule-selection change, but it
  contradicts brief (c) and every mainstream globber.
- Segment-local `**` as an error (globstar must be a whole segment, as old `globset` did):
  stricter; picomatch and `.gitignore` read it as `*`; XP112 already tells the user to write `*`.
- `a/**` matching `a` itself too (some globbers): useless for files and inconsistent with
  `.gitignore`.

Depends: 4.3, 5.1, 5.2, 5.5, 5.8 (regex), 5.9 (format), 6.2, 7.3 (XP112/XP113/XP119),
11 examples 9–10; hermetic's `dir/**` → `dir/**/*` workaround can go in 4.0.

### 6. What an include segment without wildcards means

**Recommendation.** A probe, not a listing (5.6): a mask with no non-literal element at all is
an explicit name, listed even if absent (3.x rule; needed for `needFiles (!! "x")` to build
missing files); otherwise literal segments are kept if they exist (`Directory.Exists` /
`File.Exists`), with the mask's spelling. Braces are non-literal (matched against existing
names, not expanded bash-style).

Alternatives considered:
- Literal segments matched against a listing (the disk's spelling in the result): uniform with
  wildcard segments and case-exact, but lists every directory on the way (slow for deep literal
  prefixes) and changes the spelling of results on case-insensitive file systems.
- Explicit names filtered by existence too: simpler mental model ("a fileset lists what
  exists"), but breaks `need`-ing a file that a rule will build through a fileset.
- Bash-style brace expansion into explicit names (`src/{a,b}.cs` lists both even if absent):
  handy for `need`, but braces would then mean different things in includes and excludes.

Depends: 5.6, 6.2 steps 1–2, 11 example 4.

### 7. Class and brace rollout timing; the reserved-character signal

**Recommendation.** Both ship in 4.0, in every context, with the module (D1): `[...]` is a class
and `{a,b}` an alternation everywhere. The legacy readings are signalled, not kept: `[abc]` and
`{a,b}` where 3.x read literal text warn on evidence (XP104, a scan meets an entry literally named
so); `[c]` warns at parse (XP105 where the meaning is unchanged, XP106 where 3.x read the literal
text); `[!a]` in rules warns at parse (XP117); `{x}` is an error (XP005); `( ) |` outside a group
are errors (XP003), whose text points `|` inside a group to braces.

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

**Recommendation.** Errors are `PatternException of string` (one type, the prototype's, reused
by the typed layer, tm 64) raised at parse time with the message `Invalid pattern '<text>'
(<context>) at character <n>: <reason> [XPnnn]`; the reason names the 3.x meaning where the
input had one; `tryParse` returns a structured `PatternError` record. Masks are checked at
construction, rule patterns when the rule is created (and again at first match for rules built
from DU cases); inside a build the engine rethrows as `XakeException`. Warnings are about legacy
inputs: build-log level Warning, stable codes `XP1nn`, keyed by `(context, pattern text)`, fired
at parse time when a warning-free 4.0 spelling exists and on evidence otherwise (7.3), buffered
until a log attaches, all removed in 5.0.

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
  mask, but the 3.x meaning named in the text depends on the context, so one of them would be
  wrong.
- Warnings at level Message (prototype): less noise, but users filtering on warnings would miss
  the migration hints.
- No codes: shorter messages; codes cost little and let docs link from a warning to its fix.

Depends: 7 (all), 8.2 (`PatternException`, `PatternError`, `check`), 8.3, 6.2 step 5,
11 example 17.

### 9. Minimal public API, name, namespace; relation to the public `PatternAst`

**Recommendation.** Public module `Xake.PatternAst` in its own file, not auto-opened, holding the
AST types, `PatternException`/`PatternError`, `tryParse`/`parseIn`/`parse`/`check`,
`unparse`/`escape`, `groupNames`/`wildcards`/`specificity`, `format`/`formatMatches`/`glob`
(8.2). The matcher and the regex translation stay internal; matching goes through the existing
`Path.matchGroups`/`Path.matches`/`Fileset` functions, whose signatures do not change.
`Fileset` keeps `Includes of PathMask` in 4.0 (pickled in `.xake`, used by hermetic).

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

**Recommendation.** A depth-first walker driven by the AST (6.2): literal prefix joined without
listing, literal segments probed, one-segment patterns matched against a directory listing,
globstars recursed, braces with `/` expanded into a union; the OS listing filter is only a
superset pre-filter (`*`/`?`/literals passed, `"*"` otherwise), the module decides every name;
subdirectories covered entirely by a later exclude are pruned; ordinal per-directory order,
duplicates removed; globstar recursion does not descend into reparse points; unreadable
directories are skipped with a warning; the evidence checks for XP104/XP113/XP114 run on the
walk.

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
  or warning in the compiler.

Depends: 6 (all), 5.6, 9 (fast paths), section 3 items 25–27.

### 11. Performance: regex compile cache vs an own matcher

**Recommendation.** Regex generated from the AST, cached by `(context, text, anchor, case)`,
`Compiled` for rule and exclude patterns, interpreted for one-segment walker matchers,
run-time-generated masks and the XP116 evidence regex, plus literal / `*` / `*.ext` fast paths
that bypass the regex (9). Measure in Phase 3 with the existing `FilesetTests` benchmark; no
regression on includes, a win on excludes through pruning.

Alternatives considered:
- An own matcher (two-pointer glob with backtracking and captures): no compile cost and no
  ReDoS, but must re-create .NET's greedy capture values, backreferences and case rules exactly
  (brief e: `getRuleMatch` unchanged), and needs its own test corpus.
- Regex without cache: the pre-G8 behaviour, ~1 ms per mask and file; ruled out.
- Always `RegexOptions.Compiled`: fastest matching, but ~1 ms per distinct pattern adds up when
  scripts generate many masks (typed `Glob` in a loop).
- Non-backtracking regex (`RegexOptions.NonBacktracking`): .NET 7+ only, no backreferences;
  Core targets netstandard2.0.

Depends: 9, 5.8, 6.3, 8.2 (nothing regex-shaped is public).

### 12. Compat corpus

**Recommendation.** Pin, as conformance vectors tagged "unchanged" or "changed in 4.0" (with the
3.x result, the signal and the section 3 item): the engine tests that touch patterns
(`PathTests`, `FilesetTests`, `XakeScriptTests` group and multi-target cases, `RuleMatchTests`,
`RuleSpecificityTests`, `ParentDirTargetTests`, `PatternAstTests` regex identity rewritten to
5.8); the prototype's `EscapingTests`/`EscapingDiskTests`/`PatternAstTests` (with C2 adjusted
and the 4.0 warnings added); every pattern/mask in `samples/*.fsx` (not `samples/Research`),
`build.fsx`, `build.fsc.fsx` and the fenced F# blocks in `docs/*.md` (type-checked and, for
masks, run on a fixture tree); hermetic's direct uses (`Path.parse`, `matchesPattern`,
`dir/**/*` workaround, `(ext:dll|exe|nupkg)`). Vectors that change meaning are rewritten, not
deleted, and each maps to a section 3 entry.

Alternatives considered:
- Only the unit tests: cheapest, but the docs and samples are what users copy; the `|`-in-group
  case lives only in hermetic docs/tests.
- Also scan public GitHub scripts using Xake: broader, but out of reach for a conformance suite
  and not reproducible.
- Pin `samples/Research` too: those files carry their own regex experiments
  (`a?/(TAG:*.*)/*.txt/**/+`), not supported usage.

Depends: 10.3, Phase 2 vector format, section 3.

### 13. Migration of `[(]` and `sub[1]`

**Recommendation.** In 4.0 `[c]` is the one-character class, the character `c`, in every context
(valid POSIX glob). Where 3.x meant `c` too (rules, exclude wildcard segments: `[(]*.ss`), the
meaning is unchanged and XP105 at parse suggests `\(` (or plain `c` for an ordinary character),
as tm 55 asks. Where 3.x read the literal text `[c]` (includes, exclude literal segments:
`sub[1]/*.c`), the meaning changes and XP106 at parse names the 3.x reading and both
replacements: `\[1\]` for the brackets, `1` for the character. Both warnings stop in 5.0.

Alternatives considered:
- XP106 on evidence only (a scan meets an entry literally named `sub[1]`): quieter, but `[c]` in
  a mask is almost never 4.0-intended (`1` is shorter), so parse time costs nothing and catches
  masks whose directories do not exist on this machine.
- An error for `[c]` in masks in 4.0: forces the fix, but `[c]` is legal glob and the prototype
  tests already use it with the 4.0 meaning.
- Auto-detect (in an include, use the literal `sub[1]` if it exists, else `sub1`): no surprise
  for either reading, but the result depends on the tree, which breaks determinism.

Depends: 5.2 rows `[c]`/`[...]`, 7.3 XP104–XP106, 10.3 (prototype tests), 11 examples 6–7.

### 14. Named group details

**Recommendation.** Names: a Unicode letter or `_`, then letters, digits, `_`; a leading digit is
an error (XP007; 3.x accepted it). Body: any element, including `/`, globstars, classes, braces
and nested groups. A repeated name in one pattern is a backreference (every occurrence matches
the same text; `src/(p:*)/(p:*).fsproj`), with XP116 on evidence when a target that 3.x's
last-wins reading accepted is rejected. Across outputs of a multi-output rule: the same set of
names in every output (XP013, checked at creation); string rules may also pair bare wildcards by
position. Positional numbers, the `"0"` key and greedy capture values stay as in 3.x (5.7).

Alternatives considered:
- Repeated names as an error: simplest and unambiguous, but the backreference form is a real
  need in build scripts (project folder = project name) and costs nothing with a regex backend.
- Repeated names as independent captures (3.x): keeps the "last wins" surprise; `format` would
  have one value for two possibly different texts.
- A leading digit as a warning in 4.0 (3.x meaning kept): softer, but the collision with the
  positional key is a latent bug in any script that has it.
- ASCII-only names: matches .NET regex group-name rules more tightly, but F# records allow
  Unicode field names and the typed layer maps fields to groups.
- Forbid `/` or globstars inside groups: simpler walker, but `(apl:*/*)` and `(path:bin/**)` are
  pinned by `PathTests`.

Depends: 4.1 (`name`), 4.2 (`group`), 5.7, 5.8 (`\k<n>`), 5.9, 7.2 XP006/XP007/XP013, 7.3 XP116,
11 example 13.

### 15. Version number (brief f) and the `\` separator calendar

**Recommendation.** Version **4.0.0** (D1): the module, the engine migration and the typed layer
ship in the 4.0 line with the variable model; no 3.x release of them. For `\` as a separator:
**warn in 4.0, remove in 5.0**. In 4.0 `\` before a reserved character is an escape (tm 55) and
any other `\` is still a separator with XP110 at parse; in 5.0 `\` also escapes `* ? , ! ^ - \`
and any other `\x` is an error (XP009). Reasons: brief (b) tm 56 already says "still works in
this release, warns; a later major makes `\` an escape only"; masks built with `</>` on Windows
contain `\` (tm 57), so removing it in 4.0 would turn `dir </> "*.cs"` into the literal file
`dir*.cs` — a silent empty result, the worst kind of break; and the warning is exact (parse time,
no noise for new code that writes `/`).

Alternatives considered:
- Remove in 4.0 (`\` escapes `* ? , ! ^ - \`, any other `\x` is XP009): one major sooner, a
  backslash escape for `*`/`?` right away (4.0 needs `[*]`/`[?]`), and no deprecated spelling in
  the grammar; but every Windows script that joins masks with `</>` breaks, partly silently
  (`\*`, `\?` would become literals), and tm 56 would have to be reopened.
- Remove in 4.0 but keep `\` as a separator when the pattern contains no `/` at all (a heuristic
  for `</>`-built masks): fewer breaks, but two meanings of `\` decided by the rest of the text.
- In 5.0 make `\x` = `x` for any character (picomatch): `src\x.cs` would silently become
  `srcx.cs`; the error is safer.

Depends: 2, 4.1 (`legacy_sep`), 5.9 (`escape`/`unparse`), 7.3 XP110, 10.1, 10.2.

## 13. Conflicts and findings that need the user's attention

- **C1 — `[c]` in includes is a plain 4.0 change.** #90 already reads `[c]` as `c` in includes
  (`sub[1]/*.c` finds `sub1`, test "an unescaped sub[1] is the [c] form"); 3.x reads the literal
  text. Under D1 this is the 4.0 meaning, announced by XP106 at parse and section 3 item 9; the
  prototype's tests are ported as they are, plus the warning (open item 13).
- **C2 — warning level.** The prototype attaches pattern warnings at `Level.Message`
  (`ExecCore.fs:388` on the frozen branch); the spec recommends `Level.Warning` (open item 8).
- **C3 — resolved: braces ship in 4.0.** Brief (b)'s rollout ("this release keeps today's
  behaviour for reserved characters") was written for a minor release; with D1 classes and
  braces ship together in 4.0 (open item 7). `(ext:dll|exe|nupkg)` is XP003 in 4.0, so hermetic
  (`Sign.fs` docs, `SignTests.fs`) and `docs/*.md` switch to `(ext:{dll,exe,nupkg})` in the same
  change that ships the module. Nothing left to decide.
- **C4 — rules vs masks on `**`: a plain 4.0 change.** In 3.x a rule's `**/` needs at least one
  directory (`PathTests`: `**/mlib.dll` does not match `c:/mlib.dll` with root `c:/`); masks
  accept zero. One matcher (brief c) makes rules accept zero too; signalled by XP119 when a
  rule match takes zero directories, and by section 3 item 12 (including the rule-selection
  effect, example 9) (open item 5).
- **C5 — a test whose title and assertion disagree.** `FilesetTests` "exec function removes
  duplicates from result" asserts the duplicate count is 2. The spec removes duplicates (6.5);
  the test must change, the user should confirm the intent.
- **C6 — `PatternAst` visibility on `dev`.** It is `internal` inside `Path.fs` today; tm 13 makes
  it public; the prototype moved it to its own file. The spec follows the prototype (8.1).
- **C7 — the `"0"` key.** `getRuleMatches` contains `"0"` (the whole match, from
  `Regex.GetGroupNames`). Not documented, not tested, but "unchanged" (brief e) means it stays;
  an own matcher would have to produce it.
- **C8 — hermetic depends on mask internals.** `Path.parse`, `Path.matchesPattern`,
  `Includes/Excludes of PathMask` and the `dir/**` → `dir/**/*` workaround (`Import.fs`,
  `Lock.fs`, `Locks.fs`). The spec keeps those public shapes in 4.0 (6.1, open item 9); the
  `Xake.Hermetic.Dotnet` Xake range has to admit 4.0 (release-process decision).
- **C9 — observed, out of scope.** `File.fs` `compareNames` passes `ignoreCase = Env.isUnix`
  (case-insensitive on Unix, sensitive on Windows), the inverse of every other comparer; worth a
  separate issue.
- **C10 — OS listing quirks are asserted from .NET documentation and source knowledge, not
  measured here** (`?` matching zero characters, `*.*`, the .NET Framework 3-letter extension
  rule, macOS case-insensitive listing, symlink following under `AllDirectories`). Phase 2 should
  measure them before the release notes state them.
- **C11 — named group in a mask, unclosed group in a string rule: warning/silent or error in
  4.0?** The brief says a named group in a mask warns in this release and is an error in a
  later major (tm 58), and an unclosed group in a string rule stays silent (tm 61, the typed
  layer rejects it). Both were written for a minor release; since 4.0 may break, either could be
  an error already. Option A (the brief, what the body specifies): XP111 at parse and the group
  matched as its body in 4.0, error XP008 in 5.0; unclosed `(name:` in a string rule runs to the
  end silently in 4.0 and becomes XP006 in 5.0. Option B: XP008 and XP006 are errors in 4.0 (one
  less warning, one less legacy grammar path, string and typed rules agree; but scripts with
  either spelling stop loading, and the 3.x meaning of a group in an include, literal text, was
  rarely what the author meant anyway). The user decides; option B changes 4.2, 5.1, 5.2, 7.2,
  7.3, 10.1, section 3 item 17 and example 18.
