/** @file
  Structured, replayable game recorder for spectated games.

  While watching a game, this streams a COMPLETE, timestamped record to an NDJSON
  file (one JSON object per line) that a viewer can reconstruct later:

    {"type":"header", gameId, players:[{index,name}], startedAt}
    {"type":"state",  timestamp, turn, phase, prompt, players:[...], cards:[...]}   (one per game tick — a FULL board, not a delta)
    {"type":"log",    at, user, text}                                               (the play-by-play, interleaved)
    {"type":"result", finalStatus, winners, at}                                     (when the game ends)

  All card/player fields are read from the SDK's GameStateSnapshot partials, so they
  resolve locally (TypeLine/P/T/zone/counters) with no per-card IPC. Read-only.
**/

using System.Text.Json;
using System.Text.Json.Serialization;

using GS = MTGOSDK.API.Play.Games;
using GSProc = MTGOSDK.API.Play.Games.Processors;
using GSArgs = MTGOSDK.API.Play.Games.Processors.EventArgs;

namespace TradeBot;

public static class GameRecorder
{
  static readonly JsonSerializerOptions J = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false, // NDJSON: one object per line
  };

  // Small guarded readers — every field is a remote/partial read that may throw.
  static string S(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
  static int I(Func<int> f) { try { return f(); } catch { return 0; } }
  static bool B(Func<bool> f) { try { return f(); } catch { return false; } }

  /// <summary>Default output dir: %USERPROFILE%\mtgosdk-tradebot\games</summary>
  public static string DefaultDir =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mtgosdk-tradebot", "games");

  /// <summary>
  /// Record a watched game to <paramref name="outPath"/> until it finishes or
  /// <paramref name="maxMinutes"/> elapses. Returns the number of state ticks written.
  /// </summary>
  public static int Run(GS.Game game, string outPath, int maxMinutes = 90)
  {
    int gid = -1; try { gid = game.Id; } catch { }
    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

    using var writer = new StreamWriter(outPath, append: false) { AutoFlush = true };
    var wlock = new object();
    void Write(object rec) { lock (wlock) { try { writer.WriteLine(JsonSerializer.Serialize(rec, J)); } catch { } } }

    // Header
    var names = ReadNames(game);
    Write(new { type = "header", gameId = gid, players = names.Select(kv => new { index = kv.Key, name = kv.Value }), startedAt = NowIso() });

    // State capture (dedup by server tick Timestamp — one record per distinct tick).
    var seenTicks = new HashSet<uint>();
    int stateCount = 0, logCount = 0;
    void TryState(GSProc.GameStateSnapshot? snap)
    {
      if (snap is null) return;
      lock (wlock) { if (!seenTicks.Add(snap.Timestamp)) return; }
      Write(BuildState(snap));
      System.Threading.Interlocked.Increment(ref stateCount);
    }

    Action<GSArgs.PromptChangedEventArgs> onPrompt = a => TryState(SafeSnap(() => a.Snapshot));
    Action<GSArgs.CardChangedEventArgs>   onCard   = a => TryState(SafeSnap(() => a.Snapshot));
    Action<GSArgs.PlayerChangedEventArgs> onPlayer = a => TryState(SafeSnap(() => a.Snapshot));

    try
    {
      game.OnPromptChanged += onPrompt;   // first subscribe activates the processor
      game.OnCardChanged   += onCard;
      game.OnPlayerChanged += onPlayer;
      game.ReadyProcessor();              // MANDATORY: drain loop is inert until this
    }
    catch (Exception ex) { Console.WriteLine($"[record] subscribe failed: {ex.Message.Split('\n')[0]}"); }

    // Main loop: append new log lines + watch for game end. State records are
    // written from the event handlers above (on the drain thread).
    int logSeen = 0;
    var end = DateTime.Now.AddMinutes(maxMinutes);
    bool finished = false;
    int lastReport = 0;
    while (DateTime.Now < end)
    {
      // Drain new log lines.
      try
      {
        var msgs = game.LogChannel.Messages;
        for (int i = logSeen; i < msgs.Count; i++)
        {
          string who = "", txt = "", ts = "";
          try { who = msgs[i].User?.Name ?? ""; } catch { }
          try { txt = (msgs[i].Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); } catch { }
          try { ts = msgs[i].Timestamp.ToString("HH:mm:ss"); } catch { }
          Write(new { type = "log", at = ts, user = who, text = txt });
          logCount++;
        }
        logSeen = msgs.Count;
      }
      catch { }

      // End detection.
      string status = S(() => game.Status.ToString());
      if (status == "Finished") { finished = true; break; }

      if (stateCount + logCount - lastReport >= 20)
      { lastReport = stateCount + logCount; Console.WriteLine($"[record] {stateCount} states, {logCount} log lines so far..."); }

      System.Threading.Thread.Sleep(1000);
    }

    if (finished) Write(BuildResult(game));

    try { game.ClearEvents(); } catch { }
    Console.WriteLine($"[record] done: {stateCount} states, {logCount} log lines{(finished ? " (game finished)" : " (window elapsed)")} -> {outPath}");
    return stateCount;
  }

  // ── record builders ──────────────────────────────────────────────────────

  static object BuildState(GSProc.GameStateSnapshot snap)
  {
    var names = new Dictionary<int, string>();
    foreach (var kv in snap.Players)
    { var nm = S(() => kv.Value.Name); names[kv.Key] = string.IsNullOrWhiteSpace(nm) ? $"P{kv.Key}" : nm; }

    var players = new List<object>();
    foreach (var idx in snap.Players.Keys.OrderBy(k => k))
    {
      var p = snap.Players[idx];
      var counters = new Dictionary<string, int>();
      try { foreach (var kv in p.Counters) counters[kv.Key] = kv.Value; } catch { }
      var mana = new Dictionary<string, int>();
      try { foreach (var m in p.ManaPool) { var sym = S(() => m.Symbol); int amt = I(() => m.Amount); if (amt > 0 && sym.Length > 0) mana[sym] = mana.GetValueOrDefault(sym) + amt; } } catch { }
      int clock = 0; try { clock = (int)p.ChessClock.TotalSeconds; } catch { }
      players.Add(new
      {
        index = idx,
        name = names[idx],
        life = I(() => p.Life),
        hand = I(() => p.HandCount),
        library = I(() => p.LibraryCount),
        graveyard = I(() => p.GraveyardCount),
        clockSeconds = clock,
        counters = counters.Count > 0 ? counters : null,
        mana = mana.Count > 0 ? mana : null,
      });
    }

    var cards = new List<object>();
    foreach (var kv in snap.Cards) cards.Add(CardRec(kv.Value, names));

    return new
    {
      type = "state",
      timestamp = snap.Timestamp,
      turn = snap.TurnNumber,
      phase = snap.CurrentPhase.ToString(),
      promptedPlayer = snap.PromptedPlayer == byte.MaxValue ? (int?)null : snap.PromptedPlayer,
      prompt = S(() => snap.PromptText),
      players,
      cards,
    };
  }

  static object CardRec(GS.GameCard c, Dictionary<int, string> names)
  {
    string type = S(() => c.TypeLine).Trim();
    bool creature = type.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0;
    int ctrl = I(() => c.ControllerIndex), own = I(() => c.OwnerIndex);
    int loy = I(() => c.Loyalty), dmg = I(() => c.Damage), chap = I(() => c.CurrentChapter), lvl = I(() => c.CurrentLevel), att = I(() => c.AttachedToId);

    var counters = new Dictionary<string, int>();
    try { foreach (var cc in c.Counters) { var k = cc.ToString() ?? "?"; counters[k] = counters.GetValueOrDefault(k) + 1; } } catch { }

    return new
    {
      id = I(() => c.Id),
      name = S(() => c.Name),
      type,
      power = creature ? (int?)I(() => c.Power) : null,
      toughness = creature ? (int?)I(() => c.Toughness) : null,
      zone = S(() => c.Zone?.Name),
      controller = ctrl >= 0 ? (int?)ctrl : null,
      controllerName = ctrl >= 0 && names.TryGetValue(ctrl, out var cn) ? cn : null,
      owner = own >= 0 ? (int?)own : null,
      tapped = B(() => c.IsTapped) ? true : (bool?)null,
      attacking = B(() => c.IsAttacking) ? true : (bool?)null,
      blocking = B(() => c.IsBlocking) ? true : (bool?)null,
      blocked = B(() => c.IsBlocked) ? true : (bool?)null,
      // summoning sickness is only meaningful for creatures (MTGO sets the raw
      // flag on anything freshly entered, incl. lands/artifacts).
      summoningSick = (creature && B(() => c.HasSummoningSickness)) ? true : (bool?)null,
      token = B(() => c.IsToken) ? true : (bool?)null,
      faceDown = B(() => c.IsFaceDown) ? true : (bool?)null,
      phased = B(() => c.IsPhasedOut) ? true : (bool?)null,
      loyalty = loy > 0 ? (int?)loy : null,
      damage = dmg > 0 ? (int?)dmg : null,
      chapter = chap > 0 ? (int?)chap : null,
      level = lvl > 0 ? (int?)lvl : null,
      attachedTo = att > 0 ? (int?)att : null,
      counters = counters.Count > 0 ? counters : null,
    };
  }

  static object BuildResult(GS.Game game)
  {
    var winners = new List<string>();
    try { foreach (var p in game.WinningPlayers) { try { winners.Add(p.Name); } catch { } } } catch { }
    return new { type = "result", finalStatus = S(() => game.Status.ToString()), winners, at = NowIso() };
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  static Dictionary<int, string> ReadNames(GS.Game game)
  {
    var d = new Dictionary<int, string>();
    try
    {
      int i = 0;
      foreach (var p in game.Players) { var nm = S(() => p.Name); d[i] = string.IsNullOrWhiteSpace(nm) ? $"P{i}" : nm; i++; }
    }
    catch { }
    return d;
  }

  static GSProc.GameStateSnapshot? SafeSnap(Func<GSProc.GameStateSnapshot> f) { try { return f(); } catch { return null; } }

  static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
