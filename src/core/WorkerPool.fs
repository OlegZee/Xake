module Xake.WorkerPool


/// Message type for the worker pool mailbox: requests execution of a target.
type ExecMessage<'r> =
    | Run of Target * Target list * Async<'r> * AsyncReplyChannel<Async<'r>>
    /// Reports a queued task that got its slot: it is running now
    | Started of string list
    /// Reports a finished task: it moves aside and stays available for the rest of the run
    | Done of string list
    /// Marks the boundary between one run and the next
    | NewRun

/// The state of a target's task in the current run (`Scheduler.query`).
type TaskState<'r> =
    | NotStarted
    | Running of Async<'r>
    | Finished

/// Internal worker pool that deduplicates and throttles parallel task execution.
module internal WorkerPool =

  open System.Threading.Tasks

  /// At most `n` holders; a freed slot goes to the waiter with the largest key, equal keys in
  /// arrival order (every key 0: FIFO).
  type Gate(n: int) =
      let sync = obj ()
      let mutable free = n
      let waiters = ResizeArray<int * TaskCompletionSource<unit>>()
      member _.Acquire (key: int) : Async<unit> =
          let wait = lock sync (fun () ->
              if free > 0 && waiters.Count = 0 then free <- free - 1; None
              else
                  let t = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                  waiters.Add((key, t)); Some t)
          match wait with
          | None -> async.Return ()
          | Some t -> Async.AwaitTask t.Task
      member _.Release () =
          let next = lock sync (fun () ->
              if waiters.Count = 0 then free <- free + 1; None
              else
                  let mutable best = 0
                  for i in 1 .. waiters.Count - 1 do
                      if fst waiters.[i] > fst waiters.[best] then best <- i
                  let _, t = waiters.[best]
                  waiters.RemoveAt best
                  Some t)
          next |> Option.iter (fun t -> t.SetResult ())

  /// Creates a throttled worker pool that runs a target at most once per run: a request for
  /// a target already queued or running joins it, a request for one this run has finished gets
  /// its result, and a new run starts over -- there the database decides what needs rebuilding.
  /// A new task waits at the gate by its key (`keyOf`). Returns the gate, the mailbox processor
  /// and the query.
  let create (logger:ILogger) maxThreads (keyOf: Target list -> int) =
    let gate = Gate maxThreads
    let log = logger.Log

    let mapKey (artifact:Target) = artifact.Name

    // Logging the agent's own failure must not throw out of the loop again.
    let logError fmt = Printf.kprintf (fun s -> try log Error "%s" s with _ -> ()) fmt

    // the maps after the last message, read by `query` without going through the mailbox; a
    // queued task is not started yet, as for the verdict
    let snapshot = ref (Map.empty, Map.empty)
    let query (artifact: Target) =
        let running, finished = System.Threading.Volatile.Read &snapshot.contents
        match Map.tryFind (mapKey artifact) running with
        | Some r -> Running r
        | None when Map.containsKey (mapKey artifact) finished -> Finished
        | None -> NotStarted

    let agent = new MailboxProcessor<_>(fun mbox ->

        let move keys (from, into) =
            keys |> List.fold (fun (from, into) key ->
                match from |> Map.tryFind key with
                | Some result -> from |> Map.remove key, into |> Map.add key result
                | None -> from, into) (from, into)

        // Handles one message and returns the next state; it never waits.
        let handle (queued, running, finished) msg =
          match msg with
          | Run(artifact, targets, action, chnl) ->
              // queued and running tasks are shared whenever they started, finished ones only
              // within the run that produced them
              let found = [queued; running; finished] |> List.tryPick (Map.tryFind (mapKey artifact))
              match found with
              | Some result ->
                  log Never "Task found for '%s'" artifact.Name
                  chnl.Reply result
                  queued, running, finished

              | None ->
                  let key = keyOf targets
                  do log Info "Task queued '%s' (key %d ms)" artifact.Name key
                  let keys = targets |> List.map mapKey
                  let task = Async.StartAsTask (async {
                      do! gate.Acquire key
                      mbox.Post (Started keys)
                      try
                          let! buildResult = action
                          do log Info "Task done '%s'" artifact.Name
                          return buildResult
                      finally
                          gate.Release()
                          mbox.Post (Done keys)
                    })
                  let result = Async.AwaitTask task
                  chnl.Reply result
                  keys |> List.fold (fun m k -> m |> Map.add k result) queued, running, finished

          | Started keys ->
              let queued, running = move keys (queued, running)
              queued, running, finished

          | Done keys ->
              let running, finished = move keys (running, finished)
              queued, running, finished

          | NewRun ->
              queued, running, Map.empty

        // An exception escaping the loop would kill the agent silently, and every later
        // PostAndAsyncReply would wait forever. So a failed message is logged, its requester
        // gets the failure (the build fails), and the agent keeps serving.
        let rec loop state = async {
          let! msg = mbox.Receive()
          let next =
              try
                  handle state msg
              with ex ->
                  match msg with
                  | Run(artifact, _, _, chnl) ->
                      let text = sprintf "WorkerPool agent failed to schedule '%s': %s" (try artifact.Name with _ -> "?") ex.Message
                      logError "%s" text
                      let error = XakeException text
                      try chnl.Reply (async { return raise error }) with _ -> ()
                  | _ ->
                      logError "WorkerPool agent failed on %A: %O" msg ex
                  state
          let _, running, finished = next
          System.Threading.Volatile.Write(&snapshot.contents, (running, finished))
          return! loop next
        }
        loop (Map.empty, Map.empty, Map.empty) )
    agent.Error.Add (fun ex -> logError "WorkerPool agent stopped: %O" ex)
    agent.Start()
    gate, agent, query

