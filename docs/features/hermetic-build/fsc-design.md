# B6 design note: `Fsc` through the shared runner

Status: **draft for approval**, 2026-10-03. Nothing here is implemented. Every statement about
current behaviour cites the source it was read from (paths relative to the repo root, as of
`4c2ec46`). Statements marked *(probe)* come from a design-time build of a scratch fsproj in
`/tmp` with SDK 10.0.401, not from repo code.

## 1. Goal and non-goals

**Goal.** `fsc { ...; resolve }` returns an `Fsc` record (the resolved F# compilation, the twin
of `Csc`); `Fsc.run` runs it behind the same gate as `Csc.run` (generated files written back,
inputs `needFiles`d, every hash verified before the compiler starts); `fsc { ...; lock "path" }`
lives in Xake.Hermetic.Dotnet like `csc { lock }`; `Project.import` accepts `.fsproj`, so
`Fsproj` retires; `build.fsc.fsx` builds from a lock.

**Non-goals.** A NuGet-restorable F# compiler: there is none current. `Microsoft.FSharp.Compiler`
stops at `10.4.3-beta.19224.1` (2019) and `FSharp.Compiler.Tools` (a net4x `fsc.exe`) at
`10.2.3`, both on nuget.org's flat container. The compiler ships only inside the SDK
(`<sdk>/FSharp/fsc.dll`, with `FSharp.Compiler.Service.dll` and the SDK's own `FSharp.Core.dll`
next to it), so the one reproducible way to fix the F# compiler is to fix the SDK, i.e.
`global.json` with `rollForward: disable` (`Lock.SdkPin.Pinned`, `src/hermetic/Lock.fs:57-61`).
There is no `fsc { toolset }`. Also out of scope: the net462 leg (docs/session.md:75-79 and
97-99: the SDK has no net4x `FSharp.Core`, so the nupkg's net462 asset keeps coming from
`dotnet pack`).

## 2. The record

### Fields

`Csc` is `src/dotnet/Csc.fs:262-294`. `Fsc` gets **the same fields with the same meaning**:

