// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//
// StyxWebManage -- management surface for the Styx web dashboard (Phase 5).
//
// Two CSMM/steenamaro-style admin pages, served by the vanilla TFP web server
// alongside the live dashboard, plus the REST endpoints behind them:
//
//   PERMISSIONS  (read + write)
//     GET  /styx/perms          -- the perm-manager HTML page          (user)
//     GET  /styx/perms/data     -- groups + players + known-perm list  (user)
//     POST /styx/perms/grant    body {group, perm}                     (admin)
//     POST /styx/perms/revoke   body {group, perm}                     (admin)
//     POST /styx/perms/assign   body {player, group}                   (admin)
//     POST /styx/perms/unassign body {player, group}                   (admin)
//
//   CONFIGS  (read-only for now)
//     GET  /styx/configs        -- the config-viewer HTML page         (user)
//     GET  /styx/configs/data   -- list of plugin config file names    (user)
//     GET  /styx/configs/<name> -- one config's raw JSON               (user)
//
// All page + data reads are gated to a logged-in web USER (these expose player
// identities and config contents); every write is ADMIN-only. Auth is the
// vanilla per-request web permission level (admin 0 / user 1000 / guest 2000),
// inherited from the dashboard session or an API token -- the same gate the
// rest of the web layer uses. Group/player writes go straight through
// StyxCore.Perms (PermissionManager), which persists on success.
//
// Pure plugin -- uses only the shipped Styx.Web API + StyxCore, so it
// hot-reloads with no framework rebuild. Nav links tie it together with
// /styx/dashboard. Config editing is intentionally read-only this pass.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Styx;
using Styx.Permissions;
using Styx.Plugins;

[Info("StyxWebManage", "Doowkcol", "0.1.0")]
public class StyxWebManage : StyxPlugin
{
    public override string Description =>
        "Web perm manager (/styx/perms) + read-only config viewer (/styx/configs) for the Styx dashboard";

    public override void OnLoad()
    {
        // ---- Permissions ----
        // GET page + data. One route; the trailing /data segment selects JSON.
        Styx.Web.MapGet(this, "perms", StyxWebPerm.User, req =>
        {
            if (string.Equals(req.Id, "data", StringComparison.OrdinalIgnoreCase))
                return StyxWebResponse.Json(BuildPermsData());
            return Html(PermsPage);
        });

        // POST writes. The trailing segment is the action; body carries operands.
        Styx.Web.MapPost(this, "perms", StyxWebPerm.Admin, req =>
        {
            string action = (req.Id ?? "").ToLowerInvariant();
            switch (action)
            {
                case "grant":
                case "revoke":
                {
                    string g = req.BodyString("group"), p = req.BodyString("perm");
                    if (string.IsNullOrEmpty(g) || string.IsNullOrEmpty(p))
                        return StyxWebResponse.Error(HttpStatusCode.BadRequest, "need 'group' and 'perm'");
                    if (!StyxCore.Perms.GroupExists(g))
                        return StyxWebResponse.Error(HttpStatusCode.NotFound, "no such group: " + g);
                    bool changed = action == "grant"
                        ? StyxCore.Perms.GrantToGroup(g, p)
                        : StyxCore.Perms.RevokeFromGroup(g, p);
                    Log.Out("[StyxWebManage] {0} {1} '{2}' {3} group '{4}'",
                        req.CallerName, action, p, action == "grant" ? "to" : "from", g);
                    return StyxWebResponse.Json(new { ok = true, changed });
                }
                case "assign":
                case "unassign":
                {
                    string player = req.BodyString("player"), grp = req.BodyString("group");
                    if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(grp))
                        return StyxWebResponse.Error(HttpStatusCode.BadRequest, "need 'player' and 'group'");
                    if (!StyxCore.Perms.GroupExists(grp))
                        return StyxWebResponse.Error(HttpStatusCode.NotFound, "no such group: " + grp);
                    bool changed = action == "assign"
                        ? StyxCore.Perms.AddPlayerToGroup(player, grp)
                        : StyxCore.Perms.RemovePlayerFromGroup(player, grp);
                    Log.Out("[StyxWebManage] {0} {1} player '{2}' {3} group '{4}'",
                        req.CallerName, action, player, action == "assign" ? "to" : "from", grp);
                    return StyxWebResponse.Json(new { ok = true, changed });
                }
                default:
                    return StyxWebResponse.Error(HttpStatusCode.BadRequest,
                        "unknown perms action: " + (string.IsNullOrEmpty(req.Id) ? "(none)" : req.Id));
            }
        });

