# Review of docs/hermetic-architecture.md: answers (2026-10-12)

Code base: worktree `pr/hermetic-0.3` at `b10c0cc`. Paths are relative to it.

## Answers

1. **Two import routes exist; only the 0.3 route goes through the composed task.**
   - `Project.import` (0.1/0.2) writes a **lock** directly. It runs a design-time build, factors
     msbuild's command line with `Csc.ofArgs`/`Fsc.ofArgs` (`src/hermetic/Project.fs:286,296`) and
     writes the lock (`Project.fs:579`). It does not call `csc {}` or `Csc.ofSettings`.
   - `Import.descriptions` (0.3) writes a **description**, not a lock (`src/hermetic/Import.fs:301-372`).
     msbuild only evaluates; no targets run. `Locks.generate` then composes the lock through the
     task: `csc { targetfwk; out; ref; project; config; packages; resolve }` (`src/hermetic/Locks.fs:200,205`).
     That calls `Described.csc`, which calls `Csc.ofSettings` (`src/hermetic/Described.fs:421`).
   - Hermetic import, the 0.3 route: **yes, for C#.** `Csc.ofSettings` reads `CSC_TOOLSET` with
     `getVar` when the block has no `toolset` (`src/dotnet/Csc.fs:670-686`). So `-d CSC_TOOLSET=<v>`
     (or `var "CSC_TOOLSET"`) on `update-locks` records the package compiler. The script cannot
     pass `toolset` itself, because `Locks.generate` builds the block. The description has no
     toolset field. F#: no. `FscSettingsType` has no toolset, and no package carries fsc. The SDK
     is a `dotnet-sdk` prerequisite.
   - Hermetic import, the `Project.import` route: **not by a toolset option.** `ImportOptions` has
     no toolset field (`Project.fs:23-49`), and the import does not read `CSC_TOOLSET`. The
     compiler is the one msbuild wires up (`Project.fs:191-221`): the SDK's, or the package's if
     the project references `Microsoft.Net.Compilers.Toolset` and sets `RoslynCompilerType=Toolset`
     (`src/hermetic.tests/ToolsetImportTests.fs:13-37`). Under `HERMETIC=on` an SDK compiler is
     accepted only as a prerequisite under an exact pin (message 11, `docs/hermetic/hermetic-mode.md:126`).
     Missing: an `ImportOptions` toolset field (or a `CSC_TOOLSET` read) that restores the package
     and replaces `Dependencies.Compiler` after `parseImport`. Untested idea: `ImportOptions.Properties`
     with `CscToolPath`/`CscToolExe`. `cscCompilerPath` honours them (`Project.fs:211-219`), but
     the package must already be in the cache.
2. **Section 6 is current.** No part of it comes from removed code. Every claim matches the code:
   - `CompilerServer`, `fromEnvironment` and `resolve`: `src/dotnet/Csc.fs:18-62`.
   - `serverArgs` (the conditions in which the switch is not added): `Csc.fs:176-188`.
   - The switches go ahead of `/noconfig` and the rsp: `src/dotnet/CompilerRunner.fs:163,179`.
   - `csc { lock }` uses `Csc.runOptions`: `src/hermetic/Lock.fs:1277`.
   - `Lock.build` and `Lock.compile` call `resolve None`: `Lock.fs:1040,1233`.
   - `docs/csc-server.md` has no `intermediate`/`unbuilt`/`produced`/`mapped` logic.
   - Two omissions, added to the doc: `Locks.compileAs` also calls `CompilerServer.resolve None`
     (`Locks.fs:360`), and fsc has no server (`CompilerRunner.fs:43-45`).
