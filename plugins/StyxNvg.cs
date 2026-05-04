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

// StyxNvg -- toggleable "personal night vision" via a buff that pins a
// real Unity light source to the player's first-person camera. Same
// underlying mechanism the vanilla Mining Helmet Light mod uses, just
// driven by buff lifecycle instead of armour-equip events. Server-side
// only, EAC-compatible, no client install.
//
// Not a literal "force daylight" effect (the engine doesn't expose
// per-player sky overrides to mods on V2.6) -- but functionally
// equivalent: the player can see in the dark without burning through
// torches or holding a flashlight.
//
// Toggle state is persisted per platform-id, so dying / relogging /
// respawning preserves the player's preference.
//
// Commands:
//   /nvg                  -- toggle on/off
//   /nvg on  / /nvg off   -- explicit
//   /nvg show <player>    -- admin: read another player's state
//
// Perms:
//   styx.nvg.use          -- run /nvg (default group gets it)
//   styx.nvg.admin        -- run /nvg show <player>

using System;
using System.Collections.Generic;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxNvg", "Doowkcol", "0.1.0")]
public class StyxNvg : StyxPlugin
{
    public override string Description => "Toggleable personal night vision via a camera-pinned light source";

    private const string PermUse   = "styx.nvg.use";
    private const string PermAdmin = "styx.nvg.admin";
    private const string BuffName  = "buffStyxNvg";

    public class Config
    {
        // Reapply tick. Death and some teleport events clear the buff;
        // a periodic refresh ensures the persistent toggle stays effective
        // without us hooking every possible buff-clearing edge case.
        public int ReapplyIntervalSeconds = 60;

        // Buff duration override. Long; the reapply tick refreshes it.
        public int BuffDurationSeconds = 99999;
    }

    /// <summary>
    /// Per-player toggle state. Set membership = "NVG is ON".
    /// Persisted so dying / relogging keeps the preference.
    /// </summary>
    public class State
    {
        public HashSet<string> Enabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _reapplyTick;

    public override void OnLoad()
    {
        _cfg   = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("state");

        StyxCore.Perms.RegisterKnown(PermUse,
            "Toggle personal night vision (/nvg)", Name);
        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Inspect another player's NVG state (/nvg show)", Name);

        StyxCore.Commands.Register("nvg",
            "Toggle personal night vision -- /nvg, /nvg on, /nvg off, /nvg show <player>",
            HandleCommand);

        // Periodic refresh -- death, spectator transitions and a few mod
        // interactions can drop a buff the plugin still considers active.
        // Cheap re-apply each minute closes the gap.
        int tick = Math.Max(15, _cfg.ReapplyIntervalSeconds);
        _reapplyTick = Scheduler.Every(tick, ReapplyAll, name: Name + ".reapply");

        // First sweep covers hot-reload (players already in-world get their
        // buff back without waiting for the first tick).
        Scheduler.Once(2.0, ReapplyAll, name: Name + ".initial");

        Log.Out("[StyxNvg] Loaded v0.1.0 -- {0} player(s) currently have NVG enabled",
            _state.Value.Enabled.Count);
    }

