// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// PermEditor — UI-driven permission management for groups.
//
// Three-stage navigation (one window, stage cvar gates which rect shows):
//   stage 0 → pick a GROUP    (8 rows max)
//   stage 1 → pick a PLUGIN   (20-row sliding window over the full list,
//                              "(All)" is row 0 — show every perm)
//   stage 2 → edit the group × plugin slice:
//              3 leading config rows (priority / tag / color) + perms filtered
//              to the chosen plugin (16 rows max, plenty when filtered)
//
// Why three stages instead of one: we've passed ~20 registered perms and the
// single-page list was silently truncating (.Take(16)). The plugin pick is
// stable + small (one row per plugin) so it's the natural cut.
//
// Stage 1 sliding window (since plugin count crossed 20 at v0.3): the XUi
// has a fixed 20-row layout, but the underlying list can be arbitrarily
// long. We track an absolute selection (`_pluginAbsSel`) and a view offset
// (`_pluginViewOffset`); the visible cursor `styx.pe.sel` is `absSel -
// viewOffset` (always 0..19). Scrolling past row 19 advances the window;
// wrapping from last → first snaps viewOffset to 0; first → last snaps
// viewOffset to count - 20. A small "X/Y" badge (top-right of the panel)
// shows current absolute position so the player has a "page" landmark.
//
// Player → group assignment STAYS in chat (`/perm addto <player> <group>`).
// That's the only "dynamic string" piece and chat is fine for it.
//
// Permission required: styx.perm.admin (same as PermManager mutations).
//
// Limitations:
//   - Labels are baked at OnServerInitialized → groups / perms / plugin
//     owners added LATER won't appear in UI until next server restart.
//     Acceptable since this is rare admin work.
//   - 8 group rows max + 16 perm-per-plugin rows max; bump constants and
//     mirror rows in XUi if a server grows beyond that. Plugin list is now
//     a sliding window so no hard cap there.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Permissions;
using Styx.Plugins;


