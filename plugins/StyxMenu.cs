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

// StyxMenu — interactive server-only UI (demo).
//
// Uses the v0.6.3 framework-level Styx.Ui.Input subsystem. See
// STYX_CAPABILITIES.md §10b (UI output) + §10c (UI input).
//
// Controls (while menu open):
//   SCROLL WHEEL         navigate options (up = prev, down = next)
//   PRIMARY (LMB)        confirm — execute action for selected row
//   SECONDARY (RMB)      cancel  — close menu with no action
//
// Commands:
//   /menu          open the menu
//   /menu close    force-close

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;


/* @styx-xui-windows
<!--
    styxMenu — interactive menu demo (2026-04-20).

    Shows when cvar styx.menu.open == 1; hidden otherwise. All content
    gated by visible binding so the window itself is always mounted
    (can't Open/Close a window without a client-side controller method,
    but we can freely hide its contents).

    Row cursor ">" for row N is visible only when styx.menu.sel == N.
    Labels pull from Localization.txt keys styxMenu_0..2.

    Inputs handled by plugin StyxMenu.cs (scroll wheel via Ui.Input):
      jump (scroll down) -> sel = (sel + 1) % OptionCount
      crouch (scroll up) -> sel = (sel - 1 + OptionCount) % OptionCount
      LMB (primary)      -> confirm: execute action[sel]
      RMB (secondary)    -> back to /m launcher
-->
<window name="styxMenu"
        anchor="CenterCenter" pos="-250,125"
        width="500" height="250"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="50">

    <!-- The whole panel is gated by menu.open. One ncalc binding drives
         visibility of the wrapping rect; all children inherit. -->
    <rect name="wrap" pos="0,0" width="500" height="250"
          visible="{#cvar('styx.menu.open') == 1}">

        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,200"     type="sliced" width="500" height="250" />
        <sprite depth="1" name="border" sprite="menu_empty3px" color="100,255,180,220" type="sliced" width="500" height="250" fillcenter="false" />

        <label depth="2" name="hdr" text="STYX MENU"
               font_size="28" justify="center" style="outline"
               color="100,255,180,255"
               pos="250,-10" width="500" height="32" pivot="top" />

        <!-- Row 0 -->
        <label depth="3" name="c0" text="&gt;" font_size="22" color="100,255,180,255"
               pos="20,-50" width="20" height="24"
               visible="{#cvar('styx.menu.sel') == 0}" />
        <label depth="3" name="o0" text="{#localization('styxMenu_0')}" font_size="20"
               pos="50,-50" width="430" height="24" color="240,240,240,255" />

        <!-- Row 1 -->
        <label depth="3" name="c1" text="&gt;" font_size="22" color="100,255,180,255"
               pos="20,-76" width="20" height="24"
               visible="{#cvar('styx.menu.sel') == 1}" />
        <label depth="3" name="o1" text="{#localization('styxMenu_1')}" font_size="20"
               pos="50,-76" width="430" height="24" color="240,240,240,255" />

        <!-- Row 2 -->
        <label depth="3" name="c2" text="&gt;" font_size="22" color="100,255,180,255"
               pos="20,-102" width="20" height="24"
               visible="{#cvar('styx.menu.sel') == 2}" />
        <label depth="3" name="o2" text="{#localization('styxMenu_2')}" font_size="20"
               pos="50,-102" width="430" height="24" color="240,240,240,255" />

        <!-- Legend -->
        <label depth="3" name="legend"
               text="[SCROLL] navigate   [LMB] confirm   [RMB] back"
               font_size="14" justify="center"
               pos="250,-220" width="500" height="20" pivot="top"
               color="180,180,180,255" />
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxMenu" />
*/

