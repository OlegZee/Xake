# `csc` through the Roslyn compiler server

Design note, 2026-09-23. Tracker item: "no compiler server: the recorded command line has no
`/shared`, so every `csc` is a fresh process (0.75–1.55 s per assembly); msbuild's `Csc` task
reuses a warm `VBCSCompiler`. Per-assembly Xake is ~1.7x msbuild for that reason alone."

## What changed

`run` (`src/dotnet/Dotnet.csc.fs`) adds `/shared` -- and `/keepalive:<seconds>` when asked --
to the csc command line. With `/shared`, `csc.dll` is a thin client: it connects to the
`VBCSCompiler` next to it, starting one if none is listening, sends the arguments, and
compiles in-process itself if no server can be reached (Roslyn `BuildClient`: "Server build
failed, falling back to local build"). Nothing else about the invocation changed.

```fsharp
type CompilerServer =
    | Shared of keepAlive: int option   // default: Shared None
    | InProcess

RunOptions      = { FailOnError; CscPath; Restore; Server: CompilerServer }
CscSettingsType = { ...; Server: CompilerServer }     // `noserver`, `keepalive <s>` in the builder
```

Opt-out: `noserver` in `csc {}`, `Server = InProcess` on `RunOptions`/`CscSettingsType`, or
the environment variable `XAKE_CSC_SERVER=0` (`false`/`off`/`no`), which changes the default
for the whole process -- the CI switch.

## The switches are parameters of the run, not of the compilation

`/shared` and `/keepalive` never enter a lock. `CommandLineParser.TryParseClientArgs` strips
them on the client side, before the request is built; what the server compiles is the argument
list without them. In Xake they are added in `run` at invocation time, on the command line
next to `/noconfig` (not in the response file, not in `Entry.Args`) -- exactly as the
framework's env vars are a parameter of `run` and not a lock field. Locks recorded today are
byte-identical to those recorded before this change; `CscServerTests` "the lock's arguments
never carry /shared" checks that `CscLock.resolve` yields the same entry for either setting.

## Which server, and is it the hashed compiler?

The lock hashes `csc.dll`. The server that compiles is not that file, so the question is
whether it is the same build of Roslyn.

- The client locates the server relative to itself: `VBCSCompiler` (the apphost) or
  `VBCSCompiler.dll` in `AppDomain.CurrentDomain.BaseDirectory`, i.e. the directory of the
  `csc.dll` that ran -- the SDK's `Roslyn/bincore`, or the toolset package's
  `tasks/netcore/bincore`. Both ship the server next to the client.
- Roslyn's default pipe name is `base64(sha256("<user>.<isAdmin>.<lowercase client directory>"))`
  (`BuildServerConnection.GetPipeName`), so two distinct compilers -- another SDK, another
  toolset version -- never share a pipe: each gets a server of its own. This is also the pipe
  name msbuild's `Csc` task uses for the same SDK, so Xake and `dotnet build` share one warm
  server for one compiler rather than starting two (observed: the test's compile was served by
  the `VBCSCompiler -pipename:RSQW...` a previous `dotnet build` had left running).
- A stale server on that pipe -- the compiler swapped in place under the same directory --
  is caught by Roslyn itself: the request carries the client's commit hash and the server
  answers `IncorrectHashBuildResponse` on a mismatch, after which the client compiles
  in-process.

So there was no need for a Xake-specific `/shared:<name>`: the client directory the pipe name
is derived from is the directory of the hashed `csc.dll`, and the hash check on the request
covers the remaining gap. Keeping the default name is what lets `dotnet build-server shutdown`
and msbuild's own server coexist with ours. (A pipe name of our own would have had to stay
under macOS's 104-byte socket path limit and would have made `dotnet build-server shutdown`
miss our server.)

## When the switch is *not* added (`serverArgs`)

`/shared` goes on the command line only when

1. a `VBCSCompiler.dll` sits in the directory of the compiler file about to be run. The legacy
   full-framework `csc.exe` (`C:\Windows\Microsoft.NET\Framework\v4.0.30319`, the C# 5 native
   compiler the registry provider finds) and mono's `mcs` do not know the switch; an arbitrary
   `cscpath` is not known to be Roslyn. The test is the server file, not the compiler's name,
   so a Roslyn `csc.exe` (a `Microsoft.Net.Compilers.Toolset` `tasks/net472` directory
   carries `VBCSCompiler.exe`, not `VBCSCompiler.dll`) also compiles in-process -- the safe
   side; only the netcore client is served today.
