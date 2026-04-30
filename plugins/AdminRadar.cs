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

// AdminRadar — multi-category through-walls entity readout for admins.
//
// Same engine pattern as StyxZombieRadar (server-tick + cvar push) extended to
// five entity categories: players (excluding self), zombies, animals, items
// (dropped loot + backpacks), and vehicles. Each row shows count + nearest
// distance. The XUi panel binds to cvar set per category.
//
// Permission-gated:  styx.admin.radar
// Per-player toggle: /aradar  (in-memory, not persisted across reboot — match
//                              the transient nature of admin tooling)
//
// Updates 1×/sec by default; configurable. Hidden for non-admins regardless of
// their toggle state.

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("AdminRadar", "Doowkcol", "0.1.0")]
public class AdminRadar : StyxPlugin, Styx.Plugins.IRadarStatus
{
    public override string Description => "Through-walls entity radar for admins (5 categories + distances)";

    private const string PermRadar = "styx.admin.radar";

    public class Config
    {
        public float RadiusMeters = 80f;
        public double TickSeconds = 1.0;
    }

    private Config _cfg;
    private TimerHandle _tick;

    // Per-entity-id toggle state. In-memory only.
    private readonly HashSet<int> _enabled = new HashSet<int>();

    /// <summary>Singleton accessor — used by AdminTools for cross-plugin status reads.
    /// Set in OnLoad, cleared in OnUnload, so a hot-reload swaps cleanly.</summary>
    public static AdminRadar Instance { get; private set; }

    /// <summary>True if this entity has the radar toggled on. Stable cross-plugin state
    /// query — no cvar round-trip, so it reflects the actual toggle regardless of
    /// where in the tick cycle we're called.</summary>
    public bool IsEnabledFor(int entityId) => _enabled.Contains(entityId);

    // 8-point compass bearings: indexed by direction id (0=N, 1=NE, ... 7=NW).
    // Registered as static labels so XUi can render via
    //   {#localization('aradar_dir_' + int(cvar('styx.aradar.<cat>_dir')))}
    private static readonly string[] DirNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    public override void OnLoad()
    {
        Instance = this;
        // Publish ourselves under the framework Service Registry so other
        // plugins (AdminTools status display) can query our toggle state
        // without reflection. Singleton accessor kept for now as a fallback.
        StyxCore.Services.Publish<Styx.Plugins.IRadarStatus>(this);

        _cfg = StyxCore.Configs.Load<Config>(this);
        _tick = Scheduler.Every(Math.Max(0.25, _cfg.TickSeconds), Tick, name: "AdminRadar.tick");

        // Register all driven cvars so they're cleared on respawn (avoids stale
        // readings flashing on the HUD between login and first tick).
        Styx.Ui.Ephemeral.Register(
            "styx.aradar.visible", "styx.aradar.radius",
            "styx.aradar.players_count",  "styx.aradar.players_dist",  "styx.aradar.players_dir",
            "styx.aradar.zombies_count",  "styx.aradar.zombies_dist",  "styx.aradar.zombies_dir",
            "styx.aradar.animals_count",  "styx.aradar.animals_dist",  "styx.aradar.animals_dir",
            "styx.aradar.items_count",    "styx.aradar.items_dist",    "styx.aradar.items_dir",
            "styx.aradar.vehicles_count", "styx.aradar.vehicles_dist", "styx.aradar.vehicles_dir");

        // Static direction labels — written to StyxRuntime/Localization.txt
        // on framework persist, available as localization keys next boot.
        for (int i = 0; i < DirNames.Length; i++)
            Styx.Ui.Labels.Register(this, "aradar_dir_" + i, DirNames[i]);

        StyxCore.Commands.Register("aradar",
            "Toggle the admin radar HUD — /aradar [on|off]",
            (ctx, args) => CmdToggle(ctx, args));

        StyxCore.Perms.RegisterKnown(PermRadar,
            "Toggle the admin through-walls radar HUD", Name);

        Log.Out("[AdminRadar] Loaded v0.1.0 — radius {0}m, tick {1}s, perm: {2}",
            _cfg.RadiusMeters, _cfg.TickSeconds, PermRadar);
    }

