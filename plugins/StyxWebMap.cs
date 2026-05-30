// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//
// StyxWebMap -- live world map tab for the Styx web dashboard (Phase 5b).
//
// A real terrain map with live overlays, served alongside the rest of the web
// layer. It rides the TFP web server's own rendered map tiles and layers Styx
// live data on top:
//
//   GET /styx/map               -- the Leaflet map page (user-gated)
//   SSE "map" channel           -- snapshot pushed ~every 1.5s
//
// Terrain comes from the vanilla TFP "Web Dashboard" mod, which already renders
// + serves the explored world as Leaflet tiles:
//   - tiles:  /map/{z}/{x}/{y}.png   (Y inverted: getTileUrl uses -y-1)
//   - config: /api/map/config -> {maxZoom, tileSize}
//   - custom CRS (extracted from the TFP front-end): a CRS.Simple variant whose
//     projection is  project(ll)=point(ll.lat/2^M, ll.lng/2^M),  M=maxZoom,
//     transformation (1,0,-1,0), scale z=>2^z. Net effect: L.latLng(x, z) maps
//     1:1 onto world block coords (lat = world X / East, lng = world Z / North),
//     so every overlay is just L.latLng(worldX, worldZ).
//
// Styx provides every LIVE overlay (TFP's map has none of these):
//   - players  : StyxCore.Player.All() -- marker + popup (Kick / Perms link)
//   - zombies  : world EntityEnemy positions, DS-variant coloured (count-capped)
//   - claims   : GetPersistentPlayerList().LPBlocks -- LCB squares + owner
//   - shields  : Styx.Shield.Snapshot() -- shielded LCBs highlighted
//
// Pure plugin -- only the shipped Styx.Web API + game types, so it hot-reloads
// with no framework rebuild. Needs the TFP web dashboard mod present (it is) and
// the viewing browser to reach a CDN for Leaflet. Open /styx/map (logged in).
//
// Phase 2 (planned): teleport actions, player movement trails/timeline, a
// configurable player-facing map, animal markers.

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxWebMap", "Doowkcol", "0.1.0")]
public class StyxWebMap : StyxPlugin
{
    public override string Description => "Live world map at /styx/map (terrain tiles + players/zombies/claims/shields)";

    // Hard cap on zombie markers pushed per snapshot. Blood moon can spawn
    // hundreds; we send the first N and flag the overflow so the UI can say so
    // (never silently truncate). Tunable if needed.
    private const int ZombieCap = 500;

    private StyxWebChannel _feed;
    private TimerHandle _tick;

    // entity_class name -> friendly DS label (same set the dashboard uses).
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
        Styx.Web.MapGet(this, "map", StyxWebPerm.User,
            req => new StyxWebResponse { ContentType = "text/html; charset=utf-8", Body = Page });

        _feed = Styx.Web.Channel(this, "map");
        _tick = Scheduler.Every(1.5, () => { try { _feed.Push(BuildSnapshot()); } catch { } },
            name: "StyxWebMap.feed");

