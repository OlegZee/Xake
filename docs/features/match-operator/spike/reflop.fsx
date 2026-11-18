#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
/// parameter names of an F# closure: the widest Invoke overload
let closureNames (f: obj) =
    f.GetType().GetMethods()
    |> Array.filter (fun m -> m.Name = "Invoke" && m.DeclaringType = f.GetType())
    |> Array.map (fun m -> m.GetParameters() |> Array.map (fun p -> p.Name) |> List.ofArray)
    |> Array.sortByDescending List.length |> Array.tryHead |> Option.defaultValue []
let groupsOf (pattern: string) =
    System.Text.RegularExpressions.Regex.Matches(pattern, @"\((\w+):") |> Seq.cast<System.Text.RegularExpressions.Match> |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq
let check pattern names =
    for n in names do
        if not (Set.contains n (groupsOf pattern)) then failwithf "rule %s: parameter '%s' is not a group of the pattern" pattern n
type RB = RB with
    static member ($) (RB, body: Recipe<ExecContext, unit>) = (fun (_: string) -> ()), (fun _ -> body)
    static member ($) (RB, f: string -> Recipe<ExecContext, unit>) =
        let ns = closureNames f
        (fun p -> check p ns), (fun _ -> recipe { let! m = getRuleMatches () in do! f m.[ns.[0]] })
    static member ($) (RB, f: string -> string -> Recipe<ExecContext, unit>) =
        let ns = closureNames f
        (fun p -> check p ns), (fun _ -> recipe { let! m = getRuleMatches () in do! f m.[ns.[0]] m.[ns.[1]] })
let inline ( ..>> ) (pattern: string) body : ExecContext Rule =
    let chk, mk = RB $ body
    chk pattern
    FileRule ([ pattern ], RuleHeader.Empty, mk ())
let build cfg n = recipe { do! writeText $"named {cfg} {n}" }
let rs = [
    "out/(cfg:*)/(n:*).two" ..>> fun cfg n -> recipe { do! writeText $"two {cfg} {n}" }
    "out/(n:*)/(cfg:*).rev" ..>> fun cfg n -> recipe { do! writeText $"rev {cfg} {n}" }   // order of the pattern does not matter
    "out/(cfg:*)/(n:*).named" ..>> build
    "out/(n:*).one" ..>> fun n -> recipe { do! writeText $"one {n}" } ]
try ("out/(cfg:*)/(n:*).bad" ..>> fun cfg nme -> recipe { () }) |> ignore with e -> printfn "load-time: %s" e.Message
try System.IO.Directory.Delete("out", true) with _ -> ()
let ts = [ "out/dbg/b.two"; "out/b/dbg.rev"; "out/dbg/b.named"; "out/a.one" ]
xake { ExecOptions.Default with Targets = ts; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } { rules rs }
for f in ts do printfn "%s" (System.IO.File.ReadAllText f)