/* @styx-xui-windows
<!--
    styxPermEditor — UI for granting/revoking perms per group.
    Two stages share one window via stage-cvar gating:
      styx.pe.stage == 0 → group picker rect
      styx.pe.stage == 1 → perm-toggle rect
    Group + perm names render via static labels (perm_grp_N,
    perm_def_N) registered by PermEditor.OnLoad and persisted to
    StyxRuntime/Localization.txt.
-->
<window name="styxPermEditor"
        anchor="CenterCenter" pos="-280,340"
        width="560" height="680"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="55">

    <!-- Whole window gated by .open -->
    <rect name="wrap" pos="0,0" width="560" height="680"
          visible="{#cvar('styx.pe.open') == 1}">

        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,220"        type="sliced" width="560" height="680" />
        <sprite depth="1" name="border" sprite="menu_empty3px" color="160,180,255,220"  type="sliced" width="560" height="680" fillcenter="false" />

        <!-- ===== STAGE 0: GROUP PICKER ===== -->
        <rect name="stage0" pos="0,0" width="560" height="680"
              visible="{#cvar('styx.pe.stage') == 0}">

            <label depth="2" name="hdr0" text="PERM EDITOR — pick a group"
                   font_size="22" justify="center" style="outline"
                   color="160,180,255,255"
                   pos="280,-10" width="560" height="28" pivot="top" />

            <!-- 8 group rows. Y: -52, -78, -104, -130, -156, -182, -208, -234 (26px steps) -->
            <label depth="3" name="gc0" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-52" width="20" height="22" visible="{#cvar('styx.pe.sel') == 0}" />
            <label depth="3" name="go0" text="{#localization('perm_grp_' + int(cvar('styx.pe.group0_id')))}"
                   font_size="20" pos="50,-52" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 0}" />

            <label depth="3" name="gc1" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-78" width="20" height="22" visible="{#cvar('styx.pe.sel') == 1}" />
            <label depth="3" name="go1" text="{#localization('perm_grp_' + int(cvar('styx.pe.group1_id')))}"
                   font_size="20" pos="50,-78" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 1}" />

            <label depth="3" name="gc2" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-104" width="20" height="22" visible="{#cvar('styx.pe.sel') == 2}" />
            <label depth="3" name="go2" text="{#localization('perm_grp_' + int(cvar('styx.pe.group2_id')))}"
                   font_size="20" pos="50,-104" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 2}" />

            <label depth="3" name="gc3" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-130" width="20" height="22" visible="{#cvar('styx.pe.sel') == 3}" />
            <label depth="3" name="go3" text="{#localization('perm_grp_' + int(cvar('styx.pe.group3_id')))}"
                   font_size="20" pos="50,-130" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 3}" />

            <label depth="3" name="gc4" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-156" width="20" height="22" visible="{#cvar('styx.pe.sel') == 4}" />
            <label depth="3" name="go4" text="{#localization('perm_grp_' + int(cvar('styx.pe.group4_id')))}"
                   font_size="20" pos="50,-156" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 4}" />

            <label depth="3" name="gc5" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-182" width="20" height="22" visible="{#cvar('styx.pe.sel') == 5}" />
            <label depth="3" name="go5" text="{#localization('perm_grp_' + int(cvar('styx.pe.group5_id')))}"
                   font_size="20" pos="50,-182" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 5}" />

            <label depth="3" name="gc6" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-208" width="20" height="22" visible="{#cvar('styx.pe.sel') == 6}" />
            <label depth="3" name="go6" text="{#localization('perm_grp_' + int(cvar('styx.pe.group6_id')))}"
                   font_size="20" pos="50,-208" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 6}" />

            <label depth="3" name="gc7" text="&gt;" font_size="22" color="160,180,255,255"
                   pos="22,-234" width="20" height="22" visible="{#cvar('styx.pe.sel') == 7}" />
            <label depth="3" name="go7" text="{#localization('perm_grp_' + int(cvar('styx.pe.group7_id')))}"
                   font_size="20" pos="50,-234" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.group_count') &gt; 7}" />

            <label depth="3" name="hint0"
                   text="Group details (priority, tag, perm count) whispered to chat as you navigate."
                   font_size="12" justify="center"
                   pos="280,-340" width="560" height="16" pivot="top"
                   color="200,200,160,255" />
            <label depth="3" name="legend0"
                   text="[SCROLL] navigate   [LMB] open group   [RMB] close"
                   font_size="13" justify="center"
                   pos="280,-370" width="560" height="18" pivot="top"
                   color="180,180,180,255" />
        </rect>

        <!-- ===== STAGE 1: PLUGIN PICKER (12 rows) =====
             Picks which plugin's perms to show in stage 2. Row 0 is
             always "(All plugins)" — selecting it shows the full list.
             Labels come from perm_plugin_N registered by PermEditor. -->
        <rect name="stage1" pos="0,0" width="560" height="680"
              visible="{#cvar('styx.pe.stage') == 1}">

            <label depth="2" name="hdr1"
                   text="PICK A PLUGIN — filters perms shown next"
                   font_size="22" justify="center" style="outline"
                   color="255,200,140,255"
                   pos="280,-10" width="560" height="28" pivot="top" />

            <label depth="2" name="hdr1b"
                   text="group: {#localization('perm_grp_' + int(cvar('styx.pe.selected_group_id')))}"
                   font_size="14" justify="center"
                   color="160,255,180,255"
                   pos="280,-38" width="560" height="18" pivot="top" />

            <!-- Sliding-window position badge (top-right). Hidden when
                 the picker is empty so it never shows "0/0". -->
            <label depth="3" name="hdr1pos"
                   text="{cvar(styx.pe.plugin_pos:1)}/{cvar(styx.pe.plugin_total:1)}"
                   font_size="13" justify="right"
                   color="180,180,180,255"
                   pos="510,-38" width="50" height="18" pivot="top"
                   visible="{#cvar('styx.pe.plugin_total') &gt; 0}" />

            <!-- 20 plugin rows. Y: -68, -94, -120, -146, -172, -198, -224, -250, -276, -302, -328, -354, -380, -406, -432, -458, -484, -510, -536, -562 (26px steps) -->
            <label depth="3" name="ppc0" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-68" width="20" height="22" visible="{#cvar('styx.pe.sel') == 0}" />
            <label depth="3" name="ppo0" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin0_id')))}"
                   font_size="20" pos="50,-68" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 0}" />

            <label depth="3" name="ppc1" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-94" width="20" height="22" visible="{#cvar('styx.pe.sel') == 1}" />
            <label depth="3" name="ppo1" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin1_id')))}"
                   font_size="20" pos="50,-94" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 1}" />

            <label depth="3" name="ppc2" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-120" width="20" height="22" visible="{#cvar('styx.pe.sel') == 2}" />
            <label depth="3" name="ppo2" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin2_id')))}"
                   font_size="20" pos="50,-120" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 2}" />

            <label depth="3" name="ppc3" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-146" width="20" height="22" visible="{#cvar('styx.pe.sel') == 3}" />
            <label depth="3" name="ppo3" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin3_id')))}"
                   font_size="20" pos="50,-146" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 3}" />

            <label depth="3" name="ppc4" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-172" width="20" height="22" visible="{#cvar('styx.pe.sel') == 4}" />
            <label depth="3" name="ppo4" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin4_id')))}"
                   font_size="20" pos="50,-172" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 4}" />

            <label depth="3" name="ppc5" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-198" width="20" height="22" visible="{#cvar('styx.pe.sel') == 5}" />
            <label depth="3" name="ppo5" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin5_id')))}"
                   font_size="20" pos="50,-198" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 5}" />

            <label depth="3" name="ppc6" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-224" width="20" height="22" visible="{#cvar('styx.pe.sel') == 6}" />
            <label depth="3" name="ppo6" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin6_id')))}"
                   font_size="20" pos="50,-224" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 6}" />

            <label depth="3" name="ppc7" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-250" width="20" height="22" visible="{#cvar('styx.pe.sel') == 7}" />
            <label depth="3" name="ppo7" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin7_id')))}"
                   font_size="20" pos="50,-250" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 7}" />

            <label depth="3" name="ppc8" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-276" width="20" height="22" visible="{#cvar('styx.pe.sel') == 8}" />
            <label depth="3" name="ppo8" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin8_id')))}"
                   font_size="20" pos="50,-276" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 8}" />

            <label depth="3" name="ppc9" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-302" width="20" height="22" visible="{#cvar('styx.pe.sel') == 9}" />
            <label depth="3" name="ppo9" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin9_id')))}"
                   font_size="20" pos="50,-302" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 9}" />

            <label depth="3" name="ppc10" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-328" width="20" height="22" visible="{#cvar('styx.pe.sel') == 10}" />
            <label depth="3" name="ppo10" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin10_id')))}"
                   font_size="20" pos="50,-328" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 10}" />

            <label depth="3" name="ppc11" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-354" width="20" height="22" visible="{#cvar('styx.pe.sel') == 11}" />
            <label depth="3" name="ppo11" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin11_id')))}"
                   font_size="20" pos="50,-354" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 11}" />

            <label depth="3" name="ppc12" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-380" width="20" height="22" visible="{#cvar('styx.pe.sel') == 12}" />
            <label depth="3" name="ppo12" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin12_id')))}"
                   font_size="20" pos="50,-380" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 12}" />

            <label depth="3" name="ppc13" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-406" width="20" height="22" visible="{#cvar('styx.pe.sel') == 13}" />
            <label depth="3" name="ppo13" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin13_id')))}"
                   font_size="20" pos="50,-406" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 13}" />

            <label depth="3" name="ppc14" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-432" width="20" height="22" visible="{#cvar('styx.pe.sel') == 14}" />
            <label depth="3" name="ppo14" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin14_id')))}"
                   font_size="20" pos="50,-432" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 14}" />

            <label depth="3" name="ppc15" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-458" width="20" height="22" visible="{#cvar('styx.pe.sel') == 15}" />
            <label depth="3" name="ppo15" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin15_id')))}"
                   font_size="20" pos="50,-458" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 15}" />

            <label depth="3" name="ppc16" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-484" width="20" height="22" visible="{#cvar('styx.pe.sel') == 16}" />
            <label depth="3" name="ppo16" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin16_id')))}"
                   font_size="20" pos="50,-484" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 16}" />

            <label depth="3" name="ppc17" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-510" width="20" height="22" visible="{#cvar('styx.pe.sel') == 17}" />
            <label depth="3" name="ppo17" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin17_id')))}"
                   font_size="20" pos="50,-510" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 17}" />

            <label depth="3" name="ppc18" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-536" width="20" height="22" visible="{#cvar('styx.pe.sel') == 18}" />
            <label depth="3" name="ppo18" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin18_id')))}"
                   font_size="20" pos="50,-536" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 18}" />

            <label depth="3" name="ppc19" text="&gt;" font_size="22" color="255,200,140,255"
                   pos="22,-562" width="20" height="22" visible="{#cvar('styx.pe.sel') == 19}" />
            <label depth="3" name="ppo19" text="{#localization('perm_plugin_' + int(cvar('styx.pe.plugin19_id')))}"
                   font_size="20" pos="50,-562" width="480" height="22" color="240,240,240,255"
                   visible="{#cvar('styx.pe.plugin_count') &gt; 19}" />

            <label depth="3" name="hint1p"
                   text="(All plugins) = unfiltered. List scrolls past 20 — top-right badge shows your position. Perm count per plugin whispered as you navigate."
                   font_size="12" justify="center"
                   pos="280,-602" width="560" height="16" pivot="top"
                   color="200,200,160,255" />
            <label depth="3" name="legend1p"
                   text="[SCROLL] navigate   [LMB] pick   [RMB] back to groups"
                   font_size="13" justify="center"
                   pos="280,-632" width="560" height="18" pivot="top"
                   color="180,180,180,255" />
        </rect>

        <!-- ===== STAGE 2: GROUP CONFIG (3 rows) + PERM TOGGLES (16 rows) ===== -->
        <rect name="stage2" pos="0,0" width="560" height="680"
              visible="{#cvar('styx.pe.stage') == 2}">

            <label depth="2" name="hdr2"
                   text="EDITING: {#localization('perm_grp_' + int(cvar('styx.pe.selected_group_id')))} — {#localization('perm_plugin_' + int(cvar('styx.pe.selected_plugin_id')))}"
                   font_size="22" justify="center" style="outline"
                   color="160,255,180,255"
                   pos="280,-8" width="560" height="24" pivot="top" />

            <!-- Sliding-window position badge (top-right). Only shown
                 when the cursor is on a perm row (perm_pos > 0). -->
            <label depth="3" name="hdr2pos"
                   text="{cvar(styx.pe.perm_pos:1)}/{cvar(styx.pe.perm_total:1)}"
                   font_size="13" justify="right"
                   color="180,180,180,255"
                   pos="510,-12" width="50" height="18" pivot="top"
                   visible="{#cvar('styx.pe.perm_pos') &gt; 0}" />

            <!-- ===== Group config rows (sel 0..2) ===== -->

            <!-- Row 0: Priority -->
            <label depth="3" name="cfgC0" text="&gt;" font_size="20" color="255,180,80,255"
                   pos="22,-32" width="20" height="20" visible="{#cvar('styx.pe.sel') == 0}" />
            <label depth="3" name="cfgL0" text="Priority"
                   font_size="16" pos="50,-32" width="180" height="20" color="255,200,140,255" />
            <label depth="3" name="cfgV0" text="{cvar(styx.pe.cfg_priority:0)}"
                   font_size="16" pos="240,-32" width="100" height="20" color="240,240,240,255" />
            <label depth="3" name="cfgH0" text="LMB cycles +10"
                   font_size="12" justify="right" pos="450,-32" width="100" height="20" color="160,160,160,255" />

            <!-- Row 1: Chat Tag -->
            <label depth="3" name="cfgC1" text="&gt;" font_size="20" color="255,180,80,255"
                   pos="22,-54" width="20" height="20" visible="{#cvar('styx.pe.sel') == 1}" />
            <label depth="3" name="cfgL1" text="Chat Tag"
                   font_size="16" pos="50,-54" width="180" height="20" color="255,200,140,255" />
            <label depth="3" name="cfgV1"
                   text="{#localization('perm_tag_' + int(cvar('styx.pe.cfg_tag_id')))}"
                   font_size="16" pos="240,-54" width="180" height="20" color="240,240,240,255" />
            <label depth="3" name="cfgH1" text="LMB cycles preset"
                   font_size="12" justify="right" pos="430,-54" width="120" height="20" color="160,160,160,255" />

            <!-- Row 2: Chat Color -->
            <label depth="3" name="cfgC2" text="&gt;" font_size="20" color="255,180,80,255"
                   pos="22,-76" width="20" height="20" visible="{#cvar('styx.pe.sel') == 2}" />
            <label depth="3" name="cfgL2" text="Chat Color"
                   font_size="16" pos="50,-76" width="180" height="20" color="255,200,140,255" />
            <label depth="3" name="cfgV2"
                   text="{#localization('perm_color_' + int(cvar('styx.pe.cfg_color_id')))}"
                   font_size="16" pos="240,-76" width="120" height="20" color="240,240,240,255" />
            <label depth="3" name="cfgH2" text="LMB cycles preset"
                   font_size="12" justify="right" pos="430,-76" width="120" height="20" color="160,160,160,255" />

            <!-- Visual divider between config + perms -->
            <sprite depth="2" name="div1" sprite="menu_empty" color="160,255,180,80"
                    type="sliced" width="540" height="1" pos="10,-94" />

            <!-- ===== Perm rows (sel 3..14) ===== -->
            <!-- Y starts at -106; 22px steps. Cursor sel offsets shifted by +3. -->
            <label depth="3" name="pc0" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-106" width="20" height="20" visible="{#cvar('styx.pe.sel') == 3}" />
            <label depth="3" name="pn0" text="{#localization('perm_def_' + int(cvar('styx.pe.perm0_id')))}"
                   font_size="16" pos="50,-106" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 0}" />
            <label depth="3" name="pg0" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-108" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 0  and cvar('styx.pe.perm0_status') == 1}" />
            <label depth="3" name="pn0n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-108" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 0  and cvar('styx.pe.perm0_status') == 0}" />

            <label depth="3" name="pc1" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-128" width="20" height="20" visible="{#cvar('styx.pe.sel') == 4}" />
            <label depth="3" name="pn1" text="{#localization('perm_def_' + int(cvar('styx.pe.perm1_id')))}"
                   font_size="16" pos="50,-128" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 1}" />
            <label depth="3" name="pg1" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-130" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 1  and cvar('styx.pe.perm1_status') == 1}" />
            <label depth="3" name="pn1n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-130" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 1  and cvar('styx.pe.perm1_status') == 0}" />

            <label depth="3" name="pc2" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-150" width="20" height="20" visible="{#cvar('styx.pe.sel') == 5}" />
            <label depth="3" name="pn2" text="{#localization('perm_def_' + int(cvar('styx.pe.perm2_id')))}"
                   font_size="16" pos="50,-150" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 2}" />
            <label depth="3" name="pg2" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-152" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 2  and cvar('styx.pe.perm2_status') == 1}" />
            <label depth="3" name="pn2n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-152" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 2  and cvar('styx.pe.perm2_status') == 0}" />

            <label depth="3" name="pc3" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-172" width="20" height="20" visible="{#cvar('styx.pe.sel') == 6}" />
            <label depth="3" name="pn3" text="{#localization('perm_def_' + int(cvar('styx.pe.perm3_id')))}"
                   font_size="16" pos="50,-172" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 3}" />
            <label depth="3" name="pg3" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-174" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 3  and cvar('styx.pe.perm3_status') == 1}" />
            <label depth="3" name="pn3n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-174" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 3  and cvar('styx.pe.perm3_status') == 0}" />

            <label depth="3" name="pc4" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-194" width="20" height="20" visible="{#cvar('styx.pe.sel') == 7}" />
            <label depth="3" name="pn4" text="{#localization('perm_def_' + int(cvar('styx.pe.perm4_id')))}"
                   font_size="16" pos="50,-194" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 4}" />
            <label depth="3" name="pg4" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-196" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 4  and cvar('styx.pe.perm4_status') == 1}" />
            <label depth="3" name="pn4n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-196" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 4  and cvar('styx.pe.perm4_status') == 0}" />

            <label depth="3" name="pc5" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-216" width="20" height="20" visible="{#cvar('styx.pe.sel') == 8}" />
            <label depth="3" name="pn5" text="{#localization('perm_def_' + int(cvar('styx.pe.perm5_id')))}"
                   font_size="16" pos="50,-216" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 5}" />
            <label depth="3" name="pg5" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-218" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 5  and cvar('styx.pe.perm5_status') == 1}" />
            <label depth="3" name="pn5n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-218" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 5  and cvar('styx.pe.perm5_status') == 0}" />

            <label depth="3" name="pc6" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-238" width="20" height="20" visible="{#cvar('styx.pe.sel') == 9}" />
            <label depth="3" name="pn6" text="{#localization('perm_def_' + int(cvar('styx.pe.perm6_id')))}"
                   font_size="16" pos="50,-238" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 6}" />
            <label depth="3" name="pg6" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-240" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 6  and cvar('styx.pe.perm6_status') == 1}" />
            <label depth="3" name="pn6n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-240" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 6  and cvar('styx.pe.perm6_status') == 0}" />

            <label depth="3" name="pc7" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-260" width="20" height="20" visible="{#cvar('styx.pe.sel') == 10}" />
            <label depth="3" name="pn7" text="{#localization('perm_def_' + int(cvar('styx.pe.perm7_id')))}"
                   font_size="16" pos="50,-260" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 7}" />
            <label depth="3" name="pg7" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-262" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 7  and cvar('styx.pe.perm7_status') == 1}" />
            <label depth="3" name="pn7n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-262" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 7  and cvar('styx.pe.perm7_status') == 0}" />

            <label depth="3" name="pc8" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-282" width="20" height="20" visible="{#cvar('styx.pe.sel') == 11}" />
            <label depth="3" name="pn8" text="{#localization('perm_def_' + int(cvar('styx.pe.perm8_id')))}"
                   font_size="16" pos="50,-282" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 8}" />
            <label depth="3" name="pg8" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-284" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 8  and cvar('styx.pe.perm8_status') == 1}" />
            <label depth="3" name="pn8n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-284" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 8  and cvar('styx.pe.perm8_status') == 0}" />

            <label depth="3" name="pc9" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-304" width="20" height="20" visible="{#cvar('styx.pe.sel') == 12}" />
            <label depth="3" name="pn9" text="{#localization('perm_def_' + int(cvar('styx.pe.perm9_id')))}"
                   font_size="16" pos="50,-304" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 9}" />
            <label depth="3" name="pg9" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-306" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 9  and cvar('styx.pe.perm9_status') == 1}" />
            <label depth="3" name="pn9n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-306" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 9  and cvar('styx.pe.perm9_status') == 0}" />

            <label depth="3" name="pc10" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-326" width="20" height="20" visible="{#cvar('styx.pe.sel') == 13}" />
            <label depth="3" name="pn10" text="{#localization('perm_def_' + int(cvar('styx.pe.perm10_id')))}"
                   font_size="16" pos="50,-326" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 10}" />
            <label depth="3" name="pg10" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-328" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 10  and cvar('styx.pe.perm10_status') == 1}" />
            <label depth="3" name="pn10n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-328" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 10  and cvar('styx.pe.perm10_status') == 0}" />

            <label depth="3" name="pc11" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-348" width="20" height="20" visible="{#cvar('styx.pe.sel') == 14}" />
            <label depth="3" name="pn11" text="{#localization('perm_def_' + int(cvar('styx.pe.perm11_id')))}"
                   font_size="16" pos="50,-348" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 11}" />
            <label depth="3" name="pg11" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-350" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 11  and cvar('styx.pe.perm11_status') == 1}" />
            <label depth="3" name="pn11n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-350" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 11  and cvar('styx.pe.perm11_status') == 0}" />

            <label depth="3" name="pc12" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-370" width="20" height="20" visible="{#cvar('styx.pe.sel') == 15}" />
            <label depth="3" name="pn12" text="{#localization('perm_def_' + int(cvar('styx.pe.perm12_id')))}"
                   font_size="16" pos="50,-370" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 12}" />
            <label depth="3" name="pg12" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-372" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 12  and cvar('styx.pe.perm12_status') == 1}" />
            <label depth="3" name="pn12n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-372" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 12  and cvar('styx.pe.perm12_status') == 0}" />

            <label depth="3" name="pc13" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-392" width="20" height="20" visible="{#cvar('styx.pe.sel') == 16}" />
            <label depth="3" name="pn13" text="{#localization('perm_def_' + int(cvar('styx.pe.perm13_id')))}"
                   font_size="16" pos="50,-392" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 13}" />
            <label depth="3" name="pg13" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-394" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 13  and cvar('styx.pe.perm13_status') == 1}" />
            <label depth="3" name="pn13n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-394" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 13  and cvar('styx.pe.perm13_status') == 0}" />

            <label depth="3" name="pc14" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-414" width="20" height="20" visible="{#cvar('styx.pe.sel') == 17}" />
            <label depth="3" name="pn14" text="{#localization('perm_def_' + int(cvar('styx.pe.perm14_id')))}"
                   font_size="16" pos="50,-414" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 14}" />
            <label depth="3" name="pg14" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-416" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 14  and cvar('styx.pe.perm14_status') == 1}" />
            <label depth="3" name="pn14n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-416" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 14  and cvar('styx.pe.perm14_status') == 0}" />

            <label depth="3" name="pc15" text="&gt;" font_size="20" color="160,255,180,255"
                   pos="22,-436" width="20" height="20" visible="{#cvar('styx.pe.sel') == 18}" />
            <label depth="3" name="pn15" text="{#localization('perm_def_' + int(cvar('styx.pe.perm15_id')))}"
                   font_size="16" pos="50,-436" width="380" height="20" color="240,240,240,255"
                   visible="{#cvar('styx.pe.perm_count') &gt; 15}" />
            <label depth="3" name="pg15" text="GRANTED" font_size="13" color="100,220,120,255"
                   pos="450,-438" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 15  and cvar('styx.pe.perm15_status') == 1}" />
            <label depth="3" name="pn15n" text="not granted" font_size="13" color="160,160,160,255"
                   pos="450,-438" width="100" height="18"
                   visible="{#cvar('styx.pe.perm_count') &gt; 15  and cvar('styx.pe.perm15_status') == 0}" />

            <label depth="3" name="hint1"
                   text="Top 3 rows edit group identity. Below = perm grants. Long lists scroll past 16 — top-right badge shows position. Description whispered as you navigate."
                   font_size="12" justify="center"
                   pos="280,-490" width="560" height="16" pivot="top"
                   color="200,200,160,255" />
            <label depth="3" name="legend1"
                   text="[SCROLL] navigate   [LMB] cycle/toggle   [RMB] back to plugins"
                   font_size="13" justify="center"
                   pos="280,-540" width="560" height="18" pivot="top"
                   color="180,180,180,255" />
        </rect>
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxPermEditor" />
*/

