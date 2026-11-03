# vars-precedence: tracker

Done before 2026-10-29 (see [archive](archive/README.md)): analysis; `setup` op and run
banner (#101); CLI over the script's ops (#103); spec rev. 1-5 (archived).

- [x] Variant fixed by the owner; `brief.md`; rejected designs archived (2026-10-29)
- [x] Spec rev. 6 from the brief (43 rules, 10 diagnostics, 60 conformance tests)
- [x] Owner approval of spec rev. 6 (2026-10-29; amended: evaluation at launch, in createScriptContext)
- [x] Owner approval of the PR split (PR 1 core, PR 2 libraries), 2026-10-29
- [x] Archive shortened to one README (idea, key details, good, why rejected)
- [x] PR 1 #111 (merged): core — names (key = internal name, `cliArg`/`Cli` = -d name), `var` source lists, `varschema` evaluated at launch, `Var` read by stored declaration, errors, `?vars` on top, banner sources, debug dump
- [x] PR 2 spec for the import ([pr2-spec.md](pr2-spec.md)), conventions proposed (2026-10-30)
- [x] PR 2 #112 (merged): optional import of library declarations (schema records, read by key and type); library readers without environment fallback (Xake.Dotnet, Xake.Hermetic.Dotnet), switch values, `.packages`
- [x] Release notes (breaking: undeclared `Var` read, libraries no longer read the environment), docs -- PR 1 and PR 2 sections
- [x] Hermetic part of PR 2 back in #112 (in-repo hermetic build until 4.0, owner 2026-10-31); `feature/vars-hermetic` obsolete
- [x] #102, #111, #112 merged; the rework ships as Xake 4.0 (owner, 2026-10-31)
- [x] Environment prefix (owner, 2026-10-31): spec §3.11 P-1..P-8, E-12, tests 85-96; implicit `XAKE__<NAME>` source, constants overridable, collisions; repo sweep (build.fsx explicit envVar); docs — branch `feature/vars-env-prefix`, not pushed
- [ ] Owner review of the prefix readings (constants not declarations for reads/--help; collision scope) — see session.md

Open follow-ups:

- [ ] Owner review of the import conventions proposed in [pr2-spec.md](pr2-spec.md)
- [ ] The importer reads `PrivateAssets=all` as "bundled" (`package.bundle`)
- [ ] Test 60 (published hermetic against the new Xake) has no CI job
- [ ] (optional) Measure launch evaluation, dev vs 4.0
- [x] Rename `ExecOptions.Vars` to `ExecOptions.ScriptVars` (4.0, owner 2026-10-31)
- [x] `ExecOptions.IgnoreEnvironment` (spec §3.12, Q-1..Q-5, tests 97-105)
- [x] Secret masking performance measured (`perf-secrets.md`): about 1.1 ms per 10,000 lines with 5 secrets, not a concern
- [ ] Check whether the file logger loses the tail of `build.log` when the process exits (seen once during the measurement)
