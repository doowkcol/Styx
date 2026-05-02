// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxLeveling -- per-player XP + level system (the XP bank).
//
// Pure server-side, separate from vanilla 7DTD Progression.Level which
// is tied to skill points. This is a server-meta progression system on
// top, like Rust's SkillTree.
//
// Curve: cumulative XP for level L = BaseXp * L^Exponent (default
// BaseXp=650, Exponent=2 -> level 1=650, level 50=1.6M, level 100=6.5M).
// Operator can override per-level via CustomXpTable for surgical control.
//
// Milestones: configurable list of (level, group, broadcast, whisper).
// Reaching a milestone level adds the player to that perm group
// additively -- groups stack, so reaching 100 means you keep your 25/50/
// 75 group memberships and all their perms. Server rank emerges from the
// existing chat-tag-priority system: assign each milestone group a
// distinct tag/priority and the player's chat shows their highest rank.
//
// Commands:
//   /xp                    show your XP + level + progress to next
//   /xp <player>           admin: query another player
//   /xp give <p> <amt>     admin: add XP
//   /xp take <p> <amt>     admin: subtract XP
//   /xp set  <p> <amt>     admin: set XP directly
//   /xp wipe confirm       admin: clear all XP + remove all milestone groups (auto-backup)

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Styx;
using Styx.Data;
using Styx.Plugins;

[Info("StyxLeveling", "Doowkcol", "0.1.0")]
public class StyxLeveling : StyxPlugin, ILeveling
{
    public override string Description => "Per-player XP + level system with milestone group promotions";

    // ============================================================ Config

    public class Milestone
    {
        public int Level;
        public string Group = "";       // Perm group to add when reached. Empty = announce only.
        public string Broadcast = "";   // Server-wide chat. {name} -> player name. Empty = no broadcast.
        public string Whisper = "";     // Private message. {name} -> player name. Empty = no whisper.
    }

    public class Config
    {
        // Curve: cumulative XP for level L = BaseXp * L^Exponent.
        // Defaults match the SkillTree curve (BaseXp=650.77 in their config).
        public double BaseXp   = 650.0;
        public double Exponent = 2.0;
        public int    MaxLevel = 100;

        // Optional override -- if non-empty, use this lookup table instead
        // of the formula. Keys are level numbers (as strings for JSON
        // compatibility), values are cumulative XP. Useful for matching
        // an exact external table.
        public Dictionary<string, long> CustomXpTable = new Dictionary<string, long>();

        // Milestone progression. Reaching a level fires the broadcast +
        // whisper + group-add. Order doesn't matter (we sort at load).
        public List<Milestone> Milestones = new List<Milestone>
        {
            new Milestone { Level = 25,  Group = "lvl25",
                            Broadcast = "{name} reached level 25!",
                            Whisper   = "[00FF66]You earned level 25 bonuses![-]" },
            new Milestone { Level = 50,  Group = "lvl50",
                            Broadcast = "{name} reached level 50!",
                            Whisper   = "[00FF66]You earned level 50 bonuses![-]" },
            new Milestone { Level = 75,  Group = "lvl75",
                            Broadcast = "{name} reached level 75!",
                            Whisper   = "[00FF66]You earned level 75 bonuses![-]" },
            new Milestone { Level = 100, Group = "lvl100",
                            Broadcast = "{name} reached the maximum level (100)!",
                            Whisper   = "[FFAA00]You have reached level 100 -- the apex rank.[-]" },
        };

        // Push xp/level/progress to player cvars so the styxHud window
        // can render them live.
        public bool DriveHudCvar = true;

        // Log every XP grant to the server log.
        public bool LogTransactions = false;

        // Extra perm required for /xp wipe. Empty (default) = only
        // styx.xp.admin needed. Set to a NON-styx.* perm (e.g. "ops.wipe")
        // to lock the wipe out from auth-0 visitors on a test server.
        // Operator manually grants the configured perm to themselves.
        public string WipeAdditionalPerm = "";
    }

    // ============================================================ Data

    public class XpData
    {
        // Per-player XP, keyed by PlatformId.
        public Dictionary<string, long> Xps =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<XpData> _xp;

    // ============================================================ Permissions

    private const string PermAdmin = "styx.xp.admin";

    // ============================================================ ILeveling