        // ---- Configs (read-only) ----
        Styx.Web.MapGet(this, "configs", StyxWebPerm.User, req =>
        {
            if (string.IsNullOrEmpty(req.Id))
                return Html(ConfigsPage);
            if (string.Equals(req.Id, "data", StringComparison.OrdinalIgnoreCase))
                return StyxWebResponse.Json(new { files = StyxCore.Configs.ListConfigFiles() });
            return ReadConfig(req.Id);
        });

        Log.Out("[StyxWebManage] Loaded -- /styx/perms (user/admin), /styx/configs (user, read-only)");
    }

    // Routes auto-removed by the framework (Styx.Web.UnregisterAllFor) on unload.

    // ====================================================================
    // Data
    // ====================================================================

    private static StyxWebResponse Html(string body) =>
        new StyxWebResponse { ContentType = "text/html; charset=utf-8", Body = body };

    private static object BuildPermsData()
    {
        var groups = new List<object>();
        foreach (var g in StyxCore.Perms.GetAllGroups())
            groups.Add(new
            {
                name = g.Name, parent = g.Parent, priority = g.Priority, tag = g.ChatTag,
                perms = Sorted(g.Perms),
            });

        var players = new List<object>();
        foreach (var kv in StyxCore.Perms.GetAllPlayers())
            players.Add(new
            {
                id = kv.Key, name = kv.Value.LastSeenName,
                groups = Sorted(kv.Value.Groups),
                grants = Sorted(kv.Value.Grants),
                revokes = Sorted(kv.Value.Revokes),
            });

        var known = new List<object>();
        foreach (var k in StyxCore.Perms.AllKnown)
            known.Add(new { name = k.Name, desc = k.Description, owner = k.Owner });

        return new { groups, players, known };
    }

    private static List<string> Sorted(IEnumerable<string> src)
    {
        var l = new List<string>(src);
        l.Sort(StringComparer.OrdinalIgnoreCase);
        return l;
    }

    private static StyxWebResponse ReadConfig(string name)
    {
        // Defense in depth: no path traversal, and the name must be one the
        // ConfigManager actually lists (so we only ever serve plugin configs).
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains(".."))
            return StyxWebResponse.Error(HttpStatusCode.BadRequest, "bad config name");

        string resolved = null;
        foreach (var f in StyxCore.Configs.ListConfigFiles())
            if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) { resolved = f; break; }
        if (resolved == null)
            return StyxWebResponse.Error(HttpStatusCode.NotFound, "no such config: " + name);

        try
        {
            string path = Path.Combine(StyxCore.Configs.ConfigDir, resolved + ".json");
            string content = File.ReadAllText(path);
            return StyxWebResponse.Json(new { name = resolved, content });
        }
        catch (Exception e)
        {
            return StyxWebResponse.Error(HttpStatusCode.InternalServerError, e.Message);
        }
    }

    // ====================================================================
    // Pages. Built by concatenating non-interpolated verbatim strings +
    // Nav(active), so CSS/JS braces need no escaping. JS is single-quoted and
    // builds DOM programmatically (no nested-quote HTML strings to escape).
    // ====================================================================

    private static string PermsPage   => Head + Nav("perms")   + PermsBody   + "<script>" + CommonJs + PermsJs   + "</script>" + Tail;
    private static string ConfigsPage => Head + Nav("configs") + ConfigsBody + "<script>" + CommonJs + ConfigsJs + "</script>" + Tail;

    private static string Nav(string active)
    {
        return "<header>"
             + "<h1>STYX</h1>"
             + NavLink("dashboard", "/styx/dashboard", "Dashboard", active)
             + NavLink("map", "/styx/map", "Map", active)
             + NavLink("perms", "/styx/perms", "Permissions", active)
             + NavLink("configs", "/styx/configs", "Configs", active)
             + "<span class='stat' id='conn' style='margin-left:auto;color:var(--dim)'>loading...</span>"
             + "</header>";
    }

    private static string NavLink(string key, string href, string label, string active) =>
        "<a class='navlink" + (key == active ? " active" : "") + "' href='" + href + "'>" + label + "</a>";

    private const string Head = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Styx Admin</title>
