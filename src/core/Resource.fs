namespace Xake

open Xake.WorkerPool

/// Internal message protocol for the resource agent.
type private ResMsg =
    | Acquire of int * AsyncReplyChannel<unit>
    | Release of int
    | Resize of int

/// A named, concurrency-limited resource modeled on Shake's `Resource`.
/// A bounded resource grants at most `Limit` units concurrently; an unbounded
/// resource imposes no limit (acquire/release are no-ops). The scheduler honors
/// the limit by yielding the CPU slot while a recipe waits to acquire units, so a
/// blocked holder never pins a worker thread.
type Resource =
    private { Name: string; mutable Limit: int option; Agent: Agent<ResMsg> option }

    /// The resource name (for diagnostics).
    member this.ResourceName = this.Name

    interface System.IDisposable with
        member this.Dispose() =
            match this.Agent with
            | Some agent -> (agent :> System.IDisposable).Dispose()
            | None -> ()

/// Creation and acquisition primitives for resources.
[<RequireQualifiedAccess>]
module Resource =

    /// Creates a bounded resource permitting at most `quantity` units concurrently.
    /// The backing agent grants units FIFO, so waiters never starve.
    let newResource (name: string) (quantity: int) : Resource =
        if quantity < 1 then invalidArg "quantity" "Resource quantity must be at least 1"

        let agent = MailboxProcessor.Start(fun mbox ->
            let pending = System.Collections.Generic.Queue<int * AsyncReplyChannel<unit>>()
            let drainPending avail =
                let mutable a = avail
                while pending.Count > 0 && fst (pending.Peek()) <= a do
                    let need, chnl = pending.Dequeue()
                    a <- a - need
                    chnl.Reply()
                a
            let rec loop limit available = async {
                match! mbox.Receive() with
                | Acquire(units, chnl) ->
                    if units <= available && pending.Count = 0 then
                        chnl.Reply()
                        return! loop limit (available - units)
                    else
                        pending.Enqueue(units, chnl)
                        return! loop limit available
                | Release units ->
                    let avail = drainPending (available + units)
                    return! loop limit avail
                | Resize newLimit ->
                    let delta = newLimit - limit
                    let avail = drainPending (available + delta)
                    return! loop newLimit avail
            }
            loop quantity quantity)

        { Name = name; Limit = Some quantity; Agent = Some agent }

    /// Creates an unbounded resource: acquisition never blocks and never throttles.
    let newUnbounded (name: string) : Resource =
        { Name = name; Limit = None; Agent = None }

    /// Acquires `units` of the resource. Does NOT touch the CPU slot.
    /// No-op for unbounded resources.
    let acquire (resource: Resource) (units: int) : Async<unit> =
        match resource.Limit, resource.Agent with
        | Some limit, _ when units > limit ->
            invalidArg "units" (sprintf "Cannot acquire %d units of resource '%s' (limit %d)" units resource.Name limit)
        | _, None -> async.Return ()
        | _, Some agent -> agent.PostAndAsyncReply(fun chnl -> Acquire(units, chnl))

    /// Releases `units` previously acquired. No-op for unbounded resources.
    let release (resource: Resource) (units: int) : unit =
        match resource.Agent with
        | None -> ()
        | Some agent -> agent.Post(Release units)

    /// Acquires `units` for the duration of `body`, releasing even on exception.
    /// Does NOT touch the CPU slot — use from detached / executor contexts.
    /// For slot-holding recipe code, use `ScriptFuncs.withResource` instead.
    let withAcquired (resource: Resource) (units: int) (body: Async<'a>) : Async<'a> =
        async {
            do! acquire resource units
            try
                return! body
            finally
                release resource units
        }

    /// Dynamically changes the capacity of a bounded resource.
    /// For unbounded resources this is a no-op.
    /// `newLimit` must be >= 1.
    let resize (resource: Resource) (newLimit: int) : unit =
        if newLimit < 1 then invalidArg "newLimit" "Resource limit must be at least 1"
        match resource.Agent with
        | None -> ()   // unbounded: nothing to do
        | Some agent ->
            resource.Limit <- Some newLimit
            agent.Post(Resize newLimit)
