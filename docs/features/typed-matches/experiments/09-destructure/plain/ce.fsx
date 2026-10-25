// Experiment 09, item d: can a computation-expression builder introduce names by itself?
// A toy builder over `Async` with a custom operation. `dotnet fsi ce.fsx` (both SDKs) -> output-ce.txt.
// Only patterns written by the user bind names (let!, use!, and!, match!, for, `into`), so the
// question is only whether `into` reads better than `let!`.
type Groups = { cfg: string; fwk: string; name: string }
let current = { cfg = "Release"; fwk = "net8.0"; name = "Core" }

type RuleB() =
    member _.Return x = async.Return x
    member _.Zero() = async.Zero()
    member _.Bind(m: Async<'a>, f) = async.Bind(m, f)
    member _.Delay f = async.Delay f
    member _.Combine(a, b) = async.Combine(a, b)
    member _.For(xs: seq<'a>, f: 'a -> Async<unit>) = async.For(xs, f)
    member _.Yield(x) = async.Return x
    /// `matched into <pattern>`: a custom operation whose result is bound with a user-written pattern
    [<CustomOperation("matched", MaintainsVariableSpaceUsingBind = true, AllowIntoPattern = true)>]
    member _.Matched(src: Async<'v>) : Async<string * string * string> =
        async { let! _ = src in return current.cfg, current.fwk, current.name }
let rule = RuleB()

// 1. `into` with a tuple pattern
rule {
    matched into (cfg, fwk, name)
    printfn "into tuple: %s %s %s" cfg fwk name
} |> Async.RunSynchronously

// 2. `into`, then ordinary monadic code
rule {
    matched into (cfg, fwk, name)
    let! x = async.Return 42
    printfn "into tuple, then let!: %s %d" fwk x
} |> Async.RunSynchronously

// 3. without `into` the operation's result REPLACES the variable space (the names the user bound
//    before it): a custom operation can keep names or rebind the user's pattern, never add one.
//    Here `before` would be the tuple; using it as a string is FS0001 -- left commented out:
// rule {
//     let! before = async.Return "before"
//     matched
//     printfn "%s" before        // FS0001: expected 'string' but is a tuple 'string * string * string'
// } |> Async.RunSynchronously
// (`into` with a record pattern: ce-record.fsx)
