# Critique: why the 4.0 pattern grammar is so large

Reviewed 2026-11-01: `spec.md` (2,008 lines, main checkout), `brief.md` D1–D37 (D34–D37 from the
`feature/pattern-matcher` worktree), `PatternVectors.fs` (453 parser vectors), `PatternMatchVectors.fs`
(449 matcher vectors), `critique-glob.md`, `src/core/PatternAst.fs` (868 lines, parser only).
Vector counts below come from scripts over the two vector files ("patterns containing X" counts the
input pattern only; rows overlap). Sizes of other tools are from the reviewer's knowledge and are
approximate. This is an analysis, not a proposal to reopen D1–D37; which items to act on is the
owner's call.

## 0. Short answer

gitignore, Ant, globset and the .NET `Matcher` answer one question: "does this path match?". Xake
also has to answer "with which values?" and "which path do these values produce?", and it has to
reproduce, or loudly change, three 3.x encodings that disagreed with each other. The first part is
essential and costs perhaps a quarter of the grammar. The rest comes from four sources that
reinforce each other:

1. **Captures that may hold any element.** A group body may contain `/`, `**`, braces and other
   groups (D21). Every structural rule then has to be defined "through" groups: whether `**` is a
   globstar, how positional numbers count, which captures an edge globstar takes, how specificity
   adds up, how `format` inverts it. No production pattern uses this. Every group in `build.fsx`,
   `build.fsc.fsx`, `samples/*.fsx`, `docs/*.md` and `src/hermetic` is `(x:*)` or
   `(ext:dll|exe|nupkg)`.
2. **The "no silent change" migration from 3.x (D1, goal 1).** Every disagreement between the 3.x
   rule regex, `Path.matches` and the OS listing became an error text that names the 3.x meaning, a
   warning code, a mask role, or a vector that pins the 3.x result. Of the 902 vectors, 136 are
   tagged "changed in 4.0". There are 9 legacy warning codes, and 3 measurement campaigns
   (macOS engine, macOS .NET, Windows) back them.
