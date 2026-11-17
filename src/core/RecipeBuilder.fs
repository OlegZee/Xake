namespace Xake

/// Internal recipe computation primitives.
module internal A =
    open System.Threading.Tasks
    /// Unwraps a Recipe to its underlying function.
    let runAction (Recipe r) = r
    /// Wraps a value in a Recipe that passes through the build state unchanged.
    let returnF a = Recipe (fun (s,_) -> async {return (s,a)})

    /// Monadic bind for Recipe: runs m, then feeds the result into f.
    let bindF m f = Recipe (fun (s, a) -> async {
        let! (s', b) = runAction m (s, a) in
        return! runAction (f b) (s', a)
        })
    /// Binds an Async value into a Recipe continuation.
    let bindA m f = Recipe (fun (s, r) -> async {
        let! a = m in
        return! runAction (f a) (s, r)
        })
    /// Binds a Task value into a Recipe continuation.
    let bindT (m: Task<'a>) f = Recipe (fun (s, r) -> async {
        let! a = m |> Async.AwaitTask in
        return! runAction (f a) (s, r)
        })
    /// Identity for Recipe results.
    let resultFromF m = m

    /// Applies f to a value wrapped in a Recipe.
    let callF f a = bindF (returnF a) f
    /// Delays a Recipe computation by wrapping it in a unit thunk.
    let delayF f = callF f ()

    /// A Recipe that completes immediately with unit.
    let doneF = Recipe (fun (s,_) -> async {return (s,())})

    /// Discards the result of a Recipe, returning unit.
    let ignoreF p = bindF p (fun _ -> doneF)
    /// Sequences two Recipes, discarding the result of the first.
    let combineF f g = bindF f (fun _ -> g)

    /// Repeats prog while guard returns true.
    let rec whileF guard prog =
        if not (guard()) then 
            doneF
        else 
            (fun () -> whileF guard prog) |> bindF prog

    /// Wraps a Recipe body with exception handling, routing exceptions to h.
    let tryWithF body h = // (body:Recipe<'a,'b>) (h: 'exc ->Recipe<'a,'b>) : Recipe<'a,'b> =
        fun x -> async {
            try
                return! runAction body x
            with e ->
                return! runAction (h e) x
        } |> Recipe

    /// Wraps a Recipe body with a finally-style compensation action.
    let tryFinallyF body comp = // (body:Recipe<'a,'b>) -> (comp: unit -> unit) -> Recipe<'a,'b> =
        fun x -> async {
            try
                return! runAction body x
            finally
                do comp()
        } |> Recipe

    /// Executes body with a disposable resource, disposing it when done.
    let usingF (r:'T :> System.IDisposable) body =
        tryFinallyF (body r) (fun () -> r.Dispose())

    /// Iterates over a sequence, executing prog for each element.
    let forF (e: seq<_>) prog =
        usingF (e.GetEnumerator()) (fun e ->
            whileF
                (fun () -> e.MoveNext())
                ((fun () -> prog e.Current) |> delayF)
        )

//    [<CustomOperation("step")>]
//    member this.Step(m, name) =
//        printfn "STEP %A %A" m name
//        ()

[<AutoOpen>]
module Builder =
    open A
    /// Computation expression builder for recipe { ... } blocks.
    type RecipeBuilder() =
        member this.Return(c) = returnF c
        member this.Zero()    = doneF
        member this.Delay(f)  = delayF f

        // binds both monadic and for async computations
        member this.Bind(m, f) = bindF m f
        member this.Bind(m, f) = bindA m f
        member this.Bind(m, f) = bindT m f
        member this.Bind((), f) = bindF (returnF()) f

        member this.Combine(f, g) = combineF f g
        member this.While(guard, body) = whileF guard body
        member this.For(seq, f) = forF seq f
        member this.TryWith(body, handler) = tryWithF body handler
        member this.TryFinally(body, compensation) = tryFinallyF (body) compensation
        member this.Using(disposable:#System.IDisposable, body) = usingF disposable body

        // --- the rule header (Xake 5.0, docs/rule-header.md) ---
        // The header has its own type: `Yield` starts it, every operation maps it, `For` joins it
        // with the body into a `Headed`. An operation after `let!`/`do!` then does not type-check,
        // and a `Headed` cannot be bound by any of the `Bind` overloads.

        /// Starts a rule header.
        member _.Yield(()) = RuleHeader.Empty
        /// `also "out/(n:*).pdb"`: another output of the rule.
        [<CustomOperation("also")>]
        member _.Also(h: RuleHeader, pattern: string) = { h with Also = h.Also @ [ pattern ] }
        /// `needs "obj/(n:*).rsp"`: a static input, built before the body.
        [<CustomOperation("needs")>]
        member _.Needs(h: RuleHeader, pattern: string) = { h with Needs = h.Needs @ [ pattern ] }
        /// `needs ["a"; "b"]`: static inputs; all `needs` of a rule are one unordered set.
        [<CustomOperation("needs")>]
        member _.Needs(h: RuleHeader, patterns: string list) = { h with Needs = h.Needs @ patterns }
        /// `requires lock`: one unit of the resource, held by the engine while the body runs.
        [<CustomOperation("requires")>]
        member _.Requires(h: RuleHeader, resource: Resource) = { h with Requires = h.Requires @ [ resource, 1 ] }
        /// `requires (lock, n)`: n units of the resource, held while the body runs.
        [<CustomOperation("requires")>]
        member _.Requires(h: RuleHeader, (resource: Resource, units: int)) = { h with Requires = h.Requires @ [ resource, units ] }
        /// `noContentCutoff`: every rebuild of this rule's outputs is a change for its dependents.
        [<CustomOperation("noContentCutoff")>]
        member _.NoContentCutoff(h: RuleHeader) = { h with ContentCutoff = Some false }
        /// `contentCutoff`: the default, for symmetry.
        [<CustomOperation("contentCutoff")>]
        member _.ContentCutoff(h: RuleHeader) = { h with ContentCutoff = Some true }
        /// `including h`: the items of a shared header (`ruleHeader ["n"] { .. }`), merged in place.
        [<CustomOperation("including")>]
        member _.Including(h: RuleHeader, shared: SharedHeader) =
            RuleHeader.Merge (h, { shared.Header with Included = [ shared.Description, shared.DeclaredGroups ] })
        /// Joins the header and the body; the body is delayed, so loading the script never runs it.
        member _.For(h: RuleHeader, body: unit -> Recipe<'c, unit>) : Headed<'c> = Headed (h, delayF body)
        /// A rule with a header and no body.
        member _.Delay(f: unit -> RuleHeader) : Headed<'c> = Headed (f (), doneF)
        member _.Delay(f: unit -> Headed<'c>) : Headed<'c> = f ()

        [<CompilerMessage("The rule header (also, needs, requires, noContentCutoff) must come before the body: move it above the first let!/do!.", 3095, IsError = true)>]
        member _.Bind(_: Recipe<'a, 'b>, _: 'b -> RuleHeader) : RuleHeader = failwith "unreachable"
        [<CompilerMessage("A recipe with a header (also, needs, requires, ...) is a rule body: give it to a rule (..>, *..>, target, X.rule); it cannot be used inside another recipe.", 3095, IsError = true)>]
        member _.Bind(_: Headed<'c>, _: unit -> Recipe<'c, 'b>) : Recipe<'c, 'b> = failwith "unreachable"
    
    /// `ruleHeader ["n"] { needs ..; requires .. }`: a header shared between rules, merged in with
    /// `including h`. The list names the groups of the including rule the header uses.
    type RuleHeaderBuilder(groups: string list) =
        member _.Yield(()) = RuleHeader.Empty
        member _.Zero() = RuleHeader.Empty
        [<CustomOperation("also")>]
        member _.Also(h: RuleHeader, pattern: string) = { h with Also = h.Also @ [ pattern ] }
        [<CustomOperation("needs")>]
        member _.Needs(h: RuleHeader, pattern: string) = { h with Needs = h.Needs @ [ pattern ] }
        [<CustomOperation("needs")>]
        member _.Needs(h: RuleHeader, patterns: string list) = { h with Needs = h.Needs @ patterns }
        [<CustomOperation("requires")>]
        member _.Requires(h: RuleHeader, resource: Resource) = { h with Requires = h.Requires @ [ resource, 1 ] }
        [<CustomOperation("requires")>]
        member _.Requires(h: RuleHeader, (resource: Resource, units: int)) = { h with Requires = h.Requires @ [ resource, units ] }
        [<CustomOperation("noContentCutoff")>]
        member _.NoContentCutoff(h: RuleHeader) = { h with ContentCutoff = Some false }
        [<CustomOperation("contentCutoff")>]
        member _.ContentCutoff(h: RuleHeader) = { h with ContentCutoff = Some true }
        member _.Run(h: RuleHeader) : SharedHeader = Rule.share groups h

    /// `ruleHeader ["n"] { .. }`: a header shared between rules (see `RuleHeaderBuilder`).
    let ruleHeader (groups: string list) = RuleHeaderBuilder groups

    /// Backward-compatible alias for the recipe builder.
    let action = RecipeBuilder()
    /// The recipe computation expression builder instance.
    let recipe = RecipeBuilder()
