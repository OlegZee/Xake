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
fixes how much breaks. The user releases 3.12 separately from what is already on `dev`.

## Not classified / parked from the frozen brief

- tm 9 hermetic path functions from patterns: not now (sketch exists, `hermetic-sketch.md`).
- tm 11 roadmap "Rules with arguments": separate task.
- tm 16 TP SDK not vendored; tm 44 building the provider via `build.fsc.fsx` deferred (both TP-only).
- tm 45 integration branch: obsolete here; each PR goes to `dev` (see kickoff).
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
