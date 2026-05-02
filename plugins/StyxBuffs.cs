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

// StyxBuffs v0.3.0 — grant buffs by PERMISSION (not group), with user toggle UI.
//
// - configs/StyxBuffs.json defines a flat list of buffs, each with a Perm gate
// - A player is eligible for a buff iff they hold its Perm
// - Multiple buffs can share a perm (e.g. styx.buffs.vip gates 5 buffs at once)
// - Operator wires perm-to-group assignment in the standard perm editor
//   (/perm or /m → Perm Editor) -- this plugin no longer owns the mapping
// - On player join/spawn and every ReapplyIntervalSeconds: eligible buffs apply
// - /m → "My Buffs" opens a picker where players toggle their own buffs on/off
//   - ON state is the default; buffs reapply automatically
//   - OFF state is persisted per-player (data/StyxBuffs.sessions.json) and
//     skipped by the reapply loop so toggles stick across reconnect / server restart
//
// Perms (registered automatically; configure via the perm editor):
//   styx.buffs.use    — see the /m → My Buffs entry
//   styx.buffs.admin  — run /buffs reapply and see everyone's perks
//   styx.buffs.<...>  — any perm name an operator declares on a buff entry
//
// Migration from v0.2.x: if the loaded config has the old GroupBuffs / GroupCVars
// keys, they get one-time-converted to the new shape and the file rewritten.
// Each entry is tagged with Perm = "styx.buffs.<groupname>". After migration
// the operator must grant those perms to the matching groups in the perm editor.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxBuffs", "Doowkcol", "0.4.0")]
public class StyxBuffs : StyxPlugin
{
    public override string Description => "Perm-gated toggle + cooldown buffs with player-facing picker UI";

    private const string PermAdmin = "styx.buffs.admin";
    private const string PermUse   = "styx.buffs.use";
    private const int MaxUiRows = 20;

    // Per-row status enum used by the XUi (gates which status label is visible
    // and what colour/text shows). Numeric so the cvar binding can compare cheaply.
    private const int StatusNoPerm   = 0;
    private const int StatusOff      = 1;
    private const int StatusOn       = 2;
    private const int StatusReady    = 3;
    private const int StatusActive   = 4;
    private const int StatusCooldown = 5;

    public class BuffEntry
    {
        public string Buff;                   // e.g. "buffDrugAtomJunkies"
        public string Perm;                   // gate -- player must hold this perm
        public int DurationSeconds = 3600;
        // 0 (default) = always-on toggle. The buff applies on join/spawn and
        // re-applies on the reapply tick; the player toggles it on/off.
        // > 0 = activate-on-click cooldown buff. Player clicks Ready -> buff
        // applies for DurationSeconds, then enters cooldown for CooldownSeconds,
        // then becomes Ready again. Cooldown buffs are NOT auto-applied.
        public int CooldownSeconds = 0;
        public string DisplayName;            // optional UI label; falls back to Buff if null/empty
        public string Description;            // optional whisper text when highlighted
        public bool UserToggleable = true;    // toggle buffs only -- false = player can't disable
        public string Icon;                   // optional UIAtlas sprite name (e.g. "ui_game_symbol_fist")
    }

    public class CVarEntry
    {
        public string Name;
        public float Value;
        public string Perm;                   // gate -- player must hold this perm
    }

    public class Config
    {
        public int ReapplyIntervalSeconds = 1800; // 30 min

