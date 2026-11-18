#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
let (?) (m: Map<string,string>) (name: string) = Map.find name m
type R2() =
    inherit RecipeBuilder()
    /// `return! fun m -> recipe { .. }`: the rest of the body takes the rule match
    member _.ReturnFrom(f: Map<string,string> -> Recipe<ExecContext,'b>) =
        recipe { let! m = getRuleMatches () in let! x = f m in return x }
    member _.ReturnFrom(r: Recipe<ExecContext,'b>) = r
let recipe2 = R2()
// F1: no header
let f1 = recipe2 {
    do! trace Message "before"
    return! fun m -> recipe {
        do! trace Message "n=%s" m?n
        do! writeText m?n }
}
// F2: header, then return! fun m
let f2 = recipe2 {
    noContentCutoff
    return! fun m -> recipe {
        do! writeText m?n }
}
try System.IO.Directory.Delete("out", true) with _ -> ()
xake { ExecOptions.Default with Targets = [ "out/a.one"; "out/b.two" ]; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } {
    rules [ "out/(n:*).one" ..> f1; "out/(n:*).two" ..> f2 ] }
printfn "%s %s" (System.IO.File.ReadAllText "out/a.one") (System.IO.File.ReadAllText "out/b.two")
