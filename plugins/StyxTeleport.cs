// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// StyxTeleport — /m → Teleport picker with homes + nearest trader + last death.
//
// User commands:
//   /sethome <1-6>     — save current position to home slot
//   /delhome <1-6>     — clear home slot
//   /listhomes         — chat dump of saved homes
//   /m → Teleport      — open the picker
//
// Destinations:
//   Home 1-6 (count configurable via MaxHomes, up to 6)
//   Nearest trader (dynamic name — picked per-player at /m-open from pre-scanned
//   trader cache)
//   Last death ("return to bag" style — captured OnEntityDeath)
//
// Cooldowns + daily limits are permission-group-driven ("most generous wins"
// across a player's group memberships). Quest-warn uses a two-tap confirm.

using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("StyxTeleport", "Doowkcol", "0.1.2")]
public class StyxTeleport : StyxPlugin
{
    public override string Description => "Home / trader / last-death teleport picker (v0.6.3 framework demo)";

    private const int MaxHomeSlots = 6;     // hard cap — XUi has 6 home rows
    private const int MaxUiRows = 8;
    private const int DestHome0 = 0;        // dest ids 0..5 are homes
    private const int DestTrader = 6;
    private const int DestDeath = 7;

    // row{K}_kind values consumed by XUi window styxTeleport
    private const int KindHome = 0;
    private const int KindTrader = 1;
    private const int KindDeath = 2;
    private const int KindEmpty = 3;

    public class CooldownRule
    {
        public int PerUseSeconds = 600;
        public int DailyLimit = 5;          // -1 = unlimited
    }

    public class Config
    {
        public int MaxHomes = 3;
        public bool TraderEnabled = true;
        public bool LastDeathEnabled = true;
        public bool QuestWarning = true;
        public double TwoTapConfirmSeconds = 5.0;

        /// <summary>
        /// Re-apply broken leg / sprain / bleed / infection / etc. after a
        /// teleport. Vanilla `teleportplayer` does a respawn-style state
        /// cleanup that strips status buffs — without this, players could
        /// abuse teleport as a free injury reset (break leg → /m → trader →
        /// land healed). Set false to allow teleport-as-medic.
        /// </summary>
        public bool PreserveInjuriesOnTeleport = true;

