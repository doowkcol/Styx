// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//
// StyxWebDashboard -- the Styx web dashboard (web layer Phase 4).
//
// A self-contained operator dashboard served by the vanilla TFP web server,
// no Harmony, no external files. Demonstrates the whole web layer working
// together:
//   GET  /styx/dashboard            -- the HTML/CSS/JS page (guest-viewable)
//   SSE  /sse/?events=dashboard     -- a rich live snapshot pushed every ~1.5s
//   POST /styx/command|say|kick     -- admin actions (served by StyxWebAdmin)
//
// The page (single-quoted JS, served as text/html via a StyxWebResponse the
// plugin builds directly) opens the SSE feed and live-renders: server status,
// in-game day/time, blood-moon timer, threat counts with a DS-variant
// breakdown, a player table with per-player Kick/Say buttons, an x/z player
// map, and a console-command box. Action buttons require admin auth (the POST
// routes are admin-gated); a guest can watch but not act.
//
// Pure plugin -- uses only the already-shipped Styx.Web API + game types, so it
// hot-reloads with no framework rebuild. Open http://<server>:8080/styx/dashboard

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxWebDashboard", "Doowkcol", "0.1.0")]
public class StyxWebDashboard : StyxPlugin
{
    public override string Description => "Live operator dashboard at /styx/dashboard (web layer Phase 4)";

    private StyxWebChannel _feed;
    private TimerHandle _tick;

    // entity_class name -> friendly label, for the threat breakdown. Case-
    // insensitive (entityClassName casing isn't guaranteed).
    private static readonly Dictionary<string, string> DsVariants =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "zombieDsBehemoth",        "Behemoth"  },
        { "zombieDsDemon",           "Demon"     },
        { "dsHellhound",             "Hellhound" },
        { "entityDsKamikazeVulture", "Kamikaze"  },
        { "zombieDsSpecter",         "Specter"   },
        { "zombieDsWraith",          "Wraith"    },
        { "zombieDsCunning",         "Cunning"   },
        { "zombieDsPyre",            "Pyre"      },
        { "zombieDsGrenadier",       "Grenadier" },
    };

    public override void OnLoad()
    {
        // The page itself (guest-viewable; live data + actions are gated by their
        // own endpoints).
        Styx.Web.MapGet(this, "dashboard", StyxWebPerm.Guest,
            req => new StyxWebResponse { ContentType = "text/html; charset=utf-8", Body = Page });

        // Rich live snapshot, pushed every 1.5s on the "dashboard" SSE channel.
        _feed = Styx.Web.Channel(this, "dashboard");
        _tick = Scheduler.Every(1.5, () => { try { _feed.Push(BuildSnapshot()); } catch { } },
            name: "StyxWebDashboard.feed");

        Log.Out("[StyxWebDashboard] Loaded -- http://<server>:8080/styx/dashboard");
    }

    public override void OnUnload()
    {
        _tick?.Destroy(); _tick = null;
    }

    private object BuildSnapshot()
    {
        var world = GameManager.Instance?.World;

        int day = StyxCore.World?.CurrentDay ?? 0;
        string time = "--:--";
        if (world != null)
        {
            var (_, h, m) = GameUtils.WorldTimeToElements(world.worldTime);
            time = h.ToString("00") + ":" + m.ToString("00");
        }
        bool bm = StyxCore.World?.IsBloodMoon ?? false;

        int freq = 7;
        try { int f = GamePrefs.GetInt(EnumGamePrefs.BloodMoonFrequency); if (f > 0) freq = f; } catch { }
        int into = freq > 0 ? day % freq : 0;
        int bmIn = (into == 0) ? 0 : freq - into;

        var players = new List<object>();
        var pl = StyxCore.Player?.All();
        if (pl != null)
            foreach (var p in pl)
            {
                if (p == null) continue;
                var pos = p.position;
                players.Add(new
                {
                    name = p.EntityName, id = p.entityId, hp = (int)p.Health,
                    x = (int)pos.x, z = (int)pos.z,
                });
            }

        int total = 0;
        var variants = new Dictionary<string, int>();
        if (world?.Entities?.list != null)
        {
            var list = world.Entities.list;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is EntityEnemy en) || en.IsDead()) continue;
                total++;
                EntityClass ec = EntityClass.list.ContainsKey(en.entityClass) ? EntityClass.list[en.entityClass] : null;
                string cn = ec?.entityClassName;
                if (cn != null && DsVariants.TryGetValue(cn, out var disp))
                    variants[disp] = (variants.TryGetValue(disp, out var c) ? c : 0) + 1;
            }
        }

        return new
        {
            server = "Styx", version = StyxCore.Version,
            day, time, bloodMoon = bm, bloodMoonInDays = bmIn,
            playerCount = players.Count, players,
            threats = new { total, variants },
        };
    }

    // ====================================================================
    // The dashboard page. Single-quoted HTML/JS so the C# verbatim string
    // needs no escaping. Talks only to the Styx web endpoints on same-origin.
    // ====================================================================
    private const string Page = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Styx Dashboard</title>
