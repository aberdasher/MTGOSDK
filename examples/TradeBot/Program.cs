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
    gamelog [--stream]
                     READ-ONLY: list the games the client is aware of + dump the
                     LogChannel (action log) of any it is WATCHING. --stream tails
                     new log lines live.
    spectate <gameId|player> [--stream]   OR   spectate --any [--stream]
                     Drive the client to WATCH a live game itself (dispatch
                     PlayerEventActions.WatchGame() into the game's Match), then dump
                     + stream its action log. --any auto-picks the first live game.
                     Read-only observation (registers as a watcher; no state change).
    autoreport --pair=<a>,<b> [--session=<id>] [--dry-run]   OR   autoreport --daemon [--poll=N] [--dry-run]
                     Auto-report freeform Bo3 draft-match results to DraftBot: watch a
                     pairing to completion, then POST who won to DRAFTBOT_API_BASE (bearer
                     DRAFTBOT_API_TOKEN). --daemon polls DraftBot's /pairings/active and
                     reports each pairing as it finishes. --dry-run observes without POSTing.

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

using GS = MTGOSDK.API.Play.Games;
using GSProc = MTGOSDK.API.Play.Games.Processors;
using GSArgs = MTGOSDK.API.Play.Games.Processors.EventArgs;

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

// Dump a spectated/replay game's action log (Game.LogChannel = "the log channel for
// all game actions") + basic state. Read-only. `g` is a wrapped SDK Game.
static void DumpGameLog(MTGOSDK.API.Play.Games.Game g, int last = 60)
{
  int id = -1, turn = -1; string status = "?", phase = "?"; bool rep = false;
  try { id = g.Id; } catch { }
  try { status = g.Status.ToString(); } catch { }
  try { rep = g.IsReplay; } catch { }
  try { turn = g.CurrentTurn; } catch { }
  try { phase = g.CurrentPhase.ToString(); } catch { }
  var players = new System.Collections.Generic.List<string>();
  try { foreach (var p in g.Players) { try { players.Add(p.Name); } catch { } } } catch { }
  Line($"\n=== Game {id}{(rep ? "  [REPLAY]" : "")}  status={status}  turn={turn} phase={phase}  players={(players.Count > 0 ? string.Join(" vs ", players) : "?")} ===");

  MTGOSDK.API.Chat.Channel? ch = null;
  try { ch = g.LogChannel; } catch (Exception ex) { Line($"  (log channel unavailable: {ex.Message.Split('\n')[0]})"); return; }
  if (ch is null) { Line("  (no log channel)"); return; }
  System.Collections.Generic.IList<MTGOSDK.API.Chat.Message> msgs;
  try { msgs = ch.Messages; } catch (Exception ex) { Line($"  (log messages unavailable: {ex.Message.Split('\n')[0]})"); return; }
  Line($"  game log: {msgs.Count} entries; last {Math.Min(last, msgs.Count)}:");
  for (int i = Math.Max(0, msgs.Count - last); i < msgs.Count; i++)
  {
    string who = "", txt = "";
    try { who = msgs[i].User?.Name ?? ""; } catch { }
    try { txt = (msgs[i].Text ?? "").Replace("\n", " ").Replace("\r", " ").Trim(); } catch { }
    Line($"    {(who.Length > 0 ? who + ": " : "")}{txt}");
  }
}

// Dump one player zone (battlefield in detail; others brief name lists). Reads the
// live per-game zone model of a WATCHED game. Each field is a remote read → wrapped
// in try/catch and capped to bound IPC on big boards.
static void DumpZone(MTGOSDK.API.Play.Games.Game g, MTGOSDK.API.Play.Games.GamePlayer p,
                     MTGOSDK.API.Play.Games.CardZone zone, string label, int max, bool brief = false)
{
  MTGOSDK.API.Play.Games.GameZone gz;
  try { gz = g.GetGameZone(p, zone); } catch { return; }   // zone not present for this player
  System.Collections.Generic.List<MTGOSDK.API.Play.Games.GameCard> cards;
  try { cards = System.Linq.Enumerable.ToList(gz.Cards); } catch { return; }
  if (cards.Count == 0) return;
  Line($"      {label} ({cards.Count}):");
  int shown = 0;
  foreach (var c in cards)
  {
    if (shown++ >= max) { Line($"        ... (+{cards.Count - max} more)"); break; }
    string nm = "?"; try { nm = c.Name; } catch { }
    if (brief) { Line($"        {nm}"); continue; }
    string pt = ""; var flags = new System.Collections.Generic.List<string>();
    try { string tl = c.TypeLine ?? ""; if (tl.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0)
          { int dmg = 0; try { dmg = c.Damage; } catch { } pt = $" {c.Power}/{c.Toughness}" + (dmg > 0 ? $" (dmg {dmg})" : ""); } } catch { }
    try { int loy = c.Loyalty; if (loy > 0) flags.Add($"loy {loy}"); } catch { }
    try { if (c.IsTapped) flags.Add("tapped"); } catch { }
    try { if (c.IsAttacking) flags.Add("attacking"); } catch { }
    try { if (c.IsBlocking) flags.Add("blocking"); } catch { }
    try { if (c.HasSummoningSickness) flags.Add("sick"); } catch { }
    try { if (c.IsToken) flags.Add("token"); } catch { }
    try { if (System.Linq.Enumerable.Any(c.Counters)) flags.Add("counters"); } catch { }
    Line($"        {nm}{pt}{(flags.Count > 0 ? "  [" + string.Join(",", flags) + "]" : "")}");
  }
}

// Snapshot the STRUCTURED board state of a watched game (vs the text log): turn/
// phase/prompt, each player's life+counts+counters+mana, their battlefield (with
// P/T, tapped, combat, counters), graveyard/exile, and the stack. Read-only pull.
static void DumpGameState(MTGOSDK.API.Play.Games.Game g, int maxCardsPerZone = 40)
{
  int turn = -1; string phase = "?", active = "?", priority = "?", prompt = "";
  try { turn = g.CurrentTurn; } catch { }
  try { phase = g.CurrentPhase.ToString(); } catch { }
  try { active = g.ActivePlayer?.Name ?? "?"; } catch { }
  try { priority = g.PriorityPlayer?.Name ?? "?"; } catch { }
  try { prompt = g.Prompt?.Text ?? ""; } catch { }
  Line($"  turn {turn}  phase={phase}  active={active}  priority={priority}");
  if (prompt.Length > 0) Line($"  prompt: {prompt.Replace("\n", " ").Trim()}");
  try { var w = System.Linq.Enumerable.ToList(g.WinningPlayers);
        if (w.Count > 0) Line($"  winner(s): {string.Join(", ", w.ConvertAll(p => { try { return p.Name; } catch { return "?"; } }))}"); } catch { }

  System.Collections.Generic.IList<MTGOSDK.API.Play.Games.GamePlayer> players;
  try { players = g.Players; } catch (Exception ex) { Line($"  (players unavailable: {ex.Message.Split('\n')[0]})"); return; }
  foreach (var p in players)
  {
    string name = "?", clock = "", counters = "", mana = ""; int life = 0, hand = 0, lib = 0, grave = 0;
    try { name = p.Name; } catch { }
    try { life = p.Life; } catch { }
    try { hand = p.HandCount; } catch { }
    try { lib = p.LibraryCount; } catch { }
    try { grave = p.GraveyardCount; } catch { }
    try { clock = p.ChessClock.ToString(@"mm\:ss"); } catch { }
    try { var c = p.Counters; if (c != null && c.Count > 0) counters = "  counters=[" + string.Join(",", System.Linq.Enumerable.Select(c, kv => $"{kv.Key}:{kv.Value}")) + "]"; } catch { }
    try { int mp = System.Linq.Enumerable.Count(p.ManaPool); if (mp > 0) mana = $"  mana={mp}"; } catch { }
    Line($"\n  ── {name}   life={life}  hand={hand} lib={lib} grave={grave}{(clock.Length > 0 ? "  clock=" + clock : "")}{counters}{mana}");
    DumpZone(g, p, MTGOSDK.API.Play.Games.CardZone.Battlefield, "battlefield", maxCardsPerZone);
    DumpZone(g, p, MTGOSDK.API.Play.Games.CardZone.Graveyard, "graveyard", 15, brief: true);
    DumpZone(g, p, MTGOSDK.API.Play.Games.CardZone.Exile, "exile", 15, brief: true);
  }

  // Shared zones — in MTGO's model the battlefield + stack are SHARED, holding
  // BOTH players' permanents/spells tagged by controller (so per-player battlefield
  // lookups above come back empty). Dump them with controller + P/T + combat flags.
  try
  {
    foreach (var z in g.SharedZones)
    {
      string zname = "?"; System.Collections.Generic.List<MTGOSDK.API.Play.Games.GameCard> cards;
      try { zname = z.Name ?? "?"; } catch { }
      try { cards = System.Linq.Enumerable.ToList(z.Cards); } catch { continue; }
      if (cards.Count == 0) continue;
      Line($"\n  ── {zname} ({cards.Count}):");
      int shown = 0;
      foreach (var c in cards)
      {
        if (shown++ >= maxCardsPerZone) { Line($"      ... (+{cards.Count - maxCardsPerZone} more)"); break; }
        string nm = "?", ctrl = ""; try { nm = c.Name; } catch { }
        try { ctrl = c.Controller?.Name ?? ""; } catch { }
        string pt = ""; var flags = new System.Collections.Generic.List<string>();
        try { string tl = c.TypeLine ?? ""; if (tl.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0)
              { int dmg = 0; try { dmg = c.Damage; } catch { } pt = $" {c.Power}/{c.Toughness}" + (dmg > 0 ? $" (dmg {dmg})" : ""); } } catch { }
        try { int loy = c.Loyalty; if (loy > 0) flags.Add($"loy {loy}"); } catch { }
        try { if (c.IsTapped) flags.Add("tapped"); } catch { }
        try { if (c.IsAttacking) flags.Add("attacking"); } catch { }
        try { if (c.IsBlocking) flags.Add("blocking"); } catch { }
        try { if (c.HasSummoningSickness) flags.Add("sick"); } catch { }
        try { if (c.IsToken) flags.Add("token"); } catch { }
        Line($"      {(ctrl.Length > 0 ? ctrl + ": " : "")}{nm}{pt}{(flags.Count > 0 ? "  [" + string.Join(",", flags) + "]" : "")}");
      }
    }
  }
  catch (Exception ex) { Line($"  (shared zones unavailable: {ex.Message.Split('\n')[0]})"); }
}

// Acquire a full GameStateSnapshot for a watched game via the SDK processor/event
// pipeline (the correct, IPC-free path: card partials resolve TypeLine/P/T/zone
// locally). Subscribing activates the processor; ReadyProcessor() + WaitForPending
// flush the buffered ticks; we cache the snapshot from whichever event fires.
static GSProc.GameStateSnapshot? GetSnapshot(GS.Game game, int waitSec = 20)
{
  GSProc.GameStateSnapshot? snap = null;
  Action<GSArgs.PromptChangedEventArgs> onPrompt = a => { try { snap = a.Snapshot; } catch { } };
  Action<GSArgs.CardChangedEventArgs>   onCard   = a => { try { snap = a.Snapshot; } catch { } };
  Action<GSArgs.PlayerChangedEventArgs> onPlayer = a => { try { snap = a.Snapshot; } catch { } };
  try
  {
    game.OnPromptChanged += onPrompt;   // first subscribe activates the processor
    game.OnCardChanged   += onCard;
    game.OnPlayerChanged += onPlayer;
    game.ReadyProcessor();              // MANDATORY: drain loop is inert until this
    game.WaitForPendingProcessing(TimeSpan.FromSeconds(waitSec));
  }
  catch (Exception ex) { Line($"  (snapshot subscribe failed: {ex.Message.Split('\n')[0]})"); }
  for (int i = 0; i < waitSec && snap is null; i++) System.Threading.Thread.Sleep(1000);
  try { game.ClearEvents(); } catch { }
  return snap;
}

static void SnapAdd(System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<GS.GameCard>> d, int k, GS.GameCard c)
{ if (!d.TryGetValue(k, out var l)) { l = new(); d[k] = l; } l.Add(c); }

static string SnapName(GS.GameCard c) { try { return c.Name ?? "?"; } catch { return "?"; } }

// One permanent/spell rendered with everything a spectator can see about it.
static string SnapCardLine(GS.GameCard c)
{
  string nm = SnapName(c), tl = "";
  try { tl = (c.TypeLine ?? "").Trim(); } catch { }
  bool creature = tl.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0;
  string pt = "";
  if (creature) { int pw = 0, to = 0; try { pw = c.Power; to = c.Toughness; } catch { } pt = $" {pw}/{to}"; }

  var extra = new System.Collections.Generic.List<string>();
  try { int loy = c.Loyalty; if (loy > 0) extra.Add($"loy {loy}"); } catch { }
  try { int dmg = c.Damage;  if (dmg > 0) extra.Add($"dmg {dmg}"); } catch { }
  try { int ch = c.CurrentChapter; if (ch > 0) extra.Add($"chapter {ch}"); } catch { }
  try { int lv = c.CurrentLevel;   if (lv > 0) extra.Add($"level {lv}"); } catch { }
  try
  {
    var g = new System.Collections.Generic.Dictionary<string, int>();
    foreach (var cc in c.Counters) { var k = cc.ToString() ?? "?"; g[k] = g.GetValueOrDefault(k) + 1; }
    foreach (var kv in g) extra.Add($"{kv.Key}×{kv.Value}");
  }
  catch { }

  var flags = new System.Collections.Generic.List<string>();
  try { if (c.IsTapped) flags.Add("tapped"); } catch { }
  try { if (c.IsAttacking) flags.Add("attacking"); } catch { }
  try { if (c.IsBlocking) flags.Add("blocking"); } catch { }
  try { if (c.IsBlocked) flags.Add("blocked"); } catch { }
  try { if (c.HasSummoningSickness) flags.Add("sick"); } catch { }
  try { if (c.IsToken) flags.Add("token"); } catch { }
  try { if (c.IsFaceDown) flags.Add("face-down"); } catch { }
  try { if (c.IsPhasedOut) flags.Add("phased"); } catch { }

  var tail = new System.Collections.Generic.List<string>();
  if (extra.Count > 0) tail.Add(string.Join(",", extra));
  if (flags.Count > 0) tail.Add(string.Join(",", flags));
  string typ = creature ? "" : (tl.Length > 0 ? "  — " + tl : "");
  return $"{nm}{pt}{typ}{(tail.Count > 0 ? "  [" + string.Join(" | ", tail) + "]" : "")}";
}

// Render a full board from a GameStateSnapshot: turn/phase/prompt, each player's
// life+counts+counters+mana, their battlefield (detailed), graveyard/exile, the
// stack, and global designations (monarch/emblem). All local, no per-card IPC.
static void DumpGameStateSnap(GSProc.GameStateSnapshot snap)
{
  var names = new System.Collections.Generic.Dictionary<int, string>();
  foreach (var kv in snap.Players)
  { string nm; try { nm = kv.Value.Name; } catch { nm = null!; } names[kv.Key] = string.IsNullOrWhiteSpace(nm) ? $"P{kv.Key}" : nm; }

  string toAct = snap.PromptedPlayer == byte.MaxValue ? "(all/none)" : names.GetValueOrDefault(snap.PromptedPlayer, $"P{snap.PromptedPlayer}");
  Line($"  turn {snap.TurnNumber}  phase={snap.CurrentPhase}  toAct={toAct}  ({snap.Cards.Count} visible cards)");
  if (!string.IsNullOrWhiteSpace(snap.PromptText)) Line($"  prompt: {snap.PromptText.Replace("\n", " ").Trim()}");

  var bf = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<GS.GameCard>>();
  var gy = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<GS.GameCard>>();
  var ex = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<GS.GameCard>>();
  var stack = new System.Collections.Generic.List<GS.GameCard>();
  var markers = new System.Collections.Generic.List<string>();
  foreach (var kv in snap.Cards)
  {
    var c = kv.Value;
    string zone = ""; int ctrl = -1, own = -1;
    try { zone = c.Zone?.Name ?? ""; } catch { }
    try { ctrl = c.ControllerIndex; } catch { }
    try { own = c.OwnerIndex; } catch { }
    try { if (c.IsMonarch) markers.Add($"Monarch: {names.GetValueOrDefault(ctrl, "?")}"); } catch { }
    try { if (c.IsCitysBlessing) markers.Add($"City's Blessing: {names.GetValueOrDefault(ctrl, "?")}"); } catch { }
    try { if (c.IsEmblem) markers.Add($"Emblem ({SnapName(c)}): {names.GetValueOrDefault(ctrl, "?")}"); } catch { }
    switch (zone)
    {
      case "Battlefield": SnapAdd(bf, ctrl, c); break;
      case "Graveyard":   SnapAdd(gy, own, c); break;
      case "Exile":       SnapAdd(ex, own, c); break;
      case "Stack":       stack.Add(c); break;
    }
  }

  foreach (var idx in System.Linq.Enumerable.OrderBy(snap.Players.Keys, k => k))
  {
    var p = snap.Players[idx];
    int life = 0, hand = 0, lib = 0, grave = 0; string clock = "", counters = "", mana = "";
    try { life = p.Life; } catch { }
    try { hand = p.HandCount; } catch { }
    try { lib = p.LibraryCount; } catch { }
    try { grave = p.GraveyardCount; } catch { }
    try { clock = p.ChessClock.ToString(@"mm\:ss"); } catch { }
    try { var cd = p.Counters; if (cd != null && cd.Count > 0) counters = "  counters=[" + string.Join(",", System.Linq.Enumerable.Select(cd, x => $"{x.Key}:{x.Value}")) + "]"; } catch { }
    try
    {
      var parts = new System.Collections.Generic.List<string>();
      foreach (var m in p.ManaPool) { try { int amt = m.Amount; if (amt > 0) parts.Add($"{m.Symbol}×{amt}"); } catch { } }
      if (parts.Count > 0) mana = "  mana=" + string.Join("", parts);
    }
    catch { }
    Line($"\n  ── {names.GetValueOrDefault(idx, $"P{idx}")}   life={life}  hand={hand} lib={lib} grave={grave}{(clock.Length > 0 ? "  clock=" + clock : "")}{counters}{mana}");

    if (bf.TryGetValue(idx, out var perms) && perms.Count > 0)
    {
      Line($"      battlefield ({perms.Count}):");
      foreach (var c in perms) Line($"        {SnapCardLine(c)}");
    }
    if (gy.TryGetValue(idx, out var gcards) && gcards.Count > 0)
      Line($"      graveyard ({gcards.Count}): {string.Join(", ", System.Linq.Enumerable.Select(gcards, SnapName))}");
    if (ex.TryGetValue(idx, out var ecards) && ecards.Count > 0)
      Line($"      exile ({ecards.Count}): {string.Join(", ", System.Linq.Enumerable.Select(ecards, SnapName))}");
  }

  if (stack.Count > 0)
  {
    Line($"\n  ── stack ({stack.Count}) [resolves top-down]:");
    foreach (var c in stack)
    { int ci = -1; try { ci = c.ControllerIndex; } catch { } Line($"        {names.GetValueOrDefault(ci, "?")}: {SnapCardLine(c)}"); }
  }

  if (markers.Count > 0)
  {
    Line("\n  ── designations:");
    foreach (var m in System.Linq.Enumerable.Distinct(markers)) Line($"        {m}");
  }
}

// Tail a game's LogChannel, printing new lines as they stream in (a spectated
// game's actions arrive here live once you're connected as a watcher).
static void StreamGameLog(MTGOSDK.API.Play.Games.Game g, int maxSeconds = 7200)
{
  int gid = -1; try { gid = g.Id; } catch { }
  Line($"\nStreaming NEW log lines from game {gid} (Ctrl+C to stop)...");
  int seen = 0; try { seen = g.LogChannel.Messages.Count; } catch { }
  for (int t = 0; t < maxSeconds; t++)
  {
    System.Threading.Thread.Sleep(1000);
    System.Collections.Generic.IList<MTGOSDK.API.Chat.Message> msgs;
    try { msgs = g.LogChannel.Messages; } catch { continue; }
    for (int i = seen; i < msgs.Count; i++)
    {
      string who = "", txt = "";
      try { who = msgs[i].User?.Name ?? ""; } catch { }
      try { txt = (msgs[i].Text ?? "").Replace("\n", " ").Replace("\r", " ").Trim(); } catch { }
      Line($"    {(who.Length > 0 ? who + ": " : "")}{txt}");
    }
    seen = msgs.Count;
  }
}

// Enumerate the games the client currently has (live watched = InProgressGame,
// replays = ReplayGame), wrapped as SDK Game objects.
static System.Collections.Generic.List<MTGOSDK.API.Play.Games.Game> FindGames()
{
  var games = new System.Collections.Generic.List<MTGOSDK.API.Play.Games.Game>();
  foreach (var tn in new[] {
    "WotC.MtGO.Client.Model.Play.InProgressGameEvent.InProgressGame",
    "WotC.MtGO.Client.Model.Play.ReplayGameEvent.ReplayGame" })
  {
    try { foreach (var inst in MTGOSDK.Core.Remoting.RemoteClient.GetInstances(tn))
          { try { games.Add(new MTGOSDK.API.Play.Games.Game(inst)); } catch { } } }
    catch (Exception ex) { Line($"(no {tn.Substring(tn.LastIndexOf('.') + 1)} instances — {ex.Message.Split('\n')[0]})"); }
  }
  return games;
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

  // Presence-aware: send the prompt now AND re-send whenever they come online (a DM to an
  // offline user can be dropped by MTGO), returning on their YES. This is what makes an
  // offline recipient work — when they log back in they get a fresh prompt they can see.
  Line(yesTimeoutSec >= int.MaxValue / 2
    ? $"Prompt sent to {partner} — waiting (no timeout) for them to be online + reply YES; re-sending on each reconnect."
    : $"Prompt sent to {partner} — waiting up to {yesTimeoutSec / 60} min for them to be online + reply YES.");
  if (!exec.SendPromptWhenOnlineAndWaitForYes(partner, readyMsg, yesTimeoutSec))
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

// One GRAB cycle from a live negotiating escrow to completion: request the card
// from the partner's presented binder (the MATCH count is the real "is it in their
// offer" signal — reads the true partner trade-binder), guardrail (WE RECEIVE ==
// exactly the card, WE GIVE nothing), deposit, approve. Cancels on any take-from-us,
// if the card isn't in their offer, or if it's there but can't be pulled (stale VM).
// Returns true iff committed.
static bool RunGrabCycle(TradeBot.TradeExecutor exec, MTGOSDK.API.Trade.TradeEscrow esc,
    string partner, string card, System.Collections.Generic.List<int> cats, bool allowCommit, int qty = 1)
{
  string qtyCard = qty > 1 ? $"{qty}x {card}" : card;
  Line($"\nTrade open with {esc.TradePartnerName}. Looking for {qtyCard} in your presented binder...");
  bool ready = false, matchedEver = false;
  int lastReq = -100, lastNote = -100;
  const int findSec = 12, windowSec = 90;
  for (int i = 0; i < windowSec && !ready; i++)
  {
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed."); return false; }
    esc = c;

    int giveCount = 0; try { giveCount = c.TradedItems.CollectionItems.Count; } catch { }
    if (giveCount > 0)
    {
      Line($"You grabbed from my side ({TradeBot.TradeExecutor.Summarize(c.TradedItems)}) — cancelling; this is one-way, I give nothing.");
      exec.CancelCurrent(); WaitForNoTrade();
      try { exec.SendDM(partner, "Cancelled — this is a one-way grab (you give me the card, I give nothing). Don't take anything from my side; reply YES to retry."); } catch { }
      return false;
    }

    bool haveIt = false; try { foreach (var it in c.PartnerTradedItems.CollectionItems) if ((it.Card?.Name ?? "").IndexOf(card, StringComparison.OrdinalIgnoreCase) >= 0) { haveIt = true; break; } } catch { }
    if (exec.VerifyReceiveIsOnly(c, card, qty)) { ready = true; break; }

    if (!exec.VerifyReceiveIsOnly(c, card, qty) && i - lastReq >= 4)
    {
      lastReq = i;
      foreach (var cat in cats) { exec.RequestViaWishlist(cat, qty, card); if (exec.LastMatchCount > 0) matchedEver = true; }
    }

    if (i - lastNote >= 3) { lastNote = i; Line($"  [t+{i,3}s] {card} is in your offer: {(matchedEver ? "yes" : "not yet")}  |  I've secured it: {(haveIt ? "yes" : "no")}  |  WE RECEIVE: {TradeBot.TradeExecutor.Summarize(c.PartnerTradedItems)}"); }

    if (i >= findSec && !matchedEver && !haveIt)
    {
      Line($"\n!!! CANCELLING — the {card} isn't in your presented binder (match found nothing). !!!\n");
      exec.CancelCurrent(); WaitForNoTrade();
      try { exec.SendDM(partner, $"Cancelled — I didn't find the {card} in the binder you presented. Pick a binder that has it (before accepting), then reply YES to retry."); } catch { }
      return false;
    }
    if (i >= findSec + 20 && matchedEver && !haveIt)
    {
      Line($"\n!!! CANCELLING — the {card} is in your binder but I can't pull it onto my side (stale trade view-model). A client restart is needed. !!!\n");
      exec.CancelCurrent(); WaitForNoTrade();
      try { exec.SendDM(partner, $"Cancelled — I see the {card} but couldn't complete the pull (client hiccup)."); } catch { }
      return false;
    }
    System.Threading.Thread.Sleep(1000);
  }
  if (!ready) { Line("Didn't secure the cards in time — cancelling."); exec.CancelCurrent(); WaitForNoTrade(); return false; }
  Line($"GUARDRAIL passed: we receive exactly {qtyCard} and give nothing.");

  exec.SubmitDeposit();
  bool approveReady = false; string st = "?";
  for (int i = 0; i < 30 && !approveReady; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed before approval."); return false; }
    st = c.State.ToString(); esc = c;
    if (i % 3 == 0) Line($"  t+{i,2}s state={st}");
    if (st.StartsWith("Approval")) approveReady = true;
  }
  if (!approveReady) { Line("Did not reach approval-ready — cancelling."); exec.CancelCurrent(); WaitForNoTrade(); return false; }

  if (!exec.VerifyReceiveIsOnly(esc, card, qty))
  {
    Line("GUARDRAIL re-check FAILED at approval — cancelling.");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    try { exec.SendDM(partner, "Cancelled at the final check for safety."); } catch { }
    return false;
  }

  if (!allowCommit)
  {
    Line("\n[dry-run] Approval-ready + guardrail OK; no --commit -> cancelling (received nothing).");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    Line("Re-run with `--commit --yes` to actually take the card.");
    return false;
  }

  Line("\n*** COMMITTING the grab (ConfirmTrade) ***");
  exec.ConfirmTrade();
  for (int i = 0; i < 40; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Grab complete — client clear."); break; }
    if (i % 3 == 0) Line($"  t+{i,2}s state={c.State}");
    if (c.State == MTGOSDK.API.Trade.Enums.TradeState.Closed) break;
  }
  try { exec.SendDM(partner, $"Thanks — got {qtyCard}!"); } catch { }
  Line("\nLedger (latest):");
  var glp = TradeBot.TradeExecutor.LedgerPath;
  if (System.IO.File.Exists(glp)) foreach (var l in System.IO.File.ReadAllLines(glp).Reverse().Take(1)) Line("  " + l);
  return true;
}

// ── Full one-shot flows (handshake → cycle → commit), shared by the interactive
//    modes and the order `worker`. Each returns true iff it committed. ────────────

// LEND a SET of cards to a recipient (qty-aware). Builds the Lending binder EXACTLY,
// handshake, present it, wait for them to grab all of it (reminding on an early
// submit / cancelling on extras), submit our deposit, re-verify, then approve.
static bool RunLend(TradeBot.TradeExecutor exec, string recipient,
    System.Collections.Generic.List<(string name, int qty)> intended, bool allowCommit,
    bool keepOpen = false, int yesTimeoutSec = 300)
{
  var giveNames = intended.SelectMany(it => System.Linq.Enumerable.Repeat(it.name, System.Math.Max(1, it.qty))).ToList();
  // Human-readable list, e.g. "3x Kozilek's Command". Used for BOTH console and DMs —
  // do NOT wrap it in [ ] in a DM: MTGO chat treats [text] as a card-link and renders a
  // comma/qty blob as empty (recipient saw "Ready for ?").
  string cardList = string.Join(", ", intended.Select(it => it.qty > 1 ? $"{it.qty}x {it.name}" : it.name));
  int totalQty = intended.Sum(it => it.qty);
  string cardCount = totalQty == 1 ? "the card" : $"all {totalQty} cards";

  var lendBinder = exec.EnsureBinderExact("Lending", giveNames);
  if (lendBinder is null) { Line("Could not build the Lending binder — aborting."); return false; }
  Line($"Lending binder ready: '{lendBinder.Name}' (id={lendBinder.Id}, items={lendBinder.ItemCount}).");

  var esc = HandshakeThenInitiate(exec, recipient,
    $"Ready for {cardList}? Reply YES and I'll send you a trade — then accept it and grab {cardCount} from my offer.",
    presentBinder: "Lending", yesTimeoutSec: keepOpen ? int.MaxValue : yesTimeoutSec);
  if (esc is null)
  {
    try { exec.SendDM(recipient, "Couldn't open the trade — reply YES when you're ready and I'll retry."); } catch { }
    return false;
  }
  Line($"\nTrade open with {esc.TradePartnerName}. They should grab: {cardList}.");

  bool ready = false;
  string lastRemindKey = "";
  // keepOpen: no grab-window timeout — hold the offer until they finish grabbing or the
  // trade closes (the c==null check below breaks out on a close/cancel).
  for (int i = 0; (keepOpen || i < 300) && !ready; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed before they finished grabbing."); return false; }
    esc = c;
    var (missing, extra, exact) = exec.GiveStatus(c, intended);

    if (extra.Count > 0)
    {
      Line($"They grabbed something not offered ({string.Join(", ", extra)}) — cancelling for safety.");
      exec.CancelCurrent(); WaitForNoTrade();
      try { exec.SendDM(recipient, $"Cancelled — you grabbed {string.Join(", ", extra)}, which isn't part of this lend. Please grab ONLY {cardList}, then reply YES to retry."); } catch { }
      return false;
    }
    if (exact) { ready = true; break; }

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
      if (!TradeBot.TradeExecutor.PartnerHasSubmitted(c)) lastRemindKey = "";
      if (i % 10 == 0) Line($"  t+{i,3}s  WE GIVE: {TradeBot.TradeExecutor.Summarize(c.TradedItems)}  (still to grab: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))})");
    }
  }
  if (!ready)
  {
    Line("They didn't grab the full set in time — cancelling.");
    exec.CancelCurrent(); WaitForNoTrade();
    try { exec.SendDM(recipient, $"Cancelled — didn't get all of {cardList} grabbed in time. Reply YES to retry."); } catch { }
    return false;
  }
  Line("GUARDRAIL passed: we give exactly the intended set and receive nothing.");

  exec.SubmitDeposit();
  string sst = "?"; bool approveReady = false;
  for (int i = 0; i < 30 && !approveReady; i++)
  {
    System.Threading.Thread.Sleep(1000);
    var c = MTGOSDK.API.Trade.TradeManager.CurrentTrade;
    if (c is null) { Line("Trade closed before approval."); return false; }
    sst = c.State.ToString(); esc = c;
    if (i % 3 == 0) Line($"  t+{i,2}s state={sst}");
    if (sst.StartsWith("Approval")) approveReady = true;
  }
  if (!approveReady) { Line("Did not reach approval-ready — cancelling."); exec.CancelCurrent(); WaitForNoTrade(); return false; }

  if (!exec.GiveStatus(esc, intended).exact)
  {
    Line("GUARDRAIL re-check FAILED at approval — cancelling (gives nothing).");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    try { exec.SendDM(recipient, "Cancelled at the final check for safety."); } catch { }
    return false;
  }

  if (!allowCommit)
  {
    Line("\n[dry-run] Approval-ready + guardrail OK; no commit → cancelling (gave nothing).");
    try { exec.Cancel(esc); } catch { } WaitForNoTrade();
    return false;
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
  try { exec.SendDM(recipient, $"Done — enjoy {cardList}!"); } catch { }
  Line("\nLedger (latest):");
  var lpl = TradeBot.TradeExecutor.LedgerPath;
  if (System.IO.File.Exists(lpl)) foreach (var l in System.IO.File.ReadAllLines(lpl).Reverse().Take(1)) Line("  " + l);
  return true;
}

// SWAP: give one card, request one-or-more back. Ensures the SwapOffer binder,
// handshake, then runs the swap cycle.
static bool RunSwapFlow(TradeBot.TradeExecutor exec, string partner, string giveCard,
    System.Collections.Generic.List<string> getNames, bool allowCommit)
{
  var offer = exec.EnsureBinderExact("SwapOffer", new[] { giveCard });
  if (offer is null) { Line("Could not build the SwapOffer binder — aborting."); return false; }
  string getList = string.Join(", ", getNames);
  var esc = HandshakeThenInitiate(exec, partner,
    $"Swap offer: my 1 {giveCard} for your [{getList}]. Reply YES when ready — then accept the trade, present [{getList}], and grab the {giveCard} from my SwapOffer binder.",
    presentBinder: "SwapOffer");
  if (esc is null) { Line($"Swap not started with {partner} (no YES / not accepted)."); exec.CancelCurrent(); WaitForNoTrade(); return false; }
  return RunSwapCycle(exec, esc, partner, giveCard, 1, getNames, allowCommit);
}

// GRAB: receive one card, give nothing. Handshake, then runs the grab cycle.
static bool RunGrabFlow(TradeBot.TradeExecutor exec, string partner, string card, bool allowCommit,
    int yesTimeoutSec = 300, int qty = 1)
{
  System.Collections.Generic.List<int> cats;
  try { cats = MTGOSDK.API.Collection.CollectionManager.GetCardIds(card).ToList(); }
  catch { Line($"Unknown card '{card}'."); return false; }
  string qtyCard = qty > 1 ? $"{qty}x {card}" : card;
  var esc = HandshakeThenInitiate(exec, partner,
    $"Ready to give me {qtyCard}? Reply YES, accept the trade, and present a binder that HAS {qtyCard} (pick it before accepting). I'll grab it; take nothing from my side.",
    presentBinder: null, yesTimeoutSec: yesTimeoutSec);
  if (esc is null) { try { exec.SendDM(partner, "Couldn't open the trade — reply YES when ready and I'll retry."); } catch { } return false; }
  return RunGrabCycle(exec, esc, partner, card, cats, allowCommit, qty);
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

// Optional: run as a CHOSEN account by loading a specific env file (any filename) instead
// of the default upward .env search. Used by `serve` to pick the custodian (e.g. Sealed01):
//   serve --env=C:/Users/you/.env --commit
string? envPath = null;
{
  var e = args.FirstOrDefault(a => a.StartsWith("--env=", StringComparison.OrdinalIgnoreCase));
  if (e != null) envPath = e.Substring("--env=".Length);
}

// The ONLY modes a commit (final approve) can happen in: autofullgrab (acquire a
// free card) and lend (give a card), each requiring an explicit --commit flag.
// Every other mode stays hard-off (AllowCommit=false) even if --commit is passed.
bool allowCommit = (mode == "autofullgrab" || mode == "lend" || mode == "swap" || mode == "grabfrom" || mode == "worker" || mode == "serve") && args.Contains("--commit");

Line("=== MTGOSDK TradeBot prototype ===");
Line($"mode={mode}  allowCommit={allowCommit.ToString().ToLowerInvariant()}" +
     (allowCommit ? $"  *** WILL COMMIT (final approve) — {(mode == "lend" ? "GIVING a card away" : mode == "swap" ? "SWAPPING (both sides move)" : mode == "grabfrom" ? "RECEIVING a card (giving nothing)" : "acquiring a free card")} ***" : "  (no commit)") + "\n");

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

// Control UI: a local web panel that composes orders + spawns the `worker` process.
// This process does NOT attach to MTGO (the spawned worker does), so it runs without
// a live client.
if (mode == "ui")
{
  TradeBot.ControlUi.Run(args);
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
    try { if (envPath != null) DotEnv.LoadFrom(envPath); else DotEnv.LoadFile(); }
    catch (System.IO.FileNotFoundException) { /* no env file — fall through to env vars */ }

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
    // FIRE LogOn but do NOT await it: its await can hang even after MTGO has actually logged
    // in (observed with a fast login), wedging the process at "Logging in ...". The
    // CurrentUser poll below is the real "did we log in" gate.
    try { _ = client.LogOn(username: DotEnv.Get("USERNAME"), password: DotEnv.Get("PASSWORD")); }
    catch (Exception ex) { Line($"(LogOn dispatch threw: {ex.Message.Split('\n')[0]})"); }
  }
  // A freshly (re)started client can report no logged-in user for a while even
  // though it will settle. Poll CurrentUser rather than dereferencing it blindly
  // (a logged-out client throws "User ID must be greater than zero. Got -1.").
  string? whoami = null;
  for (int i = 0; i < 40 && whoami is null; i++)   // ~60s: room for a fire-and-forget login to land
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
      // Aggregate duplicate names into (name, count): "3x of one card" must be ONE
      // (Kozilek's Command, 3) entry, not three (…, 1) entries. The guardrail compares
      // per-name totals, so three qty-1 entries make a single "qty 2" grab misread as
      // "extra" instead of "missing 1", and it cancels a valid partial grab.
      var intended = giveCards
          .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
          .Select(g => (name: g.Key, qty: g.Count()))
          .ToList();
      string cardList = string.Join(", ", intended.Select(it => it.qty > 1 ? $"{it.qty}x {it.name}" : it.name));
      if (recipient.Length == 0) { Line("Usage: lend <recipient> [card] [--cards=\"A,B,C\"] [--commit] --yes"); break; }
      if (giveCards.Count == 0) { Line("Nothing to lend — give a card or --cards=\"...\"."); break; }
      if (!yes) { Line($"Refusing: DMs '{recipient}', opens a trade, and GIVES [{cardList}]{(allowCommit ? " (WILL COMMIT the give)" : " (dry-run: stops before commit)")}. Re-run with --yes."); break; }
      Line($"Lending [{cardList}] to {recipient}." + (allowCommit ? "  *** WILL COMMIT ***" : "  [dry-run: reaches approval-ready then cancels — gives nothing]"));

      bool keepOpen = args.Any(a => a.Equals("--keepopen", StringComparison.OrdinalIgnoreCase)
                                 || a.Equals("--listen", StringComparison.OrdinalIgnoreCase));
      exec.Attach();
      // keepOpen: wait indefinitely for their YES (single DM), no grab-window timeout, and
      // re-open the desk after any non-completed attempt. A successful commit returns true
      // and stops. Stop the task to end. Without it: original single 5-min-window attempt.
      while (true)
      {
        bool committed = RunLend(exec, recipient, intended, allowCommit, keepOpen);
        if (committed || !keepOpen) break;
        Line("\n[keepopen] attempt ended without a completed give — re-opening the desk. Reply YES to try again (stop this task to end).");
      }
      break;
    }

    case "serve":
    {
      // GIVE-SIDE INTERNAL SERVICE. A continuously-running, token-protected HTTP API in
      // front of an async job queue: POST /request enqueues a lend (custodian gives cards
      // to a user); ONE worker runs jobs sequentially; clients poll GET /jobs/{id}. Bound
      // to loopback by default. Per-job `commit` gates each give AND the process must be
      // started with --commit to enable committing at all. Moves REAL assets — keep it
      // internal (loopback / Tailscale) and protect the token.
      //   serve --env=<sealed.env> [--commit] [--port=8787] [--bind=127.0.0.1] [--token=<t>] [--timeout=600]
      if (envPath != null) { try { DotEnv.LoadFrom(envPath); } catch { } }  // so the token may live in the env

      int port = 8787;
      { var p = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
        if (p != null && int.TryParse(p.Substring("--port=".Length), out var pv)) port = pv; }
      string bind = "127.0.0.1";
      { var b = args.FirstOrDefault(a => a.StartsWith("--bind=", StringComparison.OrdinalIgnoreCase));
        if (b != null && b.Length > "--bind=".Length) bind = b.Substring("--bind=".Length); }
      int perJobTimeout = 600;
      { var t = args.FirstOrDefault(a => a.StartsWith("--timeout=", StringComparison.OrdinalIgnoreCase));
        if (t != null && int.TryParse(t.Substring("--timeout=".Length), out var tv) && tv > 0) perJobTimeout = tv; }

      // Bearer token: --token= > env TRADEBOT_API_TOKEN > generated + printed once.
      string token = "";
      { var tk = args.FirstOrDefault(a => a.StartsWith("--token=", StringComparison.OrdinalIgnoreCase));
        if (tk != null) token = tk.Substring("--token=".Length); }
      if (token.Length == 0) { try { token = DotEnv.Get("TRADEBOT_API_TOKEN"); } catch { } }
      if (token.Length == 0)
      {
        token = Guid.NewGuid().ToString("N");
        Line("[serve] no --token= / TRADEBOT_API_TOKEN set — generated a token for THIS run:");
        Line($"        {token}");
      }

      exec.Attach();

      // Auto-reconnect: RE-ATTACH a fresh client + executor after an MTGO restart (attach,
      // NOT cold-start — re-login is the user's/MTGO's job, so no MFA is needed here).
      var conn = new TradeBot.MtgoConnection(client, exec, whoami ?? "?",
        () =>
        {
          // If MTGO is fully GONE, relaunch it (cold-start) and log in from the env creds
          // (needs --env; the MFA is still the user's). If MTGO is up but only the diver
          // dropped, just re-attach. Either way we then wait for a logged-in user.
          // MTGO gone -> relaunch it (CreateProcess). If it's up but only the diver dropped,
          // just re-attach.
          bool mtgoUp = System.Diagnostics.Process.GetProcessesByName("MTGO").Length > 0;
          Client? c = null;
          try
          {
            var opts = mtgoUp
              ? new ClientOptions()
              : new ClientOptions { CreateProcess = true, AcceptEULAPrompt = true };
            if (!mtgoUp) Line("[serve] MTGO gone — relaunching + logging in from env...");
            c = new Client(opts, loggerFactory: factory);
          }
          catch { return (null, null, null); }
          // Detect login with WaitForUserLogin, which RESETS the remote cache each poll — a
          // raw CurrentUser read reports a STALE not-logged-in right after diver injection
          // (the login can be complete while CurrentUser still says -1), the same pitfall the
          // bootstrap's attach path avoids.
          bool loggedIn = false;
          try { loggedIn = c.WaitForUserLogin(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); } catch { }
          if (!loggedIn)
          {
            // Relaunched MTGO is at the login screen — drive the login from env creds. FIRE
            // LogOn (don't await; its await can hang even after MTGO has actually logged in).
            try
            {
              string un = ""; try { un = DotEnv.Get("USERNAME"); } catch { }
              if (un.Length > 0) { Line("[serve] logging MTGO back in from env..."); _ = c.LogOn(DotEnv.Get("USERNAME"), DotEnv.Get("PASSWORD")); }
            }
            catch { }
            try { loggedIn = c.WaitForUserLogin(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult(); } catch { }
          }
          if (!loggedIn) { try { c.Dispose(); } catch { } return (null, null, null); }
          string? acct = null;
          try { var u = c.CurrentUser; if (u != null && u.Id > 0) acct = u.Name; } catch { }
          if (acct is null) { try { c.Dispose(); } catch { } return (null, null, null); }
          var e = new TradeBot.TradeExecutor { AllowCommit = allowCommit };
          e.Attach();
          return (c, e, acct);
        },
        s => Line(s));

      var svc = new TradeBot.TradeService(
        token: token, bind: bind, port: port, conn: conn, perJobTimeoutSec: perJobTimeout,
        lendFn: (user, intended, jobCommit, timeoutSec) =>
        {
          // Belt + suspenders: give only if the process allows commits AND the job asks to.
          bool effectiveCommit = allowCommit && jobCommit;
          bool committed = RunLend(conn.Exec, user, intended, allowCommit: effectiveCommit,
                                   keepOpen: false, yesTimeoutSec: timeoutSec);
          string set = string.Join(", ", intended.Select(it => it.qty > 1 ? $"{it.qty}x {it.name}" : it.name));
          string detail = committed
            ? $"committed — gave {set}"
            : effectiveCommit
              ? "not completed (declined / offline / grab incomplete / guardrail)"
              : $"dry-run — nothing given (service commit {(allowCommit ? "on" : "off")}, job commit={jobCommit})";
          return (committed, detail);
        },
        grabFn: (user, card, qty, jobCommit, timeoutSec) =>
        {
          // Deposit: custodian RECEIVES qty of one card from the user, gives nothing (one-way grab).
          bool effectiveCommit = allowCommit && jobCommit;
          bool committed = RunGrabFlow(conn.Exec, user, card, allowCommit: effectiveCommit, yesTimeoutSec: timeoutSec, qty: qty);
          string set = qty > 1 ? $"{qty}x {card}" : card;
          string detail = committed
            ? $"committed — received {set}"
            : effectiveCommit
              ? "not completed (declined / offline / cards not presented / took-from-us / guardrail)"
              : $"dry-run — nothing received (service commit {(allowCommit ? "on" : "off")}, job commit={jobCommit})";
          return (committed, detail);
        },
        log: s => Line(s));

      Line($"[serve] trade service (give + deposit) as {whoami}; per-job wait {perJobTimeout}s; " +
           $"commits {(allowCommit ? "ENABLED (--commit)" : "DISABLED — dry-run only")}.");
      svc.Run();   // blocks the process on the accept loop until stopped
      break;
    }

    case "grabfrom":
    {
      // ACQUIRE a specific card FROM a user (one-way, receive-only). Handshake ->
      // initiate -> request the card from THEIR presented binder -> guardrail (WE
      // RECEIVE == exactly the card, WE GIVE nothing) -> deposit -> approve. The
      // partner must PRESENT a binder containing the card at trade START (can't
      // change it mid-trade). Cancels immediately if they take anything from us.
      // Usage: grabfrom <partner> [card] [--listen] [--commit] --yes
      string partner = arg1;
      string card = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "Azimaet Drake";
      bool listen = args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase));
      if (partner.Length == 0) { Line("Usage: grabfrom <partner> [card] [--listen] [--commit] --yes"); break; }
      if (!yes) { Line($"Refusing: DMs '{partner}' and opens a trade to RECEIVE '{card}' (I give nothing){(allowCommit ? " and WILL COMMIT" : " (dry-run: stops before commit)")}. Re-run with --yes."); break; }
      Line($"Grabbing '{card}' from {partner}." + (allowCommit ? "  *** WILL COMMIT ***" : "  [dry-run: reaches approval-ready then cancels]"));

      exec.Attach();

      System.Collections.Generic.List<int> cats;
      try { cats = MTGOSDK.API.Collection.CollectionManager.GetCardIds(card).ToList(); }
      catch { Line($"Unknown card '{card}'."); break; }

      string readyMsg = $"Ready to give me the {card}? Reply YES, accept the trade, and present a binder that HAS the {card} (pick it before accepting — you can't change it mid-trade). I'll grab it; take nothing from my side.";

      if (listen)
      {
        // PERSISTENT LISTENER: watch the chat FOREVER; each YES fires one grab cycle,
        // then back to listening. No window — reply YES whenever the card binder is set.
        if (!exec.EnsureKnownUser(partner)) { Line($"Could not resolve '{partner}' — aborting."); break; }
        try { exec.SendDM(partner, $"Grab desk is OPEN: reply YES any time you're ready to hand me the {card}. Have a binder that HAS the {card} active before you accept — I'm watching continuously; take nothing from my side."); } catch { }
        Line($"\n>>> LISTENING continuously for a YES from {partner}. Reply YES in MTGO whenever ready; stop this task to end. <<<\n");
        while (true)
        {
          if (!exec.WaitForDMYes(partner, int.MaxValue)) continue;
          Line($"\n>>> YES received from {partner} — starting a grab cycle. <<<");
          try { exec.SendDM(partner, $"On it — sending the trade now. Accept it with a binder that has the {card}, and take nothing from my side."); } catch { }
          exec.CancelCurrent(); WaitForNoTrade();
          var le = TryReachNegotiation(exec, partner, presentBinder: null, negotiateWaitSec: 90);
          if (le is null)
          {
            exec.CancelCurrent(); WaitForNoTrade();
            string why = exec.LastCloseReason ?? "";
            if (why.IndexOf("Busy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
              Line($"\n!!! Invite auto-DECLINED — {partner} is 'busy trading' ({why}). Close any open trade window / restart MTGO on that account, then reply YES. (Still listening.) !!!\n");
              try { exec.SendDM(partner, "Couldn't reach you — your client says you're already in a trade. Close any open trade window (or restart MTGO), then reply YES."); } catch { }
            }
            else
            {
              Line($"Invite not accepted (closed: {(why.Length > 0 ? why : "unknown")}) — back to listening.");
              try { exec.SendDM(partner, "Didn't see you accept the trade — reply YES again when you're ready."); } catch { }
            }
            continue;
          }
          bool ok = RunGrabCycle(exec, le, partner, card, cats, allowCommit);
          Line(ok ? "\n>>> Grab cycle COMPLETE — back to listening for the next YES. <<<\n"
                  : "\n>>> Grab cycle ended (cancelled/incomplete) — back to listening for the next YES. <<<\n");
        }
        // (unreachable — loop only exits when the task is stopped)
      }

      // ONE-SHOT: standardized handshake (DM -> wait up to 5 min for YES) then one cycle.
      var esc = HandshakeThenInitiate(exec, partner, readyMsg, presentBinder: null);
      if (esc is null) { try { exec.SendDM(partner, "Couldn't open the trade — reply YES when ready and I'll retry."); } catch { } break; }
      RunGrabCycle(exec, esc, partner, card, cats, allowCommit);
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

    case "gamelog":
    {
      // READ-ONLY spectate capture: dump the action log + state of any game the
      // client is currently WATCHING (or replaying). Game.LogChannel is "the log
      // channel for all game actions" -- the full play-by-play. This does NOT start
      // watching; watch a game manually in MTGO first (Play -> a room like
      // "Free For all Best of 3" -> double-click a match to WATCH it), then run this.
      // Usage: gamelog [--stream]   (--stream tails new log lines live)
      bool stream = args.Any(a => a.Equals("--stream", StringComparison.OrdinalIgnoreCase));
      var games = FindGames();
      if (games.Count == 0)
      {
        Line("No games found on the client.");
        Line("In MTGO: Play -> a room (e.g. Free For All Best of 3) -> double-click a match to WATCH it, then re-run `gamelog`.");
        break;
      }
      bool state = args.Any(a => a.Equals("--state", StringComparison.OrdinalIgnoreCase));
      Line($"Found {games.Count} game(s) on the client.");
      foreach (var g in games)
      {
        DumpGameLog(g, 60);
        if (state) { Line("  -- board state --"); DumpGameState(g); }
      }
      if (stream) StreamGameLog(games[0]);
      break;
    }

    case "spectate":
    {
      // AUTO-SPECTATE: drive the client to WATCH a live game itself, then dump its
      // action log. `target` is a game id (from `gamelog`) or a player-name substring.
      // Read-only observation (registers you as a watcher; no game/trade state change).
      // Usage: spectate <gameId|playerName> [--stream]   OR   spectate --any [--stream]
      bool any = args.Any(a => a.Equals("--any", StringComparison.OrdinalIgnoreCase));
      bool twoOnly = args.Any(a => a.Equals("--2p", StringComparison.OrdinalIgnoreCase));   // only 2-player games (duels)
      string? target = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
      bool stream = args.Any(a => a.Equals("--stream", StringComparison.OrdinalIgnoreCase));

      // Build the candidate list: an explicit id/player, or (with --any) every live
      // Started game the client can see — we try each until one accepts a watcher
      // (tournament matches often disallow spectators, so the first isn't always it).
      // With --2p, restrict to 2-player games (skip 4-player FFA/Commander).
      var candidates = new System.Collections.Generic.List<string>();
      if (!string.IsNullOrWhiteSpace(target)) candidates.Add(target);
      else if (any)
      {
        foreach (var gm in FindGames())
          try
          {
            if (gm.Status.ToString() != "Started") continue;
            if (twoOnly) { int pc = 0; try { pc = gm.Players.Count; } catch { } if (pc != 2) continue; }
            candidates.Add(gm.Id.ToString());
          } catch { }
        if (candidates.Count == 0)
        {
          Line("No in-progress (Started) games are loaded on the client.");
          Line("Open the Play lobby / a room's Watch (Games) list in MTGO so live matches appear, then retry `spectate --any`.");
          break;
        }
        Line($"{candidates.Count} live game(s) to try (some competitive matches disallow watchers)...");
      }
      else
      {
        Line("Usage: spectate <gameId|playerName> [--stream] [--state]   OR   spectate --any [--stream] [--state]");
        Line("Tip: run `gamelog` first to list live game ids + players, then `spectate <id>`.");
        break;
      }

      bool ok = false; int gid = -1; string detail = "";
      foreach (var cand in candidates)
      {
        Line($"Attempting to watch '{cand}'...");
        (ok, gid, detail) = exec.WatchGame(cand);
        Line($"  -> ok={ok} game={gid} — {detail}");
        if (ok) break;
      }
      if (!ok)
      {
        Line("Could not start watching any candidate. Open a CASUAL room (e.g. Free For All");
        Line("Best of 3) so watchable games load — competitive/tournament matches block spectators.");
        break;
      }

      System.Threading.Thread.Sleep(2000);   // let the first log entries stream in
      var watched = FindGames().FirstOrDefault(x => { try { return x.Id == gid; } catch { return false; } });
      if (watched is null) { Line($"(game {gid} not found after watch — re-run `gamelog`)"); break; }
      DumpGameLog(watched, 60);

      // --record: stream a COMPLETE, replayable NDJSON record of the whole game
      // (header + full-board state per tick + interleaved log + final result) until
      // the game finishes or --minutes elapses. Usage: spectate --any --record [--out=<path>] [--minutes=N]
      if (args.Any(a => a.Equals("--record", StringComparison.OrdinalIgnoreCase)))
      {
        int minutes = 90;
        var mflag = args.FirstOrDefault(a => a.StartsWith("--minutes=", StringComparison.OrdinalIgnoreCase));
        if (mflag != null && int.TryParse(mflag.Substring("--minutes=".Length), out var mm)) minutes = mm;
        string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))?.Substring("--out=".Length)
                         ?? System.IO.Path.Combine(TradeBot.GameRecorder.DefaultDir, $"game-{gid}-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");
        Line($"\nRecording game {gid} -> {outPath}  (until finished or {minutes} min; Ctrl+C to stop)...");
        TradeBot.GameRecorder.Run(watched, outPath, minutes);
        break;
      }

      if (args.Any(a => a.Equals("--state", StringComparison.OrdinalIgnoreCase)))
      {
        Line("\n-- board state (snapshot) --");
        var s = GetSnapshot(watched);
        if (s != null) DumpGameStateSnap(s);
        else Line("  (no snapshot arrived — the game may be idle; retry, or add --stream to catch activity)");
      }
      if (stream) StreamGameLog(watched);
      break;
    }

    case "autoreport":
    {
      // Auto-report freeform Bo3 draft-match results to DraftBot. Read-only observation
      // -> report who won. Two forms:
      //   autoreport --pair=<a>,<b> [--session=<id>] [--dry-run] [--max-min=N]   one-shot test
      //   autoreport --daemon [--poll=N] [--dry-run]                            worker loop
      // Reporting requires DRAFTBOT_API_BASE + DRAFTBOT_API_TOKEN (except --dry-run, which
      // observes + computes the result but does not POST).
      string? FlagVal(string name)
      {
        var eq = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (eq != null) return eq.Substring(name.Length + 1);
        int idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")) return args[idx + 1];
        return null;
      }

      bool dry = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
      bool daemon = args.Any(a => a.Equals("--daemon", StringComparison.OrdinalIgnoreCase));

      if (!dry && !TradeBot.MatchReporter.IsConfigured)
        Line("(note: DRAFTBOT_API_BASE/DRAFTBOT_API_TOKEN not set — reporting will no-op; add --dry-run to just observe)");

      if (daemon)
      {
        int poll = 60;
        var pv = FlagVal("--poll");
        if (pv != null && int.TryParse(pv, out var pp)) poll = Math.Max(10, pp);
        TradeBot.MatchWatcher.RunDaemon(exec, poll, dry);   // runs until Ctrl+C
        break;
      }

      string? pair = FlagVal("--pair");
      if (pair is null)
      {
        Line("Usage: autoreport --pair=<a>,<b> [--session=<id>] [--dry-run] [--max-min=N]");
        Line("       autoreport --daemon [--poll=N] [--dry-run]");
        break;
      }
      var parts = pair.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (parts.Length != 2)
      {
        Line("--pair needs two comma-separated MTGO usernames, e.g. --pair=alice,bob");
        break;
      }
      int maxMin = 90;
      var mm = FlagVal("--max-min");
      if (mm != null && int.TryParse(mm, out var mmv)) maxMin = Math.Max(1, mmv);
      // WatchAndReportPair already prints its own summary line (so does the daemon path);
      // don't re-print the returned string here.
      TradeBot.MatchWatcher.WatchAndReportPair(exec, parts[0], parts[1], FlagVal("--session"), maxMin, dry);
      break;
    }

    case "worker":
    {
      // Drain a FOLDER of trade-order JSON files, executing each against MTGO one at
      // a time, writing a sibling <name>.status.json (the input file is never
      // touched). Manual trigger now (drop order files, run this); later a remote
      // producer writes the SAME JSON and nothing here changes. An order commits only
      // if BOTH its own commit:true AND the worker is launched with --commit.
      // Usage: worker [--dir=<path>] [--commit] --yes    (default dir: %USERPROFILE%\mtgosdk-tradebot\orders)
      string dir = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))?.Substring("--dir=".Length)
                   ?? TradeBot.OrderQueue.DefaultDir;
      System.IO.Directory.CreateDirectory(dir);
      Line($"Worker: order folder = {dir}");
      var pending = TradeBot.OrderQueue.Pending(dir);
      if (pending.Count == 0) { Line("No pending orders (a *.json with no sibling *.status.json). Drop one in and re-run."); break; }
      if (!yes)
      {
        Line($"Refusing: {pending.Count} pending order(s){(allowCommit ? " — would COMMIT those marked commit:true" : " — dry-run, nothing commits")}. Re-run with --yes:");
        foreach (var o in pending)
          Line($"  {o.Id}: partner={o.MtgoPartner}  GIVE=[{string.Join(", ", o.Give.Select(i => i.ToString()))}]  RECEIVE=[{string.Join(", ", o.Receive.Select(i => i.ToString()))}]  commit={o.Commit}");
        break;
      }

      exec.Attach();

      foreach (var order in pending)
      {
        string gs = string.Join(", ", order.Give.Select(i => i.ToString()));
        string rs = string.Join(", ", order.Receive.Select(i => i.ToString()));
        Line($"\n===== ORDER {order.Id}  partner={order.MtgoPartner}  GIVE=[{gs}]  RECEIVE=[{rs}]  commit={order.Commit} =====");
        TradeBot.OrderQueue.WriteStatus(order.SourcePath, new TradeBot.OrderStatus { Id = order.Id, Status = "running", Detail = "executing" });

        bool perOrderCommit = order.Commit && allowCommit;
        var status = new TradeBot.OrderStatus { Id = order.Id, Gave = order.Give.Select(i => i.ToString()).ToList(), Received = order.Receive.Select(i => i.ToString()).ToList() };
        try
        {
          if (string.IsNullOrWhiteSpace(order.MtgoPartner)) { status.Status = "failed"; status.Detail = "order has no mtgoPartner"; }
          else
          {
            var giveItems = order.Give.Select(i => (name: i.Name, qty: System.Math.Max(1, i.Qty))).ToList();
            bool giveOnly = order.Give.Count > 0 && order.Receive.Count == 0;
            bool recvOnly = order.Give.Count == 0 && order.Receive.Count > 0;
            bool both     = order.Give.Count > 0 && order.Receive.Count > 0;
            bool? ok = null;

            if (giveOnly) { Line("→ LEND (give-only)"); ok = RunLend(exec, order.MtgoPartner, giveItems, perOrderCommit); }
            else if (recvOnly && order.Receive.Count == 1 && order.Receive[0].Qty == 1) { Line("→ GRAB (receive-only, single card)"); ok = RunGrabFlow(exec, order.MtgoPartner, order.Receive[0].Name, perOrderCommit); }
            else if (both && order.Give.Count == 1 && order.Give[0].Qty == 1) { Line("→ SWAP (one give card, request the rest)"); ok = RunSwapFlow(exec, order.MtgoPartner, order.Give[0].Name, order.Receive.Select(i => i.Name).ToList(), perOrderCommit); }
            else { status.Status = "unsupported"; status.Detail = "unsupported give/receive combo (v1: give-only lend, single receive-only grab, or single-give+receive swap; qty>1 receive / multi-give not yet)."; }

            if (ok.HasValue)
            {
              status.Status = ok.Value ? (perOrderCommit ? "completed" : "completed_dryrun") : "cancelled";
              status.Detail = ok.Value ? (perOrderCommit ? "trade committed" : "reached ready + guardrail OK; dry-run (no commit)") : "cancelled / incomplete — see worker log";
            }
          }
        }
        catch (Exception ex) { status.Status = "failed"; status.Detail = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}"; }

        TradeBot.OrderQueue.WriteStatus(order.SourcePath, status);
        Line($"===== ORDER {order.Id} → {status.Status} ({status.Detail}) =====");

        // Make sure nothing is left open before the next order.
        exec.CancelCurrent(); WaitForNoTrade();
      }
      Line($"\nWorker done — processed {pending.Count} order(s).");
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
      Line("  grabfrom  : grabfrom <partner> [card] [--listen] --yes  (receive a card, give nothing; add --commit)");
      Line("  worker    : worker [--dir=<path>] --yes                 (drain a folder of trade-order JSONs; add --commit for orders marked commit:true)");
      Line("  ui        : ui [--dir=<path>] [--port=5577]             (local web control panel: compose orders, run the worker, watch status)");
      Line("  low-level : opentrade <bot> --yes | takecard <card> --yes | grab <bot> --yes | invite <user> --yes");
      Line("  test      : stagetest <bot> [card] --yes  (controlled TakeCard test: stage -> observe -> cancel)");
      Line("  outward   : post \"<msg>\" --yes | clearpost --yes");
      Line("  observe   : gamelog [--stream] | spectate <id|player>|--any [--2p] [--record] | autoreport --pair=a,b|--daemon [--dry-run]");
      break;
  }

  Line(allowCommit
    ? "\nDone. (AllowCommit was TRUE for this run — a final approve was authorized.)"
    : "\nDone. (AllowCommit was false the entire run — no trade was committed.)");
}
