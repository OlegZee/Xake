# Session: hermetic-windows

Branch `feature/hermetic-windows`, worktree `.claude/worktrees/feature+hermetic-windows`.

| PR | Branch | Worktree | State |
|---|---|---|---|
| none yet | `feature/hermetic-windows` | `.claude/worktrees/feature+hermetic-windows` | docs only, uncommitted |
| PR2-core | `pr/msbuild-diagnostics-core` | `.claude/worktrees/agent-a409090deab5766dd` | committed, not pushed |

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

## 2026-10-26, evening: interview held, four PRs open

Decisions in `interview.md` (authoritative). PR4 (build log) dropped, PR5 (satellites) deferred.

| PR | Branch | Worktree | State |
|---|---|---|---|
| #93 PR2-core: msbuild/NuGet diagnostics in `Tool.diagnosticLevel`, error summary | `pr/msbuild-diagnostics-core` | `.claude/worktrees/agent-a409090deab5766dd` | draft, 421 tests green |
| #94 PR1: line-ending-neutral hashes, packs from NuGet, 0.8.0 | `pr/lock-eol-packs` (origin; locally `worktree-agent-ac02d3da3009192a9`) | `.claude/worktrees/agent-ac02d3da3009192a9` | draft, 310 tests green |
| #95 PR2-hermetic: MSBuild floor check, import errors in the log, ProjectRoot | `pr/import-diagnostics` | `.claude/worktrees/agent-a1765fdc13c027b5a` | draft, 310 tests green |
| #96 PR3: secret variables (core, 3.13 train) | `pr/secret-vars` | `.claude/worktrees/agent-aa55c82459e825ae5` | draft, 434 tests green |
| (docs) brief, analyses, interview, roadmap scrub | `feature/hermetic-windows` | `.claude/worktrees/feature+hermetic-windows` | pushed; merged into every PR branch |

Known rough edges: the stale worktree `.claude/worktrees/pr+lock-eol-packs` holds the local
branch `pr/lock-eol-packs` at c3febad (pre-work); the real PR1 work is the agent branch above,
pushed under the PR name. Core PRs are 3.13.0 (tag `v3.12.0` exists at #89); hermetic follows
with `[3.13, 3.14)` and the `Environment` fields of `RecordOptions`/`ImportOptions` (PR3 hermetic
item in `tracker.md`). Merge order suggestion: #93, #94, #95 (README conflict with #94 trivial),
#96; then a release of core 3.13.0 and hermetic 0.8.0.

Next step: owner reviews the four drafts; after each merge, remove its worktree and branch.
