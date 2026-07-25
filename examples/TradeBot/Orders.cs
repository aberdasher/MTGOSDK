/** @file
  Trade-order queue for the `worker` mode.

  A "trade order" is a small JSON file describing ONE trade: who the MTGO partner
  is, what we GIVE them, what we RECEIVE, and whether to actually commit. The worker
  drains a folder of these, executes each against MTGO (reusing the lend/swap/grab
  flows), and writes a SIBLING `<name>.status.json` next to each input — the input
  file is never modified.

  This is deliberately transport-agnostic: today you drop order files into the
  folder by hand; later a remote producer (the Discord bot over Tailscale) writes the
  SAME JSON shape and nothing about the executor changes.

  Order shape:
    { "id":"ord-001", "mtgoPartner":"Aberdasher",
      "give":[{"name":"Radiant Strike","qty":1}],
      "receive":[{"name":"Azimaet Drake","qty":1}],
      "commit":false, "listen":false }
  Status shape (written by the worker):
    { "id":"ord-001", "status":"completed", "detail":"...", "updatedAt":"...",
      "gave":["1x Radiant Strike"], "received":["1x Azimaet Drake"] }
**/

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeBot;

public sealed class TradeItem
{
  public string Name { get; set; } = "";
  public int Qty { get; set; } = 1;
  public override string ToString() => $"{Qty}x {Name}";
}

public sealed class TradeOrder
{
  public string Id { get; set; } = "";
  public string MtgoPartner { get; set; } = "";
  public List<TradeItem> Give { get; set; } = new();
  public List<TradeItem> Receive { get; set; } = new();
  public bool Commit { get; set; } = false;
  public bool Listen { get; set; } = false;

  /// <summary>Path the order was loaded from (not serialized).</summary>
  [JsonIgnore] public string SourcePath { get; set; } = "";
}

public sealed class OrderStatus
{
  public string Id { get; set; } = "";
  public string Status { get; set; } = "";     // running | completed | cancelled | failed | unsupported
  public string Detail { get; set; } = "";
  public string UpdatedAt { get; set; } = "";
  public List<string>? Gave { get; set; }
  public List<string>? Received { get; set; }
}

/// <summary>Reads/writes trade orders + their sibling status files in a folder.</summary>
public static class OrderQueue
{
  static readonly JsonSerializerOptions Opts = new()
  {
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  /// <summary>Default queue: %USERPROFILE%\mtgosdk-tradebot\orders (beside the ledger).</summary>
  public static string DefaultDir =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "orders");

  public static string StatusPathFor(string orderPath) =>
    Path.Combine(Path.GetDirectoryName(orderPath) ?? ".",
                 Path.GetFileNameWithoutExtension(orderPath) + ".status.json");

  /// <summary>
  /// Pending orders in <paramref name="dir"/>: every *.json that ISN'T a
  /// *.status.json and doesn't already have a sibling status file. Unparseable
  /// files get a "failed" status written so they're not retried forever.
  /// </summary>
  public static List<TradeOrder> Pending(string dir)
  {
    var list = new List<TradeOrder>();
    if (!Directory.Exists(dir)) return list;
    foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
      if (f.EndsWith(".status.json", StringComparison.OrdinalIgnoreCase)) continue;
      if (File.Exists(StatusPathFor(f))) continue;   // already processed

      TradeOrder? o = null;
      try { o = JsonSerializer.Deserialize<TradeOrder>(File.ReadAllText(f), Opts); }
      catch { }
      if (o is null)
      {
        WriteStatus(f, new OrderStatus { Id = Path.GetFileNameWithoutExtension(f), Status = "failed",
          Detail = "could not parse order JSON", UpdatedAt = NowIso() });
        continue;
      }
      o.SourcePath = f;
      if (string.IsNullOrWhiteSpace(o.Id)) o.Id = Path.GetFileNameWithoutExtension(f);
      list.Add(o);
    }
    return list;
  }

  public static void WriteStatus(string orderPath, OrderStatus s)
  {
    s.UpdatedAt = NowIso();
    try { File.WriteAllText(StatusPathFor(orderPath), JsonSerializer.Serialize(s, Opts)); } catch { }
  }

  public static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
