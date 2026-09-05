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

// the target file is the output; `targetfwk` selects the reference assemblies
"hello.exe" ..> csc { targetfwk "net-4.6.2"; src !!"hello.cs"; grefs ["System.dll"] }
"app.exe"   ..> fsc { src !!"src/*.fs"; ref !!"bin/FSharp.Core.dll" }
"build"      => msbuild { buildfile "a.sln"; target "Rebuild"; prop ("Configuration","Release") }
"res"        => resgen { resources (resourceset { prefix "App"; files !!"**/*.resx" }) }
```

Script variables: `NETFX` (tool framework), `NETFX-TARGET` (default target framework), `FSCVER`.

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

