// Experiment 09, item d: `into` with a record pattern (separate file: it does not compile).
type Groups = { cfg: string; fwk: string; name: string }
let current = { cfg = "Release"; fwk = "net8.0"; name = "Core" }

type RuleR() =
    member _.Return x = async.Return x
    member _.Zero() = async.Zero()
    member _.Bind(m: Async<'a>, f) = async.Bind(m, f)
    member _.Delay f = async.Delay f
    member _.Combine(a, b) = async.Combine(a, b)
    member _.For(xs: seq<'a>, f: 'a -> Async<unit>) = async.For(xs, f)
    member _.Yield(x) = async.Return x
    [<CustomOperation("matched", MaintainsVariableSpaceUsingBind = true, AllowIntoPattern = true)>]
    member _.Matched(src: Async<'v>) : Async<Groups> = async { let! _ = src in return current }
let rule = RuleR()

rule {
    matched into { fwk = fwk; name = name }
    printfn "into record: %s %s" fwk name
} |> Async.RunSynchronously
