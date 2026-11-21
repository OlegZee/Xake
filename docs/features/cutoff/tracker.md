# Tracker — content cut-off (phase 3)

- [x] Rebase on origin/dev, branch feature/content-cutoff
- [x] Brief (accepted 2026-11-18)
- [x] Design report, approved 2026-11-18 (5 answers)
- [x] Docs: content-cutoff.md, implnotes, release notes, rule-header, index
- [x] Database agent: Observation (Known/Moved), Settle compare-and-set, PreHash, Output flag, off clears hash
- [x] Static needs before the verdict; pre-hash; outputs hashed after every rule; cut-off log and count
- [x] Verdict: finished / running (waited, WaitGraph cycle check) / not started (transitive); Scheduler.query
- [x] RunSummary, onSummary, LastSummary, --content-cutoff=off, fast path
- [x] Tests (ContentCutoffTests.fs), macOS green, build.fsx green
- [x] Windows VM check: core 0 warnings, 1815 passed / 21 skipped; the cut-off fixture 30/30
- [x] Pushed, PR OlegZee/Xake#143 open (2026-11-18)
- [ ] Owner review, CI, squash-merge

## Later
- The no-build verdict's memo holds one lock (CommonLib.memoize): hashing a moved source inside
  it is serialised across tasks. Fine for sources (hashed at moves only); revisit if it shows.
- `--dryrun` / progress use the no-build verdict, so they report a target behind a cut-off
  dependee as out of date (they cannot build to compare).