/* @styx-xui-windows
<!--
    styxLauncher — top-level plugin launcher driven by the framework's
    LauncherService (see src/Styx.Core/Ui/LauncherService.cs). Opened
    via /m. Shows up to 8 numbered rows; count-driven visibility hides
    empty rows. Whispers to chat provide the actual entry labels since
    XUi can't paint dynamic strings — row shows the position, chat shows
    the full name.
-->
<window name="styxLauncher"
        anchor="CenterCenter" pos="-200,275"
        width="400" height="540"
        pivot="TopLeft"
        controller="ToolbeltWindow"
        depth="60">

    <rect name="wrap" pos="0,0" width="400" height="540"
          visible="{#cvar('styx.launcher.open') == 1}">

        <sprite depth="0" name="bg"     sprite="menu_empty"    color="0,0,0,220"       type="sliced" width="400" height="540" />
        <sprite depth="1" name="border" sprite="menu_empty3px" color="255,160,80,220"  type="sliced" width="400" height="540" fillcenter="false" />

        <label depth="2" name="hdr"
               text="{#localization('styx_launcher_header')}"
               font_size="26" justify="center" style="outline"
               color="255,160,80,255"
               pos="200,-10" width="400" height="30" pivot="top" />
        <label depth="2" name="sub"
               text="/m  —  navigate with scroll wheel, LMB select"
               font_size="12" justify="center"
               pos="200,-42" width="400" height="18" pivot="top"
               color="200,200,200,255" />

        <!-- 8 rows. Label comes from the per-row id cvar that the
             LauncherService sets per-player when /m opens:
               styx.launcher.rowK_id  → index into styxLauncherPlugin_N
             The plugin labels live in the StyxRuntime mod's
             Localization.txt, written at last shutdown from the
             plugin-profile registry.
             Rows are visibility-gated by count so empty slots hide. -->
        <label depth="3" name="c0" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-68" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 0}" />
        <label depth="3" name="o0"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row0_id')))}"
               font_size="20" pos="56,-68" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 0}" />

        <label depth="3" name="c1" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-94" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 1}" />
        <label depth="3" name="o1"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row1_id')))}"
               font_size="20" pos="56,-94" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 1}" />

        <label depth="3" name="c2" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-120" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 2}" />
        <label depth="3" name="o2"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row2_id')))}"
               font_size="20" pos="56,-120" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 2}" />

        <label depth="3" name="c3" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-146" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 3}" />
        <label depth="3" name="o3"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row3_id')))}"
               font_size="20" pos="56,-146" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 3}" />

        <label depth="3" name="c4" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-172" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 4}" />
        <label depth="3" name="o4"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row4_id')))}"
               font_size="20" pos="56,-172" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 4}" />

        <label depth="3" name="c5" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-198" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 5}" />
        <label depth="3" name="o5"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row5_id')))}"
               font_size="20" pos="56,-198" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 5}" />

        <label depth="3" name="c6" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-224" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 6}" />
        <label depth="3" name="o6"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row6_id')))}"
               font_size="20" pos="56,-224" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 6}" />

        <label depth="3" name="c7" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-250" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 7}" />
        <label depth="3" name="o7"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row7_id')))}"
               font_size="20" pos="56,-250" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 7}" />

        <label depth="3" name="c8" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-276" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 8}" />
        <label depth="3" name="o8"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row8_id')))}"
               font_size="20" pos="56,-276" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 8}" />

        <label depth="3" name="c9" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-302" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 9}" />
        <label depth="3" name="o9"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row9_id')))}"
               font_size="20" pos="56,-302" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 9}" />

        <label depth="3" name="c10" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-328" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 10}" />
        <label depth="3" name="o10"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row10_id')))}"
               font_size="20" pos="56,-328" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 10}" />

        <label depth="3" name="c11" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-354" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 11}" />
        <label depth="3" name="o11"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row11_id')))}"
               font_size="20" pos="56,-354" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 11}" />

        <label depth="3" name="c12" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-380" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 12}" />
        <label depth="3" name="o12"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row12_id')))}"
               font_size="20" pos="56,-380" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 12}" />

        <label depth="3" name="c13" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-406" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 13}" />
        <label depth="3" name="o13"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row13_id')))}"
               font_size="20" pos="56,-406" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 13}" />

        <label depth="3" name="c14" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-432" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 14}" />
        <label depth="3" name="o14"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row14_id')))}"
               font_size="20" pos="56,-432" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 14}" />

        <label depth="3" name="c15" text="&gt;" font_size="22" color="255,200,120,255"
               pos="28,-458" width="20" height="22"
               visible="{#cvar('styx.launcher.sel') == 15}" />
        <label depth="3" name="o15"
               text="{#localization('styxLauncherPlugin_' + int(cvar('styx.launcher.row15_id')))}"
               font_size="20" pos="56,-458" width="300" height="22" color="240,240,240,255"
               visible="{#cvar('styx.launcher.count') &gt; 15}" />

        <!-- Hint + legend -->
        <label depth="3" name="hint"
               text="See chat — plugin names are whispered as you navigate."
               font_size="13" justify="center"
               pos="200,-490" width="400" height="18" pivot="top"
               color="200,200,160,255" />
        <label depth="3" name="legend"
               text="[SCROLL] navigate   [LMB] select   [RMB] close"
               font_size="13" justify="center"
               pos="200,-516" width="400" height="18" pivot="top"
               color="180,180,180,255" />
    </rect>
</window>
*/

