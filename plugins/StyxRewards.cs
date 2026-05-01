// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxRewards -- configurable "server rewards" earn engine.
//
// Consumes IEconomy (StyxEconomy must be loaded). All earn paths are
// individually toggleable + tunable + perm-gateable + multiplier-aware:
//
//   * StartingBalance      one-time grant on first spawn ever
//   * DailyLoginBonus      first spawn of each calendar day
//   * OnlineStipend        passive credits per N minutes of online time
//   * Zombie kills         flat or per-class
//   * Loot containers      flat or per-class (TileEntity class name)
//   * Block harvest        flat or per-block (block.Block.GetBlockName())
//   * Quest completion     flat or per-quest-id
//
// Multiplier perms let donor / VIP / event-active groups earn more:
//   styx.eco.x2 -> 2x, styx.eco.x3 -> 3x, etc. First-match-wins from the
//   Multipliers list in config; players without any multiplier perm earn 1x.
//
// Whisper toggles per source so operators can opt into chat noise per
// earn path -- defaults give friendly feedback for "big" events
// (login bonus, quest reward) and stay silent for grindy ones (loot,
// harvest, kills).
//
// Commands:
//   /rewards          show your current multiplier + cumulative earnings
//   /rewards stipend  admin: trigger a stipend tick now (testing)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxRewards", "Doowkcol", "0.2.0")]
public class StyxRewards : StyxPlugin
{
    public override string Description => "Configurable earn paths -- pay money + xp on kills, loot, harvest, quests, login, online time";

    // ============================================================ Config

    public class Tier
    {
        public string Perm;
        public float Multiplier;
    }

    public class Config
    {
        // Player must hold this perm to earn from any auto-source. Spending
        // and admin commands on the bank are gated separately by StyxEconomy.
        // Empty = open to all.
        public string EarnPerm = "styx.eco.earn";

        // Each earn path has TWO knobs: <X>Bounty/Reward (currency) and
        // <X>Xp (experience). Set either to 0 to disable that side. Both
        // 0 disables the path entirely. The ByClass / ByBlock / ByID
        // dictionaries override the Default for matching keys.

        // ---- One-time + periodic ----
        public long StartingBalance     = 100;
        public long StartingXp          = 0;

        public long DailyLoginBonus     = 25;
        public long DailyLoginXp        = 50;

        // Passive online stipend. Awarded every IntervalMinutes of online
        // time. Both 0 = disable.
        public long OnlineStipend       = 5;
        public long OnlineStipendXp     = 10;
        public int  OnlineStipendIntervalMinutes = 10;

        // ---- Zombie kills ----
        public long ZombieBountyDefault = 1;
        public long ZombieXpDefault     = 5;
        public Dictionary<string, long> ZombieBountyByClass =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> ZombieXpByClass =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // ---- Loot containers ----
        // Fires once per fresh container open (engine bTouched gate).
        public long LootBountyDefault   = 5;
        public long LootXpDefault       = 10;
        public Dictionary<string, long> LootBountyByClass =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> LootXpByClass =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // ---- Block harvest ----
        // Fires per BLOCK destroyed -- felling a tree pays per trunk segment.
        // HarvestRequireTool: only pay when the player used a harvest-eligible
        // tool (axe, pickaxe). Excludes hand-punching and demolition.
        public long HarvestBountyDefault = 1;
        public long HarvestXpDefault     = 1;
        public bool HarvestRequireTool   = true;
        public Dictionary<string, long> HarvestBountyByBlock =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> HarvestXpByBlock =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // ---- Quest completion ----
        public long QuestRewardDefault  = 25;
        public long QuestXpDefault      = 100;
        public Dictionary<string, long> QuestRewardByID =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> QuestXpByID =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // ---- Multiplier tiers (first-match-wins) ----
        // Players holding the perm earn the listed multiplier on every
        // earn path -- applies to BOTH money and xp. Order matters --
        // highest multiplier should be FIRST.
        public List<Tier> Multipliers = new List<Tier>
        {
            new Tier { Perm = "styx.eco.x3", Multiplier = 3.0f },
            new Tier { Perm = "styx.eco.x2", Multiplier = 2.0f },
        };

