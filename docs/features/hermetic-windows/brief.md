# Brief: hermetic build of the consumer solution on Windows

Status: draft for the owner's review, 2026-10-26. Sources: the seven roadmap sections dated 2026-10-26
(`docs/roadmap.md`, "Line-ending-neutral hashes..." to "Satellite assemblies..."), and three analyses that
checked them against the code on this branch: [lock-content.md](analysis/lock-content.md),
[engine.md](analysis/engine.md), [import-msbuild.md](analysis/import-msbuild.md). Where an analysis
disagrees with the roadmap, the analysis wins and this brief says so. File:line references are the
analyses'; they are of this branch after #89.

## 1. Purpose and scope

The consumer solution was imported on macOS (locks recorded there) and built on Windows. That run
produced seven remarks, now in the roadmap. This brief gives each one a verdict, a recommended option,
its effort and its impact on locks, the `.xake` database and the release plan. It also lists every open
question for the owner and a proposed split into PRs. It does not decide the open questions and it is
not an implementation plan: that follows the owner interview.

## 2. Summary

| Item | Verdict | Recommended | Effort | Lock / db / format impact | Release |
|---|---|---|---|---|---|
| (a) Line-ending-neutral hashes | confirmed, wider (also `App.Files`, own `App.config`) | B: record normalized, accept raw or normalized | M, 2-3 d | no format bump; Windows-recorded locks change on re-import; LF-recorded format-2 locks become portable as is | hermetic 0.8 |
| (b) Targeting packs from NuGet | confirmed with corrections | A: `PacksFrom = NuGet` default, one global property | S, 1 d + docs | no format change; pack hashes change once on re-import | hermetic 0.8 |
| (c) msbuild errors in the log (+ msbuild in `ProjectRoot`) | confirmed, with a nuance | classify `<origin> : error CODE:` lines + summarizing failure | S-M, 1-1.5 d | none | classifier: Xake 3.12 (or a local copy in hermetic 0.8); summary: hermetic 0.8 |
| (d) MSBuild floor check | confirmed with corrections (17.10 / SDK 8.0.300) | `dotnet msbuild -version` now, the SDK probe later | S, 0.5 d | none | hermetic 0.8 (probe variant needs Xake 3.12) |
| (e) Secret variables | confirmed, wider | A: new dependency case + log masking | M, 3-4 d | `.xake` format 0.5 -> 0.6 (old files upgrade on open) | Xake 3.12 + hermetic with range `[3.12, 3.13)` |
| (f) Build log per run | not confirmed; real defects nearby | A: keep truncate, fix the writer | S, 1 d | none | Xake (next release, not breaking) |
| (g) Satellite assemblies | confirmed; reopens decision S71 | A: nested `Satellites` captured from msbuild | L, 6-9 d | new optional lock field, no bump; affected locks change on re-import | hermetic (after the owner's go) |

"Xake" is the `Xake` NuGet package (engine + `Xake.Dotnet`). The hermetic package references it as
`[3.11.0.40, 3.12)` (`Xake.Hermetic.Dotnet.fsproj:82`), so any change to a `Xake.Dotnet` public or to the
engine's public records means Xake 3.12 and a range bump in hermetic.

## 3. Items

Priority order. (a) and (b) come first: they make every Windows build of macOS-recorded locks fail or
churn, and they share one re-import. (c) and (d) are diagnostics: cheap, and their absence cost hours of
running msbuild by hand. (e) is a real leak but needs a core release and a database format change. (f)
is mostly a non-issue with small real defects. (g) is the largest and reverses a recorded decision.

### (a) Line-ending-neutral hashes, incl. `App.Files` and `App.config`

Observed on Windows: with `core.autocrlf`, git checks the repository's msbuild files out with CRLF.
Every import of the lock set was reported "changed since the import that wrote it", though nothing changed.

What the code does:
- Record: `Import.fs:1501`, `Imports = ctx.Inputs @ appSources |> List.map Csc.hashed`, raw bytes
  (`Csc.sha256`, `src/dotnet/Csc.fs:329`). `ctx.Inputs` are the repository's msbuild files
  (`Import.fs:675-686`); `appSources` adds `App.config` and `runtimeconfig.template.json` (`Import.fs:1488-1497`).
- Check: `Locks.staleOf` (`Locks.fs:440-443`, through `FileHash.sha256`, `Lock.fs:13-21`), used by `check`
  and by every replay (`Locks.fs:488-504`, `:535-536`).
- Also raw: `Lock.rehash` (`Lock.fs:541-543`, bypasses the memo); legacy `Project.import` (`Project.fs:312`).
- The lock text itself is already LF-normalized (`Locks.fs:262`, `Import.fs:1032`, `:1932`).
- Not in the roadmap: `App.Files` beside the lock (`*.runtimeconfig.json`, `*.deps.json`, `*.config`) are
  written as LF and hashed (`Import.fs:1258-1277`), then checked raw by `Apps.write` `checkHash`
  (`Apps.fs:318-321`). A CRLF checkout fails the *build* with "does not have the hash the lock records".
  The project's own config (`ownConfig`, `Import.fs:1245`) has the same problem.

Corrections to the roadmap: "byte for byte" holds only through `FileHash`; the list of text inputs misses
`App.Files` and `ownConfig`; `Lock.rehash` is a second cache path; a per-item `"Eol"` field touches
`Hashed`, a `Xake.Dotnet` type (`Csc.fs:211`), so it would need a Xake release.

Options:
- A. Bump the lock format to 3, hash text roles normalized. `Lock.parse` refuses older imported entries
  (`Lock.fs:1169-1170`), so the guard must change too. Older packages refuse format 3 cleanly.
- B. No bump. Record the normalized hash (CRLF -> LF, lone CR kept, BOM unchanged); accept a match on
  either the normalized or the raw hash.
- C. Document `.gitattributes` only. No code, relies on every consumer repository.

Recommendation: B. No format change, no Xake change, and LF-recorded format-2 locks become portable
without a re-import. Add `FileHash.textSha256` in the hermetic `Lock.fs` with its own memo. Text is decided
by role, not extension: all of `Evaluation.Imports`, the `App.Files` the import wrote as text
(`Import.fs:1041-1044`, `:1912-1915`), and `ownConfig`. Binaries stay raw. UTF-16 files stay raw, documented.

Compatibility: format-2 locks stay readable. Windows-recorded locks (raw CRLF hashes) still match raw on
Windows and become portable after one re-import. An older hermetic package on Windows reading a new lock
reports a false "stale": loud, not silent. No `.xake` impact. Fits `[3.11, 3.12)`.

Tests: `staleOf` with a CRLF copy of an LF-recorded file and the reverse; a content change is still stale;
a lone CR is not normalized; an old raw CRLF hash is current on CRLF; raw and normalized `FileHash` of one
path in one process; `Apps.write` accepts a CRLF `runtimeconfig.json`; the record of a CRLF
`Directory.Build.props` stores the normalized hash. Docs: `docs/hermetic/lock.md` says lock sets are portable
across OSes and `core.autocrlf`.

Effort: M, 2-3 days.

### (b) Targeting packs from NuGet by default

Observed: the same project with the same SDK, recorded on macOS and on Windows, got different hashes for
pack files (336 per entry). One machine used the installed pack, the other the signed NuGet package. The
consumer script works around it by pointing `NetCoreTargetingPackRoot` at a folder that does not exist.

What the code does: `Lock.Identity.ofPath` (`Lock.fs:734-758`) names both copies `pack:<id>/<ver>/<path>`;
the hash is of whichever copy msbuild used (`Import.fs:1427`). Xake passes no `NetCoreTargetingPackRoot`.

Corrections to the roadmap:
- The replay already copes in one direction. `toPathHashed` (`Lock.fs:826-830`) uses the installed copy
  only when its hash matches, else the package copy, which `Restore.ensure` fetches. A lock recorded from
  NuGet builds everywhere. A lock recorded from an installed pack fails hard on a machine without that pack,
  after the fetch, in `packMismatches` (`Lock.fs:1590-1615`).
- An SBOM pack row has an empty `Sha512` when only the installed pack was used (`Import.fs:1461-1480`);
  NuGet by default also fixes that.
- A separate pack version pin matters only for an unpinned SDK. An exact `global.json` pin already fixes
  bundled pack versions, and `KnownFrameworkReference` reading (`DotNetFwk.fs:509-545`) is for `HermeticMode` only.

Options: A. `RecordOptions.PacksFrom = NuGet | Installed`, NuGet default, the import adds
`-p:NetCoreTargetingPackRoot=<missing temp folder>` to `globals` (`Import.fs:1645`). B. Rehash pack files
from the package copy after recording (hashes a file msbuild did not read). C. Record both hashes (doubles
pack lines, still machine-dependent).

Recommendation: A. Proven by the consumer, one property, msbuild's real inputs are recorded. It covers
targeting, apphost and runtime packs; `SdkHost` is unaffected. The extra global also changes the `key cfg`
intermediate folder (`Import.fs:1650-1656`), harmless.

Compatibility: no format change. Every lock with a pack reference changes once on the next import. Imports
now need the `Microsoft.*.App.Ref` packages on the feed; a private feed without a nuget.org upstream fails
the restore, and the error must say so. Fits `[3.11, 3.12)`.

Tests: `RecordTests`, the property is present by default and absent with `Installed`; a record test (needs
the SDK) puts `pack:` paths under the package folder with a `Sha512`; existing `toPathHashed`/`packMismatches`
tests stay. Verify on SDK 8 and 9 that a missing pack root does not trip `ProcessFrameworkReferences`,
`ResolveAppHosts`, `ResolveTargetingPackAssets`.

Effort: S, 1 day plus docs.

### (c) msbuild errors of an import in the log, and msbuild in `ProjectRoot`

Observed: a failed restore or evaluation left only "failed with exit code '1' ... Exiting due to
FailOnError" in the log. NU1008, NU1605 and NU1301 had to be found by running msbuild by hand.

What the code does: `Project.runMsbuild` (`Project.fs:91-104`) logs stdout at Verbose, stderr at Error.
`Tool.diagnosticLevel` (`src/dotnet/Tool.fs:45-50`) knows `"): error "` and lines starting with `error `;
msbuild's positionless `<origin> : error CODE: ...` matches neither. Restore errors and `MSB1001` go to
stdout (probed), so they land at Verbose. `filelog ... Chatty` drops Verbose (`Logging.fs:46-47`).
Nuance: positioned errors already reach Error. And `-getResultOutputFile` itself moves evaluation errors
from stderr to stdout (probed), so the switch the import needs hides every evaluation error. The same gap
exists in `Described.runRestore` (`Described.fs:217-239`), `DotNetFwk.fs:953-954` and the msbuild task
(`Dotnet.Msbuild.fs:100-101`).

Second defect: `runMsbuild` sets no working directory (`Project.fs:94-103`), so `dotnet msbuild` resolves
`global.json` from the process cwd, while the SDK probe runs in `ProjectRoot` (`DotNetFwk.fs:444`). A script
run from elsewhere can check one SDK and run another.

Options: A. Extend the classifier with `^\S.* : (error|warning) [A-Z]+[0-9]+:`; in `Tool.diagnosticLevel`
(all runners, csc/fsc included, Xake release) or locally in hermetic. B. A summarizing failure:
`msbuild failed on '<name>' (exit 1): <project>: NU1008 ...` (deduplicated, first 5, `[entry]` suffix
stripped, last ~20 lines when no error line was seen). C. Raise all stdout to Info (noise).

Recommendation: A + B. Errors -> Error; warnings -> Info, deduplicated per import run (a large solution
prints many NETSDK1138/NU1701/MSB3277 warnings). Same classifier and summary for the `dotnet restore`
runners. Run msbuild in `ProjectRoot`.

Compatibility: no lock or db impact. A changed `Tool.diagnosticLevel` changes what the msbuild task and
csc/fsc show (more correct errors); the signature stays, so it is binary-compatible but needs a release note.

Tests: extend `DotnetTasksTests.fs:470` with `/p/a.csproj : error NU1101: ...`, the same with
`[/t/import.sln]`, `MSBUILD : error MSB1001: ...`, a `warning NU1603`, `CSC : error CS2001`, negatives
`Build FAILED.` and `0 Error(s)`; the summary builder as a pure function; an integration test
(`RecordKit`, `RecordTests.fs:466-512`) with an offline NU1008 fixture, asserting the exception and the log line.

Effort: S-M, 1-1.5 days.

### (d) The MSBuild an import needs, checked up front

Observed: on SDK 8.0.121 the import failed; the roadmap reads it as `MSB1001` on `-getResultOutputFile`.

What the code does: `-getResultOutputFile` is passed by `evaluate` (`Import.fs:649-652`) and by
`Project.import`'s restore and design-time build (`Project.fs:438-442`, `:467-472`). `Import.record`'s
restore of `import.sln` (`Import.fs:1706-1712`) passes no `-get*` switch.

Corrections to the roadmap:
- The floor is MSBuild 17.10 / SDK 8.0.300, not 17.9 / 8.0.200 (`-getResultOutputFile` came with
  dotnet/msbuild PR #9640, in 17.10). `-getProperty`/`-getItem` are 17.8. Confidence high; one run on an
  8.0.2xx SDK would settle it.
- In `Import.record`, `MSB1001` appears at evaluation, not at restore, and the failure names the project
  file. A failure naming `import.sln` is a restore failure for another reason (feed, NU1008, ...), hidden by
  item (c). Only `Project.import` hits `MSB1001` in its restore.

Options: A. Pre-check from the SDK probe (`DotNetFwk.fs:432-470`) plus `<sdkDir>/MSBuild.deps.json`, no
extra process; needs a public `sdkVersion` in `Xake.Dotnet`, so Xake 3.12. B. `dotnet msbuild -version`
once per import call, before the restore; hermetic only; one extra process. C. Recognize `MSB1001` after
the fact (fires after a long restore, one switch only).

Recommendation: B now, A with the next Xake release. Check once per import call (`Import.record`,
`Import.descriptions`, `Project.import`), not per evaluation. Message by `globalJsonPin`: pinned -> "raise
the pin"; unpinned -> "install SDK 8.0.300 or later". Run the check with the same cwd as the imports
(see (c)). Tolerate preview versions; an unreadable version skips the check.

Compatibility: none for locks or db. Docs: a "Requirements" line in `docs/hermetic/description.md`
(`:103`), a row in `docs/hermetic/workflows.md` G (`:703`), release notes.

Tests: pure unit tests of the decision: 8.0.121 pinned and unpinned fail with the right text, 8.0.204
fails, 8.0.300, 10.0.100 and a preview pass, unreadable passes.

Effort: S, about 0.5 day.

### (e) Secret script variables

Observed: a feed token and a license key declared as script variables are stored in clear in `.xake`,
and a rotation prints old and new values.

What the code does: `Vars.resolveVar` (`Vars.fs:81-93`) records `EnvVar (name, value)` or `Var (name, value)`
(`ScriptFuncs.fs:98-118`). Leaks: `.xake` (`Database.fs:76-77`); the change reason
(`DependencyAnalysis.fs:80-84`) logged at Info (`ExecCore.fs:373`), which reaches the default `build.log`
at Chatty; dry run (`ScriptRunner.fs:56-58`); `--dump` (`DependencyAnalysis.fs:187-193`); a conversion
error (`Vars.fs:30`); `--help` defaults (`Vars.fs:159`); the shell options dump at Debug (`Shell.fs:66`,
including `EnvVars`); tool command lines at Debug/Verbose. `-d` values are not echoed (`Program.fs:50-51`).

Corrections to the roadmap:
- The log leak is not only "on rotation into the build log": with default options it is `build.log` at
  Chatty, and the shell options dump is a second, easier leak.
- Child processes already inherit the environment (`ProcessExec.fs:14-21` only adds), so an env-sourced
  secret already reaches msbuild. What is missing: a `-d` secret reaching it, and tracking that msbuild read it.
- `Project.ImportOptions` (`Project.fs:23`, env at `:409`) needs an `Environment` field too, not only
  `Import.RecordOptions` (`Import.fs:742`).
- `DependencyAnalysis` sees only the record, so a secret must be recognisable from the record itself.

Options: A. A new `SecretDep (name, source, digest)` case, digest `Absent | Present | Hashed (salt, hash)`
(HMAC-SHA256, per-record random salt); `|> secret` on any var plus `Var.secret<'t>` (env only); messages
say "was changed/set/unset", `--dump` and `--help` print `***`; a per-run masking registry in the loggers
(values of 4+ characters). B. An in-band marker inside `EnvVar` (no format change, but old Xake reports
"changed" every run). C. Masking only (leaves `.xake`).

Recommendation: A. Hash by default, presence-only opt-in. Add `Environment: (string * string) list` to
`Import.RecordOptions` and `Project.ImportOptions`; document "pass secrets by environment, never `-p:`".

Compatibility: `dependency` pickler gets tag 7, `XakeDbVersion` "0.6"; 0.4/0.5 files load and upgrade
on open. An older Xake meeting 0.6 recreates the database (warning, full rebuild). A new field on the public
`OptionalVar`/`RequiredVar` records is a binary break, so Xake 3.12 and a hermetic range bump to
`[3.12, 3.13)`; a separate `SecretVar<'t>` type avoids the break at the cost of more code. No lock impact.

Tests (`VarsTests.fs`, `StorageTests.fs`): `SecretDep` recorded and the `.xake` bytes lack the value;
rotation rebuilds (hash) or not (presence); unset -> set rebuilds; reason and `--dump` lack the value; a 0.5
database upgrades without spurious rebuilds; `shell { envs [...] }` at Diag logs `***`; `-d` on `Var.secret`
fails clearly; `--help` shows no value; `Environment` reaches msbuild in a fixture.

Effort: M, 3-4 days (core 2-2.5, hermetic 0.5, docs 0.5).

### (f) A build log per run

Verdict: the claim is not confirmed. `FileLogger` deletes the file on creation (`Logging.fs:86`) and is
created once per `xake {}` run (`ScriptRunner.fs:7-12`). Each run already starts a fresh `build.log`;
deleting it in a CI step is redundant. A growing file likely comes from elsewhere (a `ToFileAppend`
capture, `Shell.fs:85-87`, or a CI wrapper): check the consumer script first.

Real defects nearby:
- Lost tail: messages go through an unflushed mailbox, and a failed build ends with `exit`
  (`ScriptRunner.fs:189`). The last lines ("Build failed ...") can be missing. Same for Ctrl+C.
- Name clash: two runs in one process or folder delete and interleave each other's log; on Windows a
  concurrent open throws inside the mailbox and logging silently stops.
- Relative path: the file is relative to the process cwd, not `ProjectRoot`.
- "See build.log for more details" is hard-coded (`ScriptRunner.fs:161`), whatever `FileLog` is or when it is off.
- Open/append/close per line is slow on Windows with antivirus at Diag.

Options: A. Keep truncate-per-run; one `StreamWriter` (`FileMode.Create`, `FileShare.Read`), flush per
message or batch, dispose before `exit`; on a clash fall back to `build.<pid>.log` with a warning; name the
real `FileLog` in the failure message. B. A + rotation (`build.1.log`...). C. Opt-in `append`.

Recommendation: A. Not breaking. B and C only when asked.

Compatibility: none for locks, db or binary API. Tests: a failed build's last line is in the file; two runs
in one process leave two complete logs, never interleaved; a second run truncates the first; `rootdir` with a
relative `filelog` lands where the docs say.

Effort: S, about 1 day.

### (g) Satellite assemblies in imported locks

Observed: a build from locks has no `<culture>/<asm>.resources.dll`; a nuspec that ships them leaves them
out (37 files per variant, `ja` and `zh-CN`).

What the code does: culture `EmbeddedResource` items are reported and dropped (`Import.fs:410-411`,
`:443-444`, `:1415-1422`; `ProjectPlan.fs:301` refuses them). A referenced project's satellites are not
copied (`Import.fs:1176-1180`; `Locks.fs:842`, `:878`). This is decision S71, "deferred by decision"
(`docs/hermetic/README.md:214-218`), with no reason beyond "a later step". Package satellites
(`lib/<tfm>/ja/*.resources.dll`) already work, in `App.Copy`, the SBOM and pack.

Corrections to the roadmap: `al` is never used on this route; `dotnet msbuild` always runs the `Csc` task
in `CoreGenerateSatelliteAssemblies`. The roadmap omits the run folder, the `deps.json` `resources` lines and
the version (a satellite's `AssemblyVersion` must equal the main assembly's, substituted via `VersionToken`,
`Locks.fs:401`, `:577-608`). "Entries of their own" is the rejected option C below.

Options: A. A nested `Satellites` section in the entry, captured from msbuild by hooking
`CoreGenerateSatelliteAssemblies` in `xake-record.targets` (`Import.fs:1745`, `:1753`): per culture the csc
switches, the generated attributes source with the version token, the culture resx pairs (built by the
existing `Resx`); replay writes `<out>/<culture>/<asm>.resources.dll` as side outputs. B. Xake composes the
satellite compile itself (drift likely, against "record msbuild"). C. One entry per culture (breaks the
one-entry-per-framework invariant, `Locks.fs:516-525`).

Recommendation: A, plus run folders take referenced satellites by name from the lock, and stand-in
satellites in the app pass (`Import.fs:1801-1824`) so msbuild's `GenerateDepsFile` lists them.

Compatibility: an optional field, read by name (`Lock.fs:1019-1021`); an older reader ignores it and builds
without satellites, as today. No bump; if (a) ever bumps, it rides along. Locks without culture resources do
not change. Trap: under invariant globalization `Strings.ja.resx` is a neutral resource, so the lock differs
by machine. No Xake change is named in the analysis.

Tests: `Satellites` round trip; a lock without culture resx byte-identical to today; a `ja`/`zh-CN` fixture
recorded and replayed with `AssemblyCulture`, the main version and public key token; referenced satellites in
the run folder and `deps.json`; `Pack.fromNuspec` picks them up; the invariant-globalization case.

Effort: L, 6-9 days.

## 4. Decisions for the owner

Nothing here is decided. Suggested defaults in brackets.

(a) Line endings
1. Bump the lock format or not? [no bump, option B; accept a false "stale" from older packages]
2. Include `App.Files` and the project's own `App.config` in this change? [yes]
3. Should the import write `<lock>.files/.gitattributes`? [no; mention `.gitattributes` in the docs]

(b) Packs
4. Make NuGet the default for packs? [yes]
5. Keep `Installed` as an opt-in for offline imports? [yes]
6. A pack version pin independent of the SDK? [no; the exact `global.json` pin is the mechanism]

(c) msbuild errors
7. Classifier in `Tool.diagnosticLevel` (all runners, Xake 3.12) or local in hermetic? [`Tool.diagnosticLevel` in 3.12; local copy only if hermetic 0.8 ships first]
8. Import warnings at Info (file log) or Warning (console)? [Info, deduplicated]
9. How many errors in the failure message? [5, then "+N more, see the log"]
10. Same classifier and summary for the `dotnet restore` runners of `Restore`/`Described`? [yes, same PR]
11. Run `runMsbuild` in `ProjectRoot`? [yes]

(d) MSBuild floor
12. Check source: `dotnet msbuild -version` in hermetic, or the probe + `MSBuild.deps.json` (Xake release)? [`-version` now, the probe with the next Xake release]
13. Confirm the 17.10 / 8.0.300 floor with one run on an 8.0.2xx SDK before release? [yes]

(e) Secrets
14. Default digest: salted hash or presence-only? [hash; presence-only opt-in]
15. API shape: `|> secret` flag on the existing records (binary break) or a separate `SecretVar<'t>`? [flag, ship as Xake 3.12]
16. `-d` for a secret: refuse, warn or allow? [refuse for `Var.secret`; warn for `|> secret` on an arg var]
17. Engine-wide log masking now or later? [now]
18. Minimum length for masking? [4 characters]
19. A plain `getEnv` of a name declared secret: mask and hash it too? [yes, the name decides]

(f) Build log
20. Trace the consumer's growing log to its own script or CI step before any engine change? [yes]
21. Relative `filelog`: process cwd or `ProjectRoot`? [process cwd, documented]
22. Rotation now? [no]
23. An `append` option at all? [no]
24. On a name clash between concurrent runs: fallback file or fail? [fallback `build.<pid>.log` with a warning]

(g) Satellites
25. Reopen S71 now, for a project's own and a referenced project's satellites? [yes, both]
26. Capture msbuild's satellite `Csc` call or compose it? [capture]
27. Culture-suffixed resx under invariant globalization: fail or warn? [fail]
28. F# projects in scope? [C# first, F# when a consumer needs it]

Packaging
29. Land (g) with (a)+(b) for a single re-import, or accept a second re-import later? [ship PR1 now; second re-import when PR5 lands]

## 5. Packaging into PRs

- PR1, lock portability: (a) + (b). Hermetic 0.8, no Xake change, fits `[3.11, 3.12)`.
- PR2, import diagnostics: (c) + (d). Hermetic 0.8 with a local classifier and `dotnet msbuild -version`;
  or the classifier in `Tool.diagnosticLevel` with Xake 3.12 (question 7).
- PR3, secrets: (e). Xake 3.12 (dependency case, db 0.6, var flag) first, then hermetic with
  `Environment` fields and range `[3.12, 3.13)`. The `Environment` fields alone could ship in 0.8 earlier.
- PR4, log fixes: (f). Xake, not breaking; can share the 3.12 release with PR3 and PR2's classifier.
- PR5, satellites: (g). Hermetic, only after the owner reopens S71.

Re-import: (a), (b) and (g) all change the consumer's locks (normalized hashes for Windows-recorded sets,
package hashes for packs, a new field for localized projects). Plan one re-import per lock-changing release.
If PR5 is ready close to PR1, ship them together for a single re-import.

## 6. Roadmap corrections

To apply to `docs/roadmap.md`, in the sections dated 2026-10-26:
1. MSBuild floor: 17.10 / .NET SDK 8.0.300 (`-getResultOutputFile`), not 17.9 / 8.0.200.
2. `MSB1001` in `Import.record` appears at evaluation (naming the project), not at the restore of
   `import.sln`; a failure naming `import.sln` is a restore failure of another cause.
3. Build log: `filelog` already truncates per run (`Logging.fs:86`); replace the item with the real defects
   (lost tail, name clash, relative path, hard-coded "See build.log").
4. Satellites: `al` is never used; `dotnet msbuild` runs the `Csc` task. Record them as a nested section,
   not entries of their own; add the run folder, `deps.json` and version parts; note it reopens S71.
5. Line endings: `App.Files` (`runtimeconfig.json`, `deps.json`, `.config`) and the project's own
   `App.config` are affected too, and fail the build, not only `check`; `Lock.rehash` also hashes raw.
6. Secrets: child processes already inherit the environment; the gap is `-d` secrets and tracking. The
   default `build.log` at Chatty and the shell options dump are leaks too.
7. Secrets: `Project.ImportOptions` also needs an `Environment` field, not only `Import.RecordOptions`.
8. Packs: the replay already falls back to the package copy; the hard failure is a lock recorded from an
   installed pack built where that pack is missing. A separate pack pin matters only without an exact SDK pin.
