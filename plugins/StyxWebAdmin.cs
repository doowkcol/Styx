// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//
// StyxWebAdmin -- admin WRITE endpoints for the Styx web layer (Phase 3).
//
// Demonstrates POST routes through Styx.Web, gated to ADMIN (web permission
// level 0). The write infrastructure (MapPost + request body parsing) shipped
// with the web layer's Phase 1; this plugin is the actionable proof: run a
// console command and broadcast chat from the dashboard.
//
// Auth model: routes are gated by the caller's vanilla web permission level
// (admin 0 / user 1000 / guest 2000), resolved by the web server from the
// dashboard session cookie or an API token (X-SDTD-API-TOKENNAME / -SECRET),
// mapped to serveradmin.xml. That IS the right gate for a web dashboard — the
// dashboard login decides admin-ness. (Mapping a web identity onto in-game
// Styx perm GROUPS is intentionally not forced here: a web user is rarely the
// same identity as an in-game player. req.CallerName is exposed for plugins
// that have their own mapping scheme.)
//
// Endpoints (POST, admin-only):
//   POST /styx/command  body {"command":"..."}  -> runs it, returns output[]
//   POST /styx/say       body {"message":"..."}  -> broadcasts to server chat
//
// Test (needs admin auth — a dashboard session or an admin API token created
// with the `webtokens` console command):
//   curl -X POST http://127.0.0.1:8080/styx/command \
//        -H "X-SDTD-API-TOKENNAME: <name>" -H "X-SDTD-API-SECRET: <secret>" \
//        -H "Content-Type: application/json" -d "{\"command\":\"version\"}"

using System.Net;
using Styx;
using Styx.Plugins;

[Info("StyxWebAdmin", "Doowkcol", "0.1.0")]
public class StyxWebAdmin : StyxPlugin
{
    public override string Description => "Admin web write endpoints (/styx/command, /styx/say) for the Styx web layer";

    public override void OnLoad()
    {
        // Run a console/Styx command and return its output. Admin-only.
        Styx.Web.MapPost(this, "command", StyxWebPerm.Admin, req =>
        {
            string cmd = req.BodyString("command");
            if (string.IsNullOrEmpty(cmd))
                return StyxWebResponse.Error(HttpStatusCode.BadRequest, "missing 'command' in body");

            var output = Styx.Server.ExecConsole(cmd);
            Log.Out("[StyxWebAdmin] {0} ran command via web: {1}", req.CallerName, cmd);
            return StyxWebResponse.Json(new { ok = true, command = cmd, output });
        });

        // Broadcast a message to in-game chat. Admin-only.
        Styx.Web.MapPost(this, "say", StyxWebPerm.Admin, req =>
        {
            string msg = req.BodyString("message");
            if (string.IsNullOrEmpty(msg))
                return StyxWebResponse.Error(HttpStatusCode.BadRequest, "missing 'message' in body");

            Styx.Server.Broadcast(msg);
            Log.Out("[StyxWebAdmin] {0} broadcast via web: {1}", req.CallerName, msg);
            return StyxWebResponse.Ok();
        });

        // Kick a player by name. Admin-only. Used by the dashboard's per-player button.
        Styx.Web.MapPost(this, "kick", StyxWebPerm.Admin, req =>
        {
            string player = req.BodyString("player");
            if (string.IsNullOrEmpty(player))
                return StyxWebResponse.Error(HttpStatusCode.BadRequest, "missing 'player' in body");
            string reason = req.BodyString("reason");
            if (string.IsNullOrEmpty(reason)) reason = "kicked from dashboard";

            var p = StyxCore.Player?.Find(player);
            if (p == null) return StyxWebResponse.Error(HttpStatusCode.NotFound, "player not found: " + player);

            bool ok = StyxCore.Player.Kick(p, reason);
            Log.Out("[StyxWebAdmin] {0} kicked {1} via web ({2})", req.CallerName, player, reason);
            return ok ? StyxWebResponse.Ok() : StyxWebResponse.Error(HttpStatusCode.InternalServerError, "kick failed");
        });

        Log.Out("[StyxWebAdmin] Loaded -- POST /styx/command, /styx/say, /styx/kick (admin)");
    }

    // Routes auto-removed by the framework (Styx.Web.UnregisterAllFor) on unload.
}
