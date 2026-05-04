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

// StyxBuffs v0.3.0 — grant buffs by PERMISSION (not group), with user toggle UI.
//
// - configs/StyxBuffs.json defines a flat list of buffs, each with a Perm gate
// - A player is eligible for a buff iff they hold its Perm
// - Multiple buffs can share a perm (e.g. styx.buffs.vip gates 5 buffs at once)
// - Operator wires perm-to-group assignment in the standard perm editor
//   (/perm or /m → Perm Editor) -- this plugin no longer owns the mapping
// - On player join/spawn and every ReapplyIntervalSeconds: eligible buffs apply
// - /m → "My Buffs" opens a picker where players toggle their own buffs on/off
//   - ON state is the default; buffs reapply automatically
//   - OFF state is persisted per-player (data/StyxBuffs.sessions.json) and
//     skipped by the reapply loop so toggles stick across reconnect / server restart
//
// Perms (registered automatically; configure via the perm editor):
//   styx.buffs.use    — see the /m → My Buffs entry
//   styx.buffs.admin  — run /buffs reapply and see everyone's perks
//   styx.buffs.<...>  — any perm name an operator declares on a buff entry
//
// Migration from v0.2.x: if the loaded config has the old GroupBuffs / GroupCVars
// keys, they get one-time-converted to the new shape and the file rewritten.
// Each entry is tagged with Perm = "styx.buffs.<groupname>". After migration
// the operator must grant those perms to the matching groups in the perm editor.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;


