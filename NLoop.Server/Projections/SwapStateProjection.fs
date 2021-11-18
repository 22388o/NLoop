namespace NLoop.Server.Projections

open System
open System.Threading
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Domain.Utils.EventStore
open NLoop.Server
open NLoop.Server.Actors

/// Create Swap Projection with Catch-up subscription.
/// TODO: Use EventStoreDB's inherent Projection system
type SwapStateProjection(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  checkpointDB: ICheckpointDB,
                  actor: SwapActor,
                  eventAggregator: IEventAggregator) as this =
  inherit BackgroundService()
  let log = loggerFactory.CreateLogger<SwapStateProjection>()

  let mutable _state: Map<StreamId, Swap.State> = Map.empty
  let lockObj = obj()


  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun event -> unitTask {
      if not <| event.StreamId.Value.StartsWith(Swap.entityType) then () else
      match event.ToRecordedEvent(Swap.serializer) with
      | Error _ -> ()
      | Ok r ->
        this.State <-
          (
            if this.State |> Map.containsKey r.StreamId |> not then
              this.State |> Map.add r.StreamId actor.Aggregate.Zero
            else
              this.State
          )
          |> Map.change
            r.StreamId
            (Option.map(fun s ->
              actor.Aggregate.Apply s r.Data
            ))
          // We don't hold finished swaps on-memory for the scalability.
          |> Map.filter(fun _ -> function | Swap.State.Finished _ -> false | _ -> true)
        log.LogTrace($"Publishing RecordedEvent {r}")

        eventAggregator.Publish<RecordedEvent<Swap.Event>> r
        do!
          r.EventNumber.Value
          |> int64
          |> fun n -> checkpointDB.SetSwapStateCheckpoint(n, CancellationToken.None)
    }

  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      nameof(SwapStateProjection),
      SubscriptionTarget.All,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator)
  let mutable _finishedSwapSummary = Map.empty

  member this.State
    with get(): Map<_,_> = _state
    and set v =
      lock lockObj <| fun () ->
        _state <- v
  member this.OngoingLoopOuts =
    this.State
    |> Seq.choose(fun v ->
                  match v.Value with
                  | Swap.State.Out (_height, o) -> Some o
                  | _ -> None
                  )

  member this.OngoingLoopIns =
    this.State
    |> Seq.choose(fun v ->
                  match v.Value with
                  | Swap.State.In (_height, o) -> Some o
                  | _ -> None
                  )


  override this.ExecuteAsync(stoppingToken) = unitTask {
    let maybeCheckpoint = ValueNone
    //let! maybeCheckpoint =
      //checkpointDB.GetSwapStateCheckpoint(stoppingToken)
    let checkpoint =
      maybeCheckpoint
      |> ValueOption.map(Checkpoint.StreamPosition)
      |> ValueOption.defaultValue Checkpoint.StreamStart
    log.LogDebug($"Start projecting from checkpoint {checkpoint}")
    try
      do! subscription.SubscribeAsync(checkpoint, stoppingToken)
    with
    | ex ->
      log.LogError($"{ex}")
  }
