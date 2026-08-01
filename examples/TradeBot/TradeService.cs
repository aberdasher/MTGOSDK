/** @file
  Internal HTTP service exposing the GIVE side (lend) as an async job queue.

  v1 (give-side only): POST /request enqueues a lend job (the custodian gives cards to a
  user). A SINGLE worker runs jobs one at a time — MTGO trades are strictly sequential —
  and clients poll GET /jobs/{id}. Bearer-token auth on every route; bound to a chosen
  address (loopback by default). This API MOVES REAL ASSETS, so it must stay internal
  (loopback / Tailscale) and token-protected. DraftBot becomes just another authed client
  that decides who gets what.

  Deposit (bot RECEIVES from a user, via the grabfrom flow) is intentionally NOT here yet.
**/
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradeBot;

public enum JobState { Queued, Running, Done, Failed }

/// <summary>One queued give (lend) request. Mutated only by the single worker thread.</summary>
public sealed class TradeJob
{
  public string Id { get; init; } = "";
  public string Type { get; init; } = "request";     // give-side: lend to a user
  public string User { get; init; } = "";
  public List<string> Cards { get; init; } = new();   // distinct card names
  public int Qty { get; init; } = 1;                   // copies of each card (e.g. N event tickets)
  public bool Commit { get; init; }                    // false => dry-run (reaches approval, cancels)
  public JobState State { get; set; } = JobState.Queued;
  public string Detail { get; set; } = "";
  public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("o");
  public string? StartedAt { get; set; }
  public string? FinishedAt { get; set; }
}

/// <summary>
/// Continuously-running internal trade service. Owns an HTTP listener (accept loop on the
/// calling thread) plus one background worker draining a job queue. The worker is the ONLY
/// thing that touches MTGO, so trades never overlap. Constructed with a <c>lendFn</c>
/// delegate so it reuses the existing RunLend flow without depending on Program internals.
/// </summary>
public sealed class TradeService
{
  readonly string _token;
  readonly string _bind;
  readonly int _port;
  readonly MtgoConnection _conn;
  readonly int _perJobTimeoutSec;
  // (user, intended[(name,qty)], commit, yesTimeoutSec) => (ok, detail)
  readonly Func<string, List<(string name, int qty)>, bool, int, (bool ok, string detail)> _lendFn;
  // (user, card, qty, commit, yesTimeoutSec) => (ok, detail)  — receive qty of one card, give nothing
  readonly Func<string, string, int, bool, int, (bool ok, string detail)> _grabFn;
  readonly Action<string> _log;

  readonly ConcurrentDictionary<string, TradeJob> _jobs = new();
  readonly BlockingCollection<TradeJob> _queue = new(new ConcurrentQueue<TradeJob>());

