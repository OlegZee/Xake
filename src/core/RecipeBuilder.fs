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
    
    /// Backward-compatible alias for the recipe builder.
    let action = RecipeBuilder()
    /// The recipe computation expression builder instance.
    let recipe = RecipeBuilder()