    public override void OnUnload()
    {
        if (_tick != null) { _tick.Destroy(); _tick = null; }
        // Hide the panel for everyone — plugin reload shouldn't leave stale HUDs.
        var all = StyxCore.Player?.All();
        if (all != null) foreach (var p in all) Styx.Ui.SetVar(p, "styx.aradar.visible", 0f);
        _enabled.Clear();
        if (Instance == this) Instance = null;
        // Hot-reload safe — Unpublish only removes our registration if we're
        // still the published instance (a freshly-loaded version may have
        // overwritten us already).
        StyxCore.Services?.Unpublish<Styx.Plugins.IRadarStatus>(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    void OnPlayerDisconnected(ClientInfo client, bool _shutting)
    {
        if (client != null) _enabled.Remove(client.entityId);
    }

    private void CmdToggle(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }

        string pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("Could not resolve your player id."); return; }

        if (!StyxCore.Perms.HasPermission(pid, PermRadar))
        {
            ctx.Reply("[ff6666][Radar] You lack permission '" + PermRadar + "'.[-]");
            return;
        }

        int eid = ctx.Client.entityId;
        bool currentlyOn = _enabled.Contains(eid);

        bool wantOn;
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "on":  case "true":  case "1": wantOn = true;  break;
                case "off": case "false": case "0": wantOn = false; break;
                default: ctx.Reply("Usage: /aradar [on|off]"); return;
            }
        }
        else wantOn = !currentlyOn;

        if (wantOn) _enabled.Add(eid); else _enabled.Remove(eid);

        if (!wantOn)
        {
            // Hide immediately — don't wait for the next tick.
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null) Styx.Ui.SetVar(p, "styx.aradar.visible", 0f);
        }

        ctx.Reply(wantOn
            ? "[00ff66][Radar] Admin radar ON — radius " + _cfg.RadiusMeters + "m.[-]"
            : "[ffaa00][Radar] Admin radar OFF.[-]");
    }

    private void Tick()
    {
        var players = StyxCore.Player?.All();
        if (players == null) return;

        float r = _cfg.RadiusMeters;
        float r2 = r * r;

        foreach (var p in players)
        {
            if (p == null) continue;
            if (!_enabled.Contains(p.entityId)) continue;

            // Re-check perm every tick — handles admin-stripped-mid-session cleanly.
            var pid = StyxCore.Player.PlatformIdOf(p);
            if (string.IsNullOrEmpty(pid) || !StyxCore.Perms.HasPermission(pid, PermRadar))
            {
                Styx.Ui.SetVar(p, "styx.aradar.visible", 0f);
                continue;
            }

            var pos = p.position;

            // Tally per category. Track count + nearest squared-distance + the
            // nearest entity's delta vector (for bearing) in one pass.
            int playersCount = 0, zombiesCount = 0, animalsCount = 0, itemsCount = 0, vehiclesCount = 0;
            float playersNear = float.MaxValue, zombiesNear = float.MaxValue, animalsNear = float.MaxValue,
                  itemsNear = float.MaxValue, vehiclesNear = float.MaxValue;
            Vector3 playersDelta = default, zombiesDelta = default, animalsDelta = default,
                    itemsDelta = default, vehiclesDelta = default;

            var entities = StyxCore.World.EntitiesInRadius(pos, r, exclude: p);
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == null) continue;
                var d = e.position - pos;
                float sq = d.sqrMagnitude;
                if (sq > r2) continue;

                switch (e)
                {
                    case EntityPlayer _:
                        playersCount++;
                        if (sq < playersNear) { playersNear = sq; playersDelta = d; }
                        break;
                    case EntityZombie z:
                        if (z.IsDead()) break;
                        zombiesCount++;
                        if (sq < zombiesNear) { zombiesNear = sq; zombiesDelta = d; }
                        break;
                    case EntityAnimal a:
                        if (a.IsDead()) break;
                        animalsCount++;
                        if (sq < animalsNear) { animalsNear = sq; animalsDelta = d; }
                        break;
                    case EntityBackpack _:
                    case EntityItem _:
                        itemsCount++;
                        if (sq < itemsNear) { itemsNear = sq; itemsDelta = d; }
                        break;
                    case EntityVehicle _:
                        vehiclesCount++;
                        if (sq < vehiclesNear) { vehiclesNear = sq; vehiclesDelta = d; }
                        break;
                }
            }

            Styx.Ui.SetVar(p, "styx.aradar.radius", r);
            PushCategory(p, "players",  playersCount,  playersNear,  playersDelta);
            PushCategory(p, "zombies",  zombiesCount,  zombiesNear,  zombiesDelta);
            PushCategory(p, "animals",  animalsCount,  animalsNear,  animalsDelta);
            PushCategory(p, "items",    itemsCount,    itemsNear,    itemsDelta);
            PushCategory(p, "vehicles", vehiclesCount, vehiclesNear, vehiclesDelta);
            Styx.Ui.SetVar(p, "styx.aradar.visible", 1f);
        }
    }

    private static void PushCategory(EntityPlayer p, string cat, int count, float nearSq, Vector3 delta)
    {
        Styx.Ui.SetVar(p, "styx.aradar." + cat + "_count", count);
        Styx.Ui.SetVar(p, "styx.aradar." + cat + "_dist", count > 0 ? Mathf.Sqrt(nearSq) : 0f);
        Styx.Ui.SetVar(p, "styx.aradar." + cat + "_dir",  count > 0 ? BearingIndex(delta) : 0);
    }

    /// <summary>
    /// Convert a horizontal delta vector into an 8-point compass index
    /// (0=N, 1=NE, ..., 7=NW). 7DTD world axes: +Z = north, +X = east.
    /// </summary>
    private static int BearingIndex(Vector3 delta)
    {
        float deg = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
        if (deg < 0f) deg += 360f;
        // Snap to nearest 45° bucket; +22.5° offset centers each bucket on its compass label.
        return (int)((deg + 22.5f) / 45f) % 8;
    }
}