        // ---- Whisper toggles (true = whisper to player on earn) ----
        public bool WhisperOnLogin   = true;
        public bool WhisperOnQuest   = true;
        public bool WhisperOnStipend = true;
        public bool WhisperOnKill    = false;
        public bool WhisperOnLoot    = false;
        public bool WhisperOnHarvest = false;
    }

    // ============================================================ Data

    public class RewardsData
    {
        // First-spawn-ever tracker (so StartingBalance only fires once).
        public HashSet<string> SeededPlayers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // YYYY-MM-DD of the last day each player got the daily bonus.
        public Dictionary<string, string> LastBonusDate =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<RewardsData> _data;
    private TimerHandle _stipendTimer;

    // Per-player accumulated online minutes since connect. In-memory only;
    // resets on reconnect (intentional -- prevents log-off-and-back farming).
    private readonly Dictionary<int, int> _onlineMinutes = new Dictionary<int, int>();

    // ============================================================ Lifecycle

    public override void OnLoad()
    {
        _cfg  = StyxCore.Configs.Load<Config>(this);
        _data = this.Data.Store<RewardsData>("state");

        if (!string.IsNullOrEmpty(_cfg.EarnPerm))
            StyxCore.Perms.RegisterKnown(_cfg.EarnPerm, "Earn currency from server-rewards", Name);
        foreach (var t in _cfg.Multipliers)
        {
            if (t == null || string.IsNullOrEmpty(t.Perm)) continue;
            StyxCore.Perms.RegisterKnown(t.Perm,
                string.Format("Earn currency at {0}x multiplier", t.Multiplier), Name);
        }

        // 1-minute tick drives the online-stipend counter.
        _stipendTimer = Scheduler.Every(60.0, MinuteTick, name: "StyxRewards.MinuteTick");

        StyxCore.Commands.Register("rewards",
            "Show your reward multiplier -- /rewards [stipend]", CmdRewards);

        Log.Out("[StyxRewards] Loaded v0.2.0 -- money/xp earn paths active. Defaults: kill {0}/{1}, loot {2}/{3}, harvest {4}/{5}, quest {6}/{7}, login {8}/{9}, stipend {10}/{11} per {12}min",
            _cfg.ZombieBountyDefault, _cfg.ZombieXpDefault,
            _cfg.LootBountyDefault, _cfg.LootXpDefault,
            _cfg.HarvestBountyDefault, _cfg.HarvestXpDefault,
            _cfg.QuestRewardDefault, _cfg.QuestXpDefault,
            _cfg.DailyLoginBonus, _cfg.DailyLoginXp,
            _cfg.OnlineStipend, _cfg.OnlineStipendXp, _cfg.OnlineStipendIntervalMinutes);
    }

    public override void OnUnload()
    {
        _stipendTimer?.Destroy();
        _stipendTimer = null;
        _onlineMinutes.Clear();
    }

    // ============================================================ Core: AwardEarn

