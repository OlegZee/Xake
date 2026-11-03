// `csc` comes from the Xake.Dotnet package (its own repository, OlegZee/Xake.Dotnet), which
// brings the engine, the Xake package, with it.
#r "nuget: Xake.Dotnet"

open Xake
open Xake.Dotnet

do xakeScript {
    "main" <== ["hw.dll"]
    // the runtimeconfig is declared next to the dll, so `dotnet hw.dll` can start it
    ["hw.dll"; "hw.runtimeconfig.json"] *..> csc {
        src !!"hw.cs"
    }

    rule (PhonyRule ("greet", trace Message "hello"))
}