    public int MaxLevel => _cfg?.MaxLevel ?? 100;

    public long Xp(EntityPlayer player)
        => player == null ? 0 : Xp(StyxCore.Player.PlatformIdOf(player));

    public long Xp(string platformId)
    {
        if (string.IsNullOrEmpty(platformId) || _xp == null) return 0;
        return _xp.Value.Xps.TryGetValue(platformId, out var v) ? v : 0L;
    }

    public int Level(EntityPlayer player)
        => player == null ? 0 : Level(StyxCore.Player.PlatformIdOf(player));

    public int Level(string platformId) => LevelForXp(Xp(platformId));

    public long XpForLevel(int level)
    {
        if (level <= 0) return 0;
        if (level > MaxLevel) level = MaxLevel;
        if (_cfg.CustomXpTable != null && _cfg.CustomXpTable.Count > 0)
        {
            // Try exact match. If missing, fall through to formula.
            if (_cfg.CustomXpTable.TryGetValue(level.ToString(), out var v)) return v;
        }
        return (long)Math.Floor(_cfg.BaseXp * Math.Pow(level, _cfg.Exponent));
    }

    private int LevelForXp(long xp)
    {
        if (xp <= 0) return 0;
        // Linear search bounded by MaxLevel (cheap; max 100 iterations).
        int level = 0;
        for (int L = 1; L <= MaxLevel; L++)
        {
            if (XpForLevel(L) <= xp) level = L; else break;
        }
        return level;
    }

    public void AddXp(EntityPlayer player, long amount, string reason = null)
    {
        if (player == null || amount == 0) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return;

        long before = Xp(pid);
        int  beforeLvl = LevelForXp(before);
        long after = Math.Max(0, before + amount);
        int  afterLvl = LevelForXp(after);

        _xp.Value.Xps[pid] = after;
        _xp.Save();
        if (_cfg.LogTransactions)
            Log.Out("[StyxLeveling] XP {0} {1}{2} -> {3} (lvl {4}->{5}) ({6})",
                pid, amount >= 0 ? "+" : "", amount, after, beforeLvl, afterLvl, reason ?? "?");
        PushHud(player, after, afterLvl);

        if (afterLvl > beforeLvl)
            ApplyMilestones(player, beforeLvl, afterLvl);
    }

    public void SetXp(EntityPlayer player, long amount, string reason = null)
    {
        if (player == null) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        if (string.IsNullOrEmpty(pid)) return;
        amount = Math.Max(0, amount);
        int beforeLvl = LevelForXp(Xp(pid));
        _xp.Value.Xps[pid] = amount;
        _xp.Save();
        int afterLvl = LevelForXp(amount);
        if (_cfg.LogTransactions)
            Log.Out("[StyxLeveling] XP SET {0} = {1} (lvl {2}->{3}) ({4})",
                pid, amount, beforeLvl, afterLvl, reason ?? "?");
        PushHud(player, amount, afterLvl);
        if (afterLvl > beforeLvl)
            ApplyMilestones(player, beforeLvl, afterLvl);
    }

    private void ApplyMilestones(EntityPlayer player, int prevLevel, int newLevel)
    {
        if (_cfg.Milestones == null || _cfg.Milestones.Count == 0) return;
        var pid = StyxCore.Player.PlatformIdOf(player);
        string name = player.EntityName ?? "Player";

        foreach (var m in _cfg.Milestones)
        {
            if (m == null) continue;
            // Crossed this threshold this update?
            if (m.Level <= prevLevel || m.Level > newLevel) continue;

            // Add to group (additive -- groups stack).
            if (!string.IsNullOrEmpty(m.Group))
            {
                try
                {
                    if (!StyxCore.Perms.GroupExists(m.Group))
                        StyxCore.Perms.CreateGroup(m.Group);
                    StyxCore.Perms.AddPlayerToGroup(pid, m.Group);
                }
                catch (Exception e) { Log.Warning("[StyxLeveling] Group add failed for {0}: {1}", m.Group, e.Message); }
            }

            if (!string.IsNullOrEmpty(m.Broadcast))
                Styx.Server.Broadcast(m.Broadcast.Replace("{name}", name));
            if (!string.IsNullOrEmpty(m.Whisper))
                Styx.Server.Whisper(player, m.Whisper.Replace("{name}", name));
        }
    }