    public override void OnUnload()
    {
        // Drop the buff from everyone who has it on so we don't leave
        // "ghost" lights pinned to player cameras after unload. State on
        // disk is unaffected -- next load restores their preference.
        foreach (var p in StyxCore.Player.All())
            StyxCore.Player.RemoveBuff(p, BuffName);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    void OnPlayerSpawned(ClientInfo ci, RespawnType reason, Vector3i pos)
    {
        if (ci == null) return;
        var p = StyxCore.Player.FindByEntityId(ci.entityId);
        if (p == null) return;
        // Small delay so the player entity has settled before we layer
        // a buff on -- same idiom StyxBuffs and StyxPockets use.
        Scheduler.Once(1.0,
            () => ApplyForPlayer(p),
            name: Name + ".spawn." + ci.entityId);
    }

    // ============================================================ apply

    private void ReapplyAll()
    {
        foreach (var p in StyxCore.Player.All())
            ApplyForPlayer(p);
    }

    private void ApplyForPlayer(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        bool wantOn = _state.Value.Enabled.Contains(pid)
                   && StyxCore.Perms.HasPermission(pid, PermUse);

        if (wantOn)
        {
            // Refresh duration each pass; vanilla's onSelfBuffStart trigger
            // only fires on transition off->on, so re-applying with the same
            // buff name doesn't double the light source -- the buff system
            // recognises the existing instance and just refreshes timing.
            StyxCore.Player.ApplyBuff(p, BuffName, _cfg.BuffDurationSeconds);
        }
        else
        {
            // Either toggled off, or perm was revoked -- clean up.
            if (StyxCore.Player.HasBuff(p, BuffName))
                StyxCore.Player.RemoveBuff(p, BuffName);
        }
    }

    // ============================================================ commands

    private void HandleCommand(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null && (args == null || args.Length == 0 || args[0] != "show"))
        {
            ctx.Reply("Run /nvg from in-game, or use /nvg show <player> from console.");
            return;
        }

        // /nvg show <player> -- admin only
        if (args != null && args.Length >= 1 &&
            string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            if (!RequireAdmin(ctx)) return;
            if (args.Length < 2) { ctx.Reply("[ffaa00]Usage: /nvg show <player>[-]"); return; }
            ShowOther(ctx, args[1]);
            return;
        }

        // Bare /nvg / /nvg on / /nvg off (player must be in-game)
        var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("[ff6666]No player context.[-]"); return; }
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        if (!StyxCore.Perms.HasPermission(pid, PermUse))
        {
            ctx.Reply("[ff6666]You don't have the '" + PermUse + "' perm.[-]");
            return;
        }

        bool currentlyOn = _state.Value.Enabled.Contains(pid);
        bool turnOn;

        if (args == null || args.Length == 0)
            turnOn = !currentlyOn;
        else if (string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase))
            turnOn = true;
        else if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
            turnOn = false;
        else
        {
            ctx.Reply("[ffaa00]Usage: /nvg, /nvg on, /nvg off, /nvg show <player>[-]");
            return;
        }

        if (turnOn)
        {
            _state.Value.Enabled.Add(pid);
            _state.Save();
            StyxCore.Player.ApplyBuff(p, BuffName, _cfg.BuffDurationSeconds);
            ctx.Reply("[00ff66][NVG] ON -- night vision active.[-]");
        }
        else
        {
            _state.Value.Enabled.Remove(pid);
            _state.Save();
            StyxCore.Player.RemoveBuff(p, BuffName);
            ctx.Reply("[ffaa00][NVG] OFF.[-]");
        }
    }

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        if (ctx.Client == null) return true;  // console
        var pid = ctx.Client.PlatformId?.CombinedString;
        if (!string.IsNullOrEmpty(pid) && StyxCore.Perms.HasPermission(pid, PermAdmin)) return true;
        ctx.Reply("[ff6666]You don't have permission.[-]");
        return false;
    }

    private void ShowOther(Styx.Commands.CommandContext ctx, string ident)
    {
        var p = StyxCore.Player.Find(ident);
        if (p == null) { ctx.Reply("[ff6666]No matching player.[-]"); return; }
        var pid = StyxCore.Player.PlatformIdOf(p);
        var name = StyxCore.Player.DisplayName(p);
        bool stateOn = !string.IsNullOrEmpty(pid) && _state.Value.Enabled.Contains(pid);
        bool buffOn  = StyxCore.Player.HasBuff(p, BuffName);
        ctx.Reply(string.Format("[ccddff][NVG] {0} -- preference: {1}, buff active: {2}[-]",
            name,
            stateOn ? "[00ff66]ON[-]" : "[888888]OFF[-]",
            buffOn  ? "[00ff66]yes[-]" : "[888888]no[-]"));
    }
}
