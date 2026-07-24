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
static MTGOSDK.API.Trade.TradeEscrow? TryReachNegotiation(TradeBot.TradeExecutor exec, string bot)
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
        try { exec.AdvanceBinderSelection(e0); advanced = true; }
        catch (Exception ex) { Line($"(binder advance threw: {ex.Message.Split('\n')[0]})"); }
      }
    }
    else if (st0 != MTGOSDK.API.Trade.Enums.TradeState.Uninitialized)
    {
      dispatched = true; // moved to InviteSent / InviteAccepted / Negotiate...
    }
  }

  // Wait for negotiation (or an early close = bot busy/declined).
  nulls = 0;
  for (int i = 0; i < 30; i++)
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
    Line($"\nFound {avail}x {cname} (catId={catId}). Staging 1 into the trade...");
    exec.TakeCard(esc, catId, 1);
    System.Threading.Thread.Sleep(3000);
    var e2 = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (e2 != null)
    {
      Line($"state={e2.State}");
      try { Line($"  WE RECEIVE: {TradeBot.TradeExecutor.Summarize(e2.PartnerTradedItems)}"); } catch (Exception ex) { Line($"  (receive read failed: {ex.Message})"); }
      try { Line($"  WE GIVE:    {TradeBot.TradeExecutor.Summarize(e2.TradedItems)}"); } catch { }
    }
  }

  // HOLD the session open (bot stays attached, keeping the trade alive) while
  // YOU review and click Submit. The bot never commits.
  Line("\n>>> Review the trade window and click SUBMIT to complete it. <<<");
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
using (var exec = new TradeExecutor { AllowCommit = false })
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
  if (!loggedIn && mtgoRunning)
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

    default:
      Line($"Unknown mode '{mode}'. Use:");
      Line("  read-only : probe | find [kw] | watch | botstatus <bot> | poolstatus [--bots=a,b,c]");
      Line("              readcurrent [card] | readchat | ledger");
      Line("  acquire   : autograb <bot> <card> --yes | poolgrab <card> [--bots=a,b,c] --yes");
      Line("  low-level : opentrade <bot> --yes | takecard <card> --yes | grab <bot> --yes | invite <user> --yes");
      Line("  outward   : post \"<msg>\" --yes | clearpost --yes");
      break;
  }

  Line("\nDone. (AllowCommit was false the entire run — no trade was committed.)");
}
