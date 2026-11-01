# Brief: the pattern module

Carried over from the frozen `feature/typed-matches` (reference prototype and test corpus, never
merged). That branch redesigned the grammar piecemeal while implementing; the user called it
"chaotic rethinking during implementation" and restarted: **spec first, then conformance tests,
then the module, then the engine on it, then the typed layer**. Only settled decisions are listed;
"tm N" = `typed-matches` brief decision N (`docs/features/typed-matches/brief.md` on that branch;
later rounds supersede earlier). Do not re-litigate; what is still open is in "Open for design".

Frozen reference code (read-only): `src/core/{PatternAst,Path,Fileset,TypedTargets}.fs`,
`src/tests/{EscapingTests,EscapingDiskTests,PatternAstTests,TypedTargets*,TypedCompileTests}.fs`,
`docs/overview.md` ("Patterns, masks and paths"), `docs/typed-targets.md`.
Facts about today's behaviour: `escaping-today.md` there (table of special characters, rules vs masks).

## (a) Grammar target

- **Masks (includes/excludes) follow mainstream glob** (gitignore / Rust globset / picomatch): `*`
  within a segment, `?`, `**`, classes `[abc]` `[!abc]` `[a-z]`, alternation `{a,b}`, escape `\`,
  separator always `/` (tm 59).
- **Rule patterns = the same lexis plus named groups `(name:...)`** and positional bare wildcards
  (`*`, `**`, `?`) (tm 59, 6).
- **Named groups exist only in rule patterns.** In a fileset mask a group warns at construction
  (`!!`, `ls`, `--`, ...) and becomes an error in a future major (tm 58).
- **`( | )` stay reserved** (bash extglob; not taken now) (tm 60).
- `[c]` = literal `c` is the one-character class of POSIX glob, so today's compat spelling is
  consistent with the target (tm 59).
- Departure from Ant: `\` is an escape, not a separator (tm 59).
- Bare wildcards are allowed in `Command` patterns, positional (tm 63).

## (b) Escaping and compatibility policy

POLICY (keep) vs HOW #90 did it (implementation, not binding; #90 was reworked 3 times).

POLICY:
- Reserved characters in rule patterns and masks: `( ) [ ] { } |`, except the named group
  `(name:...)` (tm 52). `{ }` are reserved because alternation is the natural next element.
- `\` is the escape: `\(`, `\{`, `\|`, ... Windows-style `src\(x)\*.cs` breaks on purpose (tm 55).
  F# string literals keep `\(` etc. unchanged (checked in fsi).
- `[c]` stays accepted for compatibility, with a warning suggesting `\c` (tm 55).
- `\` as a path separator in user-written patterns/masks is deprecated: still works in this
  release, warns "use `/`"; a later major makes `\` an escape only. Paths read from disk are
  still normalized (tm 56).
- In patterns `/` is used always and unconditionally. `</>` joins real system paths, stays
  OS-dependent and is not changed; Xake's own joining is not made to produce `/` (tm 57,
  supersedes the last sentence of 56).
- The project root is a literal in matching (a root with `(`/`[`/`|` mismatched or crashed
  before); a lone `(`/`)`/`[` gives a clear error instead of `RegexParseException` (tm 54).
- Warnings go through the Xake build log, **once per pattern** (tm 53).
- Rollout idea (tm 52, 60): this release keeps today's behaviour for reserved characters (regex in
  rules, literal in mask includes) and warns; a later major turns warnings into errors AND turns
  the reservation into the standard elements (classes, `{a,b}`) rather than only errors.
  Whether classes/braces ship earlier is open (see below).
- Break-softening principle: no silent change of meaning of `( ) [ ] |`; keep syntax open for the
  future (tm 48, supersedes the "literal" part of tm 15/28).

HOW #90 DID IT (non-binding): `PatternAst` in `Path.fs` with `format`/`glob`/positional wildcards;
`formatMatches` replacing `ExecCore.applyWildcards`; warnings via the build log; named groups in
masks: an include treats the group as literal text (finds nothing), an exclude uses its body;
`Cp` `Dir` builds its mask with `/`; tests in `EscapingTests.fs`, `EscapingDiskTests.fs`.

## (c) One AST, one matcher

- One pattern AST and one matcher serve rules, includes and excludes; the include/exclude
  asymmetry disappears. The OS directory listing knows only `*` and `?`, so classes/braces cannot
  go through it (tm 60).
- No third-party glob library in Core (tm 60).
- `ExecCore.applyWildcards` moves onto the AST's `format` (tm 14); a pattern made only of
  literals formats to itself (cover by a test); bare wildcards substitute by position.
- `PatternAst` is public (tm 13).
- Separate bugs found by reading, to be covered by the conformance tests: `applyWildcards` with an
  empty group value or `)` in a value.

## (d) The typed layer (comes back last, mostly as built in #91)

- Namespace `Xake.Typed` in Core, plain F#, no type provider: `Target<'G>`, `Target<'G,'O>`,
  `Command<'G>` (tm 33). Anonymous records work as carriers (not destructurable); named records can
  be destructured.
- Carriers: records, anonymous records, `string`, `unit`; no tuples (tm 50). Constructors take
  `string` or `string list`. Outputs of `Target<'G,'O>` are a record of patterns, labels = field
  names (tm 50, 64). `"label=pattern"` syntax is dropped (tm 29 superseded by 50).
- Rule forms: D `X.Rule <| fun m -> recipe {...}` is the main one; B `X.rule { let! m = matched ...
  }` ships beside it; A is dropped (tm 18, 20, 22). The case-only pair `Rule`/`rule` stays (tm 62).
- Members: `Format` makes a path from group values, `Pattern` is the pattern text; also `Paths`,
  `For`, `Any`, `Glob` (returns a `Fileset`, tm 8), `Make` (tm 4, 27, 64). No `Need`/`TryMatch`
  members (tm 25).
- `Command<>` without groups is allowed, lambda takes `unit` (tm 23, 30); it always runs
  (AlwaysRerun) by intent (tm 21, 31).
- No TP in this release; the provider is a separate later feature/package (tm 32, 46). Same short
  names in Core and a future TP package (tm 34); the TP would erase onto Core's runtime (tm 36).
  Cross-product helper `All(...)` only in the TP package (tm 37); Core uses list comprehensions.
- Script-start errors: no stack trace, readable type names (`{| fwk; lbi |}`), pattern text shown
  (tm 38); they reuse `PatternAst.PatternException` (tm 64). Unclosed group in string rules does
  not warn (typed patterns reject it) (tm 61). `*..>`/`targets` string rules check their group
  sets when created (tm 50).
- PR2 renames deferred (no `Level` qualification, no `BuildTarget` rename, no `Level` shim;
  tm 35, 39, 40 on hold) (tm 47). The typed layer lives in its own namespace opened after `Xake`.
- Compile-time negative tests run through `dotnet fsi --typecheck-only` (tm 43).
- Untyped `Pattern`/`PatternMatch` stay internal (tm 64).

## (e) Phony and `getRuleMatch`

- Phony `=>` is to be phased out; `Command` replaces it, which is the way to change the semantics
  and retire "phony" (tm 49, supersedes tm 42). Note: migrating existing `=>` rules with groups to
  `Command.Rule` adds AlwaysRerun (tm 21).
- `getRuleMatch` stays as is, including `""` for an unknown group (tm 7).

## (f) Version

Open (tm 51; tm 41's 4.0.0 was reconsidered once escaping was softened). Decide after the spec
fixes how much breaks. 3.12.0 is released (tag `v3.12.0`); nothing of this feature is in it.

## Not classified / parked from the frozen brief

- tm 9 hermetic path functions from patterns: not now (sketch exists, `hermetic-sketch.md`).
- tm 11 roadmap "Rules with arguments": separate task.
- tm 16 TP SDK not vendored; tm 44 building the provider via `build.fsc.fsx` deferred (both TP-only).
- tm 45 integration branch: **not decided for this feature** — PRs straight to `dev` or an
  integration branch again is the user's call; ask before the first implementation PR (Phase 3).
- tm 1/2/3/5/12/17/24/26/32 TP packaging and naming: only relevant if the TP returns.
- tm 10 `[<Literal>]` patterns: concerned the TP only.

## Open for design (decide in the spec phase, not during implementation)

1. Exact EBNF of masks and of rule patterns (one grammar, rule patterns as a superset).
2. Semantics table: rule vs include vs exclude, per element (`*`, `?`, `**`, class, brace, group,
   escape, `\` separator).
3. Dotfiles: does `*` match a leading `.`; Ant-style or gitignore/picomatch-style.
4. Case sensitivity per OS (today: insensitive on Windows, ordinal on Unix); case of classes.
5. `**` edge cases: `a/**`, `**/b`, `a**b`, zero segments, trailing `/`, `**` in a rule capture.
6. What an include segment without wildcards means (today the directory listing resolves it;
   literal existence vs matching).
7. Class and brace rollout timing: this release (behind warnings) or the next major; what the
   reserved-character warning says meanwhile.
8. Error vs warning model: which inputs warn, which throw, exception types, message format,
   how "once per pattern" is keyed.
9. Minimal public API of the module and its name/namespace (parse, match, format, glob, AST
   visibility; relation to the public `PatternAst`).
10. How `Fileset` scanning uses the matcher (walk strategy, pruning with `**`, the OS listing's
    `*`/`?` role, symlinks, ordering).
11. Performance: regex compile cache vs an own matcher; cost per file in large trees.
12. Compat corpus: which existing masks/patterns (samples/, docs code blocks, tests) pin behaviour.
13. Migration of existing masks such as `[(]` and `sub[1]` (today literal/regex, tomorrow a class);
    the warning text and the suggested replacement.
14. Named group details: allowed names, group content (`*`, `**`, classes, braces inside),
    duplicate names, repeated use in multi-output patterns.
15. Version number (f) and the deprecation calendar for `\` separators.

## Decisions from the spec review (2026-10-27 onwards)

- **D1 (2026-10-27, revised 2026-10-31): Standard only, in 4.0.** `dev` is already the 4.0 line
  (variable model PRs #111-#115 landed after the spec draft), so the module ships its Standard
  semantics directly with no Compat level, no `XAKE_PATTERNS` switch and no intermediate 3.x
  release of the module. What remains of Compat: warnings on legacy masks (`[(]`, `sub[1]`, `\`
  separators) that name the old meaning and the replacement, and a behaviour-change list for the
  4.0 release notes. The 2026-10-27 answer (Compat now, Standard in 4.0) was given before the
  `dev` state was checked and is superseded.
- **D2 (2026-10-31): integration branch `feature/patterns2`**, cut from `dev`. Every phase PR of
  this feature targets it (squash); it merges to `dev` when the typed layer and docs are in.
  Closes the open item tm 45 for this feature.
- **D3 (2026-11-01): the regex-equivalent table is an implementation detail.** It moves out of
  the semantics section into a "Matcher implementation" section; the requirements are the
  semantics tables and the conformance vectors, not a regex encoding. (Critique M1/M2 are then
  implementation notes, not spec defects.)
- **D4 (2026-11-01): a repeated group name is an error** (critique S2). Backreference semantics
  were never intended; `{(x:a)-1,(x:b)-2}` and `(x:*)/(x:*)` are both rejected at parse time.
- **D5 (2026-11-01): specificity must not count the `/` next to `**`** (critique S7). `*.dll`
  is more specific than `**/*.dll` for a root file; the specificity rule is fixed in the spec.
- **D6 (2026-11-01): a leading `/` stays absolute** (critique S1). The difference from gitignore
  goes into the list of deliberate deviations; an exclude whose absolute root lies outside the
  fileset base gets a warning (it can never remove anything). Rejected: gitignore-style anchoring
  (indistinguishable from an absolute path on Unix; masks are anchored at the base anyway).
- **D7 (2026-11-01): `(` stays reserved (tm 60); add `escapePath`** (critique S3). `escapePath`
  normalizes `\` to `/` and escapes every pattern metacharacter; the XP003 text for masks points
  to it. Rejected: reading `(` without `name:` as a literal (one more lexer rule to explain).
- **D8 (2026-11-01): open item 1 accepted as recommended.** One grammar, two contexts (a mask is
  a rule pattern without named groups; a group in a mask is parsed, then rejected); `(name:` is
  one lexical token; `{x}` without a top-level comma is an error, not a literal.
- **D9 (2026-11-01): open item 2: one meaning per element, two contexts.** Contexts are `Rule`
  and `Mask` only. One parser implementation with a context argument and two public entry points
  (`Rule.parse`, `Mask.parse`); the context changes exactly two things: in `Mask` the lexer does
  not produce the `(name:` token (so `(` is XP003 at once), in `Rule` a trailing `/` is XP011.
  One AST, one matcher. The trailing `/` means "matches a directory" in every mask; include vs
  exclude differ only in the scanner (an include yields the matched directories, an excluded
  directory is not entered, so "everything below" is a consequence, not a separate meaning).
  Semantics tables get two columns (rule, mask). The 3.x exclude behaviour (`obj/` read as
  `obj`) becomes a release-notes entry. Rejected: two independent parsers (the 3.x drift came
  from separate encodings); parse-then-validate (errors should arise where the problem is).
- **D10 (2026-11-01): open item 3 accepted.** A leading `.` is not special: `*`, `?`, `**` and
  classes match it (Ant, gitignore, globset, 3.x). Hidden folders are excluded explicitly and
  pruned by the walker. Rejected: picomatch-style dot handling; a `dot` fileset option (possible
  later, not part of the module).
- **D11 (2026-11-01): open item 4 accepted.** One comparer everywhere: ordinal on Linux/macOS,
  ignore-case on Windows, as `File` equality and rule matching today; classes follow it. The two
  3.x quirks (case-insensitive literal excludes on every OS; case-insensitive wildcard includes on
  macOS via the .NET listing) are release-note entries without a warning. Rejected: macOS
  insensitive; an evidence warning; a `caseSensitive` option (possible later in `ExecOptions`);
  per-directory detection.
- **D12 (2026-11-01): open item 5 accepted, with one addition.** `**` is zero or more whole
  segments everywhere; captures have no edge `/`; `a/**` excludes `a` itself; `a**b` = `a*b`
  (XP112); rule `**/` zero segments is a 4.0 change signalled by XP119 on evidence; mask
  `dir/**` by XP113. Addition: the spec must document the "one or more segments" idiom
  `*/**/file.o` (with capture: `(path:*/**)/file.o`) in the semantics section, in a worked
  example and in the XP119 text, because the 3.x "one or more" rule came from a real case and
  users need the replacement spelled out.
- **D13 (2026-11-01): open item 6 accepted; the explicit-name rule stays.** A mask with no
  non-literal element is listed even if absent (load-bearing: hermetic `Locks.fs` build-from-lock,
  the compiler tasks' `Ref` filesets, four tests; see `explicit-name-history.md`). Literal
  prefixes are probed, not listed; braces are matched against listings. Two follow-ups: document
  the rule in `docs/overview.md` (Phase 5 docs), and revisit it after 4.0 once the compiler tasks
  and hermetic demand generated files through `need` instead of filesets.
- **D14 (2026-11-01): open item 7 accepted.** Classes and braces ship in 4.0 in every context.
  Legacy readings are signalled: XP104 on evidence (an entry literally named `[abc]`/`{a,b}`),
  XP105/XP106 at parse for `[c]`, XP117 for `[!a]` in rules, XP005 error for `{x}`, XP003 error
  for `( ) |` outside a group (with the braces hint). Rejected: parse-time warning on every
  class/brace in masks; classes and braces only in 5.0; braces only inside groups.
- **D15 (2026-11-01): open item 8 accepted.** Errors: `PatternException of string` at parse
  time, message `Invalid pattern '<text>' (<context>) at character <n>: <reason> [XPnnn]`, the
  reason naming the 3.x meaning; `tryParse` returns `PatternError`; the engine rethrows as
  `XakeException` inside a build. Warnings: legacy input only, level Warning (closes C2), codes
  XP1nn, keyed by (context, pattern text), at parse when a warning-free 4.0 spelling exists and
  on evidence otherwise, buffered until a log attaches, all removed in 5.0.
- **D16 (2026-11-01): open item 9 accepted.** Public module `Xake.PatternAst` (own file, not
  auto-opened): AST types, `PatternException`/`PatternError`, parsing as `Rule.parse`/`Mask.parse`
  (+ `tryParse`, `check`) per D9 instead of `parseIn`, `unparse`/`escape`/`escapePath` (D7),
  `groupNames`/`wildcards`/`specificity`, `format`/`formatMatches`/`glob`. Matcher and regex
  translation internal; `Path.matchGroups`/`Path.matches`/`Fileset` signatures unchanged;
  `Includes of PathMask` kept in 4.0 (C8). Rejected: a new module name; a public compiled
  pattern type (possible in a 4.x minor); public `toRegex`; replacing `FilesetElement` now.
- **D17 (2026-11-01): open item 10 accepted, with two Phase-2 measurements.** Depth-first walker
  driven by the AST: literal prefix probed, one-segment patterns against a listing (OS filter is
  a superset pre-filter only), `**` recursion, brace-with-`/` as a union, pruning of directories
  covered by a later exclude, ordinal order per directory, duplicates removed, unreadable
  directories skipped with a warning, evidence checks on the walk. Symlinks are not followed
  under `**`; whether junctions, mount points and cloud-sync placeholders count (critique S5) and
  the OS-listing quirks (C10: Win32 `?`/`*.*`, macOS case) are measured in Phase 2 before the
  spec wording is final. Rejected: `AllDirectories` + filter (3.x); one whole-path regex after a
  full listing; symlink following with cycle detection; unsorted; keeping duplicates (C5 test
  to be rewritten).
- **D18 (2026-11-01): open item 11 accepted as the Phase-3 starting point** (implementation
  detail per D3, revisable on measurement). Regex from the AST, cached by (context, text, anchor,
  case), `Compiled` for rule/exclude patterns, interpreted for walker/one-off matchers, fast paths
  for literal, `*`, `*.ext`; measured with the `FilesetTests` benchmark. Rejected for now: an own
  matcher; no cache; always `Compiled`; `NonBacktracking`.
- **D19 (2026-11-01): open item 12 accepted.** Conformance corpus = engine pattern tests
  (`PathTests`, `FilesetTests`, `XakeScriptTests` group/multi-target, `RuleMatchTests`,
  `RuleSpecificityTests`, `ParentDirTargetTests`, `PatternAstTests`), the prototype's escaping
  and AST tests (C2 adjusted, 4.0 warnings added), every pattern in `samples/*.fsx` (not
  `samples/Research`), `build.fsx`, `build.fsc.fsx`, fenced F# blocks in `docs/*.md`, and
  hermetic's direct uses. Vectors tagged unchanged / changed-in-4.0 with the 3.x result and the
  release-notes entry; changed vectors are rewritten, never deleted.
- **D20 (2026-11-01): open item 13 accepted.** `[c]` is the character `c` everywhere. XP105 at
  parse where 3.x meant `c` too (rules, exclude wildcard segments; suggests `\(` or plain `c`,
  tm 55); XP106 at parse where 3.x read the literal text (includes, exclude literal segments;
  names the 3.x reading, suggests `\[1\]` or `1`). Both stop in 5.0. Rejected: XP106 on evidence
  only; an error for `[c]` in masks; tree-dependent auto-detection.
- **D21 (2026-11-01): open item 14 accepted with D4 applied.** Names: Unicode letter or `_`,
  then letters/digits/`_`; leading digit is XP007. Body: any element incl. `/`, `**`, classes,
  braces, nested groups. A repeated name is an error (D4; XP116 dropped). Multi-output rules:
  the same name set in every output (XP013); positional pairing of bare wildcards stays.
  Positional numbers, the `"0"` key (C7) and greedy capture values stay as in 3.x. Rejected:
  leading digit as a warning; ASCII-only names; no `/`/`**` inside groups.
- **D22 (2026-11-01): open item 15 accepted.** Version 4.0.0 (closes brief f). `\` as a
  separator: warn in 4.0 (XP110 at parse), remove in 5.0 (then `\` escapes `* ? , ! ^ - \` and
  any other `\x` is XP009). In 4.0 `\` before a reserved character is an escape (tm 55); literal
  `*`, `?`, `,`, `\` are written as one-character classes. Rejected: removal in 4.0; the
  "no `/` in the text" heuristic; picomatch-style `\x` = `x` in 5.0.
- **D23 (2026-11-01): C11 option B.** A named group in a mask and an unclosed group in a string
  rule are both errors in 4.0 (XP008 / XP006); tm 58 and tm 61 are superseded. XP111 is dropped.
  Consistent with D9: the mask lexer has no `(name:` token, so `(` in a mask is XP003 directly.
- **D24 (2026-11-01): the spec agent's derived choices accepted.** `(` in a mask: XP008 when
  followed by `name:`, XP003 otherwise. `MaskRole = Include | Exclude` on `Mask.check` so that
  XP105/XP106 and the warning key (role, text) know the role; errors print `(rule|mask)`. XP120
  (absolute exclude outside the base) is not a legacy warning and stays after 5.0. D5 can change
  rule selection (behaviour change 33). S6: repeated separators collapse; UNC roots parse on
  every OS and match only on Windows; `\,` inside braces is XP017.
- **D25 (2026-11-01): deferred 3 (probe spelling on case-insensitive volumes): document only.**
  A probed literal segment keeps the mask's spelling; the macOS trap (`SRC/a.cs` found by the
  probe, then missed by an ordinal exclude or rule) is documented next to the case example. The
  scan result must depend on the mask text and the file list, not on how the volume stores case.
  A disk-spelling option may come later without a grammar change.
- **D26 (2026-11-01): deferred 1 (`?` and classes match one UTF-16 code unit).** Documented in
  the deliberate-differences list with the `??` example; a scalar-value matcher can come later
  without breaking any mask that has no surrogate pairs.
- **D27 (2026-11-01): deferred 2 (Unicode normalization on macOS): document only.** No NFC/NFD
  normalization; names compare by code units, as every mainstream globber does. Listed as a
  known limit next to the case example.
- **D28 (2026-11-01): deferred 4 (extglob room): no reservation of `*(` / `?(`.** `(name:` is a
  group wherever it appears; a future extglob may use only `@(`, `+(`, `!(` (already XP003).
  Recorded as a constraint in the grammar section.
- **D29 (2026-11-01): deferred 5–9.** Empty pattern `""`: an error in both contexts. `.` as a
  segment after a wildcard: an error (as `..`, XP012). A directory skipped under `**` because it
  is a symlink: logged once at Message level, as unreadable directories are. `LinkTarget`
  detection: deferred to the Phase-2 measurement, not in the spec. `{a/,b}` ending a pattern:
  an error (`{a,b}/` covers the honest case).
- **D30 (2026-11-01): `spec.md` approved by the user** (Phase 1 exit). Later changes to the
  spec need a decision entry here.
- **D31 (2026-11-01): staged implementation, four PRs into `feature/patterns2`.** PR 1 docs
  (spec, decisions, measurements; this branch). PR 2 grammar and parser: parse/error/`unparse`/
  `escape`/`escapePath`/`groupNames`/`specificity` vectors first (own commit, reviewed on the
  PR), then `PatternAst.fs`; the engine does not use it yet. PR 3 matcher: match/capture/`format`
  vectors (with 3.x results from the measurements), then the regex-backed matcher. PR 4 scanner:
  fixture-tree vectors, then the walker. Phase 4 (engine migration) follows. Vectors are
  table-driven NUnit tests, ignored until their stage's code lands.
- **D32 (2026-11-01): `-- "obj/"` is a release-notes entry only**, no warning (3.x removed
  nothing; 4.0 prunes the directory, which is what the author meant).
- **D33 (2026-11-01): file-system attributes are not pattern semantics.** A file with the
  Windows `Hidden` (or any other) attribute matches like any other name (C16); one sentence in
  5.3. The walker never uses attribute-based filtering of the listing.
- Windows measurements: a standalone `measure-windows.fsx` (Xake 3.15.0 from nuget.org) run by
  the user on a Windows machine without git; results come back as `measurements-windows.md`.
- **D34 (2026-11-01, PROVISIONAL by the orchestrator; the user confirms or reverses):** answers to
  the 23 "SPEC?" notes of the parser vectors, chosen to follow the spec's intent and the simplest
  reading. Semantics: `..` is a literal segment only in the literal prefix (before the first
  wildcard; `../lib/*.dll` is valid, `..` alone and `..` above a root are kept literal and fail
  at scan time), after a wildcard it is XP012 as specified. Mask `/` (root only) is accepted;
  rule `/` is XP011; `//a` collapses to `/a` (D24). Consecutive globstars `**/**/b` collapse to
  one in the AST (unparse gives `**/b`). A `**` that is a whole brace alternative between
  separators is a globstar (transparency, as for groups). A one-character class holding only a
  metacharacter (`[*] [?] [,] [\\]`) is the escape idiom (D22) and never warns XP106. `wildcards`
  lists wildcards inside braces too, in document order. A separator next to a group ending in a
  globstar counts 0 in specificity (D5 through group transparency). Conventions: an error's
  position is the start of the offending construct (`[` for a class, `(` for a group, `{` for
  braces, the `|` itself for a stray `|`, the `(` for XP007 and XP003 on `(1:*)`); XP016 at the
  first non-literal element. Unparse/escape: a negated class prints `[^…]`, a `,` inside a
  braced literal prints `[,]`, `escape ""` = `""`, `escapePath` keeps a trailing separator. API
  gap: `Mask.tryParse`/`Mask.parse` take no role; role-dependent warnings (XP105/XP106/XP117)
  come from `Mask.warnings : MaskRole -> Ast -> PatternWarning list` (pure) and `Mask.check :
  MaskRole -> string -> unit` logs them (8.2 to be aligned in PR 2).
- **D35 (2026-11-01): D34 confirmed by the user, plus the three remaining notes:** `.` alone is
  XP018 (P-065); XP019 in nested braces is reported at the inner `{` (P-222); the XP016 reason
  quotes the literal sample `//server/share` (M-018). D34 is no longer provisional.
- **D36 (2026-11-01): the UNC root stays as specified** (`root = unc | drive sep | sep`; XP016
  for a non-literal server or share; matches nothing on Unix). 3.x had no UNC notion and read
  `\\server\share` as `/server/share` after collapsing. Rejected: an error on a leading `//`
  ("mount the share"); silent collapsing.
- **D37 (2026-11-01): the matcher vectors' SPEC? notes.** XP119 does not fire when the globstar
  is preceded by a `*` segment (the D12 idiom `*/**/b`: 3.x needed two directories, 4.0 one;
  the warning must not recommend its own trigger) nor for a globstar inside braces (3.x read the
  braces literally and matched nothing). Accepted as read by the vectors: `**/**/b` has one
  capture after the D34 collapse (spec 5.5 to be corrected); a named group in an unmatched brace
  alternative is present with `""`; the matcher compares the target path as given (`..` not
  resolved, doubled `/` not collapsed: paths are normalized by `File.make` before matching);
  `\` on the path side is a separator only in Windows mode; an empty globstar group at an edge
  (`(p:**)/b`, `p = ""`) drops one `/` in `format` like a bare globstar; `[x]` in a multi-output
  rule is a class and XP014 at creation (4.0 behaviour change; `lib\[x\].dll` or `libx.dll`).
- **D38 (2026-11-01): no cuts from the complexity critique.** Groups spanning segment
  boundaries are needed; nested groups stay (unused, harmless, no measurable cost). The critique
  stays in the folder as input for 5.0 planning (migration debt: legacy warnings, `\` hybrid).
- **D39 (2026-11-01): the scanner vectors' SPEC? notes.** Accepted as read: explicit names are
  listed whether they are files, directories, absent, or under a missing base (D13); `**/` does
  not yield the base itself; a brace union holding `/` comes out in tree order; duplicates are
  removed with the mode's comparer (Windows: `b` = `B`); a directory link under `**/` is neither
  entered nor yielded; a pruned directory is not read, so nothing below it is logged; `**/`
  yields an unreadable directory and warns when it cannot list it; XP113 only when the pruning
  would have removed something; XP104 also for excludes and only on a whole name equal to the
  literal text; no XP104 for `sub[1]` (XP106 at parse); no XP120 for a relative `../` exclude;
  no XP114 for `a*.*`; an absolute include's anchor is its literal prefix. One change: an
  unreadable literal prefix (probe) counts as absent AND is logged as `Unreadable` (SC-275), so
  an empty result is explainable. Shim contract: one ordered `(MaskRole * string)` list (an
  exclude applies to the includes before it, spec 6.3); the D25 trap is on `SRC/*.cs` and the
  volume decides, not the mode (explicit `SRC/a.cs` is listed in every mode).
- **D40 (2026-11-01, PROVISIONAL by the orchestrator; the user confirms or reverses):** three
  readings the walker needed beyond the spec text. (1) A literal segment is probed only while
  the walker has not listed its directory; inside a directory that is listed anyway (after `**`,
  `*`, braces) the literal is matched against the listing and takes the disk's spelling (6.3,
  vectors SC-227/228). (2) XP120 does not fire for an absolute exclude whose literal prefix lies
  above the base (`/**/obj/` can remove files below the base). (3) A `P/**` exclude prunes a
  directory only once a found file is known to match it, so the XP113 evidence equals the
  unpruned result (SC-122). Vectors SC-214/229 were corrected on their own terms (`*.cs` matches
  `B.cs` ordinally; `lib/` does not exclude `Lib` in Unix mode).
