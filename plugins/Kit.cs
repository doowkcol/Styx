// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// Kit plugin — chat AND in-game UI.
//
// Chat:
//   /kit                    — list available kits
//   /kit info <name>        — preview a kit's contents
//   /kit <name>             — claim it
//
// UI (/m → "Kits"):
//   jump/crouch navigate, LMB claim, RMB back to launcher.
//
// PERMISSION MODEL (v0.3):
//   Old model auto-generated one perm per kit (`styx.kit.<name>`), so a
//   "vip" kit and a "vipMedkit" kit needed two separate perm grants.
//   New model:
//     - Config.KitPerms is the registry of "perm tiers" admins can assign
//       to kits. Each perm registers with PermEditor for one-click grant.
//     - Each KitDef.Perm names one of those perms (or any custom perm).
//     - Multiple kits can share a perm: granting `styx.kit.vip` unlocks
//       every kit with `Perm: "styx.kit.vip"` at once.
//     - Empty Perm = no permission required (open to anyone with the
//       launcher entry, gated only by `styx.kit.use`).
//
//   Migration: configs from v0.2 (no Perm field, RequirePermission=true)
//   get auto-mapped to "styx.kit.basic" on load. Set Perm explicitly to
//   override.
//
// PER-KIT MECHANICS:
//   - Kits in configs/Kit.json (live-reloaded on save).
//   - Cooldowns persisted via StyxCore Data API → data/Kit.cooldowns.json.
//   - Items delivered via StyxCore.Player.GiveBackpack — single lootable
//     bag at the player's feet.
//   - Icons (vanilla ItemIconAtlas sprite name) shown 40×40 in UI.
//
// LABELS:
//   Kit names + descriptions + icons are static-label-registered via
//   Styx.Ui.Labels (baked into Mods/StyxRuntime/Config/Localization.txt at
//   server shutdown). Adding/removing a kit takes effect on the second
//   server restart (first to bake labels, second to load them).

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Data;
using Styx.Plugins;

[Info("Kit", "Doowkcol", "0.3.0")]
public class Kit : StyxPlugin
{
    public class KitItem
    {
        public string Item;          // e.g. "drinkJarBoiledWater", "meleeToolStoneAxe"
        public int Count = 1;
        public int Quality = 1;      // 1-6 for tiered items; ignored by stackables
    }

    public class KitDef
    {
        public string Description = "";
        public int CooldownSeconds = 0;

        /// <summary>Permission required to claim this kit. Reference any
        /// perm in <see cref="Config.KitPerms"/> for one-click admin grant
        /// via PermEditor. Empty string = no perm required.</summary>
        public string Perm = "styx.kit.basic";

        /// <summary>
        /// Vanilla sprite name from ItemIconAtlas, shown in the UI picker.
        /// Typically the kit's marquee item (e.g. "gunMGT3M60" for a gun kit).
        /// If null/empty, falls back to Items[0].Item.
        /// </summary>
        public string Icon = "";

        public List<KitItem> Items = new List<KitItem>();
    }

    public class Config
    {
        /// <summary>Perm registry — admins assign kits to one of these via
        /// the KitDef.Perm field. Each perm here is registered with
        /// PermEditor on load so it's visible in the UI grant editor.
        /// Add custom perms freely; no other code path needs to know about
        /// them.</summary>
        public Dictionary<string, string> KitPerms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["styx.kit.basic"]  = "Standard kits available to everyone",
            ["styx.kit.vip"]    = "VIP-tier kit access (donor / supporter)",
            ["styx.kit.master"] = "Admin-tier kit access (everything)",
        };