/* @styx-xui-windows
<!--
    styxBuffs — StyxBuffs toggle UI.
    Per-row name + ON/OFF status (visibility-gated labels) + a
    description line that tracks the cursor. Same wiring as styxKits:
    row content driven by styx.buffs.rowK_id, styx.buffs.rowK_status.
-->
<window name="styxBuffs"
        anchor="CenterCenter" pos="-250,330"
        width="500" height="660"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="55">

    <rect name="wrap" pos="0,0" width="500" height="660"
          visible="{#cvar('styx.buffs.open') == 1}">

        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,215"        type="sliced" width="500" height="660" />
        <sprite depth="1" name="border" sprite="menu_empty3px" color="220,140,255,220"  type="sliced" width="500" height="660" fillcenter="false" />

        <label depth="2" name="hdr" text="MY BUFFS"
               font_size="26" justify="center" style="outline"
               color="220,140,255,255"
               pos="250,-10" width="500" height="30" pivot="top" />

        <!-- 20 rows. Each row has: cursor, icon, name, and SIX visibility-gated
             status labels (NoPerm/Off/On/Ready/Active/Cooldown), plus a
             cooldown countdown cvar (row{N}_cd) consumed by Active and
             Cooldown labels. The plugin pushes a status int per row at
             menu-open and on-click; this is a static snapshot, not live. -->
        <!-- Row 0 -->
        <label  depth="3" name="c0" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-52" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 0}" />
        <sprite depth="3" name="i0"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row0_id')))}"
                pos="46,-52" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 0}" />
        <label  depth="3" name="o0"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row0_id')))}"
                font_size="18" pos="76,-52" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 0}" />
        <label depth="3" name="snp0" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 0}" />
        <label depth="3" name="soff0" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 1}" />
        <label depth="3" name="son0" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 2}" />
        <label depth="3" name="srdy0" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 3}" />
        <label depth="3" name="sact0" text="Active {cvar(styx.buffs.row0_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 4}" />
        <label depth="3" name="scd0" text="{cvar(styx.buffs.row0_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-54" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 0  and cvar('styx.buffs.row0_status') == 5}" />
        <!-- Row 1 -->
        <label  depth="3" name="c1" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-76" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 1}" />
        <sprite depth="3" name="i1"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row1_id')))}"
                pos="46,-76" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 1}" />
        <label  depth="3" name="o1"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row1_id')))}"
                font_size="18" pos="76,-76" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 1}" />
        <label depth="3" name="snp1" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 0}" />
        <label depth="3" name="soff1" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 1}" />
        <label depth="3" name="son1" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 2}" />
        <label depth="3" name="srdy1" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 3}" />
        <label depth="3" name="sact1" text="Active {cvar(styx.buffs.row1_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 4}" />
        <label depth="3" name="scd1" text="{cvar(styx.buffs.row1_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-78" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 1  and cvar('styx.buffs.row1_status') == 5}" />
        <!-- Row 2 -->
        <label  depth="3" name="c2" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-100" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 2}" />
        <sprite depth="3" name="i2"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row2_id')))}"
                pos="46,-100" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 2}" />
        <label  depth="3" name="o2"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row2_id')))}"
                font_size="18" pos="76,-100" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 2}" />
        <label depth="3" name="snp2" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 0}" />
        <label depth="3" name="soff2" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 1}" />
        <label depth="3" name="son2" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 2}" />
        <label depth="3" name="srdy2" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 3}" />
        <label depth="3" name="sact2" text="Active {cvar(styx.buffs.row2_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 4}" />
        <label depth="3" name="scd2" text="{cvar(styx.buffs.row2_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-102" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 2  and cvar('styx.buffs.row2_status') == 5}" />
        <!-- Row 3 -->
        <label  depth="3" name="c3" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-124" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 3}" />
        <sprite depth="3" name="i3"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row3_id')))}"
                pos="46,-124" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 3}" />
        <label  depth="3" name="o3"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row3_id')))}"
                font_size="18" pos="76,-124" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 3}" />
        <label depth="3" name="snp3" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 0}" />
        <label depth="3" name="soff3" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 1}" />
        <label depth="3" name="son3" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 2}" />
        <label depth="3" name="srdy3" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 3}" />
        <label depth="3" name="sact3" text="Active {cvar(styx.buffs.row3_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 4}" />
        <label depth="3" name="scd3" text="{cvar(styx.buffs.row3_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-126" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 3  and cvar('styx.buffs.row3_status') == 5}" />
        <!-- Row 4 -->
        <label  depth="3" name="c4" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-148" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 4}" />
        <sprite depth="3" name="i4"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row4_id')))}"
                pos="46,-148" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 4}" />
        <label  depth="3" name="o4"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row4_id')))}"
                font_size="18" pos="76,-148" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 4}" />
        <label depth="3" name="snp4" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 0}" />
        <label depth="3" name="soff4" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 1}" />
        <label depth="3" name="son4" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 2}" />
        <label depth="3" name="srdy4" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 3}" />
        <label depth="3" name="sact4" text="Active {cvar(styx.buffs.row4_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 4}" />
        <label depth="3" name="scd4" text="{cvar(styx.buffs.row4_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-150" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 4  and cvar('styx.buffs.row4_status') == 5}" />
        <!-- Row 5 -->
        <label  depth="3" name="c5" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-172" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 5}" />
        <sprite depth="3" name="i5"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row5_id')))}"
                pos="46,-172" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 5}" />
        <label  depth="3" name="o5"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row5_id')))}"
                font_size="18" pos="76,-172" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 5}" />
        <label depth="3" name="snp5" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 0}" />
        <label depth="3" name="soff5" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 1}" />
        <label depth="3" name="son5" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 2}" />
        <label depth="3" name="srdy5" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 3}" />
        <label depth="3" name="sact5" text="Active {cvar(styx.buffs.row5_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 4}" />
        <label depth="3" name="scd5" text="{cvar(styx.buffs.row5_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-174" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 5  and cvar('styx.buffs.row5_status') == 5}" />
        <!-- Row 6 -->
        <label  depth="3" name="c6" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-196" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 6}" />
        <sprite depth="3" name="i6"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row6_id')))}"
                pos="46,-196" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 6}" />
        <label  depth="3" name="o6"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row6_id')))}"
                font_size="18" pos="76,-196" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 6}" />
        <label depth="3" name="snp6" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 0}" />
        <label depth="3" name="soff6" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 1}" />
        <label depth="3" name="son6" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 2}" />
        <label depth="3" name="srdy6" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 3}" />
        <label depth="3" name="sact6" text="Active {cvar(styx.buffs.row6_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 4}" />
        <label depth="3" name="scd6" text="{cvar(styx.buffs.row6_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-198" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 6  and cvar('styx.buffs.row6_status') == 5}" />
        <!-- Row 7 -->
        <label  depth="3" name="c7" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-220" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 7}" />
        <sprite depth="3" name="i7"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row7_id')))}"
                pos="46,-220" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 7}" />
        <label  depth="3" name="o7"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row7_id')))}"
                font_size="18" pos="76,-220" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 7}" />
        <label depth="3" name="snp7" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 0}" />
        <label depth="3" name="soff7" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 1}" />
        <label depth="3" name="son7" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 2}" />
        <label depth="3" name="srdy7" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 3}" />
        <label depth="3" name="sact7" text="Active {cvar(styx.buffs.row7_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 4}" />
        <label depth="3" name="scd7" text="{cvar(styx.buffs.row7_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-222" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 7  and cvar('styx.buffs.row7_status') == 5}" />
        <!-- Row 8 -->
        <label  depth="3" name="c8" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-244" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 8}" />
        <sprite depth="3" name="i8"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row8_id')))}"
                pos="46,-244" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 8}" />
        <label  depth="3" name="o8"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row8_id')))}"
                font_size="18" pos="76,-244" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 8}" />
        <label depth="3" name="snp8" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 0}" />
        <label depth="3" name="soff8" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 1}" />
        <label depth="3" name="son8" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 2}" />
        <label depth="3" name="srdy8" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 3}" />
        <label depth="3" name="sact8" text="Active {cvar(styx.buffs.row8_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 4}" />
        <label depth="3" name="scd8" text="{cvar(styx.buffs.row8_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-246" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 8  and cvar('styx.buffs.row8_status') == 5}" />
        <!-- Row 9 -->
        <label  depth="3" name="c9" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-268" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 9}" />
        <sprite depth="3" name="i9"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row9_id')))}"
                pos="46,-268" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 9}" />
        <label  depth="3" name="o9"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row9_id')))}"
                font_size="18" pos="76,-268" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 9}" />
        <label depth="3" name="snp9" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 0}" />
        <label depth="3" name="soff9" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 1}" />
        <label depth="3" name="son9" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 2}" />
        <label depth="3" name="srdy9" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 3}" />
        <label depth="3" name="sact9" text="Active {cvar(styx.buffs.row9_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 4}" />
        <label depth="3" name="scd9" text="{cvar(styx.buffs.row9_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-270" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 9  and cvar('styx.buffs.row9_status') == 5}" />
        <!-- Row 10 -->
        <label  depth="3" name="c10" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-292" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 10}" />
        <sprite depth="3" name="i10"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row10_id')))}"
                pos="46,-292" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 10}" />
        <label  depth="3" name="o10"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row10_id')))}"
                font_size="18" pos="76,-292" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 10}" />
        <label depth="3" name="snp10" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 0}" />
        <label depth="3" name="soff10" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 1}" />
        <label depth="3" name="son10" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 2}" />
        <label depth="3" name="srdy10" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 3}" />
        <label depth="3" name="sact10" text="Active {cvar(styx.buffs.row10_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 4}" />
        <label depth="3" name="scd10" text="{cvar(styx.buffs.row10_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-294" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 10  and cvar('styx.buffs.row10_status') == 5}" />
        <!-- Row 11 -->
        <label  depth="3" name="c11" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-316" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 11}" />
        <sprite depth="3" name="i11"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row11_id')))}"
                pos="46,-316" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 11}" />
        <label  depth="3" name="o11"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row11_id')))}"
                font_size="18" pos="76,-316" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 11}" />
        <label depth="3" name="snp11" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 0}" />
        <label depth="3" name="soff11" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 1}" />
        <label depth="3" name="son11" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 2}" />
        <label depth="3" name="srdy11" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 3}" />
        <label depth="3" name="sact11" text="Active {cvar(styx.buffs.row11_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 4}" />
        <label depth="3" name="scd11" text="{cvar(styx.buffs.row11_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-318" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 11  and cvar('styx.buffs.row11_status') == 5}" />
        <!-- Row 12 -->
        <label  depth="3" name="c12" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-340" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 12}" />
        <sprite depth="3" name="i12"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row12_id')))}"
                pos="46,-340" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 12}" />
        <label  depth="3" name="o12"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row12_id')))}"
                font_size="18" pos="76,-340" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 12}" />
        <label depth="3" name="snp12" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 0}" />
        <label depth="3" name="soff12" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 1}" />
        <label depth="3" name="son12" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 2}" />
        <label depth="3" name="srdy12" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 3}" />
        <label depth="3" name="sact12" text="Active {cvar(styx.buffs.row12_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 4}" />
        <label depth="3" name="scd12" text="{cvar(styx.buffs.row12_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-342" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 12  and cvar('styx.buffs.row12_status') == 5}" />
        <!-- Row 13 -->
        <label  depth="3" name="c13" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-364" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 13}" />
        <sprite depth="3" name="i13"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row13_id')))}"
                pos="46,-364" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 13}" />
        <label  depth="3" name="o13"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row13_id')))}"
                font_size="18" pos="76,-364" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 13}" />
        <label depth="3" name="snp13" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 0}" />
        <label depth="3" name="soff13" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 1}" />
        <label depth="3" name="son13" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 2}" />
        <label depth="3" name="srdy13" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 3}" />
        <label depth="3" name="sact13" text="Active {cvar(styx.buffs.row13_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 4}" />
        <label depth="3" name="scd13" text="{cvar(styx.buffs.row13_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-366" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 13  and cvar('styx.buffs.row13_status') == 5}" />
        <!-- Row 14 -->
        <label  depth="3" name="c14" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-388" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 14}" />
        <sprite depth="3" name="i14"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row14_id')))}"
                pos="46,-388" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 14}" />
        <label  depth="3" name="o14"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row14_id')))}"
                font_size="18" pos="76,-388" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 14}" />
        <label depth="3" name="snp14" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 0}" />
        <label depth="3" name="soff14" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 1}" />
        <label depth="3" name="son14" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 2}" />
        <label depth="3" name="srdy14" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 3}" />
        <label depth="3" name="sact14" text="Active {cvar(styx.buffs.row14_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 4}" />
        <label depth="3" name="scd14" text="{cvar(styx.buffs.row14_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-390" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 14  and cvar('styx.buffs.row14_status') == 5}" />
        <!-- Row 15 -->
        <label  depth="3" name="c15" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-412" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 15}" />
        <sprite depth="3" name="i15"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row15_id')))}"
                pos="46,-412" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 15}" />
        <label  depth="3" name="o15"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row15_id')))}"
                font_size="18" pos="76,-412" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 15}" />
        <label depth="3" name="snp15" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 0}" />
        <label depth="3" name="soff15" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 1}" />
        <label depth="3" name="son15" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 2}" />
        <label depth="3" name="srdy15" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 3}" />
        <label depth="3" name="sact15" text="Active {cvar(styx.buffs.row15_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 4}" />
        <label depth="3" name="scd15" text="{cvar(styx.buffs.row15_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-414" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 15  and cvar('styx.buffs.row15_status') == 5}" />
        <!-- Row 16 -->
        <label  depth="3" name="c16" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-436" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 16}" />
        <sprite depth="3" name="i16"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row16_id')))}"
                pos="46,-436" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 16}" />
        <label  depth="3" name="o16"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row16_id')))}"
                font_size="18" pos="76,-436" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 16}" />
        <label depth="3" name="snp16" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 0}" />
        <label depth="3" name="soff16" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 1}" />
        <label depth="3" name="son16" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 2}" />
        <label depth="3" name="srdy16" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 3}" />
        <label depth="3" name="sact16" text="Active {cvar(styx.buffs.row16_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 4}" />
        <label depth="3" name="scd16" text="{cvar(styx.buffs.row16_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-438" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 16  and cvar('styx.buffs.row16_status') == 5}" />
        <!-- Row 17 -->
        <label  depth="3" name="c17" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-460" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 17}" />
        <sprite depth="3" name="i17"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row17_id')))}"
                pos="46,-460" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 17}" />
        <label  depth="3" name="o17"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row17_id')))}"
                font_size="18" pos="76,-460" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 17}" />
        <label depth="3" name="snp17" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 0}" />
        <label depth="3" name="soff17" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 1}" />
        <label depth="3" name="son17" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 2}" />
        <label depth="3" name="srdy17" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 3}" />
        <label depth="3" name="sact17" text="Active {cvar(styx.buffs.row17_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 4}" />
        <label depth="3" name="scd17" text="{cvar(styx.buffs.row17_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-462" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 17  and cvar('styx.buffs.row17_status') == 5}" />
        <!-- Row 18 -->
        <label  depth="3" name="c18" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-484" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 18}" />
        <sprite depth="3" name="i18"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row18_id')))}"
                pos="46,-484" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 18}" />
        <label  depth="3" name="o18"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row18_id')))}"
                font_size="18" pos="76,-484" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 18}" />
        <label depth="3" name="snp18" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 0}" />
        <label depth="3" name="soff18" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 1}" />
        <label depth="3" name="son18" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 2}" />
        <label depth="3" name="srdy18" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 3}" />
        <label depth="3" name="sact18" text="Active {cvar(styx.buffs.row18_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 4}" />
        <label depth="3" name="scd18" text="{cvar(styx.buffs.row18_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-486" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 18  and cvar('styx.buffs.row18_status') == 5}" />
        <!-- Row 19 -->
        <label  depth="3" name="c19" text="&gt;" font_size="22" color="220,140,255,255"
                pos="22,-508" width="20" height="22" visible="{#cvar('styx.buffs.sel') == 19}" />
        <sprite depth="3" name="i19"
                sprite="{#localization('buffs_icon_' + int(cvar('styx.buffs.row19_id')))}"
                pos="46,-508" width="22" height="22"
                visible="{#cvar('styx.buffs.count') &gt; 19}" />
        <label  depth="3" name="o19"
                text="{#localization('buffs_name_' + int(cvar('styx.buffs.row19_id')))}"
                font_size="18" pos="76,-508" width="300" height="22" color="240,240,240,255"
                visible="{#cvar('styx.buffs.count') &gt; 19}" />
        <label depth="3" name="snp19" text="(No Perm)" font_size="14" color="130,130,130,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 0}" />
        <label depth="3" name="soff19" text="OFF" font_size="14" color="220,120,100,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 1}" />
        <label depth="3" name="son19" text="ON" font_size="14" color="100,220,120,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 2}" />
        <label depth="3" name="srdy19" text="Ready" font_size="14" color="130,200,255,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 3}" />
        <label depth="3" name="sact19" text="Active {cvar(styx.buffs.row19_cd:0)}s" font_size="14" color="100,220,120,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 4}" />
        <label depth="3" name="scd19" text="{cvar(styx.buffs.row19_cd:0)}s cd" font_size="14" color="240,180,100,255"
               pos="384,-510" width="110" height="20"
               visible="{#cvar('styx.buffs.count') &gt; 19  and cvar('styx.buffs.row19_status') == 5}" />
        <!-- Description follows the cursor -->
        <label depth="3" name="descsep" text="—" font_size="16" justify="center"
               color="220,140,255,180" pos="250,-540" width="500" height="18" pivot="top" />
        <label depth="3" name="desc"
               text="{#localization('buffs_desc_' + int(cvar('styx.buffs.desc_id')))}"
               font_size="14" justify="center"
               pos="250,-560" width="480" height="32" pivot="top"
               color="220,220,200,255" />

        <label depth="3" name="hint"
               text="(No Perm) = need a perm. ON/OFF = toggle. Ready/Active/Cooldown = on-demand buffs."
               font_size="12" justify="center"
               pos="250,-600" width="500" height="16" pivot="top"
               color="200,200,160,255" />
        <label depth="3" name="legend"
               text="[SCROLL] navigate   [LMB] toggle / activate   [RMB] back"
               font_size="13" justify="center"
               pos="250,-622" width="500" height="18" pivot="top"
               color="180,180,180,255" />
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxBuffs" />
*/

