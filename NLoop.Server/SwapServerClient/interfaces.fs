namespace NLoop.Server.SwapServerClient

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.DTOs
open FSharp.Control.Tasks
open NLoop.Server.DTOs

/// DTO against swap server (e.g. boltz-backend, loop-server)
[<RequireQualifiedAccess>]
module SwapDTO =
  type LoopOutRequest = {
    PairId: PairId
    ClaimPublicKey: PubKey
    InvoiceAmount: Money
    PreimageHash: uint256
  }
  type LoopOutResponse = {
    Id: string
    LockupAddress: string
    Invoice: PaymentRequest
    TimeoutBlockHeight: BlockHeight
    OnchainAmount: Money
    RedeemScript: Script

    /// The invoice
    MinerFeeInvoice: PaymentRequest option
  }
    with
    member this.Validate(preimageHash: uint256,
                         claimPubKey: PubKey,
                         offChainAmountWePay: Money,
                         maxSwapServiceFee: Money,
                         maxPrepay: Money,
                         rate: ExchangeRate,
                         n: Network): Result<_, string> =
      let mutable addr = null
      let mutable e = null
      try
        addr <-
          BitcoinAddress.Create(this.LockupAddress, n)
      with
      | :? FormatException as ex ->
        e <- ex
        ()
      if isNull addr then Error($"Boltz returned invalid bitcoin address for lockup address ({this.LockupAddress}): error msg: {e.Message}") else
      let actualSpk = addr.ScriptPubKey
      let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
      if (actualSpk <> expectedSpk) then
        Error $"lockupAddress {this.LockupAddress} and redeem script ({this.RedeemScript}) does not match"
      else if this.Invoice.PaymentHash <> PaymentHash(preimageHash) then
        Error "Payment Hash in invoice does not match preimage hash we specified in request"
      else if (this.Invoice.AmountValue.IsSome && this.Invoice.AmountValue.Value.Satoshi <> offChainAmountWePay.Satoshi) then
        Error $"What they requested in invoice {this.Invoice.AmountValue.Value} does not match the amount we are expecting to pay ({offChainAmountWePay})."
      else
        let prepayAmount =
          this.MinerFeeInvoice
          |> Option.bind(fun invoice -> invoice.AmountValue)
          |> Option.defaultValue LNMoney.Zero
        if prepayAmount.Satoshi > maxPrepay.Satoshi then
          Error $"The amount specified in invoice ({prepayAmount.Satoshi} sats) was larger than the max we can accept ({maxPrepay.Satoshi} sats)"
        else
        let swapServiceFee =
          decimal (offChainAmountWePay.Satoshi + prepayAmount.Satoshi) - (decimal(this.OnchainAmount.Satoshi) * rate)
          |> Money.Satoshis
        if maxSwapServiceFee < swapServiceFee then
          let msg =
            $"What swap service claimed as their fee ({swapServiceFee.Satoshi} sats) is larger than our max acceptable" +
            $"fee rate ({maxSwapServiceFee.Satoshi} sats). you may want to set max_swap_fee in request."
          Error msg
        elif maxPrepay.Satoshi < prepayAmount.Satoshi then
          Error $"The counterparty-specified amount for prepayment miner fee ({prepayAmount}) is larger than our maximum ({maxPrepay})"
        else
          this.RedeemScript |> Scripts.validateReverseSwapScript preimageHash claimPubKey this.TimeoutBlockHeight

  type LoopInRequest = {
    PairId: PairId
    RefundPublicKey: PubKey
    Invoice: PaymentRequest
  }

  type LoopInResponse = {
    Id: string
    Address: string
    RedeemScript: Script
    AcceptZeroConf: bool
    ExpectedAmount: Money
    TimeoutBlockHeight: BlockHeight
  }
    with
    member this.Validate(preimageHash: uint256,
                         refundPubKey,
                         ourInvoiceAmount: Money,
                         maxSwapServiceFee: Money,
                         onChainNetwork: Network,
                         rate: ExchangeRate
                         ): Result<_, string> =
      let mutable addr = null
      let mutable e = null
      try
        addr <-
          BitcoinAddress.Create(this.Address, onChainNetwork)
      with
      | :? FormatException as ex ->
        e <- ex
        ()
      if isNull addr then Error $"Boltz returned invalid bitcoin address ({this.Address}): error msg: {e.Message}" else
      let actualSpk = addr.ScriptPubKey
      let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
      if (actualSpk <> expectedSpk) then
        Error $"Address {this.Address} and redeem script ({this.RedeemScript}) does not match"
      else
        let swapServiceFee =
          (decimal ourInvoiceAmount.Satoshi) - (decimal this.ExpectedAmount.Satoshi / rate)
          |> Money.Satoshis
        if maxSwapServiceFee < swapServiceFee then
          Error $"What swap service claimed as their fee ({swapServiceFee.Satoshi} sats) is larger than our max acceptable fee ({maxSwapServiceFee.Satoshi} sats)"
        else
          (this.RedeemScript |> Scripts.validateSwapScript preimageHash refundPubKey this.TimeoutBlockHeight)

  type GetNodesResponse = {
    Nodes: Map<string, NodeInfo>
  }
  and NodeInfo = {
    NodeKey: PubKey
    Uris: PeerConnectionString []
  }

  [<RequireQualifiedAccess>]
  type UnAcceptableQuoteError =
    | SweepFeeTooHigh of ourMax: Money * actual: Money
    | MinerFeeTooHigh of ourMax: Money * actual: Money
    | CLTVDeltaTooShort of ourMax: BlockHeightOffset32 * actual: BlockHeightOffset32
    with
    member this.Message =
      match this with
      | SweepFeeTooHigh(ourMax, actual) ->
        $"Sweep fee specified by the server is too high (ourMax: {ourMax}, The amount they specified: {actual}) "
      | MinerFeeTooHigh(ourMax, actual) ->
        $"Miner fee specified by the server is too high (ourMax: {ourMax}, The amount they specified {actual})"
      | CLTVDeltaTooShort(ourMax, actual) ->
        "CLTVDelta they say they want to specify for their invoice is too short for our HTLCConfirmation parameter. " +
        $"(our acceptable max is {ourMax}, actual value is {actual})" +
        $"You may want to set a longer value to \"{nameof(LoopOutRequest.Instance.SwapTxConfRequirement)}\" in a loopout request, " +
        "The risk of having longer value is that worst-case swap time gets longer. " +
        "This means that the counterparty will e a larger chance to have a incentive to quit the " +
        "swap in case of sudden price fluctuation. " +
        $"Another way is to make {nameof(LoopOutRequest.Instance.SwapTxConfRequirement)} shorter, you will have a larger chance" +
        $"to lose your funds in case of reorg in this case."

  type LoopOutQuoteRequest = {
    Amount: Money
    SweepConfTarget: BlockHeightOffset32
    Pair: PairId
  }

  /// estimates for the fees making up the total swap cost for the client.
  type LoopOutQuote = {
    /// An amount the service will take in swap. unit is an off-chain asset
    SwapFee: Money
    SweepMinerFee: Money
    SwapPaymentDest: PubKey
    CltvDelta: BlockHeightOffset32
    PrepayAmount: Money
  }
    with
    member this.Validate(limits: LoopOutLimits) =
      if this.SwapFee > limits.MaxSwapFee then
        (limits.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SweepFeeTooHigh |> Error
      elif this.SweepMinerFee > limits.MaxMinerFee then
        (limits.MaxMinerFee, this.SweepMinerFee) |> UnAcceptableQuoteError.MinerFeeTooHigh |> Error
      else
        let ourAcceptableMaxCLTVDelta =
          limits.SwapTxConfRequirement
            + Swap.Constants.DefaultSweepConfTargetDelta
            + limits.MaxCLTVDelta
        if this.CltvDelta > ourAcceptableMaxCLTVDelta then
          (ourAcceptableMaxCLTVDelta, this.CltvDelta) |> UnAcceptableQuoteError.CLTVDeltaTooShort |> Error
        else
          Ok ()

  type LoopInQuoteRequest = {
    Amount: Money
    /// We don't need this since boltz does not require us to specify it.
    // HtlcConfTarget: BlockHeightOffset32
    Pair: PairId
  }

  type LoopInQuote = {
    /// An amount the service will take in swap. unit is an off-chain asset
    SwapFee: Money
    MinerFee: Money
  }
    with
    member this.Validate(feeLimit: LoopInLimits) =
      if this.SwapFee > feeLimit.MaxSwapFee then
        (feeLimit.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SweepFeeTooHigh |> Error
      elif this.MinerFee > feeLimit.MaxMinerFee then
        (feeLimit.MaxMinerFee, this.MinerFee) |> UnAcceptableQuoteError.MinerFeeTooHigh |> Error
      else
        Ok ()

  [<Struct>]
  type OutTermsRequest = {
    PairId: PairId
    IsZeroConf: bool
  }

  [<Struct>]
  type InTermsRequest = {
    PairId: PairId
    IsZeroConf: bool
  }

  type OutTermsResponse = {
    MinSwapAmount: Money
    MaxSwapAmount: Money
  }

  type InTermsResponse = {
    MinSwapAmount: Money
    MaxSwapAmount: Money
  }

type ISwapServerClient =
  abstract member LoopOut: request: SwapDTO.LoopOutRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopOutResponse>
  abstract member LoopIn: request: SwapDTO.LoopInRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopInResponse>
  abstract member GetNodes: ?ct: CancellationToken -> Task<SwapDTO.GetNodesResponse>

  abstract member GetLoopOutQuote: request: SwapDTO.LoopOutQuoteRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopOutQuote>
  abstract member GetLoopInQuote: request: SwapDTO.LoopInQuoteRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopInQuote>

  abstract member GetLoopOutTerms: req: SwapDTO.OutTermsRequest * ?ct : CancellationToken -> Task<SwapDTO.OutTermsResponse>
  abstract member GetLoopInTerms: req: SwapDTO.InTermsRequest * ?ct : CancellationToken -> Task<SwapDTO.InTermsResponse>

  abstract member CheckConnection: ?ct: CancellationToken -> Task

  /// In loop out, the counterparty must tell us about the swap tx (htlc tx, lockup tx). Otherwise we have no way to
  /// know about it. This is a method for waiting about the tx information.
  abstract member ListenToSwapTx: swapId: SwapId * ?ct: CancellationToken -> Task<Transaction>


type RestrictionError =
  | MinimumExceedsMaximumAmt
  | MaxExceedsServer of clientMax: Money * serverMax: Money
  | MinLessThenServer of clientMin: Money * serverMin: Money
  with
  member this.Message =
    match this with
    | MinimumExceedsMaximumAmt -> "minimum swap amount exceeds maximum"
    | MaxExceedsServer (c, s) ->
      $"maximum swap amount ({c.Satoshi} sats) is more than the server maximum ({s.Satoshi} sats)"
    | MinLessThenServer (c, s)  ->
      $"minimum swap amount ({c.Satoshi} sats) is less than server minimum ({s.Satoshi} sats)"

type ClientRestrictions = {
  Minimum: Money option
  Maximum: Money option
}
  with
  static member Default = {
    Minimum = None
    Maximum = None
  }

type ServerRestrictions = {
  Minimum: Money
  Maximum: Money
}
  with
  static member Validate
    ({Minimum = serverMin; Maximum =  serverMax}: ServerRestrictions,
     { Minimum = maybeClientMin; Maximum = maybeClientMax }: ClientRestrictions) =
    match maybeClientMin, maybeClientMax with
    | Some clientMin, Some clientMax when clientMin > clientMax ->
      Error(RestrictionError.MinimumExceedsMaximumAmt)
    | Some clientMin, _  when clientMin < serverMin ->
      Error(RestrictionError.MinLessThenServer(clientMin, serverMin))
    | _, Some clientMax when clientMax > serverMax ->
        Error(RestrictionError.MaxExceedsServer(clientMax, serverMax))
    | _ ->
      Ok()


[<Extension;AbstractClass;Sealed>]
type ISwapServerClientExtensions =
  [<Extension>]
  static member GetSwapAmountRestrictions(this: ISwapServerClient, group: Swap.Group, zeroConf: bool, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    match group.Category with
    | Swap.Category.In ->
      let! resp = this.GetLoopInTerms({
        SwapDTO.InTermsRequest.PairId = group.PairId
        SwapDTO.InTermsRequest.IsZeroConf = zeroConf
      }, ct )
      return {
        ServerRestrictions.Maximum = resp.MaxSwapAmount
        Minimum = resp.MinSwapAmount
      }
    | Swap.Category.Out ->
      let! resp = this.GetLoopOutTerms({
          SwapDTO.OutTermsRequest.PairId = group.PairId
          SwapDTO.OutTermsRequest.IsZeroConf = zeroConf
        }, ct)
      return {
        ServerRestrictions.Maximum = resp.MaxSwapAmount
        Minimum = resp.MinSwapAmount
      }
    }
