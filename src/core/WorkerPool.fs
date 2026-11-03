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

    // Logging the agent's own failure must not throw out of the loop again.
    let logError fmt = Printf.kprintf (fun s -> try log Error "%s" s with _ -> ()) fmt

    let agent = new MailboxProcessor<_>(fun mbox ->

        // Handles one message and returns the next state. `acquired` tells the failure handler
        // whether a slot was taken for a task that never started.
        let handle (running, finished) acquired msg = async {
          match msg with
          | Run(artifact, targets, action, chnl) ->
              let mkey = artifact |> mapKey

              // running tasks are shared whenever they started, finished ones only within the
              // run that produced them
              match running |> Map.tryFind mkey |> Option.orElse (finished |> Map.tryFind mkey) with
              | Some result ->
                  log Never "Task found for '%s'" artifact.ShortName
                  chnl.Reply result
                  return running, finished

              | None ->
                  do log Info "Task queued '%s'" artifact.ShortName
                  do! throttler.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore
                  acquired := true
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
                  acquired := false   // the task owns the slot now
                  let result = Async.AwaitTask task
                  chnl.Reply result
                  return keys |> List.fold (fun m k -> m |> Map.add k result) running, finished

          | Done keys ->
              let move (running, finished) key =
                  match running |> Map.tryFind key with
                  | Some result -> running |> Map.remove key, finished |> Map.add key result
                  | None -> running, finished
              return keys |> List.fold move (running, finished)

          | NewRun ->
              return running, Map.empty
        }

        // An exception escaping the loop would kill the agent silently, and every later
        // PostAndAsyncReply would wait forever. So a failed message is logged, its requester
        // gets the failure (the build fails), and the agent keeps serving.
        let rec loop state = async {
          let! msg = mbox.Receive()
          let acquired = ref false
          let! next = async {
              try
                  return! handle state acquired msg
              with ex ->
                  if acquired.Value then throttler.Release() |> ignore
                  match msg with
                  | Run(artifact, _, _, chnl) ->
                      let text = sprintf "WorkerPool agent failed to schedule '%s': %s" (try artifact.ShortName with _ -> "?") ex.Message
                      logError "%s" text
                      let error = XakeException text
                      try chnl.Reply (async { return raise error }) with _ -> ()
                  | Done _ | NewRun ->
                      logError "WorkerPool agent failed on %s: %O" (match msg with Done _ -> "Done" | _ -> "NewRun") ex
                  return state
            }
          return! loop next
        }
        loop (Map.empty, Map.empty) )
    agent.Error.Add (fun ex -> logError "WorkerPool agent stopped: %O" ex)
    agent.Start()
    throttler, agent

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
