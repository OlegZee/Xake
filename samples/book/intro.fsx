#r "nuget: Xake, 3.5.0"

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
