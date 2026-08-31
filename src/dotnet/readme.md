Xake is a make utility made for .NET on F# language. Xake is inspired by [shake](https://github.com/ndmitchell/shake) build tool.

See [Xake documentation](https://github.com/xakebuild/Xake/wiki/introduction) for more details.

## Prerequisites

The build sctipt expects dotnet 2.1.300+ to build the package. That version properly writes package metadata.
You will need [net46 Dev Pack](https://www.microsoft.com/net/download/thank-you/net46-developer-pack) to build for .net 4.6 target.

## Csc task

The simple script looks like:

```fsharp
#r "paket:
  nuget Xake ~> 1.1 prerelease
  nuget Xake.Dotnet ~> 1.1 prerelease //"

open Xake
open Xake.Dotnet

do xakeScript {
  rules [
    "main" <== ["helloworld.exe"]

    "helloworld.exe" ..> csc {src !!"helloworld.cs"}
  ]
}
```

This script compiles helloworld assembly from helloworld.cs file.
