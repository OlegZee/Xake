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

Next step: spec rev. 6 (`spec.md`) from the brief, with conformance tests; the owner approves it;
then PR 1 starts with the tests of the spec. Proposed split (not yet approved): PR 1 core (`var`
lists, `varschema` at launch, `Var` reads, second-definition errors, `?vars` on top, banner sources);
PR 2 library readers (no env fallback, switches, `.packages`) in Xake.Dotnet and the hermetic package.
