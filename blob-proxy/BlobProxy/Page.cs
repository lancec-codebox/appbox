namespace BlobProxy;

/// <summary>GET /: a dense page over /v1/stats (refreshes every 10 s). Values are set with textContent only.</summary>
static class Page
{
    public static string Html(string version) => """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>blob-proxy</title>
<style>
:root{--bg:#fff;--fg:#1a1a1a;--mute:#666;--line:#ddd;--head:#f4f4f4}
@media (prefers-color-scheme:dark){:root{--bg:#141414;--fg:#e6e6e6;--mute:#999;--line:#333;--head:#1f1f1f}}
body{background:var(--bg);color:var(--fg);font:13px/1.35 system-ui,sans-serif;margin:12px 16px}
h1{font-size:15px;margin:0 0 4px}h2{font-size:13px;margin:14px 0 4px}.mute{color:var(--mute)}
select{font:inherit}table{border-collapse:collapse;width:100%;max-width:1100px}th,td{border-bottom:1px solid var(--line);padding:2px 6px;text-align:right;white-space:nowrap}
th:first-child,td:first-child{text-align:left}th{background:var(--head);font-weight:600}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:4px 16px}
</style></head><body>
<h1>blob-proxy <span class="mute">__VERSION__</span></h1>
<div class="mute" id="meta"></div>
<label>Window <select id="win"><option value="last1m">last minute</option><option value="last5m" selected>last 5 minutes</option><option value="last60m">last hour</option><option value="sinceStart">since start</option></select></label>
<div class="grid"><div><h2>By client</h2><table id="byClient"></table></div><div><h2>By operation</h2><table id="byOp"></table></div>
<div><h2>By folder</h2><table id="byFolder"></table></div><div><h2>By store</h2><table id="byStore"></table></div></div>
<script>
const cols=["key","requests","bytesOut","bytesIn","avgMs","2xx","3xx","4xx","5xx","noResponse"],names=["","req","out","in","avg ms","2xx","3xx","4xx","5xx","none"];
const mb=b=>b>=1073741824?(b/1073741824).toFixed(2)+" GB":(b/1048576).toFixed(1)+" MB";
function fill(t,rows){t.replaceChildren();const h=t.insertRow();names.forEach(n=>{const th=document.createElement("th");th.textContent=n;h.appendChild(th)});
 for(const r of rows){const tr=t.insertRow();cols.forEach(c=>{const td=tr.insertCell();const v=r[c];td.textContent=(c==="bytesOut"||c==="bytesIn")?mb(v):String(v)})}}
let last=null;async function load(){try{const r=await fetch("v1/stats");last=await r.json();show()}catch(e){document.getElementById("meta").textContent="stats unavailable"}}
function show(){if(!last)return;const w=last[document.getElementById("win").value];document.getElementById("meta").textContent="started "+last.started+" · now "+last.now+" · bytes from Content-Length headers";
 for(const k of ["byClient","byOp","byFolder","byStore"])fill(document.getElementById(k),w[k])}
document.getElementById("win").onchange=show;load();setInterval(load,10000);
</script></body></html>
""".Replace("__VERSION__", version);
}
