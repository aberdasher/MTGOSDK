/** @file
  TradeBot prototype driver.

  Modes:
    probe            (default) READ-ONLY: validate the trade-execution surface is
                     callable on the live objects. No state change, no assets.
    watch            READ-ONLY: subscribe to trade lifecycle events and log them.
    botstatus <bot>  READ-ONLY: read a bot's marketplace post + classify it
                     Open/Busy/Unknown (how it signals availability).
    poolstatus       READ-ONLY: availability sweep across a pool of bots
                     (default freebots; override with --bots=a,b,c).
    autograb <bot> <card>
                     Wait for <bot> to read OPEN, then open a trade and stage
                     <card>, holding for your Submit. Never commits. Requires --yes.
    poolgrab <card>  Rotate across a POOL of bots: invite only ones reading OPEN,
                     move to the next instead of spamming a busy one, stage <card>
                     on the first that reaches negotiation. Requires --yes.
    post "<msg>"     OUTWARD-FACING: publish a marketplace message listing, then
                     clear it. Requires --yes (publishes public content).
    clearpost        OUTWARD-FACING: retract your marketplace listing. Requires --yes.
    invite <user>    OUTWARD-FACING: send a trade invite. Requires --yes.

  Availability gating: bots advertise "open"/"free" vs "busy" in their post
  message; the acquire modes read that (read-only, no ping) and only invite open
  bots. Classification tokens live in BotAvailability.cs — tune them against what
  `botstatus`/`poolstatus` print for live bots.

  The committing final-approve is never invoked here (AllowCommit stays false).

  Login: by default the bot launches MTGO (if needed) and logs in using
  credentials from a .env file at the repo root (USERNAME= / PASSWORD=, see
  .env-example) or environment variables of the same names. Pass --attach-only
  to skip all of that and require an already-running, hand-logged-in client.
**/

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.Core.Security;

using TradeBot;

static void Line(string s = "") => Console.WriteLine(s);

// Reads the private chat channel bound to a trade and prints the last `last`
// messages. Used to confirm from chat that a trade session is actually live
// (the freebot greets you / echoes item updates in this channel). Read-only.
// Returns the number of messages seen (0 = channel missing/empty).
static int DumpChat(MTGOSDK.API.Trade.TradeEscrow e, int last = 10)
{
  MTGOSDK.API.Chat.Channel? ch = null;
  try { ch = e.ChatChannel; }
  catch (Exception ex) { Line($"  (chat channel unavailable: {ex.Message.Split('\n')[0]})"); return 0; }
  if (ch is null) { Line("  (no chat channel bound to this trade yet)"); return 0; }

  System.Collections.Generic.IList<MTGOSDK.API.Chat.Message> msgs;
  try { msgs = ch.Messages; }
  catch (Exception ex) { Line($"  (chat messages unavailable: {ex.Message.Split('\n')[0]})"); return 0; }

  string chName; try { chName = ch.Name; } catch { chName = "?"; }
  Line($"  chat channel \"{chName}\" — {msgs.Count} message(s):");
  int start = Math.Max(0, msgs.Count - last);
  for (int i = start; i < msgs.Count; i++)
  {
    string who = "?", txt = "", ts = "";
    try { who = msgs[i].User?.Name ?? "?"; } catch { }
    try { txt = (msgs[i].Text ?? "").Replace("\n", " ").Replace("\r", " ").Trim(); } catch { }
    try { ts = msgs[i].Timestamp.ToString("HH:mm:ss"); } catch { }
    Line($"    [{ts}] {who}: {txt}");
  }
  return msgs.Count;
}

// Parse an optional `--bots=a,b,c` override for the bot pool. Returns null when
// the flag is absent (callers fall back to BotPool.FreeBots).
static string[]? ParseBots(string[] argv)
{
  var flag = argv.FirstOrDefault(a => a.StartsWith("--bots=", StringComparison.OrdinalIgnoreCase));
  if (flag is null) return null;
  var list = flag.Substring("--bots=".Length)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  return list.Length > 0 ? list : null;
}

// Bounded wait until no trade is active (CurrentTrade == null). Used after a
// cancel so the NEXT invite isn't rejected with AlreadyTrading. A cancel is
// dispatched async, so poll rather than sleep a fixed time. Returns true if the
// client cleared. Tolerates transient nulls by requiring the null to persist.
static bool WaitForNoTrade(int maxMs = 6000)
{
  for (int waited = 0; waited < maxMs; waited += 500)
  {
    MTGOSDK.API.Trade.TradeEscrow? c = null;
    try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { c = null; }
    if (c is null) return true;
    System.Threading.Thread.Sleep(500);
  }
  return false;
}