[Info("PermEditor", "Doowkcol", "0.3.0")]
public class PermEditor : StyxPlugin
{
    public override string Description => "UI for granting/revoking plugin perms per group (group → plugin → perms)";

    private const string PermAdmin = "styx.perm.admin";  // Same gate as PermManager

    private const int MaxGroupRows  = 8;
    private const int MaxPluginRows = 20;
    private const int MaxPermRows   = 16;
    private const int ConfigRowCount = 3;  // Stage 2 has 3 leading config rows: priority / tag / color

    // Stage numbers — shared with windows.xml `{#cvar('styx.pe.stage')}` visibility.
    private const int STAGE_GROUPS  = 0;
    private const int STAGE_PLUGINS = 1;
    private const int STAGE_PERMS   = 2;

    // "(All)" sentinel — first entry in every plugin-picker list. When the
    // player picks this row, stage 2 shows EVERY known perm (unfiltered).
    private const string AllPluginsLabel = "(All plugins)";

    // Preset cycles for chat-tag editing. Index 0 of TagPresets is the "no
    // tag" sentinel — selecting it sets ChatTag = null. Storing presets
    // statically lets us register them as static labels for indexed lookup
    // (Recipe B), avoiding the dynamic-string painting limitation of XUi.
    //
    // The array MUST cover every tag a group might actually carry, otherwise
    // IndexOfTagPreset falls back to 0 and the editor shows "[None]" for
    // groups whose stored ChatTag isn't in the cycle (the disconnect bug).
    // Append new tags rather than reordering — preset indices are exposed via
    // perm_tag_<i> localization keys consumed by windows.xml.
    private static readonly string[] TagPresets =
    {
        "[None]",     // index 0 → null tag
        "[Player]",
        "[Builder]",
        "[Helper]",
        "[VIP]",
        "[Mod]",
        "[Admin]",
        "[Owner]",
        "[Survivor]",  // default group
        "[LVL25]",
        "[LVL50]",
        "[LVL75]",
        "[LVL100]",
    };