/* @styx-xui-window-group toolbelt
<window name="styxLauncher" />
*/

/* @styx-buffs
<!--
    Full restorative buff — heal HP + refill food/water/stamina + cure
    every injury and disease. Applied by StyxMenu's "Heal Full" action.

    Mechanics:
      1. RemoveAllNegativeBuffs — vanilla action that strips every
         buff marked is_negative (broken legs, sprains, bleeds,
         abrasions, stuns, infections, dysentery, food poisoning,
         hypothermia, etc.). Future-proof — also clears any modlet
         negative buffs without us having to enumerate them.
      2. RemoveBuff for the lingering TREATMENT buffs (splints,
         casts, abrasion-treated). These aren't is_negative so they
         survive step 1, but a "full heal" should remove them too.
      3. ModifyStats set 25000 — overshoots any reasonable max so the
         stats system clamps each to its own actual max (vanilla
         Health 100, Food/Water/Stamina 100, +mods). Same idiom the
         vanilla admin debug heal uses.

    All actions go through the buff system — client-authoritative
    safe (the heal can't be clobbered by the next PlayerData sync
    because the AUTHORITATIVE write path is the buff trigger, same
    as a bandage / first-aid-kit / Grandpa's Awesome Sauce).

    Applied by plugins with a 1s duration via Player.ApplyBuff;
    onSelfBuffStart fires once, all effects propagate, buff
    self-removes when duration ends.
-->
<buff name="buffStyxHealFull"
      name_key="buffStyxHealFullName"
      hidden="true">
    <stack_type value="replace"/>
    <duration value="1"/>
    <effect_group>
        <!-- Wipe all negative buffs (injuries, infections, diseases) -->
        <triggered_effect trigger="onSelfBuffStart" action="RemoveAllNegativeBuffs"/>

        <!-- Wipe lingering treatment buffs (not is_negative so survives RemoveAllNegativeBuffs) -->
        <triggered_effect trigger="onSelfBuffStart" action="RemoveBuff"
                          buff="buffInjuryAbrasionTreated,buffLegSplinted,buffLegCast,buffArmSplinted,buffArmCast"/>

        <!-- Refill core stats to max (25000 = sentinel that the stats system clamps to actual max) -->
        <triggered_effect trigger="onSelfBuffStart" action="ModifyStats" stat="Health"  operation="set" value="25000"/>
        <triggered_effect trigger="onSelfBuffStart" action="ModifyStats" stat="Food"    operation="set" value="25000"/>
        <triggered_effect trigger="onSelfBuffStart" action="ModifyStats" stat="Water"   operation="set" value="25000"/>
        <triggered_effect trigger="onSelfBuffStart" action="ModifyStats" stat="Stamina" operation="set" value="25000"/>
    </effect_group>
</buff>
*/

[Info("StyxMenu", "Doowkcol", "0.3.0")]
public class StyxMenu : StyxPlugin
{
    public override string Description => "Interactive server-only menu (framework Ui.Input subsystem)";

    private const string CvOpen   = "styx.menu.open";
    private const string CvSel    = "styx.menu.sel";
    private const int OptionCount = 3;

    // Plugins that want input should track which players they hold a claim on
    // so OnUnload can release cleanly.
    private readonly HashSet<int> _openFor = new HashSet<int>();

