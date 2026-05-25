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


/* @styx-xui-windows
<window name="styxHud"
        anchor="LeftTop"
        pos="10,-10"
        width="260" height="200"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="-20">

    <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,180"     type="sliced" width="260" height="200" />
    <sprite depth="1" name="border" sprite="menu_empty3px" color="255,220,0,200" type="sliced" width="260" height="200" fillcenter="false" />

    <!-- Header — text comes from StyxHud config (HeaderText) baked
         into runtime localization at last shutdown. Default "STYX". -->
    <label depth="2" name="hdr"
           text="{#localization('styx_hud_header')}"
           font_size="20" justify="center" style="outline"
           color="255,220,0,255"
           pos="130,-6" width="260" height="22" pivot="top" />

    <!-- Vanish badge — small indicator in the top-right corner of the
         panel, visible only when the local player has IsSpectator=true.
         StyxHud's tick pushes styx.hud.vanish_active = 1 when vanished. -->
    <label depth="2" name="vanishBadge"
           text="VANISHED" font_size="11" style="outline"
           color="220,120,255,255"
           pos="195,-8" width="60" height="14"
           visible="{#cvar('styx.hud.vanish_active') == 1}" />

    <!-- Optional subheader (e.g. "Survival Hardcore", "PvP"), visible
         only when StyxHud config Subheader is non-empty. -->
    <label depth="2" name="subhdr"
           text="{#localization('styx_hud_subheader')}"
           font_size="13" justify="center"
           pos="130,-28" width="260" height="16" pivot="top"
           color="200,200,200,255"
           visible="{#cvar('styx.hud.subheader_visible') == 1}" />

    <!-- Player count + rank on one line. Y=-50 reserves space for the
         optional subheader at -28 even when subheader is hidden — the
         visible/hidden state of subheader doesn't reflow downstream
         rows in XUi, so we always reserve. -->
    <label depth="2" name="pcount"
           text="Online: {cvar(styx.world.players:0)}"
           font_size="14" pos="12,-50" width="120" height="18"
           color="180,220,255,255" />
    <label depth="2" name="rankLbl"
           text="Rank:" font_size="14"
           pos="135,-50" width="50" height="18"
           color="180,220,255,255" />
    <label depth="2" name="rankVal"
           text="{#localization('styx_rank_' + int(cvar('styx.hud.rank_id')))}"
           font_size="14" pos="180,-50" width="80" height="18"
           color="255,200,100,255" />

    <!-- Divider -->
    <sprite depth="2" name="div1" sprite="menu_empty" color="255,220,0,80"
            type="sliced" width="240" height="1" pos="10,-74" />

    <!-- Server timing: wipe countdown (always shown) -->
    <label depth="2" name="wipeLbl"
           text="Next wipe:" font_size="14"
           pos="12,-82" width="100" height="18"
           color="180,255,200,255" />
    <label depth="2" name="wipeVal"
           text="{cvar(styx.srm.wipe_d:0)}d {cvar(styx.srm.wipe_h:0)}h"
           font_size="14" justify="right"
           pos="135,-82" width="115" height="18"
           color="240,240,240,255" />

    <!-- Restart warning — visible only when SRM signals warning window -->
    <label depth="2" name="warnIcon"
           text="*" font_size="14" color="255,80,80,255"
           pos="12,-104" width="14" height="18"
           visible="{#cvar('styx.srm.restart_warning') == 1}" />
    <label depth="2" name="warnLbl"
           text="Restart in" font_size="14"
           pos="28,-104" width="80" height="18" color="255,160,80,255"
           visible="{#cvar('styx.srm.restart_warning') == 1}" />
    <label depth="2" name="warnVal"
           text="{cvar(styx.srm.restart_h:0)}h {cvar(styx.srm.restart_m:0)}m {cvar(styx.srm.restart_s:00)}s"
           font_size="14" justify="right"
           pos="105,-104" width="145" height="18" color="255,200,100,255"
           visible="{#cvar('styx.srm.restart_warning') == 1}" />

    <!-- Optional zombie-radar section -->
    <label depth="2" name="zCntLbl"
           text="Zombies ({cvar(styx.radar.radius:0)}m):" font_size="14"
           pos="12,-128" width="160" height="18" color="255,180,180,255"
           visible="{#cvar('styx.radar.radius') &gt; 0}" />
    <label depth="2" name="zCntVal"
           text="{cvar(styx.radar.count:0)}" font_size="14" justify="right"
           pos="180,-128" width="70" height="18" color="255,220,120,255"
           visible="{#cvar('styx.radar.radius') &gt; 0}" />

    <!-- Economy section. Visibility gated on styx.eco.loaded so the
         row hides when StyxEconomy isn't installed (cvar absent => 0).
         Currency name is plugin-config-driven, baked at boot via
         Styx.Ui.Labels.Register("styx_eco_currency", ...). -->
    <label depth="2" name="ecoLbl"
           text="{#localization('styx_eco_currency')}:" font_size="14"
           pos="12,-150" width="160" height="18" color="255,220,140,255"
           visible="{#cvar('styx.eco.loaded') == 1}" />
    <label depth="2" name="ecoVal"
           text="{cvar(styx.eco.balance:0)}" font_size="14" justify="right"
           pos="180,-150" width="70" height="18" color="255,240,180,255"
           visible="{#cvar('styx.eco.loaded') == 1}" />

    <!-- Level + XP section (StyxLeveling). Hidden if plugin not loaded. -->
    <label depth="2" name="lvlLbl"
           text="Level:" font_size="14"
           pos="12,-168" width="60" height="18" color="200,220,255,255"
           visible="{#cvar('styx.xp.loaded') == 1}" />
    <label depth="2" name="lvlVal"
           text="{cvar(styx.xp.level:0)}" font_size="14"
           pos="76,-168" width="40" height="18" color="255,240,180,255"
           visible="{#cvar('styx.xp.loaded') == 1}" />
    <label depth="2" name="xpVal"
           text="{cvar(styx.xp.balance:0)} / {cvar(styx.xp.next:0)}"
           font_size="12" justify="right"
           pos="120,-168" width="130" height="18" color="180,200,220,255"
           visible="{#cvar('styx.xp.loaded') == 1}" />

    <!-- Hint footer -->
    <label depth="2" name="hint"
           text="/m for menu" font_size="11" justify="center"
           pos="130,-185" width="260" height="14" pivot="top"
           color="160,160,160,255" />
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxHud" />
*/