/* @styx-buffs
<!--
    Three reference VIP / donor buffs shipped with StyxBuffs. They're
    referenced by the default StyxBuffs config under the styx.buffs.vip
    perm; operators can rebrand or replace them in their own buffs config.
    Fixed-duration variant: no CVar-driven timer, no display_value_format="time".
    Server applies via Player.ApplyBuff(name, duration); client ticks it
    down with the standard buff machinery. Simpler and crash-free.
-->

<!-- +100% damage vs zombies -->
<buff name="buffStyxVipUndead"
      name_key="buffStyxVipUndeadName"
      description_key="buffStyxVipUndeadDesc"
      icon="ui_game_symbol_zombie">
    <stack_type value="replace"/>
    <duration value="3600"/>

    <effect_group>
        <passive_effect name="EntityDamage" operation="perc_add" value="1.0"/>
        <requirement name="EntityTagCompare" target="other" tags="zombie"/>
    </effect_group>
</buff>

<!-- +100% harvest yield + 50% block damage -->
<buff name="buffStyxVipHarvest"
      name_key="buffStyxVipHarvestName"
      description_key="buffStyxVipHarvestDesc"
      icon="ui_game_symbol_tool">
    <stack_type value="replace"/>
    <duration value="3600"/>

    <effect_group>
        <passive_effect name="HarvestCount" operation="perc_add" value="1"/>
        <passive_effect name="BlockDamage" operation="perc_add" value=".5"/>
    </effect_group>
</buff>

<!-- Big visible toughness package -->
<buff name="buffStyxVipToughness"
      name_key="buffStyxVipToughnessName"
      description_key="buffStyxVipToughnessDesc"
      icon="ui_game_symbol_armor_iron">
    <stack_type value="replace"/>
    <duration value="3600"/>

    <effect_group>
        <passive_effect name="PhysicalDamageResist" operation="base_add" value="60"/>
        <passive_effect name="ElementalDamageResist" operation="base_add" value="60"/>
        <passive_effect name="RunSpeed" operation="perc_add" value=".3"/>
        <passive_effect name="CarryCapacity" operation="base_add" value="40"/>
        <passive_effect name="HealthMax" operation="base_add" value="50"/>
    </effect_group>
</buff>
*/

