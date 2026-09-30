# Xake.Hermetic.Dotnet

Reproducible .NET builds for [Xake](https://github.com/OlegZee/Xake): lock files, package
restore, CycloneDX SBOMs, verification of built binaries, deterministic packing and signing.

The package builds on the `csc {}` task of `Xake` (`Xake.Dotnet`) and adds what a build needs to
be repeatable and auditable: a lock file that records exactly what the compiler is handed, with
the SHA-256 of every reference, analyzer and the compiler itself; a restore of the packages that
lock names; an SBOM generated from the lock; and the release steps after the compile. It is a
0.x preview: names may still change between minor versions.

## Usage

```fsharp
#r "nuget: Xake"
#r "nuget: Xake.Hermetic.Dotnet"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

Compile gated by a lock (recorded on the first build, enforced afterwards):

```fsharp
"out/app.dll" ..> csc { targetfwk "net472"; src !!"src/*.cs"; lock "locks/app.json" }
```

The same with the resolved compilation in hand:

```fsharp
"out/app.dll" ..> recipe {
    let! c = csc { targetfwk "net472"; src !!"src/*.cs"; resolve }
    do! Lock.build "locks/app.json" c
}
```

Restore the packages a lock names, checked against their recorded sha512:

```fsharp
let! doc = Lock.load "locks/app.json"
do! Lock.restore Restore.Options.Default doc
```

An SBOM for an assembly built from a lock entry:

```fsharp
let bom = Sbom.forAssembly (DotNetFwk.nugetRoot ()) (Lock.entry "app" doc) "out/app.dll"
File.WriteAllText ("out/app.cdx.json", Sbom.cycloneDx bom)
```

A signing rule (the signer is yours; `Sign.fakeSigner` exists for tests only):

```fsharp
sign {
    target "signed/(name:*).dll"
    input (fun t -> "out" </> Path.GetFileName t)
    certificate (Sign.Thumbprint "...")
    signer mySigner
}
```

See the [documentation](https://github.com/OlegZee/Xake/tree/dev/docs/hermetic) for the lock
format, `Project.import` (locks from existing `.csproj` files), verification and packing.
