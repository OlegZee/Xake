# Brief: typed rule matches and path generation

Decisions taken with the user on 2026-10-25, answering the open questions of
[`research.md`](research.md). Do not re-litigate; open items are marked as such.

## Direction

A type provider `Pattern<...>` (research option 1): typed group access in recipes, compile-time
consistency of multi-file patterns, typed generation of paths, path lists and filesets from the
same pattern. Patterns are compile-time literals (`[<Literal>]`); rules built from run-time
data keep the string API.

Base: the pattern AST in `src/core/Path.fs` (`PatternAst`, merged to `dev` in #89) is the one
parser of the pattern grammar. The provider should reuse it rather than add a fourth encoding
(how the design-time dll gets it — shared source file or a reference — is an implementation
question).

## Decisions

1. **Packaging: the same package.** The provider ships in the `Xake` nupkg, no separate
   `Xake.Patterns` package. The design-time part is still its own dll (the compiler loads it and
   it carries the TP SDK), placed under `typeproviders/fsharp41/netstandard2.0` in the same
   package; the small runtime part lives in `Xake.dll`.
2. **Multi-file patterns: separate static parameters**, one per output pattern, not a
   `;`-separated string.
3. **Naming outputs: labels**, with a fallback to number suffixes when no label is given.
4. **Generation method names:** `Make`/`Format`, `Target`, `For`, `Glob` are fine.
5. **Phony rules: separate pattern types** (e.g. a `Command<>`-like type next to `Pattern<>`),
   with specialized methods. Looks promising; **open: the cost** has to be estimated before
   committing to it.
6. **Bare wildcards (`*`, `**`, `?`) in typed patterns: positional** — exposed by position.
7. **`getRuleMatch` stays as is** — no change to its behaviour (an unknown group still gives
   `""`). Research option 0's strictness change is not taken.
8. **`Glob` returns a `Fileset`.**
9. **Hermetic path functions (`lockOf`/`outOf`/`runOf`) from patterns: not now**, but the user
   wants to see what it would look like — a sketch, no change to `src/hermetic`.
10. **Requiring `[<Literal>]` patterns in scripts is acceptable.**
11. **Roadmap "Rules with arguments": not replaced by this.** Explicit outputs would be good to
    have, but that is a separate task.

## Decisions, round 2 (2026-10-25)

Answering the questions of `phony-types.md` and `ast-reuse.md`.

12. **The phony type is named `Command<>`.**
13. **`PatternAst` becomes public** (no `InternalsVisibleTo` needed to reach it).
14. **`ExecCore.applyWildcards` moves onto the AST's `format`.** A pattern made only of literals
    formats to itself (as today, to be covered by a test); bare wildcards substitute by position.
15. **Literal `(` `)` `[` `]` in patterns are escaped** (a matching behaviour change).
16. **The TP SDK is not vendored** into the repo: its sources come from the
    `FSharp.TypeProviders.SDK` package. Open: whether `build.fsc.fsx` can compile `.fs` sources
    taken from a package.

## Decisions, round 3 (2026-10-25)

After experiments 06 and 07 (`ce-syntax.md`).

17. **The provider attribute sits on a separate runtime dll `Xake.Patterns.dll`** (namespace
    `Xake.Patterns`), not on `Xake.dll`. Still **one package**: `lib/<tfm>/` holds `Xake.dll`
    and `Xake.Patterns.dll`; `typeproviders/fsharp41/netstandard2.0/` holds the whole
    design-time output (`Xake.Patterns.DesignTime.dll` and its dependencies). Package consumers
    always get the design-time dll (it loads on reference, used or not); anything referencing
    only `Xake.dll` from a folder never needs it.
18. **Rule form D is the main form:** `Dll.Rule <| fun m -> recipe { ... }` (the existing
    `Rule` member, no new code).
19. ~~Form A stays as `Dll.Matched`~~ — **reversed in round 4: form A is dropped**.
20. **Form B ships beside D**; its marker is named `matched`
    (`Dll.rule { let! m = matched ... }`).
21. **`Command<>.Rule` follows today's `command` semantics** (adds AlwaysRerun). Note: the 9
    existing phony rules with groups use `=>` (no AlwaysRerun); migrating them to
    `Command<>.Rule` changes that.

## Decisions, round 4 (2026-10-25)

22. **Forms: D and B; A is dropped** (rather lose A than B).
23. **`Command<>` without groups is allowed**: `Command<"clean">.Rule <| fun () -> recipe { ... }`
    (the lambda takes `unit` when there are no groups), and form B `Command<"clean">.rule { ... }`
    needs no `matched` at all.
24. **Future option kept open: move the provider into its own package.** For now it ships in
    the `Xake` package (decision 17); the separate `Xake.Patterns.dll` keeps that split cheap.
## Decisions, round 5 (2026-10-25), after experiment 08

26. **The provided type is named `Target<...>`** (was `Pattern<...>`); one type for single and
    multi-output. The engine's DU `Xake.DomainTypes.Target` is internal by intent — scripts are
    not meant to use it — so the clash (the namespace opened last wins) is accepted on the
    condition that only the new `Target<>` stays visible to scripts: the DU is to be taken off
    the script surface (rename/move/hide; how is an implementation question).
27. **Member names:** `Format(...)` produces a concrete path/name from group values (both
    types); `Pattern` is the pattern text itself. (Supersedes the `Target` member of decision 4.)
28. **Escaping also covers `|`** (in addition to `( ) [ ]`, decision 15); it affects fileset
    masks as well — tests using `[(]` get rewritten.
29. **Output labels are written `"label=pattern"`**; unlabelled outputs are `Out1`, `Out2`, ….
30. A no-group `Command<>` accepting `fun m ->` (`m : unit`) is fine — not a problem.
31. **`Command<>` = runs unconditionally (AlwaysRerun), by intent.** Ordinary targets honestly
    skip when nothing changed; a command always runs — that is what makes it a command.

## Decisions, round 6 (2026-10-25)

32. **Target design: a separate package, not just a namespace.** Concern: the provider is
    heavyweight and its reliability across development environments (IDEs) is unproven. So the
    type provider and all new declaration forms (`Target<>`, `Command<>`, forms D/B) go into
    their own NuGet package (e.g. `Xake.Patterns`), opt-in. Supersedes decisions 1, 17 and 24
    on packaging (the dll split of 17 stays). **Core (`Xake`) gets whatever improvements need no
    TP SDK** — public `PatternAst`, `Format`, escaping, and any typed helpers that work without
    a provider (to be found: research option 2 "record-declared groups" is a candidate).
    This changes the concept; choosing among the variants is harder, accepted.

## Decisions, round 7 (2026-10-25), after experiment 10 (`core-without-tp.md`)

33. **Core gets record-declared typed patterns** (`Target<'G>`, `Target<'G,'O>`, `Command<'G>`,
    forms D/B, `matched`) in plain F#, in its own namespace (prototype: `Xake.Typed`).
    Anonymous records work as the group carrier (access, `Format`, copy-and-update; a
    mismatch with the pattern is caught at script start) but cannot be destructured — F# has no
    anonymous-record patterns; named records can.
34. **Same short names in Core and the TP package** (`Target`, `Command`, `matched`); a script
    switches by opening one namespace. Using both via qualified names is expected to work —
    to verify once the TP erases onto Core's runtime. The TP package reuses Core's `matched`.
35. **`[<RequireQualifiedAccess>]` on the logging `Level`** to free the name `Command`.
36. **The TP erases onto Core's runtime** (no runtime of its own).
37. **Cross-product helper:** `All(field = [..], …)` in the TP package (cheap there); not in
    Core (would need a second, list-typed record) — Core uses list comprehensions. (Proposed;
    the user liked the helper, cost was the question.)
38. **Script-start errors** are rendered without a stack trace, with readable type names
    (`{| fwk; lbi |}`, not `<>f__AnonymousType…`) and the pattern text.

## Decisions, round 8 (2026-10-25), on `plan.md`'s open items

39. **The DU `Target` is renamed `BuildTarget`, no alias.**
40. **`Level` gets a shim**: unqualified level values except `Command` keep compiling
    (`trace Info`).
41. **The release is Xake 4.0.0** (not 3.12.0): changing the meaning of `( ) [ ] |` in masks
    breaks silently. The user releases 3.12 separately from the changes already on `dev`.
42. **The `=>` phony rules in the build scripts stay `=>`**; `Command<>` is for what must always
    run.
43. **Compile-time negative tests run through `fsi --typecheck-only`** as integration tests.
44. **Building the provider package through `build.fsc.fsx` is deferred.**
45. **Integration branch:** `feature/typed-matches` collects the PRs (PR1… branch from it and
    merge into it, squash); at the end one PR `feature/typed-matches` → `dev`, then release
    4.0.0. PR0 is not needed (this branch carries the docs). Hermetic and dogfooding follow the
    published 4.0.0.

## Decisions, round 9 (2026-10-25), on `critique.md`

46. **The TP package (PR7) is not part of 4.0**; it becomes a separate feature later.
47. **PR2 is deferred** (no `Level` qualification, no `BuildTarget` rename for now; decisions
    35, 39, 40 are on hold). Core's typed layer lives in its own namespace opened after `Xake`.
48. **Escaping: soften the break and keep the syntax open for the future** (user's idea on
    #90). Today `( ) [ ] |` reach the regex implicitly; the change should introduce an explicit
    escape and safeguard the reserved characters so new pattern elements can be added later —
    not simply make them literal. Exact design pending the measurement in `escaping-today.md`.
    Supersedes the "literal" part of decisions 15 and 28.
49. **Phony `=>` is to be phased out; `Command` replaces it** — the way to change the semantics
    and retire "phony". Supersedes decision 42.
50. **PR3 trimmed per critique §5:** records / anonymous records / `string` / `unit` carriers
    only (no tuples); a `string list` constructor; the group-set check for multi-file string
    rules (`*..>`/`targets`); output labels from the `'O` record rather than `"label=pattern"`;
    settle `Rule`/`rule` casing and the `matched` name.
51. **Version open again:** with PR2 deferred and the escaping break softened, the release may
    not need 4.0.0 (decision 41) — to decide once the escaping design is fixed.

## Decisions, round 10 (2026-10-25), escaping design (after `escaping-today.md`)

52. **Reserved characters: `( ) [ ] { } |`** in rule patterns and fileset masks, except the named
    group `(name:...)`. This release keeps today's behaviour for them (regex in rules, literal in
    mask includes) and **warns once per pattern** through the Xake log (53); a later major turns
    the warning into an error, freeing them for new pattern elements. `{ }` are reserved too
    (glob alternation `*.{dll,pdb}` is a natural future element).
53. **Warning: once per pattern, through the Xake log.**
54. **Bugs fixed outright:** the project root is escaped in the matching regex (a root with
    `(`/`[`/`|` today mismatches or crashes); a lone `(`/`)`/`[` gives a clear error instead of
    `RegexParseException`.
55. **`\` is the escape character.** In this release `\` before a reserved character escapes
    it (`\(`, `\{`, `\|`, …) — a Windows-style pattern like `src\(x)\*.cs` breaks on purpose.
    F# string literals keep `\(` etc. unchanged (checked in fsi). `[c]` stays accepted for
    compatibility with a warning suggesting `\c`.
56. **`\` as a path separator in user-written patterns/masks is deprecated now:** it still works
    in this release but warns (once per pattern) "use `/`"; a later major makes `\` an escape
    only. Paths read from disk keep being normalized. Patterns built with `</>`/`Path.Combine`
    on Windows must not trigger the warning (make Xake's own path joining produce `/` if needed).

25. **No `Need` and no `TryMatch` members** on `Pattern<>` or `Command<>`
    (`need [ X.Make(...) ]` stays the way to require targets).

## Open

- Cost of separate pattern types for phony rules (decision 5).
- Sketch of the hermetic path functions fed from patterns (decision 9).
- IDE support and packaging into the Xake nupkg are untested (research).