2. the runner sets no env vars for the compiler process. The client passes its working
   directory, temp directory and `LIB` to the server explicitly (`BuildRequest`); the rest of
   the client's environment reaches the server only if this client is the one starting it
   (`GetServerEnvironmentVariables` copies the client's environment, normalizing
   `DOTNET_ROOT`). The env vars `run` sets are `FrameworkInfo.EnvVars`: empty for the SDK path,
   whose references are all absolute `/reference:` arguments; mono's `PATH` and the registry
   provider's `COMPLUS_VERSION` otherwise -- toolchains whose compilers are not Roslyn anyway.
   A non-empty list means a compiler that needs its environment, and it is not served.
3. the compiler is not under the temp directory (`Path.GetTempPath()`). The first full test
   run showed why: tests that restore the toolset package into a throwaway package root
   (`$TMPDIR/xake-fromlock-restore-<guid>`) each left a `VBCSCompiler` running from a folder
   the test had discarded, for the whole keepalive, and every run added more. A compiler in a
   throwaway folder is not one to keep a server for; it compiles in-process.

Everything that *does* determine the output travels in the request: the argument list
(`/deterministic`, `/pathmap`, every absolute path), the working directory
(`Compilation.Directory`, which csc resolves `<include file>` against -- passed as
`workingDirectory`), and the temp directory.

## Byte identity

`CscServerTests` "server and in-process compiles produce byte-identical output" compiles one
library twice with `/deterministic`, `Server = InProcess` then `Server = Shared (Some 120)`,
and `Verify.compare` reports no differing range. (Without `/deterministic` two compiles differ
in MVID and timestamp regardless of the server; and the output name matters -- the module name
is in the PE, so the comparison compiles to the same `/out:` both times.) The same test checks
the traced command line carries `/shared` in one run and not the other, and, on Unix, that a
`VBCSCompiler` from the compiler's own `bincore` is running afterwards. A by-hand check of a
900-class, 60-file library gave identical bytes as well (table below).

## Lifetime and shutdown

A server this client starts idles out after `/keepalive` seconds -- Roslyn's own default is
600, the same 10 minutes msbuild's leaves it -- and a client only ever passes the keepalive it
was given, so `Shared None` inherits that default and `keepalive 30` in a test keeps the box
tidy sooner. A leftover server does not break `dotnet test`: it is a detached process, not a
child the test host waits for (the test suite here leaves one running, exactly as `dotnet
build` does). To stop it deliberately:

- `dotnet build-server shutdown` -- the SDK's own compiler (the default pipe name for
  `<sdk>/Roslyn/bincore`, the current SDK only);
- `dotnet <dir>/VBCSCompiler.dll -shutdown` -- any compiler, that directory's server, the
  toolset package's included (`-pipename:<name>` when a name was given).

A server restored under `$(NuGetPackageRoot)` by the restore step starts from the package
folder, like any other; nothing there is special.

## Measurements

macOS, SDK 10.0.401 (`csc` 5.9.0-1.26423.113), net462 reference assemblies, `/deterministic`,
wall clock per compile, 5 compiles each. "Whole recipe" is the `Csc` recipe inside a one-rule
`xake {}` run (what `CscServerTests` "timing" prints); the others are the bare `dotnet csc.dll`.

| Compile | in-process | `/shared` (warm) | ratio |
|---|---|---|---|
| one class, whole recipe (test) | 0.21 s | 0.07 s | 3.0x |
| one class, bare csc | 0.18 s | 0.04 s | 4.5x |
| 900 classes / 60 files (461 KB dll), bare csc | 0.35 s | 0.19 s | 1.8x |

The first `/shared` compile after a machine is idle pays for starting the server (0.23 s for
the one-class case here, against 0.18 s in-process -- the server start is cheap; the JIT warmth
that follows is what the later compiles get). The tracker's 0.75–1.55 s per assembly was
measured on real assemblies with many references; the bare numbers here are small because the
inputs are, but the fixed cost saved per compile -- host start, Roslyn assembly load and JIT --
is the same one, roughly 0.15 s of it visible even on a one-class compile, and the server also
keeps reference metadata cached across compiles, which is where the larger assemblies gain.

## Caveats

- Verified on macOS only; the `ps` assertion is Unix-only, the switch logic is not
  platform-specific.
- Windows full framework: the registry provider's `csc.exe` is never served (rule 1 above), by
  construction, so the Windows-only path is unchanged rather than merely believed safe.
- Not covered: a Roslyn `csc.exe` client (`tasks/net472` of the toolset package) -- it could be
  served by `VBCSCompiler.exe`, but the test for a server file is `VBCSCompiler.dll`, so it
  stays in-process until someone needs it.
