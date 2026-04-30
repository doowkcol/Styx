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

// StyxHud — always-on player HUD pinned top-left.
//
// Replaces the old StyxUiProbe demo plugin. Renders useful per-player
// status: online player count, player rank/tag, server timing (next wipe
// + restart warning), and an optional zombie-radar section that mirrors
// the StyxZombieRadar plugin's count cvar (only visible when the radar
// plugin is feeding data).
//
// Day/time deliberately omitted — vanilla compass HUD already shows it.
//
// Header text is server-branded — pulled from StyxCore.Branding (loaded
// from configs/Branding.json by the framework). Set ServerName once
// there and the HUD header + launcher header + any future branded UI
// all auto-pick it up. No per-plugin config needed.
//
// Hybrid panel design (see STYX_CAPABILITIES.md §18-21 for the broader
// pattern): StyxHud is the always-on core. Other plugins keep their own
// detail panels but ALSO push their primary cvar so StyxHud can mirror
// it as an optional section. If a plugin is unloaded its cvar stops
// updating and the section's visibility binding hides it automatically
// — no tight coupling, no plugin-load-order dependency.

using System;
using System.Linq;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxHud", "Doowkcol", "0.1.0")]
public class StyxHud : StyxPlugin
{
    public override string Description => "Always-on player HUD: players, rank, wipe + restart countdowns";

    private TimerHandle _tick;

    // Rank id → label index. Owner overrides any group; otherwise we
    // pick the highest tier the player belongs to. Default = 0.
    private const int RankDefault = 0;
    private const int RankVip     = 1;
    private const int RankAdmin   = 2;
    private const int RankOwner   = 3;

    public override void OnLoad()
    {
        // Header + subheader — read from the framework-wide branding config
        // (single source of truth, see configs/Branding.json). Registered as
        // static labels so XUi binds via {#localization('styx_hud_header')}.
        // Two server restarts needed after a Branding.json edit: first to
        // bake the labels into runtime localization, second to load them.
        var branding = StyxCore.Branding;
        string header = branding?.EffectiveHudHeader ?? "STYX";
        string subheader = branding?.HudSubheader ?? "";

        Styx.Ui.Labels.Register(this, "styx_hud_header", header);
        Styx.Ui.Labels.Register(this, "styx_hud_subheader", subheader);

        // Rank labels — XUi binds via {#localization('styx_rank_' + int(cvar(...)))}
        Styx.Ui.Labels.Register(this, "styx_rank_0", "Player");
        Styx.Ui.Labels.Register(this, "styx_rank_1", "[VIP]");
        Styx.Ui.Labels.Register(this, "styx_rank_2", "[Admin]");
        Styx.Ui.Labels.Register(this, "styx_rank_3", "[Owner]");

        Styx.Ui.Ephemeral.Register(
            "styx.world.players", "styx.hud.rank_id",
            "styx.hud.subheader_visible", "styx.hud.vanish_active");

        // Push subheader visibility once globally — branding-driven,
        // doesn't change per-player or per-tick.
        int subVis = string.IsNullOrEmpty(subheader) ? 0 : 1;
        Styx.Ui.SetVarAll("styx.hud.subheader_visible", subVis);

        _tick = Scheduler.Every(1.0, Tick, name: "StyxHud.tick");

        Log.Out("[StyxHud] Loaded v0.1.0 — header '{0}'{1}",
            header,
            string.IsNullOrEmpty(subheader) ? "" : " / sub '" + subheader + "'");
    }

    public override void OnUnload()
    {
        if (_tick != null) { _tick.Destroy(); _tick = null; }
        Styx.Ui.Labels.UnregisterAll(this);
    }

    private void Tick()
    {
        int playerCount = StyxCore.World?.PlayerCount ?? 0;
        int subVis = string.IsNullOrEmpty(StyxCore.Branding?.HudSubheader) ? 0 : 1;

        var players = StyxCore.Player?.All();
        if (players == null) return;
        foreach (var p in players)
        {
            if (p == null) continue;
            Styx.Ui.SetVar(p, "styx.world.players", playerCount);
            Styx.Ui.SetVar(p, "styx.hud.subheader_visible", subVis);

            string pid = StyxCore.Player.PlatformIdOf(p);
            Styx.Ui.SetVar(p, "styx.hud.rank_id", ResolveRankId(pid));

            // Vanish indicator — read EntityPlayer.IsSpectator directly so
            // it reflects current state regardless of which plugin toggled
            // it. Self-only: each player only sees their own vanish badge.
            Styx.Ui.SetVar(p, "styx.hud.vanish_active", p.IsSpectator ? 1f : 0f);
        }
    }

    /// <summary>Map a player to a rank tier — owner > admin > vip > default.
    /// Returns the index into our styx_rank_N labels.</summary>
    private int ResolveRankId(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return RankDefault;
        // Owner via vanilla serveradmin.xml auth tier — beats any group.
        if (StyxCore.Perms.IsOwner(pid)) return RankOwner;

        var groups = StyxCore.Perms.GetPlayerGroups(pid);
        // Highest tier wins (admin > vip > default).
        if (groups.Any(g => string.Equals(g, "admin", StringComparison.OrdinalIgnoreCase)))
            return RankAdmin;
        if (groups.Any(g => string.Equals(g, "vip", StringComparison.OrdinalIgnoreCase)))
            return RankVip;
        return RankDefault;
    }
}