// ONE invite -> advance-past-binder -> reach-negotiation attempt against `bot`.
// Returns the negotiating escrow, or null if it didn't get there (bot busy /
// declined / timed out). Does NOT retry internally — the caller decides whether
// to retry the same bot or ROTATE to a different one (so we never spam a bot).
static MTGOSDK.API.Trade.TradeEscrow? TryReachNegotiation(TradeBot.TradeExecutor exec, string bot, string? presentBinder = null, int negotiateWaitSec = 30)
{
  try { exec.RequestTrade(bot); }
  catch (Exception ex) { Line($"(initiate threw but escrow likely opened: {ex.Message.Split('\n')[0]})"); }

  // Advance past the (bot-invisible) binder-selection dialog as soon as the
  // escrow reports InviteSelectBinder. AdvanceBinderSelection dispatches
  // SendTradeInvitationReqAction — a real ping to the bot — so fire it AT MOST
  // ONCE per attempt; re-issuing it every poll (state can lag) would spam the
  // bot and trip its throttle. CurrentTrade can read null between ticks even on
  // a live escrow, so tolerate a few transient nulls before giving up.
  bool advanced = false;
  bool dispatched = false;
  int nulls = 0;
  for (int i = 0; i < 30 && !dispatched; i++)   // up to ~15s
  {
    System.Threading.Thread.Sleep(500);
    MTGOSDK.API.Trade.TradeEscrow? e0 = null;
    try { e0 = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { e0 = null; }
    if (e0 is null) { if (++nulls >= 4) return null; continue; } // escrow gone -> rotate
    nulls = 0;
    var st0 = e0.State;
    if (st0 == MTGOSDK.API.Trade.Enums.TradeState.InviteSelectBinder)
    {
      if (!advanced)
      {
        try
        {
          // If a specific binder was requested (GIVE side), make it the last-used
          // binder FIRST so the parameterless invite action presents ONLY it, then
          // dispatch the invite via the proven AdvanceBinderSelection (the dialog
          // OkCommand path does NOT advance the escrow — verified live).
          if (presentBinder != null) exec.SetLastUsedBinder(presentBinder);
          exec.AdvanceBinderSelection(e0);
          advanced = true;
        }
        catch (Exception ex) { Line($"(binder advance threw: {ex.Message.Split('\n')[0]})"); }
      }
    }
    else if (st0 != MTGOSDK.API.Trade.Enums.TradeState.Uninitialized)
    {
      dispatched = true; // moved to InviteSent / InviteAccepted / Negotiate...
    }
  }

  // Wait for negotiation (or an early close = bot busy/declined). Humans need more
  // room to click Accept than a bot does, so callers can widen this window.
  nulls = 0;
  for (int i = 0; i < negotiateWaitSec; i++)
  {
    System.Threading.Thread.Sleep(1000);
    MTGOSDK.API.Trade.TradeEscrow? esc = null;
    try { esc = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { esc = null; }
    if (esc is null) { if (++nulls >= 3) return null; continue; } // closed -> rotate/retry
    nulls = 0;
    if (esc.State.ToString().StartsWith("Negotiate")) return esc;
  }
  return null;
}

// STANDARD trade-initiation handshake for any flow that trades with a HUMAN
// (lend, swap, ...). The point: NEVER fire an invite into a 30-second accept race.
// Instead DM the partner a "reply YES" prompt and WAIT (default 5 min) for their
// YES — that both confirms they're available AND means they're watching, so the
// invite that follows is accepted immediately. Only after the YES do we clear any
// stale trade, initiate, and present the binder (with a wide accept window). No
// YES within the window => no invite is ever sent. Returns the negotiating escrow,
// or null (with a reason logged). This is what makes the flow run without
// babysitting: the human replies YES once, then the bot drives the rest.
static MTGOSDK.API.Trade.TradeEscrow? HandshakeThenInitiate(
    TradeBot.TradeExecutor exec, string partner, string readyMsg, string presentBinder, int yesTimeoutSec = 300)
{
  if (!exec.EnsureKnownUser(partner))
  { Line($"Could not resolve '{partner}' (spelling? they may need to accept the buddy request) — aborting."); return null; }

  try { exec.SendDM(partner, readyMsg); }
  catch (Exception ex) { Line($"DM failed: {ex.Message} — aborting."); return null; }

  Line($"Waiting up to {yesTimeoutSec / 60} min for {partner} to reply YES (no timer pressure — take your time)...");
  if (!exec.WaitForDMYes(partner, yesTimeoutSec))
  { Line($"No YES from {partner} within the window — aborting (no invite was sent)."); return null; }

  try { exec.SendDM(partner, "Great — sending the trade now; accept the invite when it pops up."); } catch { }

  // Clear any stale/wedged escrow, then initiate with a HUMAN-sized accept window.
  exec.CancelCurrent(); WaitForNoTrade();
  return TryReachNegotiation(exec, partner, presentBinder, negotiateWaitSec: 90);
}

// One SWAP cycle from a live negotiating escrow to completion: find the GET card in
// the partner's presented binder (cancel if unavailable), request it, wait until
// BOTH sides are EXACTLY right (cancel if incomplete), submit our deposit,
// re-verify, then (with allowCommit) approve. Moves nothing unless both sides
// match the guardrail. Returns true iff the swap committed.
static bool RunSwapCycle(TradeBot.TradeExecutor exec, MTGOSDK.API.Trade.TradeEscrow esc,
    string partner, string giveCard, int giveQty, System.Collections.Generic.List<string> getNames, bool allowCommit)
{
  string wantList = string.Join(", ", getNames);
  Line($"\nTrade open with {esc.TradePartnerName}. You should present: {wantList}. I'm offering {giveQty}x {giveCard} — grab it from my SwapOffer binder.");

  // WE GIVE must be EXACTLY our offer (safety); WE RECEIVE must contain every required card.
  bool GiveIsExactly(MTGOSDK.API.Trade.TradeEscrow t)
  {
    var g = new System.Collections.Generic.List<(string n, int q)>();
    try { foreach (var it in t.TradedItems.CollectionItems) g.Add((it.Card?.Name ?? "?", (int)it.Quantity)); } catch { return false; }
    return g.Count == 1 && g[0].q == giveQty && g[0].n.IndexOf(giveCard, StringComparison.OrdinalIgnoreCase) >= 0;
  }
  bool ReceiveHas(MTGOSDK.API.Trade.TradeEscrow t, string name)
  {
    try { foreach (var it in t.PartnerTradedItems.CollectionItems) if ((it.Card?.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
    return false;
  }

  // The required cards must be in the binder the partner PRESENTS (esc.PartnerCollection).
  // The presented binder is FIXED at trade start — the partner CANNOT change it mid-
  // trade — so a required card that isn't there now never will be. Therefore: as soon
  // as the trade is open (checkAtSec, giving their binder a moment to populate) and
  // any required card is absent from their presented offer, CANCEL immediately and DM
  // the exact missing list. This is NOT gated on them grabbing our offer. Cards they
  // DO present are requested so a complete offer can still finish.
  var requested = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
  bool bothReady = false;
  int lastDump = -100;
  const int overallSec = 40, checkAtSec = 5;

  for (int i = 0; i < overallSec; i++)
  {
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed before it completed."); return false; }
    esc = c;

    // What the partner PRESENTS right now (their presented binder is readable here).
    var shown = new System.Collections.Generic.Dictionary<string, (int cat, int qty)>(StringComparer.OrdinalIgnoreCase);
    try { foreach (var it in c.PartnerCollection.CollectionItems) { string nm = it.Card?.Name ?? ""; if (nm.Length > 0) shown[nm] = (it.Id, it.Quantity); } } catch { }

    var missing = getNames.Where(w => !shown.Keys.Any(k => k.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

    // Request each required card they DO present (so a complete offer can finish).
    foreach (var want in getNames)
    {
      if (requested.Contains(want)) continue;
      var hit = shown.Keys.FirstOrDefault(k => k.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
      if (hit != null) { Line($"  saw '{want}' (catId={shown[hit].cat}) — requesting it."); exec.RequestViaWishlist(shown[hit].cat, 1, want); requested.Add(want); }
    }

    var secured = getNames.Where(w => ReceiveHas(c, w)).ToList();
    bool grabbed = GiveIsExactly(c);

    if (i - lastDump >= 3)
    {
      lastDump = i;
      Line($"  [t+{i,3}s] you present: {(shown.Count == 0 ? "(nothing)" : string.Join(", ", shown.Keys.Take(12)))}  |  MISSING from your offer: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}  |  secured: {(secured.Count == 0 ? "(none)" : string.Join(", ", secured))}  |  you grabbed my {giveCard}: {(grabbed ? "yes" : "no")}");
    }

    // COMPLETE: every required card present + secured AND you grabbed my offer.
    if (missing.Count == 0 && getNames.All(w => ReceiveHas(c, w)) && grabbed) { bothReady = true; break; }

    // CANCEL AS SOON AS POSSIBLE: trade is open and a required card isn't in your offer.
    if (i >= checkAtSec && missing.Count > 0)
    {
      Line($"\n!!! CANCELLING — your offer is missing required card(s): {string.Join(", ", missing)}. !!!\n");
      exec.CancelCurrent(); WaitForNoTrade();
      try { exec.SendDM(partner, $"Trade cancelled — your offer is missing: {string.Join(", ", missing)}. (You can't change the shown binder mid-trade — set it up with those cards first, then reply YES to retry.)"); } catch { }
      return false;
    }
    System.Threading.Thread.Sleep(1000);
  }

  if (!bothReady)
  {
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    var missing = c == null ? getNames : getNames.Where(w => !ReceiveHas(c, w)).ToList();
    string why = missing.Count > 0 ? $"couldn't secure: {string.Join(", ", missing)}" : $"you didn't grab the {giveCard}";
    Line($"\n!!! CANCELLING — {why}. !!!\n");
    exec.CancelCurrent(); WaitForNoTrade();
    try { exec.SendDM(partner, $"Trade cancelled — {why}. Reply YES to retry."); } catch { }
    return false;
  }
  Line("All required cards secured and you grabbed my offer — finalizing.");

  // submit our deposit; wait for approval-ready.
  exec.SubmitDeposit();
  bool approveReady = false; string sst = "?";
  for (int i = 0; i < 30 && !approveReady; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed before approval."); return false; }
    esc = c; sst = c.State.ToString();
    if (i % 3 == 0) Line($"  t+{i,2}s state={sst}");
    if (sst.StartsWith("Approval")) approveReady = true;
  }
  if (!approveReady) { Line("Did not reach approval-ready — cancelling."); exec.CancelCurrent(); WaitForNoTrade(); return false; }

  // RE-VERIFY both sides right before committing (belt and suspenders).
  if (!(GiveIsExactly(esc) && getNames.All(w => ReceiveHas(esc, w))))
  {
    Line("Guardrail re-check FAILED at approval — cancelling (moves nothing).");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    try { exec.SendDM(partner, "Cancelled at the final check for safety."); } catch { }
    return false;
  }

  if (!allowCommit)
  {
    Line("\n[dry-run] Both sides ready + guardrail OK; no --commit -> cancelling (nothing moved).");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    Line("Re-run with `--commit --yes` to actually complete the swap.");
    return false;
  }

  Line("\n*** COMMITTING the swap (ConfirmTrade) ***");
  exec.ConfirmTrade();
  for (int i = 0; i < 40; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Swap complete — client clear."); break; }
    if (i % 3 == 0) Line($"  t+{i,2}s state={c.State}");
    if (c.State == MTGOSDK.API.Trade.Enums.TradeState.Closed) break;
  }
  try { exec.SendDM(partner, $"Done — you got {giveQty} {giveCard}, I got {wantList}. Thanks!"); } catch { }
  Line("\nLedger (latest):");
  var slp = TradeBot.TradeExecutor.LedgerPath;
  if (System.IO.File.Exists(slp)) foreach (var l in System.IO.File.ReadAllLines(slp).Reverse().Take(1)) Line("  " + l);
  return true;
}

// Given a negotiating escrow, stage the requested card (non-committing) and HOLD
// the session open while the user reviews + clicks Submit. Never commits
// (AllowCommit stays false); the final approve is the user's.
static void StageAndHold(TradeBot.TradeExecutor exec, MTGOSDK.API.Trade.TradeEscrow esc, string card)
{
  // The escrow could close between reaching negotiation and here; read its
  // header fields defensively (State/TradePartnerName are not internally guarded).
  string partner = "?", st = "?";
  try { partner = esc.TradePartnerName ?? "?"; } catch { }
  try { st = esc.State.ToString(); } catch { }
  Line($"\n>>> TRADE OPEN with {partner} (state={st}) <<<");
  Line("Confirming from trade chat that the session is live:");
  System.Threading.Thread.Sleep(2000);
  DumpChat(esc);

  // Stage the requested card into the trade (non-committing).
  System.Collections.Generic.List<MTGOSDK.API.Collection.CardQuantityPair> items;
  try { items = esc.PartnerCollection.CollectionItems; }
  catch (Exception ex) { Line($"PartnerCollection read failed: {ex.Message} — leaving trade open, nothing staged."); return; }

  MTGOSDK.API.Collection.CardQuantityPair? match = null;
  foreach (var it in items)
  {
    string nm; try { nm = it.Card?.Name ?? ""; } catch { nm = ""; }
    if (nm.ToLowerInvariant().Contains(card.ToLowerInvariant())) { match = it; break; }
  }
  if (match is null && items.Count > 0)
  {
    match = items[0];
    Line($"\n'{card}' not found in {partner}'s offer; falling back to first available card.");
  }
  if (match is null)
  {
    Line($"\n{partner} is offering nothing to take. Trade is open but nothing was staged.");
  }
  else
  {
    int catId = -1; string cname = "?"; int avail = 0;
    try { catId = match.Id; } catch { }
    try { cname = match.Card?.Name ?? "?"; } catch { }
    try { avail = match.Quantity; } catch { }
    Line($"\nFound {avail}x {cname} (catId={catId}). Requesting via wishlist import (autonomous)...");
    string requested = exec.RequestViaWishlist(catId, 1, cname);
    System.Threading.Thread.Sleep(3000);
    var e2 = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (e2 != null)
    {
      Line($"state={e2.State}");
      Line($"  YOU RECEIVE (requested): {requested}");
      try { Line($"  bot deposited:  {TradeBot.TradeExecutor.Summarize(e2.PartnerTradedItems)}"); } catch (Exception ex) { Line($"  (receive read failed: {ex.Message})"); }
      try { Line($"  WE GIVE:        {TradeBot.TradeExecutor.Summarize(e2.TradedItems)}"); } catch { }
    }
  }

  // HOLD the session open (bot stays attached, keeping the trade alive) while
  // YOU review and click Submit/Confirm. The bot never commits.
  Line("\n>>> Card requested autonomously. Review the trade window and click SUBMIT + CONFIRM to complete it. <<<");
  Line("    Bot is holding the session open and will NOT commit. Waiting up to 180s...\n");
  for (int i = 0; i < 180; i++)
  {
    System.Threading.Thread.Sleep(1000);
    MTGOSDK.API.Trade.TradeEscrow? c = null;
    try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
    if (c is null) { Line("Trade no longer active (submitted+completed, or closed) — done."); break; }
    if (i % 10 == 0) Line($"  t+{i,3}s  state={c.State}");
    if (c.State == MTGOSDK.API.Trade.Enums.TradeState.Closed) { Line("Trade closed."); break; }
  }
}

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
bool yes = args.Contains("--yes");
// By default the bot will launch MTGO (if needed) and log in from stored
// credentials. Pass --attach-only to require an already-running, logged-in
// client instead (never launches, never touches credentials).
bool attachOnly = args.Contains("--attach-only");
string arg1 = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";

// The ONLY modes a commit (final approve) can happen in: autofullgrab (acquire a
// free card) and lend (give a card), each requiring an explicit --commit flag.
// Every other mode stays hard-off (AllowCommit=false) even if --commit is passed.
bool allowCommit = (mode == "autofullgrab" || mode == "lend" || mode == "swap") && args.Contains("--commit");

Line("=== MTGOSDK TradeBot prototype ===");
Line($"mode={mode}  allowCommit={allowCommit.ToString().ToLowerInvariant()}" +
     (allowCommit ? $"  *** WILL COMMIT (final approve) — {(mode == "lend" ? "GIVING a card away" : mode == "swap" ? "SWAPPING (both sides move)" : "acquiring a free card")} ***" : "  (no commit)") + "\n");

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

bool mtgoRunning = Process.GetProcessesByName("MTGO").Length > 0;
if (!mtgoRunning && attachOnly)
{
  Line("MTGO is not running and --attach-only was given.");
  Line("Launch + log in first, or drop --attach-only to auto-launch and auto-login.");
  Environment.Exit(2);
}

using ILoggerFactory factory = LoggerFactory.Create(b =>
{
  b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
  b.SetMinimumLevel(LogLevel.Warning);   // keep SDK chatter down; our own output uses Console
});

Client client;
try
{
  // attach-only: connect to an already-running, logged-in client (old behavior).
  // default: create the MTGO process if it isn't running and auto-accept the EULA
  // prompt, so the bot can come up unattended. Login happens below from .env.
  ClientOptions opts = attachOnly
    ? new ClientOptions()
    : new ClientOptions { CreateProcess = true, AcceptEULAPrompt = true };
  client = new Client(opts, loggerFactory: factory);
}
catch (Exception ex) { Line($"Failed to connect to MTGO: {ex.GetType().Name}: {ex.Message}"); Environment.Exit(1); return; }

using (client)
using (var exec = new TradeExecutor { AllowCommit = allowCommit })
{
  // Determine the client's TRUE login state before deciding whether to log in.
  //
  // CRITICAL: attaching right after diver injection can transiently report
  // IsLoggedIn=false even on a live, already-logged-in session. Calling LogOn
  // in that window re-executes the login command on the live session, which
  // hangs and crashes the client (observed: repeated MTGO restarts). So:
  //   * If we ATTACHED to an already-running MTGO, wait for its real login
  //     state to settle (WaitForUserLogin resets the remote cache each poll)
  //     and NEVER force a re-login on it.
  //   * Only when WE launched MTGO ourselves do we go straight to auto-login,
  //     because we know it started fresh at the login screen.
  bool loggedIn = client.IsLoggedIn;
  // Gate the "attached — don't re-login" path on --attach-only, NOT on whether an
  // MTGO was running at startup. With CreateProcess=true (the default, i.e. no
  // --attach-only) the SDK KILLS any running MTGO and launches a FRESH one at the
  // login screen — so a pre-existing MTGO does NOT mean we attached to a live
  // session. Keying off mtgoRunning made cold-starts silently skip login whenever
  // an old MTGO happened to be up (relaunched fresh, then never logged in →
  // CurrentUser = -1). --attach-only is the only mode that truly attaches.
  if (!loggedIn && attachOnly)
  {
    // Attached to a PRE-EXISTING client. NEVER re-run LogOn on it: executing
    // the login command on a live session crashes the client (observed as
    // repeated MTGO restarts). Wait for the real login state to surface; if it
    // still doesn't report logged-in, PROCEED WITHOUT re-login rather than
    // forcing it — the remote read can stay stale right after diver injection.
    Line("Attached to a running client — checking its login state...");
    loggedIn = await client.WaitForUserLogin(TimeSpan.FromSeconds(8));
    if (!loggedIn)
    {
      Line("Login state not confirmed within the wait window.");
      Line("Proceeding WITHOUT re-login (never re-execute login on a live session).");
      Line("If the client is really at the login screen, either log in by hand, or");
      Line("fully close MTGO and re-run so the bot cold-starts and logs in itself.");
      loggedIn = true; // do NOT fall through to LogOn on an attached client
    }
  }

  // Only the cold-start path (we launched MTGO ourselves) can reach LogOn.
  if (!loggedIn)
  {
    if (attachOnly)
    {
      Line("Client running but not logged in — complete MTGO login and retry");
      Line("(or drop --attach-only to auto-login from stored credentials).");
      return;
    }

    // Auto-login — reached only when the client is genuinely at the login
    // screen (we launched it, or an attached client stayed logged-out through
    // the settle wait above). Credentials come from a .env file (searched from
    // the repo root upward: USERNAME= / PASSWORD=) or environment variables of
    // the same names. The password is read into a SecureString, never written.
    try { DotEnv.LoadFile(); }
    catch (System.IO.FileNotFoundException) { /* no .env — fall through to env vars */ }

    string uname = "";
    try { uname = DotEnv.Get("USERNAME"); }
    catch (System.Collections.Generic.KeyNotFoundException) { /* handled below */ }

    if (string.IsNullOrWhiteSpace(uname))
    {
      Line("Not logged in and no credentials found.");
      Line("Create a .env at the repo root (copy .env-example) with your MTGO");
      Line("USERNAME= and PASSWORD=, then retry. Or pass --attach-only to log in by hand.");
      Environment.Exit(3); return;
    }

    Line($"Logging in as {uname} ...");
    try
    {
      await client.LogOn(
        username: DotEnv.Get("USERNAME"),  // string
        password: DotEnv.Get("PASSWORD")); // SecureString
      Line("Login complete.");
    }
    catch (InvalidOperationException)
    {
      // LogOn throws this if the session became logged-in underneath us
      // (raced with the settle check). That's fine — we're logged in.
      Line("Already logged in (login raced to completion).");
    }
  }
  // A freshly (re)started client can report no logged-in user for a while even
  // though it will settle. Poll CurrentUser rather than dereferencing it blindly
  // (a logged-out client throws "User ID must be greater than zero. Got -1.").
  string? whoami = null;
  for (int i = 0; i < 20 && whoami is null; i++)
  {
    try { var u = client.CurrentUser; if (u != null && u.Id > 0) whoami = u.Name; }
    catch { /* not settled yet */ }
    if (whoami is null) await Task.Delay(1500);
  }
  if (whoami is null)
  {
    Line("Client is not reporting a logged-in user (CurrentUser id = -1).");
    Line("MTGO looks logged out / at the login screen — log in by hand, or fully");
    Line("close MTGO and re-run without --attach-only so the bot cold-starts.");
    Environment.Exit(4); return;
  }
  Line($"Connected as {whoami} (MTGO {Client.Version})\n");

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
          Line("Confirming from trade chat that the session is live:");
          System.Threading.Thread.Sleep(2000); // give the bot a moment to greet
          int seen = DumpChat(c);
          Line(seen > 0
            ? "  ^ chat present — trade session is active."
            : "  (no chat yet — session may still be active; the bot just hasn't spoken)");
          Line("\nLeaving it open — run `takecard <name> --yes` next to grab a specific card.");
          open = true; break;
        }
      }
      if (!open) Line("Did not reach negotiation. (No cleanup needed — nothing stranded past the binder step.)");
      // NOTE: intentionally NOT cancelling — leave the trade open for takecard.
      break;
    }

    case "autograb":
    {
      // FULL SINGLE-RUN FLOW against ONE named bot. Must be a COLD START (MTGO
      // closed beforehand) — the MTGO session does NOT survive a bot process
      // exit, so open-trade and take-card can't be separate invocations. One run
      // does it all: login (above) -> wait for <bot> to read OPEN -> open trade
      // -> stage <card> -> STAY ATTACHED while you review + click Submit -> exit.
      // Never commits (AllowCommit stays false); the final Submit is the user's.
      // To ROTATE across many bots instead of waiting on one, use `poolgrab`.
      string bot = arg1;
      string card = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
      if (bot.Length == 0 || card.Length == 0) { Line("Usage: autograb <bot> <cardname> --yes"); break; }
      if (!yes) { Line($"Refusing: opens a real trade with '{bot}' and stages '{card}' (non-committing). Re-run with --yes."); break; }

      exec.Attach();

      // Only invite when the bot advertises OPEN. RequestTrade pings the bot (it
      // then shows "busy"), and freebots THROTTLE rapid invite/cancel cycles, so
      // we avoid poking a bot that's already mid-trade: read its post message
      // first (read-only — no ping) and fire the invite only when it reads open.
      // We stay logged in, so re-checking is cheap — poll availability, invite
      // when open, and retry within the same session until we reach negotiation.
      MTGOSDK.API.Trade.TradeEscrow? esc = null;
      const int maxAttempts = 8;
      for (int attempt = 1; attempt <= maxAttempts && esc is null; attempt++)
      {
        var avail = TradeBot.BotPool.Check(bot);
        Line($"[{attempt}/{maxAttempts}] {avail}");
        if (avail.Status is TradeBot.BotStatus.Busy or TradeBot.BotStatus.NoPost)
        {
          Line(avail.Status == TradeBot.BotStatus.Busy
            ? "  bot is busy — waiting 8s and re-checking (not inviting a busy bot)."
            : "  bot has no marketplace post right now — waiting 8s and re-checking.");
          System.Threading.Thread.Sleep(8000);
          continue;
        }
        // Open or Unknown -> attempt the invite (Unknown = message matched
        // neither token list; we still try rather than stall on an unfamiliar
        // post — tune the tokens in BotAvailability.cs against `botstatus`).
        esc = TryReachNegotiation(exec, bot);
        if (esc is null)
        {
          Line("  didn't reach negotiation (bot busy/declined/timed out) — cleaning up before retry.");
          exec.CancelCurrent();
          if (!WaitForNoTrade()) Line("  (warning: prior escrow didn't clear in time)");
          System.Threading.Thread.Sleep(4000);   // space out invites to avoid throttling
        }
      }
      if (esc is null)
      {
        Line($"Could not reach negotiation with {bot} after {maxAttempts} attempts.");
        Line($"Tip: `poolgrab {card}` rotates across the whole freebot pool instead of waiting on one.");
        break;
      }

      StageAndHold(exec, esc, card);
      break;
    }

    case "poolgrab":
    {
      // ROTATION ACQUISITION. Checks a POOL of bots' post messages, invites only
      // ones reading OPEN, and ROTATES to the next open bot instead of spamming
      // a busy one. Must be a COLD START (see autograb) — one run does it all.
      //   login (above) -> availability sweep -> invite each open bot in turn
      //   until one reaches negotiation -> stage <card> -> HOLD for your Submit.
      // Never commits (AllowCommit stays false). Pool defaults to the known
      // freebots; override with `--bots=a,b,c`.
      string card = arg1;
      if (card.Length == 0) { Line("Usage: poolgrab <cardname> [--bots=a,b,c] --yes"); break; }
      if (!yes) { Line($"Refusing: opens real trades (rotating across open bots) and stages '{card}' (non-committing). Re-run with --yes."); break; }

      string[] pool = ParseBots(args) ?? TradeBot.BotPool.FreeBots;
      Line($"Bot pool ({pool.Length}): {string.Join(", ", pool)}");
      exec.Attach();

      MTGOSDK.API.Trade.TradeEscrow? esc = null;
      const int maxSweeps = 3;   // re-scan the pool a few times as bots free up
      for (int sweep = 1; sweep <= maxSweeps && esc is null; sweep++)
      {
        Line($"\n--- availability sweep {sweep}/{maxSweeps} (read-only) ---");
        var statuses = TradeBot.BotPool.CheckPool(pool);
        foreach (var s in statuses) Line("  " + s);

        // Prefer bots reading OPEN; fall back to Unknown-status ones (never Busy
        // or NoPost) so unfamiliar post wording can't stall the whole rotation.
        var candidates = TradeBot.BotPool.CandidatesIn(statuses);
        if (candidates.Count == 0)
        {
          Line("No open/available bots this sweep; waiting 15s before re-checking...");
          System.Threading.Thread.Sleep(15000);
          continue;
        }

        foreach (var b in candidates)
        {
          string tier = b.Status == TradeBot.BotStatus.Open ? "OPEN" : "unrecognized-status";
          Line($"\n>>> Inviting {tier} bot: {b.Name} <<<");
          esc = TryReachNegotiation(exec, b.Name);
          if (esc != null) break;
          Line($"  {b.Name} didn't reach negotiation (busy/declined) — rotating to the next candidate.");
          exec.CancelCurrent();                                  // clear any stranded escrow first
          if (!WaitForNoTrade()) Line("  (warning: prior escrow didn't clear in time)");
        }

        // Space out sweeps even if candidates existed, so a lone flaky bot isn't
        // re-invited every few seconds (throttle avoidance).
        if (esc is null && sweep < maxSweeps) System.Threading.Thread.Sleep(5000);
      }

      if (esc is null)
      {
        exec.CancelCurrent();            // safety: nothing should be stranded
        WaitForNoTrade();
        Line("\nNo bot reached negotiation after sweeping the pool. Try again later.");
        break;
      }

      StageAndHold(exec, esc, card);
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

    case "botstatus":
    {
      // READ-ONLY availability check for a SINGLE bot. Reads only the named bot's
      // marketplace post via the SDK's server-side poster-name filter (no
      // whole-marketplace scan, no bot ping), prints the raw message, and shows
      // how it classifies — so you can see how the bot signals busy vs free and
      // tune the token lists in BotAvailability.cs if the wording differs.
      if (arg1.Length == 0) { Line("Usage: botstatus <botname>"); break; }
      string name = arg1;
      var v = TradeBot.BotPool.Check(name);
      if (v.Status == TradeBot.BotStatus.NoPost)
      {
        Line($"{name}: no marketplace post found (offline / not currently listed).");
        break;
      }
      Line($"{name}: {v.PostCount} post(s) matching this exact poster name.");
      Line($"  message: {(string.IsNullOrEmpty(v.Message) ? "(empty)" : v.Message)}");
      string mark = v.Status switch
      {
        TradeBot.BotStatus.Open => "✅ safe to invite",
        TradeBot.BotStatus.Busy => "⛔ busy — skip / rotate",
        _                       => "❔ unrecognized wording — inspect the message above",
      };
      Line($"  → classified: {v.Status}  {mark}");
      break;
    }

    case "poolstatus":
    {
      // READ-ONLY: availability sweep across a POOL of bots (default: known
      // freebots; override with `--bots=a,b,c`). Reads each bot's post message
      // (server-side name filter — no whole-marketplace scan, no bot ping) and
      // classifies Open/Busy/Unknown/NoPost so you can see which are tradeable
      // right now. This is exactly what `poolgrab` gates its invites on.
      string[] pool = ParseBots(args) ?? TradeBot.BotPool.FreeBots;
      Line($"Checking availability of {pool.Length} bot(s) (read-only, no ping)...\n");
      var statuses = TradeBot.BotPool.CheckPool(pool);
      foreach (var s in statuses) Line("  " + s);
      var open = TradeBot.BotPool.OpenIn(statuses).Select(s => s.Name).ToList();
      var candidates = TradeBot.BotPool.CandidatesIn(statuses).Select(s => s.Name).ToList();
      Line($"\nOpen now:       {(open.Count > 0 ? string.Join(", ", open) : "(none)")}");
      Line($"poolgrab order: {(candidates.Count > 0 ? string.Join(", ", candidates) : "(none — try again shortly)")}"
           + "   (open first, then unrecognized-status as fallback)");
      break;
    }

    case "readchat":
    {
      // READ-ONLY: dump the chat channel of the currently-open trade to confirm
      // the session is live (freebot greeting / item-update echoes).
      var esc = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
      if (esc is null) { Line("No active trade — open one first."); break; }
      Line($"Active trade: partner={esc.TradePartnerName}  state={esc.State}");
      DumpChat(esc, 25);
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

    case "stagetest":
    {
      // CONTROLLED TEST of autonomous card-select (TakeCard ->
      // SendTradeItemUpdateAction). Initiates a trade with an OPEN bot, reaches
      // negotiation, stages ONE partner card via TakeCard, checks whether it
      // landed (WE RECEIVE non-empty, still in Negotiate, CurrentTrade intact)
      // WITHOUT corrupting the trade UI, then AUTO-CANCELS. Non-committing.
      // This exercises the diver's ForceUIThread -> Application.Current.Dispatcher
      // marshaling for a non-DispatcherObject target (the escrow) — the exact
      // path that previously corrupted the DataGrid on the wrong thread.
      if (arg1.Length == 0) { Line("Usage: stagetest <bot> [cardname] --yes"); break; }
      if (!yes) { Line($"Refusing: initiates a real trade with '{arg1}', stages a card via TakeCard (non-committing), then cancels. Re-run with --yes."); break; }
      string wantCard = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "";

      exec.Attach();
      bool staged = false;
      try
      {
        var esc = TryReachNegotiation(exec, arg1);
        if (esc is null) { Line("Did not reach negotiation (bot busy/declined). Try an open bot (see poolstatus)."); break; }
        Line($"\nReached negotiation with {esc.TradePartnerName} (state={esc.State}).");

        System.Collections.Generic.List<MTGOSDK.API.Collection.CardQuantityPair> items;
        try { items = esc.PartnerCollection.CollectionItems; }
        catch (Exception ex) { Line($"PartnerCollection read failed: {ex.Message}"); break; }

        MTGOSDK.API.Collection.CardQuantityPair? match = null;
        foreach (var it in items)
        {
          string nm; try { nm = it.Card?.Name ?? ""; } catch { nm = ""; }
          if (wantCard.Length == 0 || nm.ToLowerInvariant().Contains(wantCard.ToLowerInvariant())) { match = it; break; }
        }
        if (match is null) { Line($"No card to stage (partner offers {items.Count} stack(s); no match for '{wantCard}')."); }
        else
        {
          int catId = -1; string cn = "?";
          try { catId = match.Id; } catch { }
          try { cn = match.Card?.Name ?? "?"; } catch { }
          Line($"Requesting 1x {cn} (catId={catId}) via wishlist import (autonomous)...");
          string requested = exec.RequestViaWishlist(catId, 1, cn);
          System.Threading.Thread.Sleep(2000);

          var e2 = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
          if (e2 is null)
          {
            Line("!! CurrentTrade went NULL after request — trade was rejected/closed (the old ErrorReceived mode).");
          }
          else
          {
            string st = "?"; try { st = e2.State.ToString(); } catch { }
            string dep = "?"; try { dep = TradeBot.TradeExecutor.Summarize(e2.PartnerTradedItems); } catch (Exception ex) { dep = $"<read failed: {ex.Message}>"; }
            Line($"  state={st}");
            Line($"  YOU RECEIVE (requested list): {requested}");
            Line($"  partner-deposited so far:    {dep}");
            bool alive = st.StartsWith("Negotiate") || st.StartsWith("Approval");
            bool gotItem = requested != "(none)" && !requested.StartsWith("<");
            if (alive && gotItem)
            {
              Line("  ✅ REQUEST ACCEPTED — card is in the you-receive list and the trade is alive (no ErrorReceived).");
              staged = true;
            }
            else if (alive)
            {
              Line("  ⚠ Trade alive but requested list empty — the request may not have applied (inspect above).");
            }
            else
            {
              Line("  ⚠ Trade left negotiation unexpectedly — inspect above.");
            }
          }
        }
      }
      catch (Exception ex) { Line($"stagetest threw: {ex.Message.Split('\n')[0]}"); }
      finally
      {
        Line("\nCleaning up (auto-cancel — no commit)...");
        exec.CancelCurrent();
        if (!WaitForNoTrade()) Line("  (warning: escrow didn't clear — MTGO may need a restart)");
        MTGOSDK.API.Trade.TradeEscrow? f = null;
        try { f = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        Line($"Final: CurrentTrade={(f is null ? "<none — CLEARED OK>" : f.State + " (NOT cleared)")}");
        Line(staged
          ? "\nRESULT: ✅ Card requested via ActiveTradeViewModel and staged into WE RECEIVE — autonomous card-select works."
          : "\nRESULT: ⚠ Request did not cleanly stage — see above.");
      }
      break;
    }

    case "autofullgrab":
    {
      // FULLY AUTONOMOUS grab, end to end: detect -> initiate -> advance ->
      // negotiate -> request card via wishlist import -> submit my (empty)
      // deposit -> reach approval-ready. WITHOUT --commit it stops there and
      // cancels (validates the whole flow, moves nothing). WITH --commit it sends
      // the final approve and completes the trade. WE GIVE is always (none) — a
      // free-card grab. Must be a COLD START or an attached, logged-in client.
      string bot = arg1;
      string card = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
      if (bot.Length == 0) { Line("Usage: autofullgrab <bot> [cardname] [--commit] --yes"); break; }
      if (!yes) { Line($"Refusing: full autonomous grab from '{bot}'{(allowCommit ? " that WILL COMMIT (acquire a free card, give nothing)" : " (dry-run: stops before commit)")}. Re-run with --yes."); break; }

      exec.Attach();
      var esc = TryReachNegotiation(exec, bot);
      if (esc is null) { Line("Did not reach negotiation (bot busy/declined) — try an open bot (poolstatus)."); break; }
      Line($"\nNegotiating with {esc.TradePartnerName} (state={esc.State}).");

      // 1) pick + request the card via the wishlist-import path
      System.Collections.Generic.List<MTGOSDK.API.Collection.CardQuantityPair> items;
      try { items = esc.PartnerCollection.CollectionItems; }
      catch (Exception ex) { Line($"PartnerCollection read failed: {ex.Message}"); exec.CancelCurrent(); break; }
      MTGOSDK.API.Collection.CardQuantityPair? match = null;
      foreach (var it in items)
      {
        string nm; try { nm = it.Card?.Name ?? ""; } catch { nm = ""; }
        if (card.Length == 0 || nm.ToLowerInvariant().Contains(card.ToLowerInvariant())) { match = it; break; }
      }
      if (match is null) { Line("No matching card in the bot's offer — aborting."); exec.CancelCurrent(); WaitForNoTrade(); break; }
      int catId = -1; string cn = "?";
      try { catId = match.Id; } catch { }
      try { cn = match.Card?.Name ?? "?"; } catch { }
      Line($"Requesting 1x {cn} (catId={catId}) via wishlist import...");
      string requested = exec.RequestViaWishlist(catId, 1, cn);
      if (requested == "(none)" || requested.StartsWith("<")) { Line("Request didn't stage — aborting."); exec.CancelCurrent(); WaitForNoTrade(); break; }

      // 2) submit my (empty) deposit; retry SubmitDeposit until state advances
      Line("Submitting my deposit (we give nothing)...");
      bool submitted = false;
      for (int i = 0; i < 10 && !submitted; i++)
      {
        exec.SubmitDeposit();
        for (int j = 0; j < 4 && !submitted; j++)
        {
          System.Threading.Thread.Sleep(1000);
          MTGOSDK.API.Trade.TradeEscrow? c = null; try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
          if (c is null) break;
          string st = c.State.ToString();
          if (st.Contains("Deposit") && (st.Contains("Submitted") || st.Contains("Received")) || st.StartsWith("Approval"))
          { submitted = true; Line($"  deposit accepted (state={st})."); }
        }
      }

      // 3) wait for approval-ready (both deposited)
      string finalSt = "?"; bool approveReady = false;
      for (int i = 0; i < 30 && !approveReady; i++)
      {
        MTGOSDK.API.Trade.TradeEscrow? c = null; try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        if (c is null) { Line("Trade closed before approval."); break; }
        finalSt = c.State.ToString();
        if (i % 3 == 0) Line($"  t+{i,2}s state={finalSt}");
        if (finalSt.StartsWith("Approval")) approveReady = true;
        else System.Threading.Thread.Sleep(1000);
      }
      if (!approveReady) { Line("Did not reach approval-ready — cancelling (nothing moved)."); exec.CancelCurrent(); WaitForNoTrade(); break; }

      try { Line($"  bot deposited: {TradeBot.TradeExecutor.Summarize(esc.PartnerTradedItems)}"); } catch { }
      try { Line($"  WE GIVE:       {TradeBot.TradeExecutor.Summarize(esc.TradedItems)}"); } catch { }
      Line($"\n>>> APPROVAL-READY (state={finalSt}) — deposits done; WE GIVE nothing. <<<");

      // 4) commit, or (dry-run) stop + cancel
      if (!allowCommit)
      {
        Line("[dry-run] No --commit → NOT approving. Cancelling (no assets moved)...");
        try { exec.Cancel(esc); } catch (Exception ex) { Line($"(cancel threw: {ex.Message.Split('\n')[0]})"); }
        WaitForNoTrade();
        Line("\nDry-run OK. Re-run with `--commit --yes` to actually complete the grab.");
        break;
      }

      Line("\n*** COMMITTING: sending final approve (ConfirmTrade) — acquiring the free card ***");
      exec.ConfirmTrade();
      for (int i = 0; i < 40; i++)
      {
        System.Threading.Thread.Sleep(1000);
        MTGOSDK.API.Trade.TradeEscrow? c = null; try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        if (c is null) { Line("Trade complete/closed — client is clear."); break; }
        string st = c.State.ToString();
        if (i % 3 == 0) Line($"  t+{i,2}s state={st}");
        if (st == "Closed") { Line("Trade closed."); break; }
      }
      Line("\nAcquisition ledger (latest):");
      var lp2 = TradeBot.TradeExecutor.LedgerPath;
      if (System.IO.File.Exists(lp2))
        foreach (var l in System.IO.File.ReadAllLines(lp2).Reverse().Take(1)) Line("  " + l);
      else Line("  (no ledger entry written)");
      break;
    }

    case "buddy":
    {
      // Diagnostic: add <name> as a buddy and report resolution before/after.
      if (arg1.Length == 0) { Line("Usage: buddy <name> --yes"); break; }
      if (!yes) { Line($"Refusing: adds '{arg1}' as a buddy. Re-run with --yes."); break; }
      exec.Attach();
      int? before = null; try { before = MTGOSDK.API.Users.UserManager.GetUserId(arg1); } catch (Exception ex) { Line($"GetUserId(before) threw: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
      Line($"GetUserId('{arg1}') before add = {(before?.ToString() ?? "null")}");
      Line("Adding as buddy...");
      try { exec.AddBuddy(arg1); } catch (Exception ex) { Line($"AddBuddy threw: {ex.Message.Split('\n')[0]}"); }
      System.Threading.Thread.Sleep(4000);
      int? after = null; try { after = MTGOSDK.API.Users.UserManager.GetUserId(arg1); } catch { }
      Line($"GetUserId('{arg1}') after add  = {(after?.ToString() ?? "null")}");
      Line("Current buddy list:");
      try
      {
        foreach (var b in MTGOSDK.API.Users.UserManager.GetBuddyUsers())
        { string nm = "?"; int bid = -1; try { nm = b.Name; } catch { } try { bid = b.Id; } catch { } Line($"  {nm} (id={bid})"); }
      }
      catch (Exception ex) { Line($"  (buddy list read failed: {ex.Message.Split('\n')[0]})"); }
      break;
    }

    case "dmtest":
    {
      // Test the DM handshake in isolation (no trade): DM <user> and wait for YES.
      if (arg1.Length == 0) { Line("Usage: dmtest <user> [message] --yes"); break; }
      if (!yes) { Line($"Refusing: sends a DM to '{arg1}'. Re-run with --yes."); break; }
      string msg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "DM handshake test — reply YES to confirm.";
      exec.Attach();
      if (!exec.EnsureKnownUser(arg1)) { Line($"Could not resolve or add '{arg1}' as a buddy — check the exact username."); break; }
      exec.DumpDMTail(arg1);
      try { exec.SendDM(arg1, msg); }
      catch (Exception ex) { Line($"SendDM failed: {ex.Message}"); break; }
      System.Threading.Thread.Sleep(1500);
      exec.DumpDMTail(arg1);   // did our sent message land in the log?
      Line($"\nWaiting up to 90s for a YES reply from {arg1} (reply now from that account)...");
      bool gotYes = exec.WaitForDMYes(arg1, 90);
      Line(gotYes ? $"✅ Got YES from {arg1} — handshake works." : $"⏱ No YES within 90s (no reply, or they said something else).");
      break;
    }

    case "dealers":
    {
      // READ-ONLY: find marketplace bots whose POST TEXT mentions a keyword
      // (default "foil"), grouped by poster, most-active first. Posters currently
      // listed are online. One server-side diver pass (no whole-marketplace scan).
      // Usage: dealers [keyword]
      string keyword = arg1.Length > 0 ? arg1 : "foil";
      Line($"Searching marketplace posts for \"{keyword}\" (read-only)...\n");
      var dealers = TradeBot.BotPool.FindDealers(keyword);
      if (dealers.Count == 0) { Line($"No online bots have \"{keyword}\" in their posts right now."); break; }
      Line($"{dealers.Count} bot(s) dealing in \"{keyword}\" (by matching-post count):");
      foreach (var d in dealers.Take(25)) Line("  " + d);
      if (dealers.Count > 25) Line($"  … and {dealers.Count - 25} more.");
      Line($"\nDM one with:  dmbot <name> [message] --yes");
      break;
    }

    case "dmbot":
    {
      // Test the DM path against a MARKETPLACE BOT (reliably online). Resolves the
      // bot's user id straight from its post (no buddy-add), sends a message, then
      // dumps the DM tail so any auto-reply is visible.
      // Usage: dmbot <botname> [message] --yes
      if (arg1.Length == 0) { Line("Usage: dmbot <botname> [message] --yes"); break; }
      if (!yes) { Line($"Refusing: sends a DM to bot '{arg1}'. Re-run with --yes."); break; }
      string botMsg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--"))
                      ?? "Hi! Testing chat — do you carry foils? (automated test message)";
      exec.Attach();
      int uid = TradeBot.BotPool.ResolvePosterId(arg1);
      if (uid <= 0) { Line($"'{arg1}' isn't currently posting on the marketplace (can't get its id). Try `dealers` to see live names, exact spelling matters."); break; }
      Line($"Resolved {arg1} → id={uid} (from its marketplace post).");
      exec.DumpDMTailById(uid, arg1);
      try { exec.SendDMToId(uid, arg1, botMsg); }
      catch (Exception ex) { Line($"SendDM failed: {ex.Message}"); break; }
      Line($"\nSent. Waiting 20s for a reply from {arg1}...");
      System.Threading.Thread.Sleep(20000);
      exec.DumpDMTailById(uid, arg1, 12);
      break;
    }

    case "makebinder":
    {
      // Create/verify a trade binder on THIS (bot) account. No counterparty needed.
      // Usage: makebinder [binderName] [cardname] --yes
      //        makebinder <binderName> --cards="Event Ticket,Azimaet Drake,Radiant Strike" --yes
      string binderName = arg1.Length > 0 ? arg1 : "Lending";
      var cardsFlag = args.FirstOrDefault(a => a.StartsWith("--cards=", StringComparison.OrdinalIgnoreCase));
      System.Collections.Generic.List<string> cards = cardsFlag != null
        ? cardsFlag.Substring("--cards=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        : new() { args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "Azimaet Drake" };
      if (!yes) { Line($"Refusing: creates a binder '{binderName}' containing [{string.Join(", ", cards)}] on your account. Re-run with --yes."); break; }
      var binder = exec.CreateBinder(binderName, cards);
      if (binder is null) { Line("Binder create/reuse failed — see above."); break; }
      Line($"\nBinder '{binder.Name}' (id={binder.Id}) — {binder.ItemCount} item(s):");
      try { foreach (var it in binder.Items) Line($"  {it.Quantity}x  catId={it.Id}  {it.Card?.Name}"); }
      catch (Exception ex) { Line($"  (item read failed: {ex.Message})"); }
      break;
    }

    case "deletebinder":
    {
      // Delete a trade binder on THIS (bot) account. No counterparty needed.
      // Usage: deletebinder <binderName> --yes
      string binderName = arg1.Length > 0 ? arg1 : "";
      if (binderName.Length == 0) { Line("Usage: deletebinder <binderName> --yes"); break; }
      if (!yes) { Line($"Refusing: deletes binder '{binderName}' from your account. Re-run with --yes."); break; }
      Line(exec.DeleteBinder(binderName) ? $"Binder '{binderName}' is gone." : $"Could not delete '{binderName}' — see above.");
      break;
    }

    case "owned":
    {
      // READ-ONLY: for a card name, list every printing (catId) and how many THIS
      // account owns of each — to pick a printing a lend binder can actually show.
      // Usage: owned <cardname>
      string name = arg1.Length > 0 ? arg1 : (args.Skip(1).FirstOrDefault() ?? "");
      if (name.Length == 0) { Line("Usage: owned <cardname>"); break; }
      exec.Attach();
      System.Collections.Generic.List<int> ids;
      try { ids = MTGOSDK.API.Collection.CollectionManager.GetCardIds(name).ToList(); }
      catch (Exception ex) { Line($"No card named '{name}' ({ex.Message.Split('\n')[0]})."); break; }
      Line($"'{name}' has {ids.Count} printing(s): {string.Join(", ", ids)}");
      var col = MTGOSDK.API.Collection.CollectionManager.Collection;
      var ownedByCat = new System.Collections.Generic.Dictionary<int,int>();
      try { foreach (var it in col.Items) { int id = it.Id; if (ids.Contains(id)) ownedByCat[id] = (ownedByCat.TryGetValue(id, out var q) ? q : 0) + it.Quantity; } }
      catch (Exception ex) { Line($"(collection scan failed: {ex.Message.Split('\n')[0]})"); }
      int total = 0;
      foreach (var id in ids) { int q = ownedByCat.TryGetValue(id, out var v) ? v : 0; total += q; Line($"  catId={id,-8} owned={q}"); }
      var (oc, oq) = exec.ResolveOwnedPrinting(name);
      Line(oc > 0 ? $"\nOwned printing a binder can present: catId={oc} (qty {oq}). Total owned across printings: {total}."
                  : $"\nYou own NONE of '{name}' in any printing — a lend binder of it would show empty.");
      break;
    }

    case "collection":
    {
      // READ-ONLY: list owned cards (name x total qty) — to pick cards to lend/test.
      // Usage: collection [filter-substring]
      string filter = arg1;
      exec.Attach();
      var col = MTGOSDK.API.Collection.CollectionManager.Collection;
      var byName = new System.Collections.Generic.Dictionary<string, int>();
      try { foreach (var it in col.Items) { string nm = it.Card?.Name ?? "?"; int q = it.Quantity; if (q <= 0) continue; if (filter.Length > 0 && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue; byName[nm] = (byName.TryGetValue(nm, out var v) ? v : 0) + q; } }
      catch (Exception ex) { Line($"collection read failed: {ex.Message.Split('\n')[0]}"); break; }
      Line($"Owned distinct items: {byName.Count}{(filter.Length > 0 ? $" (filter '{filter}')" : "")}. Top 60 by quantity:");
      foreach (var kv in byName.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(60)) Line($"  {kv.Value,4}x  {kv.Key}");
      break;
    }

    case "lendinvite":
    {
      // ATTEMPT the give-side SETUP only: DM the recipient, then initiate a trade
      // that PRESENTS the dedicated Lending binder (so they see only <card>).
      // STRICTLY DRY-RUN — there is NO commit path here, so it can never give
      // anything. Always cancels at the end. This is the "message then make the
      // lending binder available" step, isolated from the actual hand-over.
      // Usage: lendinvite <recipient> [cardname] --yes
      string recipient = arg1;
      string card = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "Azimaet Drake";
      if (recipient.Length == 0) { Line("Usage: lendinvite <recipient> [cardname] --yes"); break; }
      if (!yes) { Line($"Refusing: DMs '{recipient}' and sends a trade invite presenting the Lending binder ('{card}'). Dry-run — commits/gives NOTHING. Re-run with --yes."); break; }
      Line($"Lend-invite to {recipient}: DM, then present the Lending binder ('{card}').  [dry-run — gives nothing, always cancels]");

      exec.Attach();

      // 0) ensure the dedicated single-card Lending binder exists.
      var lendBinder = exec.CreateSingleCardBinder("Lending", card);
      if (lendBinder is null) { Line("Could not create/find the Lending binder — aborting."); break; }
      Line($"Lending binder ready: '{lendBinder.Name}' (id={lendBinder.Id}, items={lendBinder.ItemCount}).");

      // 1) make sure the client knows the recipient (buddy-add if needed), then DM.
      if (!exec.EnsureKnownUser(recipient))
      { Line($"Could not resolve '{recipient}' (spelling? they may need to accept the buddy request) — aborting."); break; }
      try { exec.SendDM(recipient, $"Hi {recipient} — I'd like to lend you {card}. Sending you a trade now; grab it from my Lending binder."); }
      catch (Exception ex) { Line($"DM failed: {ex.Message} — aborting."); break; }
      System.Threading.Thread.Sleep(1500);
      exec.DumpDMTail(recipient);

      // 2) initiate + PRESENT the Lending binder (dispatches the invite).
      var esc = TryReachNegotiation(exec, recipient, presentBinder: "Lending");
      if (esc is null)
      {
        Line($"\nInvite dispatched, but no trade negotiation opened (recipient offline, or didn't accept the invite in time). Nothing given.");
        exec.CancelCurrent(); WaitForNoTrade();
        break;
      }
      Line($"\n>>> TRADE OPEN with {esc.TradePartnerName} — Lending binder presented ({card}). They can grab it. <<<");

      // 3) HOLD open + observe (NEVER commit). Cancel at the end — gives nothing.
      //    Highlight the moment WE GIVE becomes non-empty = they grabbed from the
      //    presented binder (proof it was available with the right card).
      string lastGive = "", lastState = "";
      bool sawGrab = false;
      for (int i = 0; i < 90; i++)
      {
        System.Threading.Thread.Sleep(1000);
        MTGOSDK.API.Trade.TradeEscrow? c = null;
        try { c = MTGOSDK.API.Trade.TradeManager.CurrentTrade; } catch { }
        if (c is null) { Line("Trade closed by the other side."); esc = null; break; }
        esc = c;
        string give = TradeBot.TradeExecutor.Summarize(c.TradedItems);
        string state = c.State.ToString();
        if (give != lastGive || state != lastState)
        {
          Line($"  t+{i,3}s state={state}  WE GIVE: {give}");
          lastGive = give; lastState = state;
        }
        if (!sawGrab && !string.IsNullOrWhiteSpace(give) && give != "(none)" && give != "(empty)")
        { Line($"  *** {esc.TradePartnerName} grabbed from the Lending binder: {give} ***"); sawGrab = true; }
      }
      if (sawGrab) Line("\nConfirmed: the Lending binder was available and its card was grabbable.");
      if (esc != null)
      {
        Line("\n[dry-run] Done observing — cancelling (nothing committed, nothing given).");
        exec.CancelCurrent(); WaitForNoTrade();
      }
      try { exec.SendDM(recipient, "That was a test invite — cancelling for now. Thanks!"); } catch { }
      break;
    }

    case "lend":
    {
      // TARGETED LEND (custodian / GIVE side). Flow: DM "Ready for <cards>? Reply
      // YES" → wait for yes → initiate + present the Lending binder → they grab the
      // card(s) from our offer. NEW: if they hit SUBMIT before grabbing everything,
      // DM them the still-un-grabbed cards and DON'T approve until they have them
      // all. GUARDRAIL: WE GIVE == EXACTLY the intended set, receive nothing — cancel
      // on anything extra. Final approve is autonomous, gated by --commit.
      // Usage: lend <recipient> [card] [--cards="A,B,C"] [--commit] --yes
      string recipient = arg1;
      var cardsFlag = args.FirstOrDefault(a => a.StartsWith("--cards=", StringComparison.OrdinalIgnoreCase));
      var giveCards = cardsFlag != null
        ? cardsFlag.Substring("--cards=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        : new System.Collections.Generic.List<string> { args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "Azimaet Drake" };
      var intended = giveCards.Select(n => (name: n, qty: 1)).ToList();
      string cardList = string.Join(", ", giveCards);
      if (recipient.Length == 0) { Line("Usage: lend <recipient> [card] [--cards=\"A,B,C\"] [--commit] --yes"); break; }
      if (giveCards.Count == 0) { Line("Nothing to lend — give a card or --cards=\"...\"."); break; }
      if (!yes) { Line($"Refusing: DMs '{recipient}', opens a trade, and GIVES [{cardList}]{(allowCommit ? " (WILL COMMIT the give)" : " (dry-run: stops before commit)")}. Re-run with --yes."); break; }
      Line($"Lending [{cardList}] to {recipient}." + (allowCommit ? "  *** WILL COMMIT ***" : "  [dry-run: reaches approval-ready then cancels — gives nothing]"));

      exec.Attach();

      // 0) ensure the "Lending" binder holds ALL the cards to give (owned printings).
      var lendBinder = exec.CreateBinder("Lending", giveCards);
      if (lendBinder != null) Line($"Lending binder ready: '{lendBinder.Name}' (id={lendBinder.Id}, items={lendBinder.ItemCount}).");

      // 1+2) standardized handshake: DM "reply YES", wait for it, THEN initiate +
      //       present the Lending binder (no accept-race — see HandshakeThenInitiate).
      var esc = HandshakeThenInitiate(exec, recipient,
        $"Ready for [{cardList}]? Reply YES and I'll send you a trade — then accept it and grab {(giveCards.Count == 1 ? "the card" : $"all {giveCards.Count} cards")} from my offer.",
        presentBinder: "Lending");
      if (esc is null)
      {
        try { exec.SendDM(recipient, "Couldn't open the trade — reply YES when you're ready and I'll retry."); } catch { }
        break;
      }
      Line($"\nTrade open with {esc.TradePartnerName}. They should grab: {cardList}.");

      // 3) wait until WE GIVE == exactly the intended SET. If they hit SUBMIT before
      //    grabbing everything, REMIND them what's still un-grabbed. If they grab
      //    something not offered, cancel.
      bool ready = false;
      string lastRemindKey = "";
      for (int i = 0; i < 300 && !ready; i++)
      {
        System.Threading.Thread.Sleep(1000);
        var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (c is null) { Line("Trade closed before they finished grabbing."); esc = null; break; }
        esc = c;
        var (missing, extra, exact) = exec.GiveStatus(c, intended);

        if (extra.Count > 0)
        {
          Line($"They grabbed something not offered ({string.Join(", ", extra)}) — cancelling for safety.");
          exec.CancelCurrent(); WaitForNoTrade();
          try { exec.SendDM(recipient, $"Cancelled — you grabbed {string.Join(", ", extra)}, which isn't part of this lend. Please grab ONLY [{cardList}], then reply YES to retry."); } catch { }
          esc = null; break;
        }
        if (exact) { ready = true; break; }

        // THE REMINDER: they submitted their deposit but haven't grabbed everything.
        if (missing.Count > 0 && TradeBot.TradeExecutor.PartnerHasSubmitted(c))
        {
          string key = string.Join("|", missing);
          if (key != lastRemindKey)
          {
            lastRemindKey = key;
            Line($"  [reminder] {recipient} submitted but still needs to grab: {string.Join(", ", missing)}");
            try { exec.SendDM(recipient, $"Hold on — you submitted, but you still need to grab: {string.Join(", ", missing)}. Please grab {(missing.Count == 1 ? "it" : "them")} from my offer and submit again. I won't approve until you have everything."); } catch { }
          }
        }
        else
        {
          if (!TradeBot.TradeExecutor.PartnerHasSubmitted(c)) lastRemindKey = "";   // reset so a fresh submit re-reminds
          if (i % 10 == 0) Line($"  t+{i,3}s  WE GIVE: {TradeBot.TradeExecutor.Summarize(c.TradedItems)}  (still to grab: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))})");
        }
      }
      if (esc is null) break;
      if (!ready)
      {
        Line("They didn't grab the full set in time — cancelling.");
        exec.CancelCurrent(); WaitForNoTrade();
        try { exec.SendDM(recipient, $"Cancelled — didn't get all of [{cardList}] grabbed in time. Reply YES to retry."); } catch { }
        break;
      }
      Line("GUARDRAIL passed: we give exactly the intended set and receive nothing.");

      // 4) submit our deposit; wait for approval-ready
      exec.SubmitDeposit();
      string st = "?"; bool approveReady = false;
      for (int i = 0; i < 30 && !approveReady; i++)
      {
        System.Threading.Thread.Sleep(1000);
        var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (c is null) { Line("Trade closed before approval."); esc = null; break; }
        st = c.State.ToString(); esc = c;
        if (i % 3 == 0) Line($"  t+{i,2}s state={st}");
        if (st.StartsWith("Approval")) approveReady = true;
      }
      if (esc is null) break;
      if (!approveReady) { Line("Did not reach approval-ready — cancelling."); exec.CancelCurrent(); WaitForNoTrade(); break; }

      // 5) RE-VERIFY the guardrail right before committing (belt and suspenders)
      if (!exec.GiveStatus(esc, intended).exact)
      {
        Line("GUARDRAIL re-check FAILED at approval — cancelling (gives nothing).");
        try { exec.Cancel(esc); } catch { } WaitForNoTrade();
        try { exec.SendDM(recipient, "Cancelled at the final check for safety."); } catch { }
        break;
      }

      if (!allowCommit)
      {
        Line("\n[dry-run] Approval-ready + guardrail OK; no --commit → cancelling (gave nothing).");
        try { exec.Cancel(esc); } catch { } WaitForNoTrade();
        Line("Re-run with `--commit --yes` to actually complete the lend.");
        break;
      }

      Line("\n*** COMMITTING the give (ConfirmTrade) ***");
      exec.ConfirmTrade();
      for (int i = 0; i < 40; i++)
      {
        System.Threading.Thread.Sleep(1000);
        var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
        if (c is null) { Line("Give complete — client clear."); break; }
        if (i % 3 == 0) Line($"  t+{i,2}s state={c.State}");
        if (c.State == MTGOSDK.API.Trade.Enums.TradeState.Closed) break;
      }
      try { exec.SendDM(recipient, $"Done — enjoy [{cardList}]!"); } catch { }
      Line("\nLedger (latest):");
      var lpl = TradeBot.TradeExecutor.LedgerPath;
      if (System.IO.File.Exists(lpl)) foreach (var l in System.IO.File.ReadAllLines(lpl).Reverse().Take(1)) Line("  " + l);
      break;
    }

    case "swap":
    {
      // BIDIRECTIONAL trade (both sides move at once): present a binder holding our
      // GIVE card (default 1 Event Ticket) so the partner can grab it, AND request
      // their GET card (default 1 Azimaet Drake). SUBMIT + COMMIT only when BOTH
      // sides are EXACTLY right; if either side is incomplete or the requested card
      // is unavailable in their binder, CANCEL (moves nothing).
      // Usage: swap <partner> [--give=<card>] [--get=<card,card,...>] [--listen] [--commit] --yes
      string partner  = arg1;
      string giveCard = args.FirstOrDefault(a => a.StartsWith("--give=", StringComparison.OrdinalIgnoreCase))?.Substring("--give=".Length) ?? "Event Ticket";
      string getRaw   = args.FirstOrDefault(a => a.StartsWith("--get=",  StringComparison.OrdinalIgnoreCase))?.Substring("--get=".Length)  ?? "Azimaet Drake";
      bool listen     = args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase));
      // --get may list SEVERAL required cards (comma-separated) to test incomplete offers.
      var getNames = getRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
      string getList = string.Join(", ", getNames);
      int giveQty = 1;
      if (partner.Length == 0) { Line("Usage: swap <partner> [--give=<card>] [--get=<card,card,...>] [--listen] [--commit] --yes"); break; }
      if (getNames.Count == 0) { Line("Need at least one --get=<card>."); break; }
      if (!yes) { Line($"Refusing: opens a trade with '{partner}', offers {giveQty}x {giveCard}, requests [{getList}]{(allowCommit ? " and WILL COMMIT (both sides move)" : " (dry-run: stops before commit)")}. Re-run with --yes."); break; }
      Line($"Swap with {partner}: GIVE {giveQty}x {giveCard}  <->  GET [{getList}]." + (allowCommit ? "  *** WILL COMMIT ***" : "  [dry-run: reaches both-ready then cancels — moves nothing]"));

      exec.Attach();

      // ensure our offer binder holds the give card (owned printing).
      var offer = exec.CreateSingleCardBinder("SwapOffer", giveCard);
      if (offer is null) { Line("Could not create the SwapOffer binder — aborting."); break; }
      Line($"Offer binder ready: '{offer.Name}' (id={offer.Id}, items={offer.ItemCount}).");

      if (listen)
      {
        // PERSISTENT LISTENER: stay running and watch the chat with the partner
        // FOREVER. Each time they reply YES — whenever that is — fire one swap
        // cycle, then go back to listening. No window, purely trigger-driven. Stop
        // by ending the task (there is no other exit).
        if (!exec.EnsureKnownUser(partner)) { Line($"Could not resolve '{partner}' — aborting."); break; }
        try { exec.SendDM(partner, $"Swap desk is OPEN: my {giveQty} {giveCard} for your [{getList}]. Reply YES any time you're ready — I'm watching continuously. When I send the trade, accept it, present [{getList}], and grab the {giveCard}."); } catch { }
        Line($"\n>>> LISTENING continuously for a YES from {partner}. Reply YES in MTGO whenever ready; stop this task to end. <<<\n");
        while (true)
        {
          if (!exec.WaitForDMYes(partner, int.MaxValue)) continue;   // blocks until a NEW 'yes'
          Line($"\n>>> YES received from {partner} — starting a swap cycle. <<<");
          try { exec.SendDM(partner, $"On it — sending the trade now. Accept it, present [{getList}], and grab the {giveCard} from my SwapOffer binder."); } catch { }
          exec.CancelCurrent(); WaitForNoTrade();
          var le = TryReachNegotiation(exec, partner, presentBinder: "SwapOffer", negotiateWaitSec: 90);
          if (le is null)
          {
            exec.CancelCurrent(); WaitForNoTrade();
            string why = exec.LastCloseReason ?? "";
            if (why.IndexOf("Busy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
              // The partner's client auto-declined because IT thinks it's already
              // trading (a stuck/leftover trade on their end). Retrying is futile
              // until they clear it — say so plainly instead of looping.
              Line($"\n!!! Invite auto-DECLINED — {partner} is 'busy trading' ({why}). Their MTGO client has a stuck/open trade, so it rejects invites WITHOUT showing them. Fix: on the {partner} account, CLOSE any trade window, or fully restart MTGO. Then reply YES again. (Still listening.) !!!\n");
              try { exec.SendDM(partner, "Couldn't reach you — your client reports you're already in a trade, so it declined mine automatically. Close any open trade window (or restart MTGO), then reply YES."); } catch { }
            }
            else
            {
              Line($"Invite not accepted (closed: {(why.Length > 0 ? why : "unknown")}) — back to listening.");
              try { exec.SendDM(partner, "Didn't see you accept the trade — reply YES again when you're ready."); } catch { }
            }
            continue;
          }
          bool ok = RunSwapCycle(exec, le, partner, giveCard, giveQty, getNames, allowCommit);
          Line(ok ? "\n>>> Swap cycle COMPLETE — back to listening for the next YES. <<<\n"
                  : "\n>>> Swap cycle ended (cancelled/incomplete) — back to listening for the next YES. <<<\n");
        }
        // (unreachable — loop only exits when the task is stopped)
      }

      // ONE-SHOT: standardized handshake (DM -> wait up to 5 min for YES) then one cycle.
      var esc = HandshakeThenInitiate(exec, partner,
        $"Swap offer: my {giveQty} {giveCard} for your [{getList}]. Reply YES when ready — then accept the trade, present [{getList}], and grab the {giveCard} from my SwapOffer binder.",
        presentBinder: "SwapOffer");
      if (esc is null) { Line($"Swap not started with {partner} (no YES, or the invite wasn't accepted). Nothing moved."); exec.CancelCurrent(); WaitForNoTrade(); break; }
      RunSwapCycle(exec, esc, partner, giveCard, giveQty, getNames, allowCommit);
      break;
    }

    default:
      Line($"Unknown mode '{mode}'. Use:");
      Line("  read-only : probe | find [kw] | watch | botstatus <bot> | poolstatus [--bots=a,b,c]");
      Line("              readcurrent [card] | readchat | ledger");
      Line("  acquire   : autograb <bot> <card> --yes | poolgrab <card> [--bots=a,b,c] --yes");
      Line("  full-auto : autofullgrab <bot> [card] --yes         (request->deposit->approval-ready; add --commit to complete)");
      Line("  lend      : lend <recipient> [card] --yes           (DM->wait YES->give card w/ guardrail; add --commit to complete)");
      Line("  swap      : swap <partner> [--give=<card>] [--get=<card>] [--listen] --yes  (offer one card, request another; --listen watches for YES continuously; add --commit)");
      Line("  low-level : opentrade <bot> --yes | takecard <card> --yes | grab <bot> --yes | invite <user> --yes");
      Line("  test      : stagetest <bot> [card] --yes  (controlled TakeCard test: stage -> observe -> cancel)");
      Line("  outward   : post \"<msg>\" --yes | clearpost --yes");
      break;
  }

  Line(allowCommit
    ? "\nDone. (AllowCommit was TRUE for this run — a final approve was authorized.)"
    : "\nDone. (AllowCommit was false the entire run — no trade was committed.)");
}
