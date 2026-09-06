module Xake.WorkerPool


/// Message type for the worker pool mailbox: requests execution of a target.
type ExecMessage<'r> =
    | Run of Target * Target list * Async<'r> * AsyncReplyChannel<Async<'r>>
    /// Reports a finished task: it moves aside and stays available for the rest of the run
    | Done of string list
    /// Marks the boundary between one run and the next
    | NewRun

/// Internal worker pool that deduplicates and throttles parallel task execution.
module internal WorkerPool =

  open System.Threading

  /// Creates a throttled worker pool that runs a target at most once per run: a request for
  /// a target already running joins it, a request for one this run has finished gets its
  /// result, and a new run starts over -- there the database decides what needs rebuilding.
  /// Returns the semaphore and the mailbox processor.
  let create (logger:ILogger) maxThreads =
    // controls how many threads are running in parallel
    let throttler = new SemaphoreSlim (maxThreads)
    let log = logger.Log

    let mapKey (artifact:Target) = artifact.FullName

    throttler, MailboxProcessor.Start(fun mbox ->
        let rec loop (running, finished) = async {
          match! mbox.Receive() with
          | Run(artifact, targets, action, chnl) ->
              let mkey = artifact |> mapKey

              // running tasks are shared whenever they started, finished ones only within the
              // run that produced them
              match running |> Map.tryFind mkey |> Option.orElse (finished |> Map.tryFind mkey) with
              | Some result ->
                  log Never "Task found for '%s'" artifact.ShortName
                  chnl.Reply result
                  return! loop (running, finished)

              | None ->
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
                          mbox.Post (Done keys)
                    })
                  let result = Async.AwaitTask task
                  chnl.Reply result
                  return! loop (keys |> List.fold (fun m k -> m |> Map.add k result) running, finished)

          | Done keys ->
              let move (running, finished) key =
                  match running |> Map.tryFind key with
                  | Some result -> running |> Map.remove key, finished |> Map.add key result
                  | None -> running, finished
              return! loop (keys |> List.fold move (running, finished))

          | NewRun ->
              return! loop (running, Map.empty)
        }
        loop (Map.empty, Map.empty) )

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
