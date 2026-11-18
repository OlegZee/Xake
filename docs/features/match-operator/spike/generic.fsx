#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
let invokeNames (o: obj) =
    o.GetType().GetMethods()
    |> Array.filter (fun m -> m.Name = "Invoke" && m.DeclaringType = o.GetType())
    |> Array.map (fun m -> m.GetParameters() |> Array.map (fun p -> p.Name) |> List.ofArray)
    |> Array.sortByDescending List.length |> Array.tryHead |> Option.defaultValue []
let groupsOf (pattern: string) =
    System.Text.RegularExpressions.Regex.Matches(pattern, @"\((\w+):") |> Seq.cast<System.Text.RegularExpressions.Match> |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq
/// call the widest Invoke of a closure with the group values, by parameter name
let applyAll (o: obj) (m: Map<string,string>) : obj =
    let inv =
        o.GetType().GetMethods()
        |> Array.filter (fun mi -> mi.Name = "Invoke" && mi.DeclaringType = o.GetType())
        |> Array.maxBy (fun mi -> mi.GetParameters().Length)
    inv.Invoke(o, inv.GetParameters() |> Array.map (fun p -> box m.[p.Name]))
/// no SRTP: any body, dispatched when the script loads
let ( ..>> ) (pattern: string) (body: 'B) : ExecContext Rule =
    match box body with
    | :? Recipe<ExecContext, unit> as r -> FileRule ([ pattern ], RuleHeader.Empty, r)
    | o when Microsoft.FSharp.Reflection.FSharpType.IsFunction (o.GetType()) ->
        let ns = invokeNames o
        for n in ns do if not (Set.contains n (groupsOf pattern)) then failwithf "rule %s: parameter '%s' is not a group of the pattern" pattern n
        FileRule ([ pattern ], RuleHeader.Empty, recipe { let! m = getRuleMatches () in do! (applyAll o m :?> Recipe<ExecContext, unit>) })
    | o -> failwithf "rule %s: the body is a %s, not a recipe or a function of the groups" pattern (o.GetType().Name)
let build cfg n = recipe { do! writeText $"named {cfg} {n}" }
let rs = [
    "out/(n:*).one"                 ..>> fun n -> recipe { do! writeText $"one {n}" }
    "out/(cfg:*)/(n:*).two"         ..>> fun cfg n -> recipe { do! writeText ("two " + cfg + " " + n) }
    "out/(a:*)/(b:*)/(c:*).three"   ..>> fun c a b -> recipe { do! writeText $"three {a} {b} {c}" }
    "out/(cfg:*)/(n:*).named"       ..>> build
    "out/plain.txt"                 ..>> recipe { do! writeText "plain" } ]
try ("out/(cfg:*)/(n:*).bad" ..>> fun cfg nme -> recipe { () }) |> ignore with e -> printfn "load-time: %s" e.Message
try System.IO.Directory.Delete("out", true) with _ -> ()
let ts = [ "out/a.one"; "out/dbg/b.two"; "out/x/y/z.three"; "out/dbg/b.named"; "out/plain.txt" ]
xake { ExecOptions.Default with Targets = ts; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } { rules rs }
for f in ts do printfn "%s" (System.IO.File.ReadAllText f)
