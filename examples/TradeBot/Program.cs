/** @file
  TradeBot prototype driver.

  Modes:
    probe            (default) READ-ONLY: validate the trade-execution surface is
                     callable on the live objects. No state change, no assets.
    watch            READ-ONLY: subscribe to trade lifecycle events and log them.
    post "<msg>"     OUTWARD-FACING: publish a marketplace message listing, then
                     clear it. Requires --yes (publishes public content).
    clearpost        OUTWARD-FACING: retract your marketplace listing. Requires --yes.
    invite <user>    OUTWARD-FACING: send a trade invite. Requires --yes.

  The committing final-approve is never invoked here (AllowCommit stays false).
**/

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;

using TradeBot;

static void Line(string s = "") => Console.WriteLine(s);

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
bool yes = args.Contains("--yes");
string arg1 = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";

Line("=== MTGOSDK TradeBot prototype ===");
Line($"mode={mode}  allowCommit=false (hard-coded off)\n");

// Ledger view needs no MTGO connection.
if (mode == "ledger")
{
  var lp = TradeBot.TradeExecutor.LedgerPath;
  Line($"Acquisition ledger: {lp}");
  if (System.IO.File.Exists(lp))
    foreach (var l in System.IO.File.ReadAllLines(lp)) Line("  " + l);
  else
    Line("  (no acquisitions recorded yet)");
  return;
}

if (Process.GetProcessesByName("MTGO").Length == 0)
{
  Line("MTGO is not running. Launch + log in first."); Environment.Exit(2);
}

using ILoggerFactory factory = LoggerFactory.Create(b =>
{
  b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
  b.SetMinimumLevel(LogLevel.Warning);   // keep SDK chatter down; our own output uses Console
});

Client client;
try { client = new Client(new ClientOptions(), loggerFactory: factory); }
catch (Exception ex) { Line($"Failed to attach to MTGO: {ex.GetType().Name}: {ex.Message}"); Environment.Exit(1); return; }

