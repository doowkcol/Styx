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

// StyxPockets -- perm-tiered passive bumps to inventory size.
//
// Backed by buffs (defined in Config/buffs.xml: buffStyxPockets6/12/24)
// that pump BagSize + CarryCapacity passive_effects. The vanilla engine
// resizes the bag UI dynamically based on BagSize -- the same mechanism
// vanilla storage-pocket armour mods use -- so this works under EAC with
// zero client install. Plugin re-applies on join/spawn and on a periodic
// tick, so the buff is effectively permanent while the perm holds.
//
// Two operator knobs:
//   1. BaselineBuff -- empty by default. Set to a buff name (e.g.
//      "buffStyxPockets6") to give EVERY player that buff regardless of
//      perms. Useful when an admin wants a flat baseline like "everyone
//      gets +6 pockets, period".
//   2. Tiers -- list of (perm, buff). Players with the perm get the buff.
//      Combined with BaselineBuff, the baseline always applies in addition
//      to whichever tier the player qualifies for.
//
// TierMode controls multi-tier behaviour:
//   "highest" (default) -- player gets ONLY the last matching tier
//                          (config order). Cleanest tier-promotion model.
//   "stack"             -- player gets EVERY matching tier, all stacked
//                          via vanilla passive_effect base_add. A player
//                          with t1+t2 perms gets +6 AND +12 = +18 total.
//
// Perms shipped:
//   styx.pockets.t1 / .t2 / .t3 -- the three default tiers
//   styx.pockets.admin           -- run /pockets reapply <player|all>
//
// Why a separate plugin from StyxBuffs:
//   Pocket buffs are passive infrastructure -- the player doesn't toggle
//   them on/off, they just have them based on their group tier. Mixing
//   them into the StyxBuffs picker UI ("My Buffs") would clutter the
//   list with non-toggleable rows. StyxPockets is silent: apply on
//   spawn, refresh on tick, never appears in the picker.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Styx;
using Styx.Plugins;

[Info("StyxPockets", "Doowkcol", "0.1.0")]
public class StyxPockets : StyxPlugin
{
    public override string Description => "Perm-tiered extra inventory slots / carry capacity (passive-effect buffs)";

    private const string PermAdmin = "styx.pockets.admin";

    public class Tier
    {
        public string Perm;
        public string Buff;
        public string DisplayName;
    }

    public class Config
    {
        // Empty = no baseline. Set to a shipped buff name (buffStyxPockets6/12/24)
        // or a custom one defined in Config/buffs.xml to apply that buff to
        // every player regardless of perms. Stacks on top of any matched
        // tier buff.
        public string BaselineBuff = "";

        // Default tier ladder. Tier order matters in "highest" mode --
        // last matching wins. Edit, add, or remove tiers freely; admins who
        // want different bag-size values should add their own buff entries
        // to Config/buffs.xml and reference them here.
        public List<Tier> Tiers = new List<Tier>
        {
            new Tier { Perm = "styx.pockets.t1", Buff = "buffStyxPockets6",  DisplayName = "+6 pockets"  },
            new Tier { Perm = "styx.pockets.t2", Buff = "buffStyxPockets12", DisplayName = "+12 pockets" },
            new Tier { Perm = "styx.pockets.t3", Buff = "buffStyxPockets24", DisplayName = "+24 pockets" },
        };

        // "highest" -- apply only the last matching tier in config order.
        // "stack"   -- apply every matching tier (vanilla passive_effect base_add stacks).
        public string TierMode = "highest";

        // Reapply tick. Defensive -- buffs persist 999999s but a periodic
        // refresh covers cases where vanilla cleared a buff (e.g. negative
        // buff wipe, debug actions, mod interactions).
        public int ReapplyIntervalSeconds = 600;

        // Apply immediately on player spawn (join, respawn, world change).
        public bool ReapplyOnSpawn = true;

        // Buff duration override on apply. The buff XML defines 999999;
        // this just refreshes the timer each apply pass.
        public int BuffDurationSeconds = 99999;
    }

