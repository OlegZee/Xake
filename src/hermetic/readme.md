# Xake.Hermetic.Dotnet

Reproducible .NET builds for [Xake](https://github.com/OlegZee/Xake): lock files, package
restore, CycloneDX SBOMs, verification of built binaries, deterministic packing and signing.

The package builds on the `csc {}` and `fsc {}` tasks of `Xake` (`Xake.Dotnet`; Xake 3.6.0.24
or later, below 3.7) and adds what a build needs to be repeatable and auditable: a lock file that records
exactly what the compiler is handed, with the SHA-256 of every reference, analyzer and the
compiler itself; a restore of the packages that lock names; an SBOM generated from the lock; and
the release steps after the compile. C# and F# alike: 0.2 locks F# compilations, from `fsc {}`
or from an imported `.fsproj`. With `HERMETIC=on` (the base's script variable) a lock that
names anything outside the checkout and the build's package folder, other than an exactly
pinned .NET SDK, is neither written nor replayed. It is a 0.x preview: names may still change between minor
versions.

## Usage

```fsharp
#r "nuget: Xake, 3.6.0.24"
#r "nuget: Xake.Hermetic.Dotnet, 0.2.0"

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
    do! Lock.build "locks/app.json" (Lock.Compilation.Csc c)
}
```

An F# library, gated the same way (the compiler is the SDK's `fsc.dll`: pin the SDK in
`global.json`):

```fsharp
"out/lib.dll" ..> fsc { targetfwk "net8.0"; src !!"src/*.fs"; lock "locks/lib.json" }
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
format, `Project.import` (locks from existing `.csproj` and `.fsproj` files), verification and
packing.