[Info("StyxBuffs", "Doowkcol", "0.4.0")]
public class StyxBuffs : StyxPlugin
{
    public override string Description => "Perm-gated toggle + cooldown buffs with player-facing picker UI";

    private const string PermAdmin = "styx.buffs.admin";
    private const string PermUse   = "styx.buffs.use";
    private const int MaxUiRows = 20;

    // Per-row status enum used by the XUi (gates which status label is visible
    // and what colour/text shows). Numeric so the cvar binding can compare cheaply.
    private const int StatusNoPerm   = 0;
    private const int StatusOff      = 1;
    private const int StatusOn       = 2;
    private const int StatusReady    = 3;
    private const int StatusActive   = 4;
    private const int StatusCooldown = 5;

    public class BuffEntry
    {
        public string Buff;                   // e.g. "buffDrugAtomJunkies"
        public string Perm;                   // gate -- player must hold this perm
        public int DurationSeconds = 3600;
        // 0 (default) = always-on toggle. The buff applies on join/spawn and
        // re-applies on the reapply tick; the player toggles it on/off.
        // > 0 = activate-on-click cooldown buff. Player clicks Ready -> buff
        // applies for DurationSeconds, then enters cooldown for CooldownSeconds,
        // then becomes Ready again. Cooldown buffs are NOT auto-applied.
        public int CooldownSeconds = 0;
        public string DisplayName;            // optional UI label; falls back to Buff if null/empty
        public string Description;            // optional whisper text when highlighted
        public bool UserToggleable = true;    // toggle buffs only -- false = player can't disable
        public string Icon;                   // optional UIAtlas sprite name (e.g. "ui_game_symbol_fist")
    }

    public class CVarEntry
    {
        public string Name;
        public float Value;
        public string Perm;                   // gate -- player must hold this perm
    }

    public class Config
    {
        public int ReapplyIntervalSeconds = 1800; // 30 min

