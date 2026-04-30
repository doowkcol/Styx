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

// DonorPerks v0.2.0 — grant buffs by permission group, with user toggle UI.
//
// - configs/DonorPerks.json maps group-name -> list of buff-name + DisplayName + Description
// - On player join/spawn and every ReapplyIntervalSeconds: eligible buffs are applied
// - NEW: /m → "My Buffs" opens a picker where players toggle their own buffs on/off
//   - ON state is the default; buffs reapply automatically
//   - OFF state is persisted per-player (data/DonorPerks.sessions.json) and
//     skipped by the reapply loop so toggles stick across reconnect / server restart
//   - Whispers show buff description + current ON/OFF state as you navigate
//
// Perms:
//   styx.donor.admin  — run /donor reapply and see everyone's perks
//
// Grant group membership with:
//   styx usergroup add user Steam_xxxxxxxxxx vip
//   styx group create vip (if you haven't already)

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;

[Info("DonorPerks", "Doowkcol", "0.2.0")]
public class DonorPerks : StyxPlugin
{
    public override string Description => "Apply donor-group buffs with player-facing toggle UI";

    private const string PermAdmin = "styx.donor.admin";
    private const int MaxUiRows = 8;

    public class BuffEntry
    {
        public string Buff;                   // e.g. "buffDrugAtomJunkies"
        public int DurationSeconds = 3600;
        public string DisplayName;            // optional UI label; falls back to Buff if null/empty
        public string Description;            // optional whisper text when highlighted
        public bool UserToggleable = true;    // if false, player can't disable this buff
        public string Icon;                   // optional UIAtlas sprite name (e.g. "ui_game_symbol_fist")
    }

    public class CVarEntry
    {
        public string Name;
        public float Value;
    }

    public class Config
    {
        public int ReapplyIntervalSeconds = 1800; // 30 min

        public Dictionary<string, List<BuffEntry>> GroupBuffs =
            new Dictionary<string, List<BuffEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["vip"] = new List<BuffEntry>
            {
                new BuffEntry { Buff = "buffStyxVipUndead",    DurationSeconds = 3600,
                    DisplayName = "VIP: Undead Slayer",
                    Description = "+100% damage to zombies",
                    Icon = "ui_game_symbol_zombie" },
                new BuffEntry { Buff = "buffStyxVipHarvest",   DurationSeconds = 3600,
                    DisplayName = "VIP: Harvest Master",
                    Description = "+100% harvest yield, +50% block damage",
                    Icon = "ui_game_symbol_wrench" },
                new BuffEntry { Buff = "buffStyxVipToughness", DurationSeconds = 3600,
                    DisplayName = "VIP: Toughness",
                    Description = "+60 resist, +30% run speed, +40 carry, +50 HP",
                    Icon = "ui_game_symbol_armor_iron" },
                new BuffEntry { Buff = "buffDrugAtomJunkies",  DurationSeconds = 3600,
                    DisplayName = "Atom Junkies",
                    Description = "Vanilla drug — periodic HP regen",
                    Icon = "ui_game_symbol_medical" },
                new BuffEntry { Buff = "buffDrugRecog",        DurationSeconds = 1800,
                    DisplayName = "Recog",
                    Description = "Vanilla drug — loot quality bonus",
                    Icon = "ui_game_symbol_loot_sack" },
            },
            ["admin"] = new List<BuffEntry>
            {
                new BuffEntry { Buff = "buffDrugSteroids",    DurationSeconds = 3600,
                    DisplayName = "Steroids",
                    Description = "Vanilla drug — +stamina regen, +damage",
                    Icon = "ui_game_symbol_agility" },
                new BuffEntry { Buff = "buffDrugAtomJunkies", DurationSeconds = 3600,
                    DisplayName = "Atom Junkies",
                    Description = "Vanilla drug — periodic HP regen",
                    Icon = "ui_game_symbol_medical" },
            },
        };

