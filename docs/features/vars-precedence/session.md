# vars-precedence: session

Branch `feature/vars-precedence`, PR #102 (spec), merged. Final state below.
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

PR split approved (PR 1 core, PR 2 library readers); archive shortened to one README.
PR 1 started 2026-10-29 by an implementer subagent: tests of spec §8.1-8.7 first, then the core.

## Final state (2026-10-31): all merged

| PR | Branch | What | State |
|---|---|---|---|
| #102 | `feature/vars-precedence` | spec rev. 6, brief, archive | merged |
| #111 | `feature/vars-core` | core: names, `var` sources, launch evaluation, Var reads by declaration, errors, banner | merged (c0889d3) |
| #112 | `feature/vars-libraries` | library readers without environment, `Var.read`, `DotNetVars.Default` / `HermeticVars.Default`; hermetic on the in-repo Xake | merged (a3bd326) |

Implemented as specified; deviations are in [pr1-notes.md](pr1-notes.md) and
[pr2-notes.md](pr2-notes.md). The work ships as **Xake 4.0** (breaking; owner), together with
hermetic 0.11; at 4.0 `Xake.Dotnet` and the hermetic package are split out (release-process
decision D). Release notes: `docs/release-notes.md`, "4.0.0 (unreleased)".

History of the PR 2 split (kept for the trail):

2026-10-31: CI on #112 failed (src/hermetic compiles against the published Xake, no `Var.read`).
Split without rewriting history: the full PR 2 state is kept on `feature/vars-hermetic` (be83c13,
pushed); one commit on `feature/vars-libraries` takes the hermetic part out (src/hermetic back to
dev, HermeticVarsTests, hermetic docs and 0.11 notes). The hermetic PR ships after the Xake
release (4.0 planned), floor at that release. See pr2-notes.md.

2026-10-31, later: owner's decision -- until Xake 4.0 the hermetic package builds on the in-repo
Xake. The split is reverted (54b4837), src/hermetic references src/core/src/dotnet, the hermetic
part of PR 2 is back on #112 without a floor; `feature/vars-hermetic` is obsolete (owner may delete
it). CI: `hermetic-compat` dropped, `bump-base` disabled, `hermetic-v*` paused until 4.0
(docs/features/release-process/session.md).


## Leftovers

- The owner has not yet reviewed the import conventions proposed in [pr2-spec.md](pr2-spec.md)
  (`Var.read`, `DotNetVars`/`HermeticVars`, fields named by key, string-typed, `-d` only).
- The importer reads `PrivateAssets=all` as "bundled" (`package.bundle: ["Xake"]`).
- Test 60 (the published hermetic package against the new Xake, `test60-published-hermetic.fsx`)
  has no CI job.
- Optional: measure the launch-evaluation cost, dev vs 4.0.
- Idea for 4.0: rename `ExecOptions.Vars` (script constants only) to say so.
