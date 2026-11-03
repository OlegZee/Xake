# SDK path with a space: failure analysis

Research digest for branch `pr/sdk-path-spaces`, 2026-10-22. Reported from Windows testing:
a hermetic task fails when the .NET SDK lives in `C:\Program Files\dotnet` (the default).
No source was changed; the rsp findings were confirmed by a probe on macOS against SDK 10.0.402
(a directory `a b` with a space, `dotnet fsc.dll @x.rsp` and `dotnet csc.dll @x.rsp`).

## Summary

- **Two independent bugs, both on the compile path shared by `csc`/`fsc`/`Lock.compile`/`Locks.compile`.**
  1. `CompilerRunner.launch` starts `dotnet <compiler.dll> @rsp` through `shell`, which joins
     `Args` with a plain space: the SDK's `csc.dll`/`fsc.dll` path is split at the space.
     Every compile with an SDK compiler under `Program Files` fails before the compiler starts.
  2. The response file is written with `Impl.escapeArgument` (Windows argv quoting of the
     *whole* line). csc accepts that; **fsc does not**: a quoted line `"--reference:C:\Program Files\..."`
     is read as a file name (FS2020). Every F# compile referencing the SDK's ref pack fails,
     even once bug 1 is fixed.
- `shell` (core) never quotes; most other callers pass repository paths (restore, msbuild import),
  which break only when the *repository* or the user profile has a space, not the SDK.
- Bugs 1 and 2 reproduce on macOS/Linux too: .NET parses `ProcessStartInfo.Arguments` with
  Windows rules on every OS, so a symlinked SDK path with a space is a CI-runnable repro.

## Ranked candidates

