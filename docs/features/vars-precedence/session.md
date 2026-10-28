# vars-precedence: session

Where things stand (2026-10-28): the owner took the design decisions P1-P11 for part A (variable
evaluation: one evaluator per run, chain Request > Cli > Script > Env > Default > LibraryDefault,
`VarStore` in the context, declarations as values, CLI over ops for options). `spec.md` formalizes
them, with numbered rules and 34 conformance tests; it supersedes the "Options" of `analysis.md`.
Parts B and C (`setup`, banner) landed in #101 (4ef149d on dev). This branch is still based on
92ef634; rebase on dev before the implementation.

**State: spec rev. 2, awaiting the owner's approval.** Rev. 1 was reviewed in PR #102; rev. 2
applies the review: `Owner` is a plain field (no `inLibrary`), D-10 "registration at task
construction" replaces persisted declarations, the database stays 0.6, `SecretDep` loses its
source kind (0.6 on-disk layout kept), value-not-source rationale and the scope wording of the
change reason added. Section 10 lists the filled gaps and the open questions Q-1..Q-3 plus Q-4
(new: `getEnv` of a secret without the `SecretDep` kind).

Next step: the owner reviews `spec.md` (gaps + open questions) and approves. Then implementation,
split into PRs, each with its conformance tests from spec section 9 written first:
1. core store + dependencies: `VarStore`/`VarSource`/`VarDecl`, chain, memo, conflicts, weak reads,
   canonical `Var` records, `SecretDep` without kind, pending declarations, banner sources, diagnostics;
2. Xake.Dotnet declarations: `DotNetVars`, readers moved onto it, builders register at
   construction, `HERMETIC=on` by source;
3. options precedence: CLI overlay over `rootdir`/`consolelog`/`filelog`;
4. hermetic move: `HermeticVars`, Xake `[3.14, 3.15)`, Xake.Hermetic.Dotnet 0.9.
Release as Xake 3.14 with the release note of spec V-3.
