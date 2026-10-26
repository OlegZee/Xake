# Xake cheatsheet

## Package reference

```fsharp
#r "nuget: Xake"
open Xake
open Xake.Tasks
```

## Script entry point

```fsharp
do xakeScript {
	"main" <== ["build"; "test"]
}
```

Rules go straight in the script body. The older `rules [ ... ]` wrapper still works, and both
forms may be mixed.

## Dependency operators

`<==` demand targets in parallel:

```fsharp
"main" <== ["build"; "test"]
```

`<<<` demand targets in sequence:

```fsharp
"deploy" <<< ["build"; "test"; "package"]
```

`=>` plain phony rule:

```fsharp
"clean" => rm {dir "out"}
```

## Preferred rule forms

```fsharp
do xakeScript {
	command "build" {
		do! sh "dotnet build src/core -c Release" { () }
	}

	target "out/version.txt" {
		do! writeText "1.0.0"
	}

	targets ["out/a.dll"; "out/a.xml"] {
		do! sh "dotnet build src/core -c Release -o out" { () }
	}

	// rules can also be generated
	for name in ["a"; "b"] do
		$"clean-{name}" => rm { dir $"out/{name}" }
}
```

## Recipe helpers

```fsharp
recipe {
	do! need ["out/a.dll"]
	let! files = getFiles <| fileset { includes "src/**/*.fs" }
	do! needFiles files
	do! trace Info "Done"
}
```

Capture groups of the rule's pattern (file or phony): `let! name = getRuleMatch "name"` (`""` when
absent), `let! all = getRuleMatches ()` (a map). Also `getVar`, `getEnv`, `getCtxOptions`,
`getTargetFile` (file rules only), `getTargetFullName`, `alwaysRerun ()`.

Typed groups (`open Xake.Typed` after `open Xake`; see [typed-targets.md](typed-targets.md)):
`type G = { cfg: string; name: string }`, `let X = Target<G> "out/(cfg:*)/(name:*).dll"`,
`X.Rule <| fun m -> recipe { ... m.cfg ... }`, `need [ X.Format { cfg = "Release"; name = "a" } ]`;
`Command<G> "pack::(name:*)"` is a phony rule that always runs.

## Shell task

```fsharp
do! sh "dotnet" {
	args ["test"; "src/tests"; "-c"; "Release"]
	failonerror
}
```

## .NET tasks

```fsharp
open Xake.Dotnet

// the target file is the output; `targetfwk` selects the reference assemblies,
// by default the SDK's own .NET (net10.0 on SDK 10.0.x)
"hello.exe" ..> csc { targetfwk "net-4.6.2"; src !!"hello.cs"; grefs ["System.dll"] }
"app.dll"   ..> fsc { src !!"src/*.fs" }
// an app `dotnet hw.dll` runs: declare the runtimeconfig as a target too
["hw.dll"; "hw.runtimeconfig.json"] *..> csc { src !!"hw.cs" }
"build"      => msbuild { buildfile "a.sln"; target "Rebuild"; prop ("Configuration","Release") }
"res"        => resgen { resources (resourceset { prefix "App"; files !!"**/*.resx" }) }
```

Script variables: `NETFX` (tool framework), `NETFX-TARGET` (default target framework), `FSCVER`, `CSC_SERVER`, `CSC_JOBS`/`FSC_JOBS` (max concurrent compilers; `throttle (resource, units)` in `csc { }`/`fsc { }` uses the script's own resource instead).

Locks of project descriptions (`Xake.Hermetic.Dotnet`, [hermetic/lock.md](hermetic/lock.md#xake-generated-locks)):
a phony rule per project x configuration in the script, run only by `xake update-locks`:
`"lock::(name:*)::(cfg:*)" => recipe { ...; do! need [ for r in d.ProjectRefs -> $"lock::{r}::{cfg}" ]; do! alwaysRerun (); do! Locks.generate lockOf outOf cfg (descriptionOf name) }`
and `command "update-locks" { do! Locks.restoreAll descriptions "Release" lockOf descriptionOf; do! need [ for n in projects -> $"lock::{n}::Release" ] }`
-- references first by `need`, nothing compiled, all package sets in one `dotnet restore` (without `restoreAll`: one per project x framework);
`-d LOCK_REFRESH=<ids|*>` re-resolves pinned packages.
Build from the committed locks ([hermetic/lock.md](hermetic/lock.md#building-from-xake-generated-locks)):
`command "build" { do! Locks.needAll descriptions "Release" lockOf outOf }` and
`target "out/(cfg:*)/(fwk:*)/(name:*).dll" { ...; do! Locks.compileAs (lockOf name cfg) fwk versions }`
-- the dll is the target (`.xml` beside it when the lock asks for doc), refs built first, C# masks via `getFiles`, HERMETIC gate inside.

## Fingerprints of committed generated files

```fsharp
command "import" { do! Fingerprints.record "xake.fingerprints" "desc/a.json" ["a.fsproj"] }
command "build"  { do! Fingerprints.warn "xake.fingerprints" }   // warns on stale/hand-edited
"check" => Fingerprints.check "xake.fingerprints"                // fails instead
```

See [fingerprints.md](fingerprints.md).

## Filesets

```fsharp
let src = !! "src/**/*.fs"
let srcInCore = !! "**/*.fs" @@ "src/core"
```

## CLI invocation

Default target:

```bash
dotnet fsi build.fsx
```

Options and targets:

```bash
dotnet fsi build.fsx -- -- clean build test
```

Variable:

```bash
dotnet fsi build.fsx -- -- build -d Version=1.2.3
```

Dry run:

```bash
dotnet fsi build.fsx -- -- build --dryrun
```