        public Dictionary<string, KitDef> Kits = new Dictionary<string, KitDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["starter"] = new KitDef
            {
                Description = "Basic survival gear",
                CooldownSeconds = 0,
                Perm = "styx.kit.basic",
                Items = new List<KitItem>
                {
                    new KitItem { Item = "drinkJarBoiledWater", Count = 4 },
                    new KitItem { Item = "foodCanChili",         Count = 4 },
                    new KitItem { Item = "meleeToolRepairT0StoneAxe", Count = 1, Quality = 2 },
                    new KitItem { Item = "meleeToolTorch",       Count = 2 },
                }
            },
            ["daily"] = new KitDef
            {
                Description = "Daily loot (24h cooldown)",
                CooldownSeconds = 86400,
                Perm = "styx.kit.basic",
                Items = new List<KitItem>
                {
                    new KitItem { Item = "resourceScrapIron", Count = 20 },
                    new KitItem { Item = "resourceDuctTape",  Count = 5 },
                }
            },
            ["deployables"] = new KitDef
            {
                Description = "Crafting deployables — forge, workbench, chem station, etc.",
                CooldownSeconds = 86400 * 7,  // weekly
                Perm = "styx.kit.basic",
                Icon = "workbench",
                Items = new List<KitItem>
                {
                    // Stations
                    new KitItem { Item = "forge",            Count = 1 },
                    new KitItem { Item = "workbench",        Count = 1 },
                    new KitItem { Item = "chemistryStation", Count = 1 },
                    new KitItem { Item = "cementMixer",      Count = 1 },
                    // Cooking
                    new KitItem { Item = "campfire",         Count = 1 },
                    new KitItem { Item = "toolCookingGrill", Count = 1 },
                    new KitItem { Item = "toolCookingPot",   Count = 1 },
                    // Survival
                    new KitItem { Item = "bedroll",                Count = 1 },
                    new KitItem { Item = "cntWoodWritableCrate",   Count = 2 },
                }
            },
            ["vip"] = new KitDef
            {
                Description = "VIP loadout — T3 weapons, full armour, ammo & meds",
                CooldownSeconds = 0,
                Perm = "styx.kit.vip",
                Items = new List<KitItem>
                {
                    // Weapons — tier 3, quality 6
                    new KitItem { Item = "gunMGT3M60",                Count = 1, Quality = 6 },
                    new KitItem { Item = "gunHandgunT3DesertVulture", Count = 1, Quality = 6 },
                    // Ammo
                    new KitItem { Item = "ammo762mmBulletBall",      Count = 200 },
                    new KitItem { Item = "ammo44MagnumBulletBall",   Count = 50 },
                    // Full Commando armour set, quality 6
                    new KitItem { Item = "armorCommandoHelmet",      Count = 1, Quality = 6 },
                    new KitItem { Item = "armorCommandoOutfit",      Count = 1, Quality = 6 },
                    new KitItem { Item = "armorCommandoGloves",      Count = 1, Quality = 6 },
                    new KitItem { Item = "armorCommandoBoots",       Count = 1, Quality = 6 },
                    // Meds
                    new KitItem { Item = "medicalFirstAidKit",       Count = 3 },
                    new KitItem { Item = "drugPainkillers",          Count = 3 },
                }
            },
        };

        public string ColourReady = "[00ff66]";
        public string ColourCooldown = "[ffaa00]";
        public string ColourError = "[ff6666]";
    }

    // playerId -> kitName -> next-claim unix seconds
    private class State
    {
        public Dictionary<string, Dictionary<string, long>> Cooldowns =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
    }

    private Config _cfg;
    private DataStore<State> _state;

    // UI state — stable sorted kit list + per-player "menu-open" set.
    private List<string> _sortedKits = new List<string>();
    private readonly HashSet<int> _openFor = new HashSet<int>();
    private const int MaxUiRows = 8;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("cooldowns");

        StyxCore.Commands.Register("kit", "List or claim kits — /kit [name] | /kit info <name>", (ctx, args) =>
        {
            if (args.Length == 0) { ShowList(ctx); return; }
            if (args.Length >= 2 && args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                ShowContents(ctx, args[1]);
                return;
            }
            Claim(ctx, args[0]);
        });

        // Stable sort for deterministic slot IDs across restarts.
        _sortedKits = _cfg.Kits.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        // Register name + description + icon labels. On next boot the engine
        // loads them from Mods/StyxRuntime/Config/Localization.txt (written
        // at framework shutdown) and XUi bindings resolve to real strings.
        for (int i = 0; i < _sortedKits.Count && i < MaxUiRows; i++)
        {
            var name = _sortedKits[i];
            var def = _cfg.Kits[name];
            Styx.Ui.Labels.Register(this, "kit_name_" + i, name);
            Styx.Ui.Labels.Register(this, "kit_desc_" + i, def.Description ?? "");
            string icon = !string.IsNullOrEmpty(def.Icon)
                ? def.Icon
                : (def.Items != null && def.Items.Count > 0 ? def.Items[0]?.Item : "") ?? "";
            Styx.Ui.Labels.Register(this, "kit_icon_" + i, icon);
        }
        // Pad remaining UI slots so stale cvars never resolve to a missing key.
        for (int i = _sortedKits.Count; i < MaxUiRows; i++)
        {
            Styx.Ui.Labels.Register(this, "kit_name_" + i, "(no kit)");
            Styx.Ui.Labels.Register(this, "kit_desc_" + i, "");
            Styx.Ui.Labels.Register(this, "kit_icon_" + i, "");
        }

        // Show up in /m — gated on the basic "use" perm so no-perm players
        // don't see kits at all (per-kit perms still gate each claim).
        Styx.Ui.Menu.Register(this, "Kits", OpenFor, permission: "styx.kit.use");

        // UI open-state clears on every spawn — otherwise a server restart
        // while a player had /m → Kits open would reopen the panel for them.
        Styx.Ui.Ephemeral.Register("styx.kits.open", "styx.kits.sel", "styx.kits.count");

        // Register "use" + every cfg-listed kit perm. Per-kit perms (one per
        // kit) are NO LONGER registered — the v0.3 model assigns kits to a
        // shared perm. Custom Perm strings on KitDefs that aren't in
        // Config.KitPerms are also registered (so `Perm: "my.special.perm"`
        // shows up in PermEditor too).
        StyxCore.Perms.RegisterKnown("styx.kit.use",
            "See the Kits launcher entry (per-kit perms still gate each claim)", Name);
        foreach (var kv in _cfg.KitPerms)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            StyxCore.Perms.RegisterKnown(kv.Key, kv.Value ?? "", Name);
        }
        // Sweep up any kit-referenced perms that weren't pre-listed in KitPerms.
        var declared = new HashSet<string>(_cfg.KitPerms.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var kit in _cfg.Kits.Values)
        {
            if (string.IsNullOrEmpty(kit?.Perm)) continue;
            if (declared.Contains(kit.Perm)) continue;
            StyxCore.Perms.RegisterKnown(kit.Perm,
                "Kit perm (auto-registered from KitDef.Perm — add to Config.KitPerms for a description)",
                Name);
            declared.Add(kit.Perm);
        }

        Log.Out("[Kit] Loaded v0.3.0 — {0} kit(s); UI rows {1}; {2} kit perm(s) registered.",
            _cfg.Kits.Count, Math.Min(_sortedKits.Count, MaxUiRows), declared.Count);
    }

    public override void OnUnload()
    {
        // Clean up any open UI sessions.
        foreach (var eid in _openFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.kits.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _openFor.Clear();

        StyxCore.Perms.UnregisterKnownByOwner(Name);
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);

        // Data.FlushAll() runs automatically on unload; no explicit save needed.
    }

    // ---- UI: picker window driver ----

    /// <summary>
    /// Entry point from /m → "Kits". Opens the styxKits window for this player,
    /// populates row IDs, takes an input claim.
    /// </summary>
    private void OpenFor(EntityPlayer p)
    {
        if (p == null || _sortedKits.Count == 0) return;

        _openFor.Add(p.entityId);
        Styx.Ui.SetVar(p, "styx.kits.open", 1f);
        Styx.Ui.SetVar(p, "styx.kits.sel", 0f);

        int shown = Math.Min(_sortedKits.Count, MaxUiRows);
        Styx.Ui.SetVar(p, "styx.kits.count", shown);
        for (int k = 0; k < MaxUiRows; k++)
            Styx.Ui.SetVar(p, "styx.kits.row" + k + "_id", k < shown ? k : 0);
        // Description cvar tracks the highlighted row's id.
        Styx.Ui.SetVar(p, "styx.kits.desc_id", 0f);

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.kits.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _openFor.Remove(p.entityId);
    }

    // Hook-bus. Fires for every player we hold an input claim on.
    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_openFor.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.kits.open") != 1) return;

        int sel   = (int)p.Buffs.GetCustomVar("styx.kits.sel");
        int count = (int)p.Buffs.GetCustomVar("styx.kits.count");
        if (count <= 0) return;

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
            {
                int next = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.kits.sel", next);
                Styx.Ui.SetVar(p, "styx.kits.desc_id", next);
                WhisperRow(p, next);
                break;
            }
            case Styx.Ui.StyxInputKind.Crouch:
            {
                int prev = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.kits.sel", prev);
                Styx.Ui.SetVar(p, "styx.kits.desc_id", prev);
                WhisperRow(p, prev);
                break;
            }
            case Styx.Ui.StyxInputKind.PrimaryAction:
                TryClaimForPlayer(p, _sortedKits[sel]);
                CloseFor(p);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // RMB = back to /m. Close our own panel, then reopen launcher
                // (deferred one scheduler tick so the current input dispatch
                // completes before the launcher opens — matches the launcher's
                // own deferred-OnSelect pattern to avoid event double-dip).
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "Kit.BackToLauncher");
                break;
        }
    }

    private void WhisperRow(EntityPlayer p, int idx)
    {
        if (idx < 0 || idx >= _sortedKits.Count) return;
        var name = _sortedKits[idx];
        var def = _cfg.Kits[name];
        var pid = StyxCore.Player.PlatformIdOf(p) ?? "";

        string status;
        if (!HasKitPerm(pid, def))
            status = "[ff6666]locked — need " + (def.Perm ?? "") + "[-]";
        else
        {
            long remaining = CooldownRemaining(pid, name);
            status = remaining <= 0
                ? "[00ff66]ready[-]"
                : "[ffaa00]cooldown " + FormatDuration(remaining) + "[-]";
        }

        Styx.Server.Whisper(p, string.Format(
            "[ccddff][Kits] {0}/{1}:[-] [ffffdd]{2}[-] — {3}  {4}",
            idx + 1, _sortedKits.Count, name, def.Description ?? "", status));
    }

    /// <summary>
    /// UI-side claim. Same rules as the chat <c>/kit &lt;name&gt;</c> path —
    /// perm check, cooldown, delivery via GiveBackpack. Feedback goes to chat
    /// (whisper) since we don't have a CommandContext here.
    /// </summary>
    private void TryClaimForPlayer(EntityPlayer p, string name)
    {
        if (p == null || string.IsNullOrEmpty(name)) return;
        if (!_cfg.Kits.TryGetValue(name, out var def))
        {
            Styx.Server.Whisper(p, _cfg.ColourError + "No kit named '" + name + "'.[-]");
            return;
        }

        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid))
        {
            Styx.Server.Whisper(p, _cfg.ColourError + "Could not resolve player id.[-]");
            return;
        }

        if (!HasKitPerm(pid, def))
        {
            Styx.Server.Whisper(p, _cfg.ColourError + "No permission for '" + name + "' (need " + (def.Perm ?? "") + ").[-]");
            return;
        }

        long remaining = CooldownRemaining(pid, name);
        if (remaining > 0)
        {
            Styx.Server.Whisper(p, _cfg.ColourCooldown + "'" + name + "' on cooldown for " + FormatDuration(remaining) + "[-]");
            return;
        }

        var entries = new List<(string item, int count, int quality)>();
        foreach (var ki in def.Items)
        {
            if (string.IsNullOrEmpty(ki.Item)) continue;
            int q = Math.Max(1, Math.Min(6, ki.Quality));
            entries.Add((ki.Item, ki.Count, q));
        }
        if (entries.Count == 0)
        {
            Styx.Server.Whisper(p, _cfg.ColourError + "Kit '" + name + "' has no valid items.[-]");
            return;
        }

        bool ok = StyxCore.Player.GiveBackpack(p, entries);
        if (!ok)
        {
            Styx.Server.Whisper(p, _cfg.ColourError + "Kit delivery failed — check server log.[-]");
            return;
        }

        if (def.CooldownSeconds > 0)
        {
            SetCooldown(pid, name, UnixNow() + def.CooldownSeconds);
            _state.Save();
        }

        Styx.Server.Whisper(p, _cfg.ColourReady + "Kit '" + name + "' delivered (" + entries.Count + " stack(s)) — look behind you.[-]");
        Styx.Ui.Toast(p, "Kit '" + name + "' delivered.", Styx.Ui.Sounds.ChallengeRedeem);
        Log.Out("[Kit] {0} claimed '{1}' via UI ({2} stacks)", p.EntityName ?? "?", name, entries.Count);

        StyxCore.Hooks?.Fire("OnKitRedeemed", StyxCore.Player.ClientOf(p), name, entries.Count);
    }

    // ---------- chat-driven list / claim / info ----------

    private void ShowList(Styx.Commands.CommandContext ctx)
    {
        if (_cfg.Kits.Count == 0) { ctx.Reply("No kits configured."); return; }
        var playerId = ctx.Client?.PlatformId?.CombinedString ?? "";
        ctx.Reply("Available kits:");
        foreach (var kv in _cfg.Kits)
        {
            string name = kv.Key;
            var def = kv.Value;
            string status;
            if (!HasKitPerm(playerId, def))
                status = _cfg.ColourError + "(no permission)[-]";
            else
            {
                long remaining = CooldownRemaining(playerId, name);
                if (remaining <= 0) status = _cfg.ColourReady + "ready[-]";
                else status = _cfg.ColourCooldown + "cooldown " + FormatDuration(remaining) + "[-]";
            }
            ctx.Reply("  /kit " + name + " — " + def.Description + " " + status);
        }
        ctx.Reply("[888888]Use /kit info <name> to preview contents before claiming.[-]");
    }

    private void Claim(Styx.Commands.CommandContext ctx, string name)
    {
        if (!_cfg.Kits.TryGetValue(name, out var def))
        {
            ctx.Reply(_cfg.ColourError + "No kit named '" + name + "'.[-] Try /kit to see what's available.");
            return;
        }

        var client = ctx.Client;
        var playerId = client?.PlatformId?.CombinedString ?? "";
        if (string.IsNullOrEmpty(playerId)) { ctx.Reply("Could not resolve your player id."); return; }

        if (!HasKitPerm(playerId, def))
        {
            ctx.Reply(_cfg.ColourError + "You don't have permission for this kit.[-] (need " + (def.Perm ?? "") + ")");
            return;
        }

        long remaining = CooldownRemaining(playerId, name);
        if (remaining > 0)
        {
            ctx.Reply(_cfg.ColourCooldown + "On cooldown for " + FormatDuration(remaining) + "[-]");
            return;
        }

        var player = StyxCore.Player.FindByEntityId(client.entityId);
        if (player == null)
        {
            ctx.Reply(_cfg.ColourError + "Couldn't locate your player entity.[-]");
            return;
        }

        var entries = new List<(string item, int count, int quality)>();
        foreach (var ki in def.Items)
        {
            if (string.IsNullOrEmpty(ki.Item)) continue;
            int q = Math.Max(1, Math.Min(6, ki.Quality));
            entries.Add((ki.Item, ki.Count, q));
        }
        int delivered = entries.Count;
        bool ok = StyxCore.Player.GiveBackpack(player, entries);
        if (!ok)
        {
            ctx.Reply(_cfg.ColourError + "Kit delivery failed. Check server console.[-]");
            return;
        }

        if (delivered == 0)
        {
            ctx.Reply(_cfg.ColourError + "Kit '" + name + "' has no valid items.[-]");
            return;
        }

        if (def.CooldownSeconds > 0)
        {
            SetCooldown(playerId, name, UnixNow() + def.CooldownSeconds);
            _state.Save();
        }

        ctx.Reply(_cfg.ColourReady + "Kit '" + name + "' delivered (" + delivered + " item stack(s)).[-]");
        ReplyContents(ctx, def);
        Styx.Ui.Toast(player, "Kit '" + name + "' delivered — check behind you.", Styx.Ui.Sounds.ChallengeRedeem);
        Log.Out("[Kit] {0} claimed '{1}' ({2} stacks)", client.playerName, name, delivered);

        // Fire a custom hook so other plugins can react (stats, Discord, etc.)
        StyxCore.Hooks?.Fire("OnKitRedeemed", client, name, delivered);
    }

    // ---------- info / preview ----------

    private void ShowContents(Styx.Commands.CommandContext ctx, string name)
    {
        if (!_cfg.Kits.TryGetValue(name, out var def))
        {
            ctx.Reply(_cfg.ColourError + "No kit named '" + name + "'.[-] Try /kit for the list.");
            return;
        }

        var playerId = ctx.Client?.PlatformId?.CombinedString ?? "";
        string status;
        if (!HasKitPerm(playerId, def))
            status = _cfg.ColourError + "(no permission)[-]";
        else
        {
            long remaining = CooldownRemaining(playerId, name);
            status = remaining <= 0
                ? _cfg.ColourReady + "ready — /kit " + name + " to claim[-]"
                : _cfg.ColourCooldown + "cooldown " + FormatDuration(remaining) + "[-]";
        }

        ctx.Reply("Kit '" + name + "' — " + def.Description + " " + status);
        ReplyContents(ctx, def);
    }

    private void ReplyContents(Styx.Commands.CommandContext ctx, KitDef def)
    {
        ctx.Reply("Contents:");
        foreach (var ki in def.Items)
        {
            if (string.IsNullOrEmpty(ki.Item)) continue;
            var ic = ItemClass.GetItemClass(ki.Item, _caseInsensitive: true);
            string pretty = ic?.GetLocalizedItemName();
            if (string.IsNullOrEmpty(pretty)) pretty = ki.Item;

            bool showQuality = ic != null && new ItemValue(ic.Id).HasQuality;
            string qTag = showQuality ? " [ffaa00](Q" + Math.Max(1, Math.Min(6, ki.Quality)) + ")[-]" : "";
            ctx.Reply("  [-] " + pretty + " [ffffff]×" + ki.Count + "[-]" + qTag);
        }
    }

    // ---------- helpers ----------

    /// <summary>Permission gate. Empty Perm = always allowed; otherwise the
    /// player must hold the named perm (any group it's granted to).</summary>
    private bool HasKitPerm(string playerId, KitDef def)
    {
        if (def == null) return false;
        if (string.IsNullOrEmpty(def.Perm)) return true;
        if (string.IsNullOrEmpty(playerId)) return false;
        return StyxCore.Perms.HasPermission(playerId, def.Perm);
    }

    private long CooldownRemaining(string playerId, string kitName)
    {
        if (!_state.Value.Cooldowns.TryGetValue(playerId, out var kits)) return 0;
        if (!kits.TryGetValue(kitName, out var next)) return 0;
        long now = UnixNow();
        return next > now ? next - now : 0;
    }

    private void SetCooldown(string playerId, string kitName, long nextUnix)
    {
        if (!_state.Value.Cooldowns.TryGetValue(playerId, out var kits))
        {
            kits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _state.Value.Cooldowns[playerId] = kits;
        }
        kits[kitName] = nextUnix;
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return (seconds / 60) + "m " + (seconds % 60) + "s";
        long h = seconds / 3600; long m = (seconds % 3600) / 60;
        return h + "h " + m + "m";
    }
}