    // ChatTagColor preset cycle — array of (BBCode hex, friendly name).
    // Same constraint as TagPresets: extend rather than reorder.
    private static readonly string[] ColorPresets =
    {
        "ffffff",  // white
        "888888",  // gray
        "00ff66",  // green
        "55aaff",  // blue
        "ffaa00",  // gold (VIP)
        "ff6666",  // red
        "ff66ff",  // magenta
        "00ffff",  // cyan
        "cccccc",  // lt gray (default group)
        "88ff66",  // lime  (lvl25)
        "66bbff",  // sky   (lvl50)
        "cc88ff",  // lavender (lvl75)
        "ffd700",  // yellow (lvl100)
    };
    private static readonly string[] ColorNames =
    {
        "white", "gray", "green", "blue", "gold", "red", "magenta", "cyan",
        "lt gray", "lime", "sky", "lavender", "yellow",
    };

    // Priority cycle: 0..200 in steps of 10.
    private const int PriorityStep = 10;
    private const int PriorityMax  = 200;

    // Per-player session state
    private readonly HashSet<int> _open = new HashSet<int>();
    private readonly Dictionary<int, string> _selectedGroup  = new Dictionary<int, string>();
    /// <summary>The plugin-owner string the user picked, or null = show all.</summary>
    private readonly Dictionary<int, string> _selectedPlugin = new Dictionary<int, string>();
    /// <summary>Absolute selected index in the full plugin list (stage 1
    /// sliding window). Decoupled from the visible cvar `styx.pe.sel` which
    /// is always 0..MaxPluginRows-1.</summary>
    private readonly Dictionary<int, int> _pluginAbsSel = new Dictionary<int, int>();
    /// <summary>Top of the visible window into the full plugin list.</summary>
    private readonly Dictionary<int, int> _pluginViewOffset = new Dictionary<int, int>();
    /// <summary>Absolute selected index in stage 2 (config rows + full filtered
    /// perm list). 0..ConfigRowCount-1 = config rows, ConfigRowCount.. = perms.
    /// Decoupled from the visible cvar `styx.pe.sel`.</summary>
    private readonly Dictionary<int, int> _permAbsSel = new Dictionary<int, int>();
    /// <summary>Top of the visible window into the full filtered perm list.
    /// Config rows are fixed; only perm rows below them slide.</summary>
    private readonly Dictionary<int, int> _permViewOffset = new Dictionary<int, int>();

