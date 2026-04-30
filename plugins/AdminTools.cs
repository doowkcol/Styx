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

// AdminTools — single "/m → Admin Tools" sub-menu that toggles Vanish and
// Admin Radar from the launcher (and any future admin toggles we add).
//
// Underlying tools each have their own /vanish and /aradar chat commands;
// this just gives admins a UI alternative. Toggle dispatch goes through the
// existing CommandManager so all the perm checks + whisper feedback fire
// exactly as if the player typed the command — no logic duplication.
//
// Permissions:
//   styx.admin.tools — required to open the menu (the launcher entry will
//                       still appear, but selecting it whispers a perm error)
//
// Live status: each row shows [ON]/[OFF] read from the underlying state
//   row 0 (Vanish):       p.IsSpectator
//   row 1 (Admin Radar):  the styx.aradar.visible cvar that AdminRadar drives

using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;

[Info("AdminTools", "Doowkcol", "0.1.0")]
public class AdminTools : StyxPlugin
{
    public override string Description => "Sub-menu launcher for admin toggles (vanish + radar)";

    private const string PermAdmin = "styx.admin.tools";
    private const int RowCount = 2;

    private readonly HashSet<int> _open = new HashSet<int>();

    public override void OnLoad()
    {
        // Perm-gated launcher entry — players without styx.admin.tools won't see "Admin Tools" in /m.
        Styx.Ui.Menu.Register(this, "Admin Tools", OpenFor, permission: PermAdmin);
        Styx.Ui.Ephemeral.Register(
            "styx.adt.open", "styx.adt.sel",
            "styx.adt.vanish_status", "styx.adt.radar_status");

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Open the /m → Admin Tools sub-menu", Name);

        Log.Out("[AdminTools] Loaded v0.1.0 — perm: " + PermAdmin);
    }

    public override void OnUnload()
    {
        foreach (var eid in _open)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.adt.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _open.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;

        string pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        if (!StyxCore.Perms.HasPermission(pid, PermAdmin))
        {
            Styx.Server.Whisper(p, "[ff6666][AdminTools] You lack permission '" + PermAdmin + "'.[-]");
            return;
        }

        _open.Add(p.entityId);
        Styx.Ui.SetVar(p, "styx.adt.open", 1f);
        Styx.Ui.SetVar(p, "styx.adt.sel", 0f);
        RefreshStatus(p);
        Styx.Ui.Input.Acquire(p, Name);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.adt.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _open.Remove(p.entityId);
    }

    private void RefreshStatus(EntityPlayer p)
    {
        if (p == null) return;
        // Vanish state lives on EntityPlayer.IsSpectator — read direct.
        Styx.Ui.SetVar(p, "styx.adt.vanish_status", p.IsSpectator ? 1f : 0f);
        // Radar state — query via Service Registry. Type-safe call, no
        // reflection. Returns null if AdminRadar isn't loaded; we treat that
        // as OFF.
        var radar = StyxCore.Services?.Get<IRadarStatus>();
        bool radarOn = radar?.IsEnabledFor(p.entityId) == true;
        Styx.Ui.SetVar(p, "styx.adt.radar_status", radarOn ? 1f : 0f);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_open.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.adt.open") != 1) return;

        int sel = (int)p.Buffs.GetCustomVar("styx.adt.sel");

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % RowCount;
                Styx.Ui.SetVar(p, "styx.adt.sel", sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + RowCount) % RowCount;
                Styx.Ui.SetVar(p, "styx.adt.sel", sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                ToggleRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "AdminTools.BackToLauncher");
                break;
        }
    }

    private void ToggleRow(EntityPlayer p, int row)
    {
        var ci = StyxCore.Player.ClientOf(p);
        if (ci == null) return;

        string cmd = row == 0 ? "/vanish" : "/aradar";

        // Dispatch via the framework's CommandManager — runs the same code path
        // as if the player typed the command in chat (perm check + whisper +
        // state mutation all go through the right plugin). Keeps the toggle
        // logic in exactly one place.
        var ctx = new CommandContext(ci, p.entityId, ci.playerName, cmd, EChatType.Global);
        StyxCore.Commands.TryDispatch(cmd, ctx);

        // Brief defer so underlying toggle has time to apply before we re-read
        // the status cvars. Vanish flips IsSpectator instantly; the radar tick
        // hasn't necessarily run, so we still re-read its cvar for instant
        // feedback (next radar tick will overwrite if needed).
        Styx.Scheduling.Scheduler.Once(0.1, () =>
        {
            var still = StyxCore.Player.FindByEntityId(p.entityId);
            if (still != null) RefreshStatus(still);
        }, name: "AdminTools.RefreshStatus");
    }
}
