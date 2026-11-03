# Review: brief + drafts against the interview (2026-10-11)

Independent review of [brief.md](brief.md), [drafts/](drafts/) and [drafts/gaps.md](drafts/gaps.md)
against [interview.md](interview.md) (authoritative) and the source. Fixed decisions (Csc record,
resolve, no hooks, CSC_SERVER, NUGET_FETCH, Xake.Hermetic.Dotnet) are not touched.

Verdict: the layering (description / lock / build-from-lock) and the one-rule-per-layer shape are right
and match the interview; three things do not: `kept` restores rules the user leaned away from and puts a
particular mechanism into the core, the brand-at-lock-generation choice is invented and technically
fragile, and the PR plan starts with the contested engine change and under-sizes package resolution.
Findings are ordered by priority inside each section; P1 = decide before PR 1.

## 1. Fidelity to the interview

P1. **`kept` is round 1 restored under a round 2 constraint, not round 2.** interview.md R2 decisions:
"produced by explicit commands, not by rules" (R2.2 "probably the case to do *without* a rule", R2.3
"another counterexample against generating through rules", R2.4 "a rule may warn"). brief.md:94-100 and
build.fsc.fsx:40-61 keep both generation rules and add an engine class of target whose `need` does not
run (gaps.md E1-E3, ~320 lines). The user asked for "rules over rules" as the only acceptable *core*
form (R2.1); `kept [masks]` is a script-level mask list plus engine state, i.e. the "frozen mode" of
the critique renamed and applied per mask, exactly the "particular problem's solution" the user
excluded. The brief flags this itself (brief.md:179-180) but still makes it PR 1 and a dependency of
PR 6-7. Two readings exist that honour R2 literally:
- (i) no rules: `import`/`locks` are plain commands calling `Description.import` / `Lock.generate`
  (both write only on content change, Q2.2); staleness is a library check over the fingerprints file;
  `build` warns. Engine addition shrinks to the fingerprints file (R2.1 asked for exactly that).
- (ii) a rule combinator, which *is* "rules over rules": `frozen (target "..." { ... })` or
  `target ... |> warnOnly`, no mask list, no new `need` semantics in the engine; the dependency
  stays visible (R1.1-2) and never fires by itself (R2.4).
  Pick one with the user; (ii) is the only one that satisfies both rounds.

P1. **Brand-neutral descriptions + rename-by-prefix is an invented decision.** brief.md:76-78 says
"Chosen"; the interview only says brand settings come from a props file and two-vendor knowledge is
the script's (R1.2, R1.4). The rename heuristic (gaps.md:48-49, `Variant.ofProps`, trial-build.fsx:25)
re-implements msbuild property evaluation by string prefix: it cannot see `Condition="'$(Brand)'..."`
on Compile/Resource items, brand defines, `InternalsVisibleTo` names, key files or package refs to the
other repo's branded packages when `LocalBuild=false`. See §2 for the alternative.