| # | Location | Mechanism | Verdict |
|---|---|---|---|
| 1 | `src/dotnet/CompilerRunner.fs:153`, `:178-181` | `Dotnet dll -> "dotnet", [dll]`, then `shell { cmd tool; args (...) }` -> `Shell.fs:58` `String.concat " "` | **BUG** (the reported failure, csc and fsc) |
| 2 | `src/dotnet/CompilerRunner.fs:137` | rsp lines `List.map Impl.escapeArgument` for fsc (`Fsc.fs:573`) | **BUG** (fsc only; FS2020) |
| 3 | `src/core/Tasks/Shell.fs:58`, `:113-118` | args joined unquoted; non-`.exe` command on Windows becomes `cmd.exe /c <cmd> <args>` | **BUG** (root cause of 1, 4, 5, 6) |
| 4 | `src/dotnet/CompilerRunner.fs:141` | `"@" + rspFile` unquoted, `rspFile` from `Path.GetTempFileName()` | **BUG** when `%TEMP%` has a space (user name) |
| 5 | `src/dotnet/DotNetFwk.fs:946-949`, `src/hermetic/Described.fs:221-224` | `dotnet restore <obj/xake/restore/.../restore.csproj>` unquoted (NUGET_FETCH path) | **BUG** for a repo path with a space; SAFE for the SDK path |
| 6 | `src/hermetic/Project.fs:94-96` (`runMsbuild`), callers `:439`, `:467`, `:485`; `Import.fs:634` | `dotnet msbuild <project> -getResultOutputFile:<path> -p:...` unquoted | **BUG** for a repo path or a property value with a space; SAFE for the SDK path |
| 7 | `src/dotnet/Dotnet.Msbuild.fs:63-91` | `msbuild` task: `BuildFile`, `@RspFile`, `/property:` unquoted; tool is a temp `.cmd` launcher | **BUG** for spaced repo/temp paths; the launcher itself quotes the host (SAFE for SDK) |
| 8 | `src/dotnet/DotNetFwk.fs:692-701` (`launcher`) | `.cmd`: `@"<host>" <args> %*`; sh: `exec "<host>" ...`; fsc launcher quotes `fsc.dll` (`:717`) | SAFE (host and fsc.dll quoted); `msbuild` launcher fine |
| 9 | `src/dotnet/Csc.fs:260`, `:266` | `/reference:` / `/analyzer:` + `CscArgs.quoteIfNeeded` (comma only), then whole-line `escapeArgument` | SAFE for csc (probe: whole-line quoting accepted) |
| 10 | `src/dotnet/DotNetFwk.fs:379-381` (`restoreSync`) | every arg wrapped in `"..."` | SAFE (no trailing backslash in those args) |
| 11 | `src/dotnet/DotNetFwk.fs:446` (`probeSdk`) | `host` = full `dotnet.exe` path as `FileName`, args `--version` | SAFE (`FileName` takes spaces) |
| 12 | `src/hermetic/Apps.fs:132-138` (`runTool`) | every arg wrapped in `"..."` | SAFE (macOS only: `codesign`, `chmod`); a trailing `\` would break on Windows |
| 13 | `src/dotnet/DotNetFwk.fs:18,26` (`pkg-config`) | args joined | SAFE (Unix, no paths) |
| 14 | `src/hermetic/Import.fs:975`, `:1021` (`unquote`) | msbuild's quoted `DotnetFscCompilerPath` / quoted switch values are unquoted on import | SAFE (correct; feeds the stored args that 2 re-quotes) |
| 15 | lock JSON, `.exe.config`, `xake.project.json` | paths stored as data (tokenized roots), never re-read by a shell | SAFE |

## Details

**1. Compiler launch (`CompilerRunner.launch`).** `Launch` is `Dotnet compiler.Path` whenever
the compiler is a `.dll` (`launchOf`, `:78`): the SDK's `Roslyn/bincore/csc.dll`
(`Csc.managedCompiler`, also what `Import.compilerOf` records) or `FSharp/fsc.dll`
(`DotNetFwk.fscCompiler`, `DotnetFscCompilerPath`). `extraArgs = [dll]` goes into
`shell { args ... }`, which concatenates with a space. On Windows `"dotnet"` has no `.exe`, so
the process is `cmd.exe /c dotnet C:\Program Files\dotnet\sdk\<v>\Roslyn\bincore\csc.dll @C:\...\tmpX.tmp`;
dotnet receives `C:\Program` as the application. Affects `csc { }`, `fsc { }`, `Csc.run`,
`Fsc.run`, `Lock.compile*`, `Locks.compile`, and CSC_SERVER (`/shared` switches are fine, the
dll is not). Under the SDK-free locks (`sdk-free-locks.md`) the compiler comes from the NuGet
folder instead, so it fails only for a user profile with a space; SDK locks fail always.

**2. fsc response file quoting.** fsc reads an rsp as one argument per line, trimmed, with
**no unquoting of switch lines**. Probe results:

| rsp line | csc | fsc |
|---|---|---|
| `"/reference:/tmp/a b/l.dll"` / `"--reference:/tmp/a b/l.dll"` (what Xake writes) | OK | FS2020 (read as a file) |
| `/reference:"/tmp/a b/l.dll"` / `--reference:"/tmp/a b/l.dll"` | OK | FS0084 (quotes kept literally) |
| unquoted, space inside | CS2001 (split) | **OK** |

A bare quoted source path (`"src dir/a.fs"`) works in fsc, which is why `FscTests.fs:272`
(source directory with a space) passes and `docs/session.md` says the rsp quoting is tested.
On Windows the composed and imported F# command lines carry `-r:C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\...`
(`DotNetFwk.netcoreRefDir` -> `InstalledPack`, msbuild's `ReferencePath`), so every F# compile
hits FS2020; `--out:`, `--doc:`, `-r:` under a spaced repo or `.nuget` path likewise.

**3. `shell` itself.** `ShellOptions.Args` is a `string seq` but `shellCore` builds one string
with no quoting (`Shell.fs:58`), and on Windows wraps any command not ending in `.exe` in
`cmd.exe /c` (`:114`), which adds cmd metacharacter handling (`&`, `^`, `%`, `|`, `<`, `>`).
`ProcessStartInfo.ArgumentList` is not available: core, dotnet and hermetic target
`net462;netstandard2.0`, so the command line has to stay a string and be quoted.

**4. `@rsp`.** `Path.GetTempFileName()` is under `%TEMP%`, usually
`C:\Users\<name>\AppData\Local\Temp`: breaks for a user name with a space. Same fix as 1.

**5-7.** Repository paths only (`obj/xake/restore/<n>/restore.csproj`, the project file, the
`-getResultOutputFile:` dumps next to the lock output, `-p:` values the script supplies).
Not the reported failure, but the same defect, and fixed by the same change.

## Escaping mechanism analysis

`Impl.escapeArgument` (`src/dotnet/DotnetTasks.fs:23`, internal to Xake.Dotnet; not in core):
- quotes only when the argument contains a space or `"` (not a tab; an empty argument stays
  empty and so disappears from a joined command line);
- implements the Windows argv rules correctly: a run of backslashes before an embedded `"` or
  before the closing quote is doubled (the fold starts in "next is a quote" state), so
  `C:\Program Files\dotnet\` becomes `"C:\Program Files\dotnet\\"`, not the broken `...\"`;
- the same string works for `ProcessStartInfo.Arguments` on Unix (.NET parses with the same
  rules) and for csc's rsp (Roslyn's `CommandLineParser` uses them), but **not** for fsc's rsp.
- tested in `DotnetTasksTests.fs:443-451` (plain, space, inner quote); no test for a trailing
  backslash or an empty argument.

