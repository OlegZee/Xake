# Spec: the pattern module (draft for review)

Status: **draft**, written 2026-10-26 from `brief.md` (sections a–f are decisions and are not
reopened here), the frozen prototype `feature/typed-matches` (`src/core/{PatternAst,Path,Fileset}.fs`,
`src/tests/{EscapingTests,EscapingDiskTests,PatternAstTests}.fs`, `escaping-today.md`) and the
current engine on `dev` (`src/core/Path.fs`, `Fileset.fs`, `ExecCore.fs`). The 15 "Open for
design" items of the brief are answered in section 11, each with a recommendation and the
alternatives; the body of the spec is written as if every recommendation were accepted, and
each open item lists which body sections change if the user decides otherwise.

Conventions: "rule pattern" = the text of `..>`, `*..>`, `=>`, `target`, `targets`, `command`,
`phony` and of the typed layer. "Mask" = the text of `!!`, `ls`, `++`, `--`, `+`, `-`,
`fileset { includes / excludes / includefile / excludefile }`, `cp`/`rm` masks. "Include" and
"exclude" are the two mask roles. "Today" = `dev` at `d002fe4` (3.12.0). "Prototype" = the
frozen branch (#90/#91).

---

## 1. Scope and goals

In scope:

- One grammar for rule patterns and masks (masks are the subset without named groups).
- One parser producing one public AST (`Xake.PatternAst`), and one matcher derived from it
  that serves rules, includes and excludes (brief c).
- Generation from the AST: `format` (replaces `ExecCore.applyWildcards`), `glob` (rule pattern
  to mask), `unparse`, `escape`.
- How `Fileset` walks directories with that matcher.
- An error and warning model, a migration path for legacy spellings, and a release calendar.

Out of scope: the typed layer (`Xake.Typed`, brief d; it only needs the API in section 7),
hermetic path functions (tm 9), "rules with arguments" (tm 11), the phony `=>` phase-out
mechanics (brief e) beyond what the grammar needs.

Goals, in priority order:

1. **No silent change of meaning** for anything a 3.x script can write today (brief b,
   "break-softening principle"). Every change in meaning is either a bug fix listed in the
   release notes, or announced by a warning one release before it happens.
2. **Mainstream glob as the target** (brief a): a reader who knows `.gitignore`, Rust `globset`
   or `picomatch` can read a Xake mask; where Xake differs it is on purpose and documented
   (section 10 lists the differences).
3. **One implementation**: the three encodings that exist today (`PatternAst` regex for rules
   and wildcard exclude segments, `Path.parse` + OS listing for includes, the `applyWildcards`
   regexes for multi-file names) collapse into one AST and one matcher.
4. `getRuleMatch` / `getRuleMatches` return exactly what they return today for every pattern
   that is valid today (brief e).

## 2. Two semantics levels: Compat and Standard

Goal 1 and goal 2 conflict for a handful of spellings (`[abc]` in an include is a literal today
and a class in mainstream glob; `**/` needs at least one directory in a rule today and zero in
mainstream glob; ...). The spec resolves this by defining **two semantics levels over the same
grammar and the same AST**:

| level | default in | what it is |
|---|---|---|
| **Compat** | this release (3.13.x, see 9.3) | today's meaning of every spelling, per context; the spellings whose meaning will change are reported once per pattern (section 6); today's crashes (`RegexParseException`) become clear errors; listed OS-listing quirks are dropped (9.4) |
| **Standard** | the next major (4.0.0) | mainstream glob plus named groups; one meaning in all contexts; every Compat warning is an error or the new meaning |

The level is process-wide, read once from the environment variable `XAKE_PATTERNS`
(`compat` | `standard`, default `compat` in 3.x), and can be set by a script before its first
pattern is used: `PatternAst.Semantics.set PatternAst.Standard` (7.2). Standard is therefore
available as an opt-in in 3.13 so that users can migrate early and the conformance tests run
both levels. Compat is removed in 4.0 (open item 15).

Everything the two levels share is described once; the differences are in one table (4.2).

## 3. Grammar

### 3.1 Lexical rules (characters to tokens)

The lexer reads the pattern text left to right. It is the only place where escapes are resolved
and where `*` and `**` are told apart. Its output is a token stream; whether a token is legal
where it appears is decided by the syntax (3.2) and by the context (rule / include / exclude).

```ebnf
(* lexical *)
token        = escape | dstar | star | qmark | slash
             | group_open | lparen | rparen | pipe
             | lbrack | rbrack | lbrace | rbrace | comma
             | char ;

escape       = "\" , escapable ;                    (* the escaped character, as a literal *)
escapable    = "*" | "?" | "[" | "]" | "{" | "}" | "(" | ")" | "|" | "," | "!" | "^" | "-" | "\" ;
dstar        = "**" ;                               (* maximal munch: "***" = dstar, star *)
star         = "*" ;
qmark        = "?" ;
slash        = "/" ;
group_open   = "(" , name , ":" ;                   (* one token, longest match *)
name         = name_start , { name_char } ;
name_start   = letter | "_" ;                       (* Standard; Compat also takes a digit, XP115 *)
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

- **`\` is the escape character** (tm 55). In both levels `\` followed by a *reserved* character
  `( ) [ ] { } |` is that character as a literal. The remaining escapable characters
  (`* ? , ! ^ - \`) are escapes in **Standard** only. In **Compat**, a `\` that is not followed by
  a reserved character is a path separator, exactly as today, and is reported (XP110); this
  includes `\*`, `\?` and `\\`, which today mean `/*`, `/?` and `//`.
- In Standard a `\` before any character outside `escapable`, or at the end of the pattern, is an
  error (XP009), not "the character itself" as in picomatch. Reason: `src\x.cs` must not silently
  become `srcx.cs` when the Compat separator meaning goes away (9.2).
- `**` is one token wherever it appears; whether it is a *globstar* (crosses `/`, zero or more
  segments) or a *segment-local* double star is decided by the position rule in 3.3.
- `,` is a token only directly inside a brace pair; elsewhere it is a `char`.
- `!` is special only as the first character inside a class (`[!a]`); elsewhere it is a `char`.
  A leading `!` in a mask is a literal file-name character, **not** a negation as in
  `.gitignore` (section 10, example 12). `#` is never special.
- F# string literals: `"a\(b"`, `@"a\(b"` and `"""a\(b"""` are the same text, because F# keeps an
  unknown escape `\(` as two characters (checked by the prototype, `EscapingDiskTests`
  "the F# literal forms ... are the same text"). The same holds for every character in
  `escapable` except `\` itself: `"a\\b"` is `a\b`. Docs recommend `@"..."` for any pattern
  with `\`.

### 3.2 Syntax (tokens to AST)

```ebnf
(* syntactic; [ ] optional, { } repetition *)
pattern      = [ root ] , body ;
root         = slash | drive , slash ;               (* an absolute pattern; not joined to a base *)
drive        = letter , ":" ;                        (* recognised only at the very start *)
body         = { element } ;

element      = literal | star | qmark | dstar | slash
             | class | braces | group               (* group: rule context only *)
             | legacy ;                             (* Compat only, see 4.2 *)

literal      = ( char | escape | comma_outside ) , { char | escape | comma_outside } ;

class        = lbrack , [ negation ] , class_items , rbrack ;
negation     = "!" | "^" ;
class_items  = class_item , { class_item } ;        (* a "]" right after "[", "[!" or "[^" is a literal *)
class_item   = class_char , [ "-" , class_char ] ;   (* a range; "-" first or last is a literal *)
class_char   = ? any character except "/" and an unescaped "]" ? | escape ;

braces       = lbrace , alternative , comma , alternative , { comma , alternative } , rbrace ;
alternative  = { element } ;                        (* may be empty; may contain "/" and nested braces *)

group        = group_open , group_body , rparen ;
group_body   = { element } ;                        (* any element, nested groups included *)

legacy       = lparen | rparen | pipe | lbrack | rbrack  (* an unescaped reserved character  *)
             | lbrace | rbrace ;                         (* that is no class/braces/group     *)
```

Syntactic decisions:

- **One grammar, two contexts.** A rule pattern may contain `group`; a mask may not. In a mask
  a `group_open` is parsed as a group (so that the error or warning can name it) and then
  rejected (Standard, XP008) or given its Compat meaning with a warning (XP111) (tm 58).
- **Braces need a top-level comma.** `{a,b}` is an alternation; `{x}` (no comma) is not braces:
  in Compat it is the literal text `{x}` with a warning (XP108), in Standard an error (XP005),
  which keeps `{1..3}` ranges open for later. picomatch reads `{x}` as a literal; Xake rejects it
  to keep the syntax open (brief b).
- **Classes never match `/`.** A `/` inside `[...]` is an error (XP010); a negated class
  excludes `/` implicitly. Empty classes `[]`, reversed ranges `[z-a]` and POSIX names
  `[[:alpha:]]` are errors (XP010).
- `( | )` outside `group` stay **reserved** (tm 60): Compat gives them today's meaning with a
  warning, Standard rejects them (XP003).
- A group's closing `)` is the first unescaped `)` at the group's nesting level. An unclosed
  group runs to the end of the pattern in Compat **without a warning** (tm 61); in Standard it
  is an error (XP006), as the typed layer already does.
- Root: a leading `/` or `X:/` (and in Compat `\`, `X:\`) makes the pattern absolute. A pattern
  is otherwise relative to its anchor: the project root for rule patterns (phony patterns: the
  empty root), the fileset base directory for masks (4.1, "anchoring").
- `.` and `..` segments: in masks and rule patterns they are resolved in the **literal prefix**
  (the segments before the first non-literal element), as `Path.normalizeLiteralPrefix` does
  today. After a wildcard, `..` is dropped silently in Compat masks (today's
  `Recurse::Parent` rule; reported, XP118) and an error in Standard (XP012).

### 3.3 Position rule for `**`

A `dstar` token is a **globstar** when, ignoring `group_open` and its `rparen` (group delimiters
are transparent), it is preceded by the start of the pattern or a `/`, and followed by the end
of the pattern or a `/`. Examples of globstars: `**`, `**/b`, `a/**`, `a/**/b`, `(p:**)/b`,
`(path:bin/**)/file.o`, `a/(p:**)`. Every other `dstar` is **segment-local** (`a**b`, `**.cs`,
`x/**y`, `(p:a**)`); its meaning differs by level and context (4.1, 4.2, XP112).

### 3.4 The AST

```fsharp
type ClassItem =
    | One of char
    | Range of char * char

type Token =
    | Lit of string                      // literal text, escapes resolved, no '/'
    | Sep                                // '/'
    | Star                               // '*'
    | AnyChar                            // '?'
    | Globstar                           // '**' as a whole segment (3.3)
    | DoubleStar                         // '**' inside a segment (deprecated spelling)
    | Class of negated: bool * items: ClassItem list
    | Alt of Token list list             // {a,b,...}; each alternative is a token list
    | Group of name: string * body: Token list
    | Reserved of char                   // Compat only: an unescaped ( ) [ ] { } | with its legacy meaning
```

Differences from the prototype's `Token` (`Lit | Star | DoubleStar | AnyChar | Group | Reserved`):
`Sep` is split out of `Lit` (segment boundaries are needed by the walker and the globstar rule),
`Globstar` is distinguished from `DoubleStar`, `Class` and `Alt` are added. The `Lit` text is the
resolved text (prototype too). The Compat `[c]` spelling parses to `Lit "c"` in rule context and
to `Lit "[c]"` in an include (4.2), so it needs no token of its own; its warning is an `Issue`.

## 4. Semantics

### 4.1 Standard semantics (4.0 target; opt-in in 3.13)

One meaning in every context, except where a column says otherwise.

| element | rule pattern | include | exclude |
|---|---|---|---|
| literal | matches itself (case per 4.4) | an explicit name (4.6) | matches itself |
| `*` | 0+ characters other than `/`, leading `.` included (4.3) | same, per segment | same |
| `?` | exactly 1 character other than `/` | same | same |
| `**` globstar | 0+ whole segments (4.5) | same; walked recursively (5) | same |
| `**` segment-local (`a**b`) | same as `*` | same as `*` | same as `*` |
| `[abc]` `[a-z]` | one character from the set (case per 4.4) | same | same |
| `[!abc]` `[^abc]` | one character not in the set and not `/` | same | same |
| `{a,b}` | one of the alternatives; alternatives may hold `/`, wildcards, nested braces | same; walked as the union (5.3) | same |
| `{x}` (no comma) | error XP005 | error XP005 | error XP005 |
| `(name:body)` | named capture of what `body` matches (4.7) | error XP008 | error XP008 |
| `( ) \|` outside a group | error XP003 | error XP003 | error XP003 |
| `\c` | the literal `c` for `c` in `escapable`; otherwise error XP009 | same | same |
| `/` | separator | separator | separator |
| `\` as separator | not a separator (it is the escape) | same | same |
| trailing `/` | error XP011 (a rule names a file or a phony name) | directories only (4.5) | the directory and everything below it |
| leading `/`, `X:/` | absolute: the project root is not joined | absolute: the base dir is not joined | same |
| anchoring | whole target path, joined to the project root (phony: to `""`) | from the base dir | whole path, joined to the base dir |

"Anchoring" is where Xake deliberately differs from `.gitignore`: a mask without `/` matches only
directly in its base directory, it does not float to any depth (example 5).

### 4.2 Compat semantics (this release, default) and what changes in Standard

Rows are spellings whose meaning differs between Compat and Standard or between contexts in
Compat. Everything else behaves as in 4.1. "Rule" here also covers phony/command patterns. The
last column is the warning (6.3) that announces the change; "—" means no warning (no change for
valid input, or a release-note-only change).

| spelling | Compat: rule pattern | Compat: include | Compat: exclude | Standard | warning |
|---|---|---|---|---|---|
| `**/b`, `a/**/b` (globstar) | **1+** segments (regex `(.*)/`, as today) | 0+ segments | 0+ segments | 0+ everywhere | — (release note: rules widen) |
| `a/**` at the end | 1+ characters below `a` | lists the **directories** below `a` (and `a`) | matches no file | every file below `a` | XP113 (masks) |
| `a**b` (segment-local) | crosses `/` (regex `(.*)`) | like `*` | like `*` | like `*` | XP112 (rules only) |
| `[abc]`, `[a-z]`, `[^a]` | class (regex), same as Standard | **literal text** `[abc]` | segment with `*`/`?`: class; segment without: literal text | class | XP104 (include, exclude literal segment); — (rule, exclude wildcard segment) |
| `[!a]` | class of `!` and `a` (regex) | literal text | as `[abc]` | negated class | XP117 |
| `[c]` (one character) | the literal `c` | **literal text** `[c]` | segment with `*`/`?`: `c`; without: literal text `[c]` | the literal `c` (a 1-char class) | XP105 (rule: "write `\c`"), XP106 (masks) |
| unbalanced `[` | error XP002 | literal | wildcard segment: error XP002; literal segment: literal | error XP002 | — |
| `{a,b}` | **alternation** (new, see open item 7) | alternation | alternation | alternation | — |
| `{x}` | literal `{x}` | literal | literal | error XP005 | XP108 |
| `(` `)` (not a group) | regex group: a **numbered capture** (takes a positional number) | literal | wildcard segment: regex group; literal segment: literal | error XP003 | XP101 (rule), XP103 (masks) |
| lone `(` or `)` | error XP001 (today `RegexParseException`) | literal | wildcard segment: error XP001 | error XP003 | — |
| `\|` | regex alternation over the whole pattern, or over the enclosing group body | literal | wildcard segment: regex alternation; literal segment: literal | error XP003 | XP101 / XP109 (inside a group: suggests braces) |
| `(name:body)` | named capture | literal text (finds nothing with a wildcard) | wildcard segment: matches `body`; literal segment: literal | error XP008 in masks | XP111 (masks) |
| unclosed `(name:` | runs to the end, silently (tm 61) | literal text | as rule in a wildcard segment | error XP006 | — |
| group name `(1:*)` | a group named `1`, colliding with positional `1` | literal | as rule | error XP007 | XP115 |
| repeated name `(n:*)/(n:*)` | both capture; the **last** value wins, values may differ | — | — | **backreference**: every occurrence matches the same text | XP116 |
| `\` + reserved char | escape | escape | escape | escape | — |
| `\` + other char | path separator | separator | separator | escape (`\*` = literal `*`) or error XP009 | XP110 |
| `*.*` as a whole segment | needs a `.` | **any name** (OS listing rule, emulated) | needs a `.` | needs a `.` | XP114 (include) |
| `..` after a wildcard | literal (no file has it) | dropped | dropped | error XP012 | XP118 (masks) |
| trailing `/` | matches nothing | directories only | ignored (same as without) | 4.1 | — |
| case of a literal segment | per OS (4.4) | as the file system resolves it | **always insensitive** | per OS | — (release note) |
| case of a wildcard segment | per OS | per OS listing (Windows and macOS insensitive) | per OS | per OS | — (release note) |

Notes:

- Compat keeps the include meaning of `[...]`, `[c]` and `( ) |` as **literal text**, as today.
  The prototype (#90) changed `[c]` in includes to the character `c` (`sub[1]/*.c` found `sub1`
  instead of the directory `sub[1]`), which is a silent change of meaning that the brief's
  break-softening principle rules out; this spec does **not** take that over (see 12, conflict C1).
- In Compat, the meaning of a reserved character in an exclude still depends on whether its
  segment has `*` or `?`. This is today's behaviour, it is ugly, and it disappears in Standard.
- `{a,b}` is active in Compat as well (open item 7). Today `{` and `}` are literal everywhere,
  so this changes the meaning only of names that contain `{`, a `,` and `}` together; it is
  listed in the release notes, and it gives the `(ext:dll|exe|nupkg)` users a warning-free
  replacement in the same release.

### 4.3 Dotfiles

`*`, `?`, `**` and classes match a leading `.` in every context and level. `**/*.cs` lists
`.hidden/y.cs`; excluding dot folders is written explicitly: `-- "**/.*/**"` (Standard) or
`-- "**/.*/**/*"` (both levels; today's idiom, pinned by `FilesetTests`). This is today's
behaviour (Ant, `.gitignore`, Rust `globset`); it differs from picomatch / bash (`dot: false`).
The directory entries `.` and `..` are never produced by a listing.

### 4.4 Case sensitivity

- **Standard:** one comparer for all contexts: ordinal on Linux and macOS (`Env.isUnix`),
  ordinal-ignore-case on Windows. It is the comparer `File` equality and rule matching already
  use (`Types.fs`, `Fingerprints.fs`). Classes and ranges follow it: on Windows `[a-z]` matches
  `Q`. Regex options: `RegexOptions.CultureInvariant`, plus `IgnoreCase` on Windows.
- A literal segment of an include is a probe of the file system (4.6): on a case-insensitive
  file system (Windows, default macOS) it finds the file whatever the case, and the result keeps
  the mask's spelling. This is a property of the file system, not of the matcher.
- **Compat** keeps today's per-context behaviour (table 4.2): rules per OS; wildcard segments of
  includes as the .NET listing did (`MatchCasing.PlatformDefault`: insensitive on Windows *and
  macOS*); literal exclude segments insensitive on every OS (`Path.matchImpl.eq`); wildcard
  exclude segments per OS.
- Changes from Compat to Standard (release notes 4.0): on macOS a wildcard include becomes case
  sensitive (`!! "*.CS"` no longer finds `a.cs`); on Linux and macOS an exclude literal segment
  becomes case sensitive (`-- "Bin/**/*"` no longer removes `bin/...`).

### 4.5 `**` edge cases

Standard (all contexts unless stated):

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
`""`. Formatting (4.9) is the inverse: a globstar slot with value `""` drops one adjacent `/`, so
`a/**/b` formats to `a/b`, not `a//b`.

Compat differences: in rule patterns a globstar between or before slashes needs at least one
segment (`**/mlib.dll` does not match `mlib.dll` at the root: `PathTests` pins it); `a/**` in a
rule matches one or more characters below `a`; segment-local `a**b` in a rule crosses `/`; in an
include `a/**` lists directories, in an exclude it matches nothing (`FilesetTests` pins it).

### 4.6 Literal segments in includes

A mask segment without any non-literal element is a **probe**, not a listing:

- If the whole mask has no non-literal element (no `* ? ** [..] {..}`), it is an **explicit
  name**: it is listed whether or not the file exists (today's rule, `Fileset.listFiles`
  `isExplicitRule`). This is what lets `needFiles (!! "out/x.dll")` build a missing file.
- Otherwise a literal directory segment is kept if `Directory.Exists`, a literal last segment if
  `File.Exists` (or `Directory.Exists` for a trailing `/`). No listing is done, so a literal
  segment costs one `stat`, and the path keeps the mask's spelling, not the disk's.
- Braces count as non-literal (Standard and Compat): `src/{a,b}.cs` lists only the existing ones.
  Brace expansion in the bash sense (both names listed regardless) is not done (open item 6).

### 4.7 Named groups and captures

- **Names**: `name_start name_char*` with Unicode letters/digits allowed (F# record fields can be
  Unicode). Compat also accepts a leading digit with a warning (XP115). A `(` followed by
  anything else (`(a-b:*)`, `(a b:*)`, `(:*)`) is not a group: in Compat the `(` is reserved
  (today's regex group), in Standard an error.
- **Body**: any element: wildcards, globstars, classes, braces, `/`, nested groups
  (`(name:(plat:*)-a.ss)` and `(root:*/*-a).ss` are pinned by `XakeScriptTests`).
- **Repeated name** in one pattern: Standard = backreference (all occurrences match the same
  text; `src/(p:*)/(p:*).fsproj` matches `src/Core/Core.fsproj` with `p = Core`, not
  `src/Core/Other.fsproj`). Compat = today's .NET regex rule (each occurrence captures, the last
  one wins), with XP116. The wildcards inside a repeated occurrence get no positional numbers.
- **Multi-output** (`*..>`, `targets`): every output has the same set of named groups, checked
  when the rule is created (tm 50; prototype `checkSameGroups`, error XP013). Bare wildcards stay
  allowed in string multi-output rules and pair **by position** (`["dd/*.dll"; "dd/*.xml"]`, as
  today); the typed layer keeps rejecting them (its own rule). A class or brace outside a group in
  a multi-output pattern is an error in Standard (XP014): the other outputs could not be named.
- **What `getRuleMatches` holds** (must not change, brief e):
  - every named group by name;
  - every *unnamed* capture by its number `"1"`, `"2"`, ... in left-to-right order **at any depth**:
    each `*`, `**`, `?` is one, including those inside named groups (`gen/*/(name:*)-?.txt`:
    `"1"` = the first `*`, `"2"` = the group's own `*`, `"3"` = `?`); in Compat each reserved `(`
    outside a class is one too (`lit(x)/*.txt`: the `*` is `"2"`);
  - classes and braces take **no** number (they are non-capturing); wildcards inside braces are
    numbered in text order, and those of an alternative that did not match hold `""`;
  - `"0"`, the whole matched path (root included), a by-product of .NET regex numbering that
    exists today and is kept.
  - Values are **greedy, leftmost**: `(a:*)-(b:*)` on `x-y-z` gives `a = x-y`, `b = z`, as the
    .NET regex gives today. A matcher that is not regex-based must reproduce this (8).
- `getRuleMatch` of an unknown key returns `""` (tm 7).

### 4.8 Regex equivalents

The matcher is the AST translated to a .NET regex (section 8 explains why). The translation is
part of the spec so that conformance tests can pin it.

Standard (`t` = translate):

| token | regex |
|---|---|
| `Lit s` | `Regex.Escape`-equivalent of `s` (all of `. $ ^ + ! = { } ( ) [ ] \| \ * ?` escaped) |
| `Sep` | `/` |
| `Star`, segment-local `DoubleStar` | `([^/]*)` |
| `AnyChar` | `([^/])` |
| `Globstar` at the start, followed by `/` | `(?:(.*)/)?` (the `/` is consumed) |
| `Globstar` between `/` and `/` | `(?:(.*)/)?` (after the preceding `/`) |
| `Globstar` at the end, after `/` | `(.*)` |
| `Globstar` alone | `(.*)` |
| `Class (false, items)` | `[items]` |
| `Class (true, items)` | `[^/items]` |
| `Alt alts` | `(?:t a1\|t a2\|...)` |
| `Group (n, body)`, first occurrence | `(?<n>t body)` |
| `Group (n, _)`, later occurrence | `\k<n>` |

Anchoring: `^` + escaped anchor (project root, base dir; `..`/`.` collapsed in the literal
prefix) + `/` + body + `$`, options `CultureInvariant` (+ `IgnoreCase` on Windows).

Example: `src/**/(name:*).fs` under root `/p` →
`^/p/src/(?:(.*)/)?(?<name>([^/]*))\.fs$`.

Compat: the prototype's `toRegex` (byte-identical to today's for valid patterns, pinned by
`PatternAstTests` "AST regex matches the former substitution byte for byte"): `*` →
`([^/\\]*)`, `?` → `([^/\\])`, every `**` → `(.*)`, `Reserved c` → `c` raw, `[c]` → the
escaped literal `c`, `{a,b}` → `(?:a|b)` (new). The project root is escaped (prototype; today it
is not: bug fix, 9.4).

### 4.9 Generation: `format`, `formatMatches`, `glob`

- `format named positional tokens`: a named group becomes `named name`; the i-th **top-level**
  bare wildcard (1-based, wildcards inside groups do not count) becomes `positional i`;
  literals and `Sep` print as text with escapes resolved. A pattern made only of literals formats
  to its text (tm 14). A globstar slot with `""` drops one adjacent `/` (4.5). A `Class` or `Alt`
  outside a group cannot be formatted: Standard raises XP014; Compat prints the source text (as
  `applyWildcards` does today for `[`).
- `formatMatches (matches: Map<string,string>) (pattern: string)`: `format` fed from a rule match:
  named groups by name, the i-th top-level bare wildcard by the regex number it is captured
  under in the **pattern being formatted** (`wildcards` computes it). A missing key gives `""`
  (as `getRuleMatch`). This replaces `ExecCore.applyWildcards` (tm 14) and fixes its two bugs
  (brief c): an empty group value left `(lib:)` in the name; a `)` in a value broke the name.
- `glob named positional tokens`: a mask for partly known values: a bound group or wildcard
  becomes its value **escaped**; an unbound group becomes its own body (recursively); an unbound
  wildcard stays. The result has no named groups, so it is a valid mask in both levels.
  `glob` of `src/(dir:*)/**/*.fs` with `dir = core` is `src/core/**/*.fs`.
- `unparse tokens` is the inverse of `parse`: reserved characters in literals escaped, `/` as
  separator. `escape s` puts `\` before every reserved character (and, in Standard, before every
  `escapable` character) so that `parse (escape s)` is `[Lit s]` per segment.

### 4.10 Specificity

Unchanged formula (`Path.Specificity`, #88/#89): `(literal characters, -globstars, -wildcard
weight)`. New tokens: `Class` adds wildcard weight 1 (like `?`); `Alt` contributes the
specificity of its **least specific** alternative; `Globstar` and `DoubleStar` count as `**`
(the latter only in Compat, where it crosses `/`); `Sep` counts as one literal character (as `/`
inside `Lit` does today); `Reserved` counts as one literal (prototype). Specificity is computed
from the AST, never from text.

## 5. Fileset scanning

### 5.1 What stays

`Fileset`, `FilesetElement = Includes of PathMask | Excludes of PathMask`, `FilesetOptions`, the
operators and the builder keep their public shapes in 3.x. `Path.Part` values keep the
**segment text** of the mask (escapes kept, as the prototype does) so that the pickled
`GetFiles` dependency in `.xake` keeps its format (DB version stays `0.5`). The module parses each
part's text at scan time. Hermetic code that builds `Includes (Path.parse m)` keeps compiling.
In 4.0 the element may carry the mask text itself (and the DB version moves), see open item 9.

`Path.parse` / `parseDir` split at `/` but **not inside `[...]` or `{...}`** (today `[a/b]*.c`
splits into `[a` and `b]*.c`); in Compat they also split at a `\` that escapes nothing (XP110).

### 5.2 Algorithm

`toFileList` folds the elements in order, as today: an include appends what it finds, an
exclude removes from what was found so far. Excludes apply only to earlier includes (pinned by
`FilesetTests` "keeps the order of includes and excludes").

An include is walked from its anchor (base dir, or the root of an absolute mask):

1. The **literal prefix** (segments before the first non-literal one, `.`/`..` resolved) is
   joined without listing.
2. For each following segment:
   - literal: probe (4.6);
   - one-segment pattern (`*`, `?`, class, literal parts, braces without `/`): list the
     directory (subdirectories for a middle segment, files for the last one; both for a trailing
     `/` that asks for directories), and keep the names the segment's matcher accepts;
   - globstar: apply the rest of the pattern at this directory (zero segments) and then recurse
     into each subdirectory, depth first;
   - braces holding `/` (`{src,test/unit}/*.cs`): the mask is expanded into its alternatives
     before walking and the union is walked (each alternative with its own literal prefix).
3. **Pruning**: a subdirectory is not entered when an exclude that comes *after* this include
   covers it entirely, i.e. the exclude is `P/**` (Standard) or `P/**/*` (both levels) and `P`
   matches the subdirectory's path. Sound because a file removed by a later exclude cannot be
   brought back by this include (a later include brings it back by its own walk). This is what
   makes `!! "**/*.cs" -- "**/.*/**/*" -- "bin/**/*"` skip `.git`, `bin` without reading them.
   Pruning is an optimisation only: the result is the same without it.
4. An exclude is a whole-path match: the exclude mask is anchored at the base dir (4.8) and
   matched against each found file's full path (normalized to `/`).

### 5.3 The OS listing's role

The .NET listing (`Directory.EnumerateFiles/Directories(dir, filter)`) knows only `*` and `?`,
with Win32 quirks (`*.*` = `*`; `?` can match zero characters before `.` or the end; on .NET
Framework a 3-letter extension also matches longer ones, `*.rdl` finds `c.rdlx`). Therefore:

- the module's matcher decides every name; the OS filter is only a **pre-filter** that may return
  a superset;
- the filter passed is the segment with escapes resolved when the segment holds only literals,
  `*` and `?`, and `"*"` otherwise (classes, braces, Compat reserved characters);
- the Win32 quirks are thus dropped, except `*.*` = any name, which Compat keeps for includes
  (XP114) because `cp`'s own `Dir` mode and samples (`paket-files/*.*`) rely on it. The `cp`
  task's `dirMask </> "**/*.*"` becomes `dirMask + "/**/*"` (built with `/`, prototype did the same).

### 5.4 Symlinks and junctions

- Literal segments and one-segment patterns go through a symlinked directory as the OS resolves
  it (both levels).
- Globstar recursion: **Compat** follows directory symlinks as today's
  `SearchOption.AllDirectories` does; **Standard** does not descend into a directory whose
  attributes include `ReparsePoint` (symlinks, junctions), which prevents cycles without an API
  that netstandard2.0 lacks (`LinkTarget`). A link can still be reached explicitly
  (`node_modules/x/**`). Open item 10.

### 5.5 Ordering, duplicates, determinism

- Within one directory, entries are processed in **ordinal order** of their names
  (`StringComparer.Ordinal`, on every OS), files of a directory before its subdirectories
  (pre-order, as `find`). The include's results come out in that order; includes are
  concatenated in declaration order. Today the order is whatever the file system enumerates
  (alphabetical on NTFS, hash order on ext4), which matters for F# compile order.
- Duplicates (the same full path from two includes, or `**/**`) are kept once, at the first
  occurrence, compared with the OS comparer (4.4). Today they are kept twice (see 12, C5: the
  `FilesetTests` case titled "removes duplicates" asserts 2).
- The result is a function of the file tree and the fileset only: no dependence on enumeration
  order, thread timing or caches. `compareFileList` (incremental check) compares sets, so ordering
  never triggers a rebuild.
- An unreadable directory met during a walk is skipped and reported once per directory as a
  warning (today an `UnauthorizedAccessException` ends the build). A missing base directory
  gives an empty list (`failonempty` turns it into an error).

## 6. Error and warning model

### 6.1 What throws, what warns

- **Errors** (exception, section 6.2): input the matcher cannot give a meaning to, or that
  Standard rejects. Raised when the pattern is **parsed**:
  - masks: at construction (`!!`, `ls`, `++`, `--`, the builder's operations), i.e. usually when
    the script is loaded;
  - rule patterns: when the rule is created by `..>`, `*..>`, `=>`, `target`, `targets`,
    `command`, `phony` (eager check), and again (cached) when the engine first matches with it,
    for rules built from the `Rule` DU cases directly;
  - inside a build, the engine reports a `PatternException` as a `XakeException` with the same
    message (prototype `ExecCore`), so the build fails like any other error.
- **Warnings** (section 6.3): input that works today and whose meaning changes in 4.0, or that is
  deprecated. Never an exception, never a change of behaviour in Compat.

### 6.2 Exceptions and errors

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
      Code: string                      // "XP001" ...
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
is what the prototype's tests assert.

| code | level | condition | reason text |
|---|---|---|---|
| XP001 | both | Compat regex context: a `(` without `)` or a `)` without `(` | `an unescaped '(' has no matching ')'; write \( for a literal '('` (or the `)` form) |
| XP002 | both | a `[` without `]` (Compat: regex contexts only) | `an unescaped '[' has no matching ']'; write \[ for a literal '['` |
| XP003 | Standard | `(`, `)` or `\|` outside a named group | `'(' is reserved; write \( for a literal '('` |
| XP004 | Standard | `{` without `}`, a stray `}` or `]` | `an unescaped '{' has no matching '}'; write \{ for a literal '{'` |
| XP005 | Standard | `{...}` without a top-level `,` | `'{x}' is not an alternation (no ','); write \{x\} for literal braces` |
| XP006 | Standard | unclosed `(name:` | `the group '(name:' is not closed` |
| XP007 | Standard | a group name starting with a digit | `the group name '1' starts with a digit; it would collide with the positional match '1'` |
| XP008 | Standard | a named group in a mask | `named groups exist only in rule patterns; write \( \) for literal parentheses` |
| XP009 | Standard | `\` at the end, or before a character that is not escapable | `'\x' escapes nothing; use '/' as the separator, '\\' for a backslash` |
| XP010 | both | `[]`, `[z-a]`, `/` in a class, `[[:` | `invalid character class '[...]': <detail>` |
| XP011 | Standard | a rule pattern ending in `/` | `a rule pattern names a file or a phony target and cannot end with '/'` |
| XP012 | Standard | `..` after a wildcard | `'..' is allowed only before the first wildcard` |
| XP013 | both | multi-output rule, outputs with different named groups | prototype text: `output "<p>" has groups [..], the first output [..] (missing [..], extra [..]); every output needs the same named groups, or the others cannot be named from a match` |
| XP014 | Standard | a class or braces outside a group in a pattern that must be formatted (multi-output, `format`) | `'{dll,pdb}' cannot be filled in from a match; put it in a named group: (ext:{dll,pdb})` |

### 6.3 Warnings (Compat)

Sent to the build log at level **Warning** (the prototype used `Message`, see 12, C2), text:

```
Pattern '<text>' (<rule|include|exclude>): <part>; <part>. [<codes>]
```

Parts, in a fixed order (the codes in brackets, comma-separated):

| code | condition | part text |
|---|---|---|
| XP101 | rule: unescaped `(` `)` `\|` (not a group) | `'(', ')' keep their regex meaning (a numbered group) in this release and are an error from Xake 4.0; write \( \) for literal characters` (the list of characters and escapes follows the pattern) |
| XP103 | include, or exclude literal segment: unescaped `(` `)` `\|` `]` | `'(' is a literal character here and an error from Xake 4.0; write \( for it` |
| XP104 | include, or exclude literal segment: `[ab]` (2+ characters) | `'[ab]' is the literal text '[ab]' here; from Xake 4.0 it is a character class (one of 'a', 'b'); write \[ab\] to keep the literal text` |
| XP105 | rule, or exclude wildcard segment: `[c]` | `'[(]' is read as the literal '('; write \( instead` / for an ordinary character: `'[x]' is read as the literal 'x'; write x instead` |
| XP106 | include, or exclude literal segment: `[c]` | `'[1]' is the literal text '[1]' here; from Xake 4.0 it matches the single character '1'; write \[1\] to keep the brackets, or 1 for the character` |
| XP108 | `{x}` without a comma | `'{x}' is literal text here and an error from Xake 4.0; write \{x\} for literal braces, or {a,b} for alternatives` |
| XP109 | rule: `\|` inside a group body whose alternatives are literals | `'(ext:dll\|exe\|nupkg)': write (ext:{dll,exe,nupkg}); '\|' is an error from Xake 4.0` |
| XP110 | `\` used as a separator | `'\' as a path separator is deprecated and is only an escape from Xake 4.0; use '/'` |
| XP111 | named group in a mask | prototype text: `named groups ('(n:...)') are only meaningful in rule patterns; in a mask they are not groups: an include reads '(name:' and ')' as literal text (a segment with a wildcard then finds no files) and an exclude segment with a wildcard matches the group's body -- this will be an error in a future release; write \( \) for literal parentheses` |
| XP112 | rule: segment-local `**` | `'a**b' crosses '/' in this release and matches within one segment from Xake 4.0; write '*' for one segment or '/**/' for any depth` |
| XP113 | mask ending in `/**` | `'dir/**' matches no file in this release (an include lists the directories below 'dir'); from Xake 4.0 it is every file below 'dir'; write 'dir/**/*' for the files or 'dir/**/' for the directories` |
| XP114 | include: `*.*` segment | `'*.*' also lists names without a '.' in this release; from Xake 4.0 it needs a '.'; write '*' for every name` |
| XP115 | group name with a leading digit | `the group name '1' collides with the positional match '1'; rename it (an error from Xake 4.0)` |
| XP116 | repeated group name | `the group 'n' appears 2 times; the last value wins and the values may differ; from Xake 4.0 every occurrence must match the same text` |
| XP117 | `[!a]` (rule, exclude wildcard segment) | `'[!a]' is a class of '!', 'a' in this release and a negated class (any character but 'a') from Xake 4.0; write [a!] to keep the '!'` |
| XP118 | mask: `..` after a wildcard | `'..' after a wildcard is ignored in this release and an error from Xake 4.0` |

Rules:

- **Once per pattern**: the key is `(context, pattern text)`, process-wide (static
  `ConcurrentDictionary`), so the same text used as a rule and as a mask can warn twice (the
  meaning differs, so does the text). All parts of one pattern go in one line.
- Warnings raised while no build log is attached (masks built at script load) wait in a buffer
  (at most 1000) and are delivered when a build attaches its log (prototype
  `Warnings.attach`). Several attached logs all receive them.
- Paths read from disk, the project root and `BaseDir`/`@@` values are paths, never patterns:
  they produce no warnings (prototype test "paths read from disk give no warning"). A mask built
  with `</>` on Windows contains `\` and **does** warn (tm 57, by design).
- In Standard, every XP1xx condition is either the new meaning (no message) or the matching XP0xx
  error; there are no warnings.

## 7. Public API

### 7.1 Placement and name

- New file `src/core/PatternAst.fs`, compiled before `Path.fs`, holding the public module
  **`Xake.PatternAst`** (tm 13: `PatternAst` becomes public; the name is the one the prototype,
  its tests, `docs/overview.md` and tm 64 use). Today it is `internal` inside `Path.fs`.
- Not opened by `open Xake` (a plain module, no `[<AutoOpen>]`), so `Lit`, `Star`, ... do not
  enter scripts' scope. `Xake.Patterns` is avoided: it was the type-provider package name.
- The matcher stays **internal** (`Path.matchImpl`); scripts and the typed layer match through
  the existing public functions, whose signatures do not change: `Path.matchGroups`,
  `Path.matches`, `Path.matchesPattern`, `Path.parse`, `Path.parseDir`, `Fileset.*`.

### 7.2 Signatures

```fsharp
module Xake.PatternAst

// ---- AST (3.4)
type ClassItem = One of char | Range of char * char
type Token =
    | Lit of string | Sep | Star | AnyChar | Globstar | DoubleStar
    | Class of negated: bool * items: ClassItem list
    | Alt of Token list list
    | Group of name: string * body: Token list
    | Reserved of char
type PatternContext = Rule | Include | Exclude
type Issue = { Code: string; Text: string }          // one warning part (6.3)
type PatternError = { Pattern: string; Context: PatternContext; Position: int option; Code: string; Reason: string }
exception PatternException of string

// ---- level (2)
type Semantics = Compat | Standard
module Semantics =
    val current : unit -> Semantics                   // from XAKE_PATTERNS on first read
    val set     : Semantics -> unit                   // before the first pattern is parsed

// ---- parsing
val tryParse   : PatternContext -> string -> Result<Token list * Issue list, PatternError>
val parseIn    : PatternContext -> string -> Token list          // raises PatternException; no warnings
val parse      : string -> Token list                            // = parseIn Rule (prototype signature)
val check      : PatternContext -> string -> unit                // parse, report warnings once (6.3), raise on error
val unparse    : Token list -> string
val escape     : string -> string
val reservedChars : string                                       // "()[]{}|"

// ---- reading the AST
val groupNames : Token list -> string list                       // first-appearance order, nested included
val wildcards  : Token list -> (Token * int) list                // top-level bare wildcards with their regex numbers
val specificity: Token list -> int * int * int                   // = Path.Specificity

// ---- generation (4.9)
val format        : named: (string -> string) -> positional: (int -> string) -> Token list -> string
val formatMatches : Map<string, string> -> string -> string      // replaces ExecCore.applyWildcards
val glob          : named: (string -> string option) -> positional: (int -> string option) -> Token list -> string

// ---- warnings (6.3)
module Warnings =
    val internal attach : (string -> unit) -> System.IDisposable
```

Kept internal (implementation, may change if the matcher changes): `toRegex`, the regex cache,
`checkSameGroups` (used by `*..>`, `targets` and the typed layer through `InternalsVisibleTo`
or a public wrapper, decided in Phase 3), `plainText`, the OS-filter helper.

### 7.3 Engine wiring

- `ExecCore.explainMatch` and `makeTarget` keep calling `Path.matchGroups pattern root`; only its
  implementation moves onto the module. **`getRuleMatch` is unchanged** (brief e), including
  `""` for an unknown key and the `"0"` key.
- `ExecCore.applyWildcards (Some matches) pat` becomes `PatternAst.formatMatches matches pat`;
  `applyWildcards None` (identity) disappears with its only caller. `wildcardsRegex` and
  `patternTagRegex` are deleted (the third grammar encoding).
- `..>`, `*..>`, `=>`, `target(s)`, `command`, `phony` call `PatternAst.check Rule` on their
  patterns (and `*..>` / `targets` the group-set check, XP013).
- `Fileset` construction calls `PatternAst.check Include|Exclude`; `toFileList` walks with the
  module (5).

## 8. Performance

Options:

| | AST → .NET regex, cached | own matcher over the AST |
|---|---|---|
| exact `getRuleMatch` values (greedy leftmost, numbering, `"0"`) | by construction (today's engine is a regex) | must re-implement backtracking for multiple wildcards to reproduce greedy captures |
| backreferences (repeated groups), alternation, classes, case-insensitive culture rules | built in (`\k<n>`, `(?:a\|b)`, `[...]`, `IgnoreCase`) | to write and test |
| cost to build | `RegexOptions.Compiled`: ~1 ms + JIT per pattern (prototype comment); interpreted: ~20–50 µs | ~0 |
| cost per match | ~0.2–1 µs per path (compiled) | similar for simple globs, worse with backtracking captures |
| code | ~100 lines translation + cache | ~400+ lines plus a second test corpus |
| risk | ReDoS on pathological patterns (`**/**/**/...`): patterns are written by the script author; paths are short | bugs in captures |

Recommendation: **regex backend, generated from the AST, cached**, plus two fast paths that skip
the regex where the AST makes it trivial:

1. Cache key `(context, semantics, pattern text, anchor, case mode)`, process-wide
   `ConcurrentDictionary` (today: per mask and per (pattern, root)); regexes are thread-safe for
   matching.
2. `RegexOptions.Compiled` for rule patterns and exclude masks (few, hot: matched against every
   target / every found file); **interpreted** for one-segment walker matchers and for patterns
   built at run time in large numbers (e.g. `glob` results in a loop), where compile cost would
   dominate.
3. Fast paths: a literal segment is a string comparison; a segment `*` matches everything; a
   segment `*<literal>` (`*.cs`) is an `EndsWith` with the OS comparer. These cover the bulk of
   real masks (`**/*.cs`, `src/*.fs`) without a regex per name.
4. Walking cost is dominated by the listing, not the matching; pruning (5.2) is the large win
   (`.git`, `bin`, `obj`). Today's G8 measurement (`!**/.*/**/*` at ~1 ms per file before
   caching) is the regression to guard: the Explicit benchmark in `FilesetTests` runs in
   Phase 3 before/after, with a target of no regression on the include-only case and a
   measurable win on the exclude cases.

An own matcher stays possible later behind the same AST; nothing in the public API exposes
`Regex`.

## 9. Compat and deprecation policy

### 9.1 Rollout of classes and braces

| element | 3.13 (Compat default) | 4.0 (Standard) |
|---|---|---|
| `{a,b}` | **active** in all contexts (new); `{x}` warns (XP108) | active; `{x}` error |
| `[...]` in rules | class, as today (no warning, except `[!` XP117) | class |
| `[...]` in includes | literal, warns (XP104/XP106) | class |
| `[...]` in excludes | as today per segment; literal-segment use warns | class |
| `( ) \|` outside groups | today's meaning, warns (XP101/XP103/XP109) | error |
| named groups in masks | today's meaning, warns (XP111) | error |

### 9.2 Calendar for `\` as a separator

| release | `\` before `( ) [ ] { } \|` | `\` before anything else |
|---|---|---|
| 3.13 (this feature) | escape (deliberate break, tm 55: `src\(x)\*.cs` now means the directory `src(x)`) | separator, warns XP110 |
| 3.14 … 3.x | same | same |
| 4.0 | escape | escape for `* ? , ! ^ - \`, error XP009 for any other character |

Paths read from disk keep being normalized (`\` → `/`) in every release (tm 56). `</>` is not
changed (tm 57).

### 9.3 Version numbers (brief f)

- **3.13.0** for the pattern module, the engine migration and the typed layer: Compat is the
  default and keeps every valid 3.12 spelling's meaning, except the items in 9.4, all of which
  are bug fixes or the tm 55 escape break.
- **4.0.0** when Standard becomes the default and every XP1xx warning becomes the new meaning or
  an error; Compat is removed. Not before one 3.x release with the warnings has shipped.

### 9.4 Changes in 3.13 that are not announced by a warning (release notes)

1. `\` before a reserved character is an escape (tm 55); `src\(x)\*.cs` changes meaning.
2. The project root is a literal in rule matching (a root containing `( ) [ ] |` mismatched or
   crashed).
3. A lone `(`, `)` or `[` gives `PatternException` (XP001/XP002) instead of
   `RegexParseException`.
4. `{a,b}` with a comma is an alternation (was literal text).
5. Multi-file names: an empty group value or a `)` in a value now gives the right name.
6. Include OS-listing quirks dropped: `?` no longer matches zero characters before `.`
   (`mli?.dll` finding `mli.dll`), and on .NET Framework `*.rdl` no longer finds `.rdlx` (both to
   be confirmed by measurement in Phase 2).
7. Fileset results are in a deterministic order and without duplicates (5.5).
8. An unreadable directory is skipped with a warning instead of failing the build.
9. Multi-output string rules check that all outputs have the same named groups (XP013, tm 50).

### 9.5 Compat corpus (what pins behaviour)

Phase 2 turns these into conformance vectors, each tagged with the level(s) it pins:

- **Engine tests on `dev`**: `PathTests.fs` (all `MaskTests`, `MaskWithParent`, the group cases
  `(apl:*/*)`, `(path:bin/**)`, the root-relative `**/mlib.dll` cases — Compat; lines 18–19
  `c:\[(]*[)].c` / `c:\[(]*.c` change meaning under tm 55 and are rewritten as
  `c:/\(*\).c`-style Compat vectors), `FilesetTests.fs` (dot-folder idiom, `dir/**` vs
  `dir/**/*`, case per OS, duplicates — see C5), `XakeScriptTests.fs` (group-in-rule cases incl.
  `(name:[(]*).ss`, `[(]*.ss`, multi-target name generation), `RuleMatchTests.fs`,
  `RuleSpecificityTests.fs`, `ParentDirTargetTests.fs`, `PatternAstTests.fs` (regex byte
  identity).
- **Prototype tests** (frozen branch), ported: `EscapingTests.fs`, `EscapingDiskTests.fs`,
  `PatternAstTests.fs` — with the include `[c]` cases changed to Compat-literal (C1) and the
  warning level changed (C2).
- **Samples and build scripts**: every pattern and mask string in `samples/*.fsx`
  (`samples/Research/*` excluded: experiments with their own regex code), `build.fsx`,
  `build.fsc.fsx`: e.g. `out/(cfg:*)/(fwk:*)/(name:*).dll`, `lock::(name:*)::(cfg:*)`,
  `test::(project:*)/(slice:*)`, `run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt`, `src/*/xake.project.json`,
  `-- "src/*tests/*.fsproj"`, `includes "a/**/"`, `paket-files/*.*`.
- **Docs code blocks**: every fenced F# block in `docs/*.md` (e.g. `db:seed-(table:*)`,
  `signed/(name:*).(ext:dll|exe|nupkg)`, `out/pkg/%s{...}/...(ver:*).nupkg`).
- **Hermetic**: `src/hermetic` and its tests use `Path.parse`, `Path.matchesPattern`,
  `Includes/Excludes` directly, the description masks `["**/*.cs", "!obj/**/*", "!bin/**/*",
  "!**/.*/**/*"]` (the `!` is hermetic's own exclude marker, stripped before Xake sees the mask),
  the `dir/**` → `dir/**/*` workaround (`Import.fs`, `Lock.fs`, `Locks.fs`), and
  `signed/(name:*).(ext:dll|exe|nupkg)` (`Sign.fs` docs, `SignTests.fs`): XP109 fires there in
  3.13; the replacement `(ext:{dll,exe,nupkg})` works in 3.13.

## 10. Worked examples

Root `/p` for rules; base dir `/p` for masks; Linux unless stated. "C" = Compat, "S" = Standard.

1. **Rule with captures.** `"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> ...`, target
   `/p/out/Release/net8.0/Core.dll`. Regex (both levels):
   `^/p/out/(?<cfg>([^/]*))/(?<fwk>([^/]*))/(?<name>([^/]*))\.dll$` (C uses `[^/\\]`).
   `getRuleMatches` = `cfg=Release, fwk=net8.0, name=Core, 1=Release, 2=net8.0, 3=Core,
   0=/p/out/Release/net8.0/Core.dll`. `getRuleMatch "nme"` = `""`.

2. **Multi-output, named.** `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] *..> ...`,
   target `out/net8.0/Core.xml` → targets `out/net8.0/Core.dll`, `out/net8.0/Core.xml`
   (`formatMatches`). `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml"]` → XP013 when the
   rule is created (today: silently `out/net8.0/.xml`).

3. **Multi-output, positional.** `["dd/*.dll"; "dd/*.xml"]`, target `dd/a.dll` → `dd/a.xml`
   (pinned). `["(pat:**/*).dll"; "(pat:**/*)/main.xml"]`, target `dd/a.dll` → `dd/a/main.xml`
   (pinned).

4. **Include with dotfiles and pruning.** Tree: `a/b.cs`, `a/.git/x.cs`, `.hidden/y.cs`,
   `top.cs`. `!! "**/*.cs"` → all four (C and S; picomatch default would skip the dot ones).
   `!! "**/*.cs" -- "**/.*/**/*"` → `a/b.cs`, `top.cs`; the walker never lists `.git` or
   `.hidden` (pruned, 5.2). S also accepts `-- "**/.*/**"`.

5. **Anchoring vs `.gitignore`.** `!! "**/*.rdl*" -- "*.rdlx"` removes only `.rdlx` files directly
   in the base dir. In `.gitignore`, `*.rdlx` (no `/`) matches at any depth; in Xake write
   `-- "**/*.rdlx"`.

6. **Escapes.** File `sub[1]/s.c`. `!! @"sub\[1\]/*.c"` → it (C and S, no warning).
   `!! "sub[1]/*.c"`: C → the directory literally named `sub[1]` (today's meaning) plus XP106
   `'[1]' is the literal text '[1]' here; from Xake 4.0 it matches the single character '1';
   write \[1\] to keep the brackets, or 1 for the character`; S → the directory `sub1`.
   (The prototype gave `sub1` already in C; see C1.)

7. **Legacy rule escape.** `@"[(]*.ss" ..> ...`, target `(abc.ss`: C → matches, `1 = abc`,
   warning XP105 `'[(]' is read as the literal '('; write \( instead`. `@"\(*.ss"` → same match, no
   warning. `@"(name:[(]*).ss"` → `name = (abc` (pinned), XP105.

8. **Alternation migration.** `target "signed/(name:*).(ext:dll|exe|nupkg)"`: C → works as today,
   warning XP109 `'(ext:dll|exe|nupkg)': write (ext:{dll,exe,nupkg}); '|' is an error from
   Xake 4.0`. `target "signed/(name:*).(ext:{dll,exe,nupkg})"` → same matches in C and S, no
   warning, `getRuleMatch "ext"` = `dll`. Regex: `^/p/signed/(?<name>([^/]*))\.(?<ext>(?:dll|exe|nupkg))$`.
   Unescaped `a|b.txt` as a rule: C → regex `^/p/a|b\.txt$` (any path starting with `/p/a` or
   ending in `b.txt`, today's meaning), XP101; S → XP003.

9. **Globstar, zero segments.** Rule `"**/*.dll"`, target `/p/a.dll`: C → no match (pinned by
   `PathTests`); S → match, `1 = ""`, `2 = a`. Rule `"a/**/b.txt"` formats with `1 = ""` to
   `a/b.txt` (S). Rule-selection effect in S: with rules `*.dll` and `**/*.dll` both present,
   `a.dll` now matches both and `**/*.dll` wins on literal count (`/.dll` = 5 vs `.dll` = 4) —
   listed in the 4.0 release notes.

10. **`dir/**` in masks.** `!! "**/*.cs" -- "dir/**"`: C → removes nothing (pinned) + XP113; S →
    removes everything below `dir`. `!! "src/hermetic/**"`: C → the directories below
    `src/hermetic` (as `File` values) + XP113; S → every file below it.

11. **Backslash.** `!! @"src\*.cs"`: C → `src/*.cs` + XP110; S → the file literally named
    `src*.cs` (`\*` is an escape). `!! @"src\(x)\*.cs"`: C → directory `src(x)`, files `*.cs`,
    one warning with XP103 (`)`) and XP110 (tm 55, deliberate); S → XP003 for the `)`.

12. **`!` and `#` are literal.** `!! "!a.txt"` lists the file named `!a.txt` (C and S). In
    `.gitignore` a leading `!` re-includes; Xake uses `++`/`--` for that. `#a.txt` likewise.

13. **Repeated group.** Rule `"src/(p:*)/(p:*).fsproj"`: target `src/Core/Core.fsproj` → `p = Core`
    (C and S); target `src/Core/Other.fsproj` → C: match, `p = Other`, XP116; S: no match.

14. **`*.*`.** Files `README`, `a.txt`. `!! "*.*"`: C → both + XP114; S → `a.txt`.
    `!! "*" -- "*.*"`: C and S → `README` (excludes always needed the dot).

15. **Case.** Files `Dir2/e.cs`, `dir/c.cs`; `!! "**/*.cs" -- "d*/**/*"`: Linux (C and S) keeps
    `Dir2/e.cs`, Windows removes it (pinned). `-- "Dir2/**/*"` on Linux: C removes `Dir2/e.cs` and
    would also remove `dir2/...` (literal exclude segments ignore case today); S only `Dir2/...`.
    macOS `!! "*.CS"`: C finds `a.cs` (as the .NET listing does today); S does not.

16. **From a rule pattern to a mask.** `PatternAst.parse "src/(lib:*)/(name:*).cs" |>
    PatternAst.glob (function "lib" -> Some "core" | _ -> None) (fun _ -> None)` = `src/core/*.cs`;
    with the value `a(b` it is `src/a\(b/*.cs` (values escaped).

17. **A clear error instead of a crash.** Rule `"a(b.txt"`: today `RegexParseException`; C →
    `Invalid pattern 'a(b.txt' (rule) at character 2: an unescaped '(' has no matching ')'; write
    \( for a literal '(' [XP001]`, raised when the rule is created; inside a build reported as a
    `XakeException` with that message.

## 11. Open items (brief "Open for design" 1–15)

Each entry is self-contained: recommendation, alternatives, and the spec sections that depend on
the decision.

### 1. Exact EBNF of masks and rule patterns

**Recommendation.** One grammar (3.1–3.3) with two contexts: a rule pattern is the grammar with
`group`; a mask is the same grammar without it (a group in a mask is parsed so it can be named in
a warning or error, then rejected or given its Compat meaning). The lexer resolves escapes,
`*` vs `**` (maximal munch), and recognises `(name:` as one token; the syntax handles classes,
braces (only with a top-level comma), groups and the globstar position rule. Both semantics
levels share this grammar; Compat only adds the `legacy` production (unescaped reserved
characters keep their old meaning).

Alternatives considered:
- Two grammars (mask, rule) sharing a lexer: simpler to read separately, but the brief asks for
  one grammar with rules as a superset (brief a), and the include/exclude asymmetry came exactly
  from separate encodings.
- Treat `(name:` syntactically (`(` then name then `:`): equivalent, but a lexical token makes
  `(a b:*)` and `(:*)` unambiguous "not a group" without backtracking in the parser.
- Accept `{x}` as a literal (picomatch): friendlier, but closes no syntax and keeps `{1..3}`
  ambiguous later; rejected for Standard, kept literal-with-warning in Compat.

Depends: 3 (all), 6.2 (XP003–XP012), 7.2 (`tryParse`, `Token`).

### 2. Semantics table: rule vs include vs exclude, per element

**Recommendation.** Standard has one meaning per element in every context (4.1); the only
context differences are named groups (rules only), the trailing `/` (directories for includes,
"everything below" for excludes, an error for rules) and anchoring (project root vs base dir).
Compat reproduces today's per-context meaning for exactly the spellings listed in 4.2 and warns
where the meaning will change.

Alternatives considered:
- Switch everything to Standard now (one table only): simplest code and docs, but silently
  changes `[...]` in includes, `**/` in rules, `dir/**` in masks — against the brief's
  break-softening principle.
- Keep per-context meanings in Standard as well (e.g. includes never get classes): contradicts
  "one matcher, the asymmetry disappears" (brief c).
- Compat as a separate legacy engine (today's code kept verbatim behind a switch): exact compat
  for free, but two implementations to maintain for a whole major; the AST approach covers the
  same rows with flags.

Depends: 4.1, 4.2, 6.3 (which rows warn), 9.1.

### 3. Dotfiles

**Recommendation.** Wildcards and classes match a leading `.` (Ant, `.gitignore`, `globset`,
and today's behaviour in every context). Hidden folders are excluded explicitly with
`-- "**/.*/**/*"` (or `**/.*/**` in Standard), which the walker prunes cheaply.

Alternatives considered:
- picomatch / bash style (`*` skips a leading `.` unless the pattern starts with `.`): what
  JavaScript users expect, and it would hide `.git` by default; but it silently drops files that
  `**/*.cs` finds today (`.hidden/y.cs` in `FilesetTests`), in rules as well as masks.
- A fileset option `dot = false` (opt-in picomatch behaviour): possible later without grammar
  change; not needed for the module.

Depends: 4.3, 5.2 (pruning example), 10 example 4.

### 4. Case sensitivity per OS; case of classes

**Recommendation.** Standard: one comparer everywhere, ordinal on Linux and macOS
(`Env.isUnix`), ignore-case on Windows, identical to `File` equality and rule matching; classes
and ranges follow it (`CultureInvariant`). Compat: today's per-context rules (literal exclude
segments always insensitive; includes as the .NET listing, i.e. insensitive on macOS too). Literal
include segments are file-system probes, so they follow the file system in both levels.

Alternatives considered:
- macOS insensitive everywhere (matches the default APFS volume): friendlier on macOS, but
  rules and `File` equality are ordinal there today, so a target and its rule could disagree;
  changing `File` equality is out of scope.
- A per-fileset / per-script option `caseSensitive`: flexible, but every cache key and the
  rule matcher would carry it; can be added later as an `ExecOptions` field.
- Detect the file system's sensitivity per directory: correct but slow and surprising across
  machines (a build behaves differently on a case-sensitive APFS volume).

Depends: 4.4, 4.8 (regex options), 8 (cache key), 9.4/release notes for 4.0, example 15.

### 5. `**` edge cases

**Recommendation.** Standard: a globstar is `**` forming a whole segment, group delimiters
transparent (3.3); it matches zero or more whole segments in every context; its capture never
has a leading/trailing `/`, `""` for zero; `format` drops one `/` next to an empty globstar;
`a/**` = every file below `a` (not `a`); `a/**/` in an include = `a` and its subdirectories;
segment-local `a**b` = `a*b`. Compat keeps today's per-context meaning (rules: 1+ segments and
`a**b` crossing `/`; includes: `a/**` lists directories; excludes: `a/**` matches nothing) with
warnings XP112/XP113.

Alternatives considered:
- Zero-segment globstar in rules already in 3.13: matches mainstream sooner, but widening a
  rule can change which rule wins (example 9), silently; no useful warning is possible because
  every `**/` rule would warn.
- Segment-local `**` as an error in Standard (globstar must be a whole segment, as old `globset`
  did): stricter; picomatch and `.gitignore` read it as `*`; the Compat warning already tells the
  user to write `*`.
- `a/**` matching `a` itself too (some globbers): useless for files and inconsistent with
  `.gitignore`.

Depends: 3.3, 4.1, 4.2, 4.5, 4.8 (regex), 4.9 (format), 5.2, examples 9–10; hermetic's
`dir/**` → `dir/**/*` workaround can go in 4.0.

### 6. What an include segment without wildcards means

**Recommendation.** A probe, not a listing (4.6): a mask with no non-literal element at all is
an explicit name, listed even if absent (today's rule; needed for `needFiles (!! "x")` to build
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

Depends: 4.6, 5.2 step 1–2, 10 example 4.

### 7. Class and brace rollout timing; the reserved-character warning meanwhile

**Recommendation.** Braces `{a,b}` (with a comma) become active **now**, in all contexts; classes
keep their per-context Compat meaning now (already classes in rules; literal in includes) and
become classes everywhere in 4.0. Meanwhile the warnings of 6.3 say per context what the spelling
means now, what it will mean in 4.0, and the spelling that keeps today's meaning
(XP101/XP103/XP104/XP106/XP108/XP109).

Alternatives considered:
- Both in the next major only (the brief's original rollout idea, tm 52/60): purest "no change
  in 3.x", but leaves `(ext:dll|exe|nupkg)` users (hermetic `Sign.fs` docs and tests) with a
  warning and no warning-free replacement for the whole 3.x line.
- Both now: one release earlier for mainstream behaviour, but silently changes includes like
  `[abc]*.txt` (finds a literal file today) and the `sub[1]` directory case.
- Braces now only inside named groups (where `|` is used today): minimal change, but an odd rule
  to document ("braces work in groups only") and still a change of meaning there.
- An opt-in to Standard (recommended, section 2) complements any of these: users who want
  classes now set `XAKE_PATTERNS=standard`.

Depends: 2, 4.2 (rows `{a,b}` and `[...]`), 6.3, 9.1, 9.4 item 4, example 8.

### 8. Error vs warning model

**Recommendation.** Errors are `PatternException of string` (one type, the prototype's, reused
by the typed layer, tm 64) raised at parse time with the message `Invalid pattern '<text>'
(<context>) at character <n>: <reason> [XPnnn]`; `tryParse` returns a structured `PatternError`
record. Masks are checked at construction, rule patterns when the rule is created (and again at
first match for rules built from DU cases); inside a build the engine rethrows as `XakeException`.
Warnings go to the build log at level Warning, one line per pattern, keyed by
`(context, pattern text)`, buffered until a log attaches; stable codes `XP1nn` make them testable
and documentable. Compat never throws for input that works today except the former crashes
(XP001/XP002/XP010) and the new multi-output check (XP013).

Alternatives considered:
- Errors as `XakeException` directly: one exception less, but the typed layer and scripts that
  load patterns outside a build would see a build-error type for a syntax error; tm 64 already
  chose `PatternException`.
- Warning key = pattern text only (prototype): one warning even if the same text is a rule and a
  mask, but the text of the warning depends on the context in Compat, so one of them would be
  wrong.
- Warnings at level Message (prototype): less noise, but users filtering on warnings would miss
  deprecations — the point of a one-release warning.
- A "warnings as errors" switch: covered by the Standard opt-in, which turns the same inputs into
  errors.
- No codes: shorter messages; codes cost little and let docs link from a warning to its fix.

Depends: 6 (all), 7.2 (`PatternException`, `PatternError`, `check`), 7.3, 10 example 17.

### 9. Minimal public API, name, namespace; relation to the public `PatternAst`

**Recommendation.** Public module `Xake.PatternAst` in its own file, not auto-opened, holding the
AST types, `PatternException`/`PatternError`, `Semantics`, `tryParse`/`parseIn`/`parse`/`check`,
`unparse`/`escape`, `groupNames`/`wildcards`/`specificity`, `format`/`formatMatches`/`glob`
(7.2). The matcher and the regex translation stay internal; matching goes through the existing
`Path.matchGroups`/`Path.matches`/`Fileset` functions, whose signatures do not change.
`Fileset` keeps `Includes of PathMask` in 3.x (pickled in `.xake`, used by hermetic); in 4.0 it
may carry the mask text.

Alternatives considered:
- A new name (`Xake.Pattern`, `Xake.Glob`): reads better, but tm 13/64, the prototype tests and
  the docs already use `PatternAst`; the typed layer has an internal `Pattern` type.
- A public compiled-pattern type (`Pattern.compile : PatternContext -> string -> Matcher` with
  `IsMatch`/`Match`): useful for library authors (hermetic), but it fixes the matcher's shape
  before Phase 3 has measured anything; can be added in 3.14 without breaking.
- Make `toRegex` public (prototype): exposes the backend and blocks an own matcher later.
- Change `FilesetElement` to carry text now: cleaner, but a binary break for hermetic and a DB
  version bump in a minor release.

Depends: 7 (all), 5.1, 9.3.

### 10. How `Fileset` scanning uses the matcher

**Recommendation.** A depth-first walker driven by the AST (5.2): literal prefix joined without
listing, literal segments probed, one-segment patterns matched against a directory listing,
globstars recursed, braces with `/` expanded into a union; the OS listing filter is only a
superset pre-filter (`*`/`?`/literals passed, `"*"` otherwise), the module decides every name;
subdirectories covered entirely by a later exclude are pruned; ordinal per-directory order,
duplicates removed; Compat follows directory symlinks under `**` (today), Standard does not
descend into reparse points; unreadable directories are skipped with a warning.

Alternatives considered:
- Keep `SearchOption.AllDirectories` for `**` and filter afterwards (today): simple, but lists
  `.git`/`node_modules` in full before any exclude applies (the G8 cost), and follows symlink
  cycles.
- Match every file's full path against one whole-pattern regex after a full recursive listing:
  one code path, but no pruning and no early stop for literal prefixes.
- Follow symlinks in Standard with cycle detection: needs the link target (`LinkTarget`,
  .NET 6+) or P/Invoke; Core targets netstandard2.0/net462.
- Keep enumeration order (no sort): no cost, but non-deterministic across file systems, which
  matters for F# compile order.
- Keep duplicates: today's (tested) behaviour; but a file twice in `Csc`/`Fsc` inputs is an error
  or warning in the compiler.

Depends: 5 (all), 4.6, 8 (fast paths), 9.4 items 6–8.

### 11. Performance: regex compile cache vs an own matcher

**Recommendation.** Regex generated from the AST, cached by `(context, semantics, text, anchor,
case)`, `Compiled` for rule and exclude patterns, interpreted for one-segment walker matchers and
run-time-generated masks, plus literal / `*` / `*.ext` fast paths that bypass the regex (8).
Measure in Phase 3 with the existing `FilesetTests` benchmark; no regression on includes, a win on
excludes through pruning.

Alternatives considered:
- An own matcher (two-pointer glob with backtracking and captures): no compile cost and no
  ReDoS, but must re-create .NET's greedy capture values, backreferences and case rules exactly
  (brief e: `getRuleMatch` unchanged), and needs its own test corpus.
- Regex without cache: today's pre-G8 behaviour, ~1 ms per mask and file; ruled out.
- Always `RegexOptions.Compiled`: fastest matching, but ~1 ms per distinct pattern adds up when
  scripts generate many masks (typed `Glob` in a loop).
- Non-backtracking regex (`RegexOptions.NonBacktracking`): .NET 7+ only, no backreferences;
  Core targets netstandard2.0.

Depends: 8, 4.8, 5.3, 7.2 (nothing regex-shaped is public).

### 12. Compat corpus

**Recommendation.** Pin, as tagged conformance vectors (Compat / Standard / both): the engine
tests that touch patterns (`PathTests`, `FilesetTests`, `XakeScriptTests` group and multi-target
cases, `RuleMatchTests`, `RuleSpecificityTests`, `ParentDirTargetTests`, `PatternAstTests` regex
identity); the prototype's `EscapingTests`/`EscapingDiskTests`/`PatternAstTests` (with C1/C2
adjusted); every pattern/mask in `samples/*.fsx` (not `samples/Research`), `build.fsx`,
`build.fsc.fsx` and the fenced F# blocks in `docs/*.md` (type-checked and, for masks, run on a
fixture tree); hermetic's direct uses (`Path.parse`, `matchesPattern`, `dir/**/*` workaround,
`(ext:dll|exe|nupkg)`). Vectors that change meaning on purpose (tm 55 `c:\[(]*[)].c`) are
rewritten, not deleted, and listed in the release notes.

Alternatives considered:
- Only the unit tests: cheapest, but the docs and samples are what users copy; the `|`-in-group
  case lives only in hermetic docs/tests.
- Also scan public GitHub scripts using Xake: broader, but out of reach for a conformance suite
  and not reproducible.
- Pin `samples/Research` too: those files carry their own regex experiments
  (`a?/(TAG:*.*)/*.txt/**/+`), not supported usage.

Depends: 9.5, Phase 2 vector format, 9.4 (release-note list).

### 13. Migration of `[(]` and `sub[1]`

**Recommendation.** Compat keeps both meanings per context (rule: `[c]` = `c`; include and
exclude literal segment: the literal text `[c]`; exclude wildcard segment: `c`) and warns with
the replacement that keeps today's meaning and the one that gives the 4.0 meaning:
rules XP105 `'[(]' is read as the literal '('; write \( instead` (for an ordinary character
`'[x]' ... write x instead`); masks XP106 `'[1]' is the literal text '[1]' here; from Xake 4.0 it
matches the single character '1'; write \[1\] to keep the brackets, or 1 for the character`. In
Standard `[c]` is a one-character class = `c` (no warning; it is valid POSIX glob).

Alternatives considered:
- The prototype's choice: `[c]` = `c` everywhere already in 3.13 (`sub[1]/*.c` finds `sub1`),
  with a warning: consistent across contexts sooner, but a silent change of meaning for includes,
  which today never treated `[` specially.
- Keep warning about `[c]` in Standard too ("write `\c`"): `[c]` is legal glob; warning on it
  forever is noise.
- Auto-detect (in an include, use the literal `sub[1]` if it exists, else `sub1`): no surprise
  for either reading, but the result depends on the tree, which breaks determinism.

Depends: 4.2 rows `[c]`/`[...]`, 6.3 XP104–XP106, 9.5 (prototype tests adjusted), example 6–7.

### 14. Named group details

**Recommendation.** Names: a Unicode letter or `_`, then letters, digits, `_` (a leading digit
warns in Compat, XP115, error in Standard). Body: any element, including `/`, globstars,
classes, braces and nested groups. A repeated name in one pattern is a backreference in Standard
(every occurrence matches the same text; `src/(p:*)/(p:*).fsproj`), today's last-wins in Compat
with XP116. Across outputs of a multi-output rule: the same set of names in every output
(XP013, checked at creation); string rules may also pair bare wildcards by position. Positional
numbers, the `"0"` key and greedy capture values stay exactly as today (4.7).

Alternatives considered:
- Repeated names as an error: simplest and unambiguous, but the backreference form is a real
  need in build scripts (project folder = project name) and costs nothing with a regex backend.
- Repeated names as independent captures (today): keeps the "last wins" surprise; `format` would
  have one value for two possibly different texts.
- ASCII-only names: matches .NET regex group-name rules more tightly, but F# records allow
  Unicode field names and the typed layer maps fields to groups.
- Forbid `/` or globstars inside groups: simpler walker, but `(apl:*/*)` and `(path:bin/**)` are
  pinned by `PathTests`.

Depends: 3.1 (`name`), 3.2 (`group`), 4.7, 4.8 (`\k<n>`), 4.9, 6.2 XP006/XP007/XP013,
example 13.

### 15. Version number (brief f) and the `\` separator calendar

**Recommendation.** Ship the module, the engine migration and the typed layer as **3.13.0**
with Compat as default (every 3.12 spelling keeps its meaning except the release-noted fixes and
the deliberate tm 55 escape, 9.4) and Standard as an opt-in. Ship **4.0.0** no earlier than one
3.x release later, with Standard as default and Compat removed; that is when `\` stops being a
separator (`\` + escapable = escape, any other `\x` = error XP009) and all XP1xx warnings become
the new meaning or errors.

Alternatives considered:
- Everything in 4.0.0 now (prototype's tm 41): honest about the tm 55 break, but then 4.0 would
  ship deprecation warnings for changes that need yet another major (5.0) to complete, or ship
  the silent changes the brief rules out.
- Keep Compat as an opt-out in 4.x (`XAKE_PATTERNS=compat`): softer major, but keeps two
  semantics in the codebase for a whole major and lets warnings be ignored indefinitely.
- In 4.0 make `\x` = `x` for any character (picomatch): `src\x.cs` would silently become
  `srcx.cs`; the error is safer, and can be relaxed in a later major.
- Treat the tm 55 escape break as reason enough for 4.0.0 now and Standard in 5.0.0: strict
  SemVer, at the cost of two majors in a row for one feature.

Depends: 2, 9.2, 9.3, 9.4.

## 12. Conflicts and findings that need the user's attention

- **C1 — prototype vs brief: `[c]` in includes.** #90 reads `[c]` as `c` in includes
  (`sub[1]/*.c` finds `sub1`, test "an unescaped sub[1] is the [c] form"). Today an include
  treats `[` literally, so this is a silent change, which brief (b)'s principle excludes. The spec
  keeps the literal meaning in Compat (open item 13); the prototype's tests must be adjusted when
  ported.
- **C2 — warning level.** The prototype attaches pattern warnings at `Level.Message`
  (`ExecCore.fs:388` on the frozen branch); the spec recommends `Level.Warning` (open item 8).
- **C3 — brief rollout vs braces now.** Brief (b) sketches "this release keeps today's behaviour
  for reserved characters"; the spec activates `{a,b}` (comma form) in 3.13 to give the
  `(ext:dll|exe|nupkg)` users (hermetic `Sign.fs` docs and `SignTests.fs`) a replacement (open
  item 7). Braces are not among the `( ) [ ] |` the principle protects, but tm 52 listed `{ }` as
  reserved.
- **C4 — rules vs masks on `**`.** Today a rule's `**/` needs at least one directory
  (`PathTests`: `**/mlib.dll` does not match `c:/mlib.dll` with root `c:/`); masks accept zero.
  "One matcher, the asymmetry disappears" (brief c) therefore changes rule matching; the spec
  defers that to Standard (open item 5).
- **C5 — a test whose title and assertion disagree.** `FilesetTests` "exec function removes
  duplicates from result" asserts the duplicate count is 2. The spec removes duplicates (5.5);
  the test must change, the user should confirm the intent.
- **C6 — `PatternAst` visibility on `dev`.** It is `internal` inside `Path.fs` today; tm 13 makes
  it public; the prototype moved it to its own file. The spec follows the prototype (7.1).
- **C7 — the `"0"` key.** `getRuleMatches` contains `"0"` (the whole match, from
  `Regex.GetGroupNames`). Not documented, not tested, but "unchanged" (brief e) means it stays;
  an own matcher would have to produce it.
- **C8 — hermetic depends on mask internals.** `Path.parse`, `Path.matchesPattern`,
  `Includes/Excludes of PathMask` and the `dir/**` → `dir/**/*` workaround (`Import.fs`,
  `Lock.fs`, `Locks.fs`). The spec keeps those public shapes in 3.x (5.1, open item 9); the
  `Xake.Hermetic.Dotnet` range `[3.11.0.40, 3.12)` needs a bump for 3.13 anyway.
- **C9 — observed, out of scope.** `File.fs` `compareNames` passes `ignoreCase = Env.isUnix`
  (case-insensitive on Unix, sensitive on Windows), the inverse of every other comparer; worth a
  separate issue.
- **C10 — OS listing quirks are asserted from .NET documentation and source knowledge, not
  measured here** (`?` matching zero characters, `*.*`, the .NET Framework 3-letter extension
  rule, macOS case-insensitive listing, symlink following under `AllDirectories`). Phase 2 should
  measure them before the release notes state them.
