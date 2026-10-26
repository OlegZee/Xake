# Session: hermetic-windows

Branch `feature/hermetic-windows`, worktree `.claude/worktrees/feature+hermetic-windows`.

| PR | Branch | Worktree | State |
|---|---|---|---|
| none yet | `feature/hermetic-windows` | `.claude/worktrees/feature+hermetic-windows` | docs only, uncommitted |
| PR2-core | `pr/msbuild-diagnostics-core` | `.claude/worktrees/agent-a409090deab5766dd` | committed, not pushed |
| PR1 | `pr/lock-eol-packs` (committed on `worktree-agent-ac02d3da3009192a9`: the name is held by a stale worktree `.claude/worktrees/pr+lock-eol-packs` at an older commit) | `.claude/worktrees/agent-ac02d3da3009192a9` | (a) and (b) pushed to `pr/lock-eol-packs` |

## Where things stand

Testing the hermetic build of the consumer solution on Windows (locks recorded on macOS) produced
seven remarks, recorded in `docs/roadmap.md` (sections dated 2026-10-26, "Line-ending-neutral
hashes..." to "Satellite assemblies..."). Nothing is implemented.

What exists on this branch:
- `analysis/lock-content.md`: line endings, targeting packs, satellites, verified against the code.
- `analysis/engine.md`: secret variables, the build log per run (claim not confirmed).
- `analysis/import-msbuild.md`: the MSBuild floor (17.10 / SDK 8.0.300), msbuild errors in the log.
- `brief.md`: verdicts, recommended options, the owner's decision list, the PR split, the roadmap corrections.
- `docs/roadmap.md`: consumer names scrubbed from the seven sections (uncommitted edit).

Known loose end: `analysis/lock-content.md` ("Tests") names the consumer's test suites by a customer
abbreviation; reword it before committing (public repository).

## Exact next step

1. Owner interview over `brief.md` section 4, "Decisions for the owner" (29 questions, defaults in
   brackets). Record the answers in `brief.md` as decisions.
2. Apply `brief.md` section 6, "Roadmap corrections", to `docs/roadmap.md`.
3. Split into PRs per `brief.md` section 5 (PR1 lock portability, PR2 import diagnostics, PR3 secrets,
   PR4 log fixes, PR5 satellites after S71 is reopened), one worktree per PR named like its branch;
   add each to the table above.

## Notes

- Hermetic references Xake as `[3.11.0.40, 3.12)`: a change to engine records or a `Xake.Dotnet`
  public means Xake 3.12 and a range bump.
- (a), (b) and (g) change the consumer's locks: plan the re-imports.
- Do not run the consumer's test suites; Xake unit tests are fine.