/// Scheduler combining the slot gate, the worker pool mailbox and the run's critical-path keys.
type Scheduler<'s> =
    private { Gate: WorkerPool.Gate; Pool: Agent<ExecMessage<'s>>; Query: Target -> TaskState<'s>
              Keys: System.Collections.Concurrent.ConcurrentDictionary<string, int> }
    interface System.IDisposable with
        member this.Dispose() = ()

/// Scheduler API for creating, accessing, and yielding execution slots.
module Scheduler =

    /// The critical-path key of a task (its targets): the largest of its targets' keys, 0 unknown.
    let private keyOf (keys: System.Collections.Concurrent.ConcurrentDictionary<string, int>) (targets: Target list) =
        targets |> List.fold (fun k t -> match keys.TryGetValue t.Name with | true, v -> max k v | _ -> k) 0

    /// Creates a new Scheduler with the given thread limit.
    let create logger maxThreads : Scheduler<_> =
        let keys = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
        let gate, pool, query = WorkerPool.create logger maxThreads (keyOf keys)
        { Gate = gate; Pool = pool; Query = query; Keys = keys }

    /// Returns the underlying mailbox processor for posting work items.
    let pool s = s.Pool

    /// The state of a target's task in this run: running (its result), finished, or not started.
    let query s target = s.Query target

    /// Starts a new run: a target an earlier run finished is looked at afresh, through the
    /// database, rather than answered from the pool.
    let newRun s = s.Pool.Post NewRun

    /// Sets the critical-path keys of a run (target name, key in ms); a demand that overlaps
    /// keeps the keys of the others.
    let setKeys s (keys: (string * int) seq) =
        for name, key in keys do s.Keys.[name] <- key

    /// The critical-path key of a task with these targets (0 when unknown).
    let key s (targets: Target list) = keyOf s.Keys targets

    /// Release the slot of the task with `targets`, run work, take a slot back by its key.
    let withYieldedSlot scheduler (targets: Target list) work = async {
        // Yield the current slot so other work can run.
        scheduler.Gate.Release()

        let reacquire () = scheduler.Gate.Acquire (key scheduler targets)

        try
            let! result = work
            do! reacquire()
            return result
        with ex ->
            try
                do! reacquire()
            with
            | :? System.OperationCanceledException
            | :? System.AggregateException -> ()
            return raise ex
    }