    // Snapshots taken at OpenFor — index-stable for the duration of one
    // menu session. New groups/perms/plugins added after open won't show
    // until the menu is reopened.
    private readonly Dictionary<int, List<GroupData>> _groupSnapshot =
        new Dictionary<int, List<GroupData>>();
    /// <summary>ALL known perms — stage 2 filters this by selected plugin
    /// before rendering. Not capped at MaxPermRows so the full registry
    /// survives; per-plugin slices comfortably fit the XUi row budget.</summary>
    private readonly Dictionary<int, List<PermissionManager.KnownPermission>> _permSnapshot =
        new Dictionary<int, List<PermissionManager.KnownPermission>>();
    /// <summary>Ordered plugin-owner list for the stage 1 picker. Index 0
    /// is always the "(All plugins)" sentinel — picking it means the stage 2
    /// perm slice is unfiltered.</summary>
    private readonly Dictionary<int, List<string>> _pluginSnapshot =
        new Dictionary<int, List<string>>();

    public override void OnLoad()
    {
        Styx.Ui.Menu.Register(this, "Perm Editor", OpenFor, permission: PermAdmin);

        Styx.Ui.Ephemeral.Register(
            "styx.pe.open", "styx.pe.stage", "styx.pe.sel",
            "styx.pe.group_count", "styx.pe.plugin_count", "styx.pe.perm_count",
            "styx.pe.plugin_total", "styx.pe.plugin_pos",
            "styx.pe.perm_total",   "styx.pe.perm_pos",
            "styx.pe.selected_group_id", "styx.pe.selected_plugin_id",
            // Group config row cvars (stage 2 leading rows)
            "styx.pe.cfg_priority", "styx.pe.cfg_tag_id", "styx.pe.cfg_color_id");
        for (int i = 0; i < MaxGroupRows; i++)
            Styx.Ui.Ephemeral.Register("styx.pe.group" + i + "_id");
        for (int i = 0; i < MaxPluginRows; i++)
            Styx.Ui.Ephemeral.Register("styx.pe.plugin" + i + "_id");
        for (int i = 0; i < MaxPermRows; i++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.pe.perm" + i + "_id",
                "styx.pe.perm" + i + "_status");
        }

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Open the /m → Perm Editor sub-menu", Name);

        // First boot: BuildLabels() runs again at OnServerInitialized once
        // every plugin has registered its perms (otherwise late-registering
        // plugins would render as raw placeholder keys).
        // Hot-reload: OnServerInitialized doesn't fire again, so call here
        // too -- by then every other plugin's perms are already in the
        // registry, and this run also re-persists tag/color labels into
        // Mods/StyxRuntime/Config/Localization.txt for next boot. The
        // first-boot call is a harmless duplicate of OnServerInitialized.
        BuildLabels();

