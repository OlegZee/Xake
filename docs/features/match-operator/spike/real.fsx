#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks

// Option A: prefix operator -> Recipe<_, string>, strict on unknown names
let (~~) (name: string) : Recipe<ExecContext, string> = recipe {
    let! m = getRuleMatches ()
    match Map.tryFind name m with
    | Some v -> return v
    | None ->
        let known = m |> Map.toList |> List.map fst |> String.concat ", "
        return failwithf "no group '%s' in the rule match (groups: %s)" name known }

// Option B: plain string from an ambient AsyncLocal; the engine would set it per task.
// Here a wrapper stands in for the engine.
let current = System.Threading.AsyncLocal<Map<string,string>>()
let (!?) (name: string) : string =
    match current.Value with
    | m when obj.ReferenceEquals(m, null) -> failwithf "!?\"%s\": only inside a rule's recipe" name
    | m -> match Map.tryFind name m with Some v -> v | None -> failwithf "no group '%s'" name
let withMatches (body: Recipe<ExecContext, unit>) = recipe {
    let! m = getRuleMatches ()
    current.Value <- m
    do! body }

// Option C: dynamic lookup on the map
let (?) (m: Map<string,string>) (name: string) =
    match Map.tryFind name m with Some v -> v | None -> failwithf "no group '%s'" name

let errors = System.Collections.Concurrent.ConcurrentBag<string>()
let ns = [ for i in 1 .. 40 -> sprintf "m%02d" i ]
try System.IO.Directory.Delete("out", true) with _ -> ()
try System.IO.File.Delete ".xake" with _ -> ()

xake { ExecOptions.Default with Targets = [ for n in ns -> "out/" + n + ".a"; ] @ [ for n in ns -> "out/" + n + ".c" ]; Threads = 8; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } {
    rules [
        "obj/(n:*).rsp" ..> recipe {
            let! n = ~~"n"                                   // A
            do! Async.Sleep (System.Random.Shared.Next 30)
            do! writeText n }
        "out/(n:*).a" ..> recipe {
            let! n = ~~"n"
            do! need [ "obj/" + n + ".rsp" ]                 // A, after a need
            do! writeText n }
        "out/(n:*).c" ..> withMatches (recipe {
            do! need [ "obj/" + !?"n" + ".rsp" ]             // B, no let!
            do! Async.Sleep (System.Random.Shared.Next 30)
            let! m = getRuleMatches ()
            if !?"n" <> m?n then errors.Add (sprintf "B leaked: %s vs %s" !?"n" m?n)   // B after awaits, C
            do! writeText !?"n" })
    ]
}
printfn "built; B mismatches: %d" errors.Count

// unknown group, A
try
  xake { ExecOptions.Default with Targets = ["out/x.bad"]; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } {
    rules [ "out/(n:*).bad" ..> recipe { let! v = ~~"name" in do! writeText v } ] }
with e -> printfn "A unknown group -> %s" (e.ToString().Split('\n').[0])
// outside a rule, B
try printfn "%s" !?"n" with e -> printfn "B outside -> %s" e.Message