P2. **the trial project: "nothing committed there" vs descriptions beside the project.** brief.md:170-171 (PR 9)
says scripts kept outside the trial project repos, nothing committed; trial-build.fsx:21,40 puts
`src/**/xake.project.json` in both the trial project repos and marks them `kept`. Either they are committed (then
PR 9's acceptance is wrong) or untracked (then the whole kept/fingerprint story is void for the trial project).

P2. **`doc` in the lock rule contradicts the schema.** build.fsc.fsx:51 `fsc { config "Release"; doc }`
vs project.schema.json:57 `options.doc`. One source of truth; also `doc` takes a value today
(src/dotnet/Dotnet.fsc.fs:45). With `doc` in the description the two lock rules collapse to one
`locks/(name:*)/(cfg:*).json` (R1.1 *allows* per-config rules, it does not ask for them).

P3. Fine: Q3 layers, Q4 pack from (c), Q6 feeds, Q7 variant A, lock = project x cfg x vendor (R1.3),
ref choice recorded in A's lock (R2 fix), versions in the script (Q7.4), descriptions dynamic via
`Description.find` (Q1.2 open point).

## 2. Soundness

P1. **Package resolution is a bigger hole than ~250 lines (gaps.md #4).** Composed `fsc {}` has no
package concept: `Ref` is a `Fileset` of files (src/dotnet/Fsc.fs:26), `Lock.ofFsc` writes
`Packages = []` (docs/hermetic/lock.md:727-733). Resolving ids/ranges to compile assets per TFM
(nearest TFM, `ref/` vs `lib/`, transitive graph) is NuGet. Reuse: write a restore project
(`DotNetFwk.restoreProjectText`, used by `Restore.download`, src/hermetic/Restore.fs:258-264), run
`dotnet restore` with a generated `packages.lock.json` + `RestoreLockedMode` for the pins, read
`project.assets.json` (`Nuget.readAssets`, src/hermetic/Nuget.fs:91). That gives "kept versions" and
`LOCK_REFRESH` for free and keeps (b) honest: lock generation needs NuGet and the network; CI never
runs it (Q4 clarifier), fine. PR 3's "packages as plain refs" (gaps.md:90) then means hand-written
file paths into `.packages/`, which the schema cannot express (project.schema.json:26) -- PR 3's
acceptance depends on PR 4's resolution.

P1. **Inputs only msbuild knows: default C# globs.** Import records "every msbuild file read"
(gaps.md #8, Project.fs:132 `parseImports`). Adding a `.cs` file under the SDK's default
`**/*.cs` changes the source list without touching any recorded input, so the description goes
stale silently -- the common edit in the trial project page (hundreds of files, gaps.md Q6). Keep msbuild's globs in
the description for C# (schema already allows it, project.schema.json:38) and *in the lock*,
expanded at (c): sources are the repo and are not hashed today anyway (digest.md:60-61). Expanding
at (b) just moves the problem to the lock.

P1. **nupkg equality "ignoring build number" is unachievable as written.** `Pack.compare`
(gaps.md #10) ignores nuspec `<version>` and file names, but the build number also lands in the
dlls (AssemblyFileVersion/InformationalVersion attributes, hence MVID and bytes). Either fix the
assembly version (major.minor.0) and let only the package version carry the build number, or define
acceptance as "two clean builds *at the same version* are byte-identical" and a separate
"only the version strings differ" diff. The `$(Version)` token in the lock (brief.md:133) must be
excluded from the generated-content hash.

P1. **Brand applied at lock generation.** Import per brand is what msbuild already does
(`Properties = ["Brand", b]`, the repo-a verification script per digest.md:196-197). Safer: import under each
brand and store base + mechanical delta (`variants: { Globex: { assemblyName; defines; packages; ... } }`)
computed by import, no heuristics, still one description per project, brand knowledge stays in the
props files the script names. If the user insists on brand-neutral descriptions, require that lock
generation *verifies* the renamed result against a per-brand import (`Lock.diff`) in PR 9.

P2. **Fingerprints file.**
- One repo-wide `xake.fingerprints.json` conflicts in every PR that re-imports (package-lock.json
  experience) and the resolution needs msbuild, which CI and offline clones do not have. Prefer one
  file per kept folder (`locks/<name>/`, `src/<dir>/`) or a sidecar; sorted JSON alone does not fix it.
- No `.gitattributes` in the repo and `autocrlf` on Windows: text inputs hash differently per OS ->
  false "stale" warnings. Hash LF-normalised content, or use git's blob id.
- "Which artifacts" from `.gitignore` (interview R2.1, brief.md:50) is not implementable from the
  engine today (no gitignore parser in src/core); as `kept` masks it duplicates the `target` masks.
- Fresh clone: the file-hash comparison works without `.xake` (DependencyAnalysis.fs:104 "Not built
  yet" is bypassed by design); state it in PR 1's acceptance, it is the whole point.

P2. **Lock consistency across referenced projects.** A's lock copies B's transitive packages and
records B's (cfg, TFM) (gaps.md #5); when B's lock changes, A is stale, so `locks` must refresh in
topological order -- which rules give for free and plain commands must re-implement. This is the
strongest argument *for* rules at (b); weigh it against R2. Diamond case: A(Debug)->B(Release-only)
->C(Debug exists): B records C/Release, A records C/Debug -> two C's in one build; harmless under
layout A but worth a warning in `Lock.generate`.

P2. **Lock format change without versioning.** The ref choice `{Project; Assembly; Configuration;
Framework}` and unhashed refs are new; `Lock.parse` (src/hermetic/Lock.fs:575-590) refuses only the
flat pre-0.2 shape. Existing 0.2 locks (PR #42's `locks/xake.json`, the trial project verification locks) must be
either read-compatible or refused with "re-import"; the plan says neither.

P3. **Draft defects.** build.fsc.fsx:66 declares `.xml` for every `cfg`, but Debug has no `doc` ->
"target file does not exist" rebuilds forever (DependencyAnalysis.fs:97-99). `build` is driven by
which locks exist (build.fsc.fsx:65): a described project without a lock is silently skipped;
`Lock.needAll` should take the descriptions and fail on a missing lock. Composed options need
`--deterministic+` and `--pathmap` (no handling in src/dotnet/FscArgs.fs) for PR 5's "identical dlls".
`(dir:**)` with `..` segments (trial-build.fsx:40) is unverified (gaps.md:83; Path.fs:204-205 drops
`Recurse::Parent`).

## 3. Simplicity and the user's goals

- The targets hold no data processing (Q7.1): true for the Xake draft. Not for the trial project: trial-build.fsx:56,
  60, 80-85 are nested loops over brands x names x configs inside commands, and `Lock.rootOf` +
  `versionOf root` is a lookup chain to pick a version per repo. Put version per description
  (or per root in `XAKE_ROOTS`) and let `Lock.needAll`/`Package.projects` take the brand list.
- "One screen": 94 lines but ~20 new names (`kept`, `refresh`, `Kept.check`, `Description.{import,
  find,names,beside}`, `Lock.{generate,needAll,compileAs,rootOf}`, `Package.{nupkg,projects}`,
  `Variant.{ofProps,apply}`, `config`, `LOCK_REFRESH`, `XAKE_ROOTS`). A newcomer reads the shape but
  cannot predict what `build` does to a stale lock without the comments at build.fsc.fsx:2-5. Cut:
  the Debug lock rule (one mask, §1), `doc` in the rule, `Description.beside` (`import` takes the
  fsproj fileset), `Lock.rootOf`, and the hand-rolled push loop (`Package.push feed names`).
- One or two recipes for a dozen projects: yes at (c) (one `targets` rule) and (b). Core utilisation:
  `needAll` + per-ref `need` gives the DAG to the worker pool; nothing else to do here.

## 4. PR plan

- Order: PR 1 (engine, contested, ~320 lines, changes `need` semantics) is first and nothing in
  2-5 depends on it. Reorder 2 -> 3 -> 4 -> 5 with `import`/`locks` as plain commands, then decide
  kept/frozen (§1) with working scripts in hand.
- PR 3 depends on package resolution (§2) -- move resolution before it or split PR 4 into 4a (lock
  per config, ref choice, no packages), 4b (restore-based resolution), 4c (pins, `LOCK_REFRESH`).
- PR 5: add `HERMETIC=on` net462 (reference assemblies from a package, digest.md:244-245) and the
  gate moving into `Lock.compile` as a behaviour change for existing `csc { lock }` users.
- PR 7: "the same as CI" is untested while CI runs `build.fsx` only (digest.md:14-17); acceptance
  must include CI running `build` (and `check`) from committed locks with `NUGET_FETCH` on.
- PR 8: acceptance per §2 (version policy); nuspec dependency on `Xake` for the hermetic package
  (gaps.md Q7) must be decided inside it.
- PR 9 is an experiment, not a PR to `dev`: deliver a report; fix the "nothing committed" contradiction.
- Missing everywhere: lock format version + migration note (docs/hermetic/lock.md:567-623,
  workflows.md), docs updates (cheatsheet, tasks.md, csc-syntax.md) per PR, tests named per PR
  (src/tests for engine, src/hermetic.tests for the rest), Windows path/case for the `(name:*)` masks,
  CRLF policy.

## 5. Top risks

1. `kept` lands in the core before the user confirms the reading (§1) -- hard to remove later.
2. Brand-by-rename produces a lock that does not match msbuild's Globex build; found only in PR 9.
3. Package resolution re-implemented instead of delegated to NuGet; weeks, not ~250 lines.
4. C# default globs make the staleness warning blind for the trial project case it exists for.
5. Pack acceptance cannot pass as worded; the PR stalls on a definition.

## Questions for the user before implementation

1. Kept targets: (a) `kept`/`refresh` in the engine as drafted, (b) no rules (plain commands +
   fingerprints library + warning), or (c) a rule combinator (`frozen`/`warnOnly`) as the general
   "rules over rules" form? And does `locks` ordering across referenced projects change the answer?
2. Brands: brand-neutral description + prefix rename, or import per brand with a mechanical
   per-brand delta in the description? Where do the trial project descriptions live if nothing is committed there?
3. C# sources: globs kept in description and lock, expanded at build (not hashed), or expanded lists?
4. Pack: what may differ between two "equal" nupkgs -- nuspec version only (assembly version fixed),
   or also assembly version attributes inside the dlls?
5. Fingerprints: one repo file or per kept folder; LF-normalised hashes; and lock format: bump with
   refusal of 0.2 locks, or read-compatible?
