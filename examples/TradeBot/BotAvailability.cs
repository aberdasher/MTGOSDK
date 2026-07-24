/** @file
  Bot availability detection + a rotating pool of free-card trade bots.

  Marketplace trade bots advertise their state in their post message — e.g.
  "open" / "free" when they can take a trade, "busy" while serving another user.
  Reading that message is READ-ONLY and does NOT ping the bot (unlike
  RequestTrade, which pokes the bot and flips it to "busy"), so we can poll
  availability as often as we like WITHOUT tripping the invite/cancel throttle
  that makes freebots start declining.

  This lets the acquisition flow (1) skip bots that are already busy and
  (2) ROTATE across a pool of open bots instead of spamming a single one.

  HOW WE READ THE POST (and why not SerializePostsAs):
    The SDK's TradeManager.SerializePostsAs<T> requires T to be an INTERFACE
    (SerializableBase.SerializeAs throws on a concrete type) and its no-token
    fallback keys projected values by remote leaf-path, so a hand-rolled DTO
    interface isn't reliably populated. Instead we reuse the exact server-side
    filter TradeManager itself uses — CollectionHelpers.WherePropertyStringContains
    on the live Marketplace.AllPosts — which runs in the diver and returns only
    the few matching posts, then read ITradePost.RawMessage / Poster.Name off
    those directly (the same members the TradePost wrapper reads). Every piece of
    this path is already exercised elsewhere in the SDK/these examples.

  CLASSIFICATION IS HEURISTIC. Exact wording varies by bot operator, so the token
  sets below are a best effort ("open"/"free"/… available vs "busy"/… busy),
  matched on WHOLE WORDS (so "ready" doesn't fire inside "already"). The read-only
  `botstatus` / `poolstatus` modes print the RAW message next to the verdict so
  these lists can be tuned against what live bots actually post.
**/

using System.Text.RegularExpressions;

using MTGOSDK.API;                                  // ObjectProvider
using MTGOSDK.Core.Remoting;                        // RemoteClient
using static MTGOSDK.Core.Reflection.DLRWrapper;    // Unbind, Map, Try

namespace TradeBot;

/// <summary>How a bot's post message classifies its trading availability.</summary>
public enum BotStatus
{
  Open,     // post advertises availability (open / free / available / ...)
  Busy,     // post advertises it is mid-trade (busy / in trade / ...)
  Unknown,  // the bot has a post, but the message matches neither token set
  NoPost,   // the bot is not currently listed on the marketplace
}

/// <summary>A single availability reading for a named bot.</summary>
public readonly record struct BotAvailability(
  string Name, BotStatus Status, string Message, int PostCount)
{
  public bool IsOpen => Status == BotStatus.Open;

  public override string ToString()
  {
    string tag = Status switch
    {
      BotStatus.Open   => "OPEN  ",
      BotStatus.Busy   => "busy  ",
      BotStatus.NoPost => "noPost",
      _                => "?     ",
    };
    string msg = Message.Length > 90 ? Message.Substring(0, 90) + "…" : Message;
    return $"{Name,-22} {tag}  \"{msg}\"";
  }
}

/// <summary>
/// Reads and classifies marketplace trade-bot availability, and holds the
/// default pool of free-card bots to rotate through.
/// </summary>
public static class BotPool
{
  // Live WotC marketplace service + the SDK's remoting collection helper.
  const string IMarketplace     = "WotC.MtGO.Client.Model.Trade.Interfaces.IMarketplace";
  const string CollectionHelpers = "MTGOSDK.Core.Remoting.Interop.CollectionHelpers";

  /// <summary>
  /// Known free-card bots, tried in order. Names that aren't currently posting
  /// (NoPost) are simply skipped, so listing a bot that happens to be offline is
  /// harmless. Override at the CLI with <c>--bots=a,b,c</c>.
  /// </summary>
  public static readonly string[] FreeBots =
  {
    "_DojoTradeFree",
    "CardhoarderFreeBot",
    "CardhoarderFreeBot2",
    "CardhoarderFreeBot3",
  };