Used only at `CompilerRunner.fs:137`. Not used by `shell`, `runMsbuild`, the restores or the
`msbuild` task. Two ad-hoc quoters exist besides it (`"\"" + a + "\""` in `DotNetFwk.fs:379`
and `Apps.fs:133`), both without backslash handling.

## Test coverage

- `DotnetTasksTests.fs:443` `escapes compiler arguments`: the helper alone.
- `FscTests.fs:272-299`: fsc with a source directory `src dir` (bare source lines only).
- Nothing covers: a compiler path with a space, a switch value with a space in an fsc rsp,
  `shell` args with spaces, a temp or repo path with a space for restore/msbuild.
- `SdkProbeTests.fs` exercises `dotnetRoot`/`probeSdk`, not paths with spaces.
- A Unix repro is cheap: symlink the SDK root (or a copy of `Roslyn/bincore` and `FSharp`) to
  `/tmp/sdk with space/` and run `Csc.run`/`Fsc.run` with `compiler.Path` under it, plus an fsc
  compile with `-r:` to a library in a spaced directory.

## Recommended fix strategy

**A. Quote in `shell` centrally (core).** `shellCore` applies an argv quoter to each element of
`Args` (move `escapeArgument` to core, e.g. `ProcessExec.quoteArgument`).
+ fixes 1, 4, 5, 6, 7 and every script's `shell { args [...] }` at once.
- changes the public `shell`/`sh` contract: scripts that pass pre-quoted args (`args ["\"a b\""]`)
  or several tokens in one element (`args ["-c Release"]`) break. A "quote only if it has a
  space and no quote" heuristic softens the first but not the second. Core release.

**B. Quote at the call sites, keep `shell` as is.** `CompilerRunner`: quote `dll` and `"@" + rspFile`
with `escapeArgument`, and launch the full host path (`DotNetFwk.dotnetRoot()/dotnet(.exe)`, as
`probeSdk` does) so Windows skips `cmd.exe /c`; same quoting in `runMsbuild`, the two restores and
the `msbuild` task.
+ no change to the public `shell` contract; small, reviewable diff in Xake.Dotnet/Hermetic.
- every future caller has to remember; quoting stays scattered.

**C. Dialect-specific rsp writing (needed with A or B).** Add `QuoteResponseLine: string -> string`
(or a `ResponseDialect`) to `RunPlan`: csc keeps `escapeArgument`; fsc writes lines raw (one
argument per line, no quoting; a line containing a newline is impossible anyway).

**Recommendation: B + C now**, with a new `shell` operation for pre-split arguments as a
follow-up (e.g. `argv [..]` that quotes, leaving `args` raw), which gives A's uniformity without
breaking existing scripts. Add the Unix symlink repro as a test for 1 and 2, a `escapeArgument`
test for a trailing backslash and an empty argument, and an fsc test with `-r:` in a spaced dir.

## Plan (B + C)

Probe re-run on SDK 10.0.402 (macOS, `a b` directory): an unquoted `--reference:<dir with space>/l.dll`
rsp line compiles; `"--reference:..."` fails with FS2020, `--reference:"..."` with FS0084.

- `src/dotnet/Tool.fs`: the argv quoter moves here as public `Tool.quoteArgument` (compiled
  before `DotNetFwk.fs`); `Impl.escapeArgument` stays as an alias, no second copy.
- `src/dotnet/CompilerRunner.fs`: `RunPlan.ResponseQuoting` (`Argv` for csc, `Verbatim` for fsc)
  and `responseLine`; the compiler dll and `@rsp` quoted; the host is the full `dotnet(.exe)`
  path under `DotNetFwk.dotnetRoot ()` (Windows then skips `cmd.exe /c`).
- `src/dotnet/Csc.fs`, `src/dotnet/Fsc.fs`: set the dialect.
- `src/dotnet/DotNetFwk.fs`: both restores (`restoreSync`, the recipe restore) quote with it.
- `src/dotnet/Dotnet.Msbuild.fs`: the project file and `@rsp` quoted; `/property:` quoted unless
  the script already put a `"` in it.
- `src/hermetic/ToolJson.fs` (`ToolArgs.quote`), `Described.fs`, `Project.fs`, `Apps.fs`: the
  hermetic package builds on the published `Xake` package, which does not expose the quoter
  yet, so it carries one copy until its `Xake` floor has `Tool.quoteArgument`.
- Tests: `DotnetTasksTests` (trailing backslash, empty argument); `PathSpacesTests` (`responseLine` csc vs fsc, `commandLine`)
  and, Integration, csc and fsc through a symlinked SDK directory with a space,
  fsc with `-r:` to a library in a spaced directory.