        public Dictionary<string, List<CVarEntry>> GroupCVars =
            new Dictionary<string, List<CVarEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["vip"] = new List<CVarEntry>
            {
                new CVarEntry { Name = "$treatedCommando", Value = 0.5f },
            },
        };
    }

    public class State
    {
        public HashSet<string> Toasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // playerId → set of buff names the player has toggled OFF. Persisted.
        public Dictionary<string, HashSet<string>> UserDisabled =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _tick;

    // Alphabetically-sorted unique list of every buff name that appears in
    // any group. Index in this list is the GLOBAL slot id used in the
    // Labels table (donor_buff_name_N). Computed at OnLoad so it's stable
    // for the whole session.
    private List<string> _allBuffs = new List<string>();
    private Dictionary<string, BuffEntry> _buffMeta = new Dictionary<string, BuffEntry>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("sessions");

        // Re-wrap the deserialized dictionaries with case-insensitive comparers.
        // The Config field initializers use OrdinalIgnoreCase, but JSON
        // deserialization always builds a fresh case-sensitive Dictionary —
        // so an admin storing the player's group as "VIP" wouldn't match the
        // GroupBuffs key "vip" without this rebuild. Affects both GroupBuffs
        // and GroupCVars (same lookup pattern in EligibleBuffs / ApplyForClient).
        if (_cfg.GroupBuffs != null)
            _cfg.GroupBuffs = new Dictionary<string, List<BuffEntry>>(
                _cfg.GroupBuffs, StringComparer.OrdinalIgnoreCase);
        if (_cfg.GroupCVars != null)
            _cfg.GroupCVars = new Dictionary<string, List<CVarEntry>>(
                _cfg.GroupCVars, StringComparer.OrdinalIgnoreCase);

        // Re-apply on a timer so long buffs don't silently drop off.
        int interval = Math.Max(60, _cfg.ReapplyIntervalSeconds);
        _tick = Scheduler.Every(interval, () => ReapplyAll(), name: "DonorPerks.reapply");

        // Build the global buff table + register labels for the UI.
        BuildBuffCatalog();

        // Register launcher entry. Gate is dynamic ("do you have any available
        // buffs?") so we register without perm and handle the empty case at open time.
        Styx.Ui.Menu.Register(this, "My Buffs", OpenMenuFor, permission: "styx.donor.use");

        Styx.Ui.Ephemeral.Register(
            "styx.donor.open", "styx.donor.sel", "styx.donor.count", "styx.donor.desc_id");
        for (int k = 0; k < MaxUiRows; k++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.donor.row" + k + "_id",
                "styx.donor.row" + k + "_status");
        }

        StyxCore.Commands.Register("donor", "Check or manage donor perks — /donor [status|reapply]", (ctx, args) =>
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":  ShowStatus(ctx); break;
                case "reapply":
                    if (!RequireAdmin(ctx)) return;
                    int n = ReapplyAll();
                    ctx.Reply("[00ff66]Reapplied perks to " + n + " player(s).[-]");
                    break;
                default:
                    ctx.Reply("Usage: /donor status | /donor reapply (admin)");
                    break;
            }
        });

        StyxCore.Perms.RegisterKnown(PermAdmin,
            "Run /donor reapply and view all donor states", Name);
        StyxCore.Perms.RegisterKnown("styx.donor.use",
            "See the My Buffs launcher entry (toggle donor buffs on/off)", Name);

        Log.Out("[DonorPerks] Loaded v0.2.0 — {0} group(s), {1} unique buffs, reapply every {2}s",
            _cfg.GroupBuffs.Count, _allBuffs.Count, interval);
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
        foreach (var group in _cfg.GroupBuffs.Values)
        {
            if (group == null) continue;
            foreach (var be in group)
            {
                if (string.IsNullOrEmpty(be?.Buff)) continue;
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
            Styx.Ui.Labels.Register(this, "donor_buff_name_" + i, display);
            Styx.Ui.Labels.Register(this, "donor_buff_desc_" + i, meta.Description ?? "");
            Styx.Ui.Labels.Register(this, "donor_buff_icon_" + i, icon);
        }
        for (int i = _allBuffs.Count; i < MaxUiRows; i++)
        {
            Styx.Ui.Labels.Register(this, "donor_buff_name_" + i, "(no buff)");
            Styx.Ui.Labels.Register(this, "donor_buff_desc_" + i, "");
            Styx.Ui.Labels.Register(this, "donor_buff_icon_" + i, "");
        }
    }

    // ---- spawn hook ----

    public void OnPlayerSpawned(ClientInfo ci, RespawnType reason)
    {
        if (ci != null) ApplyForClient(ci, toastIfFirst: true);
    }

    // ---- core apply ----

    /// <summary>
    /// Returns the list of buff names this player is eligible for (from every
    /// donor group they belong to). Duplicates removed. Filtered by
    /// UserDisabled unless <paramref name="includeDisabled"/> is true.
    /// </summary>
    private List<string> EligibleBuffs(string platformId, bool includeDisabled = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(platformId)) return result;

        var groups = StyxCore.Perms.GetPlayerGroups(platformId);
        _state.Value.UserDisabled.TryGetValue(platformId, out var disabled);

        foreach (var g in groups)
        {
            if (!_cfg.GroupBuffs.TryGetValue(g, out var buffs) || buffs == null) continue;
            foreach (var be in buffs)
            {
                if (string.IsNullOrEmpty(be?.Buff)) continue;
                if (!seen.Add(be.Buff)) continue;
                if (!includeDisabled && disabled != null && disabled.Contains(be.Buff)) continue;
                result.Add(be.Buff);
            }
        }
        // Preserve global sort order so UI rows are stable.
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private int ApplyForClient(ClientInfo ci, bool toastIfFirst)
    {
        if (ci == null) return 0;
        var player = StyxCore.Player.FindByEntityId(ci.entityId);
        if (player == null) return 0;

        string platformId = ci.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(platformId)) return 0;

        var eligible = EligibleBuffs(platformId); // disabled-filtered
        var applied = new List<string>();
        foreach (var name in eligible)
        {
            if (!_buffMeta.TryGetValue(name, out var be)) continue;
            if (StyxCore.Player.ApplyBuff(player, be.Buff, be.DurationSeconds))
                applied.Add(be.Buff);
        }

        // CVars still apply per group (not individually toggleable in v0.2).
        var groups = StyxCore.Perms.GetPlayerGroups(platformId);
        foreach (var g in groups)
        {
            if (!_cfg.GroupCVars.TryGetValue(g, out var cvars)) continue;
            foreach (var cv in cvars)
            {
                if (string.IsNullOrEmpty(cv?.Name)) continue;
                StyxCore.Player.SetCVar(player, cv.Name, cv.Value);
                applied.Add(cv.Name + "=" + cv.Value);
            }
        }

        if (applied.Count > 0)
        {
            Log.Out("[DonorPerks] {0}: applied {1} ({2})",
                ci.playerName, applied.Count, string.Join(", ", applied));

            if (toastIfFirst && !_state.Value.Toasted.Contains(platformId))
            {
                _state.Value.Toasted.Add(platformId);
                _state.Save();
                Styx.Ui.Toast(player,
                    "Donor perks active — /m → My Buffs to toggle",
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

        var eligible = EligibleBuffs(pid, includeDisabled: true);
        if (eligible.Count == 0)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] You don't have any donor buffs available.[-]");
            return;
        }

        _uiOpenFor.Add(p.entityId);
        Styx.Ui.SetVar(p, "styx.donor.open", 1f);
        Styx.Ui.SetVar(p, "styx.donor.sel", 0f);
        int shown = Math.Min(eligible.Count, MaxUiRows);
        Styx.Ui.SetVar(p, "styx.donor.count", shown);

        var disabled = GetOrCreateDisabledSet(pid, save: false);
        int firstId = 0;
        for (int k = 0; k < MaxUiRows; k++)
        {
            int id = 0; int status = 1;
            if (k < shown)
            {
                var buff = eligible[k];
                id = _allBuffs.IndexOf(buff);
                if (id < 0) id = 0;
                status = disabled.Contains(buff) ? 0 : 1;
                if (k == 0) firstId = id;
            }
            Styx.Ui.SetVar(p, "styx.donor.row" + k + "_id", id);
            Styx.Ui.SetVar(p, "styx.donor.row" + k + "_status", status);
        }
        // Description tracks the currently-selected row's global id.
        Styx.Ui.SetVar(p, "styx.donor.desc_id", firstId);

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0, eligible);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.donor.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_uiOpenFor.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.donor.open") != 1) return;

        var pid = StyxCore.Player.PlatformIdOf(p);
        var eligible = EligibleBuffs(pid, includeDisabled: true);
        int count = eligible.Count;
        if (count == 0) { CloseFor(p); return; }

        int sel = (int)p.Buffs.GetCustomVar("styx.donor.sel");

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.donor.sel", sel);
                Styx.Ui.SetVar(p, "styx.donor.desc_id", _allBuffs.IndexOf(eligible[sel]));
                WhisperRow(p, sel, eligible);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.donor.sel", sel);
                Styx.Ui.SetVar(p, "styx.donor.desc_id", _allBuffs.IndexOf(eligible[sel]));
                WhisperRow(p, sel, eligible);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                ToggleBuff(p, pid, eligible[sel], sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "DonorPerks.BackToLauncher");
                break;
        }
    }

    private void ToggleBuff(EntityPlayer p, string pid, string buffName, int rowSel)
    {
        if (!_buffMeta.TryGetValue(buffName, out var be))
        {
            Styx.Server.Whisper(p, "[ff6666][My Buffs] Unknown buff '" + buffName + "'[-]");
            return;
        }

        if (!be.UserToggleable)
        {
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] '" + DisplayOf(be) + "' is admin-locked (not user-toggleable).[-]");
            return;
        }

        var disabled = GetOrCreateDisabledSet(pid, save: true);
        bool wasDisabled = disabled.Contains(buffName);

        if (wasDisabled)
        {
            // Toggle ON: remove from disabled set, apply the buff immediately.
            disabled.Remove(buffName);
            StyxCore.Player.ApplyBuff(p, buffName, be.DurationSeconds);
            Styx.Ui.SetVar(p, "styx.donor.row" + rowSel + "_status", 1f);
            Styx.Server.Whisper(p, "[00ff66][My Buffs] " + DisplayOf(be) + ": ACTIVE[-]");
        }
        else
        {
            // Toggle OFF: add to disabled set, remove the buff.
            disabled.Add(buffName);
            StyxCore.Player.RemoveBuff(p, buffName);
            Styx.Ui.SetVar(p, "styx.donor.row" + rowSel + "_status", 0f);
            Styx.Server.Whisper(p, "[ffaa00][My Buffs] " + DisplayOf(be) + ": DISABLED (won't reapply)[-]");
        }
        _state.Save();
    }

    private void WhisperRow(EntityPlayer p, int idx, List<string> eligible)
    {
        if (idx < 0 || idx >= eligible.Count) return;
        var buffName = eligible[idx];
        if (!_buffMeta.TryGetValue(buffName, out var meta)) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        bool isDisabled = false;
        if (_state.Value.UserDisabled.TryGetValue(pid ?? "", out var set))
            isDisabled = set.Contains(buffName);

        string status = isDisabled
            ? "[ffaa00]DISABLED (won't reapply)[-]"
            : "[00ff66]ACTIVE[-]";

        Styx.Server.Whisper(p, string.Format(
            "[ccddff][My Buffs] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  {4}",
            idx + 1, eligible.Count, DisplayOf(meta), meta.Description ?? "", status));
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

        var eligible = EligibleBuffs(id, includeDisabled: true);
        var disabledSet = _state.Value.UserDisabled.TryGetValue(id, out var s) ? s : null;

        ctx.Reply("Donor buffs available (" + eligible.Count + "):");
        foreach (var name in eligible)
        {
            var meta = _buffMeta.TryGetValue(name, out var m) ? m : null;
            bool off = disabledSet != null && disabledSet.Contains(name);
            string status = off ? "[ffaa00]DISABLED[-]" : "[00ff66]active[-]";
            ctx.Reply("  " + DisplayOf(meta) + " (" + name + ") " + status);
        }
        ctx.Reply("[888888]Use /m → My Buffs to toggle.[-]");
    }
}