/* @styx-patch xui-windows
<!-- Vanilla windowGroupBars (the team-mate name + HP + direction-arrow
     readout) is anchored LeftTop at pos=(9,-88) — overlaps the StyxHud
     panel which occupies LeftTop ~10,-10 to ~290,-280. Visual collision:
     the team-mate readout text shows over our HUD panel.

     Patch flips the window's anchor to RightTop and mirrors the child
     positions onto the right side, BELOW the area used by the vanilla
     biome label / version / tutorial-challenge HUD elements (which run
     from y≈0 down to y≈240 in normal play). Grid starts at y=-280 so
     the first row clears the tutorial area with 40px breathing room.

     IMPORTANT — cell visual extent vs grid cell_width.
     The grid declares cell_width=168, but the party_entry control
     inside (controls.xml:1034) renders an inner rect of width=200 PLUS
     an arrowContent indicator at pos=216,-18 with style icon22px
     (pivot=center → right edge at x=227 in cell-local coords). The
     direction arrow sits OUTSIDE the nominal 168px grid cell. Setting
     grid pos by cell_width alone clips the arrow off the right edge.

     Numbers (right margin = 10px):
       grid pos.x = -(arrow_right_edge + margin) = -(227 + 10) = -237
       voiceStatus mic icon: pos=(-222,-260), pivot=center. Aligns
         horizontally with the per-row speaker icon (cell-local x=15)
         to mirror the vanilla LeftTop layout's mic-over-speaker
         alignment. 20px above the first row (mirrors original 88-68
         vertical offset between mic and grid).
       grid hud (party + companion both): pos=(-237,-280). Single
         XPath set@pos updates both elements (XmlPatchMethods.SetByXPath
         applies to all matching nodes — verified in decomp).

     Far-team distance label (controls.xml:1053) extends to ~x=290 in
     cell-local coords; at -237 it would clip slightly when a team-mate
     is far enough away to show distance. If that becomes a real
     annoyance, push grid pos to -300 — no rebuild needed.

     Vanilla element names are case-sensitive in XPath — windowGroupBars
     and the inner sprite/grid names must match exactly. -->
<set xpath="/windows/window[@name='windowGroupBars']/@anchor">RightTop</set>
<set xpath="/windows/window[@name='windowGroupBars']/sprite[@name='voiceStatus']/@pos">-222,-260</set>
<set xpath="/windows/window[@name='windowGroupBars']/grid[@name='hud']/@pos">-237,-280</set>
*/

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
