# vars-precedence: session

Branch `feature/vars-precedence`, draft PR #102, worktree `.claude/worktrees/feature+hermetic-windows`.
dev merged in on 2026-10-29 (d27bb0a: Xake 3.15.0, hermetic 0.10.0 with range to 4.0, records frozen
until 4.0).

## 2026-10-29: variant fixed

The owner fixed the design: variant 3 with secret declarations, `varschema` importing `Var.*`
completely and evaluating it eagerly, a `Var` read = lookup of a matching stored declaration, a
mismatched or missing declaration = error. It is written up in [brief.md](brief.md). Rejected designs
(rev. 1-3 lazy store, variants 1/2a/2b/2c, rev. 4 lenient read, rev. 5 "Var untouched") moved to
[archive/](archive/README.md) with a short description of each.

**Spec rev. 6 written** (`spec.md`: 43 rules, 10 diagnostics, 60 tests, no open points). Points the
writer decided itself, for the owner's review: E-6 (`-d` of a non-secret `Var.env` fails the launch);
`var [...]` rules evaluated when the op applies, like `varschema`; declaration equality T-1 (inline
`Var.env`/`Var.secret` always E-8); no `ProjectDefault` case, to keep published hermetic 0.10
working; hermetic floor rises with PR 2. Compatibility: `build.fsx` `NUGET_KEY = Var.env` lacks
`|> secret` and would print in the banner, so PR 1 marks it secret; secret fields rebuild once
(dependency record changes).

**Rev. 6 approved by the owner (2026-10-29)**, with all six writer's decisions, and one amendment:
declarations are evaluated at launch (`ScriptRunner.createScriptContext`: Run, dryrun, Start), not
when the op is applied; ops only record declarations; `--help` evaluates nothing.

Next step: PR 1 (core) starts with the conformance tests of spec §8; the PR split (PR 1 core,
PR 2 libraries) awaits the owner's word. Proposed split (not yet approved): PR 1 core (`var`
lists, `varschema` at launch, `Var` reads, second-definition errors, `?vars` on top, banner sources);
PR 2 library readers (no env fallback, switches, `.packages`) in Xake.Dotnet and the hermetic package.
