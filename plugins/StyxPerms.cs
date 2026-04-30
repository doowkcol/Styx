// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxPerms — interactive perm-manager UI.
//
// Inspired by Steenamaroo's Oxide PermissionsManager. Uses the v0.6.3
// framework Ui.Input subsystem — plugin receives OnPlayerInput hook-bus
// events instead of polling cvars manually.
//
// Action-launcher pattern: admin navigates fixed options with jump/crouch +
// LMB, each action whispers formatted results to chat. Grant/revoke uses
// existing /perm chat commands (name resolution already wired there).
//
// Open: /perms  (requires styx.perm.admin)

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Plugins;

[Info("StyxPerms", "Doowkcol", "0.2.0")]
public class StyxPerms : StyxPlugin
{
    public override string Description => "Interactive perm-manager menu (framework Ui.Input subsystem)";

    private const string PermAdmin = "styx.perm.admin";
    private const string CvOpen    = "styx.perms.open";
    private const string CvSel     = "styx.perms.sel";
    private const int OptionCount  = 5;

    private readonly HashSet<int> _openFor = new HashSet<int>();

    public override void OnLoad()
    {
        StyxCore.Commands.Register("perms", "Open the Styx perm manager UI (admin-gated)", (ctx, args) =>
        {
            if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
            var id = ctx.Client.PlatformId?.CombinedString;
            if (string.IsNullOrEmpty(id) || !StyxCore.Perms.HasPermission(id, PermAdmin))
            { ctx.Reply("[ff6666]You need the " + PermAdmin + " permission.[-]"); return; }

            var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
            if (p == null) { ctx.Reply("Player entity not found."); return; }

            if (args.Length > 0 && args[0].ToLowerInvariant() == "close")
            { Close(p); ctx.Reply("[ffaa00]perms closed[-]"); return; }

            Open(p);
            ctx.Reply("[00ff66]perms open — JUMP/CROUCH navigate, LMB confirm, RMB close[-]");
        });

        // Show up in /m for admins only. Permission gate is evaluated when
        // the launcher opens, so non-admins won't see this entry.
        Styx.Ui.Menu.Register(this,
            label: "Perm Manager",
            onSelect: p => Open(p),
            permission: PermAdmin);

        Styx.Ui.Ephemeral.Register(CvOpen, CvSel);

        Log.Out("[StyxPerms] Loaded v0.2.0 — /m entry registered");
    }

    public override void OnUnload()
    {
        Styx.Ui.Menu.UnregisterAll(this);
        foreach (var eid in _openFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            Styx.Ui.SetVar(p, CvOpen, 0f);
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

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null) return;
        if ((int)p.Buffs.GetCustomVar(CvOpen) != 1) return;

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
                // RMB = back to /m. Close own panel, then reopen launcher.
                Close(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxPerms.BackToLauncher");
                break;
        }
    }

    private void Confirm(EntityPlayer p, int sel)
    {
        try
        {
            switch (sel)
            {
                case 0: ShowMyPerms(p); break;
                case 1: ListOnlinePlayers(p); break;
                case 2: ListAllGroups(p); break;
                case 3: ShowStats(p); break;
                case 4: Styx.Server.Whisper(p, "[ffaa00][Perms] Closed.[-]"); break;
            }
        }
        catch (Exception e) { Log.Error("[StyxPerms] Confirm(sel={0}) threw: {1}", sel, e); }
        Close(p);
    }

    // ---- Actions ----

    private void ShowMyPerms(EntityPlayer p)
    {
        var id = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(id)) { Styx.Server.Whisper(p, "[ff6666][Perms] No platform id.[-]"); return; }

        var groups = StyxCore.Perms.GetPlayerGroups(id);
        var perms  = StyxCore.Perms.GetEffectivePermissions(id);

        Styx.Server.Whisper(p, string.Format("[ccddff][Perms] You ({0}):[-]", id));
        Styx.Server.Whisper(p, string.Format("  [ffffdd] Groups ({0}):[-] {1}",
            groups.Count, groups.Count > 0 ? string.Join(", ", groups) : "(none)"));
        Styx.Server.Whisper(p, string.Format("  [ffffdd] Effective perms ({0}):[-]", perms.Count));
        foreach (var chunk in Chunk(perms, 6))
            Styx.Server.Whisper(p, "    " + string.Join(", ", chunk));
    }

    private void ListOnlinePlayers(EntityPlayer p)
    {
        var players = StyxCore.Player.AllClients();
        Styx.Server.Whisper(p, string.Format("[ccddff][Perms] Online players ({0}):[-]", players?.Count() ?? 0));
        if (players == null) return;
        foreach (var ci in players)
        {
            if (ci?.PlatformId == null) continue;
            var pid = ci.PlatformId.CombinedString;
            var groups = StyxCore.Perms.GetPlayerGroups(pid);
            var groupStr = groups.Count > 0 ? string.Join(",", groups) : "-";
            Styx.Server.Whisper(p, string.Format("  [ddffdd]{0}[-] ({1}) groups=[ffffdd]{2}[-]",
                ci.playerName ?? "?", pid, groupStr));
        }
    }

    private void ListAllGroups(EntityPlayer p)
    {
        var groups = StyxCore.Perms.GetAllGroups();
        Styx.Server.Whisper(p, string.Format("[ccddff][Perms] Groups ({0}):[-]", groups.Count));
        foreach (var g in groups.OrderBy(g => g.Name))
        {
            var members = StyxCore.Perms.GetPlayersInGroup(g.Name);
            var parent = string.IsNullOrEmpty(g.Parent) ? "" : " parent=[ffffdd]" + g.Parent + "[-]";
            Styx.Server.Whisper(p, string.Format("  [ddffdd]{0}[-] members=[ffffdd]{1}[-] perms=[ffffdd]{2}[-]{3}",
                g.Name, members.Count, g.Perms?.Count ?? 0, parent));
        }
    }

    private void ShowStats(EntityPlayer p)
    {
        var allPlayers = StyxCore.Perms.GetAllPlayers();
        var allGroups  = StyxCore.Perms.GetAllGroups();
        int totalGroupPerms  = allGroups.Sum(g => g.Perms?.Count ?? 0);
        int totalPlayerGrants = allPlayers.Sum(kv => kv.Value?.Grants?.Count ?? 0);
        Styx.Server.Whisper(p, "[ccddff][Perms] Server perm stats:[-]");
        Styx.Server.Whisper(p, string.Format("  tracked players: [ffffdd]{0}[-]", allPlayers.Count));
        Styx.Server.Whisper(p, string.Format("  groups:          [ffffdd]{0}[-]", allGroups.Count));
        Styx.Server.Whisper(p, string.Format("  group-attached perms:  [ffffdd]{0}[-]", totalGroupPerms));
        Styx.Server.Whisper(p, string.Format("  player-direct grants:  [ffffdd]{0}[-]", totalPlayerGrants));
        Styx.Server.Whisper(p, "  (use /perm grant|revoke|addto|removefrom for changes)");
    }

    private static IEnumerable<IEnumerable<T>> Chunk<T>(IEnumerable<T> src, int size)
    {
        var list = src?.ToList() ?? new List<T>();
        for (int i = 0; i < list.Count; i += size)
            yield return list.GetRange(i, Math.Min(size, list.Count - i));
    }
}
