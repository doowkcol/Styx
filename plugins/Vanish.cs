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

// Vanish — admin invisibility + AI ignore via vanilla spectator flag
// (EntityPlayer.IsSpectator).
//
// What V2.6 actually gives us (verified from ConsoleCmdSpectatorMode.cs +
// EntityPlayer setter):
//   ✓ AI ignores you (zombies don't aggro)
//   ✓ Your character model hidden from other clients
//   ✗ NOT noclip (V2.6 has no per-player noclip flag)
//   ✗ NOT flight (GameStats.IsFlyingEnabled is global game-mode setting)
//
// Pair with `/fly` (separate plugin) if you want flight too. For wall-clip,
// teleport via `/m → Teleport`.
//
// Implementation: direct EntityPlayer.IsSpectator setter — the setter itself
// triggers isIgnoredByAI sync + SetVisible() to peers. The console command
// `sm` is no-arg self-only and runs from the player's own context, so it's
// not usable from server-side code anyway.
//
// Permissions:
//   styx.admin.vanish — required to use /vanish
//
// Auto-restore on disconnect: a player who vanishes then crashes/quits doesn't
// stay invisible across the session. State held in memory only (no JSON
// persist) — vanish is a transient admin tool, not a config flag.

using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("Vanish", "Doowkcol", "0.1.0")]
public class Vanish : StyxPlugin
{
    public override string Description => "Admin vanish (invisible + noclip + invincible)";

    private const string PermVanish = "styx.admin.vanish";

    // Track entity ids currently vanished. Memory-only — _vanished is the
    // source of truth; the engine's EntityPlayer.IsSpectator flag may drift
    // (the client wipes it on certain events — kill XP awards, combat
    // transitions, stat syncs) so we re-apply on a tick.
    private readonly HashSet<int> _vanished = new HashSet<int>();
    private TimerHandle _reapplyTick;

    public override void OnLoad()
    {
        StyxCore.Commands.Register("vanish",
            "Toggle admin vanish (invisible + AI ignore) — /vanish [on|off]",
            (ctx, args) => CmdVanish(ctx, args));

        StyxCore.Perms.RegisterKnown(PermVanish,
            "Toggle admin vanish (spectator mode)", Name);

        // 1Hz re-apply guard — same shape as the godmode patches' problem
        // (client wipes server flag via NetPackageEntityAliveFlags / stats
        // sync). Polling keeps the flag pinned without us needing to find
        // every packet that resets it.
        _reapplyTick = Scheduler.Every(1.0, ReapplyTick, name: "Vanish.reapply");

        Log.Out("[Vanish] Loaded v0.1.0 — perm: " + PermVanish);
    }

    public override void OnUnload()
    {
        if (_reapplyTick != null) { _reapplyTick.Destroy(); _reapplyTick = null; }

        // Belt-and-braces: unvanish everyone we've vanished so plugin reload
        // doesn't strand admins in spectator mode.
        foreach (var eid in _vanished)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null) ApplySpectator(p, false);
        }
        _vanished.Clear();
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // Hook fires when a player disconnects (graceful or crash). Clear our
    // tracking set so a future reconnect with a fresh entity id starts visible.
    void OnPlayerDisconnected(ClientInfo client, bool _shutting)
    {
        if (client == null) return;
        _vanished.Remove(client.entityId);
    }

    // Death respawn (or any non-join respawn) clears vanish — sensible UX:
    // dying drops you out of vanish, admin re-enables manually. JoinMultiplayer
    // / Teleport respawns leave _vanished alone (already would be empty for a
    // fresh join thanks to OnPlayerDisconnected; teleport shouldn't strip it).
    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        if (reason == RespawnType.Died || reason == RespawnType.NewGame)
        {
            if (_vanished.Remove(client.entityId))
                Log.Out("[Vanish] Cleared vanish for entity {0} on respawn (reason: {1})",
                    client.entityId, reason);
        }
    }

    /// <summary>
    /// Re-apply IsSpectator for every entity in our vanish set whose engine
    /// flag has drifted to false. Runs 1Hz. The drift is caused by client
    /// stat sync overwriting the server flag — same root cause we patched
    /// for godmode (see engine surface §1.7), but here we're pinning a
    /// non-damage flag so we use polling rather than packet patches.
    /// </summary>
    private void ReapplyTick()
    {
        if (_vanished.Count == 0) return;
        foreach (var eid in _vanished)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            if (!p.IsSpectator)
            {
                p.IsSpectator = true;
                Log.Out("[Vanish] Re-applied IsSpectator for entity {0} (was wiped by client sync)", eid);
            }
        }
    }

    private void CmdVanish(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }

        string pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("Could not resolve your player id."); return; }

        if (!StyxCore.Perms.HasPermission(pid, PermVanish))
        {
            ctx.Reply("[ff6666][Vanish] You lack permission '" + PermVanish + "'.[-]");
            return;
        }

        var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("Player not found in world."); return; }

        bool currentlyOn = _vanished.Contains(p.entityId);

        bool wantOn;
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "on":  case "true":  case "1": wantOn = true;  break;
                case "off": case "false": case "0": wantOn = false; break;
                default: ctx.Reply("Usage: /vanish [on|off]"); return;
            }
        }
        else wantOn = !currentlyOn;

        if (wantOn == currentlyOn)
        {
            ctx.Reply(wantOn
                ? "[888888][Vanish] Already hidden.[-]"
                : "[888888][Vanish] Already visible.[-]");
            return;
        }

        bool ok = ApplySpectator(p, wantOn);
        if (!ok)
        {
            ctx.Reply("[ff6666][Vanish] Engine refused the toggle — check log.[-]");
            return;
        }

        if (wantOn) _vanished.Add(p.entityId); else _vanished.Remove(p.entityId);

        ctx.Reply(wantOn
            ? "[00ff66][Vanish] Hidden — invisible to players, ignored by AI. (Use /fly for flight.)[-]"
            : "[ffaa00][Vanish] You are visible again.[-]");
        Log.Out("[Vanish] {0} (entity {1}) -> {2}", ctx.SenderName, p.entityId, wantOn);
    }

    /// <summary>
    /// Toggle vanilla spectator mode on the player. The setter on
    /// EntityPlayer.IsSpectator drives all the side effects we want:
    ///   - sets isIgnoredByAI = isSpectator → zombies stop targeting
    ///   - calls SetVisible() → model hidden from other clients
    ///   - flips bPlayerStatsChanged → triggers stat broadcast tick
    /// The vanilla `sm` console command is no-arg, self-only, and runs from
    /// the player's own command context — useless from server-side, so we
    /// skip it entirely and use the property setter directly.
    /// </summary>
    private bool ApplySpectator(EntityPlayer p, bool on)
    {
        if (p == null) return false;
        try { p.IsSpectator = on; return true; }
        catch (System.Exception e) { Log.Warning("[Vanish] Toggle failed: " + e.Message); return false; }
    }
}
