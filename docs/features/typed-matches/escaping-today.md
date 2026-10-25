# Escaping today: special characters in rule patterns and fileset masks

Measured on `src/core` as of this branch (= `dev` before the escaping change of PR #90), macOS, netstandard2.0 build. Probe: `experiments/11-escaping-today/probe.fsx`, raw output: `output.txt` (every claim below is a line there).
Rules were probed through `Path.matchGroups pattern root file` (what `ExecCore.explainMatch` and `makeTarget` call for
`..>`, `*..>` and `=>`; phony uses root `""`). Masks were probed through `Fileset.toFileList` over a real temp tree.

## How the two encodings work

**Rules (A).** `PatternAst.parse` tokenizes the pattern (`*`, `**`, `?`, `(name:` ... `)`; everything else is `Lit`).
`toRegex` escapes only `. $ ^ + ! = { }` in literals and passes every other character, including `( ) [ ] | \`
(the last is turned into `/` before parsing), straight into a .NET regex `^...$`. The project root is
prepended **as a raw string into the same regex, unescaped**. So a rule pattern is "Ant glob + raw regex".
Group numbering is positional over unnamed regex captures: each `*`, `**`, `?` and each bare `( )` is one number,
left to right; a named group `(n:...)` adds the name but takes no number itself. `applyWildcards` (multi-file target
name generation) replaces `*`/`**`/`?` in the other patterns by number, and `(tag:text)` by name.

**Masks (B).** `Path.parse` splits on both `/` and `\`, classifies each segment: has `*` or `?` -> `FileMask` /
`DirectoryMask`, else `FileName` / `Directory` (literal). Includes (`!!`, `ls`, `includes`): masks go to
`Directory.EnumerateFiles/Directories(mask)` (the OS glob: only `*` `?`), literals go through `Path.Combine`
(explicit names are listed even if absent). Excludes (`--`, `excludes`): masks segments go through the same
`PatternAst` regex as rules, literal segments are compared case-insensitively as plain strings (no regex).
So the regex dialect appears in masks only for exclude segments that contain `*` or `?`.

## Table (`X` = the char in `aXb.txt`; E/I = exclude/include mask, R = rule)

| char | R: rule pattern | I: include mask | E: exclude mask (segment has no `*?`) | E: segment has `*`/`?` |
|---|---|---|---|---|
| `(` | regex group open; lone `(` -> `RegexParseException` crash; `(word:` opens a named group | literal | literal | regex group; lone `(` crashes (not probed in masks; same code) |
| `)` | regex group close; lone `)` -> `RegexParseException` ("Too many )'s") | literal | literal | regex (same) |
| `[` | regex class open; unterminated -> crash (`Unterminated [] set`) | literal | literal | regex class |
| `]` | passes as regex literal when unpaired (match works) | literal | literal | regex |
| `\|` | regex alternation over the whole `^...$` (see below) | literal | literal | regex alternation |
| `{` `}` | escaped, literal (`a{2}` matches only `a{2}`) | literal | literal | escaped, literal |
| `+` `.` `^` `$` `!` | escaped, literal | literal | literal | escaped, literal |
| `#` | literal (not special in .NET regex without x-flag) | literal | literal | literal |
| `\` | path separator, normalized to `/` in pattern AND file; never an escape | separator in the mask | separator | separator |

## Per-case outputs (rules, root `/proj`, from output.txt)

- `[(]x[)]` matches `(x)`; does NOT match `[(]x[)]`. `[abc]*.txt` matches `a1.txt` (1=`1`), not `[abc]1.txt`.
  `lib[x].dll` matches `libx.dll`, not `lib[x].dll`. Phony `build[x]` matches `buildx`, not `build[x]`.
- `fun(ction).o` matches `function.o` (1=`ction`), NOT the literal `fun(ction).o`.
- `a(b.txt`, `a)b.txt`, `a[b.txt`, `(name:a)b)c`, phony `a(b`: crash with `RegexParseException`.
- `a|b.txt` matches `a.txt` and `b.txt` and `a|b.txt`, and `aXb.txt`: the regex is `^/proj/a|b\.txt$`, i.e. "starts
  with `/proj/a`" OR "ends with `b.txt`". Phony `a|b` matches `xb` and `a`, not `c`. `a||b.c` matches `z.c`.
- `(foo)/*.c` matches `foo/a.c` (1=`foo`, 2=`a`), not `(foo)/a.c`: a bare paren group is an unnamed regex capture
  and consumes a positional number (the `*` becomes 2). Same for `(foo)/(name:*).c` (1=foo;2=a;name=a).
- `(name:*)(x).c` matches `ax.c` (1=a;2=x;name=a), not `a(x).c`. `(name:*)[(]x[)].c` matches `a(x).c` (1=a;name=a).
- `((name:*)).c` matches `(a).c` with 1=`(a)`;2=`(a)`;name=`(a)`: the outer parens are a regex group, not literals.
- `(name:[(]*).ss` matches `(abc.ss` (name=`(abc`; the XakeScriptTests case); `[(]*.ss` same, 1=`abc`.
- `(a:b` (unclosed) -> no crash, group a=`b` (the closer is optional); `(name:*` matches `xyz`.
- `(1:*).c`: group named `1` collides with positional 1 (MATCH 1=x). `(a-b:*)`, `(a b:*)` are not groups (literal
  parens -> regex groups; no match).
- `!a.txt`, `#a.txt`, `a#b`, `a$b.txt`, `a^b.txt`, `a+b.txt`, `a.b.txt`, `a{2}`: literal matches only.
- Root containing specials (project root literal goes into the regex): `/tmp/p(q)` and `/tmp/p[q]` -> NO MATCH for
  every file rule (`(q)` reads as group matching `pq`; `[q]` as a class: `/tmp/p[q]` matches the file under
  `/tmp/pq/`); `/tmp/p(q` and `/tmp/p)q` -> `RegexParseException` for every rule lookup; `/tmp/p|q` matches
  (alternation, `1=` empty, matches paths outside the root); `+ . { } $ ^ ! #` in the root are fine. The same code
  path works on the empty phony root, so only file/multi-file rules suffer.
- `\`: `a\*.c` == `a/*.c`; file `a\x.c` matches `a/*.c`; `a\b.txt` matches `a/b.txt`; `[\\]x` is read as `[//]x`;
  `a\(b` -> crash (it is `a/(b`); `a\*b` does NOT match the file `a*b` (`*` stays a wildcard, `\` is a separator).
  Phony `a\b` matches `a/b` and `a\b`.
- Multi-file generation: `*.c`, `(n:*).c`, `[(]*.c` all give `out/*.o` -> `out/x.o` (positional, name-agnostic).

## Per-case outputs (masks, temp tree with real files)

- Include `[(]x[)].txt` -> only a file literally named `[(]x[)].txt`; `[abc]*.txt` -> only `[abc]1.txt`; `[(]*.txt` and
  `[a]*.txt` -> nothing; `*.[ch]` -> nothing. `a|b.txt`, `(x).txt`, `lib[x].dll`, `fun(ction).o`, `{x}.txt`,
  `a+b.txt`, `a^b.txt`, `a$b.txt`, `a!b.txt`, `!a.txt`, `#a.txt` -> each its own literal file. `(foo)/*.c` -> `(foo)/a.c`.
  `(n:*).c` -> nothing (the OS glob sees literal `(n:` ... `)`). `*(x)*` -> `(x).txt` (OS glob, `(x)` literal).
- Include `a\b.txt` -> `a/b.txt` (explicit, listed although absent; the real file named `a\b.txt` is unreachable by
  name, but `!! *` lists it).
- Exclude (on `*.txt`): `[(]x[)].txt` removes nothing (literal compare), `(x).txt` removes `(x).txt`, `a|b.txt` removes
  only `a|b.txt`, `[x].txt` removes `[x].txt`, `lib[x].dll` nothing; with a wildcard, regex dialect: `[abc]*.txt` removes
  `a*`/`b*`/`c*` names (and keeps `[abc]1.txt`, `d3.txt`), `[(]*.txt` removes `(a.txt`/`(x).txt`, `(n:*).txt` removes
  everything (named group works in excludes), `*(x)*` removes every name containing `x` (incl. `.txt`), `a?b.txt` ok.
- Exclude `a\b.txt` removes nothing named `a\b.txt` on disk: the mask is dir `a` + file `b.txt`, and the file's path
  `a\b.txt` is also split at `\` for matching, so it cannot be matched or excluded by exact name either way.
- Basedir `w(1)[2]`: includes and excludes work (`Directory` parts are compared as plain strings, never regex), so
  the root problem is rules-only.
- Case: `Path.parse "[a/b]*.c"` -> `Directory "[a"; FileMask "b]*.c"` (a `/` inside a class splits the segment).

## Where rules and masks differ

1. Dialect: rule = whole-string regex (specials live), include mask = OS glob (`[`,`(`,`|` literal), exclude mask =
   regex only for segments with `*`/`?`, literal string compare otherwise. The same text `[(]x[)].txt` therefore means
   "`(x).txt`" as a rule, "file named `[(]x[)].txt`" as an include, and "nothing" as an exclude.
2. Existing escape idiom `[(]` works in rules and in wildcard excludes, not in includes, not in wildcard-less excludes.
3. Named groups are regex features in rules/excludes; in includes they are inert literal text.
4. `\`: separator in rules (pattern and target), include and exclude masks alike.
5. Crashes (`RegexParseException`) exist only on the regex paths (rules; exclude segments with a wildcard).
6. Rule root is unescaped in the regex; mask roots are never regexes.

## Existing docs, tests and samples that touch this

- Tests: `PathTests` `c:\[(]*[)].c` / `c:\[(]*.c` (comment "we are not smart enough yet, so want brackets escaped"),
  `XakeScriptTests` `(name:[(]*).ss` -> `name:(abc`, and `[(]*.ss`. All are wildcard patterns through the regex path.
  `FilesetTests` has no special-char cases. No test covers `|`, `\`-escapes, bare groups or a root with specials.
- `docs/overview.md` (rule selection) and `docs/release-notes.md` describe literals / wildcards / named groups only;
  `docs/implnotes.md` says only "Ant style masks, `**`, named match groups". No doc mentions escaping or `[x]`.
- Samples and docs use only `(name:*)` groups with `/`, `.`, `-`, `::` (e.g. `lock::(name:*)::(cfg:*)`), `*`, `**`;
  no sample uses `[`, `|`, `{`, or a bare `( )` group. So the working surface people rely on is `(name:..)`, `*`, `**`, `?`.

## Escape-syntax candidates that do not collide with working inputs (facts only)

- Single-char class `[c]` as the literal escape: works today in rules (`[(]`, `[.]`, `[*]`? not probed for `*`/`?`
  since those are tokenized before the regex) and in wildcard excludes; does NOT work in includes (OS glob) or in
  wildcard-less excludes; collides with real regex classes (`[abc]`, `[a-z]` keep working today in rules; only
  a one-char class means "literal"); `[(]` is already in tests. Inside a class `]` ends it, so `[]]` is unexpressible.
  `[*]`/`[?]` do not escape wildcards (the tokenizer runs before the regex sees them: `*` inside `[*]` becomes a star group).
- Doubling (`((`, `))`, `||`): `((` is today a literal `(` followed by a group opener only when followed by a word
  and `:`; `((name:*))` already means something (outer regex group); `))` and `||` are valid regex today (`||` matches
  `z.c`). Collides with working inputs `a||b`-style alternation, nested groups and `(x)(y)`.
- Backslash `\(`: today `\` becomes `/` before parsing, so `a\(b` is path `a/(b` (and crashes as a lone `(`); every
  Windows-style pattern (`src\*.c`, `c:\x`, PathTests) uses `\` as the separator, targets and patterns alike. It would
  collide with all of these unless Windows separators are decided separately (e.g. only `\` before a special char).
- Other chars before `(`/`[`: `!`, `#`, `$`, `^`, `+`, `{`, `}` are already escaped-literal in rules, so none can be a
  prefix escape without changing today's meaning of `a#b`, `!x`, `a$b` (all literal matches today).
- Quoted/raw form (e.g. a leading marker or `"..."`): no existing char is free in the way above except none of
  `! #` as a leading marker (both are literal in `!a.txt`, `#a.txt`, which work today as file names).
- Any scheme must be applied in three places that encode the grammar separately: `PatternAst` (rules, excludes),
  `Path.parse`/`isMask` plus the OS glob (includes), and `applyWildcards`/`patternTagRegex` (multi-file name generation).