        // Flat list of buffs, each gated by a perm. Multiple entries can share
        // a perm (e.g. styx.buffs.vip gates the 5 VIP buffs below). Operator
        // grants the configured perms to groups via the perm editor.
        public List<BuffEntry> Buffs = new List<BuffEntry>
        {
            new BuffEntry { Buff = "buffStyxVipUndead",    Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Undead Slayer",
                Description = "+100% damage to zombies",
                Icon = "ui_game_symbol_zombie" },
            new BuffEntry { Buff = "buffStyxVipHarvest",   Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Harvest Master",
                Description = "+100% harvest yield, +50% block damage",
                Icon = "ui_game_symbol_wrench" },
            new BuffEntry { Buff = "buffStyxVipToughness", Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Toughness",
                Description = "+60 resist, +30% run speed, +40 carry, +50 HP",
                Icon = "ui_game_symbol_armor_iron" },
            new BuffEntry { Buff = "buffDrugAtomJunkies",  Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "Atom Junkies",
                Description = "Vanilla drug -- periodic HP regen",
                Icon = "ui_game_symbol_medical" },
            new BuffEntry { Buff = "buffDrugRecog",        Perm = "styx.buffs.vip", DurationSeconds = 1800,
                DisplayName = "Recog",
                Description = "Vanilla drug -- loot quality bonus",
                Icon = "ui_game_symbol_loot_sack" },
            new BuffEntry { Buff = "buffDrugSteroids",     Perm = "styx.buffs.admin", DurationSeconds = 3600,
                DisplayName = "Steroids",
                Description = "Vanilla drug -- +50 carry, +10% run speed",
                Icon = "ui_game_symbol_steroids" },
            // ---- Cooldown example: vip-tier energy drink ----
            new BuffEntry { Buff = "buffMegaCrush", Perm = "styx.buffs.vip",
                DurationSeconds = 600, CooldownSeconds = 3600,
                DisplayName = "Mega Crush (10m)",
                Description = "Vanilla energy drink -- 10 min stamina + damage rush, 1h cooldown",
                Icon = "ui_game_symbol_pills" },

            // ============================================================
            // Level-milestone tiers. Each tier's perm corresponds to a
            // StyxLeveling milestone group (lvl25/lvl50/lvl75/lvl100).
            // Operator grants e.g. styx.buffs.lvl25 to the lvl25 group via
            // the perm editor; reaching the level auto-adds the player to
            // the group, unlocking the buffs.
            // ============================================================

            // ---- lvl25 -- early game (survival / utility) ----
            new BuffEntry { Buff = "buffDrugVitamins", Perm = "styx.buffs.lvl25", DurationSeconds = 3600,
                DisplayName = "L25: Vitamins",
                Description = "Resist infection + dysentery from food/drink",
                Icon = "ui_game_symbol_pills" },
            new BuffEntry { Buff = "buffDrugSugarButts", Perm = "styx.buffs.lvl25", DurationSeconds = 3600,
                DisplayName = "L25: Sugar Butts",
                Description = "+10% trader buy + sell prices",
                Icon = "ui_game_symbol_candy_sugar_butts" },
            new BuffEntry { Buff = "buffDrugPainkillers", Perm = "styx.buffs.lvl25",
                DurationSeconds = 30, CooldownSeconds = 600,
                DisplayName = "L25: Painkillers (heal)",
                Description = "Burst-heal 40 HP + stun resist. 10min cooldown.",
                Icon = "ui_game_symbol_pills" },

            // ---- lvl50 -- mid game (combat) ----
            new BuffEntry { Buff = "buffDrugSkullCrushers", Perm = "styx.buffs.lvl50", DurationSeconds = 3600,
                DisplayName = "L50: Skull Crushers",
                Description = "+50% melee damage with most weapon perks",
                Icon = "ui_game_symbol_candy_skull_crushers" },
            new BuffEntry { Buff = "buffDrugCovertCats", Perm = "styx.buffs.lvl50", DurationSeconds = 3600,
                DisplayName = "L50: Covert Cats",
                Description = "+50% sneak attack damage while crouched + unalerted",
                Icon = "ui_game_symbol_candy_covert_cats" },
            new BuffEntry { Buff = "buffDrugFortBites", Perm = "styx.buffs.lvl50",
                DurationSeconds = 600, CooldownSeconds = 1800,
                DisplayName = "L50: Fort Bites (resist)",
                Description = "+40% general damage resist for 10 min. 30 min cooldown.",
                Icon = "ui_game_symbol_candy_fortitude" },

            // ---- lvl75 -- late game (resource utility) ----
            new BuffEntry { Buff = "buffDrugRockBusters", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Rock Busters",
                Description = "+20% wood + ore harvest yield",
                Icon = "ui_game_symbol_candy_rock_busters" },
            new BuffEntry { Buff = "buffDrugHackers", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Hackers",
                Description = "+20% salvage harvest yield",
                Icon = "ui_game_symbol_candy_hackers" },
            new BuffEntry { Buff = "buffDrugJailBreakers", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Jail Breakers",
                Description = "Lockpicks break drastically less often",
                Icon = "ui_game_symbol_candy_jail_breakers" },

            // ---- lvl100 -- endgame (premium) ----
            new BuffEntry { Buff = "buffDrugEyeKandy", Perm = "styx.buffs.lvl100", DurationSeconds = 3600,
                DisplayName = "L100: Eye Kandy",
                Description = "+5 LootStage flat + 10% extra -- noticeably better loot rolls",
                Icon = "ui_game_symbol_candy_eye_candy" },
            new BuffEntry { Buff = "buffDrugHealthBar", Perm = "styx.buffs.lvl100", DurationSeconds = 3600,
                DisplayName = "L100: Health Bar",
                Description = "+10 max HP + 25% resist to bleed/sprain/infection trigger",
                Icon = "ui_game_symbol_candy_health_bar" },
            new BuffEntry { Buff = "buffBerserker", Perm = "styx.buffs.lvl100",
                DurationSeconds = 300, CooldownSeconds = 1200,
                DisplayName = "L100: Berserker (rage)",
                Description = "+25% damage resist for 5 min. 20 min cooldown.",
                Icon = "ui_game_symbol_berserker" },
        };

        public List<CVarEntry> CVars = new List<CVarEntry>
        {
            new CVarEntry { Name = "$treatedCommando", Value = 0.5f, Perm = "styx.buffs.vip" },
        };

        // ---- Deprecated v0.2.x fields, kept ONLY for one-time migration ----
        // After load, if these are non-null/non-empty AND the new fields are empty,
        // contents get converted (Perm = "styx.buffs.<groupname>") and the fields
        // are nulled before the config is saved back. Operators upgrading from
        // v0.2.x will see one log line and the file rewritten in the new shape.
        public Dictionary<string, List<BuffEntry>> GroupBuffs;
        public Dictionary<string, List<CVarEntry>> GroupCVars;
    }

    public class State
    {
        public HashSet<string> Toasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // playerId → set of buff names the player has toggled OFF. Persisted.
        public Dictionary<string, HashSet<string>> UserDisabled =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // playerId → buffName → unix-timestamp seconds of the last activation
        // for cooldown buffs. Persisted across reconnect / server restart so a
        // player can't dodge a cooldown by relogging.
        public Dictionary<string, Dictionary<string, long>> LastActivated =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _tick;

    // Alphabetically-sorted unique list of every buff name that appears in
    // any group. Index in this list is the GLOBAL slot id used in the
    // Labels table (buffs_name_N). Computed at OnLoad so it's stable
    // for the whole session.
    private List<string> _allBuffs = new List<string>();
    private Dictionary<string, BuffEntry> _buffMeta = new Dictionary<string, BuffEntry>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("sessions");

        // One-time migration from v0.2.x group-keyed config to v0.3.0 perm-gated.
        MigrateOldConfigIfPresent();

        // Re-apply on a timer so long buffs don't silently drop off.
        int interval = Math.Max(60, _cfg.ReapplyIntervalSeconds);
        _tick = Scheduler.Every(interval, () => ReapplyAll(), name: "StyxBuffs.reapply");

        // Build the global buff table + register labels for the UI.
        BuildBuffCatalog();

        // Register launcher entry. Gate is dynamic ("do you have any available
        // buffs?") so we register without perm and handle the empty case at open time.
        Styx.Ui.Menu.Register(this, "My Buffs  /buffs", OpenMenuFor, permission: PermUse);

        Styx.Ui.Ephemeral.Register(
            "styx.buffs.open", "styx.buffs.sel", "styx.buffs.count", "styx.buffs.desc_id");
        for (int k = 0; k < MaxUiRows; k++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.buffs.row" + k + "_id",
                "styx.buffs.row" + k + "_status",
                "styx.buffs.row" + k + "_cd");
        }

