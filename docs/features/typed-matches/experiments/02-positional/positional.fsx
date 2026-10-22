// Experiment 02: non-type-provider options, written as a library on top of today's engine
// (no engine change). Run from this folder:  dotnet fsi positional.fsx
//   A. positional lambda binding by arity overloads (roadmap "Rules with arguments")
//   B. binding by lambda parameter NAMES via reflection, checked when the rule is created
//   C. a pattern value whose arity is in its type: Pattern2 with Path/Rule/All/Glob
#r "../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
open System.Text.RegularExpressions
open Xake

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-02")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }
let attempt name f = try f () with e -> printfn "%s -> ERROR: %s" name e.Message

let private tag = Regex(@"\((?'tag'\w+?)\:(?'mask'[^)]+)\)")
let groupsOf (p: string) = [ for m in tag.Matches p -> m.Groups.["tag"].Value ] |> List.distinct
let subst (p: string) (values: Map<string, string>) = tag.Replace(p, fun m -> values.[m.Groups.["tag"].Value])

let checkSameGroups (patterns: string list) =
    let g0 = groupsOf patterns.Head
    for p in patterns.Tail do
        if set (groupsOf p) <> set g0 then failwithf "pattern '%s' has groups %A, the first has %A" p (groupsOf p) g0
    g0

let mkRule patterns body =
    match patterns with [ p ] -> FileRule (p, body) | ps -> MultiFileRule (ps, body)

// ---------------------------------------------------------------------------------- A
type Rule =
    static member private Arity (patterns: string list) n =
        let gs = checkSameGroups patterns
        if gs.Length <> n then failwithf "pattern '%s' has %d groups %A, the lambda takes %d" patterns.Head gs.Length gs n
        gs
    static member file (pattern: string, body: string -> Recipe<ExecContext, unit>) =
        let gs = Rule.Arity [ pattern ] 1
        mkRule [ pattern ] (recipe { let! m = getRuleMatches () in do! body m.[gs.[0]] })
    static member file (pattern: string, body: string -> string -> Recipe<ExecContext, unit>) =
        let gs = Rule.Arity [ pattern ] 2
        mkRule [ pattern ] (recipe { let! m = getRuleMatches () in do! body m.[gs.[0]] m.[gs.[1]] })
    static member file (pattern: string, body: string -> string -> string -> Recipe<ExecContext, unit>) =
        let gs = Rule.Arity [ pattern ] 3
        mkRule [ pattern ] (recipe { let! m = getRuleMatches () in do! body m.[gs.[0]] m.[gs.[1]] m.[gs.[2]] })
    /// multi-file: groups tupled, outputs tupled (2 groups x 2 outputs shown)
    static member files (patterns: string * string, body: string * string -> string * string -> Recipe<ExecContext, unit>) =
        let p1, p2 = patterns
        let gs = Rule.Arity [ p1; p2 ] 2
        mkRule [ p1; p2 ] (recipe {
            let! m = getRuleMatches ()
            let! files = getTargetFiles ()
            let f = files |> List.map File.getFullName
            do! body (m.[gs.[0]], m.[gs.[1]]) (f.[0], f.[1]) })

do xake (opts [ "out/net8.0/Core.dll" ]) {
    rules [ Rule.file ("out/(fwk:*)/(lib:*).dll", fun fwk lib -> recipe { printfn "A: fwk=%s lib=%s" fwk lib }) ]
}
// swapped names compile and bind wrongly: positional binding does not see names
do xake (opts [ "out/net8.0/Core.dll" ]) {
    rules [ Rule.file ("out/(fwk:*)/(lib:*).dll", fun lib fwk -> recipe { printfn "A swapped: lib=%s fwk=%s  (wrong, silently)" lib fwk }) ]
}
attempt "A arity" (fun () -> Rule.file ("out/(fwk:*)/(lib:*).dll", fun fwk -> recipe { () }) |> ignore)
do xake (opts [ "out/net8.0/Core.xml" ]) {
    rules [ Rule.files (("out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml"),
                        fun (fwk, lib) (dll, xml) -> recipe { printfn "A multi: %s %s -> %s, %s" fwk lib (System.IO.Path.GetFileName dll) (System.IO.Path.GetFileName xml) }) ]
}

// ---------------------------------------------------------------------------------- B
/// Parameter names of an F# lambda, read from the closure class. A curried lambda of 2+
/// args compiles to an OptimizedClosures.FSharpFunc subclass with Invoke(a, b, ...).
let lambdaParamNames (f: obj) =
    let invokes = f.GetType().GetMethods() |> Array.filter (fun m -> m.Name = "Invoke" && m.DeclaringType = f.GetType())
    let best = invokes |> Array.maxBy (fun m -> m.GetParameters().Length)
    [ for p in best.GetParameters() -> p.Name ]

type ByName =
    static member file (pattern: string, body: string -> string -> Recipe<ExecContext, unit>) =
        let names = lambdaParamNames body
        let gs = groupsOf pattern
        let missing = names |> List.filter (fun n -> not (List.contains n gs))
        if not missing.IsEmpty then failwithf "pattern '%s' has no group(s) %A (it has %A)" pattern missing gs
        mkRule [ pattern ] (recipe { let! m = getRuleMatches () in do! body m.[names.[0]] m.[names.[1]] })

