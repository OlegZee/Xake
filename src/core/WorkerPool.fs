module Xake.WorkerPool


// execution context
type ExecMessage<'r> =
    | Run of Target * Target list * Async<'r> * AsyncReplyChannel<Async<'r>>

module internal WorkerPool =

  open System.Threading
  open System.Threading.Tasks

  let create (logger:ILogger) maxThreads =
    // controls how many threads are running in parallel
    let throttler = new SemaphoreSlim (maxThreads)
    let log = logger.Log

    let mapKey (artifact:Target) = artifact.FullName

    throttler, MailboxProcessor.Start(fun mbox ->
        let rec loop(map) = async {
          match! mbox.Receive() with
          | Run(artifact, targets, action, chnl) ->
              let mkey = artifact |> mapKey

              match map |> Map.tryFind mkey with
              | Some (task:Task<'a>) ->
                  log Never "Task found for '%s'. Status %A" artifact.ShortName task.Status
                  chnl.Reply <| Async.AwaitTask task
                  return! loop(map)

              | None ->
                  do log Info "Task queued '%s'" artifact.ShortName
                  do! throttler.WaitAsync(-1) |> Async.AwaitTask |> Async.Ignore
                
                  let task = Async.StartAsTask (async {
                      try
                          let! buildResult = action
                          do log Info "Task done '%s'" artifact.ShortName
                          return buildResult
                      finally
                          throttler.Release() |> ignore
                    })
                  chnl.Reply <| Async.AwaitTask task
                  let newMap = targets |> List.fold (fun m t -> m |> Map.add (mapKey t) task) map
                  return! loop newMap
        }
        loop(Map.empty) )

open System.Threading

type Scheduler<'s> =
    private { Throttle: SemaphoreSlim; Pool: Agent<ExecMessage<'s>> }
    interface System.IDisposable with
        member this.Dispose() = this.Throttle.Dispose()

module Scheduler =

    let create logger maxThreads : Scheduler<_> =
        let throttler, pool = WorkerPool.create logger maxThreads
        { Throttle = throttler; Pool = pool }

    let pool s = s.Pool

    /// Release current slot, run work, reacquire slot.
    let withYieldedSlot scheduler work = async {
        scheduler.Throttle.Release() |> ignore
        let! result = work
        do! scheduler.Throttle.WaitAsync -1 |> Async.AwaitTask |> Async.Ignore
        return result
    }
