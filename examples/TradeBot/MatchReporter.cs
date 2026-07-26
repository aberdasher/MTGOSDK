/** @file
  MTGO -> DraftBot match-result reporter.

  The worker OBSERVES a finished draft match and POSTs who won to DraftBot's
  /matches/report endpoint (services/mtgo_result_api.py). DraftBot maps the MTGO
  usernames to the pending pairing and records the result; a username it doesn't
  know, or a match it has no pending pairing for, is simply ignored server-side
  (fire-and-forget). Read-only observation -> report an outcome; nothing of value
  moves, so a wrong result is the worst case and an admin can correct it.

  This file is the *reporting engine* and is deliberate about one thing: it is
  independent of HOW the result was observed. Both viable sources collapse to the
  same (winner, loser, game-score) tuple:

    * a spectated live Match      -> TryFromMatch(Match)            [challenge matches]
    * a tournament standings row  -> TryFromStanding(MatchStandingRecord) [league/tournament]

  The driver that decides what to watch / which standings to read lives elsewhere;
  this just turns an observed result into an HTTP POST.

  Configuration (env; the reporter is INERT until both are set, mirroring the
  DraftBot side's MTGO_API_TOKEN gate):
    DRAFTBOT_API_BASE    e.g. http://100.x.y.z:8787   (the droplet over Tailscale)
    DRAFTBOT_API_TOKEN   shared bearer token (== MTGO_API_TOKEN on DraftBot)
**/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Tournaments;

namespace TradeBot;

/// <summary>
/// A fully-decided match result, oriented as (winner, loser) with the game score
/// from the winner's perspective (e.g. 2 and 0 for a 2-0). This is exactly what
/// DraftBot's /matches/report expects.
/// </summary>
public sealed record MatchOutcome(
  string PlayerA,
  string PlayerB,
  string Winner,
  int GamesWinner,
  int GamesLoser,
  string? SessionId = null,
  string? MtgoMatchId = null)
{
  public override string ToString() =>
    $"{Winner} beat {(Winner == PlayerA ? PlayerB : PlayerA)} {GamesWinner}-{GamesLoser}"
    + (MtgoMatchId is null ? "" : $" (mtgo match {MtgoMatchId})");
}

/// <summary>One pending pairing DraftBot wants a result for, as MTGO usernames.</summary>
public sealed record PairingInfo(string PlayerA, string PlayerB, string? SessionId, int MatchNumber);

public static class MatchReporter
{
  // One HttpClient for the process (avoids socket exhaustion). Short timeout —
  // the endpoint does real work (the Discord cascade) but should answer quickly;
  // a slow answer shouldn't wedge the worker.
  static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