        Log.Out("[StyxWebMap] Loaded -- http://<server>:8080/styx/map");
    }

    public override void OnUnload()
    {
        _tick?.Destroy(); _tick = null;
    }

    // ====================================================================
    // Snapshot
    // ====================================================================

    private object BuildSnapshot()
    {
        var world = GameManager.Instance?.World;

        // ---- players ----
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

        // ---- zombies (capped) ----
        var zombies = new List<object>();
        int zTotal = 0;
        bool capped = false;
        if (world?.Entities?.list != null)
        {
            var list = world.Entities.list;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is EntityEnemy en) || en.IsDead()) continue;
                zTotal++;
                if (zombies.Count >= ZombieCap) { capped = true; continue; }
                var pos = en.position;
                string v = null;
                EntityClass ec = EntityClass.list.ContainsKey(en.entityClass) ? EntityClass.list[en.entityClass] : null;
                string cn = ec?.entityClassName;
                if (cn != null && DsVariants.TryGetValue(cn, out var disp)) v = disp;
                zombies.Add(new { x = (int)pos.x, z = (int)pos.z, v });
            }
        }

        // ---- claims + shields ----
        int half = ResolveHalfSize();
        var shielded = new HashSet<string>();
        try
        {
            foreach (var z in Styx.Shield.Snapshot())
                shielded.Add(z.Center.x + "," + z.Center.z);
        }
        catch { /* shield framework absent/empty -- claims still render */ }

        var claims = new List<object>();
        var ppl = GameManager.Instance?.GetPersistentPlayerList();
        if (ppl?.Players != null)
            foreach (var kv in ppl.Players)
            {
                var ppd = kv.Value;
                if (ppd?.LPBlocks == null || ppd.LPBlocks.Count == 0) continue;
                string owner = ppd.PlayerName?.SafeDisplayName;
                if (string.IsNullOrEmpty(owner)) owner = ppd.PrimaryId?.CombinedString ?? "?";
                foreach (var c in ppd.LPBlocks)
                    claims.Add(new
                    {
                        x = c.x, z = c.z, half, owner,
                        shielded = shielded.Contains(c.x + "," + c.z),
                    });
            }

        // World extent (blocks) so the client can frame the whole map + clamp panning.
        int wMinX = 0, wMinZ = 0, wMaxX = 0, wMaxZ = 0;
        try
        {
            if (world != null && world.GetWorldExtent(out var wmin, out var wmax))
            {
                wMinX = wmin.x; wMinZ = wmin.z; wMaxX = wmax.x; wMaxZ = wmax.z;
            }
        }
        catch { }

        return new
        {
            players,
            zombies, zShown = zombies.Count, zTotal, capped,
            claims,
            bounds = new { minX = wMinX, minZ = wMinZ, maxX = wMaxX, maxZ = wMaxZ },
        };
    }

    private static int ResolveHalfSize()
    {
        int radius = GameStats.GetInt(EnumGameStats.LandClaimSize);
        if (radius <= 0) radius = 41;
        return (radius - 1) / 2;
    }

    // ====================================================================
    // Page. Single-quoted HTML/JS in a C# verbatim string (no escaping).
    // Leaflet from CDN; talks only to same-origin TFP tiles + Styx SSE.
    // ====================================================================
    private const string Page = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Styx Map</title>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<style>
  :root{ --bg:#0d1014; --panel:#161b22; --line:#2a313a; --txt:#d6dde6; --dim:#8b96a4; --accent:#4fd6a0; --bad:#e5534b; --warn:#e3b341; }
  *{box-sizing:border-box}
  html,body{height:100%}
  body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.4 'Segoe UI',system-ui,sans-serif;overflow:hidden}
  header{display:flex;align-items:center;gap:10px;padding:12px 18px;background:var(--panel);border-bottom:1px solid var(--line);height:50px}
  header h1{font-size:18px;margin:0 12px 0 0;color:var(--accent);letter-spacing:1px}
  .navlink{color:var(--dim);text-decoration:none;padding:6px 11px;border-radius:6px;font-size:13px}
  .navlink:hover{color:var(--txt);background:#1f2630}
  .navlink.active{color:var(--accent);background:#1f2630}
  #conn{margin-left:auto;color:var(--dim);font-size:12px}
  #map{position:absolute;top:50px;left:0;right:0;bottom:0;background:#0a0d11}
  .leaflet-container{background:#0a0d11}
  .hud{position:absolute;top:62px;right:12px;z-index:1000;background:rgba(22,27,34,0.92);border:1px solid var(--line);border-radius:8px;padding:10px 12px;font-size:12px;min-width:170px}
  .hud h3{margin:0 0 6px;font-size:12px;text-transform:uppercase;letter-spacing:1px;color:var(--dim)}
  .hud .lg{display:flex;align-items:center;gap:7px;margin:3px 0}
  .dot{width:11px;height:11px;border-radius:50%;display:inline-block;border:1px solid rgba(255,255,255,.25)}
  .sq{width:12px;height:9px;display:inline-block;border-radius:2px}
  .hud .val{color:var(--txt)}
  .hud .btns{display:flex;gap:6px;margin-top:10px}
  .hud .btns button{flex:1;background:#1f2630;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:5px 8px;cursor:pointer;font-size:12px}
  .hud .btns button:hover{border-color:var(--accent);color:var(--accent)}
  #coord{position:absolute;left:12px;bottom:12px;z-index:1000;background:rgba(22,27,34,0.92);border:1px solid var(--line);border-radius:6px;padding:5px 9px;font-size:12px;color:var(--dim)}
  .pp b{color:var(--accent)} .pp{font-size:13px;line-height:1.5}
  .pp button{background:#1f2630;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:3px 9px;cursor:pointer;font-size:12px;margin-top:4px}
  .pp button:hover{border-color:var(--bad);color:var(--bad)}
  .pp a{color:var(--accent);margin-left:8px;font-size:12px}
  .err{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);z-index:1000;color:var(--dim);text-align:center;max-width:420px}
</style>
</head>
<body>
<header>
  <h1>STYX</h1>
  <a class='navlink' href='/styx/dashboard'>Dashboard</a>
  <a class='navlink active' href='/styx/map'>Map</a>
  <a class='navlink' href='/styx/perms'>Permissions</a>
  <a class='navlink' href='/styx/configs'>Configs</a>
  <span id='conn'>connecting...</span>
</header>
<div id='map'></div>
<div class='hud'>
  <h3>Legend</h3>
  <div class='lg'><span class='dot' style='background:#4fd6a0'></span> Player</div>
  <div class='lg'><span class='dot' style='background:#e5534b'></span> Zombie</div>
  <div class='lg'><span class='dot' style='background:#e3b341'></span> DS variant</div>
  <div class='lg'><span class='sq' style='background:#5a6b7a;opacity:.6'></span> Land claim</div>
  <div class='lg'><span class='sq' style='background:#4fd6a0;opacity:.6'></span> Shielded claim</div>
  <h3 style='margin-top:10px'>Live</h3>
  <div class='lg'>Players <span class='val' id='pc' style='margin-left:auto'>0</span></div>
  <div class='lg'>Zombies <span class='val' id='zc' style='margin-left:auto'>0</span></div>
  <div class='lg'>Claims <span class='val' id='cc' style='margin-left:auto'>0</span></div>
  <div class='lg' style='color:var(--dim);font-size:11px'>tiles <span id='cfgdbg' style='margin-left:auto'>-</span></div>
  <div class='btns'><button id='fitWorld'>Whole map</button><button id='fitSeen'>Explored</button></div>
  <div class='btns'><button id='refreshTiles'>Refresh tiles</button></div>
</div>
<div id='coord'>- E / - N</div>
<script>
const $ = id => document.getElementById(id);
function esc(s){ return String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }
function setConn(t,ok){ const e=$('conn'); e.textContent=t; e.style.color = ok ? 'var(--accent)' : 'var(--bad)'; }
async function post(path, body){
  try{
    const r = await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    if(r.status===403) return {error:'403 - admin session required'};
    return await r.json().catch(()=>({}));
  }catch(e){ return {error:String(e)}; }
}

let map, gClaims, gShields, gZombies, gPlayers, centered = false, lastData = null, worldBounds = null, worldRect = null, playerMarkers = {};

async function init(){
  if(typeof L === 'undefined'){ document.body.insertAdjacentHTML('beforeend',
    '<div class=err>Leaflet failed to load (no internet on this browser?). The map needs the Leaflet CDN.</div>'); return; }

  // Map config from the TFP web server. The envelope nesting isn't certain, so
  // probe a few levels and HARD-guard to finite numbers -- a NaN maxZoom here is
  // exactly what makes Leaflet throw 'Attempted to load an infinite number of
  // tiles'. The resolved values are shown in the HUD + console for verification.
  let M = 4, TS = 128, rawCfg = null;
  try{
    const r = await fetch('/api/map/config', { credentials: 'same-origin' });
    const txt = await r.text();
    try{ rawCfg = JSON.parse(txt); }catch(_){ rawCfg = txt.slice(0,160); }
    const cands = [rawCfg, rawCfg && rawCfg.data, rawCfg && rawCfg.data && rawCfg.data.data];
    for(const c of cands){
      if(c && typeof c === 'object'){
        if(Number.isFinite(+c.maxZoom))  M  = +c.maxZoom;
        if(Number.isFinite(+c.tileSize)) TS = +c.tileSize;
      }
    }
  }catch(e){}
  if(!Number.isFinite(M) || M < 0 || M > 12) M = 4;
  if(!Number.isFinite(TS) || TS <= 0) TS = 128;
  const pw = Math.pow(2, M);
  console.log('[styx-map] /api/map/config =>', rawCfg, ' resolved M=', M, ' TS=', TS);
  if($('cfgdbg')) $('cfgdbg').textContent = 'M=' + M + ' TS=' + TS;

  // Custom CRS (lifted verbatim from the TFP front-end): L.latLng(x,z) == world (x,z).
  const crs = L.extend({}, L.CRS.Simple, {
    projection: {
      project:   ll => L.point(ll.lat / pw, ll.lng / pw),
      unproject: p  => L.latLng(p.x * pw, p.y * pw)
    },
    transformation: new L.Transformation(1, 0, -1, 0),
    scale: z => Math.pow(2, z),
    zoom:  s => Math.log(s) / Math.LN2,
    infinite: true
  });

  map = L.map('map', { crs, center: [0,0], zoom: Math.max(0, M-2), attributionControl:false, zoomControl:true });

  const tileOpts = { maxZoom: M+1, minZoom: Math.max(0, M-5), maxNativeZoom: M, minNativeZoom: 0, tileSize: TS };
  function makeTiles(){
    const t = L.tileLayer('/map/{z}/{x}/{y}.png?t=' + (new Date).getTime(), tileOpts);
    // 7DTD tile Y is inverted relative to Leaflet's scheme.
    t.getTileUrl = function(coords){ coords.y = -coords.y - 1; return L.TileLayer.prototype.getTileUrl.call(this, coords); };
    return t;
  }
  let activeTiles = makeTiles().addTo(map);

  // Tiles render server-side as the world is explored; the layer would otherwise
  // stay frozen on page-load state (and keep serving cached 404s for areas that
  // have since rendered). Swap in a freshly cache-busted layer ON TOP of the old
  // one and only drop the old once the new has loaded -- seamless, no flicker.
  function refreshTiles(){
    const fresh = makeTiles();
    const swap = () => { if(activeTiles && activeTiles !== fresh){ map.removeLayer(activeTiles); } activeTiles = fresh; };
    fresh.on('load', swap);
    fresh.addTo(map);
    setTimeout(() => { if(map.hasLayer(fresh)) swap(); }, 5000); // safety net if 'load' never fires
  }
  $('refreshTiles').onclick = refreshTiles;
  setInterval(refreshTiles, 20000);

  gClaims  = L.layerGroup().addTo(map);
  gShields = L.layerGroup().addTo(map);
  gZombies = L.layerGroup().addTo(map);
  gPlayers = L.layerGroup().addTo(map);

  map.on('mousemove', e => { $('coord').textContent = Math.floor(e.latlng.lat) + ' E / ' + Math.floor(e.latlng.lng) + ' N'; });

  // Whole-map / explored framing buttons.
  $('fitWorld').onclick = () => {
    if(worldBounds) map.fitBounds(worldBounds);
    else if(lastData && lastData.players.length) map.setView([lastData.players[0].x, lastData.players[0].z], Math.max(0, M-3));
  };
  $('fitSeen').onclick = () => {
    const pts = [];
    if(lastData){
      for(const p of lastData.players) pts.push([p.x, p.z]);
      for(const c of lastData.claims)  pts.push([c.x, c.z]);
      for(const z of lastData.zombies) pts.push([z.x, z.z]);
    }
    if(pts.length) map.fitBounds(pts, { maxZoom: M, padding: [40,40] });
  };

  // Live overlays via the multiplexed StyxLive SSE feed ('map' channel).
  const es = new EventSource('/sse/?events=StyxLive');
  es.addEventListener('map', e => { setConn('live', true); try{ render(JSON.parse(e.data)); }catch(err){} });
  es.onerror = () => setConn('disconnected', false);
}

// Player markers are built ONCE and updated in place, so an open popup (with its
// Kick/Perms buttons) survives the 1.5s live refresh instead of being destroyed.
function makePlayerMarker(p){
  const marker = L.circleMarker([p.x, p.z], { radius: 6, color: '#cdeede', fillColor: '#4fd6a0', weight: 2, fillOpacity: 1 });
  marker.bindTooltip(p.name, { direction: 'top', offset: [0,-4] });
  const div = document.createElement('div'); div.className = 'pp';
  const title = document.createElement('b');
  const info = document.createElement('div');
  const row = document.createElement('div'); row.style.marginTop = '4px';
  const kick = document.createElement('button'); kick.textContent = 'Kick';
  const perms = document.createElement('a'); perms.textContent = 'Perms'; perms.href = '/styx/perms';
  row.appendChild(kick); row.appendChild(perms);
  div.appendChild(title); div.appendChild(info); div.appendChild(row);
  // autoClose/closeOnClick off so the live re-render + map clicks don't dismiss it.
  marker.bindPopup(div, { autoClose: false, closeOnClick: false });
  const pm = { marker, title, info, kick, data: p };
  kick.onclick = async () => {
    const name = pm.data.name;
    if(!confirm('Kick ' + name + '?')) return;
    const j = await post('/styx/kick', { player: name, reason: 'kicked from map' });
    kick.textContent = j.error ? 'failed' : 'kicked';
  };
  updatePlayerMarker(pm, p);
  return pm;
}
function updatePlayerMarker(pm, p){
  pm.data = p;
  pm.marker.setLatLng([p.x, p.z]);
  pm.marker.setTooltipContent(p.name);
  pm.title.textContent = p.name;
  pm.info.innerHTML = 'HP ' + p.hp + '<br>' + p.x + ', ' + p.z;
}

function render(d){
  lastData = d;
  gClaims.clearLayers(); gShields.clearLayers(); gZombies.clearLayers();

  // World extent: outline the whole map (visible even over unexplored void) and
  // clamp panning to it. Set once when the first valid extent arrives.
  if(d.bounds && d.bounds.maxX > d.bounds.minX){
    const wb = [[d.bounds.minX, d.bounds.minZ], [d.bounds.maxX, d.bounds.maxZ]];
    if(!worldBounds){
      worldBounds = wb;
      try{ map.setMaxBounds(L.latLngBounds(wb).pad(0.15)); }catch(e){}
      worldRect = L.rectangle(wb, { color:'#3a4654', weight:1, fill:false, dashArray:'5 5', interactive:false }).addTo(map);
    }
  }

  for(const c of d.claims){
    const b = [[c.x - c.half, c.z - c.half], [c.x + c.half, c.z + c.half]];
    const r = L.rectangle(b, { color: c.shielded ? '#4fd6a0' : '#5a6b7a', weight: 1,
                               fillColor: c.shielded ? '#4fd6a0' : '#5a6b7a',
                               fillOpacity: c.shielded ? 0.12 : 0.05 });
    r.bindTooltip((c.shielded ? 'Shielded LCB: ' : 'LCB: ') + esc(c.owner), { sticky: true });
    (c.shielded ? gShields : gClaims).addLayer(r);
  }

  for(const z of d.zombies){
    const m = L.circleMarker([z.x, z.z], { radius: z.v ? 4 : 3, color: z.v ? '#e3b341' : '#e5534b',
                                           weight: 1, fillOpacity: 0.85 });
    if(z.v) m.bindTooltip(z.v, { direction: 'top' });
    gZombies.addLayer(m);
  }

  // Players: update-in-place (preserve open popups); add new, drop departed.
  const seenP = {};
  for(const p of d.players){
    seenP[p.id] = true;
    let pm = playerMarkers[p.id];
    if(!pm){ pm = playerMarkers[p.id] = makePlayerMarker(p); gPlayers.addLayer(pm.marker); }
    else updatePlayerMarker(pm, p);
  }
  for(const id in playerMarkers){
    if(!seenP[id]){ gPlayers.removeLayer(playerMarkers[id].marker); delete playerMarkers[id]; }
  }

  $('pc').textContent = d.players.length;
  $('cc').textContent = d.claims.length;
  $('zc').textContent = d.zShown + (d.capped ? (' / ' + d.zTotal) : '');

  // One-time recenter onto a player so the first view lands on explored terrain
  // rather than the world-origin void (spawn can be far from 0,0).
  if(!centered && d.players.length){ centered = true; map.panTo([d.players[0].x, d.players[0].z]); }
}

init();
</script>
</body>
</html>";
}
