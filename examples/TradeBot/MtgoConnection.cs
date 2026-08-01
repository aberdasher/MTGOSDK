/** @file
  Keeps the serve process's MTGO connection alive across client restarts.

  The HTTP listener never depends on MTGO, but the worker's executor does: if the MTGO
  client restarts (crash, update, manual restart), the diver connection dies and every
  trade would fail. This manager detects that (a cheap Client.HasStarted check + a
  CurrentUser RPC probe) and RE-ATTACHES to MTGO once it's back and logged in — swapping in
  a fresh executor. Re-attach (not cold-start) is the auto path: a cold-start re-login would
  need the human's MFA, but re-attaching to a client the user logged back in needs nothing.
  Reconnect blocks the worker (jobs queue) rather than failing during an outage.

  Locking: a single reconnect GATE serializes reconnects; current client/exec/account are
  volatile so getters (and /health) never block behind an in-progress reconnect.
**/
using System;
using System.Diagnostics;
using System.Threading;

using MTGOSDK.API;

namespace TradeBot;

public sealed class MtgoConnection : IDisposable
{
  // Creates a FRESH attached client + ready executor (new Client(attach) -> wait for login
  // -> new TradeExecutor + Attach()). Returns (null,null,null) if it couldn't settle.
  readonly Func<(Client? client, TradeExecutor? exec, string? account)> _attach;
  readonly Action<string> _log;
  readonly SemaphoreSlim _reconnectGate = new(1, 1);

  volatile Client _client;
  volatile TradeExecutor _exec;
  volatile string _account;
  volatile bool _reconnecting;

  public MtgoConnection(Client client, TradeExecutor exec, string account,
    Func<(Client?, TradeExecutor?, string?)> attach, Action<string> log)
  {
    _client = client; _exec = exec; _account = account; _attach = attach; _log = log;
  }

  // Lock-free reads (reference/bool reads are atomic; volatile adds ordering).
  public TradeExecutor Exec => _exec;
  public string Account => _account;
  public bool Reconnecting => _reconnecting;

  /// <summary>Cheap liveness probe: MTGO running + diver up + a CurrentUser RPC that a dead
  /// connection would throw on. HasStarted is checked first so a gone client doesn't wait on
  /// an RPC timeout.</summary>
  public bool IsHealthy()
  {
    try
    {
      if (Process.GetProcessesByName("MTGO").Length == 0) return false;
      if (!Client.HasStarted) return false;
      var u = _client.CurrentUser;              // RPC — throws if the diver connection is dead
      return u != null && u.Id > 0;
    }
    catch { return false; }
  }

  /// <summary>Return once the connection is healthy, reconnecting (BLOCKING) if it isn't.
  /// maxWaitSec == 0 waits indefinitely; otherwise gives up after that many seconds.</summary>
  public bool EnsureHealthy(int maxWaitSec = 0)
  {
    if (IsHealthy()) return true;
    _reconnectGate.Wait();
    try
    {
      if (IsHealthy()) return true;             // another thread already reconnected
      _reconnecting = true;
      _log("[serve] MTGO connection lost — reconnecting (waiting for MTGO to be back + logged in)...");
      try { _exec?.Dispose(); } catch { }
      try { _client?.Dispose(); } catch { }

      int waited = 0;
      while (true)
      {
        try
        {
          // Always call _attach — it relaunches MTGO itself when the process is gone (and
          // just re-attaches when MTGO is up but only the diver dropped). Guarding this
          // behind "is MTGO running" would prevent the relaunch and wait forever.
          var (c, e, acct) = _attach();
          if (c != null && e != null)
          {
            _client = c; _exec = e; _account = acct ?? _account;
            _reconnecting = false;
            _log($"[serve] reconnected as {_account}.");
            return true;
          }
        }
        catch (Exception ex) { _log($"[serve] reconnect attempt failed: {ex.Message.Split('\n')[0]}"); }

        Thread.Sleep(5000);
        waited += 5;
        if (maxWaitSec > 0 && waited >= maxWaitSec) { _reconnecting = false; return false; }
      }
    }
    finally { _reconnectGate.Release(); }
  }

  public void Dispose()
  {
    try { _exec?.Dispose(); } catch { }
    try { _client?.Dispose(); } catch { }
  }
}