    /// <summary>
    /// Single chokepoint for all earn paths. Resolves the player's
    /// multiplier, computes final amounts for money and xp, calls
    /// IEconomy.Credit + ILeveling.AddXp, and optionally whispers a
    /// combined receipt. Either side can be 0 to disable. Both 0 = noop.
    /// </summary>
    private void AwardEarn(EntityPlayer player, long baseMoney, long baseXp, string source, bool whisper)
    {
        if (player == null) return;
        if (baseMoney <= 0 && baseXp <= 0) return;

        var pid = StyxCore.Player.PlatformIdOf(player);
        if (!CanEarn(pid)) return;

        float mult = ResolveMultiplier(pid);
        long money = baseMoney > 0 ? (long)Math.Floor(baseMoney * mult) : 0;
        long xp    = baseXp    > 0 ? (long)Math.Floor(baseXp    * mult) : 0;

        var eco = StyxCore.Services?.Get<IEconomy>();
        var lvl = StyxCore.Services?.Get<ILeveling>();

        if (money > 0 && eco != null) eco.Credit(player, money, source);
        if (xp    > 0 && lvl != null) lvl.AddXp (player, xp,    source);

        if (whisper)
        {
            string currency = eco?.CurrencyName ?? "Credits";
            string parts = "";
            if (money > 0) parts += string.Format("+{0} {1}", money, currency);
            if (xp    > 0) parts += (parts == "" ? "" : ", ") + string.Format("+{0} XP", xp);
            string multSuffix = mult != 1.0f ? string.Format(" x{0:0.##}", mult) : "";
            Styx.Server.Whisper(player, string.Format(
                "[88ddff][Reward] {0} ({1}{2})[-]", parts, source, multSuffix));
        }
    }

    private bool CanEarn(string platformId)
    {
        if (string.IsNullOrEmpty(_cfg.EarnPerm)) return true;
        return StyxCore.Perms.HasPermission(platformId, _cfg.EarnPerm);
    }

    private float ResolveMultiplier(string platformId)
    {
        if (string.IsNullOrEmpty(platformId) || _cfg.Multipliers == null) return 1.0f;
        foreach (var t in _cfg.Multipliers)
        {
            if (t == null || string.IsNullOrEmpty(t.Perm)) continue;
            if (StyxCore.Perms.HasPermission(platformId, t.Perm)) return t.Multiplier;
        }
        return 1.0f;
    }

    // ============================================================ Hooks

    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        // First-ever spawn: starting balance + xp seed.
        if (!_data.Value.SeededPlayers.Contains(pid))
        {
            _data.Value.SeededPlayers.Add(pid);
            _data.Save();
            AwardEarn(p, _cfg.StartingBalance, _cfg.StartingXp, "starting balance", whisper: true);
        }

