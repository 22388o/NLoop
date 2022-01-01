namespace NLoop.Server.Projections

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open EventStore.ClientAPI
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server

type StartHeight = BlockHeight
type IOnGoingSwapStateProjection =
  abstract member State: Map<StreamId, StartHeight * Swap.State>
  abstract member FinishCatchup: Task

/// Create Swap Projection with Catch-up subscription.
/// TODO: Use EventStoreDB's inherent Projection system
type OnGoingSwapStateProjection(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  checkpointDB: ICheckpointDB,
                  actor: ISwapActor,
                  eventAggregator: IEventAggregator,
                  conn: IEventStoreConnection) as this =
  inherit BackgroundService()
  let log = loggerFactory.CreateLogger<OnGoingSwapStateProjection>()

  let mutable _state: Map<StreamId, StartHeight * Swap.State> = Map.empty
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
              this.State |> Map.add r.StreamId (BlockHeight(0u), actor.Aggregate.Zero)
            else
              this.State
          )
          |> Map.change
            r.StreamId
            (Option.map(fun (h, s) ->
              let nextState = actor.Aggregate.Apply s r.Data
              let startHeight =
                match r.Data with
                | Swap.Event.NewLoopOutAdded(h, _)
                | Swap.Event.NewLoopInAdded(h, _) -> h | _ -> h
              (startHeight, nextState)
            ))
          // We don't hold finished swaps on-memory for the scalability.
          |> Map.filter(fun _ (_, state) -> match state with | Swap.State.Finished _ -> false | _ -> true)
        log.LogTrace($"Publishing RecordedEvent {r}")

        eventAggregator.Publish<RecordedEvent<Swap.Event>> r
        do!
          r.EventNumber.Value
          |> int64
          |> fun n -> checkpointDB.SetSwapStateCheckpoint(n, CancellationToken.None)
    }

  let mutable catchupCompletion = TaskCompletionSource()
  let onFinishCatchup _ =
    catchupCompletion.SetResult()
    ()
  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      nameof(OnGoingSwapStateProjection),
      SubscriptionTarget.All,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator,
      onFinishCatchup,
      conn)

  member this.State
    with get(): Map<_, StartHeight * Swap.State> = _state
    and set v =
      lock lockObj <| fun () ->
        _state <- v

  interface IOnGoingSwapStateProjection with
    member this.State = this.State
    member this.FinishCatchup = catchupCompletion.Task

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


[<AutoOpen>]
module ISwapStateProjectionExtensions =
  type IOnGoingSwapStateProjection with
    member this.OngoingLoopIns =
      this.State
      |> Seq.choose(fun kv ->
                    let _, v = kv.Value
                    match v with
                    | Swap.State.In (_height, o) -> Some o
                    | _ -> None
                    )

    member this.OngoingLoopOuts =
      this.State
      |> Seq.choose(fun kv ->
                    let _, v = kv.Value
                    match v with
                    | Swap.State.Out (_height, o) -> Some o
                    | _ -> None
                    )