3. **CI.**
   - Detection: `Lock.underCi` (`Lock.fs:729-741`). It reads the script variable `CI` first (`on`/`off`,
     anything else fails), then the environment variable `CI`. A script can use it as
     `let! ci = Lock.underCi`, or read `getEnv "CI"` itself. Nothing reads `GITHUB_ACTIONS`.
   - Strict semantics of `Lock.buildWith` (`Lock.fs:1181-1227`), and so of `Lock.build`,
     `csc/fsc { lock }` and `csc { project; lock }` (`Described.buildCsc`, `Described.fs:442-466`):
     - A missing lock is recorded. Under CI it fails, even with `FailOnError = false`.
     - A lock that differs fails with the diff. **The lock is never rewritten on a mismatch, on
       CI or anywhere else.**
     - A matching lock: the recorded entry is compiled.
   - Gap 1, the "override" (`nofailonerror`, `FailOnError = false`): it turns the mismatch into a
     traced error. Then `buildWith` compiles the resolved compilation (`Lock.fs:1218-1226`), and
     `buildCsc` compiles the recorded entry. Under CI this is **not** disabled.
   - Gap 2: `Locks.generate`, `Import.descriptions` and `Project.import` do not read `CI`. Each
     writes when it runs.
   - Gap 3: `Locks.compileAs` compiles the committed lock as is. It does not compare the lock with
     the description. Staleness is only what `Fingerprints.warn`/`check` report
     (`src/core/Fingerprints.fs:204,212`), and neither function is keyed on CI.
   - `HERMETIC=on` and `NUGET_FETCH` are independent of `CI` (`docs/hermetic/hermetic-mode.md:21,137-141`).
   - Today, a script gets "on CI: never rewrite a lock, fail on mismatch" when it does all of this:
     - It relies on `CI` (the runner sets it) for missing locks.
     - It has no `nofailonerror` in locked blocks.
     - CI never runs `import`/`update-locks`.
     - CI runs `Fingerprints.check` before `build`, or `build` chooses `check` when `Lock.underCi`.
     - Optional safety net: `git diff --exit-code` on locks and descriptions after the build.
   - The design matches this: import and lock generation never run on CI
     (`brief.md` §2 "Import uses msbuild; CI never runs it"; `session.md:1076-1077`), and "whether
     CI runs `check` is the user's call" (`brief.md:64-65`). `LOCK_RECORD_MISSING`/`RecordMissing`
     (#30, `session.md:976`) no longer exist in the code.
   - The doc did not explain this. Added: section 2, "Lock strictness and CI". The gaps are
     marked as a proposal.
4. **Still true, but only for phony rules.** `Csc.ofSettings` and `Fsc.ofSettings` take the output
   from the rule's file targets when `Out` is not set (`Csc.fs:564-571`, `Fsc.fs:298-302`). The first
   target that is not a `.runtimeconfig.json` is used, else `getTargetFile`. This includes
   `resolve`, `lock` and `project` (`Described.fs:290-301`). In a phony rule `getTargetFile` fails
   ("not available for phony actions", `src/core/ScriptFuncs.fs:150-156`). So in a file rule `out`
   is optional (the guide's example sets it redundantly), and outside one it is required.
   `Locks.compileAs` (#49) does the same for a lock entry: the lock has no `-o`, so it takes
   `getTargetFile` (`Locks.fs:307`) and inserts `/out:`/`--out:` (`outputAt`). The sentence is in
   `docs/hermetic-guide.md:129` and `docs/csc-syntax.md:33`. Both now have the note. It is not in
   the architecture doc.
5. **"`Lock.build` gates the compilation" means `buildWith`'s three-way check** (`Lock.fs:1181-1227`).
   It compares the resolved compilation with the lock entry (`diffText`; it compares hashes only
   when both sides have one):
   - Missing: record it (under CI: fail).
   - Different: fail with the diff.
   - Equal: compile the *recorded* entry through `compileEntry`. That step checks the
     prerequisites, the `HERMETIC=on` replay gate, the restore, the compiler and
     `$(SourceRevisionId)`. Then `Csc.run`/`Fsc.run` checks the recorded hashes against the files
     on disk.

   It does not mean the `HERMETIC` gate alone. The guide sentence and the architecture doc
   (sections 3 and 8) now say this plainly.

## Other findings (not edited)

- `docs/fingerprints.md` "Wiring in a script" uses `Description.import` and `Lock.needAll`. The
  real names are `Import.descriptions` and `Locks.needAll`.
- The roadmap item `B6: Fsc through the shared runner (planned after 3.4)` is done (3.5 / 0.2). It
  stays under "Xake.Dotnet" as if open.
- In the architecture doc, `StrongName` was kept. It re-signs an obfuscator's output for byte
  identity, which is not code signing. Remove it with signing if the user says so.
