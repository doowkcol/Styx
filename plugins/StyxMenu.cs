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

[Info("StyxMenu", "Doowkcol", "0.3.0")]
public class StyxMenu : StyxPlugin
{
    public override string Description => "Interactive server-only menu (framework Ui.Input subsystem)";

    private const string CvOpen   = "styx.menu.open";
    private const string CvSel    = "styx.menu.sel";
    private const int OptionCount = 5;

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
            label: "Action menu (heal / water / tp / info)",
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
                    bool ok = StyxCore.Player.GiveBackpack(p, new[] { ("drinkJarBoiledWater", 3, 1) });
                    Styx.Server.Whisper(p, ok
                        ? "[00ff66][Menu] 3 waters dropped at your feet. Look down.[-]"
                        : "[ff6666][Menu] GiveBackpack failed — check server log.[-]");
                    break;
                case 2:
                    var pos = StyxCore.Player.PositionOf(p);
                    var dest = new UnityEngine.Vector3(pos.x + 20f, pos.y, pos.z);
                    bool teleOk = StyxCore.Player.Teleport(p, dest);
                    Styx.Server.Whisper(p, teleOk
                        ? string.Format("[00ff66][Menu] Teleported 20m east.[-]")
                        : "[ff6666][Menu] Teleport failed — check server log.[-]");
                    break;
                case 3:
                    Styx.Server.Whisper(p, string.Format(
                        "[88ddff][Menu] day {0}, {1} online, blood moon: {2}[-]",
                        StyxCore.World.CurrentDay, Styx.Server.PlayerCount, StyxCore.World.IsBloodMoon));
                    break;
                case 4:
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
