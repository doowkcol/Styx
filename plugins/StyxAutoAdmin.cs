// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxAutoAdmin -- auto-grant a vanilla auth level to every joining
// player. Intended for test / sandbox / demo servers where visitors
// should be able to experience the full framework without the
// operator having to manually grant perms each time.
//
// DEFAULT DISABLED. Flip Enabled=true in config when deploying to a
// test server. NEVER enable this on a production server -- visitors
// at AuthLevel=0 get full vanilla admin (kick, ban, killall, give,
// teleportplayer, etc.) plus implicit styx.* perms (including
// /eco wipe and /xp wipe -- gate those separately via the
// WipeAdditionalPerm config option on StyxEconomy + StyxLeveling).
//
// Existing entries in serveradmin.xml are respected: if a user is
// already at the same level or more powerful (lower number = more
// power), the auto-grant is skipped.
//
// Implementation: plugin shells out to vanilla `admin add <pid>
// <level>` via Styx.Server.ExecConsole. The vanilla command handles
// persistence to serveradmin.xml + reloads AdminTools, so the new
// permission level takes effect immediately.

using Styx;
using Styx.Plugins;

[Info("StyxAutoAdmin", "Doowkcol", "0.1.0")]
public class StyxAutoAdmin : StyxPlugin
{
    public override string Description => "Auto-grant admin to all joining players -- test/sandbox use only";

    public class Config
    {
        // Master safety toggle. Default OFF -- explicit opt-in required.
        public bool Enabled = false;

        // Vanilla auth level to grant. 0 = full owner (everything: vanilla
        // console + implicit styx.*). 1 = "near-admin" but locked out of
        // any console command at permission_level=0 (kick, ban, give,
        // etc.) AND no implicit styx.* perms -- they'd need explicit group
        // grants for the styx commands. Use 0 for the easiest "experience
        // the whole framework" demo, 1 if you want vanilla commands locked
        // down (requires you also manually maintain a group with the
        // styx.* perms you want visitors to have).
        public int AuthLevel = 0;

        // Log every grant to the server log. Useful while tuning, set
        // false to quiet the log on a busy server.
        public bool LogGrants = true;
    }

    private Config _cfg;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        Log.Out("[StyxAutoAdmin] Loaded v0.1.0 -- Enabled={0}, AuthLevel={1}",
            _cfg.Enabled, _cfg.AuthLevel);

        if (_cfg.Enabled && _cfg.AuthLevel == 0)
            Log.Warning(
                "[StyxAutoAdmin] *** AuthLevel=0 means visitors get FULL OWNER ACCESS *** -- " +
                "vanilla console (kick/ban/give/etc.) AND every styx.* perm. " +
                "Make sure WipeAdditionalPerm is set on StyxEconomy + StyxLeveling " +
                "to keep visitors from nuking all wallets / XP via /eco wipe / /xp wipe.");
    }

    /// <summary>
    /// OnPlayerJoined fires after authentication succeeds, before the
    /// player spawns into the world. We grant here so the auth level is
    /// in effect before they run their first command.
    /// </summary>
    void OnPlayerJoined(ClientInfo client)
    {
        if (!_cfg.Enabled) return;
        if (client?.PlatformId == null) return;

        string platformId = client.PlatformId.CombinedString;
        string playerName = client.playerName ?? "?";

        // Skip if the player is already at this level or more powerful
        // (lower number = more power). Avoids overwriting an operator's
        // existing auth-0 entry with auth-0 again, and prevents downgrading
        // a manually-set lower level if the operator changes AuthLevel
        // mid-deployment.
        try
        {
            int existing = StyxCore.Perms.GetAuthLevel(platformId);
            if (existing <= _cfg.AuthLevel)
            {
                if (_cfg.LogGrants)
                    Log.Out("[StyxAutoAdmin] {0} already at auth {1} -- skipping (target was {2})",
                        playerName, existing, _cfg.AuthLevel);
                return;
            }
        }
        catch { /* fall through and grant */ }

        // Vanilla `admin add` handles persistence + AdminTools reload.
        try
        {
            string cmd = string.Format("admin add {0} {1}", platformId, _cfg.AuthLevel);
            Styx.Server.ExecConsole(cmd);
            if (_cfg.LogGrants)
                Log.Out("[StyxAutoAdmin] Auto-granted auth {0} to {1} ({2})",
                    _cfg.AuthLevel, playerName, platformId);
        }
        catch (System.Exception e)
        {
            Log.Warning("[StyxAutoAdmin] Auto-grant failed for " + playerName + ": " + e.Message);
        }
    }
}