        // Flat list of buffs, each gated by a perm. Multiple entries can share
        // a perm (e.g. styx.buffs.vip gates the 5 VIP buffs below). Operator
        // grants the configured perms to groups via the perm editor.
        public List<BuffEntry> Buffs = new List<BuffEntry>
        {
            new BuffEntry { Buff = "buffStyxVipUndead",    Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Undead Slayer",
                Description = "+100% damage to zombies",
                Icon = "ui_game_symbol_zombie" },
            new BuffEntry { Buff = "buffStyxVipHarvest",   Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Harvest Master",
                Description = "+100% harvest yield, +50% block damage",
                Icon = "ui_game_symbol_wrench" },
            new BuffEntry { Buff = "buffStyxVipToughness", Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "VIP: Toughness",
                Description = "+60 resist, +30% run speed, +40 carry, +50 HP",
                Icon = "ui_game_symbol_armor_iron" },
            new BuffEntry { Buff = "buffDrugAtomJunkies",  Perm = "styx.buffs.vip", DurationSeconds = 3600,
                DisplayName = "Atom Junkies",
                Description = "Vanilla drug -- periodic HP regen",
                Icon = "ui_game_symbol_medical" },
            new BuffEntry { Buff = "buffDrugRecog",        Perm = "styx.buffs.vip", DurationSeconds = 1800,
                DisplayName = "Recog",
                Description = "Vanilla drug -- loot quality bonus",
                Icon = "ui_game_symbol_loot_sack" },
            new BuffEntry { Buff = "buffDrugSteroids",     Perm = "styx.buffs.admin", DurationSeconds = 3600,
                DisplayName = "Steroids",
                Description = "Vanilla drug -- +50 carry, +10% run speed",
                Icon = "ui_game_symbol_steroids" },
            // ---- Cooldown example: vip-tier energy drink ----
            new BuffEntry { Buff = "buffMegaCrush", Perm = "styx.buffs.vip",
                DurationSeconds = 600, CooldownSeconds = 3600,
                DisplayName = "Mega Crush (10m)",
                Description = "Vanilla energy drink -- 10 min stamina + damage rush, 1h cooldown",
                Icon = "ui_game_symbol_pills" },

            // ============================================================
            // Level-milestone tiers. Each tier's perm corresponds to a
            // StyxLeveling milestone group (lvl25/lvl50/lvl75/lvl100).
            // Operator grants e.g. styx.buffs.lvl25 to the lvl25 group via
            // the perm editor; reaching the level auto-adds the player to
            // the group, unlocking the buffs.
            // ============================================================

            // ---- lvl25 -- early game (survival / utility) ----
            new BuffEntry { Buff = "buffDrugVitamins", Perm = "styx.buffs.lvl25", DurationSeconds = 3600,
                DisplayName = "L25: Vitamins",
                Description = "Resist infection + dysentery from food/drink",
                Icon = "ui_game_symbol_pills" },
            new BuffEntry { Buff = "buffDrugSugarButts", Perm = "styx.buffs.lvl25", DurationSeconds = 3600,
                DisplayName = "L25: Sugar Butts",
                Description = "+10% trader buy + sell prices",
                Icon = "ui_game_symbol_candy_sugar_butts" },
            new BuffEntry { Buff = "buffDrugPainkillers", Perm = "styx.buffs.lvl25",
                DurationSeconds = 30, CooldownSeconds = 600,
                DisplayName = "L25: Painkillers (heal)",
                Description = "Burst-heal 40 HP + stun resist. 10min cooldown.",
                Icon = "ui_game_symbol_pills" },

            // ---- lvl50 -- mid game (combat) ----
            new BuffEntry { Buff = "buffDrugSkullCrushers", Perm = "styx.buffs.lvl50", DurationSeconds = 3600,
                DisplayName = "L50: Skull Crushers",
                Description = "+50% melee damage with most weapon perks",
                Icon = "ui_game_symbol_candy_skull_crushers" },
            new BuffEntry { Buff = "buffDrugCovertCats", Perm = "styx.buffs.lvl50", DurationSeconds = 3600,
                DisplayName = "L50: Covert Cats",
                Description = "+50% sneak attack damage while crouched + unalerted",
                Icon = "ui_game_symbol_candy_covert_cats" },
            new BuffEntry { Buff = "buffDrugFortBites", Perm = "styx.buffs.lvl50",
                DurationSeconds = 600, CooldownSeconds = 1800,
                DisplayName = "L50: Fort Bites (resist)",
                Description = "+40% general damage resist for 10 min. 30 min cooldown.",
                Icon = "ui_game_symbol_candy_fortitude" },

            // ---- lvl75 -- late game (resource utility) ----
            new BuffEntry { Buff = "buffDrugRockBusters", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Rock Busters",
                Description = "+20% wood + ore harvest yield",
                Icon = "ui_game_symbol_candy_rock_busters" },
            new BuffEntry { Buff = "buffDrugHackers", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Hackers",
                Description = "+20% salvage harvest yield",
                Icon = "ui_game_symbol_candy_hackers" },
            new BuffEntry { Buff = "buffDrugJailBreakers", Perm = "styx.buffs.lvl75", DurationSeconds = 3600,
                DisplayName = "L75: Jail Breakers",
                Description = "Lockpicks break drastically less often",
                Icon = "ui_game_symbol_candy_jail_breakers" },

            // ---- lvl100 -- endgame (premium) ----
            new BuffEntry { Buff = "buffDrugEyeKandy", Perm = "styx.buffs.lvl100", DurationSeconds = 3600,
                DisplayName = "L100: Eye Kandy",
                Description = "+5 LootStage flat + 10% extra -- noticeably better loot rolls",
                Icon = "ui_game_symbol_candy_eye_candy" },
            new BuffEntry { Buff = "buffDrugHealthBar", Perm = "styx.buffs.lvl100", DurationSeconds = 3600,
                DisplayName = "L100: Health Bar",
                Description = "+10 max HP + 25% resist to bleed/sprain/infection trigger",
                Icon = "ui_game_symbol_candy_health_bar" },
            new BuffEntry { Buff = "buffBerserker", Perm = "styx.buffs.lvl100",
                DurationSeconds = 300, CooldownSeconds = 1200,
                DisplayName = "L100: Berserker (rage)",
                Description = "+25% damage resist for 5 min. 20 min cooldown.",
                Icon = "ui_game_symbol_berserker" },
        };

