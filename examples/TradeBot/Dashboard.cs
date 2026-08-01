/** @file
  The serve process's built-in operator dashboard (served at GET / by TradeService).

  Self-contained page (no external resources): compose a trade (give / receive / swap),
  watch the job queue + service log live, and see the vault balance (owned tix + top
  holdings). It talks to the SAME authed API DraftBot uses — /trade, /jobs, /log, /vault —
  holding the bearer token in the browser (localStorage) since the page shell itself is the
  only unauthenticated route (loopback). This replaces the old standalone `ui` order desk:
  one process, one queue, one UI that is also the API.
**/
namespace TradeBot;

public static class Dashboard
{
  public const string Html = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>TradeBot Vault</title>
<style>
:root{--bg:#0f1216;--panel:#171c22;--panel2:#0c0f13;--edge:#2a323c;--ink:#e6edf3;--mut:#8b98a5;--acc:#e0a458;--ok:#3fb950;--run:#58a6ff;--bad:#f85149;--warn:#d29922;--queued:#8b98a5}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,Segoe UI,Roboto,sans-serif}
header{padding:12px 20px;border-bottom:1px solid var(--edge);display:flex;align-items:center;gap:12px;flex-wrap:wrap}
header h1{font-size:16px;margin:0;letter-spacing:.3px;font-weight:600}
header h1 .g{color:var(--acc)}
.dot{width:9px;height:9px;border-radius:50%;background:var(--mut)}.dot.live{background:var(--ok);box-shadow:0 0 8px var(--ok)}.dot.bad{background:var(--bad);box-shadow:0 0 8px var(--bad)}
.spacer{flex:1}
.tokbar{display:flex;align-items:center;gap:6px}
.tokbar input{background:var(--panel2);border:1px solid var(--edge);color:var(--ink);border-radius:6px;padding:5px 8px;width:150px;font:12px ui-monospace,Consolas,monospace}
main{display:grid;grid-template-columns:380px 1fr;gap:16px;padding:16px;align-items:start;max-width:1200px}
.col{display:flex;flex-direction:column;gap:16px;min-width:0}
.card{background:var(--panel);border:1px solid var(--edge);border-radius:10px;padding:14px}
.card h2{font-size:12px;text-transform:uppercase;letter-spacing:.7px;color:var(--mut);margin:0 0 10px;font-weight:600}
label{display:block;font-size:12px;color:var(--mut);margin:8px 0 4px}
input[type=text],input[type=number]{background:var(--panel2);border:1px solid var(--edge);color:var(--ink);border-radius:7px;padding:7px 9px;width:100%;font:13px system-ui}
input[type=number]{width:64px}
.row{display:flex;gap:6px;align-items:center;margin:5px 0}.row input[type=text]{flex:1}
button{background:#20262e;color:var(--ink);border:1px solid var(--edge);border-radius:7px;padding:7px 12px;cursor:pointer;font-size:13px}
button:hover{border-color:var(--acc)}button.primary{background:var(--acc);color:#1a1206;border-color:var(--acc);font-weight:600}
button.mini{padding:2px 8px;font-size:12px}
.badge{font-size:11px;text-transform:uppercase;letter-spacing:.5px;padding:2px 7px;border-radius:5px;border:1px solid var(--edge);white-space:nowrap}
.badge.request{color:#7ee787;border-color:#2ea04366}.badge.deposit{color:#79c0ff;border-color:#1f6feb66}.badge.swap{color:#d2a8ff;border-color:#8957e566}
.status{display:inline-flex;align-items:center;gap:6px;font-size:12px;color:var(--mut)}
.sdot{width:8px;height:8px;border-radius:50%;background:var(--mut);flex:none}
.sdot.running{background:var(--run);box-shadow:0 0 6px var(--run);animation:pulse 1.2s infinite}.sdot.done{background:var(--ok)}.sdot.queued{background:var(--queued)}.sdot.failed{background:var(--bad)}
@keyframes pulse{50%{opacity:.35}}
.job{border:1px solid var(--edge);border-radius:8px;padding:10px;margin-bottom:8px}
.job .top{display:flex;align-items:center;gap:8px;justify-content:space-between;flex-wrap:wrap}
.job .who{font-weight:600}
.job .items{color:var(--mut);font-size:12px;margin-top:6px}.job .items b{color:var(--ink);font-weight:500}
.job .detail{color:var(--mut);font-size:12px;margin-top:4px;font-style:italic}
.job .t{color:var(--mut);font-size:11px;font-variant-numeric:tabular-nums}
#log{background:var(--panel2);border:1px solid var(--edge);border-radius:8px;padding:10px;height:220px;overflow:auto;font:12px/1.55 ui-monospace,Consolas,monospace;color:#c9d1d9;white-space:pre-wrap}
.muted{color:var(--mut);font-size:12px}
.vault{display:flex;align-items:baseline;gap:10px}
.vault .big{font-size:34px;font-weight:700;color:var(--acc);font-variant-numeric:tabular-nums;line-height:1}
.vault .unit{font-size:13px;color:var(--mut)}
.holdings{margin-top:12px;display:flex;flex-direction:column;gap:3px;max-height:180px;overflow:auto}
.holdings .h{display:flex;justify-content:space-between;font-size:12px;padding:3px 0;border-bottom:1px solid #ffffff08}
.holdings .h .q{color:var(--mut);font-variant-numeric:tabular-nums}
.dir{display:flex;align-items:center;justify-content:space-between;margin-top:12px}
.warn{display:none;background:#3d1f1f;border:1px solid var(--bad);color:#ffb4ae;border-radius:7px;padding:8px 10px;font-size:12px;margin:0 16px}
.commit-warn{color:var(--warn);font-size:11px;margin-top:6px;display:none}
@media(max-width:820px){main{grid-template-columns:1fr}}
</style></head><body>
<header>
  <h1>&#9670; MTGO TradeBot <span class="g">Vault</span></h1>
  <span class="dot" id="livedot"></span><span class="muted" id="livetext">connecting&hellip;</span>
  <span class="spacer"></span>
  <div class="tokbar"><span class="muted">token</span><input type="password" id="tok" placeholder="bearer&hellip;"><button class="mini" onclick="saveTok()">set</button></div>
</header>
<div class="warn" id="authwarn">Unauthorized &mdash; set the serve <b>--token</b> value above to talk to the API.</div>
<main>
  <div class="col">
    <section class="card">
      <h2>Vault &mdash; <span id="custodian">&hellip;</span></h2>
      <div class="vault"><span class="big" id="tix">&mdash;</span><span class="unit">event tickets</span></div>
      <div class="muted" id="distinct" style="margin-top:4px">&mdash;</div>
      <div class="holdings" id="holdings"></div>
    </section>
    <section class="card">
      <h2>Compose trade</h2>
      <label>MTGO partner</label>
      <input type="text" id="partner" placeholder="username">
      <label>Give <span class="muted">(cards / tix the bot hands over)</span></label><div id="give"></div>
      <button class="mini" onclick="addRow('give')">+ give</button>
      <label>Receive <span class="muted">(cards / tix the bot takes)</span></label><div id="receive"></div>
      <button class="mini" onclick="addRow('receive')">+ receive</button>
      <div class="dir">
        <span>Direction: <span id="dir" class="badge">&mdash;</span></span>
        <label style="display:flex;align-items:center;gap:6px;margin:0"><input type="checkbox" id="ocommit"> commit</label>
      </div>
      <div class="commit-warn" id="cwarn">&#9888; commit moves REAL assets on MTGO.</div>
      <button class="primary" style="width:100%;margin-top:10px" onclick="queue()">Queue trade</button>
    </section>
  </div>
  <div class="col">
    <section class="card">
      <h2>Jobs <span class="muted" id="qdepth"></span></h2>
      <div id="jobs"><div class="muted">no jobs yet &mdash; compose one on the left</div></div>
    </section>
    <section class="card">
      <h2>Service log</h2>
      <div id="log"></div>
    </section>
  </div>
</main>
<script>
function $(id){return document.getElementById(id);}
function tok(){return localStorage.getItem('tbtoken')||'';}
function saveTok(){localStorage.setItem('tbtoken',$('tok').value.trim());$('authwarn').style.display='none';tick();vault();}
async function api(path,opts){opts=opts||{};opts.headers=Object.assign({'Authorization':'Bearer '+tok()},opts.headers||{});
  const r=await fetch(path,opts);if(r.status===401)$('authwarn').style.display='block';return r;}
function el(t,a,k){const e=document.createElement(t);for(const x in(a||{}))e.setAttribute(x,a[x]);(k||[]).forEach(c=>e.append(c));return e;}
function addRow(kind,name,qty){const box=$(kind);const row=el('div',{class:'row'});
  const n=el('input',{type:'text',placeholder:'Card name or Event Ticket'});n.value=name||'';
  const q=el('input',{type:'number',min:'1'});q.value=qty||'1';
  const rm=el('button',{class:'mini'});rm.textContent='×';rm.onclick=()=>{row.remove();updateDir();};
  n.oninput=updateDir;row.append(n,q,rm);box.append(row);updateDir();}
function rows(kind){return[...$(kind).children].map(r=>{const i=r.querySelectorAll('input');
  return{name:i[0].value.trim(),qty:parseInt(i[1].value)||1};}).filter(x=>x.name);}
function updateDir(){const g=rows('give').length,r=rows('receive').length;const d=$('dir');let t='—',c='';
  if(g>0&&r===0){t='LEND (give)';c='request';}else if(g===0&&r>0){t='DEPOSIT (receive)';c='deposit';}else if(g>0&&r>0){t='SWAP';c='swap';}
  d.textContent=t;d.className='badge '+c;}
$('ocommit').addEventListener('change',e=>{$('cwarn').style.display=e.target.checked?'block':'none';});
async function queue(){const o={partner:$('partner').value.trim(),give:rows('give'),receive:rows('receive'),commit:$('ocommit').checked};
  if(!o.partner){alert('partner required');return;}
  if(o.give.length===0&&o.receive.length===0){alert('add at least one give or receive');return;}
  const r=await api('/trade',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(o)});
  let j={};try{j=await r.json();}catch(e){}
  if(!r.ok){alert('rejected: '+(j.error||('HTTP '+r.status)));return;}
  tick();}
function items(l){return(l&&l.length)?l.map(i=>i.qty+'× '+i.name).join(', '):'—';}
function ago(iso){if(!iso)return'';const s=Math.max(0,(Date.now()-new Date(iso).getTime())/1000);
  if(s<60)return Math.floor(s)+'s ago';if(s<3600)return Math.floor(s/60)+'m ago';return Math.floor(s/3600)+'h ago';}
async function tick(){try{
  const jr=await api('/jobs');if(!jr.ok)return;const {jobs}=await jr.json();const box=$('jobs');
  if(!jobs||jobs.length===0){box.innerHTML='<div class="muted">no jobs yet &mdash; compose one on the left</div>';}
  else{box.innerHTML='';jobs.forEach(o=>{const card=el('div',{class:'job'});
    card.innerHTML='<div class="top"><span class="who"><span class="badge '+o.type+'">'+o.type+'</span> '+esc(o.user)+'</span>'
      +'<span class="status"><span class="sdot '+o.state+'"></span>'+o.state+(o.commit?' · commit':' · dry')+'</span></div>'
      +'<div class="items">give <b>'+esc(items(o.give))+'</b> &nbsp; receive <b>'+esc(items(o.receive))+'</b></div>'
      +(o.detail?'<div class="detail">'+esc(o.detail)+'</div>':'')
      +'<div class="t">'+esc(o.id)+' · '+ago(o.createdAt)+'</div>';
    box.append(card);});}
  const lr=await api('/log');if(lr.ok){const lg=await lr.json();const log=$('log');
    const bottom=log.scrollTop+log.clientHeight>=log.scrollHeight-30;
    log.textContent=(lg.lines||[]).join('\n');if(bottom)log.scrollTop=log.scrollHeight;
    $('qdepth').textContent=lg.queued?('· '+lg.queued+' queued'):'';
    $('livedot').className='dot'+(lg.running?' live':'');$('livetext').textContent=lg.running?'trade running':'idle';}
}catch(e){$('livedot').className='dot bad';$('livetext').textContent='unreachable';}}
async function vault(){try{const r=await api('/vault');if(!r.ok)return;const v=await r.json();
  if(v.custodian)$('custodian').textContent=v.custodian;
  if(!v.available){$('tix').textContent='—';$('distinct').textContent=v.reason||'vault unavailable';$('holdings').innerHTML='';return;}
  $('tix').textContent=v.tix;$('distinct').textContent=(v.distinct||0)+' distinct items held';
  const h=$('holdings');h.innerHTML='';(v.top||[]).forEach(t=>{const d=el('div',{class:'h'});
    d.innerHTML='<span>'+esc(t.name)+'</span><span class="q">'+t.qty+'</span>';h.append(d);});
}catch(e){}}
function esc(s){return String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));}
$('tok').value=tok();
addRow('give');updateDir();
tick();vault();setInterval(tick,1500);setInterval(vault,15000);
</script></body></html>
""";
}