    private Config _cfg;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        // Register perms so they show up in the perm editor.
        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Run /pockets reapply <player|all>", Name);
        if (_cfg.Tiers != null)
        {
            foreach (var t in _cfg.Tiers)
            {
                if (t == null || string.IsNullOrEmpty(t.Perm)) continue;
                var label = string.IsNullOrEmpty(t.DisplayName) ? t.Buff : t.DisplayName;
                StyxCore.Perms.RegisterKnown(t.Perm,
                    "Pocket buff: " + label, Name);
            }
        }

        // Periodic reapply tick.
        int tick = Math.Max(60, _cfg.ReapplyIntervalSeconds);
        Styx.Scheduling.Scheduler.Every(tick, ReapplyAll, name: Name + ".reapply");

        // Initial sweep -- catches the hot-reload case where players are
        // already in-world when the plugin loads.
        Styx.Scheduling.Scheduler.Once(2.0, ReapplyAll, name: Name + ".initial");

        StyxCore.Commands.Register("pockets",
            "Show / manage StyxPockets — /pockets, /pockets show <player>, /pockets reapply [player|all], /pockets list",
            HandleCommand);

        Log.Out("[StyxPockets] Loaded v0.1.0 — mode={0}, tiers={1}, baseline={2}",
            _cfg.TierMode,
            _cfg.Tiers?.Count ?? 0,
            string.IsNullOrEmpty(_cfg.BaselineBuff) ? "(none)" : _cfg.BaselineBuff);
    }

    public override void OnUnload()
    {
        // Best-effort cleanup so unloading the plugin doesn't leave players
        // with phantom +N slots until vanilla expires the buff.
        var allBuffs = AllManagedBuffNames();
        foreach (var p in StyxCore.Player.All())
            foreach (var b in allBuffs)
                StyxCore.Player.RemoveBuff(p, b);

        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    void OnPlayerSpawned(ClientInfo ci, RespawnType reason, Vector3i pos)
    {
        if (!_cfg.ReapplyOnSpawn) return;
        if (ci == null) return;
        var player = StyxCore.Player.FindByEntityId(ci.entityId);
        if (player == null) return;
        // Small delay so the spawn-time stat snapshot has settled before
        // we layer buffs onto it. Same idiom used by StyxBuffs.
        Styx.Scheduling.Scheduler.Once(1.0,
            () => ApplyForPlayer(player),
            name: Name + ".spawn." + ci.entityId);
    }

    // ============================================================ apply

    private void ReapplyAll()
    {
        var players = StyxCore.Player.All();
        if (players == null) return;
        foreach (var p in players) ApplyForPlayer(p);
    }

    private void ApplyForPlayer(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        var allBuffs = AllManagedBuffNames();

        // 1. Compute matching tier buffs.
        var matching = new List<Tier>();
        if (_cfg.Tiers != null)
        {
            foreach (var t in _cfg.Tiers)
            {
                if (t == null || string.IsNullOrEmpty(t.Perm) || string.IsNullOrEmpty(t.Buff)) continue;
                if (StyxCore.Perms.HasPermission(pid, t.Perm)) matching.Add(t);
            }
        }

        // 2. Decide which tier buff(s) should be active for this player.
        var wantBuffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_cfg.BaselineBuff))
            wantBuffs.Add(_cfg.BaselineBuff);

        if (string.Equals(_cfg.TierMode, "stack", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in matching) wantBuffs.Add(t.Buff);
        }
        else  // "highest" (default)
        {
            if (matching.Count > 0)
                wantBuffs.Add(matching[matching.Count - 1].Buff);
        }

        // 3. Remove any managed buff that's no longer wanted.
        foreach (var bn in allBuffs)
        {
            if (wantBuffs.Contains(bn)) continue;
            if (StyxCore.Player.HasBuff(p, bn))
                StyxCore.Player.RemoveBuff(p, bn);
        }

        // 4. Apply (or refresh) every wanted buff.
        foreach (var bn in wantBuffs)
            StyxCore.Player.ApplyBuff(p, bn, _cfg.BuffDurationSeconds);
    }

    /// <summary>
    /// Every buff name this plugin manages -- BaselineBuff plus every
    /// configured tier buff. Used for cleanup passes so we never leave a
    /// stale managed buff on a player.
    /// </summary>
    private HashSet<string> AllManagedBuffNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_cfg.BaselineBuff)) set.Add(_cfg.BaselineBuff);
        if (_cfg.Tiers != null)
            foreach (var t in _cfg.Tiers)
                if (t != null && !string.IsNullOrEmpty(t.Buff)) set.Add(t.Buff);
        return set;
    }

    // ============================================================ commands

    private void HandleCommand(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (args == null || args.Length == 0)
        {
            ShowSelf(ctx);
            return;
        }

        var sub = args[0].ToLowerInvariant();
        switch (sub)
        {
            case "show":
                if (args.Length < 2) { ctx.Reply("[ffaa00]Usage: /pockets show <player>[-]"); return; }
                ShowOther(ctx, args[1]);
                break;

            case "reapply":
                if (!RequireAdmin(ctx)) return;
                if (args.Length < 2 || string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase))
                {
                    int n = 0;
                    foreach (var p in StyxCore.Player.All()) { ApplyForPlayer(p); n++; }
                    ctx.Reply(string.Format("[00ff66]Reapplied pockets for {0} player(s).[-]", n));
                }
                else
                {
                    var target = StyxCore.Player.Find(args[1]);
                    if (target == null) { ctx.Reply("[ff6666]No matching player.[-]"); return; }
                    ApplyForPlayer(target);
                    ctx.Reply(string.Format("[00ff66]Reapplied pockets for {0}.[-]",
                        StyxCore.Player.DisplayName(target)));
                }
                break;

            case "list":
                ListTiers(ctx);
                break;

            default:
                ctx.Reply("[ffaa00]Unknown subcommand. Try: show, reapply, list[-]");
                break;
        }
    }

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        if (ctx.Client == null) return true;  // console / server-side
        var pid = ctx.Client.PlatformId?.CombinedString;
        if (!string.IsNullOrEmpty(pid) && StyxCore.Perms.HasPermission(pid, PermAdmin)) return true;
        ctx.Reply("[ff6666]You don't have permission.[-]");
        return false;
    }

    private void ShowSelf(Styx.Commands.CommandContext ctx)
    {
        var p = ctx.Client == null ? null : StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("Run from in-game to see your status, or use /pockets show <player>."); return; }
        ShowFor(ctx, p);
    }

    private void ShowOther(Styx.Commands.CommandContext ctx, string ident)
    {
        var p = StyxCore.Player.Find(ident);
        if (p == null) { ctx.Reply("[ff6666]No matching player.[-]"); return; }
        ShowFor(ctx, p);
    }

    private void ShowFor(Styx.Commands.CommandContext ctx, EntityPlayer p)
    {
        var pid = StyxCore.Player.PlatformIdOf(p);
        var name = StyxCore.Player.DisplayName(p);
        var managed = AllManagedBuffNames();
        var active = new List<string>();
        foreach (var bn in managed)
            if (StyxCore.Player.HasBuff(p, bn)) active.Add(bn);

        if (active.Count == 0)
        {
            ctx.Reply(string.Format(
                "[ccddff][Pockets] {0} has no active pocket buffs.[-]", name));
            return;
        }
        ctx.Reply(string.Format("[ccddff][Pockets] {0} -- active: {1}[-]",
            name, string.Join(", ", active)));
    }

    private void ListTiers(Styx.Commands.CommandContext ctx)
    {
        ctx.Reply(string.Format("[ccddff][Pockets] mode={0}  baseline={1}[-]",
            _cfg.TierMode,
            string.IsNullOrEmpty(_cfg.BaselineBuff) ? "(none)" : _cfg.BaselineBuff));

        if (_cfg.Tiers == null || _cfg.Tiers.Count == 0)
        {
            ctx.Reply("[ffaa00]No tiers configured.[-]");
            return;
        }
        for (int i = 0; i < _cfg.Tiers.Count; i++)
        {
            var t = _cfg.Tiers[i];
            ctx.Reply(string.Format("  {0}. {1} -> {2} ({3})",
                i + 1,
                t.Perm,
                t.Buff,
                string.IsNullOrEmpty(t.DisplayName) ? "" : t.DisplayName));
        }
    }
}
