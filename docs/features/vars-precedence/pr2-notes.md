# vars-precedence: PR 2 (library readers, import) implementation notes

Branch `feature/vars-libraries` (on top of `feature/vars-core`, PR 1). Spec: spec.md rev. 6 §3.6,
§7 item 2, §8.8-8.9 and test 60; [pr2-spec.md](pr2-spec.md) for the import (I-1..I-12, E-11,
tests 69-84). Where the specs were ambiguous or did not fit the code, the reading closest to
brief.md was taken; each point is listed here for the owner.

## Conventions chosen (pr2-spec.md, "proposed, owner reviews")

- **`Var.read<'t> (key: string) : Recipe<ExecContext, 't option>`**, a static member of `Var`.
  Declared key: the stored declaration is trusted, only its `ValueType` is checked (E-11), the
  value and dependency as T-2 (`getVar`; a field's typed default comes back typed). Undeclared:
  `getVar`, converted to `'t` (E-4 on failure). Never reads the environment. Returns `None` when
  unset: the library applies its fixed default (I-2).
- **Schemas**: `Xake.Dotnet.DotNetVars.Default` (in `DotNetFwk.fs`) with `NUGET_PACKAGES`,
  `NUGET_FETCH`, `HERMETIC`, `CSC_SERVER`, `XAKE_CSC_SERVER`, `CSC_JOBS`, `FSC_JOBS`;
  `Xake.Hermetic.Dotnet.HermeticVars.Default` (in `Roots.fs`, the earliest file that opens Xake;
  no new file, so no description re-import) with `CI`, `LOCK_REFRESH`, `LOCK_STALE`,
  `SOURCE_DATE_EPOCH`.
- **Fields named by their keys verbatim** (`NUGET_PACKAGES`, not `NugetPackages`): the key is the
  field name with no `name` to repeat, so a copy-and-update override `NUGET_PACKAGES = Var.string()`
  keeps the key and derives the env name `NUGET_PACKAGES` by itself. All fields are `string`,
  `Var.arg` (no `Env` source, L-1), `withDefault` the library's constant default where it has one
  (`.packages`, `on`, `off`, `on`, `off`, `fail`); the switches are parsed at the read, so an
  invalid switch is E-5 at the read with or without the import.
- **E-11** `Variable 'N' is read as 'T' but is declared by DECL as 'U': declare it as 'T'` (full type names).

## Deviations and readings

1. **§3.10 said "T-4 still applies when the key is not declared"**; the brief and the task say
   the import is optional and without it the library reads `getVar` with its fixed default.
   `Var.read` follows the brief: undeclared is `getVar` (no E-8). pr2-spec I-1 states it.
2. **Importing records the default** (I-9): with the import, a read records `Var (N, Some "on")`
   instead of `Var (N, None)`, so a target built before the import rebuilds once. Values are
   unchanged (constraint f).
3. **Variables of L-2 marked "unchanged"** (`NETFX`, `CSC_TOOLSET`, the reference-pack versions,
   ...) are not in `DotNetVars` and keep `getVar` (pr2-spec §1); they could be added later.
4. **`*_JOBS`** stay untracked reads of `ctx.Vars` (I-4); `jobsLimit` keeps its signature
   `(variable, envVariable)` (the second name is ignored now, record shape of the internal run
   plan unchanged). No E-11 for them.
5. **`CompilerServer.fromEnvironment`** is public: kept, `[<Obsolete>]`, returns `Shared None`
   (no caller in this repo or in hermetic 0.10). `CSC_SERVER` negative numbers fail (E-5).
6. **`DotNetFwk.parseSwitch name value`** (new public helper) is the one on/off parser
   (`HERMETIC`, `NUGET_FETCH`, `XAKE_CSC_SERVER`); `HermeticMode.parse` delegates to it.
7. **`PackageRootSource.EnvironmentVariable`/`UserProfile`** carry `[<Obsolete>]` on the union
   cases; `packageRootWithSource` always returns `ScriptVariable`. `Lock.hermeticRecordGate` no
   longer asks for message 2; `HermeticMode.check` still produces it for a caller that passes
   `PackageRootDeclared = false`, with the new hint text (`HermeticMode.fs`).
8. **`SOURCE_DATE_EPOCH`**: "recipe-level callers pass it" became two new recipes,
   `Pack.Options.current ()` and `Sbom.PackageScopeOptions.current ()`; `Pack.Options.Default`
   and `PackageScopeOptions.Default` are the 1980 epoch, constant. `Sbom.PackageScope.timestamp`
   is constant too. Nothing in the package calls them inside a recipe today.
9. **`Locks.staleWarns`, `Locks.refreshValue`** were made `internal` (tests 57); `LOCK_STALE`
   blank is `fail` (L-7).
10. **Tests (XakeTestBase)**: the test jobs switch the server with the environment variable
    `XAKE_CSC_SERVER` (testjobs.fsx); since the library no longer reads it, `XakeTestBase` puts
    it into `TestOptions.CliVars` (a `-d` of the test runner). `VarsConformanceTests` and the new
    fixtures clear `CliVars` so their banners are exact. `XakeTestBase.PackageRoot` is the
    fixture's `.packages`; tests that asserted `~/.nuget/packages` paths now assert it (reference
    packs and the toolset are restored once per fixture folder into `~testout~/<fixture>/.packages`;
    the full src/tests run stayed at about 1 minute). `SdkProbeTests` pass the machine cache as
    `NUGET_PACKAGES`: its roots pin older SDKs, whose restore into a fresh folder fails here.