        // Daily login bonus -- first spawn of each calendar day.
        if ((_cfg.DailyLoginBonus > 0 || _cfg.DailyLoginXp > 0) && CanEarn(pid))
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            _data.Value.LastBonusDate.TryGetValue(pid, out var last);
            if (last != today)
            {
                _data.Value.LastBonusDate[pid] = today;
                _data.Save();
                AwardEarn(p, _cfg.DailyLoginBonus, _cfg.DailyLoginXp, "daily login", _cfg.WhisperOnLogin);
            }
        }
    }

    void OnPlayerDisconnected(ClientInfo client, bool gameShuttingDown)
    {
        if (client == null) return;
        _onlineMinutes.Remove(client.entityId);
    }

    void OnEntityKill(EntityAlive victim, DamageResponse response)
    {
        if (!(victim is EntityZombie)) return;

        int killerId = response.Source?.getEntityId() ?? -1;
        if (killerId <= 0) return;
        var killer = StyxCore.Player.FindByEntityId(killerId);
        if (killer == null) return;

        string klass = victim.EntityClass?.entityClassName ?? "";
        long money = _cfg.ZombieBountyByClass.TryGetValue(klass, out var m) ? m : _cfg.ZombieBountyDefault;
        long xp    = _cfg.ZombieXpByClass.TryGetValue(klass, out var x)     ? x : _cfg.ZombieXpDefault;
        if (money <= 0 && xp <= 0) return;

        AwardEarn(killer, money, xp, "kill " + klass, _cfg.WhisperOnKill);
    }

    void OnLootContainerOpened(ITileEntityLootable container, int playerEntityId, FastTags<TagGroup.Global> tags)
    {
        var p = StyxCore.Player.FindByEntityId(playerEntityId);
        if (p == null) return;

        string klass = container?.GetType()?.Name ?? "";
        long money = _cfg.LootBountyByClass.TryGetValue(klass, out var m) ? m : _cfg.LootBountyDefault;
        long xp    = _cfg.LootXpByClass.TryGetValue(klass, out var x)     ? x : _cfg.LootXpDefault;
        if (money <= 0 && xp <= 0) return;

        AwardEarn(p, money, xp, "loot " + klass, _cfg.WhisperOnLoot);
    }

    // Hot path -- block destruction fires per block. Keep cheap.
    void OnBlockDestroyed(Vector3i pos, BlockValue block, int entityId, bool useHarvestTool)
    {
        if (_cfg.HarvestRequireTool && !useHarvestTool) return;
        if (entityId <= 0) return;
        var p = StyxCore.Player.FindByEntityId(entityId);
        if (p == null) return;

        string blockName = block.Block?.GetBlockName() ?? "";
        long money = _cfg.HarvestBountyByBlock.TryGetValue(blockName, out var m) ? m : _cfg.HarvestBountyDefault;
        long xp    = _cfg.HarvestXpByBlock.TryGetValue(blockName, out var x)     ? x : _cfg.HarvestXpDefault;
        if (money <= 0 && xp <= 0) return;

        AwardEarn(p, money, xp, "harvest " + blockName, _cfg.WhisperOnHarvest);
    }

    void OnQuestCompleted(EntityPlayer player, Quest quest)
    {
        if (player == null) return;

        string id = quest?.ID ?? "";
        long money = _cfg.QuestRewardByID.TryGetValue(id, out var m) ? m : _cfg.QuestRewardDefault;
        long xp    = _cfg.QuestXpByID.TryGetValue(id, out var x)     ? x : _cfg.QuestXpDefault;
        if (money <= 0 && xp <= 0) return;

        AwardEarn(player, money, xp, "quest " + id, _cfg.WhisperOnQuest);
    }

    // ============================================================ Stipend tick

    private void MinuteTick()
    {
        if (_cfg.OnlineStipendIntervalMinutes <= 0) return;
        if (_cfg.OnlineStipend <= 0 && _cfg.OnlineStipendXp <= 0) return;
        var players = StyxCore.Player?.All();
        if (players == null) return;

        foreach (var p in players)
        {
            if (p == null) continue;
            int eid = p.entityId;
            if (!_onlineMinutes.TryGetValue(eid, out int mins)) mins = 0;
            mins++;
            _onlineMinutes[eid] = mins;
            if (mins % _cfg.OnlineStipendIntervalMinutes == 0)
                AwardEarn(p, _cfg.OnlineStipend, _cfg.OnlineStipendXp, "online stipend", _cfg.WhisperOnStipend);
        }
    }

    // ============================================================ Commands

    private void CmdRewards(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }

        // /rewards stipend -- admin trigger
        if (args.Length > 0 && args[0].ToLowerInvariant() == "stipend")
        {
            var actorId = StyxCore.Player.PlatformIdOf(StyxCore.Player.FindByEntityId(ctx.Client.entityId));
            if (!StyxCore.Perms.HasPermission(actorId, "styx.eco.admin"))
            { ctx.Reply("[ff6666]No permission.[-]"); return; }
            MinuteTick();
            ctx.Reply("[00ff66]Forced a stipend tick.[-]");
            return;
        }

        var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("Player not found."); return; }
        var pid = StyxCore.Player.PlatformIdOf(p);

        float mult = ResolveMultiplier(pid);
        bool can = CanEarn(pid);
        _onlineMinutes.TryGetValue(p.entityId, out int mins);
        int next = _cfg.OnlineStipendIntervalMinutes - (mins % Math.Max(1, _cfg.OnlineStipendIntervalMinutes));

        ctx.Reply(string.Format("[ffcc00][Rewards][-] earn={0}, multiplier=x{1:0.##}",
            can ? "yes" : "no (missing " + _cfg.EarnPerm + ")", mult));
        if (_cfg.OnlineStipend > 0)
            ctx.Reply(string.Format("[ffcc00][Rewards][-] online {0}m, next stipend in {1}m (+{2})",
                mins, next, _cfg.OnlineStipend));
    }
}
