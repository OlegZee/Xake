#r "nuget: Xake, 3.5.0"
#r "nuget: Xake.Hermetic.Dotnet, 0.1"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do xakeScript {
    "main" <== ["hw.dll"]
    "hw.dll" ..> csc {
        targetfwk "net8.0"
        src !!"hw.cs"
        lock "hw.lock"
    }
    
    "greet" => trace Error "hello"
}