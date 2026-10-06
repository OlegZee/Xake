#r "nuget: Xake, 3.5.0"
#r "nuget: Xake.Hermetic.Dotnet, 0.1"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do xakeScript {
    var "CSC_TOOLSET" "4.12.0"          // компилятор из пакета Microsoft.Net.Compilers.Toolset
    var "NETCORE_REF_VERSION" "8.0.31"  // targeting pack из пакета Microsoft.NETCore.App.Ref (PR #37)
    var  "NUGET_PACKAGES" ".nuget-packages"

    "main" <== ["hw.dll"]
    "hw.dll" ..> csc {
        targetfwk "net8.0"
        src !!"hw.cs"
        lock "hw.lock"
    }
    
    "greet" => trace Error "hello"
}