// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// Demonstrates every Styx capability:
//   - Config: configs/HelloSource.json, live-reloadable
//   - Commands: /src
//   - Hook methods: OnPlayer*, OnServerInitialized, OnChatMessage (auto-wired by name)
//   - Custom hook firing: demonstrated in Kit.cs (OnKitRedeemed)
//   - Harmony patch: non-invasive probe on GameManager.ItemDropServer
//
// Save this file → Roslyn recompiles → old patches come off → new patches go on.

using HarmonyLib;
using Styx;
using Styx.Plugins;

[Info("HelloSource", "Doowkcol", "0.4.0")]
public class HelloSource : StyxPlugin
{
    public class Config
    {
        public string Greeting = "Greetings from a source plugin";
        public string Signoff = "Edit my .cs file (or configs/HelloSource.json) and save.";
        public bool Shout = false;
        public bool AnnounceJoins = true;
        public bool LogChatToConsole = false;
        public bool LogItemDrops = false;   // toggles the Harmony patch's output
        public bool LogDamage = false;      // log every OnEntityDamage
        public bool LogBlockDestroy = false; // log every OnBlockDestroyed
    }

    private Config _cfg;

    // Static for Harmony patch access; refreshed on every OnLoad.
    internal static Config Current { get; private set; }

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        Current = _cfg;

        StyxCore.Commands.Register("src", "Source-plugin demo", (ctx, args) =>
        {
            string who = args.Length > 0 ? string.Join(" ", args) : (ctx.SenderName ?? "friend");
            string msg = _cfg.Greeting + ", " + who + "! " + _cfg.Signoff;
            if (_cfg.Shout) msg = msg.ToUpperInvariant();
            ctx.Reply(msg);
        });

        Log.Out("[HelloSource] Loaded v0.4.0");
    }

    public override void OnUnload()
    {
        Log.Out("[HelloSource] Unloaded");
        Current = null;
    }

    // === Hook methods — auto-bound by name ===

    void OnServerInitialized() => Log.Out("[HelloSource] Server init hook fired.");

    void OnPlayerJoined(ClientInfo client)
    {
        if (_cfg != null && _cfg.AnnounceJoins)
            Log.Out("[HelloSource] Welcome, {0}!", client?.playerName ?? "<unknown>");
    }

    void OnPlayerDisconnected(ClientInfo client, bool gameShuttingDown)
    {
        if (gameShuttingDown) return;
        Log.Out("[HelloSource] See you later, {0}.", client?.playerName ?? "<unknown>");
    }

    void OnChatMessage(ClientInfo client, string message)
    {
        if (_cfg != null && _cfg.LogChatToConsole)
            Log.Out("[HelloSource chat] {0}: {1}", client?.playerName ?? "<unknown>", message);
    }

    // === First-party Harmony-backed hooks ===

    void OnEntityDeath(EntityAlive victim)
    {
        Log.Out("[HelloSource] Entity died: {0} (id {1})",
            victim.EntityClass?.entityClassName ?? victim.GetType().Name, victim.entityId);
    }

    void OnEntityDamage(EntityAlive victim, DamageSource source, int strength, bool critical)
    {
        // Gated behind config to avoid log spam during combat.
        if (_cfg == null || !_cfg.LogDamage) return;
        Log.Out("[HelloSource] {0} will take {1} damage (crit={2}) from {3}",
            victim.EntityClass?.entityClassName ?? "?", strength, critical,
            source?.damageType.ToString() ?? "?");
    }

    void OnPlayerLevelUp(EntityAlive player, int newLevel, int oldLevel)
    {
        Log.Out("[HelloSource] {0} levelled up: {1} -> {2}",
            (player as EntityPlayer)?.EntityName ?? "player", oldLevel, newLevel);
    }

    void OnBloodMoonStart(int day)
    {
        Log.Out("[HelloSource] BLOOD MOON STARTING (day {0})", day);
    }

    void OnBloodMoonEnd(int day)
    {
        Log.Out("[HelloSource] Blood moon ended (day {0})", day);
    }

    void OnPlayerRespawn(EntityPlayer player, RespawnType reason)
    {
        Log.Out("[HelloSource] {0} respawned (reason: {1})", player?.EntityName ?? "?", reason);
    }

    void OnVehicleMount(Entity passenger, EntityVehicle vehicle, int seat)
    {
        Log.Out("[HelloSource] {0} mounted {1} (seat {2})",
            (passenger as EntityAlive)?.EntityName ?? "?", vehicle?.EntityClass?.entityClassName ?? "?", seat);
    }

    void OnBlockDestroyed(Vector3i pos, BlockValue block, int entityId, bool useHarvestTool)
    {
        if (_cfg == null || !_cfg.LogBlockDestroy) return;
        Log.Out("[HelloSource] Block destroyed at {0} by entity {1}", pos, entityId);
    }

    // === Harmony patch (proof-of-life for the patch manager) ===
    // Passive probe: logs every item the server drops into the world, *if* the config toggle
    // is on. Demonstrates the patch lifecycle: apply on load, remove on unload.

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ItemDropServer),
        new[] { typeof(ItemStack), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3),
                typeof(int), typeof(float), typeof(bool) })]
    static class ItemDropServerProbe
    {
        static void Prefix(ItemStack _itemStack, int _entityId)
        {
            if (Current == null || !Current.LogItemDrops) return;
            int itemType = _itemStack?.itemValue?.type ?? -1;
            int count = _itemStack?.count ?? 0;
            Log.Out("[HelloSource] ItemDropServer: itemType={0} x{1} to entityId {2}",
                itemType, count, _entityId);
        }
    }
}
