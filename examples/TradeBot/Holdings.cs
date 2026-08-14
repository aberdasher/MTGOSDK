/** @file
  Ledger — an append-only record of what physically crossed the MTGO boundary. Each entry
  says a quantity of an exact printing moved IN (a player handed it to the custodian) or OUT
  (the custodian handed it to a player), when, and as part of which job.

  This is deliberately NOT an ownership or custody ledger, and it holds no claims:

    * WHO IS OWED WHAT lives in DraftBot's wallet ledger, which is double-entry and whose
      stated invariant is `vault tix == SUM(all wallet rows)`. Claims move between holders
      there constantly WITHOUT anything physical happening — a tournament entry fee is a
      transfer into a prize wallet, net zero, no MTGO trade. Mirroring any of that here
      would create a second source of truth with no transactional link to the first.
    * Likewise obligations (a borrower owing a card back) are claims, so they belong to
      DraftBot too. The bot still EXECUTES a recall — a receive-only trade pinned to an
      exact printing — but the caller supplies the printing; the bot does not remember it.

  So entries are immutable: never decremented, never "consumed", never closed. The balance
  question this file used to try to answer ("how much does X still have with us?") is not
  answerable here and should not be asked of it — the honest answer needs DraftBot.

  Persisted to ~/mtgosdk-tradebot/ledger.json so it survives restarts. Kept SEPARATE from
  ledger.log, the executor's own text record: that one is written by the trade-completed
  event handler and so survives even when a job is reported failed, which is what made a
  real accounting divergence detectable. Two independent records is the point.
**/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TradeBot;

/// <summary>One asset movement across the MTGO boundary. Immutable once written.</summary>
public sealed class LedgerEntry
{
  public string Id { get; set; } = "";
  public string Kind { get; set; } = "deposit";  // deposit = came IN to us | withdraw = went OUT to them
  public string User { get; set; } = "";         // the counterparty, NOT an owner of a claim
  public string Card { get; set; } = "";
  public int CatId { get; set; }                 // the EXACT printing that moved
  public int Qty { get; set; } = 1;
  public string At { get; set; } = "";           // ISO-8601 UTC
  public string? JobId { get; set; }             // the serve job this movement belonged to
}

/// <summary>Thread-safe, disk-backed, append-only ledger. One instance owned by the serve.</summary>
public sealed class LedgerStore
{
  sealed class Db { public List<LedgerEntry> Entries { get; set; } = new(); }

  readonly string _path;
  readonly object _lock = new();
  Db _db = new();
  static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

  public static string DefaultPath =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "ledger.json");

  public LedgerStore(string? path = null) { _path = path ?? DefaultPath; Load(); }

  void Load()
  {
    try { if (File.Exists(_path)) _db = JsonSerializer.Deserialize<Db>(File.ReadAllText(_path), Opts) ?? new(); }
    catch { _db = new(); }
    _db.Entries ??= new();
  }

  void Save()
  {
    try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_db, Opts)); }
    catch { }
  }

  static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

  /// <summary>Record a movement. The only writer — there is no update and no delete.</summary>
  public LedgerEntry Record(string kind, string user, string card, int catId, int qty, string? jobId = null)
  {
    var e = new LedgerEntry
    {
      Id = NewId(), Kind = kind, User = user, Card = card, CatId = catId,
      Qty = Math.Max(1, qty), At = DateTime.UtcNow.ToString("o"), JobId = jobId,
    };
    lock (_lock) { _db.Entries.Add(e); Save(); }
    return e;
  }

  public LedgerEntry RecordDeposit(string user, string card, int catId, int qty, string? jobId = null)
    => Record("deposit", user, card, catId, qty, jobId);

  public LedgerEntry RecordWithdraw(string user, string card, int catId, int qty, string? jobId = null)
    => Record("withdraw", user, card, catId, qty, jobId);

  /// <summary>Most recent entries first, newest <paramref name="limit"/> only.</summary>
  public List<LedgerEntry> Recent(int limit = 200)
  {
    lock (_lock) { return _db.Entries.OrderByDescending(e => e.At).Take(Math.Max(1, limit)).ToList(); }
  }

  public int Count { get { lock (_lock) return _db.Entries.Count; } }

  /// <summary>
  /// Net quantity per card across the whole ledger (deposits minus withdrawals) — a
  /// reconciliation aid against the physical vault, NOT a per-user balance. Deliberately
  /// aggregate: per-user balances are DraftBot's, and computing one here would be wrong
  /// the moment a claim moves between holders without a trade.
  /// </summary>
  public Dictionary<string, int> NetByCard()
  {
    lock (_lock)
    {
      var net = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (var e in _db.Entries)
      {
        int sign = string.Equals(e.Kind, "withdraw", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        net[e.Card] = (net.TryGetValue(e.Card, out var v) ? v : 0) + sign * e.Qty;
      }
      return net;
    }
  }
}
