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
19. **Form A stays if it costs nothing**, with the accessor named **`Dll.Matched`** (not
    `Get`): `Dll.Pattern ..> recipe { let! m = Dll.Matched ... }`; a run-time check rejects
    another pattern's `Matched` inside the rule.
20. **The marker of form B is named `matched`** (`Dll.rule { let! m = matched ... }`). Open:
    whether form B ships at all beside D.
21. **`Command<>.Rule` follows today's `command` semantics** (adds AlwaysRerun). Note: the 9
    existing phony rules with groups use `=>` (no AlwaysRerun); migrating them to
    `Command<>.Rule` changes that.

## Open

- Cost of separate pattern types for phony rules (decision 5).
- Sketch of the hermetic path functions fed from patterns (decision 9).
- IDE support and packaging into the Xake nupkg are untested (research).
