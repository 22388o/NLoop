namespace NLoop.Domain

open System
open System.Text.Json
open System.Threading.Tasks
open System.Threading.Tasks
open FSharp.Control.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open FsToolkit.ErrorHandling
open NLoop.Domain.Utils.EventStore

[<RequireQualifiedAccess>]
/// List of ubiquitous languages
/// * SwapTx (LockupTx) ... On-Chain TX which offers funds with HTLC.
/// * ClaimTx ... TX to take funds from SwapTx in exchange of preimage. a.k.a "sweep"
/// * RefundTx ... TX to take funds from SwapTx in case of the timeout.
/// * Offer ... the off-chain payment from us to counterparty. The preimage must be sufficient to claim SwapTx.
/// * Payment ... off-chain payment from counterparty to us.
module Swap =
  [<RequireQualifiedAccess>]
  type FinishedState =
    | Success
    /// Counterparty went offline. And we refunded our funds.
    | Refunded of uint256
    /// Counterparty gave us bogus msg. Swap did not start.
    | Errored of msg: string
    /// Counterparty did not publish the swap tx (lockup tx) in loopout, so the swap is canceled.
    | Timeout of msg: string

  [<Struct>]
  type Category =
    | In
    | Out

  type State =
    | HasNotStarted
    | Out of blockHeight: BlockHeight * LoopOut
    | In of blockHeight: BlockHeight * LoopIn
    | Finished of FinishedState
    with
    static member Zero = HasNotStarted

  // ------ command -----

  module Data =
    type TxInfo = {
      TxId: uint256
      Tx: Transaction
      Eta: int option
    }
    and SwapStatusResponseData = {
      _Status: string
      Transaction: TxInfo option
      FailureReason: string option
    }
      with
      member this.SwapStatus =
        SwapStatusType.FromString(this._Status)


  [<Literal>]
  let entityType = "swap"

  [<AutoOpen>]
  module private Constants =
    let MinPreimageRevealDelta = BlockHeightOffset32(20u)

    let DefaultSweepConfTarget = BlockHeightOffset32 9u

    let DefaultSweepConfTargetDelta = BlockHeightOffset32 18u

  type LoopOutParams = {
    MaxPrepayFee: Money
    MaxPaymentFee: Money
    OutgoingChanId: ShortChannelId option
    Height: BlockHeight
  }

  type Command =
    // -- loop out --
    | NewLoopOut of LoopOutParams * LoopOut
    | OffChainOfferResolve of paymentPreimage: PaymentPreimage

    // -- loop in --
    | NewLoopIn of height: BlockHeight * LoopIn

    // -- both
    | SwapUpdate of Data.SwapStatusResponseData
    | SetValidationError of err: string
    | NewBlock of height: BlockHeight * cryptoCode: SupportedCryptoCode

  type PayInvoiceParams = {
    MaxFee: Money
    OutgoingChannelId: ShortChannelId option
  }
  // ------ event -----
  type Event =
    // -- loop out --
    | NewLoopOutAdded of height: BlockHeight * loopIn: LoopOut
    | OffChainOfferStarted of swapId: SwapId * pairId: PairId * invoice: PaymentRequest * payInvoiceParams: PayInvoiceParams
    | ClaimTxPublished of txid: uint256
    | OffChainOfferResolved of paymentPreimage: PaymentPreimage

    // -- loop in --
    | NewLoopInAdded of height: BlockHeight * loopIn: LoopIn
    // We do not use `Transaction` type just to make serializer happy.
    // if we stop using json serializer for serializing this event, we can use `Transaction` instead.
    // But it seems that just using string is much simpler.
    | SwapTxPublished of txHex: string
    | RefundTxPublished of txid: uint256

    // -- general --
    | NewTipReceived of BlockHeight

    | FinishedByError of id: SwapId * err: string
    | FinishedSuccessfully of id: SwapId
    | FinishedByRefund of id: SwapId
    | FinishedByTimeout of reason: string
    | UnknownTagEvent of tag: uint16 * data: byte[]
    with
    member this.EventTag =
      match this with
      | NewLoopOutAdded _ -> 0us
      | ClaimTxPublished _ -> 1us
      | OffChainOfferStarted _ -> 2us
      | OffChainOfferResolved _ -> 3us

      | NewLoopInAdded _ -> 256us + 0us
      | SwapTxPublished _ -> 256us + 1us
      | RefundTxPublished _-> 256us + 2us

      | NewTipReceived _ -> 512us + 0us

      | FinishedSuccessfully _ -> 1024us + 0us
      | FinishedByRefund _ -> 1024us + 1us
      | FinishedByError _ -> 1024us + 2us
      | FinishedByTimeout _ -> 1024us + 3us

      | UnknownTagEvent (t, _) -> t

    member this.Type =
      match this with
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | ClaimTxPublished _ -> "claim_tx_published"
      | OffChainOfferStarted _ -> "offchain_offer_started"
      | OffChainOfferResolved _ -> "offchain_offer_resolved"

      | NewLoopInAdded _ -> "new_loop_in_added"
      | SwapTxPublished _ -> "swap_tx_published"
      | RefundTxPublished _ -> "refund_tx_published"

      | NewTipReceived _ -> "new_tip_received"

      | FinishedSuccessfully _ -> "finished_successfully"
      | FinishedByRefund _ -> "finished_by_refund"
      | FinishedByError _ -> "finished_by_error"
      | FinishedByTimeout _ -> "finished_by_timeout"

      | UnknownTagEvent _ -> "unknown_version_event"
    member this.ToEventSourcingEvent effectiveDate source : ESEvent<Event> =
      {
        ESEvent.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
        Type = (entityType + "-" + this.Type) |> EventType.EventType
        Data = this
      }

  type Error =
    | TransactionError of string
    | UnExpectedError of exn
    | FailedToGetAddress of string
    | UTXOProviderError of UTXOProviderError
    | InputError of string
    | CanNotSafelyRevealPreimage

  let inline private expectTxError (txName: string) (r: Result<_, Transactions.Error>) =
    r |> Result.mapError(fun e -> $"Error while creating {txName}: {e.Message}" |> TransactionError)

  let inline private expectInputError(r: Result<_, string>) =
    r |> Result.mapError InputError

  let private jsonConverterOpts =
    let o = JsonSerializerOptions()
    o.AddNLoopJsonConverters()
    o
  let serializer : Serializer<Event> = {
    Serializer.EventToBytes = fun (e: Event) ->
      let v = e.EventTag |> fun t -> Utils.ToBytes(t, false)
      let b =
        match e with
        | UnknownTagEvent (_, b) ->
          b
        | e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
      Array.concat (seq [v; b])
    BytesToEvents =
      fun b ->
        try
          let e =
            match Utils.ToUInt16(b.[0..1], false) with
            | 0us | 1us | 2us | 3us
            | 256us | 257us | 258us
            | 512us
            | 1024us | 1025us | 1026us ->
              JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b.[2..], jsonConverterOpts)
            | v ->
              UnknownTagEvent(v, b.[2..])
          e |> Ok
        with
        | ex ->
          $"Failed to deserialize event json\n%A{ex}"
          |> Error
  }

  // ------ deps -----
  type Deps = {
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
    UTXOProvider: IUTXOProvider
    GetChangeAddress: GetAddress
    GetRefundAddress: GetAddress
    /// Used for pre-paying miner fee.
    /// This is not for paying an actual swap invoice, since we cannot expect it to get finished immediately.
    PayInvoice: Network -> PayInvoiceParams -> PaymentRequest -> Task
  }

  // ----- aggregates ----


  let private enhanceEvents date source (events: Event list) =
    events |> List.map(fun e -> e.ToEventSourcingEvent date source)

  /// Returns None in case we don't have to do anything.
  /// Otherwise returns txid for sweep tx.
  let private sweepOrBump
    { Deps.FeeEstimator = feeEstimator; Broadcaster = broadcaster }
    (height: BlockHeight)
    (lockupTx: Transaction)
    (loopOut: LoopOut): Task<Result<_ option, Error>> = taskResult {
      let struct (baseAsset, _quoteAsset) = loopOut.PairId
      let! feeRate =
        let confTarget =
          let remainingBlocks = loopOut.TimeoutBlockHeight - height
          if remainingBlocks <= DefaultSweepConfTargetDelta && loopOut.SweepConfTarget > DefaultSweepConfTarget then
            DefaultSweepConfTarget
          else
            loopOut.SweepConfTarget
        feeEstimator.Estimate confTarget baseAsset
      let getClaimTx feeRate: Result<Transaction, Error> =
        Transactions.createClaimTx
          (BitcoinAddress.Create(loopOut.ClaimAddress, loopOut.BaseAssetNetwork))
          loopOut.ClaimKey
          loopOut.Preimage
          loopOut.RedeemScript
          feeRate
          lockupTx
          loopOut.BaseAssetNetwork
        |> expectTxError "claim tx"
      let! claimTx = getClaimTx feeRate
      let! maybeClaimTxToPublish =
        if loopOut.MaxMinerFee > feeRate.GetFee(claimTx) then
          if loopOut.ClaimTransactionId.IsSome then
            // if the preimage is already revealed, we have no choice to bump the fee
            // to our possible maximum value.
            getClaimTx (FeeRate(loopOut.MaxMinerFee, claimTx.GetVirtualSize()))
            |> Result.map(Some)
          else
            // otherwise, we should not do anything.
            None |> Ok
        else
          claimTx |> Some |> Ok
      match maybeClaimTxToPublish with
      | Some claimTx ->
        do!
          broadcaster.BroadcastTx(claimTx, baseAsset)
      | None -> ()
      return
        maybeClaimTxToPublish |> Option.map(fun t -> t.GetWitHash())
    }

  let executeCommand
    ({ Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
       GetChangeAddress = getChangeAddress; GetRefundAddress = getRefundAddress; PayInvoice = payInvoice
     } as deps)
    (s: State)
    (cmd: ESCommand<Command>): Task<Result<ESEvent<Event> list, _>> =
    taskResult {
      try
        let { CommandMeta.EffectiveDate = effectiveDate; Source = source } = cmd.Meta
        let enhance = enhanceEvents effectiveDate source
        let checkHeight (height: BlockHeight) (oldHeight: BlockHeight) =
          if height.Value > oldHeight.Value then
            [NewTipReceived(height)] |> enhance |> Ok
          else
            [] |> Ok

        match cmd.Data, s with
        // --- loop out ---
        | NewLoopOut({ Height = h } as p, loopOut), HasNotStarted ->
          do! loopOut.Validate() |> expectInputError
          if loopOut.PrepayInvoice |> String.IsNullOrEmpty |> not then
            let prepaymentParams =
              { PayInvoiceParams.MaxFee =  p.MaxPrepayFee
                OutgoingChannelId = p.OutgoingChanId }
            do!
              loopOut.PrepayInvoice
              |> PaymentRequest.Parse
              |> ResultUtils.Result.deref
              |> payInvoice
                   loopOut.QuoteAssetNetwork
                   prepaymentParams
          return
            [
              NewLoopOutAdded(h, loopOut)
              let invoice =
                loopOut.Invoice
                |> PaymentRequest.Parse
                |> ResultUtils.Result.deref
              let paymentParams = {
                MaxFee = p.MaxPrepayFee
                OutgoingChannelId = p.OutgoingChanId
              }
              OffChainOfferStarted(loopOut.Id, loopOut.PairId, invoice, paymentParams)
            ]
            |> enhance
        | OffChainOfferResolve pp, Out(_, loopOut) ->
          return [OffChainOfferResolved pp; FinishedSuccessfully loopOut.Id] |> enhance
        | SwapUpdate u, Out(height, loopOut) ->
          if (u.SwapStatus = loopOut.Status) then
            return []
          else
          match u.SwapStatus with
          | SwapStatusType.TxMempool when not <| loopOut.AcceptZeroConf ->
            return []
          | SwapStatusType.TxMempool
          | SwapStatusType.TxConfirmed ->

            let swapTx =
               u.Transaction
               |> Option.map(fun txInfo -> txInfo.Tx)
               |> Option.defaultWith(fun () -> raise <| Exception("No Transaction in response"))

            let e = SwapTxPublished(swapTx.ToHex())
            match! sweepOrBump deps height swapTx loopOut with
            | Some txid ->
              return [e; ClaimTxPublished(txid);] |> enhance
            | None ->
              return [e] |> enhance

          | SwapStatusType.SwapExpired ->
            let reason =
              u.FailureReason
              |> Option.defaultValue "Counterparty claimed that the loop out is expired but we don't know the exact reason."
            return [FinishedByTimeout($"Swap expired (Reason: %s{reason})");] |> enhance
          | _ ->
            return []

        // --- loop in ---
        | NewLoopIn(h, loopIn), HasNotStarted ->
          do! loopIn.Validate() |> expectInputError
          return [NewLoopInAdded(h, loopIn)] |> enhance
        | SwapUpdate u, In(_height, loopIn) ->
          match u.SwapStatus with
          | SwapStatusType.InvoiceSet ->
            let struct (baseAsset, _) = loopIn.PairId
            let! utxos =
              utxoProvider.GetUTXOs(loopIn.ExpectedAmount, baseAsset)
              |> TaskResult.mapError(UTXOProviderError)
            let! feeRate =
              feeEstimator.Estimate
                loopIn.HTLCConfTarget
                baseAsset
            let! change =
              getChangeAddress.Invoke(baseAsset)
              |> TaskResult.mapError(FailedToGetAddress)
            let psbt =
              Transactions.createSwapPSBT
                utxos
                loopIn.RedeemScript
                loopIn.ExpectedAmount
                feeRate
                change
                loopIn.QuoteAssetNetwork
              |> function | Ok x -> x | Error e -> failwith $"%A{e}"
            let! psbt = utxoProvider.SignSwapTxPSBT(psbt, baseAsset)
            match psbt.TryFinalize() with
            | false, e ->
              return raise <| Exception $"%A{e |> Seq.toList}"
            | true, _ ->
              let tx = psbt.ExtractTransaction()
              do! broadcaster.BroadcastTx(tx, baseAsset)
              return [SwapTxPublished(tx.ToHex())] |> enhance
          | SwapStatusType.TxConfirmed
          | SwapStatusType.InvoicePayed ->
            // Ball is on their side. Just wait till they claim their on-chain share.
            return []
          | SwapStatusType.TxClaimed ->
            // boltz server has happily claimed their on-chain share.
            return [FinishedSuccessfully(loopIn.Id)] |> enhance
          | SwapStatusType.InvoiceFailedToPay ->
            // The counterparty says that they have failed to receive preimage before timeout. This should never happen.
            // But even if it does, we will just reclaim the swap tx when the timeout height has reached.
            // So nothing to do here.
            return []
          | SwapStatusType.SwapExpired ->
            // This means we have not send SwapTx. Or they did not recognize it.
            // This can be caused only by our bug.
            // (e.g. boltz-server did not recognize our swap script.)
            // So just ignore it.
            return []
          | _ ->
            return []

        // --- ---

        | SetValidationError(err), Out(_, { Id = swapId })
        | SetValidationError(err), In (_ , { Id = swapId }) ->
          return [FinishedByError( swapId, err )] |> enhance
        | NewBlock (height, cc), Out(oldHeight, ({ ClaimTransactionId = maybePrevTxId; PairId = struct(baseAsset, _); TimeoutBlockHeight = timeout } as loopOut))
          when baseAsset = cc ->
            let! events = (height, oldHeight) ||> checkHeight
            let! additionalEvents = taskResult {
              let remainingBlocks = timeout - height
              let haveWeRevealedThePreimage = maybePrevTxId.IsSome
              // If we have not revealed the preimage, and we don't have time left to sweep the swap,
              // we abandon the swap because we can no longer sweep on the success path (without potentially having to
              // compete with server's timeout tx.) and we have not had any coins pulled off-chain
              if remainingBlocks <= MinPreimageRevealDelta && not <| haveWeRevealedThePreimage then
                let msg =
                  $"We reached a timeout height (timeout: {timeout}) - (current height: {height}) < (minimum_delta: {MinPreimageRevealDelta})" +
                  "That means Preimage can no longer safely revealed, so we will give up the swap"
                return [FinishedByTimeout msg] |> enhance
              else
                match loopOut.LockupTransactionHex with
                | None ->
                  // When we don't know lockup tx (a.k.a. swap tx), there is no way we can claim our share.
                  return []
                | Some lockupTxHex ->
                  let tx = Transaction.Parse(lockupTxHex, loopOut.BaseAssetNetwork)
                  match! sweepOrBump deps height tx loopOut with
                  | Some txid ->
                    return [ClaimTxPublished(txid);] |> enhance
                  | None ->
                    return []
              }
            return events @ additionalEvents

        | NewBlock (height, cc), In(oldHeight, loopIn) when let struct (_, quoteAsset) = loopIn.PairId in quoteAsset = cc ->
          let! events = (height, oldHeight) ||> checkHeight
          if loopIn.TimeoutBlockHeight <= height then
            let struct(_offChainAsset, onChainAsset) = loopIn.PairId
            let! refundAddress =
              getRefundAddress.Invoke(onChainAsset)
              |> TaskResult.mapError(FailedToGetAddress)
            let! fee =
              feeEstimator.Estimate loopIn.HTLCConfTarget onChainAsset
            let! refundTx =
              Transactions.createRefundTx
                loopIn.LockupTransactionHex.Value
                loopIn.RedeemScript
                fee
                refundAddress
                loopIn.RefundPrivateKey
                loopIn.TimeoutBlockHeight
                loopIn.QuoteAssetNetwork
              |> expectTxError "refund tx"

            do! broadcaster.BroadcastTx(refundTx, onChainAsset)
            let additionalEvents =
              [RefundTxPublished(refundTx.GetWitHash()); FinishedByRefund loopIn.Id]
              |> enhance
            return events @ additionalEvents
          else
            return events
        | _, Finished _ ->
          return []
        | x, s ->
          return raise <| Exception($"Unexpected Command \n{x} \n\nWhile in the state\n{s}")
      with
      | ex ->
        return! UnExpectedError ex |> Error
    }

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | NewLoopOutAdded(h, x), HasNotStarted ->
      Out (h, x)
    | ClaimTxPublished txid, Out(h, x) ->
      Out (h, { x with ClaimTransactionId = Some txid })
    | SwapTxPublished tx, Out(h, x) ->
      Out (h, { x with LockupTransactionHex = Some tx })
    | OffChainOfferResolved(preimage), Out(h, x) ->
      Out(h, { x with Preimage = preimage })

    | NewLoopInAdded(h, x), HasNotStarted ->
      In (h, x)
    | SwapTxPublished tx, In(h, x) ->
      In (h, { x with LockupTransactionHex = Some tx })
    | RefundTxPublished txid, In(h, x) ->
      In(h, { x with RefundTransactionId = Some txid })

    | FinishedByError (_, err), In _ ->
      Finished(FinishedState.Errored(err))
    | FinishedByError (_, err), Out _ ->
      Finished(FinishedState.Errored(err))
    | FinishedSuccessfully _, Out _
    | FinishedSuccessfully _, In _ ->
      Finished(FinishedState.Success)
    | FinishedByRefund _, In (_h, { RefundTransactionId = Some txid }) ->
      Finished(FinishedState.Refunded(txid))
    | FinishedByTimeout reason, Out _
    | FinishedByTimeout reason, In _ ->
      Finished(FinishedState.Timeout(reason))

    | NewTipReceived h, Out(_, x) ->
      Out(h, x)
    | NewTipReceived h, In(_, x) ->
      In(h, x)
    | _, x -> x

  type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>
  type Handler = Handler<State, Command, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter = id
    Enrich = id
    SortBy = fun event ->
      event.Data.EventTag, event.Meta.EffectiveDate.Value
  }

  let getRepository eventStoreUri =
    let store = eventStore eventStoreUri
    Repository.Create
      store
      serializer
      entityType

  type EventWithId = {
    Id: SwapId
    Event: Event
  }

  type ErrorWithId = {
    Id: SwapId
    Error: EventSourcingError<Error>
  }

  let getHandler aggr eventStoreUri =
    getRepository eventStoreUri
    |> Handler.Create aggr

