module Xake.WorkerPool


/// Message type for the worker pool mailbox: requests execution of a target.
type ExecMessage<'r> =
    | Run of Target * Target list * Async<'r> * AsyncReplyChannel<Async<'r>>
    /// Marks the boundary between one run and the next: within a run a target is executed at
    /// most once, across runs the database decides again.
    | NewRun

/// Internal worker pool that deduplicates and throttles parallel task execution.
module internal WorkerPool =

  open System.Threading
  open System.Threading.Tasks

  /// Creates a throttled worker pool that deduplicates tasks by target name.
  /// Returns the semaphore and the mailbox processor.
  let create (logger:ILogger) maxThreads =
    // controls how many threads are running in parallel
    let throttler = new SemaphoreSlim (maxThreads)
    let log = logger.Log

    let mapKey (artifact:Target) = artifact.FullName

    throttler, MailboxProcessor.Start(fun mbox ->
        let rec loop (run, map) = async {
          match! mbox.Receive() with
          | Run(artifact, targets, action, chnl) ->
              let mkey = artifact |> mapKey

              match map |> Map.tryFind mkey with
              // A task of this run answers the request whether it is still going or already
              // finished: one target, one execution per run. A task of an earlier run is
              // joined only while it is in flight -- once it is done the next run asks the
              // database again, which is what makes a second Demand notice a changed variable.
              | Some (taskRun, (task: Task<'a>)) when taskRun = run || not task.IsCompleted ->
                  log Never "Task found for '%s'. Status %A" artifact.ShortName task.Status
                  chnl.Reply <| Async.AwaitTask task
                  return! loop (run, map)

              | _ ->
                  do log Info "Task queued '%s'" artifact.ShortName
                  do! throttler.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore
                  let keys = targets |> List.map mapKey
                  let task = Async.StartAsTask (async {
                      try
                          let! buildResult = action
                          do log Info "Task done '%s'" artifact.ShortName
                          return buildResult
                      finally
                          throttler.Release() |> ignore
                    })
                  chnl.Reply <| Async.AwaitTask task
                  let newMap = keys |> List.fold (fun m k -> m |> Map.add k (run, task)) map
                  return! loop (run, newMap)

          | NewRun ->
              // finished tasks are forgotten, the ones still in flight are not: overlapping
              // runs keep sharing them instead of doing the work twice
              return! loop (run + 1, map |> Map.filter (fun _ (_, task: Task<'a>) -> not task.IsCompleted))
        }
        loop (0, Map.empty) )

open System.Threading

/// Scheduler combining a throttle semaphore and a worker pool mailbox.
type Scheduler<'s> =
    private { Throttle: SemaphoreSlim; Pool: Agent<ExecMessage<'s>> }
    interface System.IDisposable with
        member this.Dispose() = this.Throttle.Dispose()

/// Scheduler API for creating, accessing, and yielding execution slots.
module Scheduler =

    /// Creates a new Scheduler with the given thread limit.
    let create logger maxThreads : Scheduler<_> =
        let throttler, pool = WorkerPool.create logger maxThreads
        { Throttle = throttler; Pool = pool }

    /// Returns the underlying mailbox processor for posting work items.
    let pool s = s.Pool

    /// Starts a new run: a target an earlier run finished is looked at afresh, through the
    /// database, rather than answered from the pool.
    let newRun s = s.Pool.Post NewRun

    /// Release current slot, run work, reacquire slot.
    let withYieldedSlot scheduler work = async {
        // Yield the current semaphore slot so other work can run.
        scheduler.Throttle.Release() |> ignore

        let reacquire () =
            scheduler.Throttle.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore

        try
            let! result = work
            do! reacquire()
            return result
        with ex ->
            try
                do! reacquire()
            with
            | :? System.OperationCanceledException
            | :? System.ObjectDisposedException
            | :? System.AggregateException -> ()
            return raise ex
    }