<style>
  :root{ --bg:#0d1014; --panel:#161b22; --line:#2a313a; --txt:#d6dde6; --dim:#8b96a4; --accent:#4fd6a0; --bad:#e5534b; --warn:#e3b341; }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.4 'Segoe UI',system-ui,sans-serif}
  header{display:flex;align-items:center;gap:10px;padding:12px 18px;background:var(--panel);border-bottom:1px solid var(--line)}
  header h1{font-size:18px;margin:0 12px 0 0;color:var(--accent);letter-spacing:1px}
  .navlink{color:var(--dim);text-decoration:none;padding:6px 11px;border-radius:6px;font-size:13px}
  .navlink:hover{color:var(--txt);background:#1f2630}
  .navlink.active{color:var(--accent);background:#1f2630}
  .stat{color:var(--dim)}
  main{display:grid;grid-template-columns:1fr 1fr;gap:14px;padding:14px;max-width:1200px;margin:0 auto}
  .panel{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px}
  .panel h2{margin:0 0 10px;font-size:13px;text-transform:uppercase;letter-spacing:1px;color:var(--dim);display:flex;align-items:center;gap:8px}
  .panel h2 .muted{text-transform:none;letter-spacing:0}
  .muted{color:var(--dim);font-size:12px}
  .card{background:#11161d;border:1px solid var(--line);border-radius:8px;padding:10px 12px;margin-bottom:10px}
  .cardhead{display:flex;align-items:baseline;gap:8px;margin-bottom:6px;flex-wrap:wrap}
  .gname{font-weight:600;color:var(--txt)}
  .perms{display:flex;flex-wrap:wrap;gap:5px;margin:6px 0}
  .chip{display:inline-flex;align-items:center;gap:6px;background:#1f2630;border:1px solid var(--line);border-radius:12px;padding:3px 6px 3px 10px;font-size:12px}
  .chip.g{border-color:#2d4a63}
  .chip.add{color:var(--accent);padding:3px 10px} .chip.del{color:var(--bad);padding:3px 10px}
  .chip .x{background:none;border:none;color:var(--dim);cursor:pointer;font-size:15px;padding:0 2px;line-height:1}
  .chip .x:hover{color:var(--bad)}
  .row{display:flex;gap:8px;margin-top:8px;align-items:center}
  .row .grow{flex:1;min-width:0}
  select,input{background:#0a0d11;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:6px 8px;font-size:13px}
  button{background:#1f2630;color:var(--txt);border:1px solid var(--line);border-radius:5px;padding:6px 11px;cursor:pointer;font-size:12px}
  button:hover{border-color:var(--accent);color:var(--accent)}
  .out{background:#0a0d11;border:1px solid var(--line);border-radius:6px;padding:10px;color:var(--dim);font-size:12px;white-space:pre-wrap;margin:0}
  .cfgwrap{display:grid;grid-template-columns:240px 1fr;gap:14px;padding:14px;max-width:1200px;margin:0 auto}
  .filelist{display:flex;flex-direction:column;gap:4px;max-height:78vh;overflow:auto}
  .filebtn{text-align:left;background:#11161d}
  .filebtn.active{border-color:var(--accent);color:var(--accent)}
  pre.cfg{background:#0a0d11;border:1px solid var(--line);border-radius:6px;padding:12px;overflow:auto;max-height:80vh;color:var(--txt);font:12px/1.55 Consolas,'Courier New',monospace;white-space:pre;margin:0}
</style>
</head>
<body>
";

    private const string Tail = @"
</body>
</html>";

    // Shared JS helpers (no <script> tags; wrapped by the page assembler).
    private const string CommonJs = @"
const $ = id => document.getElementById(id);
function mk(tag, cls, text){ const e=document.createElement(tag); if(cls) e.className=cls; if(text!=null) e.textContent=text; return e; }
function note(msg){ const o=$('out'); if(o) o.textContent=msg; }
async function post(path, body){
  try{
    const r = await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    if(r.status===403) return {error:'403 - admin session required'};
    return await r.json().catch(()=>({}));
  }catch(e){ return {error:String(e)}; }
}
";

    private const string PermsBody = @"
<main>
  <section class='panel'>
    <h2>Groups <span class='muted' id='gcount'></span> <button id='reload' style='margin-left:auto'>Reload</button></h2>
    <div id='groups'></div>
  </section>
  <section class='panel'>
    <h2>Players <span class='muted' id='pcount'></span></h2>
    <div id='players'></div>
  </section>
  <section class='panel' style='grid-column:1 / -1'>
    <h2>Activity</h2>
    <pre id='out' class='out'>Loading... edits require an admin dashboard session.</pre>
  </section>
</main>
";

    private const string PermsJs = @"
let DATA = {groups:[], players:[], known:[]};

function renderGroups(){
  const wrap=$('groups'); wrap.innerHTML=''; $('gcount').textContent='('+DATA.groups.length+')';
  for(const g of DATA.groups){
    const card=mk('div','card');
    const head=mk('div','cardhead');
    head.appendChild(mk('span','gname', g.name));
    const bits=[]; if(g.parent) bits.push('parent: '+g.parent); bits.push('priority '+g.priority); if(g.tag) bits.push('tag '+g.tag);
    head.appendChild(mk('span','muted', bits.join('  |  ')));
    card.appendChild(head);

    const perms=mk('div','perms');
    if(!g.perms.length) perms.appendChild(mk('span','muted','no permissions'));
    for(const p of g.perms){
      const chip=mk('span','chip', p);
      const x=mk('button','x','×'); x.title='revoke'; x.onclick=()=>revoke(g.name,p);
      chip.appendChild(x); perms.appendChild(chip);
    }
    card.appendChild(perms);

    const inSet=new Set(g.perms.map(s=>s.toLowerCase()));
    const avail=DATA.known.filter(k=>!inSet.has(k.name.toLowerCase()));
    const row=mk('div','row');
    const sel=mk('select','grow');
    const ph=mk('option',null, avail.length ? 'add a known permission...' : '(all known perms already granted)'); ph.value=''; sel.appendChild(ph);
    for(const k of avail){ const o=mk('option',null, k.name + (k.desc?(' - '+k.desc):'')); o.value=k.name; sel.appendChild(o); }
    const txt=mk('input','grow'); txt.placeholder='...or type any permission';
    const btn=mk('button',null,'Grant');
    btn.onclick=()=>{ const v=(txt.value.trim()||sel.value); if(!v){ note('pick or type a permission first'); return; } grant(g.name, v); txt.value=''; sel.value=''; };
    row.appendChild(sel); row.appendChild(txt); row.appendChild(btn);
    card.appendChild(row);
    wrap.appendChild(card);
  }
}

function renderPlayers(){
  const wrap=$('players'); wrap.innerHTML=''; $('pcount').textContent='('+DATA.players.length+')';
  if(!DATA.players.length){ wrap.appendChild(mk('p','muted','No players recorded yet. Players appear here after they first join.')); return; }
  const groupNames=DATA.groups.map(g=>g.name);
  for(const pl of DATA.players){
    const card=mk('div','card');
    const head=mk('div','cardhead');
    head.appendChild(mk('span','gname', pl.name||'(unknown)'));
    head.appendChild(mk('span','muted', pl.id));
    card.appendChild(head);

    const gs=mk('div','perms');
    if(!pl.groups.length) gs.appendChild(mk('span','muted','no groups'));
    for(const gn of pl.groups){
      const chip=mk('span','chip g', gn);
      const x=mk('button','x','×'); x.title='remove from group'; x.onclick=()=>unassign(pl.id, gn);
      chip.appendChild(x); gs.appendChild(chip);
    }
    card.appendChild(gs);

    if(pl.grants.length || pl.revokes.length){
      const info=mk('div','perms');
      for(const p of pl.grants) info.appendChild(mk('span','chip add','+ '+p));
      for(const p of pl.revokes) info.appendChild(mk('span','chip del','- '+p));
      card.appendChild(info);
      const lbl=mk('div','muted','per-player overrides (read-only here)'); card.appendChild(lbl);
    }

    const inSet=new Set(pl.groups.map(s=>s.toLowerCase()));
    const avail=groupNames.filter(n=>!inSet.has(n.toLowerCase()));
    const row=mk('div','row');
    const sel=mk('select','grow');
    const ph=mk('option',null, avail.length ? 'add to a group...' : '(in every group)'); ph.value=''; sel.appendChild(ph);
    for(const n of avail){ const o=mk('option',null,n); o.value=n; sel.appendChild(o); }
    const btn=mk('button',null,'Add to group');
    btn.onclick=()=>{ if(!sel.value){ note('pick a group first'); return; } assign(pl.id, sel.value); };
    row.appendChild(sel); row.appendChild(btn);
    card.appendChild(row);
    wrap.appendChild(card);
  }
}

async function grant(group, perm){ const j=await post('/styx/perms/grant',{group,perm}); note(j.error?('grant failed: '+j.error):('granted ['+perm+'] to '+group)); if(!j.error) load(); }
async function revoke(group, perm){ const j=await post('/styx/perms/revoke',{group,perm}); note(j.error?('revoke failed: '+j.error):('revoked ['+perm+'] from '+group)); if(!j.error) load(); }
async function assign(player, group){ const j=await post('/styx/perms/assign',{player,group}); note(j.error?('assign failed: '+j.error):('added player to '+group)); if(!j.error) load(); }
async function unassign(player, group){ const j=await post('/styx/perms/unassign',{player,group}); note(j.error?('remove failed: '+j.error):('removed player from '+group)); if(!j.error) load(); }

async function load(){
  try{
    const r=await fetch('/styx/perms/data');
    if(r.status===403){ $('conn').textContent='login required'; note('403 - log in as a web user/admin to view permissions.'); return; }
    DATA=await r.json();
    renderGroups(); renderPlayers();
    $('conn').style.color='var(--accent)'; $('conn').textContent=DATA.known.length+' known perms';
  }catch(e){ note('load error: '+e); }
}
$('reload').onclick=load;
load();
";

    private const string ConfigsBody = @"
<div class='cfgwrap'>
  <section class='panel'>
    <h2>Config files <span class='muted' id='fcount'></span></h2>
    <div class='filelist' id='files'></div>
  </section>
  <section class='panel'>
    <h2><span id='cfgname'>Select a config</span> <span class='muted'>read-only</span></h2>
    <pre class='cfg' id='cfg'>Pick a config file on the left to view its JSON.</pre>
  </section>
</div>
";

    private const string ConfigsJs = @"
async function loadList(){
  try{
    const r=await fetch('/styx/configs/data');
    if(r.status===403){ $('conn').textContent='login required'; $('files').appendChild(mk('p','muted','403 - log in as a web user/admin.')); return; }
    const d=await r.json();
    const wrap=$('files'); wrap.innerHTML=''; $('fcount').textContent='('+d.files.length+')';
    $('conn').style.color='var(--accent)'; $('conn').textContent=d.files.length+' configs';
    if(!d.files.length){ wrap.appendChild(mk('p','muted','no plugin config files found')); return; }
    for(const f of d.files){ const b=mk('button','filebtn', f); b.onclick=()=>view(f,b); wrap.appendChild(b); }
  }catch(e){ $('files').appendChild(mk('p','muted','load error: '+e)); }
}

async function view(name, btn){
  document.querySelectorAll('.filebtn').forEach(x=>x.classList.remove('active'));
  if(btn) btn.classList.add('active');
  $('cfgname').textContent=name+'.json';
  $('cfg').textContent='loading...';
  try{
    const r=await fetch('/styx/configs/'+encodeURIComponent(name));
    if(r.status===403){ $('cfg').textContent='403 - login required'; return; }
    if(!r.ok){ $('cfg').textContent='error '+r.status; return; }
    const d=await r.json();
    let t=d.content;
    try{ t=JSON.stringify(JSON.parse(d.content), null, 2); }catch(e){}
    $('cfg').textContent=t;
  }catch(e){ $('cfg').textContent='error: '+e; }
}
loadList();
";
}
