module NLoop.Server.LoopHandlers

open System
open System.Linq
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open FSharp.Control.Reactive

open FsToolkit.ErrorHandling
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Services
open System.Reactive.Linq

open DotNetLightning.Utils

open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks
open Giraffe


let handleLoopOutCore (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let boltzCli = ctx.GetService<BoltzClient>()
      let struct(baseCryptoCode, quoteCryptoCode) as pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let height = ctx.GetBlockHeight(baseCryptoCode)
      let actor = ctx.GetService<SwapActor>()
      let f = boltzCli.CreateReverseSwapAsync
      let obs =
        ctx
          .GetService<IEventAggregator>()
          .GetObservable<Swap.EventWithId, Swap.ErrorWithId>()
          .Replay()

      match! actor.ExecNewLoopOut(f, req, height) with
      | Error e ->
        return! (error503 e) next ctx
      | Ok loopOut ->
      if (not req.AcceptZeroConf) then
        let response = {
          LoopOutResponse.Id = loopOut.Id.Value
          Address = loopOut.ClaimAddress
          ClaimTxId = None
        }
        return! json response next ctx
      else
        let firstErrorOrTxIdT =
          obs
          |> Observable.filter(function
                               | Choice1Of2 { Id = swapId }
                               | Choice2Of2 { Id = swapId } -> swapId = loopOut.Id)
          |> Observable.choose(
            function
            | Choice1Of2({ Event = Swap.Event.ClaimTxPublished txId }) -> txId |> Ok |> Some
            | Choice1Of2( { Event = Swap.Event.FinishedByError(_id, err) }) -> err |> Error |> Some
            | Choice2Of2({ Error = e }) -> e.ToString() |> Error |> Some
            | _ -> None
            )
          |> fun o -> o.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
        use _ = obs.Connect()
        match! firstErrorOrTxIdT with
        | Error e ->
          return! (error503 e) next ctx
        | Ok txid ->
          let response = {
            LoopOutResponse.Id = loopOut.Id.Value
            Address = loopOut.ClaimAddress
            ClaimTxId = Some txid
          }
          return! json response next ctx
    }

let handleLoopOut (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let struct(_baseAsset, quoteAsset) =
        pairId
      return!
        (checkBlockchainIsSyncedAndSetTipHeight pairId
         >=> checkWeHaveRouteToCounterParty quoteAsset req.Amount
         >=> handleLoopOutCore req)
          next ctx
    }
let handleLoopInCore (loopIn: LoopInRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let actor = ctx.GetService<SwapActor>()
      let height = ctx.GetBlockHeight()
      let request =
        ctx
          .GetService<BoltzClient>()
          .CreateSwapAsync
      match! actor.ExecNewLoopIn(request, loopIn, height) with
      | Ok response ->
        return! json response next ctx
      | Error e ->
        return! (error503 e) next ctx
    }
let handleLoopIn (loopIn: LoopInRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let handle = (handleLoopInCore loopIn)
      let pairId =
        loopIn.PairId
        |> Option.defaultValue PairId.Default
      return!
        (checkBlockchainIsSyncedAndSetTipHeight pairId >=>
         handle)
          next ctx
    }

