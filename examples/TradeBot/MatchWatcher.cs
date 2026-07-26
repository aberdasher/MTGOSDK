/** @file
  MTGO-side driver that auto-reports draft match results to DraftBot.

  Ties together three pieces already proven elsewhere in this project:
    TradeExecutor.WatchMatchForPair  find + spectate a live match by player-pair
    poll Match.IsComplete            follow the Bo3 to the end
    MatchReporter                    compute the (winner, loser, score) + POST

  Two entry points:
    WatchAndReportPair(...)  one-shot: watch a specific pair NOW, block until the
                             match ends (or a timeout), then report. For manual
                             testing (`autoreport --pair alice,bob`), no DraftBot
                             discovery needed.
    RunDaemon(...)           the worker loop: poll DraftBot /pairings/active and,
                             for every pairing that's live, ensure we're watching it
                             and report it the moment it completes. Non-blocking /
                             round-robin — a long match never starves the others,
                             because we only CHECK completion each poll (the client
                             keeps receiving the match once we're a watcher).

  Both are inert unless DRAFTBOT_API_BASE + DRAFTBOT_API_TOKEN are set (except
  --dry-run, which computes the outcome and logs it without POSTing).
**/
using System;
using System.Collections.Generic;
using System.Threading;

using MTGOSDK.API.Play;

namespace TradeBot;

public static class MatchWatcher
{
  static T Safe<T>(Func<T> f, T dflt) { try { return f(); } catch { return dflt; } }

  /// <summary>
  /// Watch a live match between <paramref name="a"/> and <paramref name="b"/>, block until
  /// it completes (up to <paramref name="maxMinutes"/>), then report. With
  /// <paramref name="dryRun"/> the outcome is computed and logged but NOT POSTed.
  /// Returns a one-line summary.
  /// </summary>
  public static string WatchAndReportPair(TradeExecutor exec, string a, string b,
                                          string? sessionId, int maxMinutes = 90, bool dryRun = false)
  {
    var (ok, gid, match, detail) = exec.WatchMatchForPair(a, b, 30);
    Console.WriteLine($"[autoreport] watch {a} vs {b}: {detail}");
    if (match is null) return $"[autoreport] {a} vs {b}: {detail}";

    // Block-follow this one match.
    DateTime deadline = DateTime.UtcNow.AddMinutes(maxMinutes);
    while (DateTime.UtcNow < deadline && !Safe(() => match.IsComplete, false))
      Thread.Sleep(3000);

    var outcome = ToOutcome(match, out string why);
    if (outcome is null) { var m = $"[autoreport] {a} vs {b}: {why}"; Console.WriteLine(m); return m; }
    outcome = outcome with { SessionId = sessionId ?? outcome.SessionId };
    return Emit(outcome, dryRun);
  }

  /// <summary>
  /// The worker loop. Every <paramref name="pollSeconds"/>: fetch pending pairings from
  /// DraftBot; for each pairing that's live, make sure we're watching it and report it if
  /// it has completed. Runs until Ctrl-C.
  /// </summary>
  public static void RunDaemon(TradeExecutor exec, int pollSeconds = 60, bool dryRun = false)
  {
    Console.WriteLine($"[autoreport] daemon start: poll={pollSeconds}s dryRun={dryRun} "
                    + $"api={(MatchReporter.IsConfigured ? "configured" : "NOT configured")}");
    var reported = new HashSet<string>();
    while (true)
    {
      var (ok, pairings, detail) = MatchReporter.FetchPairings();
      if (!ok)
        Console.WriteLine($"[autoreport] fetch pairings failed: {detail}");
      else
      {
        Console.WriteLine($"[autoreport] {detail} pending");
        foreach (var p in pairings)
        {
          // Fast when the pair isn't live (enumeration only); when live, connects as a
          // watcher, returning immediately on subsequent polls ("already watching").
          var (wok, gid, match, wdetail) = exec.WatchMatchForPair(p.PlayerA, p.PlayerB, 12);
          if (match is null) continue; // not live yet — re-check next poll

          string key = Safe(() => match.Id.ToString(), $"{p.PlayerA}|{p.PlayerB}|{p.SessionId}");
          if (reported.Contains(key)) continue;

          if (!Safe(() => match.IsComplete, false))
          {
            // Still in progress; we're now watching it and will catch the end next poll.
            Console.WriteLine($"[autoreport] watching {p.PlayerA} vs {p.PlayerB} (game {gid}) — in progress");
            continue;
          }

          var outcome = ToOutcome(match, out string why);
          if (outcome is null) { Console.WriteLine($"[autoreport] {p.PlayerA} vs {p.PlayerB}: {why}"); continue; }
          outcome = outcome with { SessionId = p.SessionId ?? outcome.SessionId };
          string line = Emit(outcome, dryRun);
          // Mark done on success OR a 409 (already recorded server-side) so we stop retrying.
          if (dryRun || line.Contains("recorded") || line.Contains("HTTP 409")) reported.Add(key);
        }
      }
      Thread.Sleep(pollSeconds * 1000);
    }
  }

  // Turn a (hopefully complete) match into an outcome, or explain why not.
  static MatchOutcome? ToOutcome(Match match, out string why)
  {
    if (!Safe(() => match.IsComplete, false)) { why = "match did not complete in time"; return null; }
    if (!MatchReporter.TryFromMatch(match, out var outcome, out string d)) { why = d; return null; }
    why = "ok";
    return outcome;
  }

  // Report (or, in dry-run, just log) and return a one-line summary.
  static string Emit(MatchOutcome outcome, bool dryRun)
  {
    if (dryRun) { var s = $"[autoreport] (dry-run) would report: {outcome}"; Console.WriteLine(s); return s; }
    var (rok, status, rdetail) = MatchReporter.Report(outcome);
    var line = $"[autoreport] {outcome} -> HTTP {status} {(rok ? "recorded" : "not recorded")}: {rdetail}";
    Console.WriteLine(line);
    return line;
  }
}
