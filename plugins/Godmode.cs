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

// Godmode — Styx-internal protected-entity registry.
// See STYX_V26_ENGINE_SURFACE.md §1.7. The framework ships 5 Harmony patches
// that consult StyxCore.IsGodmode(entityId). Doesn't use EntityPlayer.IsGodMode
// (that gets reset by NetPackageEntityAliveFlags sync from clients).

using System.Collections.Generic;
using Styx;
using Styx.Plugins;

[Info("Godmode", "Doowkcol", "0.6.0")]
public class Godmode : StyxPlugin
{
    public class Config
    {
        public HashSet<string> Players = new HashSet<string>();
    }

    private Config _cfg;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        ReapplyAll();

        StyxCore.Commands.Register("god", "Toggle godmode — /god [on|off]", (ctx, args) =>
        {
            var id = ctx.Client?.PlatformId?.CombinedString;
            if (string.IsNullOrEmpty(id)) { ctx.Reply("No player context."); return; }

            bool wantOn;
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "on": case "true": case "1":  wantOn = true; break;
                    case "off": case "false": case "0": wantOn = false; break;
                    default: ctx.Reply("Usage: /god [on|off]"); return;
                }
            }
            else wantOn = !_cfg.Players.Contains(id);

            if (wantOn) _cfg.Players.Add(id); else _cfg.Players.Remove(id);
            StyxCore.Configs.Save(this, _cfg);

            StyxCore.SetGodmode(ctx.Client.entityId, wantOn);

            ctx.Reply(wantOn ? "[00ff66]Godmode: ON[-]" : "[ff6666]Godmode: OFF[-]");
            Log.Out("[Godmode] {0} (entity {1}) -> {2}", ctx.SenderName, ctx.Client.entityId, wantOn);
        });

        Log.Out("[Godmode] Loaded v0.6.0 (Styx-internal flag). Protected SteamIDs: {0}", _cfg.Players.Count);
    }

    public override void OnUnload()
    {
        // Clear all Styx godmode flags owned by this plugin.
        var cm = ConnectionManager.Instance;
        if (cm?.Clients?.List != null)
            foreach (var ci in cm.Clients.List)
                if (ci != null) StyxCore.SetGodmode(ci.entityId, false);
    }

    // Re-arm protection on spawn / respawn — entityId may be new.
    void OnPlayerJoined(ClientInfo client) => Rearm(client);
    void OnPlayerSpawned(ClientInfo client, RespawnType _reason, Vector3i _pos) => Rearm(client);

    private void ReapplyAll()
    {
        var cm = ConnectionManager.Instance;
        if (cm?.Clients?.List == null) return;
        foreach (var ci in cm.Clients.List) Rearm(ci);
    }

    private void Rearm(ClientInfo client)
    {
        if (client == null) return;
        var id = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(id)) return;
        bool on = _cfg.Players.Contains(id);
        StyxCore.SetGodmode(client.entityId, on);
        if (on) Log.Out("[Godmode] Rearmed for {0} (entity {1})", client.playerName, client.entityId);
    }
}
