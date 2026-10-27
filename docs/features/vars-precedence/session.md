# vars-precedence: session

Where things stand (2026-10-28): the owner took the design decisions P1-P11 for part A (variable
evaluation: one evaluator per run, chain Request > Cli > Script > Env > Default > LibraryDefault,
`VarStore` in the context, declarations as values, CLI over ops for options). `spec.md` formalizes
them, with numbered rules and 33 conformance tests; it supersedes the "Options" of `analysis.md`.
Parts B and C (`setup`, banner) landed in #101 (4ef149d on dev). This branch is still based on
92ef634; rebase on dev before the implementation.

**State: spec written, awaiting the owner's approval.** Section 10 of `spec.md` lists the gaps the
decisions left open (filled there, marked **[gap]**) and three open questions (Q-1 `CI` parsing,
Q-2 unused `?vars` in engine mode, Q-3 env fallback for `NUGET_FETCH`/`NETFX`/`HERMETIC`).

Next step: the owner reviews `spec.md` (gaps + open questions) and approves. Then implementation,
split into PRs, each with its conformance tests from spec section 9 written first:
1. core store + dependencies: `VarStore`/`VarSource`/`VarDecl`, chain, memo, conflicts, weak reads,
   canonical `Var` records, persisted declarations, db 0.7, banner sources, diagnostics;
2. Xake.Dotnet declarations: `DotNetVars`, readers moved onto it, `HERMETIC=on` by source;
3. options precedence: CLI overlay over `rootdir`/`consolelog`/`filelog`;
4. hermetic move: `HermeticVars`, Xake `[3.14, 3.15)`, Xake.Hermetic.Dotnet 0.9.
Release as Xake 3.14 with the release note of spec V-3.
