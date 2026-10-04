#r "nuget: Xake, 3.5.0"

open Xake
open Xake.Dotnet

do xakeScript {
    "main" <== ["hw.exe"]
    "hw.exe" ..> csc {
        src !!"hw.cs"
    }
    
    rule (PhonyRule ("greet", trace Message "hello"))
}