/** @file
  TradeItem — one card/tix name + quantity. The atom of a trade's give/receive lists
  (see TradeJob in TradeService.cs).

  This file formerly also held the order-file model + queue (TradeOrder / OrderStatus /
  OrderQueue) that fed the standalone `worker` mode and the `ui` order desk. Those were
  UNIFIED into `serve` (one in-process job queue + HTTP API + operator dashboard), so only
  the shared item type remains here.
**/
namespace TradeBot;

public sealed class TradeItem
{
  public string Name { get; set; } = "";
  public int Qty { get; set; } = 1;
  public int CatId { get; set; }    // exact printing (0 = unspecified — resolve by name)
  public override string ToString() => CatId > 0 ? $"{Qty}x {Name} (cat {CatId})" : $"{Qty}x {Name}";
}