        public List<CVarEntry> CVars = new List<CVarEntry>
        {
            new CVarEntry { Name = "$treatedCommando", Value = 0.5f, Perm = "styx.buffs.vip" },
        };

        // ---- Deprecated v0.2.x fields, kept ONLY for one-time migration ----
        // After load, if these are non-null/non-empty AND the new fields are empty,
        // contents get converted (Perm = "styx.buffs.<groupname>") and the fields
        // are nulled before the config is saved back. Operators upgrading from
        // v0.2.x will see one log line and the file rewritten in the new shape.
        public Dictionary<string, List<BuffEntry>> GroupBuffs;
        public Dictionary<string, List<CVarEntry>> GroupCVars;
    }

    public class State
    {
        public HashSet<string> Toasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // playerId → set of buff names the player has toggled OFF. Persisted.
        public Dictionary<string, HashSet<string>> UserDisabled =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // playerId → buffName → unix-timestamp seconds of the last activation
        // for cooldown buffs. Persisted across reconnect / server restart so a
        // player can't dodge a cooldown by relogging.
        public Dictionary<string, Dictionary<string, long>> LastActivated =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _tick;

    // Alphabetically-sorted unique list of every buff name that appears in
    // any group. Index in this list is the GLOBAL slot id used in the
    // Labels table (buffs_name_N). Computed at OnLoad so it's stable
    // for the whole session.
    private List<string> _allBuffs = new List<string>();
    private Dictionary<string, BuffEntry> _buffMeta = new Dictionary<string, BuffEntry>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("sessions");

        // One-time migration from v0.2.x group-keyed config to v0.3.0 perm-gated.
        MigrateOldConfigIfPresent();

        // Re-apply on a timer so long buffs don't silently drop off.
        int interval = Math.Max(60, _cfg.ReapplyIntervalSeconds);
        _tick = Scheduler.Every(interval, () => ReapplyAll(), name: "StyxBuffs.reapply");

        // Build the global buff table + register labels for the UI.
        BuildBuffCatalog();

        // Register launcher entry. Gate is dynamic ("do you have any available
        // buffs?") so we register without perm and handle the empty case at open time.
        Styx.Ui.Menu.Register(this, "My Buffs  /buffs", OpenMenuFor, permission: PermUse);

        Styx.Ui.Ephemeral.Register(
            "styx.buffs.open", "styx.buffs.sel", "styx.buffs.count", "styx.buffs.desc_id");
        for (int k = 0; k < MaxUiRows; k++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.buffs.row" + k + "_id",
                "styx.buffs.row" + k + "_status",
                "styx.buffs.row" + k + "_cd");
        }

