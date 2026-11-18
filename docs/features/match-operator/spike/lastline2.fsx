#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
let (?) (m: Map<string,string>) (name: string) = Map.find name m
type R3() =
    inherit RecipeBuilder()
    member _.Yield(f: Map<string,string> -> Recipe<ExecContext,unit>) =
        recipe { let! m = getRuleMatches () in do! f m }
let recipe3 = R3()
let f3 = recipe3 {
    do! trace Message "before"
    yield fun m -> recipe { do! writeText m?n }
}
let f4 = recipe {
    do! trace Message "before"
    fun (m: Map<string,string>) -> recipe { do! writeText m?n }
}
