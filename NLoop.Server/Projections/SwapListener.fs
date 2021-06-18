namespace NLoop.Server.Projections

open System
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Domain.Utils.EventStore
open NLoop.Server

type SwapListener(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  eventAggregator: IEventAggregator) =
  inherit BackgroundService()
  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun (event) -> unitTask {
      event.ToRecordedEvent(Swap.serializer)
      |> Result.map(eventAggregator.Publish<RecordedEvent<Swap.Event>>)
      |> ignore
    }
  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      "swap listener",
      Swap.streamId,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator)

  override this.ExecuteAsync(stoppingToken) = unitTask {
    let checkpoint = failwith "Todo"
    do! subscription.SubscribeAsync(checkpoint, stoppingToken)
  }
