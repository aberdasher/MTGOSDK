/** @file
  Holdings ledger — the custody accounting. Each Holding is a card the bot custodies: who it's
  FROM (owner — a player who deposited it, or "house" = the bot's own card), the exact printing,
  and where it is (held in the vault, or on loan with a borrower). Per-OWNER allow-lists gate who
  an owner's cards may be lent to. Persisted to ~/mtgosdk-tradebot/holdings.json so it survives
  restarts. (Supersedes the earlier loan-only ledger.)
**/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TradeBot;

public sealed class Holding
{
  public string Id { get; set; } = "";
  public string Owner { get; set; } = "house";   // who the bot has it FROM ("house" = the bot's own card)
  public string Card { get; set; } = "";
  public int CatId { get; set; }                 // the EXACT printing
  public int Qty { get; set; } = 1;
  public string? Borrower { get; set; }          // null => held in the vault; else on loan with this player
  public string Status { get; set; } = "held";   // held | onloan | closed
  public string AcquiredAt { get; set; } = "";   // ISO-8601 UTC
  public string? UpdatedAt { get; set; }
}

/// <summary>Thread-safe, disk-backed store of holdings + per-owner allow-lists. One instance owned by the serve.</summary>
public sealed class HoldingStore
{
  public const string House = "house";

  sealed class Db { public List<Holding> Holdings { get; set; } = new(); public Dictionary<string, List<string>> Allow { get; set; } = new(StringComparer.OrdinalIgnoreCase); }

  readonly string _path;
  readonly object _lock = new();
  Db _db = new();
  static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

  public static string DefaultPath =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "holdings.json");

  public HoldingStore(string? path = null) { _path = path ?? DefaultPath; Load(); }

  void Load()
  {
    try { if (File.Exists(_path)) _db = JsonSerializer.Deserialize<Db>(File.ReadAllText(_path), Opts) ?? new(); }
    catch { _db = new(); }
    _db.Allow ??= new(StringComparer.OrdinalIgnoreCase);
    _db.Holdings ??= new();
  }

  void Save()
  {
    try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_db, Opts)); }
    catch { }
  }

  static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

  /// <summary>A player deposited a card into custody — record a held holding owned by them.</summary>
  public Holding RecordDeposit(string owner, string card, int catId, int qty)
  {
    var h = new Holding { Id = NewId(), Owner = owner, Card = card, CatId = catId, Qty = Math.Max(1, qty),
      Borrower = null, Status = "held", AcquiredAt = DateTime.UtcNow.ToString("o") };
    lock (_lock) { _db.Holdings.Add(h); Save(); }
    return h;
  }

  /// <summary>The bot lent one of its OWN cards out — record a house holding on loan with the borrower.</summary>
  public Holding RecordHouseLoan(string card, int catId, int qty, string borrower)
  {
    var h = new Holding { Id = NewId(), Owner = House, Card = card, CatId = catId, Qty = Math.Max(1, qty),
      Borrower = borrower, Status = "onloan", AcquiredAt = DateTime.UtcNow.ToString("o") };
    lock (_lock) { _db.Holdings.Add(h); Save(); }
    return h;
  }

  /// <summary>
  /// A player WITHDREW something they had in custody (e.g. tix going back out) — the
  /// opposite of a deposit, and NOT a loan: nothing is owed back. Consumes up to
  /// <paramref name="qty"/> from their oldest 'held' holdings of that card (reduce, close
  /// zeroed rows). Returns how much was actually consumed — less than qty means the rest
  /// wasn't tracked here (fungible-claim accounting lives in DraftBot).
  /// </summary>
  public int ConsumeDeposits(string owner, string card, int qty)
  {
    int remaining = Math.Max(0, qty);
    lock (_lock)
    {
      foreach (var h in _db.Holdings
        .Where(h => h.Status == "held"
                 && string.Equals(h.Owner, owner, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(h.Card, card, StringComparison.OrdinalIgnoreCase))
        .OrderBy(h => h.AcquiredAt).ToList())
      {
        if (remaining <= 0) break;
        int take = Math.Min(h.Qty, remaining);
        h.Qty -= take; remaining -= take;
        h.UpdatedAt = DateTime.UtcNow.ToString("o");
        if (h.Qty <= 0) h.Status = "closed";
      }
      if (remaining < qty) Save();
    }
    return qty - remaining;
  }

  /// <summary>A card came back from its borrower. House cards close (back in the collection); a
  /// player-owned card returns to "held" in custody. Returns false if unknown/not-on-loan.</summary>
  public bool SettleReturn(string id)
  {
    lock (_lock)
    {
      var h = _db.Holdings.FirstOrDefault(x => x.Id == id);
      if (h is null || h.Status != "onloan") return false;
      h.Borrower = null; h.UpdatedAt = DateTime.UtcNow.ToString("o");
      h.Status = string.Equals(h.Owner, House, StringComparison.OrdinalIgnoreCase) ? "closed" : "held";
      Save();
      return true;
    }
  }

  public Holding? Get(string id) { lock (_lock) { return _db.Holdings.FirstOrDefault(h => h.Id == id); } }

  /// <summary>Active holdings (not closed), on-loan first, then newest.</summary>
  public List<Holding> Active()
  {
    lock (_lock)
    {
      return _db.Holdings.Where(h => h.Status != "closed")
        .OrderBy(h => h.Status == "onloan" ? 0 : 1).ThenByDescending(h => h.AcquiredAt).ToList();
    }
  }

  /// <summary>The allow-list for an owner (who may borrow their cards). Empty => anyone.</summary>
  public List<string> OwnerAllow(string owner)
  {
    lock (_lock) { return _db.Allow.TryGetValue(owner, out var l) ? new List<string>(l) : new List<string>(); }
  }

  public void SetOwnerAllow(string owner, IEnumerable<string> borrowers)
  {
    lock (_lock)
    {
      _db.Allow[owner] = borrowers.Select(b => (b ?? "").Trim()).Where(b => b.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      Save();
    }
  }

  /// <summary>May <paramref name="borrower"/> borrow <paramref name="owner"/>'s cards? Empty allow-list = anyone.</summary>
  public bool CanLend(string owner, string borrower)
  {
    var allow = OwnerAllow(owner);
    return allow.Count == 0 || allow.Any(b => string.Equals(b, borrower, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>Snapshot of all owner allow-lists (for the dashboard).</summary>
  public Dictionary<string, List<string>> AllAllow()
  {
    lock (_lock) { return _db.Allow.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase); }
  }
}