    public override void OnLoad()
    {
        StyxCore.Commands.Register("menu", "Open the Styx menu — /menu [close]", (ctx, args) =>
        {
            if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
            var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
            if (p == null) { ctx.Reply("Player not found."); return; }

            if (args.Length > 0 && args[0].ToLowerInvariant() == "close")
            { Close(p); ctx.Reply("[ffaa00]menu closed[-]"); return; }

            Open(p);
            ctx.Reply("[00ff66]menu open — SCROLL navigate, LMB confirm, RMB cancel[-]");
        });

        // Register with the top-level launcher (/m). Plugins that want to be
        // discoverable via /m self-register here. Auto-unregistered on unload.
        Styx.Ui.Menu.Register(this,
            label: "Action menu  /menu",
            onSelect: p => Open(p));

        // Menu open-state is UI-ephemeral — clear on each spawn so the panel
        // doesn't resurrect itself after a server restart (cvars persist in
        // the .ttp save by default).
        Styx.Ui.Ephemeral.Register(CvOpen, CvSel);

        Log.Out("[StyxMenu] Loaded v0.3.0 — framework Ui.Input subsystem + /m entry");
    }

    public override void OnUnload()
    {
        Styx.Ui.Menu.UnregisterAll(this);
        // Release every claim we hold — OnPlayerInput will stop firing for us
        // because hook-bus auto-unregisters on plugin unload.
        foreach (var eid in _openFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            StyxCore.Player.SetCVar(p, CvOpen, 0f);
            Styx.Ui.Input.Release(p, Name);
        }
        _openFor.Clear();
    }

    private void Open(EntityPlayer p)
    {
        Styx.Ui.SetVar(p, CvOpen, 1f);
        Styx.Ui.SetVar(p, CvSel, 0f);
        Styx.Ui.Input.Acquire(p, Name);
        _openFor.Add(p.entityId);
    }

    private void Close(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, CvOpen, 0f);
        Styx.Ui.Input.Release(p, Name);
        _openFor.Remove(p.entityId);
    }

    // Hook-bus auto-subscription. Fires on EVERY player whose inputs we hold
    // a claim on — we filter by CvOpen so we only handle events for players
    // who've opened this menu (vs. other plugins that may have their own).
    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null) return;
        if ((int)p.Buffs.GetCustomVar(CvOpen) != 1) return;    // menu not open for this player

        int sel = (int)p.Buffs.GetCustomVar(CvSel);

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                Styx.Ui.SetVar(p, CvSel, (sel + 1) % OptionCount);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                Styx.Ui.SetVar(p, CvSel, (sel - 1 + OptionCount) % OptionCount);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                Confirm(p, sel);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // RMB = back to /m. Close own panel, then reopen launcher
                // (deferred one tick to avoid event double-dip).
                Close(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxMenu.BackToLauncher");
                break;
            // Reload intentionally unused — only fires when a weapon needs ammo.
        }
    }

    private void Confirm(EntityPlayer p, int sel)
    {
        Log.Out("[StyxMenu] Confirm entity={0} sel={1}", p.entityId, sel);
        try
        {
            switch (sel)
            {
                case 0:
                    // Buff-driven full restore — survives client PlayerData sync.
                    // buffStyxHealFull does: HP/Food/Water/Stamina → max,
                    // RemoveAllNegativeBuffs (injuries, infections, diseases),
                    // and clears lingering splints/casts. See buffs.xml.
                    StyxCore.Player.ApplyBuff(p, "buffStyxHealFull", duration: 1);
                    Styx.Server.Whisper(p, "[00ff66][Menu] Restored — HP, food, water, stamina + cleared injuries & disease.[-]");
                    break;
                case 1:
                    Styx.Server.Whisper(p, string.Format(
                        "[88ddff][Menu] day {0}, {1} online, blood moon: {2}[-]",
                        StyxCore.World.CurrentDay, Styx.Server.PlayerCount, StyxCore.World.IsBloodMoon));
                    break;
                case 2:
                    Styx.Server.Whisper(p, "[ffaa00][Menu] Closed.[-]");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("[StyxMenu] Confirm(sel={0}) threw: {1}", sel, e);
            Styx.Server.Whisper(p, "[ff6666][Menu] Action failed — server log has details.[-]");
        }
        Close(p);
    }
}
