# vars-precedence: session

Where things stand (2026-10-28): the owner took the design decisions P1-P11 for part A (variable
evaluation: one evaluator per run, chain Request > Cli > Script > Env > Default > LibraryDefault,
`VarStore` in the context, declarations as values, CLI over ops for options). `spec.md` formalizes
them, with numbered rules and 39 conformance tests; it supersedes the "Options" of `analysis.md`.
Parts B and C (`setup`, banner) landed in #101 (4ef149d on dev). This branch is still based on
92ef634; rebase on dev before the implementation.

**State: spec rev. 3, awaiting the owner's approval.** Rev. 1 and rev. 2 were reviewed in PR #102.
Rev. 3 makes these changes:
- the `defaulted` flag on `Var` dependencies (database 0.7) replaces registration at task
  construction;
- secrets can be read only through their declaration (E-7);
- `NUGET_PACKAGES` defaults to `.packages`, and P-2 is simpler;
- all library variables are `ArgAndEnv`;
- `CSC_SERVER` / `XAKE_CSC_SERVER` are kept as two declarations with their 3.13 semantics;
- `CI` is parsed leniently;
- new §8.1 "What an existing 3.13 script sees".

One open question remains, Q-5: a library value from the environment, in a script without
`varschema`, rebuilds on every run.

Next step: the owner decides Q-5 and approves. Then the implementation, split into PRs. Each PR
writes its conformance tests from spec §9 first:
1. core store + dependencies: `VarStore`/`VarSource`/`VarDecl`, chain, memo, conflicts, weak reads,
   `Var (name, value, defaulted)`, `SecretDep` without kind, db 0.7, secrets only via declarations,
   banner sources, diagnostics;
2. Xake.Dotnet declarations: `DotNetVars` (all `ArgAndEnv`, the `CSC_SERVER` pair,
   `NUGET_PACKAGES` default `.packages`), readers moved onto it, `HERMETIC=on` by source;
3. options precedence: CLI overlay over `rootdir`/`consolelog`/`filelog`;
4. hermetic move: `HermeticVars` (`CI` lenient), Xake `[3.14, 3.15)`, Xake.Hermetic.Dotnet 0.9.

Release as Xake 3.14 with the release note of spec V-3 and §8.1.

## 2026-10-28 (late): variant 3, the script states the rule

dev merged in (eec2b2c: #103 "command line wins", #101). Gap analysis in `after-103.md`: with
`ExecOptions.Vars` (input values) and `Var.*` (declarations) treated as separate concepts, variants
1/2a/2b/2c are not needed. The owner chose **variant 3** (`concept.md` §8):
`var "N" [Env "X"; Default "d"]`, evaluated when the op applies, one `Var (name, value)` dependency;
libraries read the variable set only (no env fallback, breaking); the banner shows the real source.
Spec rev. 3 is superseded; the plan above is replaced by the tracker.

Settled the same day: a secret is a separate list element (`[Secret; Env "X"]`); `varschema`
takes a `Var.*` declaration completely and evaluates it at launch, so its fallback is a
declaration, not an instruction (concept §8).

Owner's answers: a duplicate declaration (`var [...]` + `varschema`) is an error; `-d` of a secret
is an error at launch (fail-fast); a `Var` read outside `varschema`: "variable set only" or error.

**Spec rev. 4 written** (`spec.md`: 36 rules, 59 conformance tests). Next step: the owner confirms
§10 (K-3 Var outside varschema, S-5 constant var + varschema, L-4 HERMETIC with env-sourced
NUGET_PACKAGES, SOURCE_DATE_EPOCH, Var.env given by -d), then PR 1 (core) starts with the tests
of §9.
