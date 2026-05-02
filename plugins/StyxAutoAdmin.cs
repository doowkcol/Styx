// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxAutoAdmin -- adds every joining player to the configured perm
// group (defaults to "admin"). Intended for test / sandbox / demo
// servers where visitors should be able to experience the full
// framework without the operator manually granting perms each time.
//
// DEFAULT DISABLED. Flip Enabled=true in configs/StyxAutoAdmin.json
// when deploying to a test server. The "admin" group already ships
// with sensible perms via the framework's EnsureDefaultGroups bootstrap
// (styx.admin.*, styx.perm.admin, styx.donor.admin, styx.restart.admin,
// styx.radar.master, styx.kit.wheels, plus inherits vip + default).

using Styx;
using Styx.Plugins;

[Info("StyxAutoAdmin", "Doowkcol", "0.2.0")]
public class StyxAutoAdmin : StyxPlugin
{
    public override string Description => "Auto-add joining players to the admin perm group -- test/sandbox use only";

    public class Config
    {
        // Master toggle. Default OFF -- explicit opt-in required.
        public bool Enabled = false;

        // Perm group joiners are added to. Must already exist in the
        // perm system -- the plugin will not create groups, only add
        // members to existing ones.
        public string GroupName = "admin";

        // Log every add to the server log.
        public bool LogGrants = true;
    }

    private Config _cfg;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        Log.Out("[StyxAutoAdmin] Loaded v0.2.0 -- Enabled={0}, GroupName='{1}'",
            _cfg.Enabled, _cfg.GroupName);
    }

    void OnPlayerJoined(ClientInfo client)
    {
        if (!_cfg.Enabled) return;
        if (client?.PlatformId == null) return;

        string pid = client.PlatformId.CombinedString;
        string name = client.playerName ?? "?";

        try
        {
            if (!StyxCore.Perms.GroupExists(_cfg.GroupName))
            {
                Log.Warning("[StyxAutoAdmin] Group '{0}' does not exist -- skipping {1}",
                    _cfg.GroupName, name);
                return;
            }

            StyxCore.Perms.AddPlayerToGroup(pid, _cfg.GroupName);

            if (_cfg.LogGrants)
                Log.Out("[StyxAutoAdmin] Added {0} ({1}) to group '{2}'",
                    name, pid, _cfg.GroupName);
        }
        catch (System.Exception e)
        {
            Log.Warning("[StyxAutoAdmin] Add to group failed for " + name + ": " + e.Message);
        }
    }
}
