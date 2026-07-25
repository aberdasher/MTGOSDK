/** @file
  Tiny local control panel for the manual live test.

  `worker` executes trade-order JSON files; this serves a small web UI (via the
  built-in HttpListener — no external deps) to COMPOSE those orders, TRIGGER the
  worker, and WATCH live status/log. It runs as your own foreground process (open
  the window, use the browser tab, Ctrl+C to stop) so nothing gets reaped in the
  background. The MTGO-side clicks (accept the invite, grab the cards) still happen
  in the MTGO client — this is the order desk + status board.

  Endpoints:  GET / (page) · GET /api/orders · GET /api/log
              POST /api/order (create) · POST /api/run · POST /api/clear
**/

using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace TradeBot;

public static class ControlUi
{
  static readonly object _logLock = new();
  static readonly List<string> _log = new();
  static Process? _worker;
  static string _dir = "";

  static string? FlagVal(string[] args, string prefix) =>
    args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length);

  static void Log(string line)
  {
    lock (_logLock)
    {
      _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
      if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
    }
    Console.WriteLine(line);
  }

  public static void Run(string[] args)
  {
    _dir = FlagVal(args, "--dir=") ?? OrderQueue.DefaultDir;
    int port = int.TryParse(FlagVal(args, "--port="), out var p) ? p : 5577;
    Directory.CreateDirectory(_dir);
    string url = $"http://localhost:{port}/";

    var listener = new HttpListener();
    listener.Prefixes.Add(url);
    try { listener.Start(); }
    catch (Exception ex) { Console.WriteLine($"Could not start UI on {url}: {ex.Message}"); return; }

    Console.WriteLine($"\nTradeBot Control UI  ->  {url}");
    Console.WriteLine($"Orders folder: {_dir}");
    Console.WriteLine("Leave this window open; press Ctrl+C to stop.\n");
    Log("UI started.");
    if (!args.Any(a => a.Equals("--nobrowser", StringComparison.OrdinalIgnoreCase)))
      try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }

    while (true)
    {
      HttpListenerContext ctx;
      try { ctx = listener.GetContext(); } catch { break; }
      try { Handle(ctx); }
      catch (Exception ex) { try { Send(ctx, 500, "text/plain", ex.Message); } catch { } }
    }
  }

  static void Handle(HttpListenerContext ctx)
  {
    var req = ctx.Request;
    string path = req.Url?.AbsolutePath ?? "/";
    string method = req.HttpMethod;

    if (method == "GET" && path == "/") { Send(ctx, 200, "text/html; charset=utf-8", Html); return; }

    if (method == "GET" && path == "/api/orders") { Send(ctx, 200, "application/json", OrderQueue.AllJson(_dir)); return; }

    if (method == "GET" && path == "/api/log")
    {
      bool running = _worker is { HasExited: false };
      string[] lines; lock (_logLock) lines = _log.ToArray();
      Send(ctx, 200, "application/json", JsonSerializer.Serialize(new { running, lines }));
      return;
    }

    if (method == "POST" && path == "/api/order")
    {
      var o = OrderQueue.Parse(ReadBody(req));
      if (o is null) { Send(ctx, 400, "application/json", "{\"error\":\"bad order json\"}"); return; }
      OrderQueue.SaveOrder(_dir, o);
      Log($"queued order {o.Id}  ({o.MtgoPartner}: give {o.Give.Count}, receive {o.Receive.Count}, commit={o.Commit})");
      Send(ctx, 200, "application/json", $"{{\"ok\":true,\"id\":\"{o.Id}\"}}");
      return;
    }

    if (method == "POST" && path == "/api/run")
    {
      bool commit = ReadBody(req).Contains("\"commit\":true");
      RunWorker(commit);
      Send(ctx, 200, "application/json", "{\"ok\":true}");
      return;
    }

    if (method == "POST" && path == "/api/clear")
    {
      int n = OrderQueue.ClearDone(_dir);
      Log($"cleared {n} finished order(s).");
      Send(ctx, 200, "application/json", $"{{\"ok\":true,\"cleared\":{n}}}");
      return;
    }

    Send(ctx, 404, "text/plain", "not found");
  }

  static void RunWorker(bool commit)
  {
    if (_worker is { HasExited: false }) { Log("worker already running — ignoring Run."); return; }
    string dotnet = Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
    string dll = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
    var psi = new ProcessStartInfo
    {
      FileName = dotnet,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    psi.ArgumentList.Add(dll);
    psi.ArgumentList.Add("worker");
    psi.ArgumentList.Add("--yes");
    psi.ArgumentList.Add("--attach-only");
    psi.ArgumentList.Add($"--dir={_dir}");
    if (commit) psi.ArgumentList.Add("--commit");

    Log($"▶ running worker ({(commit ? "COMMIT" : "dry-run")}) ...");
    try
    {
      _worker = new Process { StartInfo = psi, EnableRaisingEvents = true };
      _worker.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
      _worker.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log(e.Data); };
      _worker.Exited += (_, __) => Log("■ worker exited.");
      _worker.Start();
      _worker.BeginOutputReadLine();
      _worker.BeginErrorReadLine();
    }
    catch (Exception ex) { Log($"failed to start worker: {ex.Message}"); }
  }

  static string ReadBody(HttpListenerRequest req)
  {
    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
    return r.ReadToEnd();
  }

  static void Send(HttpListenerContext ctx, int code, string contentType, string body)
  {
    var bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = code;
    ctx.Response.ContentType = contentType;
    ctx.Response.ContentLength64 = bytes.Length;
    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    ctx.Response.OutputStream.Close();
  }

  // ── Self-contained page (no external resources). ──────────────────────────────
  const string Html = """
<!doctype html><html><head><meta charset="utf-8"><title>TradeBot Control</title>
<style>
:root{--bg:#0f1216;--panel:#171c22;--edge:#2a323c;--ink:#e6edf3;--mut:#8b98a5;--acc:#e0a458;--ok:#3fb950;--run:#58a6ff;--bad:#f85149;--warn:#d29922}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,Segoe UI,Roboto,sans-serif}
header{padding:14px 20px;border-bottom:1px solid var(--edge);display:flex;align-items:center;gap:10px}
header h1{font-size:16px;margin:0;letter-spacing:.3px}
.dot{width:9px;height:9px;border-radius:50%;background:var(--mut)}.dot.live{background:var(--ok);box-shadow:0 0 8px var(--ok)}
main{display:grid;grid-template-columns:360px 1fr;gap:16px;padding:16px;align-items:start}
.card{background:var(--panel);border:1px solid var(--edge);border-radius:10px;padding:14px}
.card h2{font-size:13px;text-transform:uppercase;letter-spacing:.6px;color:var(--mut);margin:0 0 10px}
label{display:block;font-size:12px;color:var(--mut);margin:8px 0 4px}
input[type=text],input[type=number]{background:#0c0f13;border:1px solid var(--edge);color:var(--ink);border-radius:7px;padding:7px 9px;width:100%}
input[type=number]{width:64px}
.row{display:flex;gap:6px;align-items:center;margin:5px 0}.row input[type=text]{flex:1}
button{background:#20262e;color:var(--ink);border:1px solid var(--edge);border-radius:7px;padding:7px 12px;cursor:pointer;font-size:13px}
button:hover{border-color:var(--acc)}button.primary{background:var(--acc);color:#1a1206;border-color:var(--acc);font-weight:600}
button.mini{padding:2px 8px;font-size:12px}
.badge{font-size:11px;text-transform:uppercase;letter-spacing:.5px;padding:2px 7px;border-radius:5px;border:1px solid var(--edge)}
.badge.lend{color:#7ee787;border-color:#2ea04366}.badge.grab{color:#79c0ff;border-color:#1f6feb66}.badge.swap{color:#d2a8ff;border-color:#8957e566}
.status{display:inline-flex;align-items:center;gap:6px;font-size:12px}
.sdot{width:8px;height:8px;border-radius:50%;background:var(--mut)}
.sdot.running{background:var(--run);box-shadow:0 0 6px var(--run)}.sdot.completed{background:var(--ok)}.sdot.completed_dryrun{background:var(--ok)}.sdot.cancelled{background:var(--warn)}.sdot.failed{background:var(--bad)}.sdot.unsupported{background:var(--bad)}
.order{border:1px solid var(--edge);border-radius:8px;padding:10px;margin-bottom:8px}
.order .top{display:flex;align-items:center;gap:8px;justify-content:space-between}
.order .items{color:var(--mut);font-size:12px;margin-top:6px}.order .items b{color:var(--ink);font-weight:500}
.toolbar{display:flex;align-items:center;gap:10px;margin-bottom:10px}
#log{background:#0a0d10;border:1px solid var(--edge);border-radius:8px;padding:10px;height:300px;overflow:auto;font:12px/1.5 ui-monospace,Consolas,monospace;color:#c9d1d9;white-space:pre-wrap}
.muted{color:var(--mut);font-size:12px}
</style></head><body>
<header><h1>&#9670; MTGO TradeBot &mdash; Control</h1><span class="dot" id="livedot"></span><span class="muted" id="livetext">idle</span></header>
<main>
<section class="card">
  <h2>New order</h2>
  <label>MTGO partner</label>
  <input type="text" id="partner" placeholder="Aberdasher" value="Aberdasher">
  <label>Give <span class="muted">(cards / tix we hand over)</span></label><div id="give"></div>
  <button class="mini" onclick="addRow('give')">+ give</button>
  <label>Receive <span class="muted">(cards / tix we take)</span></label><div id="receive"></div>
  <button class="mini" onclick="addRow('receive')">+ receive</button>
  <div class="row" style="margin-top:12px;justify-content:space-between">
    <span>Direction: <span id="dir" class="badge">&mdash;</span></span>
    <label style="display:flex;align-items:center;gap:6px;margin:0"><input type="checkbox" id="ocommit"> commit</label>
  </div>
  <button class="primary" style="width:100%;margin-top:10px" onclick="queue()">Queue order</button>
</section>
<section class="card">
  <div class="toolbar"><h2 style="margin:0;flex:1">Queue</h2>
    <label style="display:flex;align-items:center;gap:6px;margin:0"><input type="checkbox" id="runcommit"> commit</label>
    <button class="primary" onclick="run()">&#9654; Run pending</button>
    <button onclick="clearDone()">Clear done</button></div>
  <div id="orders"><div class="muted">no orders yet</div></div>
  <h2 style="margin-top:16px">Worker log</h2><div id="log"></div>
</section>
</main>
<script>
function el(t,a,k){const e=document.createElement(t);for(const x in(a||{}))e.setAttribute(x,a[x]);(k||[]).forEach(c=>e.append(c));return e;}
function addRow(kind,name,qty){const box=document.getElementById(kind);const row=el('div',{class:'row'});
  const n=el('input',{type:'text',placeholder:'Card name or Event Ticket'});n.value=name||'';const q=el('input',{type:'number',min:'1'});q.value=qty||'1';
  const rm=el('button',{class:'mini'});rm.textContent='×';rm.onclick=()=>{row.remove();updateDir();};n.oninput=updateDir;
  row.append(n,q,rm);box.append(row);updateDir();}
function rows(kind){return[...document.getElementById(kind).children].map(r=>{const i=r.querySelectorAll('input');return{name:i[0].value.trim(),qty:parseInt(i[1].value)||1};}).filter(x=>x.name);}
function updateDir(){const g=rows('give').length,r=rows('receive').length;const d=document.getElementById('dir');let t='—',c='';
  if(g>0&&r===0){t='LEND';c='lend';}else if(g===0&&r>0){t='GRAB';c='grab';}else if(g>0&&r>0){t='SWAP';c='swap';}d.textContent=t;d.className='badge '+c;}
async function queue(){const o={mtgoPartner:document.getElementById('partner').value.trim(),give:rows('give'),receive:rows('receive'),commit:document.getElementById('ocommit').checked,listen:false};
  if(!o.mtgoPartner){alert('partner required');return;}if(o.give.length===0&&o.receive.length===0){alert('add at least one give or receive');return;}
  await fetch('/api/order',{method:'POST',body:JSON.stringify(o)});refresh();}
async function run(){await fetch('/api/run',{method:'POST',body:JSON.stringify({commit:document.getElementById('runcommit').checked})});}
async function clearDone(){await fetch('/api/clear',{method:'POST'});refresh();}
function items(l){return l.map(i=>i.qty+'× '+i.name).join(', ')||'—';}
function dirOf(o){const g=o.give.length,r=o.receive.length;if(g>0&&r===0)return['LEND','lend'];if(g===0&&r>0)return['GRAB','grab'];if(g>0&&r>0)return['SWAP','swap'];return['—',''];}
async function refresh(){try{
  const os=await(await fetch('/api/orders')).json();const box=document.getElementById('orders');box.innerHTML='';
  if(os.length===0)box.innerHTML='<div class="muted">no orders yet &mdash; compose one on the left</div>';
  os.forEach(o=>{const[dt,dc]=dirOf(o);const card=el('div',{class:'order'});
    card.innerHTML='<div class="top"><span><span class="badge '+dc+'">'+dt+'</span> <b>'+o.id+'</b> <span class="muted">&rarr; '+o.partner+'</span></span>'
      +'<span class="status"><span class="sdot '+o.status+'"></span>'+o.status+(o.commit?' · commit':'')+'</span></div>'
      +'<div class="items">give <b>'+items(o.give)+'</b> &nbsp; receive <b>'+items(o.receive)+'</b>'+(o.detail?'<br><span class="muted">'+o.detail+'</span>':'')+'</div>';
    box.append(card);});
  const lg=await(await fetch('/api/log')).json();const log=document.getElementById('log');
  const bottom=log.scrollTop+log.clientHeight>=log.scrollHeight-30;log.textContent=lg.lines.join('\n');if(bottom)log.scrollTop=log.scrollHeight;
  document.getElementById('livedot').className='dot'+(lg.running?' live':'');document.getElementById('livetext').textContent=lg.running?'worker running':'idle';
}catch(e){}}
addRow('give');addRow('receive');updateDir();refresh();setInterval(refresh,1500);
</script></body></html>
""";
}