  static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };

  public TradeService(string token, string bind, int port, MtgoConnection conn, int perJobTimeoutSec,
    Func<string, List<(string name, int qty)>, bool, int, (bool ok, string detail)> lendFn,
    Func<string, string, int, bool, int, (bool ok, string detail)> grabFn,
    Action<string> log)
  {
    _token = token; _bind = bind; _port = port; _conn = conn;
    _perJobTimeoutSec = perJobTimeoutSec; _lendFn = lendFn; _grabFn = grabFn; _log = log;
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
    _log("[serve] routes: GET /health | POST /request {user,cards[],qty,commit} | POST /deposit {user,cards[1],qty,commit} | GET /jobs/{id} | GET /jobs");

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

    // Auth: Bearer <token> on EVERY route (no anonymous access — this moves assets).
    string authHeader = req.Headers["Authorization"] ?? "";
    string presented = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
      ? authHeader.Substring("Bearer ".Length).Trim() : "";
    if (!TokenOk(presented)) { Write(ctx, 401, new { error = "unauthorized" }); return; }

    if (method == "GET" && path == "/health")
    {
      Write(ctx, 200, new { ok = !_conn.Reconnecting, custodian = _conn.Account,
        reconnecting = _conn.Reconnecting, queued = _queue.Count, jobs = _jobs.Count });
      return;
    }
    if (method == "GET" && path == "/jobs")
    {
      var list = _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(100).Select(Project).ToList();
      Write(ctx, 200, new { jobs = list });
      return;
    }
    if (method == "GET" && path.StartsWith("/jobs/"))
    {
      string id = path.Substring("/jobs/".Length);
      if (_jobs.TryGetValue(id, out var job)) Write(ctx, 200, Project(job));
      else Write(ctx, 404, new { error = "no such job", id });
      return;
    }
    if (method == "POST" && path == "/request") { HandleEnqueue(ctx, req, "request"); return; }  // give
    if (method == "POST" && path == "/deposit") { HandleEnqueue(ctx, req, "deposit"); return; }  // receive

    Write(ctx, 404, new { error = "not found", path, method });
  }

  // Parse {user, cards[], commit}, validate, and enqueue a job of `type`
  // ("request" = custodian gives to user; "deposit" = custodian receives from user).
  void HandleEnqueue(HttpListenerContext ctx, HttpListenerRequest req, string type)
  {
    string body;
    using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8)) body = r.ReadToEnd();
    RequestDto dto;
    try { dto = JsonSerializer.Deserialize<RequestDto>(body, JsonIn) ?? new RequestDto(); }
    catch (Exception ex) { Write(ctx, 400, new { error = "bad json", detail = ex.Message }); return; }

    string user = (dto.User ?? "").Trim();
    var cards = (dto.Cards ?? new()).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).ToList();
    int qty = dto.Qty ?? 1;
    if (user.Length == 0) { Write(ctx, 400, new { error = "user required" }); return; }
    if (cards.Count == 0) { Write(ctx, 400, new { error = "cards required (non-empty array)" }); return; }
    if (qty < 1) { Write(ctx, 400, new { error = "qty must be >= 1" }); return; }
    // v1 deposit is a single distinct card (reuses the tested grab flow); qty copies of it are fine.
    if (type == "deposit" && cards.Count != 1)
    { Write(ctx, 400, new { error = "deposit v1 takes exactly ONE distinct card (use qty for copies; multi-distinct deposit not yet supported)" }); return; }

    var job = new TradeJob { Id = NewId(), Type = type, User = user, Cards = cards, Commit = dto.Commit ?? false, Qty = qty };
    _jobs[job.Id] = job;
    _queue.Add(job);
    _log($"[serve] queued {job.Id}: {type} user={user} cards=[{string.Join(", ", cards)}] qty={qty} commit={job.Commit}");
    Write(ctx, 202, Project(job));
  }

  // Length-checked constant-time compare so the bearer token isn't leaked by timing.
  bool TokenOk(string presented)
  {
    if (presented.Length == 0 || presented.Length != _token.Length) return false;
    int diff = 0;
    for (int i = 0; i < _token.Length; i++) diff |= presented[i] ^ _token[i];
    return diff == 0;
  }

  void WorkerLoop()
  {
    foreach (var job in _queue.GetConsumingEnumerable())
    {
      job.State = JobState.Running;
      job.StartedAt = DateTime.UtcNow.ToString("o");
      _log($"[serve] running {job.Id} ({job.Type} user={job.User}) ...");
      _conn.EnsureHealthy();   // block here until MTGO is reachable again (survives a client restart)
      try
      {
        (bool ok, string detail) r;
        if (job.Type == "deposit")
        {
          // Receive qty of one card (validated single distinct at enqueue), give nothing.
          r = _grabFn(job.User, job.Cards[0], job.Qty, job.Commit, _perJobTimeoutSec);
        }
        else
        {
          // request (give): aggregate duplicate names, then multiply each by the job qty.
          var intended = job.Cards
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => (name: g.Key, qty: g.Count() * job.Qty))
            .ToList();
          r = _lendFn(job.User, intended, job.Commit, _perJobTimeoutSec);
        }
        job.State = r.ok ? JobState.Done : JobState.Failed;
        job.Detail = r.detail;
      }
      catch (Exception ex) { job.State = JobState.Failed; job.Detail = ex.Message; }
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
    id = j.Id, type = j.Type, user = j.User, cards = j.Cards, qty = j.Qty, commit = j.Commit,
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

  sealed class RequestDto
  {
    public string? User { get; set; }
    public List<string>? Cards { get; set; }
    public int? Qty { get; set; }        // copies of each card (default 1) — e.g. N event tickets
    public bool? Commit { get; set; }
  }
}
