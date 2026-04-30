// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// This plugin is part of the Styx Restricted plugin set.
// Personal use on a single server you operate is permitted.
// Modification for personal use is permitted.
//
// Redistribution, resale, or use as part of a paid hosting /
// managed-service offering requires a separate commercial licence.
// Contact: jacklockwood@outlook.com
//
// See LICENSE-PLUGINS at the repo root for full terms.
//

// StyxZombieRadar — live HUD readout of zombies within radius X of each player.
//
// Now uses tiered "use perms" — players with no perm see nothing (vanilla);
// players with the basic use perm get a small radius; tier perms (vip,
// master) extend the radius. First match wins (highest tier first).
//
// Config (configs/StyxZombieRadar.json, live-reload on save):
//   RadiusByPerm — ordered list of {Perm, Radius}; walked top-down per tick.
//                  First perm the player has determines their radius.
//                  Empty list / no match for a player = panel hidden.
//   TickSeconds  — default 1.0
//
// Perm tier examples (default seed):
//   styx.radar.use     → 10m   (basic — granted to default group)
//   styx.radar.vip     → 30m   (vip group)
//   styx.radar.master  → 60m   (admin group, etc.)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxZombieRadar", "Doowkcol", "0.1.0")]
public class StyxZombieRadar : StyxPlugin
{
    public override string Description => "Live per-player zombie-count readout in the Styx HUD (perm-tiered radius)";

    public class TierEntry
    {
        public string Perm;
        public float Radius;
    }

    public class Config
    {
        public List<TierEntry> RadiusByPerm = new List<TierEntry>
        {
            new TierEntry { Perm = "styx.radar.master", Radius = 60f },
            new TierEntry { Perm = "styx.radar.vip",    Radius = 30f },
            new TierEntry { Perm = "styx.radar.use",    Radius = 10f },
        };
        public double TickSeconds = 1.0;
    }

    private Config _cfg;
    private TimerHandle _tick;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _tick = Scheduler.Every(Math.Max(0.25, _cfg.TickSeconds), Tick, name: "StyxZombieRadar.tick");

        Styx.Ui.Ephemeral.Register("styx.radar.visible", "styx.radar.count", "styx.radar.radius");

        StyxCore.Commands.Register("radar", "Radar status — /radar [tick] for a forced tick", (ctx, args) =>
        {
            if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
            var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
            if (p == null) { ctx.Reply("Player not found."); return; }

            if (args.Length > 0 && args[0] == "tick")
            {
                Tick();
                ctx.Reply("[00ff66]Tick forced.[-]");
            }

            float visible = p.Buffs?.GetCustomVar("styx.radar.visible") ?? -1f;
            float count   = p.Buffs?.GetCustomVar("styx.radar.count") ?? -1f;
            float radius  = p.Buffs?.GetCustomVar("styx.radar.radius") ?? -1f;
            ctx.Reply(string.Format(
                "[ccddff]Radar state:[-] visible=[ffffdd]{0:F0}[-] count=[ffffdd]{1:F0}[-] radius=[ffffdd]{2:F0}m[-]",
                visible, count, radius));
            ctx.Reply("Tiers (first match wins):");
            foreach (var t in _cfg.RadiusByPerm)
                ctx.Reply(string.Format("  {0} -> {1}m", t.Perm, t.Radius));
        });

        // Register every tier perm with PermEditor so admins can toggle them
        // per group from the UI without editing config files.
        foreach (var t in _cfg.RadiusByPerm)
        {
            if (string.IsNullOrEmpty(t.Perm)) continue;
            StyxCore.Perms.RegisterKnown(t.Perm,
                "Show zombie radar with " + t.Radius + "m radius", Name);
        }

        Log.Out("[StyxZombieRadar] Loaded v0.1.0 — {0} tier(s), tick {1}s",
            _cfg.RadiusByPerm.Count, _cfg.TickSeconds);
    }

    public override void OnUnload()
    {
        StyxCore.Perms.UnregisterKnownByOwner(Name);
        if (_tick != null) { _tick.Destroy(); _tick = null; }
        var all = StyxCore.Player?.All();
        if (all != null) foreach (var p in all) Styx.Ui.SetVar(p, "styx.radar.visible", 0f);
    }

    private void Tick()
    {
        var players = StyxCore.Player?.All();
        if (players == null) return;

        foreach (var p in players)
        {
            if (p == null) continue;

            // Resolve the player's tier — first perm in the configured list
            // they have wins. No match = hide the panel entirely AND zero
            // styx.radar.radius so the StyxHud zombie section (which mirrors
            // this cvar via visibility binding) also hides.
            float r = ResolveRadiusFor(p);
            if (r <= 0f)
            {
                Styx.Ui.SetVar(p, "styx.radar.visible", 0f);
                Styx.Ui.SetVar(p, "styx.radar.radius", 0f);
                continue;
            }
            float r2 = r * r;

            var pos = p.position;
            int count = 0;
            var nearby = StyxCore.World.AliveInRadius(pos, r, includePlayers: false);
            for (int i = 0; i < nearby.Count; i++)
            {
                var ea = nearby[i];
                if (!(ea is EntityZombie)) continue;
                if (ea.IsDead()) continue;
                var d = ea.position - pos;
                if (d.sqrMagnitude <= r2) count++;
            }

            Styx.Ui.SetVar(p, "styx.radar.count", count);
            Styx.Ui.SetVar(p, "styx.radar.radius", r);
            Styx.Ui.SetVar(p, "styx.radar.visible", 1f);
        }
    }

    /// <summary>Walk the tier list top-down — first perm the player has determines
    /// their radius. Returns 0 if no tier matches (panel hidden).</summary>
    private float ResolveRadiusFor(EntityPlayer p)
    {
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return 0f;
        foreach (var t in _cfg.RadiusByPerm)
        {
            if (string.IsNullOrEmpty(t.Perm)) continue;
            if (StyxCore.Perms.HasPermission(pid, t.Perm)) return t.Radius;
        }
        return 0f;
    }
}
