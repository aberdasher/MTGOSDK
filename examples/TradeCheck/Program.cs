/** @file
  Read-only smoke test: attach to a running MTGO client and print connection
  info plus a sample of the current trade listings. Performs NO trade actions.
**/

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Collection;

static void Line(string s = "") => Console.WriteLine(s);

Line("=== MTGOSDK TradeCheck (read-only) ===");

// Verbose logging so we can see the diver injection / connection handshake.
using ILoggerFactory factory = LoggerFactory.Create(builder =>
{
  builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
  builder.SetMinimumLevel(LogLevel.Trace);
});

// 1. Pre-flight: make sure MTGO is actually running before we try to attach.
if (Process.GetProcessesByName("MTGO").Length == 0)
{
  Line("MTGO is not running.");
  Line("Please launch MTGO, log in, and (ideally) open the Trade / Marketplace");
  Line("scene so listings are populated, then run this again.");
  Environment.Exit(2);
}

Client client;
try
{
  // Default options => attach to the already-running client, do not restart it.
  client = new Client(new ClientOptions(), loggerFactory: factory);
}
catch (Exception ex)
{
  Line($"Failed to attach to MTGO: {ex.GetType().Name}: {ex.Message}");
  Line("If this is an access/injection error, try running this tool from a");
  Line("terminal with the same privilege level as the MTGO client (e.g. as");
  Line("Administrator if MTGO is elevated).");
  Environment.Exit(1);
  return;
}

using (client)
{
  Line($"MTGO version : {Client.Version}");
  Line($"Process id   : {Client.ProcessId}");
  Line($"Connected    : {client.IsConnected}");
  Line($"Logged in    : {client.IsLoggedIn}");

  if (!client.IsLoggedIn || !client.IsConnected)
  {
    Line();
    Line("Client is running but not fully logged in / connected.");
    Line("Complete the MTGO login, then run this again to read listings.");
    return;
  }

  try { Line($"Current user : {client.CurrentUser.Name}"); } catch { /* best-effort */ }

  // 2. Your own trade post (if any).
  Line();
  Line("--- Your trade post (MyPost) ---");
  try
  {
    var mine = TradeManager.MyPost;
    if (mine is null) Line("(you have no active trade post)");
    else
    {
      Line($"Format : {mine.Format}");
      Line($"Message: {mine.Message}");
      Line($"Wanted : {mine.Wanted.Count()} card(s) | Offered: {mine.Offered.Count()} card(s)");
    }
  }
  catch (Exception ex) { Line($"(could not read MyPost: {ex.Message})"); }

  // 3. Marketplace listings (the core smoke test).
  Line();
  Line("--- Marketplace trade posts (first 10) ---");
  try
  {
    var posts = TradeManager.AllPosts.Take(10).ToList();
    Line($"Visible posts sampled: {posts.Count}");
    int i = 0;
    foreach (var post in posts)
    {
      i++;
      string poster;
      try { poster = post.Poster.Name; } catch { poster = "<unknown>"; }
      string msg = post.Message?.Replace("\n", " ").Trim() ?? "";
      if (msg.Length > 80) msg = msg[..80] + "…";
      Line($"{i,2}. {poster,-20} {msg}");

      foreach (var w in post.Wanted.Take(3))
        Line($"       wants : {w.Quantity}x {SafeName(w)}");
      foreach (var o in post.Offered.Take(3))
        Line($"       offers: {o.Quantity}x {SafeName(o)}");
    }
    if (posts.Count == 0)
      Line("(no posts visible — open the Trade scene in MTGO so the marketplace channel populates)");
  }
  catch (Exception ex) { Line($"(could not read AllPosts: {ex.Message})"); }

  // 4. Trade partners + any active trade, for completeness.
  Line();
  Line("--- Misc ---");
  try { Line($"Previous trade partners: {TradeManager.TradePartners.Count()}"); }
  catch (Exception ex) { Line($"(partners unavailable: {ex.Message})"); }
  try
  {
    var cur = TradeManager.CurrentTrade;
    Line(cur is null ? "Active trade: none"
                     : $"Active trade: with {cur.TradePartner.Name}, state={cur.State}");
  }
  catch (Exception ex) { Line($"(current trade unavailable: {ex.Message})"); }

  Line();
  Line("Done. (No trade actions were performed.)");
}

static string SafeName(CardQuantityPair pair)
{
  try { return pair.Card.Name; } catch { return "<card>"; }
}
