# Concurrent import race

## The race

`Project.import` runs two `dotnet msbuild` calls per project: a design-time build
(`-restore -t:PrepareResources;Compile`) and a `-pp` preprocess. One lock rule per (framework,
brand) means two brands can import the *same* project file at once. Both `-restore`s write
that project's `obj/project.assets.json` / `obj/*.nuget.g.*` -- only `IntermediateOutputPath`
is per-variant, not `BaseIntermediateOutputPath` -- so when the package set depends on a
property (`GcPrefix` -> `gcdocs.*` vs `ds.documents.*`), one restore lands between the other's
restore and its design-time build, which then reports the wrong references. Timing-dependent;
observed once.

## Two failed property routes

- **`BaseIntermediateOutputPath` per variant** fixes the assets race but breaks `ResxTests`
  with duplicate assembly attributes -- more than one SDK-derived path keys off it.
- **`MSBuildProjectExtensionsPath` per variant** is what restore keys its generated
  `.props`/`.targets` off, but broke the design-time build's own reference resolution
  (0-21 references instead of 113+): the SDK's resolution targets assume this sits under the
  project's own `obj/`, not an arbitrary per-variant one.

Both changed *where* restore writes; the fix instead changes *when*.

## The fix: a per-project-path lock

`Project.withProjectLock : string -> Recipe<ExecContext,'a> -> Recipe<ExecContext,'a>`
(`Project.fs`) wraps the two msbuild calls of one project in a `Resource` of quantity 1, keyed
by its full path (ordinal on Unix, ordinal-ignore-case on Windows). A process-wide
`ConcurrentDictionary<string, Resource>` hands out one `Resource` per path, so different
projects still import in parallel; only two imports of the *same* file serialize. This is
`Resource`/`withResource` (`docs/delegated.md`) used exactly as documented -- a plain value
and a recipe-level bracket, so a library recipe uses one with no script-side declaration. The
wait yields the CPU slot, so a blocked import never pins a worker; recipes run as tasks in one
process, so a process-wide lock is sufficient.

## The other half: the assets file itself

Serializing stops corruption mid-restore, but the *last* import to run still leaves its
`obj/project.assets.json` for the next to read -- whichever brand imports last "wins" the
shared file, and anything reading it later (the SBOM step did) would read the wrong brand's
graph. The stopgap was a per-variant copy of the assets file under `obj/xake/<fwk>/<variant>/`
with `Properties["ProjectAssetsFile"]` pointing at it. **Gone with Stage B (2026-09-24)**: the
import now *reads* the graph right after the design-time build, still inside the lock, and
records it in the lock entry itself (`Dependencies.Packages`, see nuget-sbom.md "The graph lives
in the lock"). Nothing reads `obj/project.assets.json` after the import any more, so the file
being overwritten by the next brand's restore is harmless -- the lock carries what this import
saw.

## Tests

`ProjectImportTests.fs`: four unit tests drive `withProjectLock` through a small long-lived
`xake {}` engine (no msbuild) with a concurrency meter -- the `DelegatedTests.fs` pattern for
`withResource`: same path never overlaps, different paths do, and paths differing only by case
serialize on Windows but not Unix.

## What remains

msbuild node reuse (`-nodeReuse`, on by default) keeps a persistent process per toolset
version; nothing here disables it. It caches *evaluation*, not the on-disk assets files, and
each call still does its own `-restore` and design-time build, gated by the lock -- so it
doesn't reopen this race. Left enabled: disabling would slow every import for no benefit once
the lock serializes the writes; revisit only if a symptom points at shared in-process state.
