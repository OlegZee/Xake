# Critique: the pattern-module spec against mainstream glob conventions

Reviewed: `spec.md` (draft, revision 2026-10-31), against `brief.md` sections a–f and D1/D2 (taken
as fixed). References, from the reviewer's knowledge (no web access; uncertain points are marked
"(unverified)"):

- **POSIX** glob(7) / fnmatch with `FNM_PATHNAME`, `FNM_PERIOD`; **bash** (incl. `globstar`, extglob)
- **gitignore** (wildmatch)
- **pico** = picomatch / micromatch / minimatch (JavaScript)
- **globset** (Rust, ripgrep)
- **MSFG** = Microsoft.Extensions.FileSystemGlobbing (`Matcher`)
- **Ant** fileset patterns (Xake's historical model)
- **Java** `FileSystem.getPathMatcher("glob:...")`

Verdicts: **conforms**; **deliberate** (deviation the spec states and justifies); **accidental**
(deviation the spec does not seem aware of); **underspecified** (the spec does not say).

## 1. Element-by-element comparison

| element / policy | Xake 4.0 spec says | mainstream says | verdict |
|---|---|---|---|
| `*` | 0+ chars except `/`, within a segment (5.1, L304) | all agree with `FNM_PATHNAME`; Java same; Ant same | conforms |
| `*` and a leading `.` | matches it, every context (5.3) | gitignore, globset, Ant, MSFG, Java: matches; POSIX `FNM_PERIOD`, bash (no `dotglob`), pico (`dot:false`): does not | deliberate, justified (3.x, open item 3); Ant additionally has *default excludes* (`**/.git/**`, ...) |
| `?` | exactly 1 char except `/` (L305) | all agree; Win32 listing quirks dropped (6.3) | conforms; "1 char" = one UTF-16 code unit via .NET regex, globset matches one Unicode scalar: underspecified (finding C2) |
| `**` whole segment | 0+ whole segments (5.5) | gitignore, globset, pico, bash `globstar`, Ant, MSFG agree; Java `**` is "any chars incl. `/`", so `**/b` does **not** match `b` | conforms (spec L1066 "every mainstream globber" overstates: Java differs) |
| `**/b` at start | `b`, `x/b` | as above | conforms |
| `a/**` at end | every file below `a`, not `a` | gitignore and globset: everything inside, not `a`; pico/minimatch and bash also yield `a` itself in some versions (unverified); Ant DirectoryScanner includes the dir `a` in dir sets | conforms with gitignore/globset |
| `a/**/b` | `a/b`, `a/x/b` | all agree (Java needs `a/x/b`) | conforms |
| `a**b`, `**.cs`, `***` | like `*`, XP112 in rules (4.3) | gitignore ("other consecutive asterisks are regular asterisks"), pico, bash: like `*`; globset: an error in older versions, like `*` or error depending on version (unverified); Java: crosses `/` (= 3.x rule meaning) | conforms with the majority; deliberate |
| `**` captures | never leading/trailing `/`, `""` for zero (L409) | no mainstream equivalent (no captures) | underspecified in 5.8: the regex table cannot produce the stated values for a globstar at a group edge (finding M1) |
| `[abc]`, `[a-z]` | one char of the set; never `/` (L244) | POSIX, bash, gitignore, pico, globset, Java; none in MSFG, Ant | conforms |
| negation `[!a]` and `[^a]` | both negate (L222) | POSIX: `!` only (`^` unspecified); bash, gitignore, pico, globset: both; Java: `!` only (`^` handling unverified) | conforms |
| `]` first, `-` first/last | literal (L223–224) | POSIX, bash, gitignore, globset agree | conforms; but makes the `[]` error unreachable (inconsistency I4) |
| `\` inside a class | escapes the next char (L226) | gitignore, pico, bash, glibc fnmatch: yes; POSIX leaves it undefined-ish; globset: no escape in classes (unverified) | conforms; but the context-free lexer of 4.1 cannot express it (finding M3) |
| `/` inside a class | error XP010 | POSIX with `FNM_PATHNAME`: the class can't match `/` (glob(7) treats such brackets specially, unverified); globset with `literal_separator`: never matches `/`; pico: never matches | deliberate, stricter; fine |
| `[]`, `[z-a]` | error XP010 | `[]` alone: literal in POSIX/bash/pico (unclosed); `[z-a]`: globset error, bash empty set | deliberate; fine |
| POSIX names `[[:alpha:]]` | error XP010 (kept for later) | POSIX, bash, gitignore, pico support; globset, Java do not | deliberate (keeps syntax open); fine |
| `[c]` | the char `c` (XP105/XP106 in 4.0) | POSIX one-char class | conforms (brief a) |
| unclosed `[` | error XP002 | POSIX, bash, gitignore, pico: literal `[`; globset, Java: error | deliberate (reserved set, brief b); has precedent |
| stray `]`, `}` | error XP004 | literal in POSIX, bash, gitignore, pico; globset: `}` error, `]` literal (unverified) | deliberate (reserved set); XP004 text covers only `{` (I9) |
| `{a,b}` | alternation, every context; alternatives may hold `/` | pico, bash, globset, Java; **not** gitignore, MSFG, Ant | conforms with glob libraries; gitignore readers will not expect it, fine |
| nested braces | allowed (L229) | bash, pico: yes; globset, Java: error ("nested alternate groups") | conforms with bash/pico; fine |
| empty alternative `{a,}` | allowed | bash, pico, globset: allowed | conforms |
| `{x}` (no comma) | error XP005 | bash, pico: literal `{x}`; globset, Java: one alternative = `x` | deliberate, justified (keeps `{1..3}`); spec L242 cites only pico (finding C9) |
| `{1..3}` ranges | not supported, reserved via XP005 | bash, pico: yes; globset, Java: no | deliberate; fine |
| `,` outside braces | literal | everyone | conforms |
| `\,` inside braces | `\` + non-reserved = separator, so `{a\,b}` is `a/` and `b` | bash, pico, globset: escaped comma | accidental; underspecified (finding S6) |
| brace semantics in includes | matched against existing names, not expanded (5.6) | bash: textual expansion regardless of existence; fast-glob/micromatch: expand into patterns, then glob (existing only) | conforms with glob libraries; deliberate |
| named groups `(name:...)` | rule patterns only; in masks body + XP111 | no equivalent; `( | )` reserved for extglob (brief a) | deliberate; extglob compatibility needs a note (finding C6) |
| `\` escape | escapes only `( ) [ ] { } \|` in 4.0; before anything else a separator (XP110); in 5.0 also `* ? , ! ^ - \` | POSIX, bash, gitignore, Java: escapes any char; globset: escape on Unix only (`backslash_escape`), separator-ish on Windows; pico: escape (Windows handling via options, unverified); MSFG, Ant: `\` is a separator, no escape | deliberate hybrid (brief b tm 55/56, open item 15); justified; consequences in findings S3, S6 |
| no `\*`, `\?` in 4.0 | `[*]`, `[?]` | the one-char class works in every POSIX-style globber | deliberate; fine, the workaround is portable |
| `/` vs `\` as separator | `/`; `\` deprecated separator | gitignore, pico, globset (Unix), Java (Unix): `/` only; MSFG, Ant: both | conforms (target) with a transition |
| `//`, `\\` | `\\` = `//` (L189–190); empty segment meaning not defined | pico: literal `//`, never matches normalized paths; gitignore: `//` collapses (unverified) | underspecified (finding S6) |
| trailing `/` | rule: error; include: directories only; exclude: dir and everything below | gitignore: "directories only", and an ignored dir hides its content (= the exclude meaning); bash/pico: `*/` lists dirs (= the include meaning); Ant: `x/` = `x/**` | conforms (gitignore for exclude, bash for include); deliberate |
| leading `/` | absolute file-system path; the base dir is not joined (L253, L319) | gitignore: anchors to the `.gitignore` directory (relative!); pico, globset, Ant: absolute path; MSFG: relative to root (unverified) | conforms with pico/Ant, but **contradicts gitignore silently** (finding S1) |
| anchoring without `/` | whole path from the base dir, no floating (ex. 5) | gitignore: a pattern without `/` floats to any depth; minimatch `matchBase` same; others: anchored | deliberate, documented (ex. 5) |
| leading `!`, `#` | literal (ex. 12) | gitignore: negation, comment; pico: leading `!` negates | deliberate; Xake uses `++`/`--`; fine |
| case | ordinal on Linux/macOS, ignore-case on Windows, classes follow (5.4) | Java: per platform (same as Xake); MSFG: ignore-case by default on all OSes; pico, globset, Ant: sensitive by default with an option; gitignore: `core.ignorecase` | conforms with Java and .NET `File` semantics; deliberate; macOS FS mismatch (finding C5) |
| whole path vs basename | whole path, anchored | gitignore no-slash and minimatch `matchBase`: basename | deliberate (ex. 5) |
| symlinks | globstar does not descend into reparse points; one-segment wildcards do (6.4) | bash ≥ 4.3 `**`: does not follow dir symlinks; ripgrep: no follow by default; Java `Files.walk`: no follow; fast-glob, Ant, MSFG: follow | conforms with bash/ripgrep/Java; "reparse point" is wider than "symlink" on Windows (finding S5) |
| include/exclude | ordered fold; an exclude affects only earlier includes (6.2) | gitignore: last match wins incl. negation; MSFG, Ant, fast-glob `ignore`: excludes global, order-free | deliberate (3.x), but not listed as a difference (finding S4) |
| explicit names | a mask without non-literals is listed even if absent (5.6) | bash: a word without glob chars passes through; MSFG/Ant/fast-glob: existing only | deliberate (3.x, `need`); fine |
| unclosed constructs | `[` XP002, `{` XP004 errors; unclosed `(name:` in a string rule silently runs to the end | globset, Java: errors for `[`, `{`; POSIX, bash, pico: literal | deliberate; the group exception is brief (d) tm 61, C11 already asks |
| ordering, duplicates | ordinal per directory, pre-order, dedup | fast-glob: `unique:true`, no order guarantee; bash: sorted per expansion | conforms/better; fine |
| dot entries `.`/`..` | never produced; `..` after a wildcard is XP012; `.` after a wildcard not specified | pico: `.`/`..` never matched by wildcards | underspecified for `.` (finding C8) |

## 2. Findings (ordered by importance)

### Must-fix

- **M1. Globstar at a group edge: the regex table does not yield the captures the spec promises.**
  5.5 (L406–407) says `(p:**)/b` gives `p = ""`/`x` and `(path:bin/**)/file.o` gives
  `path = bin`/`bin/x/y` "(no trailing `/`)". 5.8 (L473–476) translates a globstar followed by a
  separator to `(?:(.*)/)?` *consuming the `/`*; when that `/` is outside the group, the group
  either swallows it (`path = bin/x/y/`) or the `/` is emitted twice (`bin/` then `(?:..)?` then
  `/file\.o`, which no longer matches `bin/file.o`). Add explicit rows for "globstar last in a
  group body, separator after the group" and "globstar first in a group body, separator before
  it", e.g. `(?<path>bin(?:/(.*))?)/file\.o` and `(?:(?<p>.+)/)?b` (with `p` = `""` when the
  optional part is skipped), and pin them as conformance vectors. 3.x pinned `(path:bin/**)` in
  `PathTests`, so the values matter for brief (e).