using (client)
using (var exec = new TradeExecutor { AllowCommit = false })
{
  if (!client.IsConnected || !client.IsLoggedIn)
  {
    Line("Client running but not fully logged in — complete MTGO login and retry.");
    return;
  }
  Line($"Connected as {client.CurrentUser.Name} (MTGO {Client.Version})\n");

  switch (mode)
  {
    case "probe":
      exec.ProbeExecutionSurface();
      break;

    case "watch":
      exec.Attach();
      Line("Watching trade events for 60s. Open/close a trade in MTGO to see events...");
      System.Threading.Thread.Sleep(TimeSpan.FromSeconds(60));
      break;

    case "post":
      if (!yes) { Line("Refusing: 'post' publishes a public marketplace listing. Re-run with --yes to confirm."); break; }
      exec.PublishMessagePost(arg1.Length > 0 ? arg1 : "MTGOSDK TradeBot test listing");
      Line("Reading back MyPost...");
      var mine = MTGOSDK.API.Trade.TradeManager.MyPost;
      Line(mine is null ? "  MyPost: <none>" : $"  MyPost: format={mine.Format} | \"{mine.Message}\"");
      Line("Clearing the test listing...");
      exec.ClearPost();
      break;

    case "clearpost":
      if (!yes) { Line("Refusing: re-run with --yes to retract your listing."); break; }
      exec.ClearPost();
      break;

    case "invite":
      if (arg1.Length == 0) { Line("Usage: invite <username> --yes"); break; }
      if (!yes) { Line($"Refusing: 'invite' sends a trade invite to '{arg1}'. Re-run with --yes to confirm."); break; }
      var escrow = exec.RequestTrade(arg1);
      Line($"CurrentTrade: {(escrow is null ? "<null for now>" : $"state={escrow.State} partner={escrow.TradePartnerName}")}");
      break;

    case "find":
    {
      string kw = (arg1.Length > 0 ? arg1 : "free").ToLowerInvariant();
      Line($"Scanning live marketplace listings for \"{kw}\" (poster name or message)...");
      var all = MTGOSDK.API.Trade.TradeManager.AllPosts.ToList();
      Line($"Total live posts: {all.Count}\n");
      int n = 0;
      foreach (var p in all)
      {
        string poster; try { poster = p.Poster.Name; } catch { poster = "?"; }
        string msg = (p.Message ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        if (!poster.ToLowerInvariant().Contains(kw) && !msg.ToLowerInvariant().Contains(kw))
          continue;
        n++;
        int offered = 0, wanted = 0;
        try { offered = p.Offered.Count(); } catch { }
        try { wanted = p.Wanted.Count(); } catch { }
        Line($"{n,3}. {poster,-24} offered={offered} wanted={wanted}");
        if (msg.Length > 160) msg = msg.Substring(0, 160) + "…";
        Line($"      {msg}");
      }
      Line($"\nMatched {n} listing(s) for \"{kw}\".");
      break;
    }

    case "testcancel":
    {
      if (arg1.Length == 0) { Line("Usage: testcancel <botname> --yes"); break; }
      if (!yes) { Line($"Refusing: initiates a real trade with '{arg1}' then immediately cancels. Re-run with --yes."); break; }
      Line("Escape-hatch test: initiate -> hold 5s -> cancel -> verify cleared.\n");
      try { exec.RequestTrade(arg1); }
      catch (Exception ex) { Line($"(RequestTrade threw: {ex.Message.Split('\n')[0]} — escrow likely still opened; proceeding to cancel)"); }
      System.Threading.Thread.Sleep(5000);
      MTGOSDK.API.Trade.TradeEscrow? before = null;
      try { before = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
      Line($"before cancel: {(before is null ? "<none>" : before.State.ToString())}");
      exec.CancelCurrent();
      System.Threading.Thread.Sleep(3000);
      MTGOSDK.API.Trade.TradeEscrow? after = null;
      try { after = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
      Line($"AFTER cancel:  {(after is null ? "<none — CLEARED OK>" : after.State.ToString() + " (NOT cleared)")}");
      break;
    }

    case "opentrade":
    {
      if (arg1.Length == 0) { Line("Usage: opentrade <botname> --yes"); break; }
      if (!yes) { Line($"Refusing: sends a trade invite to '{arg1}'. Re-run with --yes."); break; }
      exec.Attach();
      try { exec.RequestTrade(arg1); }
      catch (Exception ex) { Line($"(initiate threw but escrow likely opened: {ex.Message.Split('\n')[0]})"); }
      System.Threading.Thread.Sleep(1500);
      var e = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
      if (e != null && e.State == MTGOSDK.API.Trade.Enums.TradeState.InviteSelectBinder)
        exec.AdvanceBinderSelection(e);

      bool open = false;
      for (int i = 0; i < 40; i++)
      {
        System.Threading.Thread.Sleep(1000);
        var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (c is null) { Line("Trade closed before negotiation — the freebot likely declined/timed out (throttling). Try again shortly."); break; }
        if (i % 3 == 0) Line($"  t+{i,2}s state={c.State}");
        if (c.State.ToString().StartsWith("Negotiate"))
        {
          Line($"\n>>> TRADE OPEN with {c.TradePartnerName} (state={c.State}). The window should be visible. <<<");
          Line("Leaving it open — run `takecard <name> --yes` next to grab a specific card.");
          open = true; break;
        }
      }
      if (!open) Line("Did not reach negotiation. (No cleanup needed — nothing stranded past the binder step.)");
      // NOTE: intentionally NOT cancelling — leave the trade open for takecard.
      break;
    }

    case "takecard":
    {
      if (arg1.Length == 0) { Line("Usage: takecard <cardname> --yes  (uses your currently-open trade)"); break; }
      if (!yes) { Line($"Refusing: this adds '{arg1}' to your live trade (non-committing). Re-run with --yes."); break; }
      var esc = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
      if (esc is null) { Line("No active trade — open a trade with the bot in MTGO first."); break; }
      if (!esc.State.ToString().StartsWith("Negotiate")) { Line($"Trade must be in negotiation (state={esc.State})."); break; }

      // Find the requested card in the partner's binder.
      System.Collections.Generic.List<MTGOSDK.API.Collection.CardQuantityPair> items;
      try { items = esc.PartnerCollection.CollectionItems; }
      catch (Exception ex) { Line($"PartnerCollection read failed: {ex.Message}"); break; }
      MTGOSDK.API.Collection.CardQuantityPair? match = null;
      foreach (var it in items)
      {
        string nm; try { nm = it.Card?.Name ?? ""; } catch { nm = ""; }
        if (nm.ToLowerInvariant().Contains(arg1.ToLowerInvariant())) { match = it; break; }
      }
      if (match is null) { Line($"'{arg1}' not offered by {esc.TradePartnerName}."); break; }
      int catId = -1; string cname = "?"; int avail = 0;
      try { catId = match.Id; } catch { }
      try { cname = match.Card?.Name ?? "?"; } catch { }
      try { avail = match.Quantity; } catch { }
      Line($"Found {avail}x {cname} (catId={catId}). Requesting 1 into the trade...");

      exec.TakeCard(esc, catId, 1);
      System.Threading.Thread.Sleep(3000);

      var esc2 = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
      if (esc2 is null) { Line("Trade no longer active after request (unexpected)."); break; }
      Line($"state={esc2.State}");
      try { Line($"  WE RECEIVE: {TradeBot.TradeExecutor.Summarize(esc2.PartnerTradedItems)}"); } catch (Exception ex) { Line($"  (receive read failed: {ex.Message})"); }
      try { Line($"  WE GIVE:    {TradeBot.TradeExecutor.Summarize(esc2.TradedItems)}"); } catch { }
      Line("\n(No approve sent — review the trade in MTGO and confirm it yourself.)");
      break;
    }

    case "readcurrent":
    {
      // READ-ONLY: inspect the trade the USER already has open. No initiate, no
      // cancel, no modification — safe against a live user trade.
      var esc = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
      if (esc is null) { Line("No active trade — open one in MTGO first."); break; }
      Line($"Active trade: partner={esc.TradePartnerName}  state={esc.State}");
      string filter = arg1.ToLowerInvariant();
      System.Collections.Generic.List<MTGOSDK.API.Collection.CardQuantityPair> items;
      try { items = esc.PartnerCollection.CollectionItems; }
      catch (Exception ex) { Line($"PartnerCollection read failed: {ex.GetType().Name}: {ex.Message}"); break; }
      Line($"Partner offers {items.Count} item stack(s)." + (filter.Length > 0 ? $"  Matching \"{arg1}\":" : "  (first 40)"));
      int shown = 0;
      foreach (var it in items)
      {
        string name; int qty = -1, cat = -1;
        try { name = it.Card?.Name ?? "?"; } catch { name = "?"; }
        try { qty = it.Quantity; } catch { }
        try { cat = it.Id; } catch { }
        if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter)) continue;
        shown++;
        Line($"  {qty}x  catId={cat}  {name}");
        if (shown >= 40) { Line("  ...(more)"); break; }
      }
      if (shown == 0) Line(filter.Length > 0 ? $"  (no match for \"{arg1}\")" : "  (empty)");
      break;
    }

    case "inspecttrade":
    {
      if (arg1.Length == 0) { Line("Usage: inspecttrade <botname> --yes"); break; }
      if (!yes) { Line($"Refusing: initiates a real trade with '{arg1}' (non-committing, auto-cancels). Re-run with --yes."); break; }
      Line("Inspect: initiate -> advance -> reach negotiation -> dump partner offerings -> auto-cancel.\n");
      exec.Attach();
      try
      {
        try { exec.RequestTrade(arg1); }
        catch (Exception ex) { Line($"(initiate threw but escrow likely opened: {ex.Message.Split('\n')[0]})"); }
        System.Threading.Thread.Sleep(1500);
        var esc0 = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (esc0 != null && esc0.State == MTGOSDK.API.Trade.Enums.TradeState.InviteSelectBinder)
          exec.AdvanceBinderSelection(esc0);

        // wait for negotiation
        bool inspected = false;
        for (int i = 0; i < 40 && !inspected; i++)
        {
          System.Threading.Thread.Sleep(1000);
          var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
          if (c is null) { Line("trade closed before negotiation."); break; }
          if (c.State.ToString().StartsWith("Negotiate"))
          {
            System.Threading.Thread.Sleep(2000); // let the freebot load its binder
            exec.InspectNegotiation(c);
            inspected = true;
          }
        }
      }
      finally
      {
        exec.CancelCurrent();
        System.Threading.Thread.Sleep(1500);
        MTGOSDK.API.Trade.TradeEscrow? f = null;
        try { f = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        Line($"Final: CurrentTrade={(f is null ? "<none — CLEARED OK>" : f.State.ToString() + " (NOT cleared)")}");
      }
      break;
    }

    case "advance":
    {
      if (arg1.Length == 0) { Line("Usage: advance <botname> --yes"); break; }
      if (!yes) { Line($"Refusing: initiates a real trade with '{arg1}' (non-committing). Re-run with --yes."); break; }
      Line("Headless test: initiate -> advance past binder dialog -> observe -> auto-cancel.\n");
      exec.Attach();
      bool skipCancel = false;
      try
      {
        try { exec.RequestTrade(arg1); }
        catch (Exception ex) { Line($"(initiate threw but escrow likely opened: {ex.Message.Split('\n')[0]})"); }
        System.Threading.Thread.Sleep(1500);

        var esc = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        Line($"after initiate: state={(esc is null ? "<none>" : esc.State.ToString())}");
        if (esc != null && esc.State == MTGOSDK.API.Trade.Enums.TradeState.InviteSelectBinder)
          exec.AdvanceBinderSelection(esc);

        for (int i = 0; i < 45; i++)
        {
          System.Threading.Thread.Sleep(1000);
          MTGOSDK.API.Trade.TradeEscrow? c = null;
          try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
          if (c is null) { Line("trade closed."); break; }
          if (i % 3 == 0) Line($"  t+{i,2}s state={c.State}");
          if (c.State.ToString().StartsWith("Approval")) { Line("approval reached — handing off (no cancel)."); skipCancel = true; break; }
          if (c.State == MTGOSDK.API.Trade.Enums.TradeState.Closed) break;
        }
      }
      finally
      {
        if (!skipCancel) { exec.CancelCurrent(); System.Threading.Thread.Sleep(1500); }
        MTGOSDK.API.Trade.TradeEscrow? f = null;
        try { f = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        Line($"Final: CurrentTrade={(f is null ? "<none — CLEARED OK>" : f.State.ToString() + " (NOT cleared)")}");
      }
      break;
    }

    case "grab":
    {
      if (arg1.Length == 0) { Line("Usage: grab <botname> --yes"); break; }
      if (!yes) { Line($"Refusing: 'grab' initiates a real trade with '{arg1}'. Re-run with --yes."); break; }

      exec.Attach();
      bool userConfirming = false;
      try
      {
        // RequestTrade opens the escrow but may throw an intermittent WPF error
        // while showing the (invisible) binder dialog — tolerate it and keep going.
        try { exec.RequestTrade(arg1); }
        catch (Exception ex) { Line($"(initiate threw but escrow likely opened: {ex.Message.Split('\n')[0]})"); }
        System.Threading.Thread.Sleep(1500);

        // Advance PAST the broken/invisible binder-selection dialog so MTGO opens
        // the real, VISIBLE trade window that you can interact with.
        var eInit = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (eInit != null && eInit.State == MTGOSDK.API.Trade.Enums.TradeState.InviteSelectBinder)
        {
          exec.AdvanceBinderSelection(eInit);
          System.Threading.Thread.Sleep(1000);
        }
        string initState = "<pending>";
        try { var e0 = MTGOSDK.API.Trade.TradeManager.CurrentTrade; if (e0 != null) initState = e0.State.ToString(); } catch { }
        Line($"\nInitiated + advanced (state={initState}).");
        Line($">>> In MTGO now: the trade window with {arg1} should be VISIBLE — grab a free card and CONFIRM. <<<");
        Line("    The bot advanced past the binder dialog for you and is staying alive; it will");
        Line("    AUTO-CANCEL if you don't finish, and will NOT send the final approve — that's yours.\n");

        const int windowSec = 180;
        for (int i = 0; i < windowSec; i++)
        {
          System.Threading.Thread.Sleep(1000);
          MTGOSDK.API.Trade.TradeEscrow? c = null;
          try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
          if (c is null) { Line("Trade is no longer active (completed or closed) — done."); break; }
          var st = c.State;
          if (i % 5 == 0) Line($"  t+{i,3}s  state={st}");
          if (st.ToString().StartsWith("Approval") && !userConfirming)
          {
            // Stay ATTACHED through approval so the observer captures completion and
            // writes the acquisition ledger. Bot will not cancel and will not commit.
            Line("You're in the approval phase — staying attached to record the acquisition (no cancel, no commit).");
            userConfirming = true;
          }
          if (st == MTGOSDK.API.Trade.Enums.TradeState.Closed) { Line("Trade closed."); break; }
        }
      }
      finally
      {
        if (!userConfirming)
        {
          exec.CancelCurrent();
          System.Threading.Thread.Sleep(1500);
        }
        MTGOSDK.API.Trade.TradeEscrow? f = null;
        try { f = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        Line($"Final: CurrentTrade={(f is null ? "<none — client is clear>" : f.State.ToString())}");
      }
      break;
    }

    default:
      Line($"Unknown mode '{mode}'. Use: probe | find [keyword] | watch | post | clearpost | invite | grab");
      break;
  }

  Line("\nDone. (AllowCommit was false the entire run — no trade was committed.)");
}
