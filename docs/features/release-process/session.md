# Release process — session

Working state of the release-process work on `dev`; each implementation PR updates this file
and [tracker.md](tracker.md).

## Where things stand (2026-10-29)

- [review.md](review.md): the release procedure, its causes, the release history 3.4..3.14 and
  options A-E. Decided (§6): A, B, E now; then D, after the vars (variant 3) and pattern module
  work, which D's scope includes. B4 deferred (owner, 2026-10-29).
- **A, B and E are done**: A1 + E3 (#106), B1 (#107), B2 + B3 (#108), E1 + E2 (#109), bootstrap
  on Xake 3.15.0 / Xake.Hermetic.Dotnet 0.10.0 (`pr/bootstrap-315-0100`).
- Released 2026-10-29: Xake `3.15.0` and Xake.Hermetic.Dotnet `0.10.0`, the first ones published
  as the tag version; hermetic 0.10.0 is the first on `Xake [3.14.0.45, 4.0)`.
- Learned on the 3.15.0 release: nuget.org indexed it ~8 minutes after the push, and the local
  NuGet HTTP cache kept listing 3.14.0.45 as the newest for up to 30 minutes (restore NU1102);
  `bump-base` clears that cache when the floor changes.

## 2026-10-31: the hermetic package builds on the in-repo Xake until 4.0

Owner's decision: until the 4.0 release `Xake.Hermetic.Dotnet` builds against the in-repo Xake
(ProjectReferences to `src/core`, private, and `src/dotnet`), not the published package; at 4.0
`Xake.Dotnet` and the hermetic package are split out of `Xake` together (decision D). Done in #112
(`feature/vars-libraries`): the Xake range and `XakeVersion` are gone from the fsproj; `dotnet pack`
makes the `Xake` dependency the build's Xake version (`-p:XakePackageVersion`, build.fsx passes
`-d Version`; the hermetic version is `-p:HermeticVersion`); `bump-base` is disabled; CI's
`hermetic-compat` job is dropped (`build`'s `test` runs the hermetic tests on the commit's Xake);
`publish.yml` refuses `hermetic-v*` until 4.0; the next hermetic release ships with Xake 4.0. The
published 0.10's range `[3.14.0.45, 4.0)` stays as it is. Importer gap: `Import.descriptions`
reads the private `src/core` reference as `package.bundle: ["Xake"]` in
`src/hermetic/xake.project.json`; only `dotnet pack` packs today, so nothing acts on it.

**Next step**: none in this folder until D0 (vars variant 3 and the pattern module on `dev`);
then D1, the design note for moving `Xake.Dotnet` out of the `Xake` package.

| PR | Branch | Worktree | Status |
|---|---|---|---|
| #104..#109 | — | — | merged; #109's worktree `.claude/worktrees/pr/bump-base` to remove |
| bootstrap 3.15.0 / 0.10.0 | `pr/bootstrap-315-0100` | `.claude/worktrees/pr/bootstrap-315-0100` | draft |
