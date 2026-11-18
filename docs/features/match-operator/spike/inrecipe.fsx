#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
let invokeParams (o: obj) =
    o.GetType().GetMethods()
    |> Array.filter (fun mi -> mi.Name = "Invoke" && mi.DeclaringType = o.GetType())
    |> Array.maxBy (fun mi -> mi.GetParameters().Length)
let applyAll (o: obj) (m: Map<string,string>) : obj =
    let inv = invokeParams o
    inv.Invoke(o, inv.GetParameters() |> Array.map (fun p -> box m.[p.Name]))
type Group = Group
/// `let! n = group`: the variable's name is the group's name
let group = Group
let (|G|) (name: string) (m: Map<string,string>) = m.[name]
type R2() =
    inherit RecipeBuilder()
    member _.Bind(Group, f: string -> Recipe<ExecContext,'b>) : Recipe<ExecContext,'b> =
        let name = (invokeParams (box f)).GetParameters().[0].Name
        recipe { let! m = getRuleMatches () in let! x = f m.[name] in return x }
    member _.MergeSources(Group, Group) = (Group, Group)
    member _.Bind((Group, Group), f: string * string -> Recipe<ExecContext,'b>) : Recipe<ExecContext,'b> =
        let ps = (invokeParams (box f)).GetParameters() |> Array.map (fun p -> p.Name)
        printfn "  and! continuation params: %A" ps
        recipe { return failwith "n/a" }
    /// `return! fun cfg n -> recipe { .. }`: the last line takes the groups by parameter name
    member _.ReturnFrom(f: string -> 'R) : Recipe<ExecContext,unit> =
        recipe { let! m = getRuleMatches () in do! (applyAll (box f) m :?> Recipe<ExecContext,unit>) }
let recipe2 = R2()
let rs = [
    "out/(cfg:*)/(n:*).a" ..> recipe2 {
        noContentCutoff
        let! cfg = group
        let! n = group
        do! writeText $"a {cfg} {n}" }
    "out/(cfg:*)/(n:*).b" ..> recipe {
        let! (G "cfg" cfg & G "n" n) = getRuleMatches ()
        do! writeText $"b {cfg} {n}" }
    "out/(cfg:*)/(n:*).c" ..> recipe2 {
        needs "out/(cfg:*)/(n:*).a"
        return! fun n cfg -> recipe { do! writeText $"c {cfg} {n}" } } ]
let probe = recipe2 {
    let! cfg = group
    and! n = group
    return () }
try System.IO.Directory.Delete("out", true) with _ -> ()
let ts = [ "out/dbg/x.a"; "out/dbg/x.b"; "out/rel/y.c" ]
xake { ExecOptions.Default with Targets = ts; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } { rules rs }
for f in ts do printfn "%s" (System.IO.File.ReadAllText f)
