namespace Xake

open Xake.WorkerPool

/// Internal message protocol for the resource agent.
type private ResMsg =
    | Acquire of int * AsyncReplyChannel<unit>
    | Release of int

/// A named, concurrency-limited resource modeled on Shake's `Resource`.
/// A bounded resource grants at most `Limit` units concurrently; an unbounded
/// resource imposes no limit (acquire/release are no-ops). The scheduler honors
/// the limit by yielding the CPU slot while a recipe waits to acquire units, so a
/// blocked holder never pins a worker thread.
type Resource =
    private { Name: string; Limit: int option; Agent: Agent<ResMsg> option }

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
            let rec loop available = async {
                match! mbox.Receive() with
                | Acquire(units, chnl) ->
                    if units <= available && pending.Count = 0 then
                        chnl.Reply()
                        return! loop (available - units)
                    else
                        pending.Enqueue(units, chnl)
                        return! loop available
                | Release units ->
                    let mutable avail = available + units
                    while pending.Count > 0 && fst (pending.Peek()) <= avail do
                        let (need, chnl) = pending.Dequeue()
                        avail <- avail - need
                        chnl.Reply()
                    return! loop avail
            }
            loop quantity)

        { Name = name; Limit = Some quantity; Agent = Some agent }

    /// Creates an unbounded resource: acquisition never blocks and never throttles.
    let newUnbounded (name: string) : Resource =
        { Name = name; Limit = None; Agent = None }

    /// Acquires `units` of the resource WITHOUT touching the CPU slot. Use this from a
    /// context that does NOT currently hold a CPU slot — e.g. inside a DistributedExecutor,
    /// which the engine already runs detached. (Calling `acquireYielding` there would
    /// release a CPU permit it does not hold and corrupt the scheduler's slot count.)
    /// No-op for unbounded resources.
    let acquire (resource: Resource) (units: int) : Async<unit> =
        match resource.Limit, resource.Agent with
        | Some limit, _ when units > limit ->
            invalidArg "units" (sprintf "Cannot acquire %d units of resource '%s' (limit %d)" units resource.Name limit)
        | _, None -> async.Return ()
        | _, Some agent -> agent.PostAndAsyncReply(fun chnl -> Acquire(units, chnl))

    /// Acquires `units` of the resource, yielding the current CPU slot while blocked so
    /// other work can proceed. Returns once the units are held and the slot is reacquired.
    /// Use from slot-holding recipe code — this is what `withResource` uses. For an
    /// already-detached context (a DistributedExecutor) use `acquire` instead.
    /// No-op for unbounded resources.
    let acquireYielding (scheduler: Scheduler<_>) (resource: Resource) (units: int) : Async<unit> =
        Scheduler.withYieldedSlot scheduler (acquire resource units)

    /// Releases `units` previously acquired. No-op for unbounded resources.
    let release (resource: Resource) (units: int) : unit =
        match resource.Agent with
        | None -> ()
        | Some agent -> agent.Post(Release units)