        StyxCore.Commands.Register("buffs", "Open the My Buffs panel -- /buffs [status|close|reapply]", (ctx, args) =>
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "open";
            switch (sub)
            {
                case "open":
                    if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
                    var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                    if (p == null) { ctx.Reply("Player not found."); return; }
                    var pid = ctx.Client.PlatformId?.CombinedString;
                    if (!string.IsNullOrEmpty(pid) && !StyxCore.Perms.HasPermission(pid, PermUse))
                    { ctx.Reply("[ff6666]You lack permission '" + PermUse + "'.[-]"); return; }
                    OpenMenuFor(p);
                    break;
                case "close":
                    if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
                    var pc = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                    if (pc != null) CloseFor(pc);
                    ctx.Reply("[ffaa00]My Buffs closed.[-]");
                    break;
                case "status":  ShowStatus(ctx); break;
                case "reapply":
                    if (!RequireAdmin(ctx)) return;
                    int n = ReapplyAll();
                    ctx.Reply("[00ff66]Reapplied to " + n + " player(s).[-]");
                    break;
                default:
                    ctx.Reply("Usage: /buffs [open|close|status] | /buffs reapply (admin)");
                    break;
            }
        });

        // Framework-baseline perms.
        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Run /buffs reapply and view all players' buff states", Name);
        StyxCore.Perms.RegisterKnown(PermUse,
            "See the My Buffs launcher entry (toggle your buffs on/off)", Name);

        // Register every distinct gate-perm declared on Buffs / CVars so they
        // show up in the perm editor and can be granted to groups.
        var seenPerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PermAdmin, PermUse };
        if (_cfg.Buffs != null)
        {
            foreach (var be in _cfg.Buffs)
            {
                if (be == null || string.IsNullOrEmpty(be.Perm)) continue;
                if (!seenPerms.Add(be.Perm)) continue;
                StyxCore.Perms.RegisterKnown(be.Perm,
                    "StyxBuffs gate -- grants the buff(s) tagged with this perm",
                    Name);
            }
        }
        if (_cfg.CVars != null)
        {
            foreach (var cv in _cfg.CVars)
            {
                if (cv == null || string.IsNullOrEmpty(cv.Perm)) continue;
                if (!seenPerms.Add(cv.Perm)) continue;
                StyxCore.Perms.RegisterKnown(cv.Perm,
                    "StyxBuffs cvar gate -- grants the cvar write(s) tagged with this perm",
                    Name);
            }
        }

        int buffCount = _cfg.Buffs?.Count ?? 0;
        int cvarCount = _cfg.CVars?.Count ?? 0;
        Log.Out("[StyxBuffs] Loaded v0.3.0 -- {0} buff entr(ies), {1} cvar entr(ies), {2} unique gate perm(s), reapply every {3}s",
            buffCount, cvarCount, seenPerms.Count - 2, interval);
    }

    /// <summary>
    /// Convert v0.2.x group-keyed config to v0.3.0 perm-gated config in place.
    /// Each entry's <see cref="BuffEntry.Perm"/> is filled with
    /// "styx.buffs.&lt;groupname&gt;". Old fields are nulled and the config is
    /// saved back so we never migrate twice.
    /// </summary>
    private void MigrateOldConfigIfPresent()
    {
        bool migrated = false;

        // Presence of legacy GroupBuffs in the JSON wins -- it's the operator's
        // source of truth from v0.2.x. The C# field default for Buffs is the
        // built-in sample set, which we clobber here. (A brand-new install has
        // GroupBuffs == null because no JSON file exists yet, so the default
        // Buffs survives.)
        if (_cfg.GroupBuffs != null && _cfg.GroupBuffs.Count > 0)
        {
            _cfg.Buffs = new List<BuffEntry>();
            foreach (var kv in _cfg.GroupBuffs)
            {
                string perm = "styx.buffs." + (kv.Key ?? "default").ToLowerInvariant();
                if (kv.Value == null) continue;
                foreach (var be in kv.Value)
                {
                    if (be == null) continue;
                    if (string.IsNullOrEmpty(be.Perm)) be.Perm = perm;
                    _cfg.Buffs.Add(be);
                }
            }
            Log.Out("[StyxBuffs] Migrated {0} GroupBuffs entr(ies) to perm-gated Buffs. " +
                "Grant the styx.buffs.<group> perms to the matching groups via the perm editor.",
                _cfg.GroupBuffs.Count);
            _cfg.GroupBuffs = null;
            migrated = true;
        }

        if (_cfg.GroupCVars != null && _cfg.GroupCVars.Count > 0)
        {
            _cfg.CVars = new List<CVarEntry>();
            foreach (var kv in _cfg.GroupCVars)
            {
                string perm = "styx.buffs." + (kv.Key ?? "default").ToLowerInvariant();
                if (kv.Value == null) continue;
                foreach (var cv in kv.Value)
                {
                    if (cv == null) continue;
                    if (string.IsNullOrEmpty(cv.Perm)) cv.Perm = perm;
                    _cfg.CVars.Add(cv);
                }
            }
            _cfg.GroupCVars = null;
            migrated = true;
        }

        if (migrated)
        {
            try { StyxCore.Configs.Save(this, _cfg); }
            catch (Exception e) { Log.Warning("[StyxBuffs] Migration save failed: " + e.Message); }
        }
    }

    public override void OnUnload()
    {
        _tick?.Destroy();
        _tick = null;
        _uiOpenFor.Clear();

        StyxCore.Perms.UnregisterKnownByOwner(Name);
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);
    }

    // ---- catalog / label registration ----

    private void BuildBuffCatalog()
    {
        _buffMeta.Clear();
        if (_cfg.Buffs != null)
        {
            foreach (var be in _cfg.Buffs)
            {
                if (string.IsNullOrEmpty(be?.Buff)) continue;
                // First entry wins for metadata. Later entries with the same
                // Buff but different Perm are still consulted for eligibility.
                if (!_buffMeta.ContainsKey(be.Buff)) _buffMeta[be.Buff] = be;
            }
        }

        _allBuffs = _buffMeta.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        for (int i = 0; i < _allBuffs.Count && i < MaxUiRows; i++)
        {
            var name = _allBuffs[i];
            var meta = _buffMeta[name];
            string display = !string.IsNullOrEmpty(meta.DisplayName) ? meta.DisplayName : name;
            string icon = !string.IsNullOrEmpty(meta.Icon) ? meta.Icon : "ui_game_symbol_fist";
            Styx.Ui.Labels.Register(this, "buffs_name_" + i, display);
            Styx.Ui.Labels.Register(this, "buffs_desc_" + i, meta.Description ?? "");
            Styx.Ui.Labels.Register(this, "buffs_icon_" + i, icon);
        }
        for (int i = _allBuffs.Count; i < MaxUiRows; i++)
        {
            Styx.Ui.Labels.Register(this, "buffs_name_" + i, "(no buff)");
            Styx.Ui.Labels.Register(this, "buffs_desc_" + i, "");
            Styx.Ui.Labels.Register(this, "buffs_icon_" + i, "");
        }
    }

    // ---- spawn hook ----

    public void OnPlayerSpawned(ClientInfo ci, RespawnType reason)
    {
        if (ci != null) ApplyForClient(ci, toastIfFirst: true);
    }

    // ---- core apply ----

    /// <summary>
    /// Computes the current per-buff status for a player. Returned tuple is
    /// (status, cdSeconds) where cdSeconds is meaningful only for
    /// <see cref="StatusActive"/> (seconds-of-buff-remaining) and
    /// <see cref="StatusCooldown"/> (seconds-until-ready).
    ///
    /// Perm check: if the player holds NO matching gate-perm across all
    /// config entries with this buff name, returns NoPerm. Otherwise the
    /// FIRST matching entry (in config order) drives the cooldown semantics.
    /// </summary>
    private (int status, int cdSec) ComputeStatus(string platformId, string buffName)
    {
        if (string.IsNullOrEmpty(platformId) || string.IsNullOrEmpty(buffName))
            return (StatusNoPerm, 0);
        if (_cfg.Buffs == null) return (StatusNoPerm, 0);

        BuffEntry matched = null;
        foreach (var be in _cfg.Buffs)
        {
            if (be == null || be.Buff != buffName) continue;
            if (string.IsNullOrEmpty(be.Perm)) continue;
            if (!StyxCore.Perms.HasPermission(platformId, be.Perm)) continue;
            matched = be;
            break;
        }
        if (matched == null) return (StatusNoPerm, 0);

        if (matched.CooldownSeconds <= 0)
        {
            // Toggle buff
            if (_state.Value.UserDisabled.TryGetValue(platformId, out var dis)
                && dis.Contains(buffName))
                return (StatusOff, 0);
            return (StatusOn, 0);
        }

        // Cooldown buff
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long lastAct = 0;
        if (_state.Value.LastActivated.TryGetValue(platformId, out var per)
            && per.TryGetValue(buffName, out var t))
            lastAct = t;

        if (lastAct == 0) return (StatusReady, 0);

        long elapsed = now - lastAct;
        if (elapsed < matched.DurationSeconds)
            return (StatusActive, (int)(matched.DurationSeconds - elapsed));

        long cdEnd = matched.DurationSeconds + matched.CooldownSeconds;
        if (elapsed < cdEnd)
            return (StatusCooldown, (int)(cdEnd - elapsed));

        return (StatusReady, 0);
    }

    private int ApplyForClient(ClientInfo ci, bool toastIfFirst)
    {
        if (ci == null) return 0;
        var player = StyxCore.Player.FindByEntityId(ci.entityId);
        if (player == null) return 0;

        string platformId = ci.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(platformId)) return 0;

        // Auto-apply only TOGGLE buffs that are currently On. Cooldown buffs
        // (CooldownSeconds > 0) are user-triggered, never auto-applied.
        var applied = new List<string>();
        foreach (var name in _allBuffs)
        {
            if (!_buffMeta.TryGetValue(name, out var meta)) continue;
            if (meta.CooldownSeconds > 0) continue;  // skip cooldown buffs
            var (status, _) = ComputeStatus(platformId, name);
            if (status != StatusOn) continue;        // skip NoPerm / Off
            if (StyxCore.Player.ApplyBuff(player, name, meta.DurationSeconds))
                applied.Add(name);
        }

        // CVars are perm-gated like buffs. Not user-toggleable in v0.3.0.
        if (_cfg.CVars != null)
        {
            var seenCv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in _cfg.CVars)
            {
                if (string.IsNullOrEmpty(cv?.Name)) continue;
                if (string.IsNullOrEmpty(cv.Perm)) continue;
                if (!StyxCore.Perms.HasPermission(platformId, cv.Perm)) continue;
                if (!seenCv.Add(cv.Name)) continue;   // first matching entry wins
                StyxCore.Player.SetCVar(player, cv.Name, cv.Value);
                applied.Add(cv.Name + "=" + cv.Value);
            }
        }

        if (applied.Count > 0)
        {
            Log.Out("[StyxBuffs] {0}: applied {1} ({2})",
                ci.playerName, applied.Count, string.Join(", ", applied));

            if (toastIfFirst && !_state.Value.Toasted.Contains(platformId))
            {
                _state.Value.Toasted.Add(platformId);
                _state.Save();
                Styx.Ui.Toast(player,
                    "Buffs active — /m → My Buffs to toggle",
                    Styx.Ui.Sounds.ChallengeRedeem);
            }
        }
        return applied.Count;
    }

    private int ReapplyAll()
    {
        int touched = 0;
        foreach (var ci in StyxCore.Player.AllClients())
        {
            if (ci?.entityId <= 0) continue;
            if (ApplyForClient(ci, toastIfFirst: false) > 0) touched++;
        }
        return touched;
    }

    // ---- UI ----

    private void OpenMenuFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid))
        {
            Styx.Server.Whisper(p, "[ff6666][My Buffs] Could not resolve your player id.[-]");
            return;
        }

        if (_allBuffs.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] No buffs configured on this server.[-]");
            return;
        }

        _uiOpenFor.Add(p.entityId);
        Styx.Ui.SetVar(p, "styx.buffs.open", 1f);
        Styx.Ui.SetVar(p, "styx.buffs.sel", 0f);
        int shown = Math.Min(_allBuffs.Count, MaxUiRows);
        Styx.Ui.SetVar(p, "styx.buffs.count", shown);

        // Show ALL configured buffs. Per-row status reveals access (NoPerm /
        // On / Off / Ready / Active / Cooldown) so the player sees what they
        // could have, not just what's already granted.
        for (int k = 0; k < MaxUiRows; k++)
        {
            int id = 0, status = StatusNoPerm, cd = 0;
            if (k < shown)
            {
                var buff = _allBuffs[k];
                id = k;  // _allBuffs index IS the label slot index
                var (s, c) = ComputeStatus(pid, buff);
                status = s;
                cd = c;
            }
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_id", id);
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_status", status);
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_cd", cd);
        }
        // Description tracks the currently-selected row.
        Styx.Ui.SetVar(p, "styx.buffs.desc_id", 0);

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.buffs.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_uiOpenFor.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.buffs.open") != 1) return;

        var pid = StyxCore.Player.PlatformIdOf(p);
        int count = Math.Min(_allBuffs.Count, MaxUiRows);
        if (count == 0) { CloseFor(p); return; }

        int sel = (int)p.Buffs.GetCustomVar("styx.buffs.sel");

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.buffs.sel", sel);
                Styx.Ui.SetVar(p, "styx.buffs.desc_id", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.buffs.sel", sel);
                Styx.Ui.SetVar(p, "styx.buffs.desc_id", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                ActivateOrToggle(p, pid, sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxBuffs.BackToLauncher");
                break;
        }
    }

    /// <summary>
    /// Dispatches an LMB click on a row to the right action based on the
    /// computed status (toggle vs cooldown vs no-perm vs already-active).
    /// </summary>
    private void ActivateOrToggle(EntityPlayer p, string pid, int rowSel)
    {
        if (rowSel < 0 || rowSel >= _allBuffs.Count) return;
        string buffName = _allBuffs[rowSel];
        if (!_buffMeta.TryGetValue(buffName, out var meta)) return;

        var (status, cd) = ComputeStatus(pid, buffName);

        switch (status)
        {
            case StatusNoPerm:
                Styx.Server.Whisper(p, "[888888][My Buffs] No perm for '" + DisplayOf(meta) + "'.[-]");
                return;

            case StatusActive:
                Styx.Server.Whisper(p, string.Format(
                    "[ffaa00][My Buffs] '{0}' already active -- {1}s left.[-]",
                    DisplayOf(meta), cd));
                return;

            case StatusCooldown:
                Styx.Server.Whisper(p, string.Format(
                    "[ffaa00][My Buffs] '{0}' on cooldown -- ready in {1}s.[-]",
                    DisplayOf(meta), cd));
                return;

            case StatusReady:
                ActivateCooldownBuff(p, pid, buffName, meta, rowSel);
                return;

            case StatusOn:
            case StatusOff:
                ToggleBuff(p, pid, buffName, meta, rowSel, currentlyOn: status == StatusOn);
                return;
        }
    }

    private void ActivateCooldownBuff(EntityPlayer p, string pid, string buffName, BuffEntry meta, int rowSel)
    {
        if (!meta.UserToggleable)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] '" + DisplayOf(meta) + "' is locked (not user-activatable).[-]");
            return;
        }

        // Apply the buff FIRST. If the buff name is invalid (typo, missing
        // from buffs.xml, etc.) ApplyBuff returns false; we don't want to
        // lock the cooldown for a buff that didn't actually apply.
        bool ok = StyxCore.Player.ApplyBuff(p, buffName, meta.DurationSeconds);
        if (!ok)
        {
            Log.Warning("[StyxBuffs] ApplyBuff failed for '" + buffName + "' -- check the buff name exists in buffs.xml.");
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][My Buffs] '{0}' could not be applied (server config issue -- log has details).[-]",
                DisplayOf(meta)));
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!_state.Value.LastActivated.TryGetValue(pid, out var perPlayer))
        {
            perPlayer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _state.Value.LastActivated[pid] = perPlayer;
        }
        perPlayer[buffName] = now;
        _state.Save();

        Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusActive);
        Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_cd", meta.DurationSeconds);

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][My Buffs] '{0}' activated -- {1}s active, then {2}s cooldown.[-]",
            DisplayOf(meta), meta.DurationSeconds, meta.CooldownSeconds));
    }

    private void ToggleBuff(EntityPlayer p, string pid, string buffName, BuffEntry meta, int rowSel, bool currentlyOn)
    {
        if (!meta.UserToggleable)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] '" + DisplayOf(meta) + "' is admin-locked (not user-toggleable).[-]");
            return;
        }

        var disabled = GetOrCreateDisabledSet(pid, save: true);

        if (currentlyOn)
        {
            // Toggle OFF: add to disabled set, remove the buff.
            disabled.Add(buffName);
            StyxCore.Player.RemoveBuff(p, buffName);
            Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusOff);
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] " + DisplayOf(meta) + ": OFF (won't reapply)[-]");
        }
        else
        {
            // Toggle ON: remove from disabled set, apply the buff immediately.
            disabled.Remove(buffName);
            StyxCore.Player.ApplyBuff(p, buffName, meta.DurationSeconds);
            Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusOn);
            Styx.Server.Whisper(p, "[00ff66][My Buffs] " + DisplayOf(meta) + ": ON[-]");
        }
        _state.Save();
    }

    private void WhisperRow(EntityPlayer p, int idx)
    {
        if (idx < 0 || idx >= _allBuffs.Count) return;
        var buffName = _allBuffs[idx];
        if (!_buffMeta.TryGetValue(buffName, out var meta)) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        var (status, cd) = ComputeStatus(pid, buffName);

        string statusText;
        switch (status)
        {
            case StatusNoPerm:   statusText = "[888888](No Perm)[-]"; break;
            case StatusOff:      statusText = "[ffaa00]OFF[-]"; break;
            case StatusOn:       statusText = "[00ff66]ON[-]"; break;
            case StatusReady:    statusText = "[66ddff]Ready[-]"; break;
            case StatusActive:   statusText = string.Format("[00ff66]Active ({0}s)[-]", cd); break;
            case StatusCooldown: statusText = string.Format("[ffcc66]Cooldown {0}s[-]", cd); break;
            default:             statusText = "?"; break;
        }

        int total = Math.Min(_allBuffs.Count, MaxUiRows);
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][My Buffs] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  {4}",
            idx + 1, total, DisplayOf(meta), meta.Description ?? "", statusText));
    }

    private HashSet<string> GetOrCreateDisabledSet(string pid, bool save)
    {
        if (!_state.Value.UserDisabled.TryGetValue(pid, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _state.Value.UserDisabled[pid] = set;
            if (save) _state.Save();
        }
        return set;
    }

    private static string DisplayOf(BuffEntry be)
        => !string.IsNullOrEmpty(be?.DisplayName) ? be.DisplayName : (be?.Buff ?? "?");

    // ---- commands ----

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        string id = ctx.Client?.PlatformId?.CombinedString;
        if (ctx.Client == null) return true;
        if (!string.IsNullOrEmpty(id) && StyxCore.Perms.HasPermission(id, PermAdmin)) return true;
        ctx.Reply("[ff6666]You don't have permission.[-]");
        return false;
    }

    private void ShowStatus(Styx.Commands.CommandContext ctx)
    {
        string id = ctx.Client?.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Reply("No player context."); return; }

        ctx.Reply("Buffs configured (" + _allBuffs.Count + "):");
        foreach (var name in _allBuffs)
        {
            var meta = _buffMeta.TryGetValue(name, out var m) ? m : null;
            var (status, cd) = ComputeStatus(id, name);
            string statusText;
            switch (status)
            {
                case StatusNoPerm:   statusText = "[888888](No Perm)[-]"; break;
                case StatusOff:      statusText = "[ffaa00]OFF[-]"; break;
                case StatusOn:       statusText = "[00ff66]ON[-]"; break;
                case StatusReady:    statusText = "[66ddff]Ready[-]"; break;
                case StatusActive:   statusText = string.Format("[00ff66]Active {0}s[-]", cd); break;
                case StatusCooldown: statusText = string.Format("[ffcc66]Cooldown {0}s[-]", cd); break;
                default:             statusText = "?"; break;
            }
            ctx.Reply("  " + DisplayOf(meta) + " (" + name + ") " + statusText);
        }
        ctx.Reply("[888888]Use /m → My Buffs to toggle / activate.[-]");
    }
}