printfn "B: parameter names seen = %A" (lambdaParamNames (fun (fwk: string) (lib: string) -> recipe { () }))
do xake (opts [ "out/net8.0/Core.dll" ]) {
    rules [ ByName.file ("out/(fwk:*)/(lib:*).dll", fun lib fwk -> recipe { printfn "B swapped order: lib=%s fwk=%s  (right, by name)" lib fwk }) ]
}
attempt "B typo" (fun () -> ByName.file ("out/(fwk:*)/(lib:*).dll", fun fkw lib -> recipe { () }) |> ignore)
// a function value instead of a lambda: names are whatever the function declared, or compiler-made
let handler (framework: string) (library: string) = recipe { () }
attempt "B named function" (fun () -> ByName.file ("out/(fwk:*)/(lib:*).dll", handler) |> ignore)

// ---------------------------------------------------------------------------------- C
/// A pattern value with its arity in its type. Group count checked when created (script
/// start); every use (Path/Rule/All) is arity-checked by the compiler; names are positional.
type Pattern2(patterns: string list) =
    let gs = checkSameGroups patterns
    do if gs.Length <> 2 then failwithf "Pattern2: '%s' has %d groups %A" patterns.Head gs.Length gs
    let map a b = Map.ofList [ gs.[0], a; gs.[1], b ]
    member _.Text = patterns.Head
    member _.Path (a: string, b: string) = subst patterns.Head (map a b)
    member _.Paths (a: string, b: string) = [ for p in patterns -> subst p (map a b) ]
    member x.All (a: string list, b: string list) = [ for a in a do for b in b do yield! x.Paths (a, b) ]
    member _.Glob (?a: string, ?b: string) =
        let bound = [ gs.[0], a; gs.[1], b ] |> List.choose (fun (k, v) -> v |> Option.map (fun v -> k, v)) |> Map.ofList
        [ for p in patterns -> tag.Replace(p, fun m -> defaultArg (bound.TryFind m.Groups.["tag"].Value) m.Groups.["mask"].Value) ]
    member _.Rule (body: string -> string -> Recipe<ExecContext, unit>) =
        mkRule patterns (recipe { let! m = getRuleMatches () in do! body m.[gs.[0]] m.[gs.[1]] })
let pattern2 p = Pattern2 [ p ]
let patterns2 ps = Pattern2 ps

let Dll = pattern2 "out/(fwk:*)/(lib:*).dll"
let Lib = patterns2 [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ]
printfn "C: Dll.Path = %s ; Lib.All = %A ; Lib.Glob(b=Xake) = %A" (Dll.Path ("net8.0", "Core")) (Lib.All ([ "net462"; "net8.0" ], [ "Xake" ])) (Lib.Glob (b = "Xake"))
do xake (opts [ Dll.Path ("net8.0", "Core") ]) {
    rules [ Dll.Rule (fun fwk lib -> recipe { printfn "C: fwk=%s lib=%s" fwk lib }) ]
}
attempt "C wrong arity" (fun () -> pattern2 "out/(fwk:*)/(cfg:*)/(lib:*).dll" |> ignore)
attempt "C mismatched multi" (fun () -> patterns2 [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml" ] |> ignore)

// ---------------------------------------------------------------------------------- D
/// A record declares the groups; the pattern string is checked against the record's fields
/// when the Pattern is created (script start). Access and generation are typed by the record.
open Microsoft.FSharp.Reflection
type Pattern<'r>(patterns: string list) =
    let fields = FSharpType.GetRecordFields typeof<'r>
    let names = [ for f in fields -> f.Name ]
    let gs = checkSameGroups patterns
    do if set gs <> set names then
        failwithf "Pattern<%s>: '%s' has groups %A, the record has fields %A" typeof<'r>.Name patterns.Head gs names
    let toMap (r: 'r) = Map.ofList [ for f in fields -> f.Name, string (f.GetValue r) ]
    let ofMap (m: Map<string, string>) = FSharpValue.MakeRecord(typeof<'r>, [| for n in names -> box m.[n] |]) :?> 'r
    member _.Path (r: 'r) = subst patterns.Head (toMap r)
    member _.Paths (r: 'r) = [ for p in patterns -> subst p (toMap r) ]
    member _.Rule (body: 'r -> Recipe<ExecContext, unit>) =
        mkRule patterns (recipe { let! m = getRuleMatches () in do! body (ofMap m) })

type DllM = { fwk: string; lib: string }
let DllR = Pattern<DllM> [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ]
printfn "D: Paths = %A" (DllR.Paths { fwk = "net8.0"; lib = "Core" })
printfn "D: need list = %A" [ for f in [ "net462"; "net8.0" ] do yield! DllR.Paths { fwk = f; lib = "Xake" } ]
do xake (opts [ "out/net8.0/Core.xml" ]) {
    rules [ DllR.Rule (fun m -> recipe { printfn "D: fwk=%s lib=%s" m.fwk m.lib }) ]
}
type BadM = { fwk: string; library: string }
attempt "D record/pattern mismatch" (fun () -> Pattern<BadM> [ "out/(fwk:*)/(lib:*).dll" ] |> ignore)
