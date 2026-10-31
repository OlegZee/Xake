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

PR split approved (PR 1 core, PR 2 library readers); archive shortened to one README.
PR 1 started 2026-10-29 by an implementer subagent: tests of spec §8.1-8.7 first, then the core.

| PR | Branch | Worktree | State |
|---|---|---|---|
| #102 (draft) | `feature/vars-precedence` | `.claude/worktrees/feature+hermetic-windows` | spec, brief, archive |
| PR 1 (not opened) | `feature/vars-core` | agent worktree (`.claude/worktrees/agent-…`) | in progress |
| PR 2 | `feature/vars-libraries` (planned) | - | after PR 1 |

Next step: review PR 1's report, push `feature/vars-core`, open the PR to dev; then PR 2. Proposed split (not yet approved): PR 1 core (`var`
lists, `varschema` at launch, `Var` reads, second-definition errors, `?vars` on top, banner sources);
PR 2 library readers (no env fallback, switches, `.packages`) in Xake.Dotnet and the hermetic package.
| #111 (draft) | `feature/vars-core` | `.claude/worktrees/agent-acc07368580609689` | done: 523 tests green, 0 warnings; owner review |
| #112 (draft, base `feature/vars-core`) | `feature/vars-libraries` | `.claude/worktrees/agent-afb9a9d9fb75e5c02` | done: 547 tests green; hermetic floor 3.16.0 needs a decision |

PR 1 done 2026-10-30 (#111): core + names + docs; deviations in `pr1-notes.md` (on that branch), e.g. an extra `ExecOptions.VarDefinedBy` field for E-2. PR 2 started from `feature/vars-core`: first `pr2-spec.md` (import conventions, proposed), then L-1..L-7.

PR 2 done 2026-10-30 (#112, stacked on #111): library readers L-1..L-7, `Var.read`, schemas `DotNetVars.Default` / `HermeticVars.Default` (conventions proposed in `pr2-spec.md`). Hermetic floor `[3.16.0, 4.0)` breaks CI until Xake 3.16.0 is published: split the hermetic commits or release first. Follow-ups: `bump-base` sets env `LOCK_REFRESH` (no longer read); CI step for test 60.

Next step: the owner reviews #111 and #112 and decides the hermetic floor path.
