/** @file
  Internal HTTP service: the custodian account as a token-protected trade vault + operator UI.

  ONE HttpListener serves BOTH a JSON API (for DraftBot + scripts) and (added in the dashboard
  step) a browser operator page. A SINGLE worker drains a job queue one trade at a time (MTGO
  trades are strictly sequential) via the unified RunTrade dispatch, so give / receive / swap all
  flow through the SAME path. Bearer-token auth on every route; bound to a chosen address
  (loopback by default). This API MOVES REAL ASSETS, so it must stay internal (loopback /
  Tailscale) and token-protected. DraftBot is just another authed client that decides who gets what.

  A job carries what the custodian GIVES + what it RECEIVES (each a list of TradeItem):
    give-only  -> lend    receive-only -> deposit/grab    give+receive -> swap
  /request and /deposit are ergonomic shortcuts (cards[] + qty) that build give-only / receive-only
  jobs; /trade takes the general {give[], receive[]} shape.
**/
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradeBot;

public enum JobState { Queued, Running, Done, Failed }

/// <summary>One queued trade: what the custodian GIVES and RECEIVES vs a user. Mutated only by
/// the single worker thread. Give-only=lend, receive-only=deposit, both=swap.</summary>
public sealed class TradeJob
{
  public string Id { get; init; } = "";
  public string Type { get; init; } = "request";       // request (give) | deposit (receive) | swap
  public string User { get; init; } = "";              // the MTGO partner
  public List<TradeItem> Give { get; init; } = new();     // custodian -> user
  public List<TradeItem> Receive { get; init; } = new();  // user -> custodian
  public bool Commit { get; init; }                    // false => dry-run (reaches approval, cancels)
  public int WaitSec { get; init; } = int.MaxValue;    // readiness wait for partner (online+YES); MaxValue = until ready
  public int CatId { get; init; }                      // recall: the exact printing to reclaim
  public string? LoanId { get; init; }                 // recall: the loan being settled
  public string? Intent { get; init; }                 // give custody semantics: "loan" | "withdraw"; null = infer (tix=withdraw, cards=loan)
  public volatile bool Cancelled;                      // operator cancel (before/at pickup, or via abort while running)
  public JobState State { get; set; } = JobState.Queued;
  public string Detail { get; set; } = "";
  public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("o");
  public string? StartedAt { get; set; }
  public string? FinishedAt { get; set; }

  // Convenience for the worker/dispatch: TradeItem list -> (name, qty) tuples.
  public List<(string name, int qty, int catId)> GiveTuples() => Give.Select(i => (i.Name, Math.Max(1, i.Qty), i.CatId)).ToList();
  public List<(string name, int qty, int catId)> ReceiveTuples() => Receive.Select(i => (i.Name, Math.Max(1, i.Qty), i.CatId)).ToList();
}

/// <summary>
/// Continuously-running internal trade service. Owns an HTTP listener (accept loop on the
/// calling thread) plus one background worker draining a job queue. The worker is the ONLY
/// thing that touches MTGO, so trades never overlap. Constructed with a <c>tradeFn</c>
/// delegate (the unified RunTrade dispatch) so it reuses the lend/grab/swap flows without
/// depending on Program internals.
/// </summary>
public sealed class TradeService
{
  readonly string _token;
  readonly string _bind;
  readonly int _port;
  readonly MtgoConnection _conn;
  readonly int _perJobTimeoutSec;
  readonly bool _commitArmed;   // master arm (from --commit); false => service can never commit (dashboard reflects this)
  // (partner, give[(name,qty)], receive[(name,qty)], commit, yesTimeoutSec) => (ok, detail)
  readonly Func<string, List<(string name, int qty, int catId)>, List<(string name, int qty, int catId)>, bool, int, (bool ok, string detail)> _tradeFn;
  // (partner, card, catId, qty, commit, waitSec) => (ok, detail) — recall: receive the EXACT owed printing
  readonly Func<string, string, int, int, bool, int, (bool ok, string detail)> _recallFn;
  readonly Action<string> _log;
  // Optional vault read: () => (owned tix, distinct item count, top holdings). Null => /vault n/a.
  readonly Func<(int tix, int distinct, List<(string name, int qty)> top)>? _vaultFn;
  readonly HoldingStore _holdings;   // custody ledger — deposits + house loans, per-owner allow-lists

