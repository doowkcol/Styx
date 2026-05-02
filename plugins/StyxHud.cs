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
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxHud", "Doowkcol", "0.2.0")]
public class StyxHud : StyxPlugin
{
    public override string Description => "Always-on player HUD: players, rank, wipe + restart countdowns";

    private TimerHandle _tick;

    // Rank labels are built dynamically from the perm system's groups.
    //   Index 0      = "Player" (no-tag fallback for default-only members)
    //   Index 1..N   = each tagged group's ChatTag, ordered priority-desc
    //   Index N+1    = "[Owner]" (vanilla auth-0 wins over everything)
    // Built on first Tick (not OnLoad) so late-loading plugins like
    // StyxLeveling have already pre-created their milestone groups.
    private const int RankDefault = 0;
    private int _ownerRankId;
    private bool _rankLabelsBuilt;
    private Dictionary<string, int> _groupRankIndex;     // group name -> label index
    private List<string> _groupsByPriorityDesc;          // resolution order

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

        // Rank labels are populated lazily on first Tick (see BuildRankLabels)
        // to give plugins like StyxLeveling time to pre-create milestone groups.
        // Pre-register slot 0 here so the XUi binding has *something* until then.
        Styx.Ui.Labels.Register(this, "styx_rank_0", "Player");

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
        // Lazy build rank labels on first tick so late-loading plugins
        // (e.g., StyxLeveling pre-creating lvl25/50/75/100) are accounted for.
        if (!_rankLabelsBuilt) BuildRankLabels();

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

    /// <summary>
    /// Snapshot every group with a non-empty ChatTag, register a rank
    /// label per group (priority-desc order), and reserve the last index
    /// for "[Owner]". Re-callable; idempotent re-registration overwrites
    /// stale label values but doesn't grow the index space.
    /// </summary>
    private void BuildRankLabels()
    {
        _groupRankIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _groupsByPriorityDesc = new List<string>();

        var taggedGroups = StyxCore.Perms.GetAllGroups()
            .Where(g => g != null && !string.IsNullOrEmpty(g.ChatTag))
            .OrderByDescending(g => g.Priority)
            .ToList();

        // Slot 0 reserved for the no-tag default ("Player").
        Styx.Ui.Labels.Register(this, "styx_rank_0", "Player");

        int idx = 1;
        foreach (var g in taggedGroups)
        {
            Styx.Ui.Labels.Register(this, "styx_rank_" + idx, g.ChatTag);
            _groupRankIndex[g.Name] = idx;
            _groupsByPriorityDesc.Add(g.Name);
            idx++;
        }

        _ownerRankId = idx;
        Styx.Ui.Labels.Register(this, "styx_rank_" + _ownerRankId, "[Owner]");

        _rankLabelsBuilt = true;
        Log.Out("[StyxHud] Rank labels built -- {0} tagged group(s) + Owner.", taggedGroups.Count);
    }

    /// <summary>
    /// Resolve a player's rank label index for the HUD.
    /// Owner (vanilla serveradmin.xml auth) wins outright; otherwise we
    /// walk the player's groups in priority-desc order and return the
    /// first index in <see cref="_groupRankIndex"/>. Falls back to 0
    /// ("Player") if no tagged group matches.
    /// </summary>
    private int ResolveRankId(string pid)
    {
        if (string.IsNullOrEmpty(pid)) return RankDefault;
        if (StyxCore.Perms.IsOwner(pid)) return _ownerRankId;

        if (_groupsByPriorityDesc == null) return RankDefault;
        var groups = StyxCore.Perms.GetPlayerGroups(pid);
        foreach (var groupName in _groupsByPriorityDesc)
        {
            if (groups.Any(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase)))
                return _groupRankIndex[groupName];
        }
        return RankDefault;
    }
}
