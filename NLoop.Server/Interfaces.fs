namespace NLoop.Server

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open LndClient
open NBitcoin
open FSharp.Control.Tasks
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server.DTOs

type ISwapEventListener =
  abstract member RegisterSwap: swapId: SwapId -> unit
  abstract member RemoveSwap: swapId: SwapId -> unit

type ILightningClientProvider =
  abstract member TryGetClient: crypto: SupportedCryptoCode -> INLoopLightningClient option
  abstract member GetAllClients: unit -> INLoopLightningClient seq

type IBlockChainListener =
  abstract member CurrentHeight: SupportedCryptoCode -> BlockHeight

[<AbstractClass;Sealed;Extension>]
type ILightningClientProviderExtensions =
  [<Extension>]
  static member GetClient(this: ILightningClientProvider, crypto: SupportedCryptoCode) =
    match this.TryGetClient crypto with
    | Some v -> v
    | None -> raise <| InvalidDataException($"cryptocode {crypto} is not supported for layer 2")

  [<Extension>]
  static member AsChangeAddressGetter(this: ILightningClientProvider) =
    NLoop.Domain.IO.GetAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )

type OnPaymentReception = Money -> Task
type OnPaymentCancellation = string -> Task

type ILightningInvoiceProvider =
  abstract member GetAndListenToInvoice:
    cryptoCode: SupportedCryptoCode *
    preimage: PaymentPreimage *
    amt: LNMoney *
    label: string *
    routeHints: LndClient.RouteHint[] *
    onPaymentFinished: OnPaymentReception *
    onCancellation: OnPaymentCancellation *
    ct: CancellationToken option
     -> Task<PaymentRequest>

type ISwapActor =
  inherit IActor<Swap.State, Swap.Command,Swap. Event, Swap.Error, SwapId, uint16 * DateTime>
  abstract member
    ExecNewLoopOut:
    req: LoopOutRequest *
    currentHeight: BlockHeight *
    ?source: string *
    ?ct: CancellationToken
      -> Task<Result<LoopOutResponse, string>>

  abstract member
    ExecNewLoopIn:
    req: LoopInRequest *
    currentHeight: BlockHeight *
    ?source: string *
    ?ct: CancellationToken
      -> Task<Result<LoopInResponse, string>>

type BlockChainInfo = {
  Progress: float32
  Height: BlockHeight
  BestBlockHash: uint256
}

type IBlockChainClient =
  abstract member GetBlock: blockHash: uint256 * ?ct: CancellationToken -> Task<BlockWithHeight>
  abstract member GetBlockChainInfo: ?ct: CancellationToken -> Task<BlockChainInfo>
  abstract member GetBlockHash: height: BlockHeight * ?ct: CancellationToken -> Task<uint256>
  abstract member GetRawTransaction: id: TxId * ?ct: CancellationToken -> Task<Transaction>
  abstract member GetBestBlockHash: ?ct: CancellationToken -> Task<uint256>


[<AbstractClass;Sealed;Extension>]
type IBlockChainClientExtensions =
  [<Extension>]
  static member GetBlockFromHeight(this: IBlockChainClient, height: BlockHeight, ct) = task {
    let! hash = this.GetBlockHash(height, ct)
    let! b = this.GetBlock(hash, ct)
    return b.Block
  }
  [<Extension>]
  static member GetBlockFromHeight(this: IBlockChainClient, height: BlockHeight) =
    this.GetBlockFromHeight(height, CancellationToken.None)

  [<Extension>]
  static member GetBestBlock(this: IBlockChainClient, ?ct) = task {
    let ct = defaultArg ct CancellationToken.None
    let! hash = this.GetBestBlockHash(ct)
    return! this.GetBlock(hash, ct)
  }

type GetBlockchainClient = SupportedCryptoCode -> IBlockChainClient

type ExchangeRate = decimal
type TryGetExchangeRate = PairId * CancellationToken -> Task<ExchangeRate option>