  readonly ConcurrentDictionary<string, TradeJob> _jobs = new();
  readonly BlockingCollection<TradeJob> _queue = new(new ConcurrentQueue<TradeJob>());
  readonly List<string> _logRing = new();   // last N service log lines, for the dashboard /log
  object? _vaultCache;                       // last good /vault snapshot (reused while a trade runs)
  readonly object _vaultScanLock = new();    // single-flight: concurrent /vault callers share one scan
  DateTime _vaultAt = DateTime.MinValue;     // when the snapshot was taken
  long _vaultSeq = -1;                       // CompletedTradeSeq the snapshot reflects
  const int VaultTtlSec = 60;                // only a completed trade changes the vault; TTL guards drift
  volatile string? _runningJobId;            // id of the job the worker is executing now (for cancel)

  static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };

  // Card-name autocomplete via Scryfall, PROXIED so the dashboard only ever talks to us.
  static readonly HttpClient _http = CreateHttp();
  static readonly ConcurrentDictionary<string, (string[] names, long at)> _acCache = new();
  static HttpClient CreateHttp()
  {
    var h = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    h.DefaultRequestHeaders.UserAgent.ParseAdd("MTGOSDK-TradeBot/1.0");   // Scryfall asks for a descriptive UA
    h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    return h;
  }

  public TradeService(string token, string bind, int port, MtgoConnection conn, int perJobTimeoutSec, bool commitArmed,
    Func<string, List<(string name, int qty, int catId)>, List<(string name, int qty, int catId)>, bool, int, (bool ok, string detail)> tradeFn,
    Func<string, string, int, int, bool, int, (bool ok, string detail)> recallFn,
    Func<(int tix, int distinct, List<(string name, int qty)> top)>? vaultFn,
    HoldingStore holdings,
    Action<string> log)
  {
    _token = token; _bind = bind; _port = port; _conn = conn;
    _perJobTimeoutSec = perJobTimeoutSec; _commitArmed = commitArmed; _tradeFn = tradeFn; _recallFn = recallFn; _vaultFn = vaultFn; _holdings = holdings;
    // Tee every service log line into a ring buffer so the dashboard's GET /log can show it.
    // Stamped through LogFormat so service lines and executor lines in the same ring share
    // one format and can be diffed against each other.
    _log = s => { log(s); Note(LogFormat.Stamp(s)); };
  }

  /// <summary>
  /// Append an ALREADY-STAMPED line to the ring buffer verbatim. This is how executor
  /// output (which stamps itself) reaches GET /log without acquiring a second timestamp.
  /// </summary>
  public void Note(string preStamped)
  {
    lock (_logRing)
    {
      _logRing.Add(preStamped);
      if (_logRing.Count > 400) _logRing.RemoveRange(0, _logRing.Count - 400);
    }
  }

  /// <summary>Start the worker + HTTP listener. BLOCKS (accept loop) until the process ends.</summary>
  public void Run()
  {
    var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "trade-worker" };
    worker.Start();
    var monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "conn-monitor" };
    monitor.Start();

    var listener = new HttpListener();
    string prefix = $"http://{_bind}:{_port}/";
    listener.Prefixes.Add(prefix);
    try { listener.Start(); }
    catch (HttpListenerException ex)
    {
      _log($"[serve] could NOT bind {prefix}: {ex.Message}");
      _log($"[serve] if this is Access Denied, reserve the URL once (elevated): " +
           $"netsh http add urlacl url={prefix} user={Environment.UserName}");
      return;
    }

    _log($"[serve] listening on {prefix}   custodian={_conn.Account}   (Bearer token required)");
    _log($"[serve] dashboard: http://{_bind}:{_port}/  (open in a browser; paste the token)");
    _log("[serve] routes: GET / (dashboard) | GET /health | GET /vault | GET /holdings | GET /autocomplete?q= | GET /log | GET /jobs | GET /jobs/{id} | " +
         "POST /jobs/{id}/cancel | POST /holdings/{id}/recall | POST /owners/{owner}/allow | POST /binders/prune | POST /request | POST /deposit | POST /trade");

    while (true)
    {
      HttpListenerContext ctx;
      try { ctx = listener.GetContext(); }
      catch (Exception ex) { _log($"[serve] accept error: {ex.Message}"); continue; }
      // Handlers are cheap (enqueue + read status); run off-thread so the accept loop
      // keeps serving even if one client is slow.
      Task.Run(() =>
      {
        try { Handle(ctx); }
        catch (Exception ex) { _log($"[serve] handler error: {ex.Message}"); try { ctx.Response.Abort(); } catch { } }
      });
    }
  }

  void Handle(HttpListenerContext ctx)
  {
    var req = ctx.Request;
    string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
    if (path.Length == 0) path = "/";
    string method = req.HttpMethod.ToUpperInvariant();

    // The dashboard SHELL (static HTML, no data) is the ONLY unauthenticated route — it then
    // asks the operator for the token and sends it as Bearer on every data call below.
    if (method == "GET" && path == "/") { Send(ctx, 200, "text/html; charset=utf-8", Dashboard.Html); return; }

    // Auth: Bearer <token> on EVERY other route (no anonymous access — this moves assets).
    string authHeader = req.Headers["Authorization"] ?? "";
    string presented = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
      ? authHeader.Substring("Bearer ".Length).Trim() : "";
    if (!TokenOk(presented)) { Write(ctx, 401, new { error = "unauthorized" }); return; }

    if (method == "GET" && path == "/health")
    {
      Write(ctx, 200, new { ok = !_conn.Reconnecting, custodian = _conn.Account, commit = _commitArmed,
        reconnecting = _conn.Reconnecting, queued = _queue.Count, jobs = _jobs.Count });
      return;
    }
    if (method == "GET" && path == "/jobs")
    {
      var list = _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(100).Select(Project).ToList();
      Write(ctx, 200, new { jobs = list });
      return;
    }
    if (method == "POST" && path.StartsWith("/jobs/") && path.EndsWith("/cancel"))
    { HandleCancel(ctx, path.Substring("/jobs/".Length, path.Length - "/jobs/".Length - "/cancel".Length)); return; }
    if (method == "GET" && path.StartsWith("/jobs/"))
    {
      string id = path.Substring("/jobs/".Length);
      if (_jobs.TryGetValue(id, out var job)) Write(ctx, 200, Project(job));
      else Write(ctx, 404, new { error = "no such job", id });
      return;
    }
    if (method == "GET" && path == "/log")
    {
      string[] lines; lock (_logRing) lines = _logRing.ToArray();
      Write(ctx, 200, new { lines, running = _jobs.Values.Any(j => j.State == JobState.Running), queued = _queue.Count });
      return;
    }
    if (method == "GET" && path == "/vault") { HandleVault(ctx); return; }
    if (method == "GET" && path == "/holdings")
    {
      var allow = _holdings.AllAllow();
      var list = _holdings.Active().Select(h => new { id = h.Id, owner = h.Owner, card = h.Card, catId = h.CatId,
        qty = h.Qty, borrower = h.Borrower, status = h.Status, acquiredAt = h.AcquiredAt,
        canLendTo = allow.TryGetValue(h.Owner, out var l) ? l : new List<string>() }).ToList();
      Write(ctx, 200, new { holdings = list });
      return;
    }
    if (method == "POST" && path.StartsWith("/owners/") && path.EndsWith("/allow"))
    {
      string owner = Uri.UnescapeDataString(path.Substring("/owners/".Length, path.Length - "/owners/".Length - "/allow".Length));
      string abody; using (var rr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8)) abody = rr.ReadToEnd();
      List<string> borrowers;
      try { borrowers = JsonSerializer.Deserialize<AllowDto>(abody, JsonIn)?.Borrowers ?? new(); }
      catch (Exception ex) { Write(ctx, 400, new { error = "bad json", detail = ex.Message }); return; }
      _holdings.SetOwnerAllow(owner, borrowers);
      _log($"[holding] owner {owner} allow-list = [{string.Join(", ", _holdings.OwnerAllow(owner))}]");
      Write(ctx, 200, new { owner, allow = _holdings.OwnerAllow(owner) });
      return;
    }
    if (method == "GET" && path == "/binders")
    {
      if (_runningJobId != null || _conn.Reconnecting) { Write(ctx, 200, new { binders = Array.Empty<object>(), note = "busy" }); return; }
      var bl = _conn.Exec.ListBinders().Select(b => new { name = b.name, items = b.items }).ToList();
      Write(ctx, 200, new { binders = bl });
      return;
    }
    if (method == "GET" && path == "/autocomplete") { HandleAutocomplete(ctx, req); return; }
    if (method == "POST" && path == "/request") { HandleEnqueue(ctx, req, "request"); return; }  // give
    if (method == "POST" && path == "/deposit") { HandleEnqueue(ctx, req, "deposit"); return; }  // receive
    if (method == "POST" && path == "/trade")   { HandleEnqueue(ctx, req, "trade");   return; }  // give+receive
    if (method == "POST" && path == "/binders/prune") { HandlePrune(ctx); return; }
    if (method == "POST" && path.StartsWith("/holdings/") && path.EndsWith("/recall"))
    { HandleRecall(ctx, path.Substring("/holdings/".Length, path.Length - "/holdings/".Length - "/recall".Length)); return; }

    Write(ctx, 404, new { error = "not found", path, method });
  }

  // Parse the body, build the job's give/receive per endpoint, validate, and enqueue.
  //   request => give-only (cards[] x qty);  deposit => receive-only (cards[] x qty);
  //   trade   => general {give[], receive[]} (each item {name, qty}) — enables swap.
  void HandleEnqueue(HttpListenerContext ctx, HttpListenerRequest req, string endpoint)
  {
    string body;
    using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8)) body = r.ReadToEnd();
    RequestDto dto;
    try { dto = JsonSerializer.Deserialize<RequestDto>(body, JsonIn) ?? new RequestDto(); }
    catch (Exception ex) { Write(ctx, 400, new { error = "bad json", detail = ex.Message }); return; }

    string user = ((dto.User ?? dto.Partner) ?? "").Trim();
    bool commit = dto.Commit ?? false;
    if (user.Length == 0) { Write(ctx, 400, new { error = "user/partner required" }); return; }

    List<TradeItem> give, receive;
    string type;

    if (endpoint == "trade")
    {
      give = ItemsFrom(dto.Give);
      receive = ItemsFrom(dto.Receive);
      if (give.Count == 0 && receive.Count == 0)
      { Write(ctx, 400, new { error = "trade needs a non-empty give[] and/or receive[] (each item {name, qty>=1})" }); return; }
      bool g = give.Count > 0, r = receive.Count > 0;
      type = g && r ? "swap" : g ? "request" : "deposit";
      // No shape restrictions: the Negotiate engine presents a binder holding exactly
      // give[] and requests exactly receive[], so multi-give, qty>1 receive, and
      // give+receive together are all legal. Engine capability is discovered by calling
      // the engine — never re-declared here.
    }
    else
    {
      // request / deposit shortcut: cards[] + qty (copies of each card).
      var cards = (dto.Cards ?? new()).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).ToList();
      int qty = dto.Qty ?? 1;
      if (cards.Count == 0) { Write(ctx, 400, new { error = "cards required (non-empty array)" }); return; }
      if (qty < 1) { Write(ctx, 400, new { error = "qty must be >= 1" }); return; }
      if (endpoint == "deposit" && cards.Count != 1)
      { Write(ctx, 400, new { error = "deposit v1 takes exactly ONE distinct card (use qty for copies; multi-distinct deposit not yet supported)" }); return; }
      // Aggregate duplicate names, multiply by qty.
      var items = cards.GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                       .Select(gr => new TradeItem { Name = gr.Key, Qty = gr.Count() * qty }).ToList();
      if (endpoint == "request") { give = items; receive = new(); type = "request"; }
      else                       { give = new(); receive = items; type = "deposit"; }
    }

    // Readiness wait: how long to wait for the partner to be online + reply YES.
    // waitMinutes absent -> serve default (--timeout); <= 0 -> until ready (unbounded).
    int waitSec = dto.WaitMinutes.HasValue
      ? (dto.WaitMinutes.Value <= 0 ? int.MaxValue : dto.WaitMinutes.Value * 60)
      : _perJobTimeoutSec;
    var job = new TradeJob { Id = NewId(), Type = type, User = user, Give = give, Receive = receive, Commit = commit, WaitSec = waitSec,
                             Intent = dto.Intent?.Trim().ToLowerInvariant() };
    _jobs[job.Id] = job;
    _queue.Add(job);
    _log($"[serve] queued {job.Id}: {type} user={user} give=[{Fmt(give)}] receive=[{Fmt(receive)}] commit={commit} wait={(waitSec >= int.MaxValue / 2 ? "until-ready" : waitSec + "s")}");
    Write(ctx, 202, Project(job));
  }

  static List<TradeItem> ItemsFrom(List<ItemDto>? items) =>
    (items ?? new())
      .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
      .Select(i => new TradeItem { Name = i!.Name!.Trim(), Qty = Math.Max(1, i.Qty ?? 1), CatId = Math.Max(0, i.CatId ?? 0) })
      .ToList();

  static string Fmt(List<TradeItem> xs) => string.Join(", ", xs.Select(i => i.ToString()));

  // Length-checked constant-time compare so the bearer token isn't leaked by timing.
  bool TokenOk(string presented)
  {
    if (presented.Length == 0 || presented.Length != _token.Length) return false;
    int diff = 0;
    for (int i = 0; i < _token.Length; i++) diff |= presented[i] ^ _token[i];
    return diff == 0;
  }

  // The job whose trade is currently executing — context for OnTradeCompleted (one job at a
  // time on one worker, so a single field is unambiguous).
  volatile TradeJob? _custodyJob;

  // THE custody recorder: fires once per COMMITTED trade, from the executor's own completion
  // event, so no flow can forget to record. Semantics:
  //   deposit job  -> a held holding owned by the depositor (custody FOR them).
  //   give, intent "loan"     -> a recallable house loan (borrower owes it back).
  //   give, intent "withdraw" -> value going OUT for good (a DraftBot withdrawal or payment):
  //                              nothing owed back; release any held deposits of theirs.
  //   defaults when the caller doesn't say: swap gives and ALL tix gives are permanent
  //   transfers ("withdraw" — tix claims are DraftBot's ledger, never loans); card gives on
  //   /request default to "loan" (the recallable-lending business). Swap receives are house
  //   acquisitions — the vault itself tracks those; no custody row.
  void OnTradeCompleted(string partner, IReadOnlyList<TradeExecutor.TradedItemInfo> given, IReadOnlyList<TradeExecutor.TradedItemInfo> received)
  {
    var job = _custodyJob;
    if (job is null || !string.Equals(partner, job.User, StringComparison.OrdinalIgnoreCase)) return;
    if (job.Type == "recall") return;   // the worker settles the specific loan itself
    foreach (var g in given)
    {
      bool isTix = string.Equals(g.Name, "Event Ticket", StringComparison.OrdinalIgnoreCase);
      string intent = job.Intent ?? ((job.Type == "trade" || isTix) ? "withdraw" : "loan");
      if (intent == "withdraw")
      {
        int released = _holdings.ConsumeDeposits(job.User, g.Name, g.Qty);
        _log($"[holding] {job.User} withdrew {g.Qty}x {g.Name}" +
             (released > 0 ? $" — released {released} from their held deposits"
                           : " — no held deposits to release (claim accounting lives in DraftBot)"));
      }
      else
      { var h = _holdings.RecordHouseLoan(g.Name, g.CatId, g.Qty, job.User); _log($"[holding] {job.User} borrowed {g.Qty}x {g.Name} (cat {g.CatId}) — house loan {h.Id}"); }
    }
    if (job.Type == "deposit")
      foreach (var rc in received)
      { var h = _holdings.RecordDeposit(job.User, rc.Name, rc.CatId, rc.Qty); _log($"[holding] {job.User} deposited {rc.Qty}x {rc.Name} (cat {rc.CatId}) — holding {h.Id} (owner {job.User})"); }
  }

  void WorkerLoop()
  {
    foreach (var job in _queue.GetConsumingEnumerable())
    {
      if (job.Cancelled)   // cancelled while still queued — skip without touching MTGO
      {
        job.State = JobState.Failed; job.Detail = "cancelled before it started";
        job.FinishedAt = DateTime.UtcNow.ToString("o");
        _log($"[serve] {job.Id} -> cancelled (before start)");
        continue;
      }
      job.State = JobState.Running;
      job.StartedAt = DateTime.UtcNow.ToString("o");
      _runningJobId = job.Id;
      _conn.Exec.ClearAbort();   // fresh cancel state for this job
      _log($"[serve] running {job.Id} ({job.Type} user={job.User}) — readiness wait: {(job.WaitSec >= int.MaxValue / 2 ? "until partner is ready" : job.WaitSec + "s")}");
      _conn.EnsureHealthy();   // block here until MTGO is reachable again (survives a client restart)
      // Custody recording rides the executor's TradeCompleted event (fires at trade close,
      // BEFORE the flow returns) — re-arm on the CURRENT executor each job, because a
      // reconnect can swap in a fresh executor and a one-time subscription would be lost.
      _conn.Exec.TradeCompleted -= OnTradeCompleted;
      _conn.Exec.TradeCompleted += OnTradeCompleted;
      _custodyJob = job;
      try
      {
        (bool ok, string detail) r;
        if (job.Type == "recall")
        {
          // Recall: receive the EXACT owed printing back, then settle that loan.
          string rcard = job.Receive.Count > 0 ? job.Receive[0].Name : "";
          int rqty = job.Receive.Count > 0 ? job.Receive[0].Qty : 1;
          r = _recallFn(job.User, rcard, job.CatId, rqty, job.Commit, job.WaitSec);
          if (r.ok && job.LoanId != null)
          {
            _holdings.SettleReturn(job.LoanId);
            _log($"[holding] {job.LoanId} settled — got {rcard} (cat {job.CatId}) back from {job.User}.");
          }
        }
        else
        {
          r = _tradeFn(job.User, job.GiveTuples(), job.ReceiveTuples(), job.Commit, job.WaitSec);
        }
        job.State = r.ok ? JobState.Done : JobState.Failed;
        job.Detail = r.detail;
      }
      catch (Exception ex) { job.State = JobState.Failed; job.Detail = ex.Message; }
      _custodyJob = null;
      _runningJobId = null;
      job.FinishedAt = DateTime.UtcNow.ToString("o");
      _log($"[serve] {job.Id} -> {job.State.ToString().ToLowerInvariant()} ({job.Detail})");
    }
  }

  // Proactively keep the MTGO connection alive so /health is accurate and a reconnect is
  // already done before the next job runs. The reconnect itself blocks inside EnsureHealthy;
  // this thread just triggers it during idle periods.
  void MonitorLoop()
  {
    while (true)
    {
      try { _conn.EnsureHealthy(); }
      catch (Exception ex) { _log($"[serve] monitor error: {ex.Message.Split('\n')[0]}"); }
      Thread.Sleep(20000);
    }
  }

  static object Project(TradeJob j) => new
  {
    id = j.Id, type = j.Type, user = j.User,
    give = j.Give.Select(i => new { name = i.Name, qty = i.Qty }).ToList(),
    receive = j.Receive.Select(i => new { name = i.Name, qty = i.Qty }).ToList(),
    commit = j.Commit,
    waitSec = j.WaitSec,
    waitLabel = j.WaitSec >= int.MaxValue / 2 ? "until ready" : (j.WaitSec >= 60 ? (j.WaitSec / 60) + " min" : j.WaitSec + "s"),
    cancellable = j.State is JobState.Queued or JobState.Running,
    state = j.State.ToString().ToLowerInvariant(), detail = j.Detail,
    createdAt = j.CreatedAt, startedAt = j.StartedAt, finishedAt = j.FinishedAt
  };

  static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

  void Write(HttpListenerContext ctx, int status, object payload)
  {
    byte[] buf = JsonSerializer.SerializeToUtf8Bytes(payload);
    try
    {
      ctx.Response.StatusCode = status;
      ctx.Response.ContentType = "application/json";
      ctx.Response.ContentLength64 = buf.Length;
      ctx.Response.OutputStream.Write(buf, 0, buf.Length);
    }
    catch { /* client hung up */ }
    finally { try { ctx.Response.OutputStream.Close(); } catch { } }
  }

  // Non-JSON responder (the dashboard HTML shell).
  void Send(HttpListenerContext ctx, int status, string contentType, string body)
  {
    byte[] buf = Encoding.UTF8.GetBytes(body);
    try
    {
      ctx.Response.StatusCode = status;
      ctx.Response.ContentType = contentType;
      ctx.Response.ContentLength64 = buf.Length;
      ctx.Response.OutputStream.Write(buf, 0, buf.Length);
    }
    catch { }
    finally { try { ctx.Response.OutputStream.Close(); } catch { } }
  }

  // Owned tix + top holdings for the dashboard. Reading the collection touches MTGO (a full
  // per-item RPC scan — expensive), so (a) DON'T race the worker: while a trade runs or we're
  // reconnecting, return the last good snapshot; (b) only a COMPLETED trade changes the vault,
  // so serve the cached snapshot until CompletedTradeSeq moves or the TTL lapses; (c) single-
  // flight the scan so overlapping dashboard tabs / DraftBot polls share one pass.
  void HandleVault(HttpListenerContext ctx)
  {
    if (_vaultFn == null) { Write(ctx, 200, new { available = false, custodian = _conn.Account, reason = "vault read not wired" }); return; }
    bool busy = _conn.Reconnecting || _jobs.Values.Any(j => j.State == JobState.Running);
    long seq = 0; try { seq = _conn.Exec.CompletedTradeSeq; } catch { }
    bool Fresh() => _vaultCache != null && _vaultSeq == seq
                    && (DateTime.UtcNow - _vaultAt).TotalSeconds < VaultTtlSec;
    if (!busy && !Fresh())
    {
      lock (_vaultScanLock)
      {
        if (!Fresh())   // a concurrent caller may have refreshed while we waited
        {
          try
          {
            var v = _vaultFn();
            _vaultCache = new { available = true, custodian = _conn.Account, tix = v.tix, distinct = v.distinct,
              top = v.top.Select(t => new { name = t.name, qty = t.qty }).ToList(), at = DateTime.UtcNow.ToString("o") };
            _vaultAt = DateTime.UtcNow; _vaultSeq = seq;
          }
          catch (Exception ex) { _vaultCache ??= new { available = false, custodian = _conn.Account, reason = ex.Message.Split('\n')[0] }; }
        }
      }
    }
    Write(ctx, 200, _vaultCache ?? new { available = false, custodian = _conn.Account, reason = "snapshot pending (busy)" });
  }

  // Operator cancel. Running job -> abort the presence-wait via the executor flag; queued job ->
  // mark so the worker skips it. Terminal jobs are a no-op.
  void HandleCancel(HttpListenerContext ctx, string id)
  {
    if (!_jobs.TryGetValue(id, out var job)) { Write(ctx, 404, new { error = "no such job", id }); return; }
    if (job.State is JobState.Done or JobState.Failed)
    { Write(ctx, 200, new { id, state = job.State.ToString().ToLowerInvariant(), note = "already finished" }); return; }
    job.Cancelled = true;
    bool running = id == _runningJobId;
    if (running) { _conn.Exec.RequestAbort(); _log($"[serve] cancel requested for RUNNING job {id} — aborting the wait."); }
    else _log($"[serve] cancel requested for queued job {id} — it will be skipped at pickup.");
    Write(ctx, 200, new { id, cancelling = true, running });
  }

  // Enqueue a recall: reclaim the EXACT lent printing from the borrower, then settle the loan.
  void HandleRecall(HttpListenerContext ctx, string holdingId)
  {
    var h = _holdings.Get(holdingId);
    if (h is null) { Write(ctx, 404, new { error = "no such holding", id = holdingId }); return; }
    if (h.Status != "onloan" || h.Borrower is null) { Write(ctx, 409, new { error = "holding is not on loan", status = h.Status }); return; }
    var job = new TradeJob
    {
      Id = NewId(), Type = "recall", User = h.Borrower,
      Receive = new() { new TradeItem { Name = h.Card, Qty = h.Qty } },
      CatId = h.CatId, LoanId = h.Id, Commit = true, WaitSec = int.MaxValue,
    };
    _jobs[job.Id] = job;
    _queue.Add(job);
    _log($"[serve] queued recall {job.Id}: reclaim {h.Qty}x {h.Card} (cat {h.CatId}) from {h.Borrower} (holding {h.Id})");
    Write(ctx, 202, Project(job));
  }

  // Delete the bot's transient trade binders (Lending / SwapOffer) that piled up under churn.
  // Only when idle — deleting a binder mid-trade would break the open trade.
  void HandlePrune(HttpListenerContext ctx)
  {
    if (_runningJobId != null || _conn.Reconnecting)
    { Write(ctx, 409, new { error = "busy (a trade is running or reconnecting) — try again when idle" }); return; }
    _conn.EnsureHealthy();
    int n;
    try { n = _conn.Exec.PruneBinders(TradeExecutor.TransientBinderNames); }
    catch (Exception ex) { Write(ctx, 500, new { error = ex.Message.Split('\n')[0] }); return; }
    Write(ctx, 200, new { pruned = n });
  }

  // Proxy Scryfall's card-name autocomplete (cached ~5 min). Returns { names: [...] } so the
  // dashboard's card fields suggest valid names. Never fails hard — a Scryfall hiccup or no
  // network just yields no suggestions (the operator can still type a name).
  void HandleAutocomplete(HttpListenerContext ctx, HttpListenerRequest req)
  {
    string q = (req.QueryString["q"] ?? "").Trim();
    if (q.Length < 2) { Write(ctx, 200, new { names = Array.Empty<string>() }); return; }
    string key = q.ToLowerInvariant();
    if (_acCache.TryGetValue(key, out var c) && Environment.TickCount64 - c.at < 300000)
    { Write(ctx, 200, new { names = c.names, cached = true }); return; }
    try
    {
      string url = "https://api.scryfall.com/cards/autocomplete?q=" + Uri.EscapeDataString(q);
      string body = _http.GetStringAsync(url).GetAwaiter().GetResult();
      using var doc = JsonDocument.Parse(body);
      string[] names = doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array
        ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
        : Array.Empty<string>();
      _acCache[key] = (names, Environment.TickCount64);
      Write(ctx, 200, new { names });
    }
    catch (Exception ex) { Write(ctx, 200, new { names = Array.Empty<string>(), error = ex.Message.Split('\n')[0] }); }
  }

  sealed class RequestDto
  {
    public string? User { get; set; }
    public string? Partner { get; set; }             // alias for User (dashboard/trade uses "partner")
    public List<string>? Cards { get; set; }         // /request, /deposit shortcut
    public int? Qty { get; set; }                    // copies of each card (default 1)
    public List<ItemDto>? Give { get; set; }         // /trade
    public List<ItemDto>? Receive { get; set; }      // /trade
    public int? WaitMinutes { get; set; }            // readiness wait; <=0 or 0 => until ready (unbounded)
    public bool? Commit { get; set; }
    public string? Intent { get; set; }              // give custody: "loan" | "withdraw"; absent = infer (tix=withdraw, cards=loan)
  }

  sealed class ItemDto
  {
    public string? Name { get; set; }
    public int? Qty { get; set; }
    public int? CatId { get; set; }   // exact printing (from a .dek import)
  }

  sealed class AllowDto { public List<string>? Borrowers { get; set; } }
}