  static readonly JsonSerializerOptions _json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
  };

  /// <summary>True when both env vars are set — otherwise reporting is a no-op.</summary>
  public static bool IsConfigured =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DRAFTBOT_API_BASE")) &&
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DRAFTBOT_API_TOKEN"));

  static T Safe<T>(Func<T> f, T dflt) { try { return f(); } catch { return dflt; } }

  // ---- Source adapters -----------------------------------------------------

  /// <summary>
  /// Build an outcome from a COMPLETED live <see cref="Match"/> (the spectate path).
  /// Returns false with a reason if the match isn't complete or isn't a clean
  /// 1v1 with a single winner/loser.
  /// </summary>
  public static bool TryFromMatch(Match match, out MatchOutcome? outcome, out string detail)
  {
    outcome = null;
    if (match is null) { detail = "null match"; return false; }
    if (!Safe(() => match.IsComplete, false)) { detail = "match not complete"; return false; }

    var winners = Safe(() => match.WinningPlayers?.Select(u => u.Name).Where(n => !string.IsNullOrEmpty(n)).ToList(),
                       new List<string>()) ?? new List<string>();
    var losers  = Safe(() => match.LosingPlayers?.Select(u => u.Name).Where(n => !string.IsNullOrEmpty(n)).ToList(),
                       new List<string>()) ?? new List<string>();
    if (winners.Count != 1 || losers.Count != 1)
    {
      detail = $"not a clean 1v1 result (winners={winners.Count}, losers={losers.Count})";
      return false;
    }
    string winner = winners[0], loser = losers[0];

    // Score: count decided games by their winner's name. A completed match has all
    // games decided; the current/undecided game (if any) simply has no winner.
    int gw = 0, gl = 0;
    foreach (var g in Safe(() => match.Games?.ToList(), new List<Game>()) ?? new List<Game>())
    {
      var gwin = Safe(() => g.WinningPlayers?.Select(p => p.Name).FirstOrDefault(), (string?)null);
      if (gwin == winner) gw++;
      else if (gwin == loser) gl++;
    }
    (gw, gl) = NormalizeScore(gw, gl, Safe(() => match.MaxGames, 1));

    outcome = new MatchOutcome(winner, loser, winner, gw, gl,
                               MtgoMatchId: Safe(() => match.Id.ToString(), (string?)null));
    detail = "ok";
    return true;
  }

  /// <summary>
  /// Build an outcome from a tournament/league <see cref="MatchStandingRecord"/>
  /// (the no-spectate path — works even when watchers are disallowed). Byes and
  /// not-yet-finished rows return false.
  /// </summary>
  public static bool TryFromStanding(MatchStandingRecord rec, out MatchOutcome? outcome, out string detail)
  {
    outcome = null;
    if (rec is null) { detail = "null record"; return false; }
    if (Safe(() => rec.HasBye, false)) { detail = "bye"; return false; }
    // Only report finished matches.
    var state = Safe(() => rec.State, MatchState.Invalid);
    bool finished = state.HasFlag(MatchState.MatchCompleted) || Safe(() => (rec.WinningPlayerIds?.Count ?? 0) > 0, false);
    if (!finished) { detail = $"not finished (state {state})"; return false; }

    var players = Safe(() => rec.Players?.ToList(), new List<MTGOSDK.API.Users.User>())
                  ?? new List<MTGOSDK.API.Users.User>();
    var winIds  = Safe(() => rec.WinningPlayerIds?.ToList(), new List<int>()) ?? new List<int>();
    var loseIds = Safe(() => rec.LosingPlayerIds?.ToList(), new List<int>()) ?? new List<int>();
    if (players.Count != 2 || winIds.Count != 1 || loseIds.Count != 1)
    {
      detail = $"not a clean 1v1 result (players={players.Count}, winners={winIds.Count}, losers={loseIds.Count})";
      return false;
    }

    string NameFor(int id) => Safe(() => players.First(p => p.Id == id).Name, null) ?? "";
    string winner = NameFor(winIds[0]), loser = NameFor(loseIds[0]);
    if (string.IsNullOrEmpty(winner) || string.IsNullOrEmpty(loser))
    { detail = "could not resolve player names from ids"; return false; }

    // Score: count games whose sole winner id is the match winner / loser.
    int gw = 0, gl = 0;
    foreach (var gsr in Safe(() => rec.GameStandingRecords?.ToList(), new List<GameStandingRecord>())
                        ?? new List<GameStandingRecord>())
    {
      if (gsr is null) continue;
      var ids = Safe(() => gsr.WinnerIds?.ToList(), new List<int>()) ?? new List<int>();
      if (ids.Count != 1) continue;
      if (ids[0] == winIds[0]) gw++;
      else if (ids[0] == loseIds[0]) gl++;
    }
    (gw, gl) = NormalizeScore(gw, gl, 3); // tournament matches are Bo3

    outcome = new MatchOutcome(winner, loser, winner, gw, gl,
                               MtgoMatchId: Safe(() => rec.Id.ToString(), null));
    detail = "ok";
    return true;
  }

  // If we somehow couldn't read individual game winners, fall back to the minimum
  // score consistent with a completed match (Bo3 -> 2-0, Bo1 -> 1-0) rather than
  // reporting 0-0. The winner by definition reached the needed game count.
  static (int gw, int gl) NormalizeScore(int gw, int gl, int maxGames)
  {
    int needed = maxGames >= 3 ? 2 : 1;
    if (gw < needed) gw = needed;
    if (gl >= gw) gl = gw - 1; // loser can't have >= winner's wins in a decided match
    if (gl < 0) gl = 0;
    return (gw, gl);
  }

  // ---- Transport -----------------------------------------------------------

  /// <summary>
  /// POST an outcome to DraftBot. Returns (ok, httpStatus, detail). ok is true only
  /// on HTTP 200 (recorded). 409 = no pending pairing (already reported / not a draft
  /// match); 422 = unlinked username or bad winner; 0 = transport/config failure.
  /// </summary>
  public static async Task<(bool ok, int status, string detail)> ReportAsync(
    MatchOutcome outcome, CancellationToken ct = default)
  {
    string? apiBase = Environment.GetEnvironmentVariable("DRAFTBOT_API_BASE")?.TrimEnd('/');
    string? token = Environment.GetEnvironmentVariable("DRAFTBOT_API_TOKEN");
    if (string.IsNullOrWhiteSpace(apiBase) || string.IsNullOrWhiteSpace(token))
      return (false, 0, "not configured (set DRAFTBOT_API_BASE and DRAFTBOT_API_TOKEN)");

    var payload = new
    {
      playerA = outcome.PlayerA,
      playerB = outcome.PlayerB,
      winner = outcome.Winner,
      gamesWinner = outcome.GamesWinner,
      gamesLoser = outcome.GamesLoser,
      sessionId = outcome.SessionId,
      mtgoMatchId = outcome.MtgoMatchId,
    };
    string body = JsonSerializer.Serialize(payload, _json);

    using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/matches/report")
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    try
    {
      using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
      string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      return ((int)resp.StatusCode == 200, (int)resp.StatusCode, respBody.Trim());
    }
    catch (Exception ex)
    {
      return (false, 0, ex.Message.Split('\n')[0]);
    }
  }

  /// <summary>Synchronous convenience wrapper for the console mode handlers.</summary>
  public static (bool ok, int status, string detail) Report(MatchOutcome outcome) =>
    ReportAsync(outcome).GetAwaiter().GetResult();

  /// <summary>
  /// GET the pending pairings DraftBot wants results for (both players linked).
  /// Returns (ok, pairings, detail). ok is false on transport/config/HTTP failure.
  /// </summary>
  public static async Task<(bool ok, List<PairingInfo> pairings, string detail)> FetchPairingsAsync(
    string? sessionId = null, CancellationToken ct = default)
  {
    var result = new List<PairingInfo>();
    string? apiBase = Environment.GetEnvironmentVariable("DRAFTBOT_API_BASE")?.TrimEnd('/');
    string? token = Environment.GetEnvironmentVariable("DRAFTBOT_API_TOKEN");
    if (string.IsNullOrWhiteSpace(apiBase) || string.IsNullOrWhiteSpace(token))
      return (false, result, "not configured (set DRAFTBOT_API_BASE and DRAFTBOT_API_TOKEN)");

    string url = $"{apiBase}/pairings/active"
               + (string.IsNullOrWhiteSpace(sessionId) ? "" : $"?sessionId={Uri.EscapeDataString(sessionId)}");
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    try
    {
      using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
      string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      if ((int)resp.StatusCode != 200)
        return (false, result, $"HTTP {(int)resp.StatusCode}: {body.Trim()}");

      using var doc = JsonDocument.Parse(body);
      if (doc.RootElement.TryGetProperty("pairings", out var arr) && arr.ValueKind == JsonValueKind.Array)
      {
        foreach (var p in arr.EnumerateArray())
        {
          string? a = p.TryGetProperty("playerA", out var ea) ? ea.GetString() : null;
          string? b = p.TryGetProperty("playerB", out var eb) ? eb.GetString() : null;
          if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) continue;
          string? sid = p.TryGetProperty("sessionId", out var es) ? es.GetString() : null;
          int mn = p.TryGetProperty("matchNumber", out var em) && em.TryGetInt32(out int v) ? v : 0;
          result.Add(new PairingInfo(a!, b!, sid, mn));
        }
      }
      return (true, result, $"{result.Count} pairing(s)");
    }
    catch (Exception ex)
    {
      return (false, result, ex.Message.Split('\n')[0]);
    }
  }

  /// <summary>Synchronous convenience wrapper.</summary>
  public static (bool ok, List<PairingInfo> pairings, string detail) FetchPairings(string? sessionId = null) =>
    FetchPairingsAsync(sessionId).GetAwaiter().GetResult();
}