11. **PR 1 fallout in src/hermetic.tests** (not run against the new core by PR 1), fixed here:
    `BuildFromLockTests` read `TEST_VERSION`/`OTHER_VERSION` as inline `Var.*` values (E-8 now):
    the fixture declares them with `varschema` and passes test values as `-d`; `RecordKit.run`
    appended defaults to `options.Vars` that a test also set (`CSC_SERVER` twice, E-2).
12. **Existing tests inverted** (they asserted the environment fallback): `jobsLimit` env,
    `XAKE_FSC_JOBS` (now through `var "FSC_JOBS" [Env "XAKE_FSC_JOBS"]`), HermeticMode message 2
    (now a pure `check` test, plus "the environment's folder is not read"), PackageRootTests
    "without the variable", FromLockTests restore (the scratch folder as a build variable),
    CscLockTests "under CI (environment variable)" (now `-d CI=true`), HermeticLockTests
    record2, RunFolderTests' package folder.

## Not done / follow-ups

- **The hermetic floor is `[3.16.0, 4.0)` by hand** (commit "build(hermetic): Xake floor
  3.16.0"): `bump-base` refuses a version nuget.org does not have. Until Xake 3.16.0 is
  published, the default restore of src/hermetic fails (NU1102), so CI's `build` job and the
  `hermetic-compat` "newest published" step fail on this branch, and `build-fsc` fails
  (`src/hermetic/xake.project.json` and `locks/Xake.Hermetic.Dotnet/Release.json` still hold
  Xake 3.14.0.45, which has no `Var.read`; the fingerprint of the changed fsproj is stale).
  Same for the hermetic test "Xake's three projects build from their generated locks". Options:
  (a) split the hermetic commits into a PR merged after the Xake 3.16.0 release, then
  `build.fsc.fsx bump-base -d XAKE_FLOOR=3.16.0` re-imports and re-locks; (b) merge as is and
  release Xake first. Locally everything was built and tested with
  `-d XAKE_VERSION=[3.999.0-vars2] -d NUGET_SOURCE=/tmp/xake-vars2-feed` (a pack of this branch).
- **`build.fsc.fsx bump-base`** sets the environment variable `LOCK_REFRESH=Xake` inside the
  recipe; on hermetic 0.11 that is no longer read (and V-3 reads the environment at launch only).
  When `build.fsc.fsx` bootstraps on 0.11, `bump-base` needs another way (e.g. `Locks` taking the
  refresh list as a parameter, or running the lock rule with `-d LOCK_REFRESH=Xake`).
- **Test 60** (published hermetic 0.10 on this branch's Xake): run locally with
  [test60-published-hermetic.fsx](test60-published-hermetic.fsx) (edit the `#i` feed path):
  Xake.Hermetic.Dotnet 0.10.0 from nuget.org, Xake 3.999.0-vars2, `NUGET_PACKAGES` set only in
  the environment, `HERMETIC=on`: the csc lock records, the toolset is restored into
  `<root>/.packages`, the lock names `$(NuGetPackageRoot)`, the environment's folder is untouched.
  PASS. `RecordShapeTests` pass unchanged. CI does not cover it today: `hermetic-compat` runs the
  *current* hermetic sources against the base, not the published binary. Proposed CI step: pack
  the base as 3.999.0-dev (as `hermetic-compat` does) and run this script against it.
- `src/tests` build warnings MSB3825/MSB3568 come from `~testout~/resx-typed/*.resx` left by
  an earlier test run (the tests project globs them); a clean checkout builds with 0 warnings.
- Flaky once under `build.fsx test` (passed alone and in the full direct run): hermetic
  "a present package without nupkg.metadata is a warning" (its log file read right after the
  run held only the start of the log).
