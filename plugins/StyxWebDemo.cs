// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//
// StyxWebDemo -- reference plugin for the Styx web layer (Phase 1).
//
// Demonstrates the plugin-facing web API: a plugin registers REST endpoints
// under /styx/ exactly like it registers chat commands. Because routes are
// registered into the Styx route table at runtime (not engine assembly-scan
// auto-discovery), this works for hot-reloaded plugins. Routes are auto-
// removed when the plugin unloads.
//
// Endpoints (served by the vanilla TFP web server, default port 8080):
//   GET /styx/status   -- framework + world summary        (guest: anyone)
//   GET /styx/players   -- live player list                 (user: logged in)
//
// Try (with the dashboard running):
//   http://<server>:8080/styx/status
//   http://<server>:8080/styx/players   (requires a web session / API token)

using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxWebDemo", "Doowkcol", "0.1.0")]
public class StyxWebDemo : StyxPlugin
{
    public override string Description => "Reference web endpoints (/styx/status, /styx/players, SSE live feed) for the Styx web layer";

    private StyxWebChannel _live;
    private TimerHandle _liveTick;

    public override void OnLoad()
    {
        // Public, unauthenticated summary -- safe for a guest dashboard view.
        Styx.Web.MapGet(this, "status", StyxWebPerm.Guest, req =>
        {
            var players = StyxCore.Player?.All();
            return StyxWebResponse.Json(new
            {
                framework = "Styx",
                version   = StyxCore.Version,
                day       = StyxCore.World?.CurrentDay ?? 0,
                bloodMoon = StyxCore.World?.IsBloodMoon ?? false,
                players   = players?.Count ?? 0,
                routes    = Styx.Web.RouteCount,
            });
        });

        // Live player roster -- gated to logged-in web users (not guests).
        Styx.Web.MapGet(this, "players", StyxWebPerm.User, req =>
        {
            var players = StyxCore.Player?.All();
            var list = new List<object>();
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null) continue;
                    var pos = p.position;
                    list.Add(new
                    {
                        name   = p.EntityName,
                        id     = p.entityId,
                        health = (int)p.Health,
                        x      = (int)pos.x,
                        y      = (int)pos.y,
                        z      = (int)pos.z,
                    });
                }
            }
            return StyxWebResponse.Json(list);
        });

        // SSE: push a live snapshot every 2s on the "live" channel. Clients
        // connect to /sse/?events=StyxLive and addEventListener('live', ...).
        _live = Styx.Web.Channel(this, "live");
        _liveTick = Scheduler.Every(2.0, () =>
        {
            var players = StyxCore.Player?.All();
            _live.Push(new
            {
                day       = StyxCore.World?.CurrentDay ?? 0,
                bloodMoon = StyxCore.World?.IsBloodMoon ?? false,
                count     = players?.Count ?? 0,
                players   = players?.Where(p => p != null).Select(p => new
                {
                    name = p.EntityName, hp = (int)p.Health,
                    x = (int)p.position.x, z = (int)p.position.z
                }),
            });
        }, name: "StyxWebDemo.live");

        Log.Out("[StyxWebDemo] Loaded -- /styx/status (guest), /styx/players (user), SSE 'live' channel");
    }

    public override void OnUnload()
    {
        // Stop the push tick. The route + channel registrations themselves are
        // auto-removed by the framework (Styx.Web.UnregisterAllFor) like commands.
        _liveTick?.Destroy(); _liveTick = null;
    }
}
