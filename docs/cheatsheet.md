# Xake cheatsheet

## Package reference

```fsharp
#r "nuget: Xake, 3.0.0"
open Xake
open Xake.Tasks
```

## Script entry point

```fsharp
do xakeScript {
	rules [
		"main" <== ["build"; "test"]
	]
}
```

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
rules [
	command "build" {
		do! sh "dotnet build src/core -c Release" {}
	}

	target "out/version.txt" {
		do! writeText "1.0.0"
	}

	targets ["out/a.dll"; "out/a.xml"] {
		do! sh "dotnet build src/core -c Release -o out" {}
	}
]
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

