# Wiki refresh: analysis

Branch `docs/wiki-refresh`, written 2026-10-27 against `origin/dev` at `ecfb126`. Input: the
GitHub wiki checkout (19 pages), the code (`src/core`, `src/dotnet`), the base docs in `docs/`,
`samples/*.fsx` and the repository log since the wiki's last edit. **Scope boundary:** nothing
about the hermetic product goes into the wiki yet. That means no `Xake.Hermetic.Dotnet`, locks,
`Import`, SBOM, restore from locks, `HERMETIC=on`, `Locks.*`, signing or pack from locks. Those
items are listed below only so they can be left out.

## 1. Executive summary

- **The wiki was last edited on 2026-09-05** (wiki commit `0acecab`), the day `v3.3.0` was tagged
  (`7f6a11d`, PR #16). It describes **Xake 3.3**. Since then, 14 releases have been tagged:
  3.4.0 (2026-10-01), 3.5.0, 3.6.0, 3.7.0, 3.8.0, 3.9.0, 3.9.1, 3.10.0, 3.10.1, 3.11.0, 3.12.0 and
  3.13.0 (2026-10-26). That is about 7.5 weeks of time but most of the package's recent changes.
  The 3.3 rewrite (2026-03 and 2026-09) was good, and most engine pages are still accurate. The
  gaps are concentrated in rule selection, variables, logging and the whole .NET tasks area.
- **Biggest gaps:**
  1. **Rule selection is now the opposite of what the wiki says.** Rules page: "when two rules
     match the same target **the last one declared wins**". Since 3.10.1 (#88, refactored in #89)
     the **most specific** rule wins. Ties log a warning. `ruleMatch LastDeclared` is the
     deprecated escape hatch, and `-d EXPLAIN_RULE=<target>` explains the choice
     (`src/core/ExecCore.fs:205-245`, `docs/overview.md` "Rule selection").
  2. **The .NET tasks pages describe a different product.** They still frame `csc`/`fsc` as
     "building for the full .NET Framework", say `targetfwk` "is effectively required", and say
     fsc cannot target net4x. Today:
     - the default target is the SDK's own .NET (3.5);
     - fsc compiles for net4x from any host (3.5);
     - `Csc.compile`/`Fsc.compile` replace `Csc settings`/`Fsc settings` (breaking, 3.4/3.5);
     - also new: `resolve`, `Csc.run`/`Fsc.run`, the compiler server (`CSC_SERVER`),
       `toolset`/`CSC_TOOLSET`, `CSC_JOBS`/`FSC_JOBS`/`throttle`, the declared
       `runtimeconfig.json` target, `NUGET_PACKAGES`/`NUGET_FETCH`, `NETCORE_REF_VERSION`,
       `FSHARP_CORE_VERSION`, `.resx` on any host for embedded resources (3.8), msbuild/NuGet
       diagnostics (3.13) and `csc/fsc { project }` from `xake.project.json` (3.9).
  3. **Secret variables (3.13, #96) are missing.** That covers `Var.secret`, `|> secret`,
     `|> secretHashed`, masking in every log, `-d` refused, and database format 0.6.
  4. **Two silent behaviour changes have no warning in the wiki.** First, since 3.10 a `need` of a
     rule target inside `withResource`/`requiring` **fails** (deadlock guard, `allowNeed`).
     Second, since 3.10.1 `rm { }`/`cp { }` accept many `dir`/`file`/`files` targets applied in
     order. Also, `filelog` is documented as "off unless used", but a `build.log` at `Chatty` is
     written by default.
- **Page count:** 4 wrong, 8 partly stale, 7 current (section 2). None is fully obsolete.
  However, `Tasks-|-Dotnet` and `Reference-|-Dotnet-Frameworks` need a rewrite, not edits.
- **The in-repo docs have defects of their own.** Fix these before deriving the wiki from
  them (section 5.6). For example: `implnotes.md` says database `0.5` (code: `0.6`);
  `dotnet-build.md` tells readers to run `-vv`, which does not exist; `release-notes.md` has no
  3.4–3.8, 3.11 or 3.12 sections and still labels tagged releases "unreleased".

## 2. Page by page

Status key:
- **current**: accurate for 3.13, small additions only.
- **partly stale**: accurate but missing features or details.
- **wrong**: contains statements that contradict the code.
- **obsolete**: the page should go.

| Page | Last wiki edit | Covers | Status | Superseded by (in repo) |
|---|---|---|---|---|
| `_Sidebar` | 2026-09-05 | navigation | partly stale | — (section 4) |
| `Home` | 2026-09-05 | pitch, one-package note, SDK 8 | partly stale | `docs/overview.md` |
| `Introduction` | 2026-09-05 | first scripts, running, rules, real-world example, filesets | current | `docs/overview.md`, `docs/cheatsheet.md` |
| `Reference-\|-Rules` | 2026-09-05 | rule forms, operators, patterns, teardown, old syntax | **wrong** | `docs/overview.md` (rule selection), `docs/cheatsheet.md` |
| `Reference-\|-Recipe` | 2026-09-05 | recipe CE, context, vars, need, filesets, errors | current | `docs/implnotes.md` (recipe behaviour) |
| `Reference-\|-Resources` | 2026-09-05 | `Resource`, `requiring`, `withResource`, `resize` | partly stale | `docs/delegated.md` §1, `docs/resources.md` (design record) |
| `Reference-\|-Delegated` | 2026-09-05 | delegated hook, `runDetached`, samples | current | `docs/delegated.md` |
| `Reference-\|-Filesets` | 2026-09-05 | builder, operators, masks | current | — (no in-repo page; `docs/implnotes.md` §Filesets is thinner) |
| `Reference-\|-Variables` | 2026-09-05 | typed vars, resolution, `--help` | partly stale | `docs/tasks.md` §Script variables, §Secret variables |
| `Reference-\|-Engine` | 2026-09-05 | `xakeEngine`, `Demand`, `StopAsync`, teardown | current | — (code: `src/core/XakeScriptBuilder.fs:151-229`) |
| `Reference-\|-Settings` | 2026-09-05 | script operations, verbosity | **wrong** | code `src/core/XakeScriptBuilder.fs`, `src/core/XakeScript.fs:67-91` |
| `Reference-\|-Command-Line` | 2026-09-05 | flags, target order, `-d`, database | partly stale | code `src/core/Program.fs` |
| `Reference-\|-Dotnet-Frameworks` | 2026-09-05 | toolchain discovery, providers, matrix | **wrong** | `docs/dotnet-build.md` |
| `Tasks-\|-Inner-functions` | 2026-09-05 | recipe helpers | current | `docs/tasks.md` §Inner recipe helpers, `docs/cheatsheet.md` |
| `Tasks-\|-Shell` | 2026-09-05 | `sh`/`shellCmd`/`shell`, capture | partly stale | `docs/tasks.md` §Shell, `docs/captureOutput.md` |
| `Tasks-\|-Cp` | 2026-09-05 | `cp`, `copyFile`, `copyFrom` | partly stale | `docs/tasks.md` §Copy |
| `Tasks-\|-Rm` | 2026-09-05 | `rm` | partly stale | `docs/tasks.md` §Remove |
| `Tasks-\|-Misc-file-tasks` | 2026-09-05 | `writeText`, `read*`, copy helpers | current | — |
| `Tasks-\|-Dotnet` | 2026-09-05 | csc, fsc, msbuild, resgen, variables | **wrong** | `docs/tasks.md` §.NET tasks, `docs/csc-syntax.md`, `docs/csc-server.md`, `docs/dotnet-build.md` |

All pages carry the same date because the 3.3 commit `0acecab` touched every page. Earlier
history goes back to 2017 (the wiki's own log).

### 2.1 Specific wrong or missing statements

**`Reference-|-Rules`**
- Wrong: "Rules accumulate in reverse declaration order, so when two rules match the same target
  **the last one declared wins**" (line 31-33), and the example "catch-all first... so that this
  concrete rule wins" (lines 35-43).
  - Now: most specific wins. Compare the count of literal characters, then the number of `**`,
    then the number and tightness of wildcards (`?` is tighter than `*`).
  - Different patterns that tie fall back to last-declared, with a `Warning`.
  - An identical pattern string is still a silent override.
  - Sources: `src/core/ExecCore.fs:205-245`, `src/core/Path.fs:20-40`, `docs/overview.md`
    "Rule selection", `docs/release-notes.md` 3.10.1.
- Missing: `ruleMatch LastDeclared` / `MostSpecific` (`src/core/XakeScriptBuilder.fs:110`,
  `src/core/Types.fs:124-129`, deprecated) and `-d EXPLAIN_RULE=<target>` (`ExecCore.fs:208`).
- Missing trap: the tie warning is logged at `Warning`. The default console level `Normal` does
  not show `Warning` (`src/core/Logging.fs:44`), so it is visible only in `build.log` (Chatty) or
  with `-ll Loud`. `docs/overview.md` says it "logs a warning ... so the ambiguity is visible",
  which is true only for the file log.
- Missing: patterns may contain `..` (`"../sibling/(x:*).dll"`, fixed 3.4, #18,
  `src/core/Path.fs` `normalizeLiteralPrefix`, `docs/session.md` trap).
- Missing: a target requested twice in one run is built once (#17, `docs/session.md`).
- Still correct: "`**` in rule patterns does not match an empty directory segment". `**` becomes
  `(.*)` between two slashes (`src/core/Path.fs:226`).

**`Reference-|-Settings`**
- Wrong: "File logging is off unless this operation is used" (line 70). `ExecOptions.Default` has
  `FileLog = "build.log"`, `FileLogLevel = Chatty` (`src/core/XakeScript.fs:74-75`). The file
  logger is added unless the name is empty or the level is `Silent` (`src/core/ScriptRunner.fs:9-12`).
  Every `xakeScript` run writes `build.log` in the current directory. Only `xakeEngine` turns it off.
- Missing operations: `ruleMatch` (`XakeScriptBuilder.fs:110`) and `engine` (alias of `start`,
  `:156`). `varschema` lives in `src/core/Vars.fs:155` and is already documented.
- Missing: the logger masks secrets (`MaskingLogger`, `ScriptRunner.fs:13`). The progress line
  is on by default (`Progress = true`, `XakeScript.fs:83`) and shows Braille dots since 3.10.1.

**`Reference-|-Command-Line`**
- Wrong: "The names are case-sensitive, and an unknown level aborts the run" (line 53-54). The
  first half is true. However, an unknown level makes `parseVerbosity` throw, and `foldFunction`
  catches it, prints "Failed to parse 'console verbosity' due to ...", and keeps going with the
  old level (`src/core/Program.fs:113-121`, `src/core/Logging.fs:385`).
- Missing: `-d EXPLAIN_RULE=<target>`. A `-d` for a `Var.secret` is refused, and a `-d` for an
  argument marked `secret` is warned about (`ScriptRunner.fs` `checkSecretVars`). Secrets show
  as "(secret)" in `--help`, with no default. Options are case-insensitive (`ToLowerInvariant`,
  `Program.fs:19`).
- Missing in "Build database": format `0.6` since 3.13. Versions 0.4/0.5 are upgraded on open,
  and an older Xake that meets 0.6 recreates the file (`src/core/Database.fs:10-15`, `docs/tasks.md`).
- Still correct: the flag table (matches `Program.fs:21-67`), `;`/`|` parallel groups, `want`/`wantOverride`.

**`Reference-|-Variables`**
- Missing: secret variables. That covers `Var.secret<'t>(envVar = ...)`, `|> secret`,
  `|> secretHashed` (`src/core/Vars.fs:83-96`), presence or HMAC digest in `.xake`, masking of
  values of 4+ characters as `***` in every logger (including `--dump`/`--dryrun`), and "the name
  decides" (a plain `getEnv` of a declared secret name is treated as secret). Source:
  `docs/tasks.md` §Secret variables.
- Stale: "`Var.env` ... Useful for secrets that must not appear on the command line" (line 77).
  Now point to `Var.secret`, because a `Var.env` value is still recorded in clear text in `.xake`.
- Stale help sample: secrets show as "(secret)" with no default.

**`Reference-|-Resources`**
- Missing (breaking in 3.10): inside `withResource`, and anywhere in a `requiring` rule, a `need`/
  `needFiles`/`dependsOn` of a **rule target** fails, with an error naming the resource and both
  targets. Plain files without a rule are allowed. The fix is "need first, then acquire".
  `allowNeed body` lifts the check (`src/core/ScriptFuncs.fs:40-60`, `docs/delegated.md` §1,
  `docs/resources.md`).
- Missing: for compilers, do not wrap the compile in `withResource`. Use `CSC_JOBS`/`FSC_JOBS` or
  `throttle` (`docs/csc-server.md`).
- API table: add `allowNeed`.

**`Reference-|-Delegated`**
- Current. One addition: the "Composing with `requiring`" example (lines 178-186) is now under the
  need guard above. Mention it there.

**`Reference-|-Dotnet-Frameworks`**
- Wrong: the support matrix says "`fsc` → net4x | **no**" (line 126), and the explanation (lines
  131-133) says "`fsc` cannot target the full framework through the SDK". Since 3.5 the
  `FSharp.Core` package (`FSHARP_CORE_VERSION`, default 8.0.100) and a `netstandard.dll` facade are
  implicit. Sources: `docs/dotnet-build.md` "Support matrix" and "Known limitations",
  `docs/session.md`.
- Wrong: the troubleshooting row "`CS0518 ... targetfwk was not set, so no framework references
  were passed`" (line 143). Since 3.5 there is a default: `targetfwk` > `NETFX-TARGET` > the SDK's
  own .NET (`DotNetFwk.sdkFramework`).
- Wrong framing: the "Target framework" row says `net462`, `net40`, mono 4.5 only (line 15), and
  "The discovery below exists for one purpose: compiling **.NET Framework** assemblies". `netN.0`
  and `netstandard2.x` are now first-class targets. They compile against the SDK's targeting pack,
  or `Microsoft.NETCore.App.Ref` from NuGet, pinned by `NETCORE_REF_VERSION`.
- Stale: the row "`resgen`, `.resx` embedded in `csc`/`fsc` | **no**". Embedded `.resx` compiles on
  any host since 3.8: `ResxFormat`, through `Resx.refresh` in the shared runner
  (`src/dotnet/CompilerRunner.fs:280`). Only the standalone `resgen` task is still
  full-framework-only (`src/dotnet/Dotnet.resgen.fs:44`). `docs/dotnet-build.md` has the same stale
  row, so fix it in both places.
- Stale: "Compiler diagnostics are logged at `Warning`/`Error` and everything else at `Verbose`"
  (line 145). Since 3.13, msbuild/NuGet `<origin> : error CODE` lines also count as `Error`, and
  NuGet warnings as `Info`. A repeated warning is logged once, and a failure repeats the first 5
  distinct errors (`docs/dotnet-build.md` "Troubleshooting", `Tool.DiagnosticLog`).
- Missing: where packages go (`NUGET_PACKAGES` script var > env > `~/.nuget/packages`),
  `NUGET_FETCH=off`, `global.json` selecting the SDK, and the platform moniker `net8.0-windows`
  being accepted (3.8).

**`Tasks-|-Dotnet`**
- Wrong: the title and opening say "Dotnet tasks — building for the **full .NET Framework**".
  The default target is the SDK's own .NET (`docs/tasks.md` §csc).
- Wrong: "**`targetfwk` is effectively required.** ... Without it the compiler is invoked with no
  framework references at all" (lines 35-38). It is optional since 3.5.
- Wrong: "`fsc` cannot target the full framework through the SDK ... Reference an `FSharp.Core.dll`
  of your own" (lines 71-73). See Dotnet-Frameworks above.
- Wrong: the `fsc` example passes `ref !!"bin/FSharp.Core.dll"` and `grefs` for a net4x target
  with no `targetfwk`. Today it would target `net10.0`.
- Missing (each one landed after 3.3):
  - `Csc.compile`/`Fsc.compile`, the record syntax. `Csc settings` (3.3) and `Fsc settings` (3.4)
    are **gone**.
  - `resolve`, `Csc.ofSettings`, `Csc.run`/`RunOptions`, `Fsc.run`/`FscRunOptions`.
  - `noserver`/`keepalive`/`CSC_SERVER`/`XAKE_CSC_SERVER`.
  - `toolset`/`CSC_TOOLSET`.
  - `throttle`, `CSC_JOBS`/`FSC_JOBS` (3.10).
  - The `runtimeconfig.json` declared as a second target.
  - `doc` (fsc).
  - `--nocopyfsharpcore`.
  - `define` emitting one `--define:` per symbol for fsc.
  - `project "xake.project.json"` (3.9, core; see section 5.3).
  - The script-variable table: `NUGET_PACKAGES`, `NUGET_FETCH`, `NETCORE_REF_VERSION`,
    `NETSTANDARD_LIBRARY_VERSION`, `NETSTANDARD_LIBRARY_REF_VERSION`, `FSHARP_CORE_VERSION`.
  - SDK paths with spaces are quoted by Xake. A script's own `shell { args }` stays raw
    (`docs/session.md` trap).
- Still correct: the `MsbVerbosity` name collision (`src/dotnet/DotnetTasks.fs:18`), the msbuild
  operations and `resourceset`.

**`Tasks-|-Shell`**
- Wrong: the "Error handling" sample `do! shell {cmd "make"} |> FailWhen Not0 "make failed"` (line
  145). `FailWhen` passes the `int` through. The Recipe page itself says to bind it with `let!` or
  `|> Recipe.Ignore`, so this `do!` does not compile.
- Duplicate row: `env` appears twice in the operations table (lines 57 and 61).
- Missing: `shell` does not quote `args` (joined with a space). Paths with spaces need quoting by
  the script (`docs/session.md` trap; `Tool.quoteArgument` exists in `Xake.Dotnet`).

**`Tasks-|-Rm`, `Tasks-|-Cp`**
- Missing (3.10.1, #82): `dir`, `file` and `files` may repeat and combine in one block, applied in
  declaration order (`src/core/Tasks/Rm.fs:87-89`, `src/core/Tasks/Cp.fs:147-149`). Before 3.10.1,
  each op overwrote the previous one. `rm` skips paths already removed.
- Cp, stale: "`flatten` is automatically enabled when using `file`" (line 33). It is now per source:
  a `file` source is always flattened, while `dir`/`files` keep their relative paths.
- Breaking for record users: `RmArgs = { targets: RmTarget list; verbose }`, and `CpArgs.sources`
  replaces `dir`/`file`/`files`.

**`Home`**
- Stale: "the tasks for the full .NET Framework (`Xake.Dotnet` — csc, fsc, msbuild, resgen)". They
  are .NET compiler tasks, targeting the SDK's .NET by default.
- Missing: a version line ("documents Xake 3.13") and a link to release notes.

**`Introduction`, `Recipe`, `Filesets`, `Engine`, `Inner-functions`, `Misc-file-tasks`**
- Introduction: the "Next steps" label "building .NET Framework assemblies with csc, fsc, msbuild"
  needs the new framing.
- Inner-functions: `getRuleMatch` works for phony rules too and returns `""` when the group is
  absent (`docs/cheatsheet.md`). Add `allowNeed` there or on Resources.
- Engine: `engine` is an alias of `start` (`XakeScriptBuilder.fs:156`). The `IAsyncDisposable`
  note is still right (`:225-228`, `#if` only).
- Recipe, Filesets, Misc-file-tasks: no change found. Every function they name exists in
  `src/core/ScriptFuncs.fs`, `src/core/Tasks/misc.fs`, `src/core/Tasks/Cp.fs` and
  `src/core/RecipeFunctions.fs`.

## 3. Missing pages

| Feature | Since | Source to derive from | Suggested page |
|---|---|---|---|
| Rule selection: specificity, ties, `ruleMatch`, `EXPLAIN_RULE` | 3.10.1 | `docs/overview.md` §Rule selection, `src/core/ExecCore.fs` | `Reference-\|-Rule-Selection` (or a long section of Rules) |
| Secret variables | 3.13 | `docs/tasks.md` §Secret variables, `docs/cheatsheet.md` | `Reference-\|-Secrets` |
| Logging and diagnostics: levels, default `build.log`, masking, progress line, msbuild/NuGet/compiler classification, error summary | 3.3–3.13 | `docs/dotnet-build.md` §Troubleshooting, `src/core/Logging.fs`, `docs/release-notes.md` 3.13 | `Reference-\|-Logging` |
| Build database and incremental rebuilds: rebuild conditions, `ArtifactDep2` change stamps, format 0.6, `--resetdb`, sharing-violation retry | 3.4–3.13 | `docs/implnotes.md` (after fixing it), `src/core/Database.fs` | `Reference-\|-Build-Database` |
| csc task in full | 3.4–3.13 | `docs/tasks.md` §csc, `docs/csc-syntax.md` (composed mode, behaviour notes) | `Tasks-\|-Csc` (revives the 2017 page name) |
| fsc task in full | 3.5–3.13 | `docs/tasks.md` §fsc, `docs/session.md` (fsc for .NET Framework) | `Tasks-\|-Fsc` |
| msbuild, resourceset, resgen | 3.3–3.13 | `docs/tasks.md` §msbuild/§resourceset, `docs/csc-syntax.md` §Not yet (ResxFormat) | `Tasks-\|-MSBuild-and-Resources` |
| Resolved compilations: `resolve`, `Csc`/`Fsc` records, `Csc.run`/`Fsc.run`, `RunOptions`, `Csc.ofArgs` | 3.4–3.5 | `docs/tasks.md` §Resolving, `docs/csc-syntax.md` §The runner | `Advanced-\|-Compiler-Runner` |
| Compiler server and compile concurrency: `CSC_SERVER`, `noserver`, `keepalive`, `CSC_JOBS`/`FSC_JOBS`, `throttle` | 3.4, 3.10 | `docs/csc-server.md` (user sections only) | `Advanced-\|-Compiler-Server-and-Jobs` |
| Script variables of the .NET tasks, and where packages go | 3.4–3.8 | `docs/tasks.md` §Script variables, §Where packages go; `docs/dotnet-build.md` | `Reference-\|-Dotnet-Variables` (or a section of Dotnet-Frameworks) |
| Project descriptions: `xake.project.json`, `csc/fsc { project }` | 3.9 | `docs/description.md`, `docs/csc-syntax.md` §From a project description (core) | `Tasks-\|-Project-Descriptions` (**core, without the lock story**, section 5.3) |
| Fingerprints of committed generated files | 3.6 | `docs/fingerprints.md` | `Advanced-\|-Fingerprints` (core, but the doc's framing is locks: rewrite the intro) |
| Release notes / What's new | — | `docs/release-notes.md` (hermetic entries filtered) | `Release-Notes` |
| Migration from 3.3 (breaking changes) | 3.4–3.13 | `docs/release-notes.md`, `docs/tasks.md` (Csc/Fsc.compile), sections 2.1 Rm/Cp, Resources, Variables | `Migration-\|-From-3.3` |
| Samples index | — | `samples/*.fsx` (exclude `samples/hermetic/`, `samples/book/hw.lock`) | `Samples` |

Not to be documented yet:
- **Typed targets / `Xake.Typed` (#91, #92) and reserved pattern characters with backslash
  escape (#90)** are on `feature/typed-matches` only. That branch is frozen; they are not on `dev`.
- Hermetic items (the boundary is in section 5.3).

## 4. Proposed structure

### 4.1 `_Sidebar` tree

```
About (Home)                       -- one paragraph, version line, links
Getting started
  Introduction                     -- rewrite lightly
  Cheatsheet                       -- NEW, from docs/cheatsheet.md (hermetic block removed)
  Samples                          -- NEW
Reference
  Rules                            -- rewrite the selection section
  Rule selection                   -- NEW
  Recipe
  Filesets
  Variables                        -- add secrets summary, link
  Secrets                          -- NEW
  Settings                         -- fix filelog, add ruleMatch/engine
  Command line                     -- small fixes
  Logging                          -- NEW
  Build database                   -- NEW
Tasks
  Built-in functions (Inner-functions)
  Shell
  Cp / Rm                          -- add many-targets
  Misc file tasks
  .NET tasks overview (Dotnet)     -- REWRITE as a short hub
  Csc                              -- NEW
  Fsc                              -- NEW
  MSBuild and resources            -- NEW
  Project descriptions             -- NEW (core only)
Advanced
  Resources                        -- add the need guard
  Delegated
  Engine
  Dotnet frameworks                -- REWRITE (toolchain discovery)
  Dotnet variables                 -- NEW (or merged into Dotnet frameworks)
  Compiler runner                  -- NEW
  Compiler server and jobs         -- NEW
  Fingerprints                     -- NEW, optional
What's new
  Release notes                    -- NEW
  Migration from 3.3               -- NEW
```

Changes:
- **Rewrite:** `Tasks-|-Dotnet` (becomes a hub), `Reference-|-Dotnet-Frameworks`, the selection part
  of `Reference-|-Rules`.
- **Move:** Resources, Delegated and Engine move to Advanced. Their names stay the same, so the
  links do not break.
- **Merge:** none required. `Tasks-|-Misc-file-tasks` and `Tasks-|-Cp` both document
  `copyFile`/`copyFrom`. Keep them on Cp and link from Misc.
- **Retire:** nothing yet. Cut the `Tasks-|-Dotnet` csc/fsc sections once the Csc and Fsc pages exist.

### 4.2 In-repo doc to wiki page

| In-repo doc | Wiki page(s) | Copy? |
|---|---|---|
| `docs/overview.md` | Home, Introduction, Rule selection | yes. Drop its "Useful links" to the wiki (circular) |
| `docs/cheatsheet.md` | Cheatsheet | yes, **without** lines 123-132 (locks) |
| `docs/tasks.md` | Shell, Cp, Rm, Csc, Fsc, MSBuild and resources, Variables, Secrets, Dotnet variables | yes, after scrubbing: lines 165 (`HERMETIC=on` clause), 206-212 (`lock`, hermetic `project/config/packages`), 296-297 ("lock entry recorded"), 366-367 (`HERMETIC=on`), 439-448 (`Project.import`, `Lock.compile`), 499 (`HERMETIC` row), 524-525 and 533 (`lock`, `nofetch`) |
| `docs/captureOutput.md` | Shell §captureOutput | yes |
| `docs/dotnet-build.md` | Dotnet frameworks, Dotnet variables, Logging | yes, without §Hermetic mode (313-385) and the `HERMETIC` row. Without the internals (`FrameworkInfo`, `pexecSync`, the "History" commit list): **maintainers-only** |
| `docs/csc-syntax.md` | Csc, Compiler runner, Project descriptions | partly: composed mode, outputs, reference assemblies, §From a project description (Xake 3.9), behaviour notes. **Not** lines 1-14 (lock intro), §From a project description in Xake.Hermetic.Dotnet (200-315), lock parts of §Compiler sources |
| `docs/csc-server.md` | Compiler server and jobs | user sections only (What changed, CSC_JOBS, When the switch is not added, Lifetime). The rest is a design note with lock and verify content: **maintainers-only** |
| `docs/description.md` | Project descriptions | partly: Example, Fields, The block and the description, Format. **Not** §Status (names the hermetic package's Import, locks, SBOM, signing) or the version notes |
| `docs/fingerprints.md` | Fingerprints | partly: The file, Functions, Wiring. Rewrite the intro without "locks written by `locks`" |
| `docs/delegated.md` | Resources, Delegated | yes (the wiki pages already derive from it) |
| `docs/resources.md` | Resources (summary only) | **maintainers-only** design record; link the summary, do not copy |
| `docs/implnotes.md` | Build database, Recipe | yes, after fixing (section 5.6) |
| `docs/release-notes.md` | Release notes, Migration | filtered: drop the Xake.Hermetic.Dotnet bullets (3.10.0 last bullet), the `Locks` mentions, and the build-script-only bullets |
| `docs/session.md` | traps only, folded into pages (`..` patterns, shell quoting, twice-requested target, `{ () }` on F# 8) | **maintainers-only**; never copy |
| `docs/devprocess.md`, `docs/roadmap.md`, `docs/ideas.md`, `docs/index.md` | — | **maintainers-only** |
| `docs/hermetic*`, `docs/hermetic/`, `docs/features/`, `docs/schemas/` | — | **excluded** (the schema may be linked from Project descriptions, see section 5.3) |
| `samples/*.fsx` | Samples, examples | yes, except `samples/hermetic/` and `samples/book/` (`hw.lock`) |

## 5. Risks and open questions for the owner

1. **Derived copy or separate audience?** Option A: the wiki becomes a generated copy of a
   `docs/user/` subset, with a script that strips lines marked as hermetic. This means one source
   of truth, but `docs/` needs a clean user/maintainer split first. Option B: the wiki is a
   hand-written user guide that links to `docs/` for depth. That is cheaper now and drifts again
   (it already drifted once in 7 weeks). Recommendation: B now, with section 4.2 as the checklist,
   and A after the split. Either way, the wiki checkout is a separate repository, so the pages
   cannot be updated by a PR to `dev`.
2. **Versioning note on Home.** Proposal: "This wiki documents Xake 3.13 (`#r "nuget: Xake"`).
   Pages marked *since 3.x* describe later features." This needs a decision on whether to keep
   per-feature "Available since" badges, as Resources/Delegated have now.
3. **The hermetic boundary around `csc { project }`.**
   - `project "xake.project.json"` is core (3.9, `Xake.Dotnet.Description`, `ProjectPlan.fs`) and
     works with no other package.
   - But the messages it gives for a rejected description name Xake.Hermetic.Dotnet
     (`docs/csc-syntax.md` "The messages name Xake.Hermetic.Dotnet"). Descriptions are written
     by the hermetic `Import` (`docs/description.md` line 6).
   - Proposal: document the page as "hand-written descriptions". List what is rejected (`packages`,
     unresolved `projectRefs`) without naming the package, and do not mention import.
   - Decide: (a) document it now this way, (b) leave it out until `Import` moves to the core
     (`docs/roadmap.md` "Import open"), or (c) change the error text first.
   - Same question for `Fingerprints` (core, but motivated by committed locks and descriptions).
     Also for `NUGET_FETCH`/`NETCORE_REF_VERSION` (core variables whose in-repo docs explain them
     with lock paths) and for the `Csc.run` hash check (core, but "hashed dependencies" exist to
     serve locks). Proposal: document `Csc.run` as "runs a resolved compilation", and treat the
     hash check as an advanced footnote or skip it.
   - `HermeticMode.fs` lives in `src/dotnet`, so `HERMETIC` is technically a core script variable.
     It stays out by the owner's rule.
4. **Breaking-change visibility.** Six breaking changes since 3.3 affect existing scripts:
   - `Csc settings` and `Fsc settings` were removed.
   - The need guard (3.10).
   - The rule-selection flip (3.10.1).
   - The `RmArgs`/`CpArgs` record shape.
   - The `CscSettingsType.Project` field and the `ExecContext.HeldResources` field (record
     construction only).
   - The `OptionalVar`/`RequiredVar.Secret` field.

   Is a `Migration-|-From-3.3` page wanted, or a "Breaking" marker inside Release notes only?
5. **`ruleMatch LastDeclared` is labelled a one-release migration aid.** If it will be removed in
   3.14, the wiki should say so with a version. When?
6. **Defects in the in-repo docs to fix before any derivation** (a separate small PR, or part of
   this branch?):
   - `docs/implnotes.md:36` says "now `0.5`"; the code says `0.6` (`src/core/Database.fs:10`).
   - `docs/implnotes.md:94` says CLI teardown "may require explicit demand". In fact it runs
     automatically (`src/core/ScriptRunner.fs:209`).
   - `docs/dotnet-build.md` §Troubleshooting says "run with `-vv`". There is no such option; the
     right command is `-ll Diag` (`src/core/Program.fs`).
   - In `docs/dotnet-build.md` §Support matrix, the `.resx` row is stale (embedded `.resx` works on
     any host since 3.8).
   - `docs/release-notes.md` has no 3.4–3.8, 3.11 or 3.12 sections, and labels 3.10.1/3.13.0
     "unreleased" although `v3.10.1` and `v3.13.0` are tagged. 3.11/3.12 entries are folded into
     3.10.1.
   - `docs/index.md` still names Xake 3.6.0.24 and hermetic 0.2.
   - `docs/overview.md` says the tie warning makes the ambiguity "visible", but it is invisible at
     the default console level.
   - The header comment of `samples/gettingstarted.fsx` still says `targetfwk` "is what wires up
     the reference assemblies -- without it the compiler is invoked with no framework references
     at all".
7. **`readme.md` (also the NuGet readme) links to the wiki** as "the documentation" (lines 128-129,
   186). Until the refresh lands, readers of nuget.org get 3.3 docs. Should the readme temporarily
   point to `docs/overview.md` and `docs/cheatsheet.md` on GitHub?
8. **Who publishes.** The wiki is a separate repository (`Xake.wiki`), and this session is
   read-only on it. Pushing it is the owner's step, or that of an agent explicitly given write
   access.

## 6. Effort and order

Effort: S = under 1 h (edits), M = 1–3 h (section rewrite or new page from one doc),
L = 3 h or more (new page from several docs, or scrubbing hermetic content).

| # | Page | Action | Effort |
|---|---|---|---|
| 1 | `Reference-\|-Rules` + new `Reference-\|-Rule-Selection` | replace "last declared wins", add specificity, ties, `ruleMatch`, `EXPLAIN_RULE`, `..` | M |
| 2 | `Reference-\|-Settings` | fix `filelog` default, add `ruleMatch`, `engine`, progress default | S |
| 3 | `Reference-\|-Resources` (+ Delegated note) | need guard, `allowNeed`, compilers via `CSC_JOBS` | S |
| 4 | `Tasks-\|-Rm`, `Tasks-\|-Cp` | many targets, flatten per source | S |
| 5 | `Reference-\|-Variables` + new `Reference-\|-Secrets` | secrets | M |
| 6 | `Reference-\|-Command-Line` | unknown-level line, `EXPLAIN_RULE`, secrets in `--help`, db 0.6 | S |
| 7 | `Tasks-\|-Shell` | `FailWhen` sample, duplicate `env`, quoting note | S |
| 8 | `Tasks-\|-Dotnet` (hub) + new `Tasks-\|-Csc`, `Tasks-\|-Fsc`, `Tasks-\|-MSBuild-and-Resources` | rewrite from `tasks.md`/`csc-syntax.md`, scrubbed | L |
| 9 | `Reference-\|-Dotnet-Frameworks` + `Reference-\|-Dotnet-Variables` | rewrite from `dotnet-build.md`, without hermetic mode | L |
| 10 | new `Advanced-\|-Compiler-Server-and-Jobs` | from `csc-server.md` user sections | M |
| 11 | new `Advanced-\|-Compiler-Runner` | `resolve`, `Csc.run`, records | M |
| 12 | new `Reference-\|-Logging`, `Reference-\|-Build-Database` | after fixing `implnotes.md` | M |
| 13 | `Home`, `Introduction`, `_Sidebar` | version line, framing, new tree | S |
| 14 | new `Release-Notes`, `Migration-\|-From-3.3` | needs the missing 3.4–3.8 notes reconstructed from the repository log | L |
| 15 | new `Tasks-\|-Project-Descriptions`, `Advanced-\|-Fingerprints` | only after question 5.3 is answered | M each |
| 16 | new `Getting-started-\|-Cheatsheet`, `Samples` | copy, scrubbed | S |

Suggested order: 1–7 first. They are small, they fix statements that mislead users today, and
they could ship as one wiki push. Then 13 (so Home states the version), then 8–12 (the .NET
area), then 14, then 15–16 once the open questions are answered. Before step 12 and step 14, fix
the in-repo defects (5.6) so the wiki is derived from correct sources. Total: about 3–4 working
days.