        /// <summary>
        /// Permission group → cooldown rule. Player's most generous matching
        /// group wins (lowest cooldown, highest daily limit). "default" applies
        /// to every player as a floor.
        /// </summary>
        public Dictionary<string, CooldownRule> Cooldowns =
            new Dictionary<string, CooldownRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new CooldownRule { PerUseSeconds = 600, DailyLimit = 5 },
            ["vip"]     = new CooldownRule { PerUseSeconds = 60,  DailyLimit = -1 },
            ["admin"]   = new CooldownRule { PerUseSeconds = 0,   DailyLimit = -1 },
        };
    }

    // ---- Persistent per-player state ----

    public class HomePos
    {
        public float X;
        public float Y;
        public float Z;
        public long SetUnix;
    }

    public class PlayerState
    {
        public HomePos[] Homes = new HomePos[MaxHomeSlots];
        public HomePos LastDeath;
        public long NextUseUnix;                 // cooldown: won't accept teleport before this
        public long DailyResetUnix;              // when DailyCount rolls over (next midnight UTC of server)
        public int DailyCount;                   // used-today counter
    }

    public class State
    {
        public Dictionary<string, PlayerState> Players =
            new Dictionary<string, PlayerState>(StringComparer.OrdinalIgnoreCase);

        // Resolved trader names keyed by area position ("x_y_z"). Once we
        // see a real name via owningTrader.EntityName, we persist it here so
        // later boots (when the trader chunk may not be loaded at scan time)
        // can still display the real name. See ResolveTraderName().
        public Dictionary<string, string> TraderNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // ---- Trader cache ----
    //
    // Trader areas are protected zones — spawning a player INSIDE one trips the
    // engine's anti-tamper eject (drops them through the world). We cache the
    // area bounds + the area-index in TraderAreas so at teleport time we can:
    //   1. Re-resolve the live owning trader entity for its shop-floor Y.
    //   2. Compute a drop point just OUTSIDE the closest face of the area to
    //      the player's pre-teleport position — they land at the door they
    //      were nearest to and walk in.

    private class CachedTrader
    {
        public int SlotId;                       // stable index into tp_trader_* labels
        public string Name;
        public int AreaIndex;                    // index into world.TraderAreas — used to re-resolve the live owner entity
        public Vector3 AreaMin;                  // SW-bottom corner of the trader prefab area
        public Vector3 AreaSize;                 // prefab extents
        public Vector3 ScanPosition;             // best-effort centre-ish position from scan (Y unreliable)
    }

    private Config _cfg;
    private DataStore<State> _stateStore;
    private readonly List<CachedTrader> _traders = new List<CachedTrader>();
    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();
    private readonly Dictionary<int, (int destId, double tUnix)> _pendingConfirm =
        new Dictionary<int, (int, double)>();

    // Injury / status buffs preserved across teleport (see Config.PreserveInjuriesOnTeleport).
    // Vanilla's teleportplayer console command does a soft respawn that strips
    // these — without preservation, a teleport heals the player.
    private static readonly string[] InjuryBuffNames =
    {
        "buffLegBroken", "buffLegSprained", "buffLegSplinted", "buffLegCast",
        "buffArmBroken", "buffArmSprained", "buffArmSplinted", "buffArmCast",
        "buffInjuryBleeding", "buffInjuryBleedingTwo", "buffInjuryBleedingBarbedWire",
        "buffInjuryAbrasion", "buffInjuryStunned01",
        "buffInfection01", "buffInfection02", "buffInfection03", "buffInfection04",
        "buffFatigued", "buffLaceration",
    };

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _stateStore = this.Data.Store<State>("state");

        // Pre-scan traders — cache positions + names. Trader prefabs don't move,
        // so one scan at plugin load is enough for the session. Retry hook on
        // OnServerInitialized in case trader areas aren't loaded at OnLoad.
        ScanTraders();

        // Register static destination labels
        for (int i = 0; i < MaxHomeSlots; i++)
            Styx.Ui.Labels.Register(this, "tp_home_" + i, "Home " + (i + 1));
        Styx.Ui.Labels.Register(this, "tp_death", "Last death (return to bag)");

        // Register trader name labels, indexed by slot id
        for (int i = 0; i < _traders.Count; i++)
            Styx.Ui.Labels.Register(this, "tp_trader_" + i, _traders[i].Name);
        // Fallback entry so the binding resolves to SOMETHING when there are no traders yet
        Styx.Ui.Labels.Register(this, "tp_trader_fallback", "(no traders found)");

        // Launcher entry — gated on basic use perm so no-perm players don't see it.
        Styx.Ui.Menu.Register(this, "Teleport", OpenFor, permission: "styx.tp.use");
        StyxCore.Perms.RegisterKnown("styx.tp.use",
            "Open the Teleport launcher entry (homes / trader / last-death)", Name);

        // Ephemeral UI cvars. Per-row we publish kind (home/trader/death/empty),
        // id (which label index inside that kind's namespace), and status
        // (set/ready vs empty/cd) so XUi can render heterogeneous rows at the
        // same visual slots without depending on the runtime MaxHomes config.
        Styx.Ui.Ephemeral.Register(
            "styx.tp.open", "styx.tp.sel", "styx.tp.count");
        for (int k = 0; k < MaxUiRows; k++)
        {
            Styx.Ui.Ephemeral.Register(
                "styx.tp.row" + k + "_status",
                "styx.tp.row" + k + "_kind",
                "styx.tp.row" + k + "_id");
        }

        // Chat commands
        StyxCore.Commands.Register("sethome", "Set a home teleport slot — /sethome <1-" + _cfg.MaxHomes + ">",
            (ctx, args) => CmdSetHome(ctx, args));
        StyxCore.Commands.Register("delhome", "Clear a home slot — /delhome <1-" + _cfg.MaxHomes + ">",
            (ctx, args) => CmdDelHome(ctx, args));
        StyxCore.Commands.Register("listhomes", "Dump your saved homes to chat",
            (ctx, args) => CmdListHomes(ctx, args));

        // Admin: force a trader re-scan + label re-register. Useful after
        // visiting all traders (chunks loaded) to pick up real names that
        // weren't resolvable at boot. Names appear in the UI after the next
        // server restart due to the label-bake cycle.
        StyxCore.Commands.Register("trescan", "Re-scan trader names + re-register labels (admin)", (ctx, args) =>
        {
            var pid = ctx.Client?.PlatformId?.CombinedString;
            if (!string.IsNullOrEmpty(pid) && !StyxCore.Perms.HasPermission(pid, "styx.admin"))
            {
                ctx.Reply("[ff6666]styx.admin perm required.[-]");
                return;
            }
            int afterFallbacks = RescanAndRelabel();
            ctx.Reply(string.Format(
                "[ccddff]StyxTeleport rescan:[-] {0} trader(s), {1} resolved, {2} still fallback. " +
                "Names show in UI after next server restart (label bake cycle).",
                _traders.Count, _traders.Count - afterFallbacks, afterFallbacks));
            for (int i = 0; i < _traders.Count; i++)
            {
                var t = _traders[i];
                string posKey = PositionKey(new Vector3i((int)t.AreaMin.x, (int)t.AreaMin.y, (int)t.AreaMin.z));
                ctx.Reply(string.Format("  [{0}] {1}  ({2})", i + 1, t.Name, posKey));
            }
        });

        // Admin: manual override for trader names. When automatic resolution
        // fails (e.g. trader chunk never loads at scan time and no player
        // ever visits the area), this lets the admin set a name directly.
        // Persists to disk and re-registers the label. Names appear after
        // the next server restart due to the label bake cycle.
        StyxCore.Commands.Register("tname", "Manually name a trader — /tname <slot> <name> (admin)", (ctx, args) =>
        {
            var pid = ctx.Client?.PlatformId?.CombinedString;
            if (!string.IsNullOrEmpty(pid) && !StyxCore.Perms.HasPermission(pid, "styx.admin"))
            {
                ctx.Reply("[ff6666]styx.admin perm required.[-]");
                return;
            }
            if (args.Length < 2 || !int.TryParse(args[0], out int slot1) || slot1 < 1 || slot1 > _traders.Count)
            {
                ctx.Reply("Usage: /tname <slot 1.." + _traders.Count + "> <name>");
                ctx.Reply("Current traders:");
                for (int i = 0; i < _traders.Count; i++)
                    ctx.Reply(string.Format("  [{0}] {1}", i + 1, _traders[i].Name));
                return;
            }
            int slot = slot1 - 1;
            string newName = string.Join(" ", args, 1, args.Length - 1).Trim();
            if (string.IsNullOrEmpty(newName)) { ctx.Reply("[ff6666]Name can't be empty.[-]"); return; }

            var t = _traders[slot];
            string posKey = PositionKey(new Vector3i((int)t.AreaMin.x, (int)t.AreaMin.y, (int)t.AreaMin.z));
            _stateStore.Value.TraderNames[posKey] = newName;
            _stateStore.Save();
            t.Name = newName;
            Styx.Ui.Labels.Register(this, "tp_trader_" + slot, newName);
            ctx.Reply(string.Format(
                "[00ff66]Trader [{0}] renamed to '{1}'.[-] Persisted. Visible in UI after next server restart.",
                slot1, newName));
        });

        // Periodic auto-resolve: every 30s, walk traders still on fallback
        // and try owningTrader.EntityName. If a player has been near a
        // trader since the last tick, the entity is now in memory and we
        // capture + persist the real name. Cheap (just a reflection probe
        // per fallback trader).
        Scheduler.Every(30.0, () =>
        {
            try
            {
                bool anyResolved = false;
                var world = GameManager.Instance?.World;
                if (world?.TraderAreas == null) return;
                for (int i = 0; i < _traders.Count; i++)
                {
                    if (!IsFallbackName(_traders[i].Name)) continue;
                    if (i >= world.TraderAreas.Count) continue;
                    var area = world.TraderAreas[i];
                    string newName = ResolveTraderName(area, area.Position, i, out bool isFallback);
                    if (!isFallback && newName != _traders[i].Name)
                    {
                        _traders[i].Name = newName;
                        Styx.Ui.Labels.Register(this, "tp_trader_" + i, newName);
                        Log.Out("[StyxTeleport] Auto-resolved trader [{0}] -> '{1}' (will appear after next restart)",
                            i + 1, newName);
                        anyResolved = true;
                    }
                }
                if (anyResolved && _stateStore != null) _stateStore.Save();
            }
            catch (Exception e) { Log.Warning("[StyxTeleport] auto-resolve failed: " + e.Message); }
        }, name: "StyxTeleport.trader-resolve");

        int persistedCount = _stateStore?.Value?.TraderNames?.Count ?? 0;
        Log.Out("[StyxTeleport] Loaded v0.1.2 — MaxHomes={0}, traders scanned={1}, persisted names={2}",
            _cfg.MaxHomes, _traders.Count, persistedCount);
    }

    public override void OnUnload()
    {
        foreach (var eid in _uiOpenFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p != null)
            {
                Styx.Ui.SetVar(p, "styx.tp.open", 0f);
                Styx.Ui.Input.Release(p, Name);
            }
        }
        _uiOpenFor.Clear();
        _pendingConfirm.Clear();
        Styx.Ui.Menu.UnregisterAll(this);
        Styx.Ui.Labels.UnregisterAll(this);
    }

    // Framework calls this hook. Always re-scan + re-register trader labels —
    // first scan at OnLoad usually runs before trader chunks are loaded, so
    // names fall back to "Trader N". By OnServerInitialized the world is
    // settled enough that the prefab-name path can resolve real names. Labels
    // are static-baked, so any newly-resolved names show up at NEXT restart.
    void OnServerInitialized()
    {
        RescanAndRelabel();
    }

    // Capture player death position for the "last death" destination.
    void OnEntityDeath(EntityAlive ea)
    {
        if (!(ea is EntityPlayer ep)) return;
        if (!_cfg.LastDeathEnabled) return;

        var pid = StyxCore.Player.PlatformIdOf(ep);
        if (string.IsNullOrEmpty(pid)) return;

        var ps = GetOrCreateState(pid);
        ps.LastDeath = new HomePos
        {
            X = ep.position.x,
            Y = ep.position.y,
            Z = ep.position.z,
            SetUnix = UnixNow(),
        };
        _stateStore.Save();
    }

    // ---- Trader scan ----

    private void ScanTraders()
    {
        _traders.Clear();
        var world = GameManager.Instance?.World;
        if (world?.TraderAreas == null) return;

        // TraderAreas is the engine's canonical list of known trader POIs.
        // We cache only the area bounds + slot id; the live trader entity (and
        // therefore the real shop-floor Y) is re-resolved at teleport time
        // because traders aren't always loaded at world-init.
        int fallbackCount = 0;
        for (int i = 0; i < world.TraderAreas.Count; i++)
        {
            var area = world.TraderAreas[i];
            Vector3 areaMin = new Vector3(area.Position.x, area.Position.y, area.Position.z);
            Vector3 areaSize = new Vector3(area.PrefabSize.x, area.PrefabSize.y, area.PrefabSize.z);
            Vector3 scanPos = areaMin + areaSize * 0.5f;

            string name = ResolveTraderName(area, area.Position, i, out bool isFallback);
            if (isFallback) fallbackCount++;

            _traders.Add(new CachedTrader
            {
                SlotId = i,
                Name = name,
                AreaIndex = i,
                AreaMin = areaMin,
                AreaSize = areaSize,
                ScanPosition = scanPos,
            });
        }
        Log.Out("[StyxTeleport] Trader scan found {0} ({1} with fallback names)",
            _traders.Count, fallbackCount);
    }

    /// <summary>Position key for the persisted trader-name dictionary.
    /// XZ uniquely identifies a trader POI; Y is included for paranoia
    /// against stacked traders (theoretically possible with modlets).</summary>
    private static string PositionKey(Vector3i p) => p.x + "_" + p.y + "_" + p.z;

    /// <summary>Resolve a friendly name for a trader area. Tries:
    ///   1. Live <c>owningTrader.EntityName</c> — only works if the trader's
    ///      chunk is loaded (a player has been near it recently). When this
    ///      fires, we also persist the result so later boots (chunk unloaded)
    ///      still get the real name.
    ///   2. Persisted name from previous resolution (per-position, on disk).
    ///   3. <c>"Trader N+1"</c> numeric fallback.
    /// <paramref name="isFallback"/> is true iff path 3 was used.</summary>
    private string ResolveTraderName(object area, Vector3i pos, int idx, out bool isFallback)
    {
        isFallback = false;
        string posKey = PositionKey(pos);

        // Path 1: live entity → also persist on success so we keep the name
        // forever, even when the chunk later unloads.
        try
        {
            var owner = HarmonyLib.Traverse.Create(area).Field("owningTrader").GetValue<EntityTrader>();
            if (owner != null && !string.IsNullOrEmpty(owner.EntityName))
            {
                var live = owner.EntityName;
                if (_stateStore?.Value != null)
                {
                    if (!_stateStore.Value.TraderNames.TryGetValue(posKey, out var existing) ||
                        !string.Equals(existing, live, StringComparison.Ordinal))
                    {
                        _stateStore.Value.TraderNames[posKey] = live;
                        _stateStore.Save();
                    }
                }
                return live;
            }
        }
        catch { /* swallow — try next path */ }

        // Path 2: persisted from a previous session
        if (_stateStore?.Value != null &&
            _stateStore.Value.TraderNames.TryGetValue(posKey, out var persisted) &&
            !string.IsNullOrEmpty(persisted))
        {
            return persisted;
        }

        // Path 3: numeric fallback
        isFallback = true;
        return "Trader " + (idx + 1);
    }

    /// <summary>Rescan + re-register the trader labels. Call after OnLoad to
    /// pick up names that weren't available at first scan (e.g. trader chunk
    /// wasn't loaded yet). Labels are static-baked, so the UI won't reflect
    /// any newly-resolved names until the NEXT server restart (the framework
    /// stages labels at shutdown and loads them at next-boot init).
    /// Returns the count of traders still on numeric fallback.</summary>
    private int RescanAndRelabel()
    {
        ScanTraders();
        int fallbacks = 0;
        for (int i = 0; i < _traders.Count; i++)
        {
            Styx.Ui.Labels.Register(this, "tp_trader_" + i, _traders[i].Name);
            if (IsFallbackName(_traders[i].Name)) fallbacks++;
        }
        return fallbacks;
    }

    /// <summary>Heuristic: numeric fallback names look like "Trader 5".
    /// Real ones look like "Trader Joel", "Bob", or whatever the entity
    /// reports — never just "Trader N".</summary>
    private static bool IsFallbackName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (!name.StartsWith("Trader ", StringComparison.Ordinal)) return false;
        var rest = name.Substring(7).Trim();
        return int.TryParse(rest, out _);
    }

    /// <summary>
    /// Resolve a destination Vector3 for teleporting to a trader. Picks a point
    /// just outside the closest face of the trader area, at a Y derived from
    /// the live trader entity if it's loaded (shop floor) or surface-clamped
    /// from the world otherwise.
    /// </summary>
    private Vector3 ResolveTraderDrop(CachedTrader t, Vector3 playerPos)
    {
        var world = GameManager.Instance?.World;

        // 1. Try to find the live trader entity for an authoritative Y.
        float dropY = float.NaN;
        if (world != null && t.AreaIndex >= 0 && t.AreaIndex < (world.TraderAreas?.Count ?? 0))
        {
            try
            {
                var area = world.TraderAreas[t.AreaIndex];
                var owner = HarmonyLib.Traverse.Create(area).Field("owningTrader").GetValue<EntityTrader>();
                if (owner != null)
                {
                    var op = owner.position;
                    if (op.y > 0f) dropY = op.y;
                }
            }
            catch { /* swallow — fall back to surface clamp */ }
        }

        // 2. XZ: pick the closest face of the area rectangle to the player and
        //    step `pad` blocks outside it. Player walks the last few metres in.
        const float pad = 4f;
        Vector3 min = t.AreaMin;
        Vector3 max = t.AreaMin + t.AreaSize;

        float dW = Mathf.Abs(playerPos.x - min.x);
        float dE = Mathf.Abs(max.x - playerPos.x);
        float dS = Mathf.Abs(playerPos.z - min.z);
        float dN = Mathf.Abs(max.z - playerPos.z);
        float minDist = Mathf.Min(Mathf.Min(dW, dE), Mathf.Min(dS, dN));

        float dropX, dropZ;
        if (minDist == dW)      { dropX = min.x - pad; dropZ = Mathf.Clamp(playerPos.z, min.z, max.z); }
        else if (minDist == dE) { dropX = max.x + pad; dropZ = Mathf.Clamp(playerPos.z, min.z, max.z); }
        else if (minDist == dS) { dropX = Mathf.Clamp(playerPos.x, min.x, max.x); dropZ = min.z - pad; }
        else                    { dropX = Mathf.Clamp(playerPos.x, min.x, max.x); dropZ = max.z + pad; }

        var roughDrop = new Vector3(dropX, float.IsNaN(dropY) ? t.ScanPosition.y : dropY, dropZ);

        // 3. Final clamp via SafeSurface — handles unloaded chunks and any
        //    case where the perimeter point happens to land on a roof / hole.
        return StyxCore.World.SafeSurface(roughDrop);
    }

    // ---- UI lifecycle ----

    private void OpenFor(EntityPlayer p)
    {
        if (p == null) return;
        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid))
        {
            Styx.Server.Whisper(p, "[ff6666][Teleport] Could not resolve your player id.[-]");
            return;
        }

        var ps = GetOrCreateState(pid);
        MaybeResetDaily(ps);

        _uiOpenFor.Add(p.entityId);
        _pendingConfirm.Remove(p.entityId);

        Styx.Ui.SetVar(p, "styx.tp.open", 1f);
        Styx.Ui.SetVar(p, "styx.tp.sel", 0f);

        int homes = Mathf.Clamp(_cfg.MaxHomes, 0, MaxHomeSlots);
        bool showTrader = _cfg.TraderEnabled && _traders.Count > 0;
        bool showDeath = _cfg.LastDeathEnabled;
        int count = homes + (showTrader ? 1 : 0) + (showDeath ? 1 : 0);
        Styx.Ui.SetVar(p, "styx.tp.count", count);

        // Default every row to empty so unused slots don't bleed previous values.
        for (int k = 0; k < MaxUiRows; k++)
        {
            Styx.Ui.SetVar(p, "styx.tp.row" + k + "_kind", KindEmpty);
            Styx.Ui.SetVar(p, "styx.tp.row" + k + "_id", 0);
            Styx.Ui.SetVar(p, "styx.tp.row" + k + "_status", 0);
        }

        // Home rows occupy slots 0..homes-1.
        for (int i = 0; i < homes; i++)
        {
            Styx.Ui.SetVar(p, "styx.tp.row" + i + "_kind", KindHome);
            Styx.Ui.SetVar(p, "styx.tp.row" + i + "_id", i);
            Styx.Ui.SetVar(p, "styx.tp.row" + i + "_status",
                ps.Homes[i] != null ? 1 : 0);
        }

        // Trader row sits right after the homes (if enabled & found).
        int traderRow = homes;
        int deathRow = showTrader ? homes + 1 : homes;

        if (showTrader)
        {
            var nearest = FindNearestTrader(p.position);
            int slotId = nearest?.SlotId ?? 0;
            Styx.Ui.SetVar(p, "styx.tp.row" + traderRow + "_kind", KindTrader);
            Styx.Ui.SetVar(p, "styx.tp.row" + traderRow + "_id", slotId);
            Styx.Ui.SetVar(p, "styx.tp.row" + traderRow + "_status", 1);
        }

        if (showDeath)
        {
            Styx.Ui.SetVar(p, "styx.tp.row" + deathRow + "_kind", KindDeath);
            Styx.Ui.SetVar(p, "styx.tp.row" + deathRow + "_id", 0);
            Styx.Ui.SetVar(p, "styx.tp.row" + deathRow + "_status",
                ps.LastDeath != null ? 1 : 0);
        }

        Styx.Ui.Input.Acquire(p, Name);
        WhisperRow(p, 0, ps);
    }

    private void CloseFor(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, "styx.tp.open", 0f);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
        _pendingConfirm.Remove(p.entityId);
    }

    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null || !_uiOpenFor.Contains(p.entityId)) return;
        if ((int)p.Buffs.GetCustomVar("styx.tp.open") != 1) return;

        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return;
        var ps = GetOrCreateState(pid);
        MaybeResetDaily(ps);

        int sel = (int)p.Buffs.GetCustomVar("styx.tp.sel");
        int count = (int)p.Buffs.GetCustomVar("styx.tp.count");
        if (count <= 0) { CloseFor(p); return; }

        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                sel = (sel + 1) % count;
                Styx.Ui.SetVar(p, "styx.tp.sel", sel);
                _pendingConfirm.Remove(p.entityId);
                WhisperRow(p, sel, ps);
                break;

            case Styx.Ui.StyxInputKind.Crouch:
                sel = (sel - 1 + count) % count;
                Styx.Ui.SetVar(p, "styx.tp.sel", sel);
                _pendingConfirm.Remove(p.entityId);
                WhisperRow(p, sel, ps);
                break;

            case Styx.Ui.StyxInputKind.PrimaryAction:
                TryConfirm(p, pid, ps, sel);
                break;

            case Styx.Ui.StyxInputKind.SecondaryAction:
                CloseFor(p);
                Styx.Scheduling.Scheduler.Once(0.05,
                    () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
                    name: "StyxTeleport.BackToLauncher");
                break;
        }
    }

    // ---- Row semantics ----

    private int RowToDestId(int row)
    {
        int homes = Mathf.Clamp(_cfg.MaxHomes, 0, MaxHomeSlots);
        if (row < homes) return DestHome0 + row;                 // 0..homes-1 → home
        if (_cfg.TraderEnabled && _traders.Count > 0 && row == homes) return DestTrader;
        return DestDeath;
    }

    private Vector3? ResolveDestination(EntityPlayer p, PlayerState ps, int destId, out string label)
    {
        if (destId >= DestHome0 && destId <= DestHome0 + MaxHomeSlots - 1)
        {
            int slot = destId - DestHome0;
            label = "Home " + (slot + 1);
            var h = ps.Homes[slot];
            return h == null ? null : (Vector3?)new Vector3(h.X, h.Y, h.Z);
        }
        if (destId == DestTrader)
        {
            var t = FindNearestTrader(p.position);
            label = t != null ? t.Name : "Nearest trader";
            if (t == null) return null;
            return ResolveTraderDrop(t, p.position);
        }
        if (destId == DestDeath)
        {
            label = "Last death";
            return ps.LastDeath == null
                ? (Vector3?)null
                : new Vector3(ps.LastDeath.X, ps.LastDeath.Y, ps.LastDeath.Z);
        }
        label = "?";
        return null;
    }

    private void WhisperRow(EntityPlayer p, int row, PlayerState ps)
    {
        int destId = RowToDestId(row);
        var dest = ResolveDestination(p, ps, destId, out string label);
        int count = (int)p.Buffs.GetCustomVar("styx.tp.count");

        string state;
        if (dest == null)
        {
            state = destId == DestDeath
                ? "[888888]no death recorded yet[-]"
                : "[888888]empty — use /sethome " + (destId - DestHome0 + 1) + "[-]";
        }
        else
        {
            var rule = ResolveRule(p);
            long now = UnixNow();
            long cdRemaining = Math.Max(0, ps.NextUseUnix - now);
            int dailyLeft = rule.DailyLimit < 0 ? -1 : Math.Max(0, rule.DailyLimit - ps.DailyCount);
            string cd = cdRemaining > 0 ? "[ffaa00]cd " + FormatDur(cdRemaining) + "[-]" : "[00ff66]ready[-]";
            string daily = dailyLeft < 0 ? "unlimited" : dailyLeft + " left today";
            state = cd + " · " + daily;
        }

        Styx.Server.Whisper(p, string.Format(
            "[ccddff][Teleport] {0}/{1}:[-] [ffffdd]{2}[-] — {3}",
            row + 1, count, label, state));
    }

    // ---- Confirm + teleport ----

    private void TryConfirm(EntityPlayer p, string pid, PlayerState ps, int row)
    {
        int destId = RowToDestId(row);
        var dest = ResolveDestination(p, ps, destId, out string label);
        if (dest == null)
        {
            Styx.Server.Whisper(p, "[ff6666][Teleport] '" + label + "' is not set.[-]");
            return;
        }

        var rule = ResolveRule(p);
        long now = UnixNow();

        if (ps.NextUseUnix > now)
        {
            Styx.Server.Whisper(p, "[ffaa00][Teleport] On cooldown (" + FormatDur(ps.NextUseUnix - now) + ").[-]");
            return;
        }
        if (rule.DailyLimit >= 0 && ps.DailyCount >= rule.DailyLimit)
        {
            Styx.Server.Whisper(p, "[ffaa00][Teleport] Daily limit reached (" + rule.DailyLimit + "). Resets at midnight UTC.[-]");
            return;
        }

        // Quest warning — only trigger on first LMB, require a second LMB within window.
        if (_cfg.QuestWarning && HasActiveQuest(p))
        {
            if (_pendingConfirm.TryGetValue(p.entityId, out var pending)
                && pending.destId == destId
                && (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - pending.tUnix <= _cfg.TwoTapConfirmSeconds)
            {
                _pendingConfirm.Remove(p.entityId);
                // fall through to teleport
            }
            else
            {
                _pendingConfirm[p.entityId] = (destId, (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
                Styx.Server.Whisper(p, "[ffaa00][Teleport] You have active quest(s). Teleporting may cancel them. " +
                    "Press LMB again within " + (int)_cfg.TwoTapConfirmSeconds + "s to confirm.[-]");
                return;
            }
        }

        // Commit the cooldown + daily-count BEFORE the teleport so a slow
        // chunk-load can't be spam-tapped into a double-charge.
        ps.NextUseUnix = now + Math.Max(0, rule.PerUseSeconds);
        ps.DailyCount += 1;
        _stateStore.Save();

        // Single teleport via the framework's Teleport() — which routes through
        // the vanilla `teleportplayer` console command. That's the engine's own
        // long-distance teleport pathway: it preloads chunks at the destination
        // before releasing the client to physics, so the unstick loop doesn't
        // yank the player back to home while terrain streams in.
        //
        // We still SafeSurface-clamp non-trader destinations defensively
        // (homes saved on top of an irregular block, etc.). Trader drops are
        // already perimeter+SafeSurface-resolved inside ResolveTraderDrop.
        var final = destId == DestTrader ? dest.Value : StyxCore.World.SafeSurface(dest.Value);

        // ── Landing protection ────────────────────────────────────────────
        // teleportplayer rounds Y to int AND our SafeSurface guess is based
        // on chunks that aren't streamed in yet, so the player frequently
        // arrives 10-30 blocks above real ground.
        //
        // Vanilla buffs.xml has the perfect flag for this: `buffDontBreakMyLeg`
        // (used by admin rocket boots). The fall-damage controller's HP-subtract
        // and leg-break/sprain buff-adds all gate on `!HasBuff buffDontBreakMyLeg`
        // (buffs.xml lines 4281, 4340, 4430). Apply it for the landing window
        // and the engine itself skips fall damage in its own pipeline — much
        // cleaner than fighting damage patches, and it covers HP loss + broken
        // leg + sprained leg in one shot.
        //
        // We still schedule a reclamp pass to gently move the player to the
        // real surface once chunks have streamed in, so they don't end up
        // standing on the trader's roof or stuck inside a wall.
        int eid = p.entityId;
        const float fallProtectSec = 8f;

        // Snapshot active injury buffs BEFORE teleport so we can restore
        // any that vanilla's teleport-respawn pathway strips. Empty list
        // when feature disabled or player has no injuries — cheap.
        var preservedInjuries = _cfg.PreserveInjuriesOnTeleport
            ? SnapshotInjuryBuffs(p)
            : new List<string>();

        // Apply the vanilla "no fall damage" flag buff. duration=0 in XML
        // means manually managed; passing an explicit duration sets the
        // buff system's lifetime tracker so it auto-removes if our scheduled
        // RemoveBuff doesn't fire.
        StyxCore.Player.ApplyBuff(p, "buffDontBreakMyLeg", fallProtectSec, primeCVar: false);

        bool ok = StyxCore.Player.Teleport(p, final);
        if (!ok)
        {
            StyxCore.Player.RemoveBuff(p, "buffDontBreakMyLeg");
            Styx.Server.Whisper(p, "[ff6666][Teleport] Engine refused the teleport.[-]");
            return;
        }

        Styx.Server.Whisper(p, string.Format(
            "[00ff66][Teleport] Sent to {0} ({1:F0},{2:F0},{3:F0}).[-]",
            label, final.x, final.y, final.z));
        Styx.Ui.Toast(p, "Teleported: " + label, Styx.Ui.Sounds.ChallengeRedeem);
        Log.Out("[StyxTeleport] {0} teleported to {1} at {2} (+buffDontBreakMyLeg {3}s)",
            pid, label, final, fallProtectSec);

        CloseFor(p);

        // Reclamp Y once chunks are loaded — gently moves the player to the
        // actual surface if our initial Y guess was off. Also re-applies any
        // injury buffs the teleport-respawn pathway stripped (no exploit
        // healing through teleport).
        Styx.Scheduling.Scheduler.Once(0.5, () =>
        {
            try
            {
                var player = StyxCore.Player.FindByEntityId(eid);
                if (player == null) return;

                // Restore stripped injury buffs.
                if (preservedInjuries.Count > 0)
                    RestoreInjuryBuffs(player, preservedInjuries, pid);

                // Re-clamp Y to the now-loaded surface.
                var here = player.position;
                var clamped = StyxCore.World.SafeSurface(new Vector3(here.x, here.y, here.z));
                if (Mathf.Abs(clamped.y - here.y) > 1.5f)
                {
                    Log.Out("[StyxTeleport] reclamp {0}: {1:F1} -> {2:F1}", pid, here.y, clamped.y);
                    StyxCore.Player.Teleport(player, clamped);
                }
            }
            catch (Exception e) { Log.Warning("[StyxTeleport] Reclamp failed: " + e.Message); }
        }, name: "StyxTeleport.Reclamp");

        // Belt-and-braces: explicitly remove the protection at end of window
        // in case the buff's own duration tracking didn't fire.
        Styx.Scheduling.Scheduler.Once(fallProtectSec + 0.5, () =>
        {
            var player = StyxCore.Player.FindByEntityId(eid);
            if (player != null) StyxCore.Player.RemoveBuff(player, "buffDontBreakMyLeg");
        }, name: "StyxTeleport.UnprotectFall");
    }

    // ---- Chat commands ----

    private void CmdSetHome(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        if (args.Length < 1 || !int.TryParse(args[0], out var n) || n < 1 || n > _cfg.MaxHomes)
        { ctx.Reply("Usage: /sethome <1-" + _cfg.MaxHomes + ">"); return; }

        var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (p == null) { ctx.Reply("Player not found."); return; }

        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("No platform id."); return; }

        var ps = GetOrCreateState(pid);
        ps.Homes[n - 1] = new HomePos
        {
            X = p.position.x,
            Y = p.position.y,
            Z = p.position.z,
            SetUnix = UnixNow(),
        };
        _stateStore.Save();
        ctx.Reply(string.Format("[00ff66]Home {0} set to ({1:F0},{2:F0},{3:F0}).[-]",
            n, p.position.x, p.position.y, p.position.z));
    }

    private void CmdDelHome(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        if (args.Length < 1 || !int.TryParse(args[0], out var n) || n < 1 || n > MaxHomeSlots)
        { ctx.Reply("Usage: /delhome <1-" + _cfg.MaxHomes + ">"); return; }

        var pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return;
        if (!_stateStore.Value.Players.TryGetValue(pid, out var ps) || ps.Homes[n - 1] == null)
        { ctx.Reply("[ffaa00]Home " + n + " wasn't set.[-]"); return; }

        ps.Homes[n - 1] = null;
        _stateStore.Save();
        ctx.Reply("[ffaa00]Home " + n + " cleared.[-]");
    }

    private void CmdListHomes(Styx.Commands.CommandContext ctx, string[] args)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        var pid = ctx.Client.PlatformId?.CombinedString ?? "";
        if (!_stateStore.Value.Players.TryGetValue(pid, out var ps))
        { ctx.Reply("You have no homes set. Use /sethome <1-" + _cfg.MaxHomes + ">."); return; }

        ctx.Reply("Your homes:");
        for (int i = 0; i < _cfg.MaxHomes; i++)
        {
            var h = ps.Homes[i];
            if (h == null)
                ctx.Reply("  " + (i + 1) + ": [888888]empty[-]");
            else
                ctx.Reply(string.Format("  {0}: [ffffdd]({1:F0}, {2:F0}, {3:F0})[-]", i + 1, h.X, h.Y, h.Z));
        }
        if (ps.LastDeath != null)
            ctx.Reply(string.Format("  Last death: [ffffdd]({0:F0}, {1:F0}, {2:F0})[-]",
                ps.LastDeath.X, ps.LastDeath.Y, ps.LastDeath.Z));
    }

    // ---- Injury preservation ----
    //
    // Vanilla `teleportplayer` triggers an EntityPlayer.Respawn(Teleport) which
    // strips status buffs (it's intended for admin travel — clean state on
    // arrival). For a player-facing teleport we don't want that — being able
    // to break your leg in a fight then /m → trader → land healed is an exploit.

    private List<string> SnapshotInjuryBuffs(EntityPlayer p)
    {
        var found = new List<string>();
        if (p?.Buffs == null) return found;
        foreach (var bn in InjuryBuffNames)
        {
            try { if (p.Buffs.HasBuff(bn)) found.Add(bn); }
            catch { /* ignore single-buff hiccups */ }
        }
        return found;
    }

    private void RestoreInjuryBuffs(EntityPlayer p, List<string> buffs, string pid)
    {
        if (p?.Buffs == null || buffs == null || buffs.Count == 0) return;
        foreach (var bn in buffs)
        {
            try
            {
                if (!p.Buffs.HasBuff(bn))
                {
                    StyxCore.Player.ApplyBuff(p, bn, primeCVar: false);
                    Log.Out("[StyxTeleport] Restored injury {0} for {1}", bn, pid);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[StyxTeleport] Restore {0} failed: {1}", bn, e.Message);
            }
        }
    }

    // ---- Helpers ----

    private CachedTrader FindNearestTrader(Vector3 pos)
    {
        CachedTrader best = null;
        float bestSqr = float.MaxValue;
        foreach (var t in _traders)
        {
            // Compare on the area centre — close enough for nearest-trader picks
            // and avoids needing the live entity to be loaded.
            var c = t.AreaMin + t.AreaSize * 0.5f;
            float d = (c - pos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = t; }
        }
        return best;
    }

    /// <summary>"Most generous" rule across player's matched groups — lowest
    /// PerUseSeconds, highest DailyLimit (with -1 as infinity).</summary>
    private CooldownRule ResolveRule(EntityPlayer p)
    {
        var fallback = new CooldownRule { PerUseSeconds = 600, DailyLimit = 5 };
        if (_cfg.Cooldowns.TryGetValue("default", out var def)) fallback = def;

        var pid = StyxCore.Player.PlatformIdOf(p);
        if (string.IsNullOrEmpty(pid)) return fallback;

        var groups = StyxCore.Perms.GetPlayerGroups(pid);
        var best = new CooldownRule
        {
            PerUseSeconds = fallback.PerUseSeconds,
            DailyLimit = fallback.DailyLimit,
        };

        foreach (var g in groups)
        {
            if (!_cfg.Cooldowns.TryGetValue(g, out var r)) continue;
            if (r.PerUseSeconds < best.PerUseSeconds) best.PerUseSeconds = r.PerUseSeconds;
            // Highest wins, with -1 (unlimited) beating any positive number.
            if (r.DailyLimit < 0) best.DailyLimit = -1;
            else if (best.DailyLimit >= 0 && r.DailyLimit > best.DailyLimit) best.DailyLimit = r.DailyLimit;
        }
        return best;
    }

    private void MaybeResetDaily(PlayerState ps)
    {
        long now = UnixNow();
        if (ps.DailyResetUnix == 0 || now >= ps.DailyResetUnix)
        {
            // Next midnight UTC
            var utc = DateTime.UtcNow;
            var nextMidnight = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
            ps.DailyResetUnix = ((DateTimeOffset)nextMidnight).ToUnixTimeSeconds();
            ps.DailyCount = 0;
        }
    }

    private bool HasActiveQuest(EntityPlayer p)
    {
        try
        {
            var journal = p.QuestJournal;
            if (journal?.quests == null) return false;
            foreach (var q in journal.quests)
            {
                if (q == null) continue;
                if (q.CurrentState == Quest.QuestState.InProgress) return true;
            }
        }
        catch { /* best-effort */ }
        return false;
    }

    private PlayerState GetOrCreateState(string pid)
    {
        if (!_stateStore.Value.Players.TryGetValue(pid, out var ps))
        {
            ps = new PlayerState();
            _stateStore.Value.Players[pid] = ps;
        }
        if (ps.Homes == null || ps.Homes.Length != MaxHomeSlots)
            ps.Homes = new HomePos[MaxHomeSlots];
        return ps;
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string FormatDur(long seconds)
    {
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return (seconds / 60) + "m " + (seconds % 60) + "s";
        return (seconds / 3600) + "h " + ((seconds % 3600) / 60) + "m";
    }
}