- **M2. Class items are not escaped in the regex translation.** 5.8 (L477–478) emits
  `[items]` / `[^/items]` raw. A class may contain `]`, `\`, `^`, `-`, `[` (`[]a]`, `[\\]`,
  `[a^]`, `[-[]`); .NET needs them escaped, and `-[` inside a .NET class is *character class
  subtraction*. Specify that each `One c` is emitted escaped and each `Range` as `\a-\b`. Aligns
  with how globset/pico compile classes.
- **M3. The lexer is context-free but classes need their own lexical mode.** 4.1 (L152–153) says
  the lexer is the only place escapes are resolved and emits `legacy_sep` for `\x`, `star` for
  `*`, `comma` inside braces, `lbrack` for `[`; 4.2 (L225–226) then defines `class_char` on raw
  characters with `\` escaping *anything* inside a class. As written, `[\-a]` lexes as separator +
  `-a`, `[*]` as a class containing a `star` token, `{[,],x}` splits at the class's comma.
  Specify a class mode in the lexer (POSIX, gitignore and pico all lex brackets as one unit), and
  state that `!`/`^`, `,`, `*`, `?`, `{`, `(`, `[` are ordinary inside it. 6.1 (L536–537,
  `Path.parse` splits at a `\` that escapes nothing) must also not split inside `[...]`.

### Should-fix

- **S1. Leading `/` means file-system root, gitignore means base dir.** 5.1 (L319) and 4.2 (L253).
  A gitignore reader (goal 2, L47–49) writing `-- "/bin/**"` gets an exclude that matches
  nothing on Unix, silently. Keep the meaning (pico, Ant, globset agree with it), but list it in
  the differences and add an evidence warning or error when an exclude is absolute and its literal
  prefix lies outside the fileset's base dir (it can never remove anything found below the base).
- **S2. Repeated group name across brace alternatives.** 5.7 (L438–441) makes every later
  occurrence a backreference `\k<n>` (5.8 L481). `{(x:a)-1,(x:b)-2}` or
  `out/{(cfg:Debug)/d,(cfg:Release)/r}` is a natural spelling; with the rule, the second
  alternative can never match. .NET allows the same group name in different alternation
  branches. Define: occurrences in *different alternatives of the same Alt* are independent
  definitions; a backreference only when an earlier occurrence is on the same match path. Also say
  what `groupNames`/XP013 do with a name present in only some alternatives (its value is `""`
  when unmatched).
- **S3. Escaping real paths, especially Windows ones.** 4.0 rejects unescaped `( )` (XP003), so
  `@"C:\Program Files (x86)\Tool" </> "*.dll"` (a mask built from a real path with `</>`, which
  the spec keeps, tm 57) now fails to load, where 3.x read it literally in masks. `escape`
  (5.9 L512–515) would turn every `\` of such a path into `[\\]`, i.e. a literal backslash, not a
  separator. Add `escapePath` (normalize `\` to `/`, then escape; drive letters kept) or define
  `escape` that way, mention it in the XP003 text for masks, and add the `Program Files (x86)`
  case to section 3 / 10.3. pico offers the same split (escape vs path utilities).
- **S4. Write the promised list of deliberate differences.** Goal 2 (L47–49) says "section 11
  lists the differences"; section 11 holds worked examples and covers only dotfiles (ex. 4),
  anchoring (ex. 5) and `!`/`#` (ex. 12). Add a short table, one line each, with the reference it
  departs from: no floating basename match (gitignore, `matchBase`); leading `/` absolute
  (gitignore); `!`/`#` literal (gitignore, pico); dotfiles matched (pico, bash, POSIX
  `FNM_PERIOD`); `{x}` an error (pico/bash literal, globset/Java `x`); unclosed `[`/`{` and
  stray `]`/`}` errors (POSIX, bash, pico literal); `\` hybrid until 5.0 (everyone); excludes
  ordered, affect only earlier includes (MSFG, Ant, fast-glob global; gitignore last-wins);
  explicit names listed even if absent; case per OS (MSFG insensitive, pico/globset sensitive);
  braces not in gitignore/MSFG/Ant; globstar not following reparse points.
- **S5. "Reparse point" is wider than "symlink" on Windows.** 6.4 (L589–592). Besides symlinks
  and junctions, volume mount points and cloud-file placeholders (OneDrive "Files On-Demand"
  folders, unverified) carry `ReparsePoint`; `**` would silently skip such source trees while
  `src/*/*.cs` (one-segment wildcard, L587–588) still enters them, so `**/*.cs` can find fewer
  files than `*/*.cs`. bash ≥ 4.3 has the same `*/` vs `**` asymmetry, but bash only skips
  symlinks. Recommend: report a skipped reparse directory once in the build log (as for
  unreadable dirs, L607–609), state the `*/` vs `**` asymmetry in 6.4 and the release notes, and
  consider runtime detection of `LinkTarget` (via reflection on .NET 6+) to skip only links.
- **S6. Define `//`, `\\`, `\,` and UNC roots.** L189–190 says `\\` means `//`, but nothing says
  what an empty segment matches; since paths are normalized, a pattern with `//` silently matches
  nothing (pico behaves the same; gitignore collapses, unverified). Either collapse repeated
  separators after the root or make an empty segment an error. A UNC root (`\\server\share\*.cs`,
  `//server/share/...` after normalization) is not covered by `root = sep | drive sep` (L211);
  add it. `{a\,b}` (L186–190 + L180) is `a/` and `b` in 4.0; bash, pico and globset read an
  escaped comma; at least warn (XP110 fires, but its text says "path separator", which is
  misleading inside braces).
- **S7. Specificity counts the `/` absorbed by a globstar as a literal.** 5.10 (L519–523) and
  example 9 (L928–929): `**/*.dll` (5) beats `*.dll` (4) for `a.dll`, so a catch-all rule
  overrides a root-specific one as soon as both match. Not a glob convention, but a direct effect
  of adopting the mainstream zero-segment `**`. Count a `Sep` adjacent to a globstar as 0; then the
  tie breaks on `-globstars` and `*.dll` wins, which is what most users expect. If kept, say why.

### Consider

- **C1. `.` vs newline.** `(.*)` (globstar, L473–476) does not match `\n`, `[^/]*` (star) does;
  Linux names may contain a newline. Use `RegexOptions.Singleline` (or `[\s\S]`) so `**` and `*`
  agree.
- **C2. What "one character" is.** `?` and a class match one UTF-16 code unit; globset matches a
  Unicode scalar value. An emoji or other non-BMP char needs `??`. State it, or translate `?` to
  `(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^/])`.
- **C3. Unicode normalization on macOS.** APFS/HFS+ names may be NFD while F# literals are NFC;
  a literal probe finds the file (the FS normalizes), a wildcard segment such as `café*` compared
  ordinally does not. No mainstream globber normalizes either; document it as a known limit.
- **C4. One comparer, two implementations.** 5.4/9 use regex `IgnoreCase|CultureInvariant` and
  fast paths with `StringComparer.OrdinalIgnoreCase` (L811–812); they disagree on some non-ASCII
  letters (e.g. Kelvin sign, dotted/dotless i; unverified per .NET version). Require the fast
  paths to be a proven subset, or compare by `ToUpperInvariant` in both.
- **C5. Probe spelling vs ordinal matching.** A literal include probe keeps the mask's spelling
  (5.6 L426–427); on default macOS `!! "SRC/a.cs"` yields `SRC/a.cs`, which a later ordinal
  exclude `src/**` or a rule `src/*.cs` will not match although it is the same file. MSFG avoids
  this by being insensitive everywhere. Either take the disk spelling for probed segments on
  case-insensitive volumes (one listing per probed dir) or document the trap next to example 15.
- **C6. Room for extglob.** Brief (a) reserves `( | )` for extglob; `*(name:x)`, `?(name:x)` and
  `+(`/`@(`/`!(` sequences: the last three are already XP003, but `*(`/`?(` followed by a group
  are valid 4.0 syntax that extglob would read as `*(...)` / `?(...)`. Note in 4.2 that a future
  extglob must keep `(name:` as a group, or reserve `*(`/`?(` followed by a group now.
- **C7. OS pre-filter safety.** 6.3 (L579–580) passes literal text to
  `Directory.Enumerate*`; on Windows .NET maps `"`, `<`, `>` to DOS wildcards (unverified for
  .NET Core on Unix). Pass `"*"` whenever the segment has a char outside a safe set, so the
  filter is guaranteed to be a superset.
- **C8. Small gaps.** The empty pattern `""` (rule and mask); `.` after a wildcard (only `..` is
  XP012, L257); `[a-z-0]` (a `-` after a range). One line each in 4.2.
- **C9. Accuracy of two claims.** L242–243 "picomatch reads `{x}` as a literal": true, but
  globset and Java read it as `x`; L1066–1067 "contradicts every mainstream globber": Java's
  `**/b` does not match `b`. Neither changes a decision.
- **C10. Ant default excludes as precedent.** Ant matches dotfiles but ships default excludes
  (`**/.git/**`, `**/.svn/**`, ...). Worth one sentence in 5.3 / open item 3 as the precedent for
  a later `dot`/default-excludes option.

## 3. Non-issues (deviations that are fine and already justified)

- Dotfiles matched by `*`, `?`, `**`, classes (5.3, open item 3): Ant/gitignore/globset/3.x.
- No floating basename match; a mask is anchored at the base dir (5.1, ex. 5).
- `!` and `#` are plain characters (ex. 12); `++`/`--` do negation.
- `{x}` an error and `{1..3}` reserved (L241–243, open item 1).
- Unclosed `[`/`{`, stray `]`/`}`, `( | )` outside groups are errors: the reserved set of brief
  (b); globset and Java also error on unclosed constructs.
- `/` in a class, `[z-a]`, `[[:alpha:]]` errors: stricter, keeps syntax open.
- `\` hybrid in 4.0 and `[*]`/`[?]` as the literal spelling (open item 15, brief tm 55–57).
- Trailing `/`: directories for includes (bash/pico), "dir and below" for excludes (gitignore).
- Segment-local `a**b` = `a*b` with XP112 in rules (gitignore, pico, bash).
- Braces in includes matched against existing names, not bash-expanded (5.6, open item 6).
- Explicit names listed even if absent (3.x, `need`).
- Case per OS, classes follow it (Java PathMatcher, .NET `File` equality).
- Ordered include/exclude fold (3.x semantics; only needs listing, S4).
- Globstar not following links (bash ≥ 4.3, ripgrep, Java `Files.walk`), modulo S5.
- Named groups only in rules, XP111 in masks (brief a; C11 open).
- Unclosed `(name:` silent in string rules (brief d, tm 61; C11 open).
- Ordinal order and dedup of results.

## 4. Internal inconsistencies

- **I1.** L87–88 (section 3 item 1) and L937–938 (example 11): `src\(x)\*.cs` "names the
  directory `src(x)`". Only the `(` is escaped; the `)` is unescaped, which is XP003 per L247,
  L314, L338. The example yields an error, not a directory (or must read `src\(x\)\*.cs`).
- **I2.** L406–407 (5.5 captures `p = ""`/`x`, `path = bin/x/y` without `/`) vs L473–476 (5.8
  globstar translations): not producible as written (finding M1).
- **I3.** L152–153 and L163–166 (context-free lexer, the only place escapes are resolved) vs
  L225–226 (`\` escapes any char inside a class) and L180 (comma inside braces): incompatible
  without a class lexical mode (finding M3).
- **I4.** L223 ("a `]` right after `[` ... is a literal") vs L245–246 / L670 (`[]` is XP010): `[]`
  is read as an unclosed class starting with `]`, so it is XP002 (or a class if a later `]`
  exists, `[]a]`); the XP010 case for `[]` is unreachable. Same for `[!]`, `[^]`.
- **I5.** L219 uses `comma_outside`, which no rule defines (4.1 only has `comma`, L180).
- **I6.** L536–537: `Path.parse` splits at a `\` that escapes nothing and exempts only `/` inside
  `[...]`/`{...}`; inside a class `\` is an escape (L226), so `[\\]` and `[\-]` would be split.
- **I7.** L48–49: "section 11 lists the differences"; section 11 is worked examples and lists
  only some (finding S4).
- **I8.** L311: "walked as the union (6.3)"; the union walk is 6.2 step 2 (L556–557); 6.3 is the
  OS listing.
- **I9.** L664: XP004's condition includes a stray `}` and a stray `]`, but its single reason
  text speaks only of an unclosed `{`; a stray `]` needs its own text (and `\]`).