  // Whole-word tokens (lower-cased). BUSY is checked FIRST so a post like
  // "normally open — busy right now" reads busy, not open. Bare "trading" is
  // deliberately NOT a busy token (bot ad copy like "trading singles" would
  // false-positive); only explicit busy phrases count.
  static readonly string[] BusyWords   = { "busy", "unavailable", "offline", "occupied" };
  static readonly string[] BusyPhrases = { "in trade", "in a trade", "please wait",
                                           "not available", "currently trading", "trading now" };
  static readonly string[] OpenWords   = { "open", "free", "available", "ready", "online" };

  /// <summary>Classify a raw post message into a <see cref="BotStatus"/>.</summary>
  public static BotStatus Classify(string? message)
  {
    if (string.IsNullOrWhiteSpace(message)) return BotStatus.Unknown;
    string m = message.ToLowerInvariant();
    // Whole-word set so "ready" doesn't match inside "already", etc.
    var words = Regex.Split(m, "[^a-z0-9]+").Where(w => w.Length > 0).ToHashSet();

    if (BusyWords.Any(words.Contains) || BusyPhrases.Any(m.Contains)) return BotStatus.Busy;
    if (OpenWords.Any(words.Contains)) return BotStatus.Open;
    return BotStatus.Unknown;
  }

  /// <summary>
  /// Read a single bot's marketplace post and classify its availability.
  /// Read-only: does not ping the bot. Uses the SDK's server-side substring
  /// filter (one diver pass; only matching posts cross the bridge).
  /// </summary>
  public static BotAvailability Check(string name)
  {
    List<(string Poster, string Message)> posts;
    try { posts = ReadPosts(name); }
    catch (Exception ex)
    {
      return new BotAvailability(name, BotStatus.Unknown,
                                 $"<read failed: {ex.GetType().Name}>", -1);
    }

    // The filter is a SUBSTRING match, so Check("CardhoarderFreeBot") also
    // returns "CardhoarderFreeBot2/3" — keep only EXACT poster matches. If the
    // exact bot isn't posting, it's NoPost (never fall back to a sibling's post).
    var exact = posts
      .Where(p => string.Equals(p.Poster, name, StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (exact.Count == 0) return new BotAvailability(name, BotStatus.NoPost, "", 0);

    string message = exact[0].Message;
    return new BotAvailability(name, Classify(message), message, exact.Count);
  }

  /// <summary>Check every bot in <paramref name="names"/> (order preserved).</summary>
  public static List<BotAvailability> CheckPool(IEnumerable<string> names) =>
    names.Select(Check).ToList();

  /// <summary>The bots from a sweep that read as Open, in the swept order.</summary>
  public static List<BotAvailability> OpenIn(IEnumerable<BotAvailability> statuses) =>
    statuses.Where(s => s.IsOpen).ToList();

  /// <summary>
  /// Rotation candidates from a sweep: Open bots first, then Unknown ones as a
  /// fallback (never Busy/NoPost). The Unknown tier keeps the rotation working
  /// when live post wording doesn't match the classifier's token lists — it can
  /// still avoid bots that explicitly read busy.
  /// </summary>
  public static List<BotAvailability> CandidatesIn(IEnumerable<BotAvailability> statuses)
  {
    var s = statuses.ToList();
    return s.Where(b => b.Status == BotStatus.Open)
            .Concat(s.Where(b => b.Status == BotStatus.Unknown))
            .ToList();
  }

  // Read (poster, message) for every marketplace post whose poster name CONTAINS
  // posterNameSearch, via the same server-side filter TradeManager uses. The
  // filter iterates AllPosts inside the diver and returns only matches, so this
  // avoids marshalling the whole (~1600-post) marketplace across the bridge.
  static List<(string Poster, string Message)> ReadPosts(string posterNameSearch)
  {
    var list = new List<(string, string)>();

    dynamic mkt = ObjectProvider.Get(IMarketplace, true, false, false);
    dynamic allPosts = Unbind((object)mkt).AllPosts;
    dynamic filtered = RemoteClient.InvokeMethod(
      CollectionHelpers,
      "WherePropertyStringContains",
      null,
      new object[] { allPosts, "Poster.Name", posterNameSearch, true });

    foreach (var post in Map<dynamic>(filtered))
    {
      string poster = Try(() => (string)post.Poster.Name) ?? "";
      string raw    = Try(() => (string)post.RawMessage) ?? "";
      string msg    = raw.Replace("\n", " ").Replace("\r", " ").Trim();
      list.Add((poster, msg));
    }
    return list;
  }
}