3. **Windows paths written inside the pattern text.** `\` is a separator and an escape at the same
   time, chosen by the next character, until 5.0, and then it changes again. Drive and UNC roots
   are part of the grammar. Ant and the .NET `Matcher` pick "`\` is a separator" and have no escape
   character. gitignore and globset pick "`\` is an escape". Xake picks both.
4. **Strictness plus review rounds.** Under the "error, not literal; keep syntax open" policy
   (brief b), each review turned an edge case into a code: D24–D37 added XP016–XP019, UNC roots,
   the `[*]`-is-a-literal special case and the "`..` above the root stays literal" rule.
   Mainstream tools read odd input as literal text or as a non-match and need no code for it.

## 1. Size comparison

| | gitignore | Ant fileset | picomatch (defaults) | globset | .NET FileSystemGlobbing | **Xake 4.0** |
|---|---|---|---|---|---|---|
| pattern elements | 9 (`* ? [..] ** \` `/`, trailing `/`, leading `!`, leading `#`) | 4–5 (`* ? **`, `/` or `\`, trailing `/` = `/**`) | ~14 (`* ? **`, classes incl. POSIX, braces, ranges, 5 extglobs, `(a\|b)`, leading `!`, `\`) | 7 (`* ? ** [..] [!..] {a,b} \`) | 4 (`* **`, `/` or `\`, `..`) | **15** (literal, `\`-escape, `* ?`, globstar, segment-local `**`, class, braces, named group, `/`, legacy `\` separator, root `/`, drive, UNC, trailing `/`, `[c]` escape idiom) |
| context-dependent rules | ~6 (slash anchors / floats, trailing `/`, `**` only at boundaries, `!` cannot re-include under an excluded dir, trailing spaces, `#`) | ~3 (trailing `/`, `**` whole segment, default excludes) | ~10, plus ~30 options | ~4 (`literal_separator`, `**` position, `\` per OS, no nested braces) | ~2 | **~24** (footnote a) |
| error kinds | 0 (bad input never matches) | 0 | ~1 (input too long) | ~8 (unclosed class/alternates, nested alternates, invalid range, dangling escape, ...) | ~1–2 | **17** (XP002–XP019 without XP009; XP009 added in 5.0) |
| warning kinds | 0 | 0 | 0 | 0 | 0 | **10** (XP104 105 106 110 112 113 114 117 119 120) |
| approx. size | man page section ~70 lines; `wildmatch.c` ~400 lines | manual ~1 page; `SelectorUtils` ~800, `DirectoryScanner` ~1,900 lines | README ~700 lines; library ~2,500 lines | docs ~150 lines; `glob.rs` ~1,600 lines incl. tests | docs ~1 page; package ~2,500 lines | spec 2,008 + brief 345 lines; parser alone 868 lines (matcher, scanner and engine still to come); 902 vectors |
| captures | no | no | option `capture` (positional regex groups) | no (reports which globs matched) | one implicit "stem" | positional at any depth, named, `"0"` |
| named groups | no | no | no | no | no | yes, any body, nested |
| format (inverse) | no | no | no | no | no | `format`, `formatMatches`, `glob` |
| multi-output | no | no | no | no | no | yes (same name set, XP013/XP014) |
| case per OS | `core.ignorecase` flag | option | option `nocase` | option | comparer argument (ignore-case by default) | automatic, one comparer |
| two separators | no | yes | option `windows` | `\` on Windows only, without escape | yes | yes until 5.0 (hybrid with escape) |
| roots (drive/UNC) | no (leading `/` = base) | partly | no | no | no (relative only) | `/`, `X:/`, `//server/share` |
| legacy migration | no | no | no | no | no | 9 warnings, 3.x clauses in all 17 reasons, 136 vectors with a 3.x result |

For the essential part, the right reference points are the tools that do have captures. GNU
make has one element, `%`, which gives one capture (the stem). Shake (Xake's model) has `*` and
`//` with positional captures and no escapes. Both have stayed in use for decades with that much.

(a) Xake's context rules: `(name:` is a token only in Rule context; a trailing separator is an
error in a rule but a directory mark in a mask; `\` is an escape or a separator depending on the
next character; inside a class `\` escapes; `\,` is an error inside braces but a separator outside
them; `,` is a comma only when braces are the innermost open construct; `**` is a globstar or a
local double star depending on its neighbours, and groups and braces are transparent for that
test; `]` is a literal when it comes first in a class; `-` is a literal at a class edge; `!`/`^`
negate only when first; `[*] [?] [,] [\\]` parse as literal text while other `[c]` parse as a
Class (D34); a drive is recognised only at the start; `//x/y` is a UNC root but `//x` collapses to
`/x`; `.`/`..` resolve in the literal prefix, are errors after a wildcard and stay literal above
the start; in a mask, `(` gives XP008 or XP003 depending on the text after it; XP019 applies only
when the braces end the pattern; positional numbering counts at any depth (getRuleMatches,
`wildcards`) but only top-level wildcards in `format` (FO-002 against W-001); XP105, XP106 and
XP117 depend on the role and on the 3.x "wildcard segment"; the evidence warnings depend on what a
scan sees; an all-literal mask is listed even if the file is absent (D13); a literal segment is a
probe that keeps the mask's spelling; excludes apply only to earlier includes; an empty globstar
slot drops one `/` in `format`; bare wildcards in multi-output rules pair by position.

## 2. Cost ledger

The vector numbers count patterns in the vector files: P = parser file (453), M = matcher file
(449). Parser line numbers refer to `PatternAst.fs`.

| # | feature | introduced by | cost | who needs it |
|---|---|---|---|---|
| 1 | Named groups, simple form `(x:*)`, `(x:{a,b})` | brief a, tm 6 | `group_open` token, XP006, XP007, XP015, `groupNames`. About 95 P + 97 M patterns contain a group | **engine** (all real rules), typed layer |
| 2 | Group bodies holding `/`, `**`, nested groups, braces | D21, 5.6, D37 | globstar test transparent through groups (4.3); M1 edge regexes `(?<p>bin(?:/(.*))?)` (9.3); "a group in an unmatched alternative holds `""`" (D37); XP015 across alternatives; separate `.`/`..` scan of nested sequences (parser 614–622); specificity "group = sum of body". **62 vectors** (30 P + 32 M) | 3.x tests only (`(apl:*/*)`, `(path:bin/**)`, `(name:(plat:*)-a.ss)`, example 3 `(pat:**/*)`); no production pattern |
| 3 | Positional captures numbered at any depth, plus `"0"` | 5.6, C7, brief e | creates two numbering schemes: `wildcards`/`formatMatches` count every wildcard (W-001: the group's `*` is "2"), `format` counts only top-level ones (FO-002); W-012 left as SPEC?. Every one of the 236 rule-match vectors must list `"1"`…`"n"` | getRuleMatch compatibility (brief e); no numeric key is read outside tests. `"0"` is cheap |
| 4 | `format` / `formatMatches` / `glob` (the inverse) | brief c, tm 14 | XP014; the empty globstar slot dropping `/`; one numbering scheme per function. 38 FO + 22 FM vectors | **engine** (`*..>` names), typed `Format`/`Glob` |
| 5 | Multi-output with a shared name set | tm 50, D21 | XP013, XP014, bare wildcards paired by position. 15 MO vectors | **engine**; the positional pairing only serves 3.x string rules (the typed layer rejects it) |
| 6 | Specificity | #88/#89, D5, D24, D34 | 3-tuple; `Sep` next to a `Globstar` counts 0; `Alt` = its least specific alternative; `Group` = sum of its body; `DoubleStar` = `*`; behaviour change 33. **69 vectors** (26 S + 20 comparisons + 23 RS) | **engine** rule selection (make uses the shortest stem; Shake uses priorities and fails on ambiguity) |
| 7 | `\` is a separator until 5.0 and also an escape for `( ) [ ] { } \|` | tm 55–57, D22 | three meanings of `\` (separator, escape, escape inside a class); XP110, XP017, plus XP009 in 5.0; no `\*`, so the `[*]` idiom, which forced the D34 special case `[*]` = Lit; `[(]` (the working 3.x spelling) warns XP105 and suggests `\(`, while `[*]` is the recommended spelling; `unparse`/`escape` output changes in 5.0 (a second migration); `escapePath` separate from `escape`. ~40 P + ~10 M vectors; 37 M vectors in Windows mode | Windows masks built with `</>` (measured W1/W2: 3.x treats `\` as a separator) |
| 8 | Drive and UNC roots | S6, D24, D36 | `root` production; XP016; collapse rules that interact with UNC (`//a` collapses, `//a/b` does not); `escapePath` keeps the root. Drive: 25 P + 35 M; UNC: **20** (13 P + 7 M); leading `/`: 20 P + 41 M | absolute masks (`toolDir`): yes. UNC: nobody evident (3.x had no UNC notion, D36) |
| 9 | Two contexts, plus `MaskRole` | D8, D9, D24 | the two contexts are cheap (2 rules). `MaskRole` and the 3.x "wildcard vs literal exclude segment" survive only to choose the warning text (`issuesOf`, parser 698–722). 20 `me` vectors | contexts: engine. Role: **migration only** |
| 10 | Trailing `/` = directory | D9, D32, items 22/23 | `dir_mark`, XP011, XP019, directory results for includes, pruning for excludes. 8 P + 15 M + 5 XP019 | excludes/pruning (real); directory includes (`includes "a/**/"` in a sample) |
| 11 | Legacy-warning model | D14, D15, D20, D24 | 9 codes; three triggers (parse, evidence, evaluation); key `(use, text)` in a process-wide dictionary; a 1,000-line buffer until a log attaches; evidence hooks in the matcher (XP119) and the walker (XP104, XP113, XP114); `Reasons` (~50 lines, each with a 3.x clause) plus warnings code (~80 lines). **71 vectors** (46 P with parse warnings + 8 issue texts + 17 M with XP119); spec 5.2, 7.3, 10.1 (~130 lines) | **migration only**; removed in 5.0 |
| 12 | 3.x results pinned in vectors, plus measurements | D19, D30 | 136 vectors "changed in 4.0" (77 P + 59 M); 146 notes cite 3.x; spec section 3 (108 lines), 6.4/6.5/9.5 measurement notes (~200 lines), 3 measurement documents, a Windows run | release notes; one-off |
| 13 | Braces holding `/`, nested, inside groups | D14, 4.2, 6.3 | a comma stack in the lexer; the union walk in the scanner; XP019; `Alt` specificity; wildcards in unmatched alternatives hold `""`. 68 P + 49 M contain braces, **23** with `/` or nesting | mainstream readers (bash, picomatch). The only real use is `(ext:{dll,exe,nupkg})`, which needs none of this |
| 14 | Classes with full POSIX edge rules | brief a, M2/M3 | class lexical mode; `]` first; `-` at the edges; `!`/`^`; `\` inside; XP002, XP010 (×3 details); .NET escaping (M2); ranges follow the OS comparer; XP105/106/117. 49 P (class section) + 39 M | mainstream readers; Xake scripts use `[(]` only |
| 15 | `**` position rule, `DoubleStar` token, collapsing | D12, D34, D37 | two tokens for one spelling; XP112; `**/**` collapse (spec 5.5 had to be corrected, D37); edge captures. 28 P + 47 M globstar + 13 `dir/**`; segment-local `**` ~18 | globstar: **engine and filesets**. `DoubleStar` exists only for `unparse` and XP112: **migration** |
| 16 | `.` / `..` | D29, D34 | literal-prefix normalisation; XP012 (two texts); `..` above the start stays literal. ~32 vectors (24 P + 8 M) | `../sibling/(x:*).dll` (docs): a leading `../` only |
| 17 | Explicit-name masks | D13 | scanner rule unlike every mainstream globber; not in the grammar; PR 4 vectors | hermetic `Locks.fs`, compiler `Ref` filesets; revisit after 4.0 (D13) |
| 18 | XP120, absolute exclude outside the base | D6, D24 | a permanent evaluation-time check against the base and earlier include anchors. 2 vectors | exists only because a leading `/` differs from gitignore |
| 19 | Case per OS | D11 | one comparer; C4 caveat (regex `IgnoreCase` vs `OrdinalIgnoreCase`). 21 M (10 rule + 11 mask) | **engine** (same as `File` equality): essential, cheap |
| 20 | Dotfiles matched | D10 | no grammar cost; the walker must avoid default `EnumerationOptions`. 18 M | essential, free |
| 21 | "Keep syntax open" errors | brief b, D8, D24, D29 | XP002, XP004, XP005, XP017, XP018, XP019, `[[:` in XP010; each with its own text. ~30 P | future syntax; nobody today |

## 3. Verdict

**Essential** (rows 1, 4, 5, 6, 10, 19, 20, plus `*`, `?`, `**` and classes, about 40% of the
vectors): named single-segment captures, an inverse `format`, multi-output name sets, a way to rank
overlapping rules, filesets with includes, excludes, `**` and directory pruning, and one comparer
per OS. None of this exists in gitignore because gitignore never makes a path. It is still small:
make shows that one capture works, Shake shows that a few do.

**Accidental**: these could be cut without losing a capability any real script uses.

| item | simplification | what it breaks | vectors removed |
|---|---|---|---|
| 2 group bodies | a body stays within one segment (literals, `*`, `?`, class, braces), or is exactly `**` (`(p:**)`); no nested groups | `(apl:*/*)`, `(root:*/*-a)`, nested groups, `(path:bin/**)/x` (becomes `bin/(path:**)/x`, value without `bin`), example 3 `(pat:**/*)`: 3.x tests only | ~62; also removes M1, group transparency and "group = sum" |
| 3 numbering | one scheme: number top-level bare wildcards only (as `format` already does) | `getRuleMatch "2"` for a `*` inside a group; no use found outside tests | the W- ambiguity; ~120 rule vectors lose their inner positional keys |
| 8 UNC | no UNC root; `//` collapses like any other run of separators (3.x behaviour) | nothing that 3.x supported | ~20, XP016 |
| 8 roots (option) | no roots in the grammar: absoluteness comes from the anchor (`ls dir mask`, `BaseDir`) | `!! "/opt/lib/*.so"`, `!! (escapePath toolDir + "/*.dll")` → `ls toolDir "*.dll"`; rules on absolute targets | ~120 (overlaps the UNC row); also removes XP016, XP120 and the drive rules |
| 13 braces | alternatives contain no `/` and no nested braces | `{src,test/unit}/*.cs` (write two includes) | ~23; also removes the union walk and XP019 |
| 15 `DoubleStar` | `a**b` is an error, as in older globset | `a**b` (3.x already meant something else by it) | ~18; also removes a token and XP112 |
| 21 reserved errors | one code "reserved or unbalanced" instead of six | only the error text | none removed, ~6 codes fewer |
| 6 specificity rules | `Sep` never counts (not only next to `**`); drop `Alt`-min and group-sum once rows 2 and 13 are simplified | ties change only for patterns with different separator counts that match the same target | ~15 |

**Migration debt** (rows 7, 9, 11, 12, the 3.x clauses in all 17 reasons, and part of 15 and 16):
about 25–30% of the vectors and most of the spec's sections 3, 5.2, 7.3 and 10. It is removed in
5.0, but item 7 creates new debt there. In 5.0 `\` changes meaning a second time (escape for
`* ? , ! ^ - \`, error XP009), and every `unparse`/`escape` output changes from `[*]` to `\*`.
Making `\` a plain separator for good (as Ant and the .NET `Matcher` do) and keeping `[c]` as the
only escape (it works in 3.x, POSIX, gitignore and globset) would remove XP110, XP017 and XP009,
the D34 special case, and the 5.0 switch, about 40 vectors. It would also make XP105 pointless,
because `[(]` would be the recommended spelling again. It contradicts brief (a) and tm 55 ("`\` is
the escape"), so it is a decision for the owner, not a defect. Separately, the in-build legacy
warnings could become a one-off lint run (`xake --check-patterns`, or a script) plus the release
notes. That removes `MaskRole`, the evidence hooks in the matcher and the walker, and the warning
buffer from Core: about 71 vectors that then move to the lint.

Total, with overlap: the accidental rows without the roots option remove about 140 vectors; with
legacy warnings moved to a lint and the `\` hybrid replaced, about 250 (~28% of 902); with the
roots option, about 330 (~37%).

## 4. If starting from scratch

The minimal grammar for a tool whose rules produce targets from captures and whose filesets
include and exclude:

```ebnf
rule     = path ;                               (* never ends with "/"; anchored at the project root *)
mask     = path , [ "/" ] ;                     (* trailing "/": a directory; an excluded one is pruned *)
path     = { "../" } , segment , { "/" , segment } ;   (* relative only; the anchor carries absoluteness *)
segment  = "**"                                 (* zero or more whole segments *)
         | "(" , name , ":" , "**" , ")"        (* rule only: a captured globstar *)
         | part , { part } ;
part     = literal | "*" | "?" | class | alt
         | "(" , name , ":" , gpart , { gpart } , ")" ;  (* rule only; inside one segment; no nesting *)
gpart    = literal | "*" | "?" | class | alt ;
alt      = "{" , word , "," , word , { "," , word } , "}" ;   (* no "/", no nesting *)
word     = { literal | "*" | "?" | class } ;
class    = "[" , [ "!" | "^" ] , citem , { citem } , "]" ;   (* "]" first is literal; never "/" *)
citem    = cchar , [ "-" , cchar ] ;
literal  = char , { char } ;                    (* a metacharacter as a literal: "[*]", "[(]" ... *)
name     = ( letter | "_" ) , { letter | digit | "_" } ;
```

Semantics in one table's worth: `*`/`?` within a segment; `**` whole segments only (anything else
is an error); dotfiles matched; one comparer per OS; captures are the named groups plus the
top-level bare wildcards, numbered once; `"0"` is the whole path; `format` fills exactly those
slots, so classes and braces are formattable only inside a group; specificity is (literal
characters, −globstars, −wildcards), with separators not counted; excludes apply to earlier
includes.

What it drops, and why:

- Roots, drives and UNC: absoluteness belongs to the anchor (`ls dir`, `BaseDir`), and every
  Windows path question (`C:`, `\\server`, `\`) stays out of the pattern text. This drops
  XP016, XP120 and the collapse rules.
- `\` in any role, so `[c]` is the only escape: one migration only, valid in 3.x, no 5.0 switch.
  This drops XP110, XP017, XP009 and the `escape`/`escapePath` split (one `escape` is enough
  once paths are not patterns).
- Groups spanning segments and nested groups: no use outside tests. This drops group
  transparency, the M1 regexes and the two numbering schemes.
- Braces with `/` or nesting: drops the union walk, XP019 and the comma stack.
- Segment-local `**`: an error, so one token and no XP112.
- In-build legacy warnings: replaced by a lint and the release notes; no `MaskRole`, no evidence
  hooks.
- Reservation codes: one code for "reserved or unbalanced".

What it keeps that gitignore lacks: named captures, `format`, multi-output name sets, specificity,
directory pruning. Rough size: ~350 lines of parser instead of 868, ~8 error codes instead of 17,
no warnings in Core, ~550 vectors instead of 902. Against spec section 4: `root`, `unc`, `drive`,
`legacy_sep`, `escape`, the `comma` context rule, the 4.3 transparency clause, `group_body =
{ r_element }`, and the extglob reservation note (D28) all disappear. `dir_mark`, `class` and
`braces` stay in reduced form.
