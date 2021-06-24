module RandomUtils

open System.Runtime.CompilerServices
open NBitcoin


[<AbstractClass;Sealed;Extension>]
type TransactionBuilderExtensions() =
  [<Extension>]
  static member AddRandomFunds(this: TransactionBuilder, amount: Money) =
    let key = new Key()
    let coin =
      let tx = Network.RegTest.CreateTransaction()
      tx.Outputs.Add(amount, key) |> ignore
      tx.Outputs.AsCoins() |> Seq.cast<ICoin> |> Seq.toArray
    this
      .AddCoins(coin)
      .AddKeys(key)
