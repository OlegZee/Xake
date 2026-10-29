# Release process — tracker

Decided 2026-10-29: A, B, E now; then D (after vars and patterns). See [review.md](review.md) §6.

| # | Item | Status |
|---|---|---|
| A1 | `publish.yml`: the version is the tag (`X.Y.Z`), no run number; devprocess.md, docs/session.md | done (#106) |
| B1 | core: `ExecContext` helper for the logger copy (hermetic `Lock.fs:1598` stops building the record); record-shape snapshot test for the seven records | done (#107), ships in 3.15 |
| B2 | hermetic: range `[<floor>, 4.0)`, an overridable Xake version property for CI; the rule in devprocess.md | done (#108); range `[3.14.0.45, 4.0)`, `XakeVersion` property, build.fsx `XAKE_VERSION` |
| B3 | CI: hermetic tests against `src/core` + `src/dotnet` of `dev` (local feed, exact version) and against the newest published Xake | done (#108); job `hermetic-compat`, build.fsx `test-project::<project>` |
| B4 | hermetic: `Lock.fs:1598` onto the B1 helper (after the Xake release with B1) | deferred (owner, 2026-10-29): `ExecContext` is frozen until 4.0, so the copy is safe; do it with the next floor raise for another reason, or in D |
| E1 | `build.fsc.fsx bump-base`: Xake version -> fsproj range, `import`, hermetic lock only | in PR (`pr/bump-base`) |
| E2 | tests read the range from the fsproj; readmes `#r` unversioned; base history out of the fsproj comment into release notes | in PR (`pr/bump-base`); the fsproj history went out in #108; the test fixture keeps its own copy (a snapshot, not tracked) |
| E3 | bootstrap bump: `build.fsx` / `build.fsc.fsx` to Xake 3.14.0.45 / hermetic 0.9.0.46 | done (#106) |
| E4 | (optional) workflow opening the bootstrap PR after a publish run | not planned: the bootstrap bump is two known versions now |
| D0 | prerequisites: vars variant 3 (spec rev. 4) and the pattern module merged to `dev` | waiting |
| D1 | design note for the split: package layout, meta-package or 4.0, `pexecSync`/`memoize` public, tests and CI moves | after D0 |
| D2 | `Xake.Dotnet` and hermetic on the final vars and pattern APIs | after D0 |
| D3 | the split itself | after D1, D2 |
