/** @file
  Loan ledger for the lending vault. Every card the bot LENDS is recorded as an OPEN loan with
  the borrower and the EXACT printing (catId) handed over — so it can recall the right copy and
  settle that specific loan. Persisted to ~/mtgosdk-tradebot/loans.json (beside the trade ledger)
  so it survives serve restarts.
**/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TradeBot;

public sealed class Loan
{
  public string Id { get; set; } = "";
  public string Borrower { get; set; } = "";
  public string Card { get; set; } = "";
  public int CatId { get; set; }                 // the EXACT printing that was lent
  public int Qty { get; set; } = 1;
  public string LentAt { get; set; } = "";       // ISO-8601 UTC
  public string Status { get; set; } = "open";   // open | returned
  public string? ReturnedAt { get; set; }
}

/// <summary>Thread-safe, disk-backed store of loans. One instance owned by the serve.</summary>
public sealed class LoanStore
{
  readonly string _path;
  readonly object _lock = new();
  List<Loan> _loans = new();
  static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

  public static string DefaultPath =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "loans.json");

  public LoanStore(string? path = null) { _path = path ?? DefaultPath; Load(); }

  void Load()
  {
    try { if (File.Exists(_path)) _loans = JsonSerializer.Deserialize<List<Loan>>(File.ReadAllText(_path), Opts) ?? new(); }
    catch { _loans = new(); }
  }

  void Save()
  {
    try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_loans, Opts)); }
    catch { }
  }

  /// <summary>Record a new OPEN loan (a card just lent out). Returns it.</summary>
  public Loan Record(string borrower, string card, int catId, int qty)
  {
    var loan = new Loan
    {
      Id = Guid.NewGuid().ToString("N").Substring(0, 12),
      Borrower = borrower, Card = card, CatId = catId, Qty = Math.Max(1, qty),
      LentAt = DateTime.UtcNow.ToString("o"), Status = "open",
    };
    lock (_lock) { _loans.Add(loan); Save(); }
    return loan;
  }

  /// <summary>All loans, open ones first, newest first.</summary>
  public List<Loan> All()
  {
    lock (_lock) { return _loans.OrderBy(l => l.Status == "open" ? 0 : 1).ThenByDescending(l => l.LentAt).ToList(); }
  }

  public List<Loan> Open() { lock (_lock) { return _loans.Where(l => l.Status == "open").ToList(); } }

  public Loan? Get(string id) { lock (_lock) { return _loans.FirstOrDefault(l => l.Id == id); } }

  /// <summary>Mark a loan returned. Returns false if unknown or already returned.</summary>
  public bool MarkReturned(string id)
  {
    lock (_lock)
    {
      var l = _loans.FirstOrDefault(x => x.Id == id);
      if (l is null || l.Status == "returned") return false;
      l.Status = "returned"; l.ReturnedAt = DateTime.UtcNow.ToString("o"); Save();
      return true;
    }
  }

  /// <summary>Total quantity currently on loan, by exact catId (for the vault "owned + on-loan" view).</summary>
  public Dictionary<int, int> OnLoanByCat()
  {
    lock (_lock)
    {
      var d = new Dictionary<int, int>();
      foreach (var l in _loans.Where(x => x.Status == "open")) d[l.CatId] = (d.TryGetValue(l.CatId, out var q) ? q : 0) + l.Qty;
      return d;
    }
  }
}
