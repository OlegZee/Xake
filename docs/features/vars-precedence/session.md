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
| - | `feature/vars-env-prefix` | implicit `XAKE__<NAME>` source (spec §3.11, P-1..P-8, E-12, tests 85-96), on top of docs PR #113 | local, not pushed |

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


## 2026-10-31: environment prefix (branch `feature/vars-env-prefix`)

Owner's decisions in [brief.md](brief.md) "Environment prefix"; spec amended (§3.11, E-12, tests
85-96; amended tests 10, 14, 21, 22, 27, 30, 35 and pr2 78, 84). Implemented in
`VarEvaluation.fs` (`implicitEnvName`, `ruleDecl` prepends the implicit `Env`, constants read
`XAKE__N` at layer 3, collision check) and `Vars.fs` (`declare` derives `XAKE__` names).
Readings taken by the implementer, for the owner:

- **Constants and `ExecOptions.Vars`** (decision 3 and its assumption): `XAKE__N` overrides the
  value only; a constant is still not a declaration for a Var read (E-8), is not listed in
  `--help`, and has no secret mode (P-3).
- **Collision (P-5)** counts declarations that read their implicit name (incl. constants and
  `ExecOptions.Vars` entries); a rule whose explicit `Env` equals the implicit name of its own key
  counts as implicit; explicit names and `Var.arg` fields take no part.
- **Invalid characters**: anything other than `A-Z`, `0-9`, `_` after UPPER_SNAKE becomes `_`
  (non-ASCII letters too). `toEnvName` only splits a lower-case letter followed by an upper-case
  one: `vc91Config` gives `VC91CONFIG` (a digit before the capital does not split), as in 3.15.
- **Secret rules** without an explicit `Env` now get E-1a (`set XAKE__T instead`) for `-d`,
  where rev. 6 gave E-1b; E-1b remains for `Var.arg ... |> secret`.
- **Sweep:** `build.fsx` `Version`/`HermeticVersion`/`NUGET_KEY` got explicit `envVar`
  (`VERSION`, `HERMETIC_VERSION`, `NUGET_KEY`): publish.yml exports `VERSION` /
  `HERMETIC_VERSION`, and build.fsx bootstraps on Xake 3.15.0, which does not know the prefix;
  explicit names work on both. Workflows unchanged. `build.fsc.fsx` `Version`/`HermeticVersion`
  left derived (nobody sets them from the environment; from 4.0 `XAKE__VERSION`), its
  `NUGET_PACKAGES` constant comment notes `XAKE__NUGET_PACKAGES`. `DotNetVars`/`HermeticVars`
  fields stay `Var.arg` (env-less, decision 5); their doc examples now use `envVar = "NUGET_PACKAGES"` /
  `envVar = "CI"`.

## Leftovers

- The owner has not yet reviewed the import conventions proposed in [pr2-spec.md](pr2-spec.md)
  (`Var.read`, `DotNetVars`/`HermeticVars`, fields named by key, string-typed, `-d` only).
- The importer reads `PrivateAssets=all` as "bundled" (`package.bundle: ["Xake"]`).
- Test 60 (the published hermetic package against the new Xake, `test60-published-hermetic.fsx`)
  has no CI job.
- Optional: measure the launch-evaluation cost, dev vs 4.0.
- Done 2026-10-31 (feature/vars-ignore-env): `ExecOptions.Vars` renamed to `ScriptVars`; `ExecOptions.IgnoreEnvironment` added (spec §3.12).

## 2026-10-31: secret masking cost (closed)

Measured on Apple M5 Pro, .NET SDK 10.0.403, Release (`perf-secrets.md`, scripts in `perf/`).
Five secrets of 24-40 chars cost about 1.1 ms of CPU per 10,000 log lines (115 ns/line vs 3.7
ns/line without secrets; about 22 ns per secret per line, almost all in `Contains`; matches add
nothing measurable). End to end (10,000 lines to a file log) the difference is inside noise: about
0.3% of the file-logging time, about 0.1% of the run. Conclusion: masking cost is not a concern; no
optimisation needed. Side finding, not investigated: in one run without waiting for the logger,
`build.log` had 3,445 of about 10,050 lines at exit (the file logger posts to a MailboxProcessor;
the tail may be lost when the process exits).