        Log.Out("[PermEditor] Loaded v0.3.0 — perm: {0} (labels built in OnLoad + OnServerInitialized)", PermAdmin);
    }

    /// <summary>
    /// First-boot label rebake. OnLoad already calls BuildLabels, but plugins
    /// that register perms AFTER PermEditor (load order dependent) wouldn't be
    /// in the registry yet at that point and would render as raw placeholder
    /// keys (e.g. "perm_def_3"). OnServerInitialized fires once all plugins
    /// have loaded -- run BuildLabels again so the registry is complete.
    /// On hot-reload this hook does NOT fire, so OnLoad's call carries.
    /// </summary>
    void OnServerInitialized()
    {
        BuildLabels();
        var perms = StyxCore.Perms.AllKnown;
        Log.Out("[PermEditor] Labels built — {0} groups, {1} known perms, {2} plugin owners ready",
            StyxCore.Perms.GetAllGroups().Count, perms.Count, DistinctOwners(perms).Count);
    }

    public override void OnUnload()
    {
        foreach (var eid in _open)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.pe.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _open.Clear();
        _selectedGroup.Clear();
        _selectedPlugin.Clear();
        _groupSnapshot.Clear();
        _permSnapshot.Clear();
        _pluginSnapshot.Clear();
        _pluginAbsSel.Clear();
        _pluginViewOffset.Clear();
        _permAbsSel.Clear();
        _permViewOffset.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ================================================================== labels

    /// <summary>Distinct, alpha-sorted list of plugin owners across AllKnown,
    /// prefixed by the "(All plugins)" sentinel at index 0.</summary>
    private static List<string> DistinctOwners(IReadOnlyList<PermissionManager.KnownPermission> perms)
    {
        var list = new List<string> { AllPluginsLabel };
        list.AddRange(perms
            .Select(k => k.Owner)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    private void BuildLabels()
    {
        var groups = StyxCore.Perms.GetAllGroups();
        for (int i = 0; i < groups.Count && i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_grp_" + i, groups[i].Name);
        // Pad unused slots so XUi resolves the binding to SOMETHING
        for (int i = groups.Count; i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_grp_" + i, "");

        var perms = StyxCore.Perms.AllKnown;
        // Up to 128 perms registered as labels (perm_def_N / perm_desc_N).
        // Stage 2 uses a filtered slice ≤ MaxPermRows at a time, but the full
        // registry must be addressable because indices are stable across
        // filtered vs unfiltered views.
        const int MaxLabelSlots = 128;
        for (int i = 0; i < perms.Count && i < MaxLabelSlots; i++)
        {
            Styx.Ui.Labels.Register(this, "perm_def_" + i, perms[i].Name);
            Styx.Ui.Labels.Register(this, "perm_desc_" + i,
                string.IsNullOrEmpty(perms[i].Description) ? "(no description)" : perms[i].Description);
        }
        for (int i = perms.Count; i < MaxLabelSlots; i++)
        {
            Styx.Ui.Labels.Register(this, "perm_def_" + i, "");
            Styx.Ui.Labels.Register(this, "perm_desc_" + i, "");
        }

        // Plugin-owner labels (perm_plugin_N).
        var owners = DistinctOwners(perms);
        for (int i = 0; i < owners.Count && i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_plugin_" + i, owners[i]);
        for (int i = owners.Count; i < 32; i++)
            Styx.Ui.Labels.Register(this, "perm_plugin_" + i, "");

        // Tag preset labels (perm_tag_0..N) — rendered via XUi
        // {#localization('perm_tag_' + int(cvar('styx.pe.cfg_tag_id')))}
        for (int i = 0; i < TagPresets.Length; i++)
            Styx.Ui.Labels.Register(this, "perm_tag_" + i, TagPresets[i]);

        // Color preset labels — display the friendly name, not the hex
        for (int i = 0; i < ColorNames.Length; i++)
            Styx.Ui.Labels.Register(this, "perm_color_" + i, ColorNames[i]);
    }

    private static int IndexOfTagPreset(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;  // [None]
        for (int i = 0; i < TagPresets.Length; i++)
            if (string.Equals(TagPresets[i], tag, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;  // unknown → treat as none
    }

    private static int IndexOfColorPreset(string color)
    {
        if (string.IsNullOrEmpty(color)) return 0;  // white default
        for (int i = 0; i < ColorPresets.Length; i++)
            if (string.Equals(ColorPresets[i], color, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    // ================================================================== open / close

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        string pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        if (!StyxCore.Perms.HasPermission(pid, PermAdmin))
        {
            Styx.Server.Whisper(p, "[ff6666][PermEditor] You lack permission '" + PermAdmin + "'.[-]");
            return;
        }

        // Snapshot stable indices at open time. Groups are display-row sized;
        // the perm list is kept full (filtering happens at stage 2 paint
        // time); plugins are kept full and rendered through a sliding window
        // since the count exceeds the 20-slot XUi layout.
        var groups  = StyxCore.Perms.GetAllGroups().Take(MaxGroupRows).ToList();
        var perms   = StyxCore.Perms.AllKnown.ToList();
        var plugins = DistinctOwners(perms);

        if (groups.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][PermEditor] No groups defined.[-]");
            return;
        }

        _open.Add(p.entityId);
        _groupSnapshot[p.entityId]  = groups;
        _permSnapshot[p.entityId]   = perms;
        _pluginSnapshot[p.entityId] = plugins;
        _selectedGroup.Remove(p.entityId);
        _selectedPlugin.Remove(p.entityId);

        // Render stage 0 (group picker)
        Styx.Ui.SetVar(p, "styx.pe.open", 1f);
        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_GROUPS);
        Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
        Styx.Ui.SetVar(p, "styx.pe.group_count", groups.Count);

        for (int i = 0; i < MaxGroupRows; i++)
        {
            // Index into perm_grp_N labels — find the group's row in BuildLabels' ordering
            int labelIdx = i < groups.Count
                ? IndexOfGroupInRegistry(groups[i].Name)
                : 0;
            Styx.Ui.SetVar(p, "styx.pe.group" + i + "_id", labelIdx);
        }

        Styx.Ui.Input.Acquire(p, Name);
        WhisperGroupRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.pe.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _open.Remove(p.entityId);
        _selectedGroup.Remove(p.entityId);
        _selectedPlugin.Remove(p.entityId);
        _groupSnapshot.Remove(p.entityId);
        _permSnapshot.Remove(p.entityId);
        _pluginSnapshot.Remove(p.entityId);
        _pluginAbsSel.Remove(p.entityId);
        _pluginViewOffset.Remove(p.entityId);
        _permAbsSel.Remove(p.entityId);
        _permViewOffset.Remove(p.entityId);
    }

    // ================================================================== input dispatch

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_open.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.pe.open") != 1) return;

        int stage = (int)p.Buffs.GetCustomVar("styx.pe.stage");
        switch (stage)
        {
            case STAGE_GROUPS:  HandleStageGroups(p, kind);  break;
            case STAGE_PLUGINS: HandleStagePlugins(p, kind); break;
            case STAGE_PERMS:   HandleStagePerms(p, kind);   break;
        }
    }

    // ================================================================== stage 0 — groups

    private void HandleStageGroups(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (!_groupSnapshot.TryGetValue(p.entityId, out var groups)) return;
        int sel = (int)p.Buffs.GetCustomVar("styx.pe.sel");
        int count = groups.Count;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperGroupRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.pe.sel", sel);
                WhisperGroupRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                EnterStagePlugins(p, groups[sel]);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "PermEditor.BackToLauncher");
                break;
        }
    }

    // ================================================================== stage 1 — plugins

    private void EnterStagePlugins(EntityPlayer p, GroupData group)
    {
        _selectedGroup[p.entityId] = group.Name;
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;

        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PLUGINS);
        Styx.Ui.SetVar(p, "styx.pe.selected_group_id", IndexOfGroupInRegistry(group.Name));

        _pluginAbsSel[p.entityId]     = 0;
        _pluginViewOffset[p.entityId] = 0;
        RefreshPluginView(p);
        WhisperPluginRow(p, 0);
    }

    /// <summary>Repaint the 20-slot XUi window at the current view offset and
    /// publish the position cvars (`plugin_pos`, `plugin_total`) used by the
    /// "X/Y" badge. Visible cursor `styx.pe.sel` = absSel - viewOffset and is
    /// always 0..MaxPluginRows-1.</summary>
    private void RefreshPluginView(EntityPlayer p)
    {
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;
        int total  = plugins.Count;
        int absSel = _pluginAbsSel.TryGetValue(p.entityId, out var s) ? s : 0;
        int viewOffset = _pluginViewOffset.TryGetValue(p.entityId, out var v) ? v : 0;

        if (total <= 0)
        {
            Styx.Ui.SetVar(p, "styx.pe.plugin_count", 0);
            Styx.Ui.SetVar(p, "styx.pe.plugin_total", 0);
            Styx.Ui.SetVar(p, "styx.pe.plugin_pos",   0);
            Styx.Ui.SetVar(p, "styx.pe.sel",          0);
            return;
        }

        // Constrain viewOffset so absSel is on screen.
        if (total <= MaxPluginRows)
        {
            viewOffset = 0;
        }
        else
        {
            if (absSel < viewOffset) viewOffset = absSel;
            else if (absSel >= viewOffset + MaxPluginRows) viewOffset = absSel - MaxPluginRows + 1;
            if (viewOffset < 0) viewOffset = 0;
            if (viewOffset > total - MaxPluginRows) viewOffset = total - MaxPluginRows;
        }
        _pluginViewOffset[p.entityId] = viewOffset;

        int visibleCount = Math.Min(MaxPluginRows, total - viewOffset);
        Styx.Ui.SetVar(p, "styx.pe.plugin_count", visibleCount);
        Styx.Ui.SetVar(p, "styx.pe.plugin_total", total);
        Styx.Ui.SetVar(p, "styx.pe.plugin_pos",   absSel + 1);
        Styx.Ui.SetVar(p, "styx.pe.sel",          absSel - viewOffset);

        for (int i = 0; i < MaxPluginRows; i++)
        {
            int labelIdx = i < visibleCount
                ? IndexOfPluginInRegistry(plugins[viewOffset + i])
                : 0;
            Styx.Ui.SetVar(p, "styx.pe.plugin" + i + "_id", labelIdx);
        }
    }

    private void HandleStagePlugins(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;
        int count = plugins.Count;
        if (count == 0) { CloseFor(p); return; }

        int absSel = _pluginAbsSel.TryGetValue(p.entityId, out var s) ? s : 0;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                absSel = (absSel + 1) % count;
                // Wrap to top: snap window back to row 0 so the user lands at
                // the start of the list, not partway down.
                if (absSel == 0) _pluginViewOffset[p.entityId] = 0;
                _pluginAbsSel[p.entityId] = absSel;
                RefreshPluginView(p);
                WhisperPluginRow(p, absSel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                absSel = (absSel - 1 + count) % count;
                // Wrap to bottom: snap window to the tail so the last row is in view.
                if (absSel == count - 1)
                    _pluginViewOffset[p.entityId] = Math.Max(0, count - MaxPluginRows);
                _pluginAbsSel[p.entityId] = absSel;
                RefreshPluginView(p);
                WhisperPluginRow(p, absSel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                string picked = plugins[absSel];
                // Row 0 is the "(All plugins)" sentinel — store null to mean unfiltered.
                _selectedPlugin[p.entityId] =
                    string.Equals(picked, AllPluginsLabel, StringComparison.OrdinalIgnoreCase) ? null : picked;
                EnterStagePerms(p);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to group picker
                _selectedGroup.Remove(p.entityId);
                Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_GROUPS);
                Styx.Ui.SetVar(p, "styx.pe.sel", 0f);
                WhisperGroupRow(p, 0);
                break;
        }
    }

    // ================================================================== stage 2 — perms

    /// <summary>Build the FULL filtered perm list for the current selection.
    /// Null filter = all; otherwise case-insensitive owner match. Stage 2
    /// renders this through a sliding window so the list is no longer
    /// truncated to MaxPermRows.</summary>
    private List<PermissionManager.KnownPermission> FilteredPerms(int entityId)
    {
        if (!_permSnapshot.TryGetValue(entityId, out var all)) return new List<PermissionManager.KnownPermission>();
        _selectedPlugin.TryGetValue(entityId, out var filter);
        IEnumerable<PermissionManager.KnownPermission> q = all;
        if (!string.IsNullOrEmpty(filter))
            q = q.Where(k => string.Equals(k.Owner, filter, StringComparison.OrdinalIgnoreCase));
        return q.ToList();
    }

    private void EnterStagePerms(EntityPlayer p)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PERMS);
        // Tell XUi which plugin row to highlight in the "EDITING ... / plugin" subtitle.
        _selectedPlugin.TryGetValue(p.entityId, out var filter);
        Styx.Ui.SetVar(p, "styx.pe.selected_plugin_id",
            IndexOfPluginInRegistry(string.IsNullOrEmpty(filter) ? AllPluginsLabel : filter));

        // Populate the 3 leading config rows (priority, tag, color).
        Styx.Ui.SetVar(p, "styx.pe.cfg_priority", group.Priority);
        Styx.Ui.SetVar(p, "styx.pe.cfg_tag_id",   IndexOfTagPreset(group.ChatTag));
        Styx.Ui.SetVar(p, "styx.pe.cfg_color_id", IndexOfColorPreset(group.ChatTagColor));

        _permAbsSel[p.entityId]     = 0;
        _permViewOffset[p.entityId] = 0;
        RefreshPermView(p, group);
        WhisperRow(p, 0);
    }

    // Row layout on stage 2:
    //   absSel 0 → Priority      (LMB cycles +10, wrap at PriorityMax)
    //   absSel 1 → Chat Tag      (LMB cycles to next preset)
    //   absSel 2 → Chat Color    (LMB cycles to next preset)
    //   absSel 3..3+N-1 → Perms  (LMB toggles grant; N = full filtered count)
    //
    // The visible cursor cvar `styx.pe.sel` matches absSel for config rows,
    // and is 3..3+visibleCount-1 for perm rows (with sliding window over the
    // full perm list). All edits respect the auth guard (CanActorEditGroup).

    /// <summary>Repaint stage 2 perm rows at the current view offset and
    /// publish position cvars (`perm_pos`, `perm_total`) used by the badge.
    /// Visible cursor `styx.pe.sel` is recomputed from absSel + viewOffset.</summary>
    private void RefreshPermView(EntityPlayer p, GroupData group)
    {
        var perms = FilteredPerms(p.entityId);
        int totalPerms = perms.Count;
        int absSel     = _permAbsSel.TryGetValue(p.entityId, out var s) ? s : 0;
        int viewOffset = _permViewOffset.TryGetValue(p.entityId, out var v) ? v : 0;

        // Constrain viewOffset only when the cursor is on a perm row; for
        // config rows we leave the perm window where it was so the user sees
        // a stable view as they navigate the boundary.
        if (totalPerms <= MaxPermRows)
        {
            viewOffset = 0;
        }
        else if (absSel >= ConfigRowCount)
        {
            int absPermIdx = absSel - ConfigRowCount;
            if (absPermIdx < viewOffset) viewOffset = absPermIdx;
            else if (absPermIdx >= viewOffset + MaxPermRows) viewOffset = absPermIdx - MaxPermRows + 1;
            if (viewOffset < 0) viewOffset = 0;
            if (viewOffset > totalPerms - MaxPermRows) viewOffset = totalPerms - MaxPermRows;
        }
        else
        {
            // Cursor is on a config row — clamp viewOffset just in case the
            // perm list shrunk (e.g. a perm was revoked + re-snapshotted).
            if (viewOffset < 0) viewOffset = 0;
            if (totalPerms > MaxPermRows && viewOffset > totalPerms - MaxPermRows)
                viewOffset = totalPerms - MaxPermRows;
        }
        _permViewOffset[p.entityId] = viewOffset;

        int visibleCount = Math.Min(MaxPermRows, Math.Max(0, totalPerms - viewOffset));
        Styx.Ui.SetVar(p, "styx.pe.perm_count", visibleCount);
        Styx.Ui.SetVar(p, "styx.pe.perm_total", totalPerms);
        Styx.Ui.SetVar(p, "styx.pe.perm_pos",
            absSel >= ConfigRowCount ? (absSel - ConfigRowCount + 1) : 0);

        // Visible cursor: config rows = absSel; perm rows = ConfigRowCount + (absPermIdx - viewOffset)
        int visSel = absSel < ConfigRowCount
            ? absSel
            : ConfigRowCount + (absSel - ConfigRowCount - viewOffset);
        Styx.Ui.SetVar(p, "styx.pe.sel", visSel);

        for (int i = 0; i < MaxPermRows; i++)
        {
            int labelIdx = i < visibleCount
                ? IndexOfPermInRegistry(perms[viewOffset + i].Name)
                : 0;
            int status = i < visibleCount && group.Perms.Contains(perms[viewOffset + i].Name)
                ? 1 : 0;
            Styx.Ui.SetVar(p, "styx.pe.perm" + i + "_id", labelIdx);
            Styx.Ui.SetVar(p, "styx.pe.perm" + i + "_status", status);
        }
    }

    private void HandleStagePerms(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        var perms = FilteredPerms(p.entityId);
        int totalPerms = perms.Count;
        int totalRows  = ConfigRowCount + totalPerms;
        if (totalRows == 0) { CloseFor(p); return; }

        int absSel = _permAbsSel.TryGetValue(p.entityId, out var s) ? s : 0;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                absSel = (absSel + 1) % totalRows;
                // Wrap to top: snap perm window to row 0 so when the user
                // navigates back into perms they start at the top.
                if (absSel == 0) _permViewOffset[p.entityId] = 0;
                _permAbsSel[p.entityId] = absSel;
                RefreshPermView(p, group);
                WhisperRow(p, absSel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                absSel = (absSel - 1 + totalRows) % totalRows;
                // Wrap to last perm — make sure tail is in view.
                if (absSel == totalRows - 1 && totalPerms > MaxPermRows)
                    _permViewOffset[p.entityId] = totalPerms - MaxPermRows;
                _permAbsSel[p.entityId] = absSel;
                RefreshPermView(p, group);
                WhisperRow(p, absSel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                if (absSel == 0)      CyclePriority(p);
                else if (absSel == 1) CycleTag(p);
                else if (absSel == 2) CycleColor(p);
                else                  TogglePerm(p, perms[absSel - ConfigRowCount].Name);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to plugin picker — reset the sliding window to the top.
                Styx.Ui.SetVar(p, "styx.pe.stage", STAGE_PLUGINS);
                _pluginAbsSel[p.entityId]     = 0;
                _pluginViewOffset[p.entityId] = 0;
                RefreshPluginView(p);
                WhisperPluginRow(p, 0);
                break;
        }
    }

    // ================================================================== group config cycle handlers

    /// <summary>Auth + selected-group lookup. Returns (groupName, group) on
    /// success or (null, null) and whispers an error on failure.</summary>
    private (string name, GroupData data) ResolveEditableGroup(EntityPlayer p)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return (null, null);
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return (null, null);

        var actorPid = StyxCore.Player.PlatformIdOf(p);
        if (!StyxCore.Perms.CanActorEditGroup(actorPid, groupName))
        {
            int actorAuth = StyxCore.Perms.GetAuthLevel(actorPid);
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][PermEditor] Refused — group '{0}' contains a member with more authority than you (your auth {1}).[-]",
                groupName, actorAuth));
            return (null, null);
        }
        return (groupName, group);
    }

    private void CyclePriority(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int newPriority = group.Priority + PriorityStep;
        if (newPriority > PriorityMax) newPriority = 0;
        StyxCore.Perms.SetGroupPriority(name, newPriority);
        Styx.Ui.SetVar(p, "styx.pe.cfg_priority", newPriority);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' priority -> {1}[-]", name, newPriority));
    }

    private void CycleTag(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int currentIdx = IndexOfTagPreset(group.ChatTag);
        int nextIdx = (currentIdx + 1) % TagPresets.Length;
        // Index 0 = "[None]" sentinel → store null so GetGroupTag treats it as "no tag"
        string newTag = nextIdx == 0 ? null : TagPresets[nextIdx];
        StyxCore.Perms.SetGroupTag(name, newTag, group.ChatTagColor);
        Styx.Ui.SetVar(p, "styx.pe.cfg_tag_id", nextIdx);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' tag -> {1}[-]", name, TagPresets[nextIdx]));
    }

    private void CycleColor(EntityPlayer p)
    {
        var (name, group) = ResolveEditableGroup(p);
        if (group == null) return;

        int currentIdx = IndexOfColorPreset(group.ChatTagColor);
        int nextIdx = (currentIdx + 1) % ColorPresets.Length;
        string newColor = ColorPresets[nextIdx];
        StyxCore.Perms.SetGroupTag(name, group.ChatTag, newColor);
        Styx.Ui.SetVar(p, "styx.pe.cfg_color_id", nextIdx);
        Styx.Server.Whisper(p, string.Format(
            "[00ff66][PermEditor] '{0}' color -> [{1}]{2}[-][-]",
            name, newColor, ColorNames[nextIdx]));
    }

    /// <summary>Dispatch whisper based on row type — config rows show the
    /// current value, perm rows show grant status + description.</summary>
    private void WhisperRow(EntityPlayer p, int row)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        if (row == 0)
        {
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' priority:[-] [ffffdd]{1}[-] (LMB cycles +{2}, wraps at {3})",
                groupName, group.Priority, PriorityStep, PriorityMax));
        }
        else if (row == 1)
        {
            int idx = IndexOfTagPreset(group.ChatTag);
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' tag:[-] [ffffdd]{1}[-] (LMB cycles to next preset)",
                groupName, TagPresets[idx]));
        }
        else if (row == 2)
        {
            int idx = IndexOfColorPreset(group.ChatTagColor);
            Styx.Server.Whisper(p, string.Format(
                "[ccddff][PermEditor] '{0}' color:[-] [{1}]{2}[-] (LMB cycles to next preset)",
                groupName, ColorPresets[idx], ColorNames[idx]));
        }
        else
        {
            WhisperPermRow(p, row - ConfigRowCount);
        }
    }

    private void TogglePerm(EntityPlayer p, string permName)
    {
        if (!_selectedGroup.TryGetValue(p.entityId, out var groupName)) return;
        var group = StyxCore.Perms.GetGroup(groupName);
        if (group == null) return;

        // Privilege-escalation guard: can't edit a group that contains a
        // higher-authority member than yourself. Owner bypasses this.
        var actorPid = StyxCore.Player.PlatformIdOf(p);
        if (!StyxCore.Perms.CanActorEditGroup(actorPid, groupName))
        {
            int actorAuth = StyxCore.Perms.GetAuthLevel(actorPid);
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][PermEditor] Refused — group '{0}' contains a member with more authority than you (your auth {1}).[-]",
                groupName, actorAuth));
            return;
        }

        bool currentlyGranted = group.Perms.Contains(permName);
        if (currentlyGranted)
        {
            StyxCore.Perms.RevokeFromGroup(groupName, permName);
            Styx.Server.Whisper(p, string.Format(
                "[ffaa00][PermEditor] Revoked [-][ffffdd]{0}[-] from group [ffffdd]{1}[-]",
                permName, groupName));
        }
        else
        {
            StyxCore.Perms.GrantToGroup(groupName, permName);
            Styx.Server.Whisper(p, string.Format(
                "[00ff66][PermEditor] Granted [-][ffffdd]{0}[-] to group [ffffdd]{1}[-]",
                permName, groupName));
        }

        // Refresh badges (the group reference may have stale Perms set since
        // GroupData is by-value here; re-fetch and re-paint the filtered
        // slice through the sliding-window painter).
        var refreshed = StyxCore.Perms.GetGroup(groupName);
        if (refreshed != null)
            RefreshPermView(p, refreshed);
    }

    // ================================================================== whisper helpers

    private void WhisperGroupRow(EntityPlayer p, int row)
    {
        if (!_groupSnapshot.TryGetValue(p.entityId, out var groups)) return;
        if (row < 0 || row >= groups.Count) return;
        var g = groups[row];
        string tagInfo = string.IsNullOrEmpty(g.ChatTag)
            ? "[888888]no tag[-]"
            : "[" + (string.IsNullOrEmpty(g.ChatTagColor) ? "ffffff" : g.ChatTagColor) + "]" + g.ChatTag + "[-]";
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] (priority {3}, {4}, {5} perm(s))",
            row + 1, groups.Count, g.Name, g.Priority, tagInfo, g.Perms.Count));
    }

    private void WhisperPluginRow(EntityPlayer p, int row)
    {
        if (!_pluginSnapshot.TryGetValue(p.entityId, out var plugins)) return;
        if (row < 0 || row >= plugins.Count) return;
        string name = plugins[row];
        // Count perms owned by this plugin for a helpful hint.
        int permCount;
        if (string.Equals(name, AllPluginsLabel, StringComparison.OrdinalIgnoreCase))
            permCount = _permSnapshot.TryGetValue(p.entityId, out var all) ? all.Count : 0;
        else
            permCount = _permSnapshot.TryGetValue(p.entityId, out var all2)
                ? all2.Count(k => string.Equals(k.Owner, name, StringComparison.OrdinalIgnoreCase))
                : 0;
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] ({3} perm(s))",
            row + 1, plugins.Count, name, permCount));
    }

    private void WhisperPermRow(EntityPlayer p, int row)
    {
        var perms = FilteredPerms(p.entityId);
        if (row < 0 || row >= perms.Count) return;
        if (!_selectedGroup.TryGetValue(p.entityId, out var gName)) return;
        var group = StyxCore.Perms.GetGroup(gName);
        var perm = perms[row];
        bool granted = group?.Perms.Contains(perm.Name) == true;
        string status = granted ? "[00ff66]GRANTED[-]" : "[888888]not granted[-]";
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][PermEditor] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  ({4}) [{5}]",
            row + 1, perms.Count, perm.Name, status,
            string.IsNullOrEmpty(perm.Description) ? "(no description)" : perm.Description,
            perm.Owner));
    }

    // ================================================================== index lookups

    private int IndexOfGroupInRegistry(string name)
    {
        var all = StyxCore.Perms.GetAllGroups();
        for (int i = 0; i < all.Count && i < 32; i++)
            if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private int IndexOfPermInRegistry(string permName)
    {
        var all = StyxCore.Perms.AllKnown;
        for (int i = 0; i < all.Count && i < 128; i++)
            if (string.Equals(all[i].Name, permName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Lookup index into the perm_plugin_N label set. BuildLabels
    /// registers these in the same order DistinctOwners produces (sentinel
    /// "(All plugins)" at 0, then alpha-sorted owners).</summary>
    private int IndexOfPluginInRegistry(string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return 0;
        var owners = DistinctOwners(StyxCore.Perms.AllKnown);
        for (int i = 0; i < owners.Count && i < 32; i++)
            if (string.Equals(owners[i], pluginName, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