    private void PushHud(EntityPlayer p, long xp, int level)
    {
        if (_cfg == null || !_cfg.DriveHudCvar || p == null) return;
        long nextThreshold = XpForLevel(level + 1);
        long currLevelStart = XpForLevel(level);
        Styx.Ui.SetVar(p, "styx.xp.balance", xp);
        Styx.Ui.SetVar(p, "styx.xp.level",   level);
        Styx.Ui.SetVar(p, "styx.xp.next",    nextThreshold);
        Styx.Ui.SetVar(p, "styx.xp.this",    currLevelStart);
    }

    // ============================================================ Lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _xp  = this.Data.Store<XpData>("wallet");

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Admin: grant / take / set / wipe XP", Name);

        // Pre-create milestone groups so they exist for /perm grant tooling
        // even before any player has reached them.
        foreach (var m in _cfg.Milestones)
        {
            if (m != null && !string.IsNullOrEmpty(m.Group) && !StyxCore.Perms.GroupExists(m.Group))
            {
                try { StyxCore.Perms.CreateGroup(m.Group); }
                catch (Exception e) { Log.Warning("[StyxLeveling] Couldn't pre-create group '{0}': {1}", m.Group, e.Message); }
            }
        }

        StyxCore.Services.Publish<ILeveling>(this);

        Styx.Ui.Ephemeral.Register("styx.xp.loaded", "styx.xp.balance", "styx.xp.level",
                                    "styx.xp.next", "styx.xp.this");

        StyxCore.Commands.Register("xp",
            "Show / manage XP -- /xp [player|give|take|set|wipe]", CmdXp);

        Log.Out("[StyxLeveling] Loaded v0.1.0 -- {0} player(s), curve BaseXp={1} Exp={2} MaxLevel={3}, {4} milestone(s)",
            _xp.Value.Xps.Count, _cfg.BaseXp, _cfg.Exponent, _cfg.MaxLevel,
            _cfg.Milestones?.Count ?? 0);
    }

    public override void OnUnload()
    {
        StyxCore.Services?.Unpublish<ILeveling>(this);
        var players = StyxCore.Player?.All();
        if (players != null)
            foreach (var p in players) Styx.Ui.SetVar(p, "styx.xp.loaded", 0f);
    }

    void OnPlayerSpawned(ClientInfo client, RespawnType reason, Vector3i pos)
    {
        if (client == null) return;
        var p = StyxCore.Player.FindByEntityId(client.entityId);
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;

        if (_cfg.DriveHudCvar)
        {
            Styx.Ui.SetVar(p, "styx.xp.loaded", 1f);
            PushHud(p, Xp(pid), Level(pid));
        }
    }

    // ============================================================ Wipe

    private (int playerCount, int groupRemovals) WipeAllXp()
    {
        // Backup first.
        try
        {
            string backupName = "wallet.wiped-" + DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmss") + ".json";
            string srcDir = Path.Combine(StyxCore.DataPath(), Name);
            Directory.CreateDirectory(srcDir);
            string srcFile = Path.Combine(srcDir, "wallet.json");
            if (File.Exists(srcFile))
                File.Copy(srcFile, Path.Combine(srcDir, backupName), overwrite: true);
        }
        catch (Exception e) { Log.Warning("[StyxLeveling] Wipe backup failed: " + e.Message); }

        int n = _xp.Value.Xps.Count;
        var pids = new List<string>(_xp.Value.Xps.Keys);
        _xp.Value.Xps.Clear();
        _xp.Save();

        // Remove every known player from every milestone group.
        int removed = 0;
        foreach (var pid in pids)
        {
            foreach (var m in _cfg.Milestones)
            {
                if (m == null || string.IsNullOrEmpty(m.Group)) continue;
                try
                {
                    if (StyxCore.Perms.GroupExists(m.Group))
                    {
                        StyxCore.Perms.RemovePlayerFromGroup(pid, m.Group);
                        removed++;
                    }
                }
                catch { /* ignore -- player may not have been in group */ }
            }
        }

        // Reset live HUD for everyone online.
        var online = StyxCore.Player?.All();
        if (online != null)
            foreach (var p in online) PushHud(p, 0, 0);

        return (n, removed);
    }

    // ============================================================ Commands