<style>
  :root{ --bg:#0d1014; --panel:#161b22; --line:#2a313a; --txt:#d6dde6; --dim:#8b96a4; --accent:#4fd6a0; --bad:#e5534b; --warn:#e3b341; }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.4 'Segoe UI',system-ui,sans-serif}
  header{display:flex;align-items:center;gap:18px;padding:12px 18px;background:var(--panel);border-bottom:1px solid var(--line)}
  header h1{font-size:18px;margin:0;color:var(--accent);letter-spacing:1px}
  .navlink{color:var(--dim);text-decoration:none;padding:6px 11px;border-radius:6px;font-size:13px}
  .navlink:hover{color:var(--txt);background:#1f2630}
  .navlink.active{color:var(--accent);background:#1f2630}
  header .stat{color:var(--dim)} header .stat b{color:var(--txt)}
  .badge{padding:3px 9px;border-radius:10px;font-size:12px;font-weight:600}
  .bm-on{background:var(--bad);color:#fff} .bm-off{background:#223;color:var(--dim)}
  main{display:grid;grid-template-columns:1fr 1fr;gap:14px;padding:14px;max-width:1200px}
  .panel{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px}
  .panel h2{margin:0 0 10px;font-size:13px;text-transform:uppercase;letter-spacing:1px;color:var(--dim)}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}
  th{color:var(--dim);font-weight:600}
  .hpbar{height:7px;background:#2a313a;border-radius:4px;overflow:hidden;width:70px}
  .hpbar i{display:block;height:100%;background:var(--accent)}
  .chip{display:inline-block;background:#1f2630;border:1px solid var(--line);border-radius:12px;padding:3px 9px;margin:2px;font-size:12px}
  .chip b{color:var(--warn)}
  button{background:#1f2630;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:4px 9px;cursor:pointer;font-size:12px}
  button:hover{border-color:var(--accent);color:var(--accent)}
  #map{width:100%;height:320px;background:#0a0d11;border-radius:6px;display:block}
  input{background:#0a0d11;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:7px 9px;width:100%}
  pre{background:#0a0d11;border:1px solid var(--line);border-radius:6px;padding:10px;max-height:180px;overflow:auto;color:var(--dim);font-size:12px;white-space:pre-wrap}
  .row{display:flex;gap:8px;margin-top:8px}
  .muted{color:var(--dim);font-size:12px}
</style>
</head>
<body>
<header>
  <h1>STYX</h1>
  <a class='navlink active' href='/styx/dashboard'>Dashboard</a>
  <a class='navlink' href='/styx/map'>Map</a>
  <a class='navlink' href='/styx/perms'>Permissions</a>
  <a class='navlink' href='/styx/configs'>Configs</a>
  <span class='stat'>v<b id='ver'>-</b></span>
  <span class='stat'>Day <b id='day'>-</b></span>
  <span class='stat'>Time <b id='time'>--:--</b></span>
  <span class='stat'>Players <b id='pcount'>0</b></span>
  <span class='stat'>Threats <b id='tcount'>0</b></span>
  <span id='bm' class='badge bm-off'>Blood Moon: off</span>
  <span class='stat' id='conn' style='margin-left:auto;color:var(--bad)'>connecting...</span>
</header>
<main>
  <section class='panel'>
    <h2>Players</h2>
    <table><thead><tr><th>Name</th><th>HP</th><th>Pos (x,z)</th><th></th></tr></thead>
      <tbody id='players'></tbody></table>
    <p class='muted' id='noplayers'>No players online.</p>
  </section>
  <section class='panel'>
    <h2>Threats</h2>
    <div id='threats'><span class='muted'>none</span></div>
    <h2 style='margin-top:16px'>Map (x / z)</h2>
    <canvas id='map'></canvas>
  </section>
  <section class='panel' style='grid-column:1 / -1'>
    <h2>Console</h2>
    <div class='row'>
      <input id='cmd' placeholder='console command (e.g. version) -- admin only' onkeydown='if(event.key===&apos;Enter&apos;)runCmd()'>
      <button onclick='runCmd()'>Run</button>
      <input id='say' placeholder='say to server chat...' onkeydown='if(event.key===&apos;Enter&apos;)say()' style='max-width:280px'>
      <button onclick='say()'>Say</button>
    </div>
    <pre id='out'>Ready. Actions require an admin dashboard session.</pre>
  </section>
</main>
<script>
const $ = id => document.getElementById(id);
let last = null;

function render(d){
  last = d;
  $('ver').textContent = d.version; $('day').textContent = d.day;
  $('time').textContent = d.time; $('pcount').textContent = d.playerCount;
  $('tcount').textContent = d.threats.total;
  const bm = $('bm');
  if(d.bloodMoon){ bm.className='badge bm-on'; bm.textContent='BLOOD MOON ACTIVE'; }
  else { bm.className='badge bm-off'; bm.textContent = d.bloodMoonInDays===0 ? 'Blood Moon: tonight' : 'Blood Moon in '+d.bloodMoonInDays+'d'; }

  const tb = $('players'); tb.innerHTML='';
  $('noplayers').style.display = d.players.length ? 'none' : 'block';
  for(const p of d.players){
    const tr = document.createElement('tr');
    const hpw = Math.max(0, Math.min(100, p.hp));
    tr.innerHTML = '<td>'+esc(p.name)+'</td>'
      + '<td><div class=hpbar><i style=width:'+hpw+'%></i></div><span class=muted>'+p.hp+'</span></td>'
      + '<td class=muted>'+p.x+', '+p.z+'</td>'
      + '<td><button>Kick</button></td>';
    tr.querySelector('button').onclick = ()=>kick(p.name);
    tb.appendChild(tr);
  }

  const th = $('threats');
  const v = d.threats.variants || {};
  const keys = Object.keys(v);
  th.innerHTML = '<span class=chip>Total <b>'+d.threats.total+'</b></span>'
    + keys.map(k=>'<span class=chip>'+esc(k)+' <b>'+v[k]+'</b></span>').join('');
  drawMap(d.players);
}

function drawMap(players){
  const c = $('map'); const ctx = c.getContext('2d');
  c.width = c.clientWidth; c.height = c.clientHeight;
  ctx.fillStyle = '#0a0d11'; ctx.fillRect(0,0,c.width,c.height);
  // grid so the box reads as a map
  ctx.strokeStyle='#141a22'; ctx.lineWidth=1;
  for(let gx=0; gx<=c.width; gx+=40){ ctx.beginPath(); ctx.moveTo(gx,0); ctx.lineTo(gx,c.height); ctx.stroke(); }
  for(let gy=0; gy<=c.height; gy+=40){ ctx.beginPath(); ctx.moveTo(0,gy); ctx.lineTo(c.width,gy); ctx.stroke(); }
  if(!players.length){ ctx.fillStyle='#586573'; ctx.font='12px sans-serif'; ctx.fillText('no players online', 12, 20); return; }
  // Center the view on the players' centroid, with a minimum span so a single
  // player sits in the MIDDLE (not a corner) and a sensible zoom out to ~150m.
  let minx=1e9,maxx=-1e9,minz=1e9,maxz=-1e9;
  for(const p of players){ minx=Math.min(minx,p.x);maxx=Math.max(maxx,p.x);minz=Math.min(minz,p.z);maxz=Math.max(maxz,p.z); }
  const cx=(minx+maxx)/2, cz=(minz+maxz)/2;
  const span=Math.max(maxx-minx, maxz-minz, 150)*1.25;
  const pad=28;
  const fx=x=> pad + (x-(cx-span/2))/span*(c.width-2*pad);
  const fz=z=> c.height - (pad + (z-(cz-span/2))/span*(c.height-2*pad));
  // centre crosshair
  ctx.strokeStyle='#223'; ctx.beginPath(); ctx.moveTo(fx(cx),0); ctx.lineTo(fx(cx),c.height);
  ctx.moveTo(0,fz(cz)); ctx.lineTo(c.width,fz(cz)); ctx.stroke();
  for(const p of players){
    const x=fx(p.x), y=fz(p.z);
    ctx.fillStyle='#4fd6a0'; ctx.beginPath(); ctx.arc(x,y,6,0,7); ctx.fill();
    ctx.fillStyle='#0a0d11'; ctx.beginPath(); ctx.arc(x,y,2,0,7); ctx.fill();
    ctx.fillStyle='#d6dde6'; ctx.font='11px sans-serif'; ctx.fillText(p.name+' ('+p.x+', '+p.z+')', x+9, y+4);
  }
}

function esc(s){ return String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }

async function post(path, body){
  try{
    const r = await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const j = await r.json().catch(()=>({}));
    if(r.status===403){ return {error:'403 -- admin session required'}; }
    return j;
  }catch(e){ return {error:String(e)}; }
}
async function runCmd(){
  const cmd = $('cmd').value.trim(); if(!cmd) return;
  $('out').textContent = '> '+cmd+'\n...';
  const j = await post('/styx/command',{command:cmd});
  $('out').textContent = '> '+cmd+'\n' + (j.output ? j.output.join('\n') : (j.error||JSON.stringify(j)));
}
async function say(){
  const m = $('say').value.trim(); if(!m) return;
  const j = await post('/styx/say',{message:m});
  $('out').textContent = j.error ? 'say: '+j.error : 'said: '+m;
  if(!j.error) $('say').value='';
}
async function kick(name){
  if(!confirm('Kick '+name+'?')) return;
  const j = await post('/styx/kick',{player:name,reason:'kicked from dashboard'});
  $('out').textContent = j.error ? 'kick: '+j.error : 'kicked '+name;
}

// Connect to the StyxLive SSE event; the 'dashboard' channel arrives as a
// named SSE event within that single stream (Styx multiplexes channels by name).
const es = new EventSource('/sse/?events=StyxLive');
es.addEventListener('dashboard', e=>{ $('conn').style.color='var(--accent)'; $('conn').textContent='live'; render(JSON.parse(e.data)); });
es.onerror = ()=>{ $('conn').style.color='var(--bad)'; $('conn').textContent='disconnected'; };
</script>
</body>
</html>";
}
