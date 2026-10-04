#r "nuget: Xake, 3.5.0"

open Xake
open Xake.Dotnet

do xakeScript {
    "main" <== ["hw.dll"]
    "hw.dll" ..> csc {
        src !!"hw.cs"
    }
    
    "greet" => trace Error "hello"
}