    private void CmdXp(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        var actor = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        var actorId = StyxCore.Player.PlatformIdOf(actor);
        bool isAdmin = StyxCore.Perms.HasPermission(actorId, PermAdmin);

        // No args -- self status.
        if (args.Length == 0) { WhisperStatus(ctx, actor); return; }

        string sub = args[0].ToLowerInvariant();

        // /xp <player>  (admin query)
        if (sub != "give" && sub != "take" && sub != "set" && sub != "wipe")
        {
            if (!isAdmin) { ctx.Reply("[ff6666]No permission.[-]"); return; }
            var t = StyxCore.Player.Find(string.Join(" ", args));
            if (t == null) { ctx.Reply("[ff6666]Player not found.[-]"); return; }
            WhisperStatus(ctx, t);
            return;
        }

        if (!isAdmin) { ctx.Reply("[ff6666]No permission.[-]"); return; }

        if (sub == "wipe")
        {
            // Optional second-perm gate so test-server visitors with
            // auth-0 implicit styx.xp.admin can't trigger a wipe.
            if (!string.IsNullOrEmpty(_cfg.WipeAdditionalPerm) &&
                !StyxCore.Perms.HasPermission(actorId, _cfg.WipeAdditionalPerm))
            {
                ctx.Reply(string.Format(
                    "[ff6666]Wipe locked on this server (operator-only -- requires '{0}' perm).[-]",
                    _cfg.WipeAdditionalPerm));
                return;
            }
            if (args.Length < 2 || args[1].ToLowerInvariant() != "confirm")
            {
                ctx.Reply("[ffaa00]This wipes ALL player XP and removes EVERY player from every milestone group. Re-run as: /xp wipe confirm[-]");
                return;
            }
            var (players, groupOps) = WipeAllXp();
            ctx.Reply(string.Format("[00ff66]Wiped XP for {0} player(s); {1} group removal(s). Backup written.[-]",
                players, groupOps));
            return;
        }

        // give / take / set
        if (args.Length < 3)
        { ctx.Reply("Usage: /xp " + sub + " <player> <amount>"); return; }
        var target = StyxCore.Player.Find(args[1]);
        if (target == null) { ctx.Reply("[ff6666]Player not found / not online.[-]"); return; }
        if (!long.TryParse(args[2], out long amount))
        { ctx.Reply("[ff6666]Amount must be an integer.[-]"); return; }
        string reason = args.Length > 3 ? string.Join(" ", args, 3, args.Length - 3) : sub;

        switch (sub)
        {
            case "give":
                if (amount <= 0) { ctx.Reply("[ff6666]Amount must be positive.[-]"); return; }
                AddXp(target, amount, reason);
                ctx.Reply(string.Format("[00ff66]Gave {0} XP to {1} (now {2}, lvl {3}).[-]",
                    amount, target.EntityName, Xp(target), Level(target)));
                break;
            case "take":
                if (amount <= 0) { ctx.Reply("[ff6666]Amount must be positive.[-]"); return; }
                AddXp(target, -amount, reason);
                ctx.Reply(string.Format("[ffaa00]Took {0} XP from {1} (now {2}, lvl {3}).[-]",
                    amount, target.EntityName, Xp(target), Level(target)));
                break;
            case "set":
                SetXp(target, amount, reason);
                ctx.Reply(string.Format("[00ff66]Set {0}'s XP to {1} (lvl {2}).[-]",
                    target.EntityName, amount, Level(target)));
                break;
        }
    }

    private void WhisperStatus(Styx.Commands.CommandContext ctx, EntityPlayer p)
    {
        long xp = Xp(p);
        int  lvl = Level(p);
        long currStart = XpForLevel(lvl);
        long nextStart = lvl >= MaxLevel ? xp : XpForLevel(lvl + 1);
        if (lvl >= MaxLevel)
        {
            ctx.Reply(string.Format("[ffcc00][XP][-] {0}: [ffffff]MAX LEVEL ({1})[-] -- {2:N0} XP",
                p.EntityName, lvl, xp));
        }
        else
        {
            long need = nextStart - xp;
            ctx.Reply(string.Format("[ffcc00][XP][-] {0}: [ffffff]Level {1}[-] -- {2:N0}/{3:N0} XP ({4:N0} to next)",
                p.EntityName, lvl, xp, nextStart, need));
        }
    }
}