| Field | Csc | Fsc |
|---|---|---|
| `Name`, `Framework`, `Directory` | as is | identical |
| `Options` | non-section switches, `/` form, markers | non-section switches, **fsc's own `-`/`--` form**, markers |
| `Defines` | `/define:A;B` split | every `--define:X` / `-d:X`, one symbol each |
| `Sources` | absolute, order kept | absolute, **order is semantic** (F# compile order) |
| `Generated`, `Resources` | text inputs; `(resx, .resources)` pairs | identical (msbuild's `AssemblyInfo.fs`, `AssemblyAttributes.fs`; resx pairs) |
| `Dependencies` | `{ Compiler; References; Analyzers }` (`Csc.fs:221-226`) | the same type; `Analyzers` always `[]` |

What differs is only the argument dialect:

| | csc (`src/dotnet/CscArgs.fs`) | fsc |
|---|---|---|
| switch prefix | `/name[:value]`; an arg starting with `/` and no second `/` before `:` (`CscArgs.fs:21-28`) | `--name[:value]` or `-x[:value]`; fsc reads a leading `/` as a path on Unix (`src/dotnet/Dotnet.fsc.fs:104-107`) |
| aliases | `r`, `a`, `res`, `linkres`, `l`, `d` (`CscArgs.fs:30-32`) | `r`, `o` (out), `g` (debug), `d` (define), `I` (lib), `a` (target library) |
| references | `/reference:a,b` list, `alias=` prefix, comma quoting (`CscArgs.fs:67-91`) | `-r:path`, one per switch, no alias, no list *(probe: msbuild emits `-r:` per item)* |
| defines | one switch, `;`-separated | one switch per symbol (`Dotnet.fsc.fs:149-151`) |
| output / doc | `/out:`, `/doc:` | `--out:` or `-o:`, `--doc:` *(probe: msbuild emits `-o:obj/...`)* |
| resources | `/resource:path,name` | `--resource:path,name` *(probe)*, same "first element is a path" shape |
| target | `/target:` | `--target:` |
| F#-only | | `--noframework`, `--targetprofile:netstandard`, `--nocopyfsharpcore`, `--simpleresolution`, `--tailcalls+/-`, `--sig:` (output) |
| response file | `@file`; `/noconfig` must stay outside it (`Csc.fs:762-765`) | `@file`, no `/noconfig` rule |

`FSharp.Core` is **an ordinary reference** in the record. msbuild passes it as
`-r:<nuget>/fsharp.core/<v>/lib/netstandard2.0/FSharp.Core.dll` *(probe)*, i.e. from the
implicit `FSharp.Core` package (`Microsoft.FSharp.NetSdk.props`, `DisableImplicitFSharpCoreReference`),
so an imported entry hashes it and lists the package in `Packages`. The composed path never adds
it: with a target framework the task passes `--noframework` (`Dotnet.fsc.fs:125-128,137-138`)
and the caller supplies `FSharp.Core` through `ref` (the integration test does,
`src/tests/DotnetTasksTests.fs:252-270`).

### (a) Two records with a private core, or one generic record

**Two records, `Csc` and `Fsc`, with a private runner core. Keep the plan's choice.**
(extraction-plan.md:128-133.) Re-checked now that both shapes are known:

- For: the fields coincide, so a generic `Compilation` with `Compiler.Tool` dispatch would cost
  no hermetic churn at all (an F# entry would simply be a `Csc` with `Tool = "fsc"`).
- Against, and decisive: (1) the dialect is per compiler. `Csc.ofArgs`, `Args`, `Output`,
  `mapPaths` (`Csc.fs:291-294,350-435`) all parse with `CscArgs`; a generic record must dispatch
  every one of them on a string field, and `ofArgs` cannot know which parser to use before the
  record exists. (2) `RunOptions` is csc's (`CscPath`, `Server`, `Csc.fs:131-149`); fsc has no
  server. (3) The type is what the user sees in `resolve`'s return and in the lock API; a `Csc`
  that is an F# compilation is a lie the type checker cannot catch. (4) The user rejected the
  abstract record by name (extraction-plan.md:131-133).

The commonality lives in two private places: the argument dialect core (2b) and the runner core
(section 4).

### (b) The argument form in the record

**Native fsc form, not csc-normalized.** `Fsc.Options` holds `--optimize+`, `-o:...`,
`--doc:...` exactly as given, with the markers `@Sources`, `@References`, `@Defines` at the
positions of their sections. Reasons:

1. The import's fidelity check compares the rebuilt line to msbuild's byte for byte
   (`src/hermetic/Project.fs:289-294`); a normalized form would need a translation inside the
   check, and the lock would show a command line nobody ran.
2. `/` is not a switch for fsc on Unix (`Dotnet.fsc.fs:104-105`), so a `/`-normalized record
   would have to be translated back on every run.
3. The lock stays readable as the fsc invocation.

So there is one dialect per compiler: a public `FscArgs` module with the same surface as
`CscArgs` (`parse`, `canonical`, `paths`, `mapPaths`, `format`, `inputs`, `outputs`, `sources`,
`switchValues`, `absolutize`), both thin over a private table-driven core (prefix rule, aliases,
path shapes, input and output switch sets). `CscArgs` keeps its 3.4 signatures exactly.
`Arg.Switch` must remember its prefix spelling (`-o` vs `--out`) for the round trip; that is an
fsc-side type, `CscArgs.Arg` is untouched.

**Analyzers.** fsc has no analyzer switch. `Fsc.ofArgs` never emits `@Analyzers` and
`Fsc.isMarker` recognizes only the three markers (a leading `@` in `Options` is still always a
marker, never a response file, as for csc, `Csc.fs:256-257`). `Dependencies.Analyzers` stays in
the shared type and is `[]`. The JSON keeps writing `"Analyzers": [ ]` for an F# entry: it is
what the one writer already prints for a csc entry without analyzers (`Lock.fs:262-266,312`), so
the F# section has exactly the C# shape:

```json
"Compilation": {
  "Directory": "$(ProjectRoot)/src/core",
  "Options": [ "-o:...", "-g", "--debug:portable", "--noframework", "@Defines", "--doc:...", "--optimize+", "@References", "--target:library", ..., "@Sources" ],
  "Defines": [ "TRACE", "RELEASE", "NETSTANDARD", ... ],
  "Sources": [ "$(ProjectRoot)/src/core/obj/xake/netstandard2.0/Xake.AssemblyInfo.fs", ... ],
  "Generated": { ... }, "Resources": { ... }
},
"Dependencies": {
  "Compiler": { "Tool": "fsc", "Path": "$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll", "Sha256": "...", "Version": "15.2.401-servicing.26423.113" },
  "References": [ { "Path": "$(NuGetPackageRoot)/fsharp.core/.../FSharp.Core.dll", "Sha256": "..." }, ... ],
  "Analyzers": [ ],
  "Packages": [ ... ]
}
```

### (c) How the lock holds a C# or an F# entry

Today `Lock.Entry = { Csc: Csc; Evaluation; Packages }` (`Lock.fs:111-116`) and the JSON already
carries `Compiler.Tool` (`Lock.fs:309-310`, read back at 368). Callers of `entry.Csc` outside
`Lock.fs`/`Project.fs`: `Sbom.fs:271,340,342,368,370`, and the tests (`ProjectImportTests` 31,
`FromLockTests` 20, `CscLockTests` 10, `LockDiffTests` 8, `ToolsetImportTests` 5,
`ResxImportTests` 2, `RestoreTests` 1), plus `docs/features/hermetic-build/import.fsx:87-91`.

| Option | Shape | csc locks byte-identical | `diff`, `rehash`, `mapPaths`, `mapText` | `restoreRequest`, `Sbom.forAssembly` | Tests |
|---|---|---|---|---|---|
| A. extra field | `Entry = { Csc: Csc; Fsc: Fsc option; ... }` | yes | each checks both | each checks both | none change |
| B. union field | `Entry = { Compilation: Compilation; ... }`, `[<RequireQualifiedAccess>] type Compilation = Csc of Csc \| Fsc of Fsc`, in `Lock` (hermetic) | **yes**: the writer prints the same keys in the same order for both cases; the reader picks the case from `Dependencies.Compiler.Tool` (`"fsc"`, anything else is csc, so old files read as before) | one dispatch each, via a private view of the common fields | one dispatch; Sbom names the tool from `Compiler.Tool` instead of the literal `"tool:csc"`/`"csc"` (`Sbom.fs:372`), unchanged for csc | construction sites (`{ Csc = ...}`) change; read sites keep compiling through a member `entry.Csc` |
| C. generic record | `Fsc` is a `Csc` with `Tool = "fsc"` | yes | none | none | none, but see 2a: rejected |

A is ill-typed: an F# entry would still need a `Csc` value to fill the mandatory field. **B is
the proposal**, made cheap by three members on `Entry`: `Name`, `Framework` and `Dependencies`
(dispatching), plus `Csc` (returns the C# compilation, fails with "'X' is an F# entry; use
entry.Compilation" otherwise), so the ~77 read sites in tests and Sbom compile unchanged. The
union is `RequireQualifiedAccess` so its case `Csc` never shadows the module `Csc` in `Csc.run`.
It lives in the hermetic `Lock` module: the base has no reason to know about it (A-lite rejected,
extraction-plan.md:124). Hermetic is 0.x; the construction-site change is the one break.

## 3. Hashing the compiler

| Item | Hashed? | Recorded as |
|---|---|---|
| `<sdk>/FSharp/fsc.dll` | yes, `Compiler.Sha256` | `Compiler.Path`; `Version` = `compilerVersion` (`Csc.fs:314-326`) of `fsc.dll`, e.g. `12.8.403-beta.24526.2` (SDK 8.0.425) or `15.2.401-servicing.26423.113` (SDK 10.0.401) *(probe, `FileVersionInfo`)* |
| `FSharp.Core.dll` the code references | yes, as a `Reference` (package or `ref`) | `References` |
| the SDK's own `<sdk>/FSharp/FSharp.Core.dll`, `FSharp.Compiler.Service.dll` | no | covered by the path (`sdk/<version>/`) and the `fsc.dll` hash, exactly as csc's `Microsoft.CodeAnalysis*.dll` are covered by `csc.dll` today |

Today the task never sees `fsc.dll`: on the SDK provider `FscTool` returns a temp launcher
script (`xake-fsc-<hash>`) that runs `dotnet "<sdk>/FSharp/fsc.dll"` (`src/dotnet/DotNetFwk.fs:472-483,498`).
`Fsc.ofSettings` must record the dll, like `Csc.managedCompiler` does for the csc apphost
(`Csc.fs:333-338`). Proposal: `DotNetFwk.fscCompiler : FrameworkInfo -> string option` returning
`InstallPath </> "FSharp" </> "fsc.dll"` when it exists (SDK provider: `InstallPath = sdk`,
`DotNetFwk.fs:493`) and `FscTool None` otherwise; no change to the public `FrameworkInfo` record.
The runner starts a `.dll` compiler as `dotnet <dll>` (`Csc.fs:784`), so the launcher script is
no longer needed for `Fsc.run`.

**Windows full-framework `fsc.exe`**: the registry provider finds `Microsoft\FSharp\<ver>\Runtime\v4.0\fsc.exe`
(`DotNetFwk.fs:156-164,204`). The generic path handles it (a non-`.dll` compiler is run directly,
`Csc.fs:785`, and hashed like any file), but nothing tests it and no CI runs Windows.
Proposed: **out of scope**, works as a side effect, documented as untested. The mono provider's
`fsharpc` (`DotNetFwk.fs:124`) is a bare command name, which the runner's existence check rejects
(`Csc.fs:683`); composed `csc` on mono has the same problem with `mcs` (`DotNetFwk.fs:123`). Not
fixed by B6.

## 4. The runner core

`Csc.run` (`Csc.fs:678-822`), step by step:

| # | Step (lines) | csc | fsc | Core |
|---|---|---|---|---|
| 1 | trace `compiling 'X' (tool version)` (681) | yes | yes | shared |
| 2 | compiler file must exist unless `CscPath` overrides (683-686) | yes | yes (override = `FscPath`) | shared |
| 3 | `needFiles` the compiler (691) | yes | yes | shared |
| 4 | write back `Generated` when missing or different (698-703) | yes | yes | shared |
| 5 | create the directories of every output switch (705-707) | `CscArgs.outputs` | `FscArgs.outputs`; covers `--doc:` whose directory fsc does not create (`Dotnet.fsc.fs:167-169`) | shared, outputs from the dialect |
| 6 | `needFiles` each resx, compile missing `.resources` (715-718) | yes | yes: today's fsc compiles resx to temp files (`Dotnet.fsc.fs:87-88,175-178`); `ofSettings` moves to csc's permanent pairs (`Csc.fs:535-560`) | shared |
| 7 | collect hashed files: references, analyzers, compiler (722-726) | yes | yes (no analyzers) | shared |
| 8 | report hashed files that are missing and have no rule (746-749) | yes | yes | shared |
| 9 | `needFiles` every input of the command line (758) | `CscArgs.inputs` | `FscArgs.inputs` | shared, inputs from the dialect |
| 10 | verify every hash (760) | yes | yes | shared |
| 11 | response file (764-777) | `/noconfig` outside, rest in `@rsp` | everything in `@rsp` | shared, leading args from the plan |
| 12 | executable: `CscPath`, `dotnet x.dll`, or the file (779-785) | yes | yes | shared |
| 13 | server switches (787-794) | `serverArgs` (`Csc.fs:176-188`) | none: fsc has no server, always in-process | csc-only, `[]` for fsc |
| 14 | env vars (804) | `options.Environment` | the framework's `EnvVars` (`Dotnet.fsc.fs:197`) | shared |
| 15 | working directory (813) | `c.Directory` | `c.Directory` (today's fsc sets none) | shared |
| 16 | log prefix, diagnostics (814-816) | `[csc]` | `[fsc]` | shared: `Tool.diagnosticLevel` (`src/dotnet/Tool.fs:19-24`) matches both `file(1,1): error FS0039` and position-less `error FS0084` (docs/session.md:68-71) |
| 17 | fail on exit code (819) | `Tool.failOnExitCode` | same (`Dotnet.fsc.fs:203`) | shared |

Response-file quoting: `Csc.run` writes each argument through `Impl.escapeArgument` (`Csc.fs:768`,
`src/dotnet/DotnetTasks.fs:23-35`). Whether fsc's `@file` reader strips the same quoting is
**to be verified in PR B** by the integration test (a source path with a space); msbuild's own
`Fsc` task also hands fsc a response file.

**Shape.** An internal module before both records, a plan as data:

```fsharp
module internal CompilerRunner =
    type RunPlan = {
        Name: string; Tool: string; Directory: string
        Compiler: Compiler; CompilerOverride: string option
        Args: string list                 // the flat command line (record.Args)
        Inputs: string list; Outputs: string list   // from the dialect
        Hashed: (string * string) list
        Generated: (string * string) list; Resources: (string * string) list
        Leading: string list              // csc: ["/noconfig"] when present; fsc: []
        ClientSwitches: string -> string list   // csc: serverArgs options; fsc: fun _ -> []
        FailOnError: bool; Environment: (string * string) list }
    let run (plan: RunPlan) : Recipe<ExecContext, unit> = ...   // steps 1-17
```

`Csc.run options c` and `Fsc.run options f` each build a `RunPlan` and call it; `Csc.run`'s
signature and behaviour do not change (proved by the existing csc suites and a dataengine replay).
`Fsc.run` takes its own `FscRunOptions = { FailOnError; FscPath: string option; Environment }`
(extraction-plan.md:200 already says `Fsc` gets its own options), so no field is silently
ignored (conceptual-review §2.2). `Lock.compileWith` maps `Lock.Options.Run` to it
(`FailOnError`, `Environment`; no path override), leaving `Lock.Options` unchanged.

## 5. `Fsc.ofSettings`

Same pattern as `Csc.ofSettings` (`Csc.fs:524-660`): compose a flat list, `Fsc.ofArgs` it, fill
`Name`/`Framework`/`Directory`/`Resources`/compiler, check the round trip. No `needFiles`
(`run` does it, B8). `fsc { ...; resolve }` returns it through an `FscRequest` marker and a
second `Run` overload, as `CscRequest` does (`src/dotnet/Dotnet.csc.fs:12,62-65`).

| Setting (`Dotnet.fsc.fs:13-44`) | Today (`Dotnet.fsc.fs`) | In the record |
|---|---|---|
| `Target` | `--target:` (134) | same |
| `Platform` | never emitted (135 commented out) | still not emitted (an emitted default would change the line) |
| `Out` | `--out:`, else the rule's target (81-85, 143-144) | same |
| `Src` | sources in fileset order (153) | `Sources`, order kept |
| `Ref` | `-r:` each (155) | `@References` |
| `RefGlobal` | looked up in the framework, or bare names without one (119-128) | looked up; see framework below |
| `Resources` | resx to temp, `--resource:file,name` (87-88, 158) | permanent `(resx, .resources)` pairs under `obj/xake/<name>/`, as csc |
| `Define` | one `--define:` per symbol (151) | `@Defines` |
| `TargetFramework` / `NETFX-TARGET` | optional; `null` means no `--noframework`, fsc's default references (95-100, 114-128) | **required** (proposal) |
| `NETFX` | picks the compiler's framework (162-165) | same, via a shared `frameworkOf` (`Csc.fs:482-494`) |
| `FscVersion` / `FSCVER` | picks the compiler (180-181); only the registry provider uses it, the SDK and mono `FscTool` ignore the argument (`DotNetFwk.fs:124,498`) | same; documented as Windows-registry-only |
| `Doc` | `--doc:`, creates its directory (146-147, 167-169) | `--doc:`; the runner creates it (step 5) |
| `NoFramework` | `--noframework` (137-138) | same |
| `Tailcalls` | **ignored**: no switch is ever emitted for it | emit `--tailcalls-` when false (a bug fix; the default line is unchanged) |
| `CommandArgs` | appended last (159) | same |
| `FailOnError` | | `FscRunOptions` via `Fsc.runOptions` |

Argument form: always `--` (and `-r:`), on every OS. Today Windows gets `/` (`Dotnet.fsc.fs:106-107`);
fsc accepts `--` there too, and a lock must not depend on the OS that recorded it.

**Framework required.** With no framework the line has neither `--noframework` nor framework
references, so fsc compiles against whatever default set the compiler process brings, which is not
a recorded input. csc made this an error on 2026-09-30 (docs/session.md:41-43,
`Csc.fs:579-580`). Proposal: the same error for fsc, same wording. It breaks
`samples/features.fsx:124-135` and the snippets in `docs/tasks.md:256-261`/`docs/cheatsheet.md:91`
(all Windows-era, `grefs ["System.dll"]`), which get a `targetfwk`.

**`FSharp.Core` and `--nocopyfsharpcore`.** `ofSettings` records exactly the `ref`s given; it does
not invent an FSharp.Core. That is the macOS full-framework gap made explicit: for `net4x` the
caller must `ref` a net4x-capable `FSharp.Core` (docs/session.md:75-79); the record then hashes
it. msbuild always passes `--nocopyfsharpcore` *(probe)*; the composed task never has, so fsc
copies `FSharp.Core.dll` next to the output, an output no rule declares. Proposed: keep today's
line (no flag) for 3.x compatibility, document it, and let `args ["--nocopyfsharpcore"]` opt in.

## 6. Hermetic side

**`fsc { lock }`.** An extension of `FscSettingsBuilder` mirroring `CscLockBuilder`
(`Lock.fs:792-838`): `FscLocked (path, settings, packageRoot, restore)`, `lock`, `packageroot`,
`norestore`, and a `Run` that does `Fsc.ofSettings`, `Fsc.runOptions`, then the lock gate. The
gate today takes a `Csc` (`Lock.buildWith`, `record`, `verify`, `Lock.fs:698-782`); it is entry
based inside (`recordEntry`, `ofCsc`, `diffText`), so it becomes `buildEntryWith options path
(entry: Entry)`, with `buildWith ... (c: Csc)` = `buildEntryWith ... (ofCsc c)` (signature kept)
and `ofFsc` added. `Lock.compileWith` (`Lock.fs:612-654`) dispatches on `Compilation`:
restore, `ensureCompilerAvailable` (takes `Name` and `Compiler` instead of a `Csc`), the
`$(SourceRevisionId)` substitution (`mapText` per case), then `Csc.run` or `Fsc.run`.
`ensureCompilerAvailable`'s "SDK <version> is not installed" message already covers
`$(DotnetRoot)/sdk/<v>/FSharp/fsc.dll` (`Lock.fs:581-591`).

**`Project.import` for `.fsproj`.** The csproj import is a design-time build:
`-t:PrepareResources;Compile` with `ProvideCommandLineArgs=true`, `SkipCompilerExecution=true`,
`NonExistentFile=...`, then `-getItem:CscCommandLineArgs,...` (`Project.fs:135-143,348-365,428-433`).
The F# targets support the same contract: the `Fsc` task takes `ProvideCommandLineArgs` and
`SkipCompilerExecution` and outputs `FscCommandLineArgs`, and `CoreCompile` lists
`$(NonExistentFile)` in its outputs (`<sdk>/FSharp/Microsoft.FSharp.Targets`, lines 305, 388,
395, 416 in SDK 8.0.425). The probe returned the full line: `-o:obj/...`, `-g`, `--debug:portable`,
`--noframework`, `--define:` x13, `--doc:`, `--optimize-`, `--tailcalls-`, 112 `-r:`,
`--target:library`, `--warn:3`, `--warnaserror:3239`, `--fullpaths`, `--flaterrors`,
`--highentropyva+`, `--targetprofile:netstandard`, `--nocopyfsharpcore`, `--deterministic+`,
`--simpleresolution`, `--resource:r.txt,p.r.txt`, then the sources (generated `AssemblyInfo.fs`
and `AssemblyAttributes.fs` first), relative to the project directory.

So `Fsproj.evaluate` (`src/hermetic/Fsproj.fs:60-62,127-196`, items and properties for a
composed `fsc {}`) is **not** the import; the import is `parseImport` with three changes:

| `parseImport` (`Project.fs:163-300`) | csproj | fsproj |
|---|---|---|
| item asked for (`items`, 139) | `CscCommandLineArgs` | `FscCommandLineArgs` (ask for both; one is empty) |
| parse | `CscArgs.absolutize`, `Csc.ofArgs` (178, 264) | `FscArgs.absolutize`, `Fsc.ofArgs` |
| compiler (195-205) | `CscToolPath`/`CSharpCoreTargetsPath`/`RoslynTargetsPath` | `DotnetFscCompilerPath` (quoted, `"<sdk>/FSharp/fsc.dll"` *(probe)*), add it to `wantedProperties` |
| `Tool` (274) | `"csc"` | `"fsc"` |

Everything else (restore once, per-framework build, `-pp` imports, packages, resx pairs,
`Generated` under the intermediate directory, SourceLink tokenizing, the round-trip check) is
compiler-neutral already, given the dialect.

**`build.fsc.fsx` after B6.** Two shapes:

| | One lock, imported | `fsc { lock }` per library |
|---|---|---|
| lock | `locks/xake.json`, rule `Project.import` over the three fsproj (as `import.fsx:57-69`) | one per library, from composed settings |
| what to compile comes from | msbuild's own command line | `Fsproj.evaluate` kept as an input |
| `Fsproj` | retired | stays |
| build rule | `Lock.load`, `Lock.entryFor fwk name`, `Lock.mapPaths` (msbuild's `-o:`/`--doc:` under `obj/xake/` to `out/<fwk>/`, project references to `out/<fwk>/<lib>.dll`, as `import.fsx:83-94`), `Lock.compile` | `fsc { ...; lock "locks/<lib>.json" }` |
| command line vs `dotnet build` | the SDK's (`--highentropyva+`, `--targetprofile`, `--nocopyfsharpcore`, ...), closer than today | today's composed line |

**Recommend the imported lock.** It is the only shape that retires `Fsproj` and the only one
where the build of record compiles what msbuild would. Its rule depends on the version variable
(the version is in the generated `AssemblyInfo.fs`), like today's `projects/*.json` rule
(`build.fsc.fsx:153-171`). Acceptance changes accordingly: "byte-identical to today"
(extraction-plan.md:154) holds only for the composed shape; for the imported one the check is
two clean builds byte-identical to each other, and the diff against `dotnet build` reported
(it was never zero, hermetic-build/session.md:885-892).

## 7. Base vs hermetic release coupling

The hermetic package builds on the published `Xake` (`src/hermetic/Xake.Hermetic.Dotnet.fsproj:67`,
`[3.4.0.21, 4.0)`), so the base lands first.

| PR | Package | Content | Tests |
|---|---|---|---|
| A | Xake 3.5 | `FscArgs` + private dialect core under `CscArgs`; `Fsc` record, `Fsc.ofArgs`/`Args`/`Output`/`rehash`/`mapPaths`/`mapText`/`isMarker`; `Fsc.ofSettings`, `DotNetFwk.fscCompiler`; `fsc { resolve }`; settings types move to an AutoOpen `FscTypes` in a new `Fsc.fs` | unit: `FscArgs` round trip on the probe line, `ofSettings` output, markers |
| B | Xake 3.5 | `CompilerRunner` extracted from `Csc.run` (csc unchanged); `Fsc.run`, `FscRunOptions`, `Fsc.runOptions`, `Fsc.compile`; `fsc {}` runs `Fsc.compile`; `let Fsc settings` retired (see 9); `targetfwk` required; `--tailcalls-` | all csc suites unchanged; new fsc integration tests (section 9) |
| C | Xake 3.5 | docs: `docs/tasks.md` fsc section, `docs/dotnet-build.md` (`FSCVER` scope), cheatsheet, `samples/features.fsx`, release notes | samples run |
| | | **release Xake 3.5.0.<run>** | |
| D | Hermetic 0.2 | floor `[3.5.0.<run>, 4.0)`; `Lock.Compilation`, `Entry.Compilation` + members; reader/writer dispatch on `Tool`; `buildEntryWith`, `ofFsc`; `fsc { lock }`; Sbom tool name; `Project.import` for fsproj | csc locks byte-identical (fixture locks and dataengine 36/36); F# import round trip; `fsc { lock }` record, match, drift, CI-missing |
| E | Hermetic 0.2 | `build.fsc.fsx` on `locks/xake.json`; `Fsproj` retired (or `[<Obsolete>]`, decision 5); `projects/` removed | two clean runs byte-identical |
| | | **release Xake.Hermetic.Dotnet 0.2.0.<run>** | |

`build.fsc.fsx` itself bootstraps from the published packages (`build.fsc.fsx:27-28`), so PR E
uses the dev `#r` lines until 0.2 is out, then bumps them (docs/session.md:23-29).

## 8. Decisions for the user

1. **How a lock entry holds C# or F#.** Options: an extra `Fsc: Fsc option` field; a
   `Compilation = Csc of Csc | Fsc of Fsc` union field; a generic record. *Recommendation:* the
   union in the hermetic `Lock` module, discriminated in JSON by the existing
   `Compiler.Tool`, with `entry.Csc`/`Name`/`Framework`/`Dependencies` members so read sites keep
   compiling. Existing csc locks stay byte-identical; only `{ Csc = ... }` construction sites
   change (hermetic 0.2).
2. **The shared core.** Options: a private `CompilerRunner.run : RunPlan -> Recipe` that both
   `Csc.run` and `Fsc.run` fill; or a dialect record of functions passed to one generic run.
   *Recommendation:* the `RunPlan` data record (testable, no closures beyond the server
   switches), plus `FscRunOptions { FailOnError; FscPath; Environment }` rather than reusing
   csc's `RunOptions` with ignored fields.
3. **3.5 breaks for fsc: a required target framework, and `Fsc settings` becoming
   `Fsc.compile settings`.** The first mirrors csc's 2026-09-30 decision; the second is forced
   by the record (a module `Fsc` cannot coexist with the function `Fsc`, extraction-plan.md:289,295)
   and was planned "for symmetry", although the roadmap calls B6 additive
   (extraction-plan.md:160-161). *Recommendation:* take both in 3.5, as csc did in 3.4, and
   correct the "additive" line.
4. **Windows full-framework `fsc.exe` (registry provider).** *Recommendation:* out of scope:
   it runs and hashes through the generic path, but is documented as untested; `FSCVER` is
   documented as meaningful only there.
5. **Retire `Fsproj` in the same release.** Options: delete it in hermetic 0.2 (PR E), or mark
   it `[<Obsolete>]` in 0.2 and delete in 0.3. *Recommendation:* delete in 0.2: it has one
   caller, `build.fsc.fsx` (conceptual-review §2.6), the package is 0.x, and the module already
   says "Retired after B6" (`Fsproj.fs:15`).

## 9. Risks

- **Byte-identity of existing csc locks.** The writer must print the `Csc` case exactly as now
  (`Lock.fs:256-314`). Guard: format every lock in `src/hermetic.tests` fixtures and the
  dataengine sample before and after PR D, compare bytes (the B2 procedure, 36/36).
- **`FscSettingsType` 3.3 compatibility.** Must not change: field names and types, `Default`
  values, `FscSettings`, every builder operation (`Dotnet.fsc.fs:209-259`). The type moves
  module (`FscImpl` to `FscTypes`, AutoOpen both), source-compatible for `open Xake.Dotnet`.
  Behaviour changes, all in 3.5 release notes: `Fsc settings` gone, `targetfwk` required,
  `--` on Windows, `notailcalls` now effective, resx no longer compiled to temp files.
- **The SDK is the compiler.** The repo's `global.json` is `8.0.0` + `latestMajor`, which
  selected 10.0.401 here; a lock records `sdk/10.0.401/FSharp/fsc.dll` and fails on a machine
  without that SDK (`Lock.fs:584-588`) and warns "not pinned" at import (`Project.fs:462-463`).
  CI (`.github/workflows/build.yml`) runs only `build.fsx` on 8.0.x and 10.0.x, so this is
  confined to `build.fsc.fsx`, but `fsc { lock }` users must be told to pin.
- **macOS full-framework gap.** Unchanged: no net4x `FSharp.Core` in the SDK. The record makes
  it visible (the caller's `ref` is hashed) but does not close it.
- **Compiler identity is the entry point's hash.** `fsc.dll` is a small host; the compiler is
  `FSharp.Compiler.Service.dll`. The SDK version in the path pins it in practice, as for csc
  today; hashing the compiler's directory would be a separate change for both tools.
- **Test coverage.** Today: one fsc integration test (`DotnetTasksTests.fs:253-278`,
  netstandard2.0, runtime `FSharp.Core`) and a builder smoke test (496); no lock, no hash
  check, no import. B6 adds: `FscArgs` unit tests (probe line round trip, prefixes, aliases,
  `inputs`/`outputs`); `Fsc.ofSettings` unit tests (order, `--` on all OSes, framework error,
  `--tailcalls-`); integration `Fsc.run` (compile, hash mismatch fails, missing reference
  reported, resx embedded, `--doc:` directory created, path with a space through the rsp);
  hermetic: fsproj import round trip, `fsc { lock }` record/match/drift/CI, mixed C#/F# lock
  read and write, csc lock byte-identity.