        StyxCore.Commands.Register("buffs", "Open the My Buffs panel -- /buffs [status|close|reapply]", (ctx, args) =>
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "open";
            switch (sub)
            {
                case "open":
                    if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
                    var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                    if (p == null) { ctx.Reply("Player not found."); return; }
                    var pid = ctx.Client.PlatformId?.CombinedString;
                    if (!string.IsNullOrEmpty(pid) && !StyxCore.Perms.HasPermission(pid, PermUse))
                    { ctx.Reply("[ff6666]You lack permission '" + PermUse + "'.[-]"); return; }
                    OpenMenuFor(p);
                    break;
                case "close":
                    if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
                    var pc = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                    if (pc != null) CloseFor(pc);
                    ctx.Reply("[ffaa00]My Buffs closed.[-]");
                    break;
                case "status":  ShowStatus(ctx); break;
                case "reapply":
                    if (!RequireAdmin(ctx)) return;
                    int n = ReapplyAll();
                    ctx.Reply("[00ff66]Reapplied to " + n + " player(s).[-]");
                    break;
                default:
                    ctx.Reply("Usage: /buffs [open|close|status] | /buffs reapply (admin)");
                    break;
            }
        });

        // Framework-baseline perms.
        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Run /buffs reapply and view all players' buff states", Name);
        StyxCore.Perms.RegisterKnown(PermUse,
            "See the My Buffs launcher entry (toggle your buffs on/off)", Name);

        // Register every distinct gate-perm declared on Buffs / CVars so they
        // show up in the perm editor and can be granted to groups.
        var seenPerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PermAdmin, PermUse };
        if (_cfg.Buffs != null)
        {
            foreach (var be in _cfg.Buffs)
            {
                if (be == null || string.IsNullOrEmpty(be.Perm)) continue;
                if (!seenPerms.Add(be.Perm)) continue;
                StyxCore.Perms.RegisterKnown(be.Perm,
                    "StyxBuffs gate -- grants the buff(s) tagged with this perm",
                    Name);
            }
        }
        if (_cfg.CVars != null)
        {
            foreach (var cv in _cfg.CVars)
            {
                if (cv == null || string.IsNullOrEmpty(cv.Perm)) continue;
                if (!seenPerms.Add(cv.Perm)) continue;
                StyxCore.Perms.RegisterKnown(cv.Perm,
                    "StyxBuffs cvar gate -- grants the cvar write(s) tagged with this perm",
                    Name);
            }
        }

        int buffCount = _cfg.Buffs?.Count ?? 0;
        int cvarCount = _cfg.CVars?.Count ?? 0;
        Log.Out("[StyxBuffs] Loaded v0.3.0 -- {0} buff entr(ies), {1} cvar entr(ies), {2} unique gate perm(s), reapply every {3}s",
            buffCount, cvarCount, seenPerms.Count - 2, interval);
    }

    /// <summary>
    /// Convert v0.2.x group-keyed config to v0.3.0 perm-gated config in place.
    /// Each entry's <see cref="BuffEntry.Perm"/> is filled with
    /// "styx.buffs.&lt;groupname&gt;". Old fields are nulled and the config is
    /// saved back so we never migrate twice.
    /// </summary>
    private void MigrateOldConfigIfPresent()
    {
        bool migrated = false;

        // Presence of legacy GroupBuffs in the JSON wins -- it's the operator's
        // source of truth from v0.2.x. The C# field default for Buffs is the
        // built-in sample set, which we clobber here. (A brand-new install has
        // GroupBuffs == null because no JSON file exists yet, so the default
        // Buffs survives.)
        if (_cfg.GroupBuffs != null && _cfg.GroupBuffs.Count > 0)
        {
            _cfg.Buffs = new List<BuffEntry>();
            foreach (var kv in _cfg.GroupBuffs)
            {
                string perm = "styx.buffs." + (kv.Key ?? "default").ToLowerInvariant();
                if (kv.Value == null) continue;
                foreach (var be in kv.Value)
                {
                    if (be == null) continue;
                    if (string.IsNullOrEmpty(be.Perm)) be.Perm = perm;
                    _cfg.Buffs.Add(be);
                }
            }
            Log.Out("[StyxBuffs] Migrated {0} GroupBuffs entr(ies) to perm-gated Buffs. " +
                "Grant the styx.buffs.<group> perms to the matching groups via the perm editor.",
                _cfg.GroupBuffs.Count);
            _cfg.GroupBuffs = null;
            migrated = true;
        }

        if (_cfg.GroupCVars != null && _cfg.GroupCVars.Count > 0)
        {
            _cfg.CVars = new List<CVarEntry>();
            foreach (var kv in _cfg.GroupCVars)
            {
                string perm = "styx.buffs." + (kv.Key ?? "default").ToLowerInvariant();
                if (kv.Value == null) continue;
                foreach (var cv in kv.Value)
                {
                    if (cv == null) continue;
                    if (string.IsNullOrEmpty(cv.Perm)) cv.Perm = perm;
                    _cfg.CVars.Add(cv);
                }
            }
            _cfg.GroupCVars = null;
            migrated = true;
        }

        if (migrated)
        {
            try { StyxCore.Configs.Save(this, _cfg); }
            catch (Exception e) { Log.Warning("[StyxBuffs] Migration save failed: " + e.Message); }
        }
    }

    public override void OnUnload()
    {
        _tick?.Destroy();
        _tick = null;
        _uiOpenFor.Clear();

        StyxCore.Perms.UnregisterKnownByOwner(Name);
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);
    }

    // ---- catalog / label registration ----

    private void BuildBuffCatalog()
    {
        _buffMeta.Clear();
        if (_cfg.Buffs != null)
        {
            foreach (var be in _cfg.Buffs)
            {
                if (string.IsNullOrEmpty(be?.Buff)) continue;
                // First entry wins for metadata. Later entries with the same
                // Buff but different Perm are still consulted for eligibility.
                if (!_buffMeta.ContainsKey(be.Buff)) _buffMeta[be.Buff] = be;
            }
        }

        _allBuffs = _buffMeta.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        for (int i = 0; i < _allBuffs.Count && i < MaxUiRows; i++)
        {
            var name = _allBuffs[i];
            var meta = _buffMeta[name];
            string display = !string.IsNullOrEmpty(meta.DisplayName) ? meta.DisplayName : name;
            string icon = !string.IsNullOrEmpty(meta.Icon) ? meta.Icon : "ui_game_symbol_fist";
            Styx.Ui.Labels.Register(this, "buffs_name_" + i, display);
            Styx.Ui.Labels.Register(this, "buffs_desc_" + i, meta.Description ?? "");
            Styx.Ui.Labels.Register(this, "buffs_icon_" + i, icon);
        }
        for (int i = _allBuffs.Count; i < MaxUiRows; i++)
        {
            Styx.Ui.Labels.Register(this, "buffs_name_" + i, "(no buff)");
            Styx.Ui.Labels.Register(this, "buffs_desc_" + i, "");
            Styx.Ui.Labels.Register(this, "buffs_icon_" + i, "");
        }
    }

    // ---- spawn hook ----

    public void OnPlayerSpawned(ClientInfo ci, RespawnType reason)
    {
        if (ci != null) ApplyForClient(ci, toastIfFirst: true);
    }

    // ---- core apply ----

    /// <summary>
    /// Computes the current per-buff status for a player. Returned tuple is
    /// (status, cdSeconds) where cdSeconds is meaningful only for
    /// <see cref="StatusActive"/> (seconds-of-buff-remaining) and
    /// <see cref="StatusCooldown"/> (seconds-until-ready).
    ///
    /// Perm check: if the player holds NO matching gate-perm across all
    /// config entries with this buff name, returns NoPerm. Otherwise the
    /// FIRST matching entry (in config order) drives the cooldown semantics.
    /// </summary>
    private (int status, int cdSec) ComputeStatus(string platformId, string buffName)
    {
        if (string.IsNullOrEmpty(platformId) || string.IsNullOrEmpty(buffName))
            return (StatusNoPerm, 0);
        if (_cfg.Buffs == null) return (StatusNoPerm, 0);

        BuffEntry matched = null;
        foreach (var be in _cfg.Buffs)
        {
            if (be == null || be.Buff != buffName) continue;
            if (string.IsNullOrEmpty(be.Perm)) continue;
            if (!StyxCore.Perms.HasPermission(platformId, be.Perm)) continue;
            matched = be;
            break;
        }
        if (matched == null) return (StatusNoPerm, 0);

        if (matched.CooldownSeconds <= 0)
        {
            // Toggle buff
            if (_state.Value.UserDisabled.TryGetValue(platformId, out var dis)
                && dis.Contains(buffName))
                return (StatusOff, 0);
            return (StatusOn, 0);
        }

        // Cooldown buff
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long lastAct = 0;
        if (_state.Value.LastActivated.TryGetValue(platformId, out var per)
            && per.TryGetValue(buffName, out var t))
            lastAct = t;

        if (lastAct == 0) return (StatusReady, 0);

        long elapsed = now - lastAct;
        if (elapsed < matched.DurationSeconds)
            return (StatusActive, (int)(matched.DurationSeconds - elapsed));

        long cdEnd = matched.DurationSeconds + matched.CooldownSeconds;
        if (elapsed < cdEnd)
            return (StatusCooldown, (int)(cdEnd - elapsed));

        return (StatusReady, 0);
    }

    private int ApplyForClient(ClientInfo ci, bool toastIfFirst)
    {
        if (ci == null) return 0;
        var player = StyxCore.Player.FindByEntityId(ci.entityId);
        if (player == null) return 0;

        string platformId = ci.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(platformId)) return 0;

        // Auto-apply only TOGGLE buffs that are currently On. Cooldown buffs
        // (CooldownSeconds > 0) are user-triggered, never auto-applied.
        var applied = new List<string>();
        foreach (var name in _allBuffs)
        {
            if (!_buffMeta.TryGetValue(name, out var meta)) continue;
            if (meta.CooldownSeconds > 0) continue;  // skip cooldown buffs
            var (status, _) = ComputeStatus(platformId, name);
            if (status != StatusOn) continue;        // skip NoPerm / Off
            if (StyxCore.Player.ApplyBuff(player, name, meta.DurationSeconds))
                applied.Add(name);
        }

        // CVars are perm-gated like buffs. Not user-toggleable in v0.3.0.
        if (_cfg.CVars != null)
        {
            var seenCv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in _cfg.CVars)
            {
                if (string.IsNullOrEmpty(cv?.Name)) continue;
                if (string.IsNullOrEmpty(cv.Perm)) continue;
                if (!StyxCore.Perms.HasPermission(platformId, cv.Perm)) continue;
                if (!seenCv.Add(cv.Name)) continue;   // first matching entry wins
                StyxCore.Player.SetCVar(player, cv.Name, cv.Value);
                applied.Add(cv.Name + "=" + cv.Value);
            }
        }

        if (applied.Count > 0)
        {
            Log.Out("[StyxBuffs] {0}: applied {1} ({2})",
                ci.playerName, applied.Count, string.Join(", ", applied));

            if (toastIfFirst && !_state.Value.Toasted.Contains(platformId))
            {
                _state.Value.Toasted.Add(platformId);
                _state.Save();
                Styx.Ui.Toast(player,
                    "Buffs active — /m → My Buffs to toggle",
                    Styx.Ui.Sounds.ChallengeRedeem);
            }
        }
        return applied.Count;
    }

    private int ReapplyAll()
    {
        int touched = 0;
        foreach (var ci in StyxCore.Player.AllClients())
        {
            if (ci?.entityId <= 0) continue;
            if (ApplyForClient(ci, toastIfFirst: false) > 0) touched++;
        }
        return touched;
    }

    // ---- UI ----

    private void OpenMenuFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid))
        {
            Styx.Server.Whisper(p, "[ff6666][My Buffs] Could not resolve your player id.[-]");
            return;
        }

        if (_allBuffs.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] No buffs configured on this server.[-]");
            return;
        }

        _uiOpenFor.Add(p.entityId);
        Styx.Ui.SetVar(p, "styx.buffs.open", 1f);
        Styx.Ui.SetVar(p, "styx.buffs.sel", 0f);
        int shown = Math.Min(_allBuffs.Count, MaxUiRows);
        Styx.Ui.SetVar(p, "styx.buffs.count", shown);

        // Show ALL configured buffs. Per-row status reveals access (NoPerm /
        // On / Off / Ready / Active / Cooldown) so the player sees what they
        // could have, not just what's already granted.
        for (int k = 0; k < MaxUiRows; k++)
        {
            int id = 0, status = StatusNoPerm, cd = 0;
            if (k < shown)
            {
                var buff = _allBuffs[k];
                id = k;  // _allBuffs index IS the label slot index
                var (s, c) = ComputeStatus(pid, buff);
                status = s;
                cd = c;
            }
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_id", id);
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_status", status);
            Styx.Ui.SetVar(p, "styx.buffs.row" + k + "_cd", cd);
        }
        // Description tracks the currently-selected row.
        Styx.Ui.SetVar(p, "styx.buffs.desc_id", 0);

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.buffs.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_uiOpenFor.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.buffs.open") != 1) return;

        var pid = StyxCore.Player.PlatformIdOf(p);
        int count = Math.Min(_allBuffs.Count, MaxUiRows);
        if (count == 0) { CloseFor(p); return; }

        int sel = (int)p.Buffs.GetCustomVar("styx.buffs.sel");

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.buffs.sel", sel);
                Styx.Ui.SetVar(p, "styx.buffs.desc_id", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.buffs.sel", sel);
                Styx.Ui.SetVar(p, "styx.buffs.desc_id", sel);
                WhisperRow(p, sel);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                ActivateOrToggle(p, pid, sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxBuffs.BackToLauncher");
                break;
        }
    }

    /// <summary>
    /// Dispatches an LMB click on a row to the right action based on the
    /// computed status (toggle vs cooldown vs no-perm vs already-active).
    /// </summary>
    private void ActivateOrToggle(EntityPlayer p, string pid, int rowSel)
    {
        if (rowSel < 0 || rowSel >= _allBuffs.Count) return;
        string buffName = _allBuffs[rowSel];
        if (!_buffMeta.TryGetValue(buffName, out var meta)) return;

        var (status, cd) = ComputeStatus(pid, buffName);

        switch (status)
        {
            case StatusNoPerm:
                Styx.Server.Whisper(p, "[888888][My Buffs] No perm for '" + DisplayOf(meta) + "'.[-]");
                return;

            case StatusActive:
                Styx.Server.Whisper(p, string.Format(
                    "[ffaa00][My Buffs] '{0}' already active -- {1}s left.[-]",
                    DisplayOf(meta), cd));
                return;

            case StatusCooldown:
                Styx.Server.Whisper(p, string.Format(
                    "[ffaa00][My Buffs] '{0}' on cooldown -- ready in {1}s.[-]",
                    DisplayOf(meta), cd));
                return;

            case StatusReady:
                ActivateCooldownBuff(p, pid, buffName, meta, rowSel);
                return;

            case StatusOn:
            case StatusOff:
                ToggleBuff(p, pid, buffName, meta, rowSel, currentlyOn: status == StatusOn);
                return;
        }
    }

    private void ActivateCooldownBuff(EntityPlayer p, string pid, string buffName, BuffEntry meta, int rowSel)
    {
        if (!meta.UserToggleable)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] '" + DisplayOf(meta) + "' is locked (not user-activatable).[-]");
            return;
        }

        // Apply the buff FIRST. If the buff name is invalid (typo, missing
        // from buffs.xml, etc.) ApplyBuff returns false; we don't want to
        // lock the cooldown for a buff that didn't actually apply.
        bool ok = StyxCore.Player.ApplyBuff(p, buffName, meta.DurationSeconds);
        if (!ok)
        {
            Log.Warning("[StyxBuffs] ApplyBuff failed for '" + buffName + "' -- check the buff name exists in buffs.xml.");
            Styx.Server.Whisper(p, string.Format(
                "[ff6666][My Buffs] '{0}' could not be applied (server config issue -- log has details).[-]",
                DisplayOf(meta)));
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!_state.Value.LastActivated.TryGetValue(pid, out var perPlayer))
        {
            perPlayer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _state.Value.LastActivated[pid] = perPlayer;
        }
        perPlayer[buffName] = now;
        _state.Save();

        Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusActive);
        Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_cd", meta.DurationSeconds);

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][My Buffs] '{0}' activated -- {1}s active, then {2}s cooldown.[-]",
            DisplayOf(meta), meta.DurationSeconds, meta.CooldownSeconds));
    }

    private void ToggleBuff(EntityPlayer p, string pid, string buffName, BuffEntry meta, int rowSel, bool currentlyOn)
    {
        if (!meta.UserToggleable)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] '" + DisplayOf(meta) + "' is admin-locked (not user-toggleable).[-]");
            return;
        }

        var disabled = GetOrCreateDisabledSet(pid, save: true);

        if (currentlyOn)
        {
            // Toggle OFF: add to disabled set, remove the buff.
            disabled.Add(buffName);
            StyxCore.Player.RemoveBuff(p, buffName);
            Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusOff);
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] " + DisplayOf(meta) + ": OFF (won't reapply)[-]");
        }
        else
        {
            // Toggle ON: remove from disabled set, apply the buff immediately.
            disabled.Remove(buffName);
            StyxCore.Player.ApplyBuff(p, buffName, meta.DurationSeconds);
            Styx.Ui.SetVar(p, "styx.buffs.row" + rowSel + "_status", StatusOn);
            Styx.Server.Whisper(p, "[00ff66][My Buffs] " + DisplayOf(meta) + ": ON[-]");
        }
        _state.Save();
    }

    private void WhisperRow(EntityPlayer p, int idx)
    {
        if (idx < 0 || idx >= _allBuffs.Count) return;
        var buffName = _allBuffs[idx];
        if (!_buffMeta.TryGetValue(buffName, out var meta)) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        var (status, cd) = ComputeStatus(pid, buffName);

        string statusText;
        switch (status)
        {
            case StatusNoPerm:   statusText = "[888888][No Perm][-]"; break;
            case StatusOff:      statusText = "[ffaa00]OFF[-]"; break;
            case StatusOn:       statusText = "[00ff66]ON[-]"; break;
            case StatusReady:    statusText = "[66ddff]Ready[-]"; break;
            case StatusActive:   statusText = string.Format("[00ff66]Active ({0}s)[-]", cd); break;
            case StatusCooldown: statusText = string.Format("[ffcc66]Cooldown {0}s[-]", cd); break;
            default:             statusText = "?"; break;
        }

        int total = Math.Min(_allBuffs.Count, MaxUiRows);
        Styx.Server.Whisper(p, string.Format(
            "[ccddff][My Buffs] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  {4}",
            idx + 1, total, DisplayOf(meta), meta.Description ?? "", statusText));
    }

    private HashSet<string> GetOrCreateDisabledSet(string pid, bool save)
    {
        if (!_state.Value.UserDisabled.TryGetValue(pid, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _state.Value.UserDisabled[pid] = set;
            if (save) _state.Save();
        }
        return set;
    }

    private static string DisplayOf(BuffEntry be)
        => !string.IsNullOrEmpty(be?.DisplayName) ? be.DisplayName : (be?.Buff ?? "?");

    // ---- commands ----

    private bool RequireAdmin(Styx.Commands.CommandContext ctx)
    {
        string id = ctx.Client?.PlatformId?.CombinedString;
        if (ctx.Client == null) return true;
        if (!string.IsNullOrEmpty(id) && StyxCore.Perms.HasPermission(id, PermAdmin)) return true;
        ctx.Reply("[ff6666]You don't have permission.[-]");
        return false;
    }

    private void ShowStatus(Styx.Commands.CommandContext ctx)
    {
        string id = ctx.Client?.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(id)) { ctx.Reply("No player context."); return; }

        ctx.Reply("Buffs configured (" + _allBuffs.Count + "):");
        foreach (var name in _allBuffs)
        {
            var meta = _buffMeta.TryGetValue(name, out var m) ? m : null;
            var (status, cd) = ComputeStatus(id, name);
            string statusText;
            switch (status)
            {
                case StatusNoPerm:   statusText = "[888888][No Perm][-]"; break;
                case StatusOff:      statusText = "[ffaa00]OFF[-]"; break;
                case StatusOn:       statusText = "[00ff66]ON[-]"; break;
                case StatusReady:    statusText = "[66ddff]Ready[-]"; break;
                case StatusActive:   statusText = string.Format("[00ff66]Active {0}s[-]", cd); break;
                case StatusCooldown: statusText = string.Format("[ffcc66]Cooldown {0}s[-]", cd); break;
                default:             statusText = "?"; break;
            }
            ctx.Reply("  " + DisplayOf(meta) + " (" + name + ") " + statusText);
        }
        ctx.Reply("[888888]Use /m → My Buffs to toggle / activate.[-]");
    }
}
