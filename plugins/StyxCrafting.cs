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

// StyxCrafting — perm-tiered crafting buffs for workstations + toolbelt.
//
// FEATURES (all per-perm tiered, first-match wins):
//   1. Craft speed multiplier   — player's recipes complete N× faster
//                                  (e.g. 0.5 = double speed, 0.25 = 4× speed).
//                                  Applies to toolbelt crafts AND
//                                  workstation queue items (forge, workbench,
//                                  chemistry station, cement mixer, campfire).
//
//   2. Output multiplier        — bonus copies of the crafted item delivered
//                                  to the workstation output (workstation
//                                  recipes only; toolbelt outputs aren't
//                                  modified). Floor(count × mult) − count
//                                  bonus stacks per recipe completion.
//
//   3. Auto-shutdown            — fuel-using stations (forge, chem, cement,
//                                  campfire) stop burning fuel when their
//                                  recipe queue is empty. Saves wood / coal /
//                                  meat for the player who placed the order.
//
// HOW IT'S WIRED:
//   Crafting time → Harmony postfix on `EffectManager.GetValue` for the
//   PassiveEffects.CraftingTime case (covers any vanilla path that asks
//   "how long does this recipe take for this player?") AND a postfix on
//   `TileEntityWorkstation.read` that re-scales every queue item's
//   `OneItemCraftTime` based on the queueing player's perm. The latter is
//   the server-authoritative path — workstations grind on their cached
//   craft time, not on a re-query.
//
//   Output multiplier → Harmony postfix on `TileEntityWorkstation.HandleRecipeQueue`
//   that adds bonus stacks to the workstation's output array after the
//   engine has added the base count.
//
//   Auto-shutdown → postfix on `TileEntityWorkstation.UpdateTick`. We
//   also cache the StartingEntityId of the last queue item we saw at each
//   workstation so that, after the queue empties, we still know who to
//   attribute the auto-shutdown perm check to.
//
// PERMS REGISTERED (default config — extend / rename in configs/StyxCrafting.json):
//   styx.craft.master  → 4× speed, 2× output, auto-shutdown
//   styx.craft.vip     → 2× speed, 1.5× output, auto-shutdown
//   styx.craft.use     → 1.33× speed, 1× output, no auto-shutdown
//   no perm            → vanilla speed, no bonus, no auto-shutdown

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("StyxCrafting", "Doowkcol", "0.1.0")]
public class StyxCrafting : StyxPlugin
{
    public override string Description => "Perm-tiered crafting — speed multiplier, output bonus, fuel auto-shutdown";

    // ============================================================ schema

    public class TierEntry
    {
        public string Perm;
        /// <summary>Multiplier applied to craft TIME — &lt; 1.0 = faster.
        /// e.g. 0.5 means recipes take half as long (2× speed).
        /// Clamped to [0.05, 10.0].</summary>
        public float CraftTimeMult = 1.0f;
        /// <summary>Multiplier applied to OUTPUT count — ≥ 1.0 to add bonus.
        /// e.g. 2.0 = double output. Bonus = floor(baseCount × mult) − baseCount.
        /// Workstation recipes only.</summary>
        public float OutputMult = 1.0f;
        /// <summary>True = fuel-using workstations (forge / chem / cement / campfire)
        /// auto-stop burning when their queue empties. Tracked per-workstation
        /// against the last player to queue a recipe there.</summary>
        public bool AutoShutdown = false;
    }

    public class Config
    {
        public bool Enabled = true;

        /// <summary>First-match-wins tier list. Walk top-down — the first
        /// perm the player has determines all three modifiers. No match =
        /// vanilla behaviour.</summary>
        public List<TierEntry> Tiers = new List<TierEntry>
        {
            new TierEntry { Perm = "styx.craft.master", CraftTimeMult = 0.1f,  OutputMult = 3.0f,  AutoShutdown = true  },
            new TierEntry { Perm = "styx.craft.vip",    CraftTimeMult = 0.25f, OutputMult = 2.0f,  AutoShutdown = true  },
            new TierEntry { Perm = "styx.craft.use",    CraftTimeMult = 0.5f,  OutputMult = 1.25f, AutoShutdown = false },
        };
    }

    // ============================================================ runtime

    private static Config _staticCfg;  // mirrored for the static Harmony helpers
    /// <summary>Toggleable verbose patch logging — flip via /crafting diag on|off.
    /// Logs every patch invocation: who, what tier matched, what numeric effect.</summary>
    private static bool _diagLogs = false;

    /// <summary>Workstation world-pos → last StartingEntityId seen queueing
    /// a recipe there. CRITICAL: workstations are BLOCK-based tile entities
    /// — `entityId == -1` for every one, so keying by entityId would treat
    /// every workstation on the server as "the same TE" and inherit any
    /// player's perm-attribution to all of them. World position is the
    /// stable per-workstation identifier.
    ///
    /// Survives the queue going empty so we can still check perms when
    /// deciding to flip isBurning off. Overwritten when a different player
    /// queues a recipe at the same workstation.</summary>
    private static readonly Dictionary<Vector3i, int> _lastQueuerByPos = new Dictionary<Vector3i, int>();

    /// <summary>Set of every workstation world-pos we've ever seen UpdateTick fire
    /// on. The scheduler-driven sweep walks this set instead of depending on
    /// UpdateTick to keep firing — the engine appears to STOP calling UpdateTick
    /// on workstations once they have no active work (no recipe queue + no raw
    /// material to smelt), even while they're still burning fuel idly. So our
    /// auto-shutdown logic never got a chance to run on the exact case it was
    /// designed for. The sweep does a fresh GetTileEntity lookup each tick to
    /// avoid holding stale references after chunks unload.</summary>
    private static readonly HashSet<Vector3i> _knownWorkstations = new HashSet<Vector3i>();
    private static readonly object _cacheLock = new object();

    private static TimerHandle _sweepTimer;

    public override void OnLoad()
    {
        var cfg = StyxCore.Configs.Load<Config>(this);
        _staticCfg = cfg;

        // Register every tier perm for PermEditor.
        foreach (var t in cfg.Tiers)
        {
            if (string.IsNullOrEmpty(t?.Perm)) continue;
            StyxCore.Perms.RegisterKnown(t.Perm,
                string.Format("Crafting buffs — {0:0.##}× time, {1:0.##}× output, autoshutdown {2}",
                    t.CraftTimeMult, t.OutputMult, t.AutoShutdown ? "ON" : "off"),
                Name);
        }

        StyxCore.Commands.Register("crafting", "StyxCrafting status — /crafting [diag on|off]", (ctx, args) =>
        {
            if (args.Length >= 2 && args[0].Equals("diag", StringComparison.OrdinalIgnoreCase))
            {
                _diagLogs = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                ctx.Reply(string.Format("[00ff66]StyxCrafting diag: {0}[-]", _diagLogs ? "ON" : "off"));
                return;
            }
            ctx.Reply(string.Format("[ccddff]StyxCrafting v0.1:[-] enabled={0}, {1} tier(s), diag={2}",
                cfg.Enabled, cfg.Tiers.Count, _diagLogs ? "ON" : "off"));
            foreach (var t in cfg.Tiers)
                ctx.Reply(string.Format("  {0} → time×{1:0.##}, output×{2:0.##}, autoshutdown {3}",
                    t.Perm, t.CraftTimeMult, t.OutputMult, t.AutoShutdown ? "ON" : "off"));
            ctx.Reply(string.Format("[ccddff]Tracked workstations:[-] {0}", _lastQueuerByPos.Count));
        });

        // Periodic auto-shutdown sweep — runs independently of UpdateTick because
        // the engine appears to deactivate UpdateTick for idle-burning
        // workstations (no recipe queue, no raw material). 2s cadence is plenty
        // for a "stop wasting fuel" feature.
        _sweepTimer = Scheduler.Every(2.0, AutoShutdownSweep, "styxcrafting.autoshutdown");

        Log.Out("[StyxCrafting] Loaded v0.1.0 — {0} tier(s), sweep every 2s", cfg.Tiers.Count);
    }

    public override void OnUnload()
    {
        _sweepTimer?.Destroy();
        _sweepTimer = null;
        StyxCore.Perms.UnregisterKnownByOwner(Name);
        lock (_cacheLock) { _lastQueuerByPos.Clear(); _knownWorkstations.Clear(); }
        _staticCfg = null;
    }

    /// <summary>Scheduler-driven sweep. Walks the cached set of known workstation
    /// positions, looks each one up in the live world, and runs the autoshutdown
    /// evaluation. Positions that no longer resolve to a workstation are pruned.</summary>
    private static void AutoShutdownSweep()
    {
        if (_staticCfg == null || !_staticCfg.Enabled) return;
        var world = GameManager.Instance?.World;
        if (world == null) return;

        List<Vector3i> snapshot;
        lock (_cacheLock) snapshot = _knownWorkstations.ToList();

        var dead = new List<Vector3i>();
        int evaluated = 0;
        foreach (var pos in snapshot)
        {
            TileEntity te = null;
            try { te = world.GetTileEntity(0, pos); } catch { }
            if (!(te is TileEntityWorkstation ws))
            {
                dead.Add(pos);
                continue;
            }
            EvaluateAutoShutdown(ws, pos);
            evaluated++;
        }
        if (dead.Count > 0)
        {
            lock (_cacheLock) foreach (var p in dead) _knownWorkstations.Remove(p);
        }
    }

    /// <summary>Decide whether the given workstation should auto-shutdown right
    /// now and do it if so. Pulled out of the UpdateTick patch so the periodic
    /// sweep can reuse the exact same gates and diag output.</summary>
    private static void EvaluateAutoShutdown(TileEntityWorkstation ws, Vector3i pos)
    {
        if (ws == null) return;
        if (!ws.isBurning) return;
        if (ws.isModuleUsed == null || ws.isModuleUsed.Length <= 3) return;
        if (!ws.isModuleUsed[3]) return;

        if (ws.hasRecipeInQueue())
        {
            if (_diagLogs) Log.Out("[StyxCrafting/diag] AutoShutdown pos={0}: SKIP (recipe in queue)", pos);
            return;
        }

        // Check input slots for ACTIVELY-SMELTING raw material. The forge
        // stores both raw inputs (iron, lead, clay, stone) AND already-
        // smelted units (unit_iron, unit_lead, ...) in the same input[]
        // array. The smelted units are an idle reserve consumed by recipes
        // — their presence should NOT keep the fuel burning. Heuristic:
        // anything whose item name starts with "unit_" is smelted output;
        // anything else is raw and still needs processing.
        var input = ws.input;
        if (input != null)
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == null || input[i].count <= 0) continue;
                var itemName = input[i].itemValue?.ItemClass?.Name;
                if (string.IsNullOrEmpty(itemName)) continue;
                if (itemName.StartsWith("unit_", StringComparison.OrdinalIgnoreCase)) continue;
                if (_diagLogs) Log.Out("[StyxCrafting/diag] AutoShutdown pos={0}: SKIP (input slot {1} has {2} of {3} — raw material still smelting)",
                    pos, i, input[i].count, itemName);
                return;
            }
        }

        // Resolve owner: LCB first, queuer fallback.
        var tier = ResolveTierForLandClaim(pos);
        string source = "lcb";
        int starterEid = 0;
        if (tier == null)
        {
            lock (_cacheLock)
            {
                if (!_lastQueuerByPos.TryGetValue(pos, out starterEid))
                {
                    if (_diagLogs) Log.Out("[StyxCrafting/diag] AutoShutdown pos={0}: SKIP (no LCB owner + no queue history)", pos);
                    return;
                }
            }
            tier = ResolveTierForEntity(starterEid);
            source = "queuer/eid=" + starterEid;
        }
        if (tier == null)
        {
            if (_diagLogs) Log.Out("[StyxCrafting/diag] AutoShutdown pos={0}: SKIP (owner found via {1} but no matching tier — player lacks all craft perms)", pos, source);
            return;
        }
        if (!tier.AutoShutdown)
        {
            if (_diagLogs) Log.Out("[StyxCrafting/diag] AutoShutdown pos={0}: SKIP (tier={1}, AutoShutdown flag off)", pos, tier.Perm);
            return;
        }

        ws.isBurning = false;
        ws.setModified();
        if (_diagLogs)
            Log.Out("[StyxCrafting/diag] AutoShutdown FIRED pos={0} (owner via {1}, tier={2})",
                pos, source, tier.Perm);
    }

    // ============================================================ tier resolution

    /// <summary>Pluck the player's tier — first perm they have wins.
    /// Returns null when no tier matches (vanilla behaviour).</summary>
    private static TierEntry ResolveTier(string pid)
    {
        if (_staticCfg == null || !_staticCfg.Enabled) return null;
        if (string.IsNullOrEmpty(pid)) return null;
        foreach (var t in _staticCfg.Tiers)
        {
            if (string.IsNullOrEmpty(t?.Perm)) continue;
            try { if (StyxCore.Perms.HasPermission(pid, t.Perm)) return t; }
            catch { }
        }
        return null;
    }

    private static TierEntry ResolveTierForEntity(int entityId)
    {
        if (entityId <= 0) return null;
        try
        {
            var ent = GameManager.Instance?.World?.GetEntity(entityId) as EntityPlayer;
            if (ent == null) return null;
            var pid = StyxCore.Player?.PlatformIdOf(ent);
            return ResolveTier(pid);
        }
        catch { return null; }
    }

    /// <summary>
    /// Find the PersistentPlayerData whose Land Claim Block covers the given
    /// world position, then resolve THAT player's crafting tier. This makes
    /// "the base owner's perks apply to every workstation in the base" the
    /// natural attribution model — semantically cleaner than
    /// "last person to queue something here owns the autoshutdown".
    ///
    /// Vanilla 7DTD claim shape: square, centred on the LCB block, side
    /// length = GameStats.LandClaimSize (default 41 blocks). Y-extent is
    /// the full vertical column.
    /// </summary>
    private static TierEntry ResolveTierForLandClaim(Vector3i pos)
    {
        try
        {
            var ppl = GameManager.Instance?.GetPersistentPlayerList();
            if (ppl == null)
            {
                if (_diagLogs) Log.Out("[StyxCrafting/diag] LCB lookup pos={0}: PPL is null", pos);
                return null;
            }
            int claimSize = GameStats.GetInt(EnumGameStats.LandClaimSize);
            if (claimSize <= 0) claimSize = 41;
            int half = (claimSize - 1) / 2;

            int totalPlayers = 0, totalLcbs = 0;
            foreach (var kv in ppl.Players)
            {
                var ppd = kv.Value;
                totalPlayers++;
                if (ppd?.LPBlocks == null || ppd.LPBlocks.Count == 0) continue;
                totalLcbs += ppd.LPBlocks.Count;
                foreach (var lcb in ppd.LPBlocks)
                {
                    if (Math.Abs(pos.x - lcb.x) > half) continue;
                    if (Math.Abs(pos.z - lcb.z) > half) continue;
                    // ID format mismatch quirk: ppd.PrimaryId on a crossplay
                    // server is the EOS ID (EOS_000222...). The perm system
                    // (and serveradmin.xml owner shortcut) uses the
                    // platform-native ID (Steam_76561...). Same player, two
                    // different strings. Walk every ID PPD exposes and take
                    // the first one that resolves to a tier.
                    string matchedPid = null;
                    var tier = ResolveTierForPpd(ppd, out matchedPid);
                    if (_diagLogs)
                        Log.Out("[StyxCrafting/diag] LCB lookup pos={0}: owner (lcb at {1}), tried IDs → matched={2}, tier={3}",
                            pos, lcb, matchedPid ?? "(none)", tier?.Perm ?? "(none)");
                    return tier;
                }
            }
            if (_diagLogs)
                Log.Out("[StyxCrafting/diag] LCB lookup pos={0}: no claim match ({1} player(s), {2} lcb(s), claimHalfSize={3})",
                    pos, totalPlayers, totalLcbs, half);
        }
        catch (Exception e)
        {
            Log.Warning("[StyxCrafting] LCB owner lookup failed: " + e.Message);
        }
        return null;
    }

    /// <summary>Walk every player-identifier the PPD exposes (primary / native /
    /// owner / extras) and return the first one that resolves to a craft tier.
    /// Necessary because crossplay servers store the EOS ID as the persistent
    /// "primary" but the perm system + serveradmin.xml are keyed by the
    /// platform-native ID (Steam_xxx). Outputs the matched ID for diagnostics.
    /// </summary>
    private static TierEntry ResolveTierForPpd(PersistentPlayerData ppd, out string matchedPid)
    {
        matchedPid = null;
        if (ppd == null) return null;

        // Collect every distinct identifier the PPD exposes, in priority order.
        // Same player can have PrimaryId=EOS_xxx and NativeId=Steam_xxx —
        // perm system / serveradmin.xml lookups only match one of them.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>(4);
        void AddPid(PlatformUserIdentifierAbs pid)
        {
            var s = pid?.CombinedString;
            if (string.IsNullOrEmpty(s)) return;
            if (seen.Add(s)) candidates.Add(s);
        }

        AddPid(ppd.PrimaryId);
        AddPid(ppd.NativeId);
        // Try every *Id property/field of type PlatformUserIdentifierAbs via
        // reflection — PersistentPlayerData's exact surface varies between
        // V2.6 patches. Covers OwnerId / SecondaryId / etc. if they exist
        // without hard-coding names that might not compile.
        try
        {
            var t = typeof(PersistentPlayerData);
            foreach (var prop in t.GetProperties())
            {
                if (!typeof(PlatformUserIdentifierAbs).IsAssignableFrom(prop.PropertyType)) continue;
                try { AddPid(prop.GetValue(ppd) as PlatformUserIdentifierAbs); } catch { }
            }
            foreach (var fld in t.GetFields())
            {
                if (typeof(PlatformUserIdentifierAbs).IsAssignableFrom(fld.FieldType))
                {
                    try { AddPid(fld.GetValue(ppd) as PlatformUserIdentifierAbs); } catch { }
                }
                else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(fld.FieldType))
                {
                    // UserIdentifiers-like collection of PlatformUserIdentifierAbs
                    try
                    {
                        if (fld.GetValue(ppd) is System.Collections.IEnumerable extras)
                            foreach (var x in extras)
                                if (x is PlatformUserIdentifierAbs p) AddPid(p);
                    }
                    catch { }
                }
            }
        }
        catch { /* ignore — reflection best-effort */ }

        foreach (var pid in candidates)
        {
            var t = ResolveTier(pid);
            if (t != null) { matchedPid = pid; return t; }
        }

        // No match — surface every ID we tried in the diag so the operator can
        // see what was on the LCB record.
        if (candidates.Count > 0) matchedPid = "tried[" + string.Join(",", candidates) + "]";
        return null;
    }

    // ============================================================ Harmony patches

    /// <summary>Generic craft-time scaler. Hits any vanilla path that asks
    /// "how long for this player to craft this recipe?" — toolbelt crafts,
    /// workstation queueing, repair actions. The actual workstation processor
    /// uses cached OneItemCraftTime values though, so this doesn't speed up
    /// already-queued recipes — see the TE.read patch below for that.</summary>
    [HarmonyPatch(typeof(EffectManager), nameof(EffectManager.GetValue),
        new[] { typeof(PassiveEffects), typeof(ItemValue), typeof(float),
                typeof(EntityAlive), typeof(Recipe), typeof(FastTags<TagGroup.Global>),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                typeof(int), typeof(bool), typeof(bool) })]
    static class EffectManager_GetValue_Patch
    {
        static void Postfix(PassiveEffects _passiveEffect, EntityAlive _entity, ref float __result)
        {
            if (_passiveEffect != PassiveEffects.CraftingTime) return;
            if (!(_entity is EntityPlayer ep)) return;
            string pid = null;
            try { pid = StyxCore.Player?.PlatformIdOf(ep); } catch { }
            var tier = ResolveTier(pid);
            if (_diagLogs)
                Log.Out("[StyxCrafting/diag] EffectManager.GetValue(CraftingTime) for {0}: tier={1} result={2}",
                    pid ?? "?", tier?.Perm ?? "(none)", __result);
            if (tier == null) return;
            float mult = Math.Max(0.05f, Math.Min(10f, tier.CraftTimeMult));
            __result *= mult;
        }
    }

    /// <summary>Server-side authoritative time scaling for workstation queues.
    /// When a workstation TE deserializes (which happens when client sends a
    /// queue update), walk every queue item with a known StartingEntityId
    /// and rescale its OneItemCraftTime + remaining CraftingTimeLeft to match
    /// the player's tier. This is what actually makes forges / workbenches
    /// grind faster — the engine processes them server-side using the cached
    /// time, not by re-querying EffectManager every tick.
    ///
    /// Idempotency: we tag each rescaled item by checking if OneItemCraftTime
    /// is already at the expected ratio, and skip if so. Without this, re-reads
    /// would compound (rescale a rescale).</summary>
    [HarmonyPatch(typeof(TileEntityWorkstation), "read")]
    static class TileEntityWorkstation_Read_Patch
    {
        static void Postfix(TileEntityWorkstation __instance)
        {
            if (_staticCfg == null || !_staticCfg.Enabled) return;
            var queue = __instance?.queue;
            if (queue == null) return;
            int scanned = 0, scaled = 0;
            for (int i = 0; i < queue.Length; i++)
            {
                var item = queue[i];
                if (item == null || item.Recipe == null) continue;
                scanned++;
                var tier = ResolveTierForEntity(item.StartingEntityId);
                if (tier == null) continue;
                float mult = Math.Max(0.05f, Math.Min(10f, tier.CraftTimeMult));
                if (mult >= 0.999f && mult <= 1.001f) continue;

                // Idempotency check: if OneItemCraftTime is already close to
                // recipe.craftingTime × mult, this item was already scaled.
                float expected = item.Recipe.craftingTime * mult;
                if (Math.Abs(item.OneItemCraftTime - expected) < 0.01f) continue;

                // Cache the queueing player so auto-shutdown has someone to
                // attribute the perm check to once the queue empties.
                // Key by world position because workstations are block TEs
                // (entityId == -1 for all of them).
                lock (_cacheLock)
                    _lastQueuerByPos[__instance.ToWorldPos()] = item.StartingEntityId;

                // Scale time-left proportionally so an in-progress craft
                // doesn't snap forward / backward.
                float ratio = expected / Math.Max(0.01f, item.OneItemCraftTime);
                item.OneItemCraftTime = expected;
                item.CraftingTimeLeft *= ratio;
                scaled++;
            }
            if (_diagLogs && scanned > 0)
                Log.Out("[StyxCrafting/diag] TE.read at te={0}: {1} queue item(s) scanned, {2} rescaled",
                    __instance.entityId, scanned, scaled);
        }
    }

    /// <summary>Bonus output. Fires after the engine processes a recipe
    /// completion. We compare the workstation's output array before vs after
    /// (well — after; we use the recipe's count + tier's mult). The engine
    /// just placed N copies; we add the bonus.
    ///
    /// Approach: prefix to capture the queue head's recipe + multiplier
    /// pre-tick; postfix to compare and add bonus copies for each multiplier
    /// step that completed. Bonus = floor(baseCount × mult) − baseCount per
    /// completion.</summary>
    [HarmonyPatch(typeof(TileEntityWorkstation), nameof(TileEntityWorkstation.HandleRecipeQueue))]
    static class TileEntityWorkstation_HandleRecipeQueue_Patch
    {
        static void Prefix(TileEntityWorkstation __instance,
                           out (Recipe recipe, int multiplier, int starterEid)? __state)
        {
            __state = null;
            var queue = __instance?.queue;
            if (queue == null || queue.Length == 0) return;
            var head = queue[queue.Length - 1];
            if (head?.Recipe == null) return;
            __state = (head.Recipe, head.Multiplier, head.StartingEntityId);
        }

        static void Postfix(TileEntityWorkstation __instance,
                            (Recipe recipe, int multiplier, int starterEid)? __state)
        {
            if (_staticCfg == null || !_staticCfg.Enabled) return;
            if (__state == null) return;
            var (recipe, preMult, starterEid) = __state.Value;

            // Refresh "last queuer" cache while we're here. Keyed by world
            // position — workstations are block TEs with entityId == -1.
            if (starterEid > 0)
            {
                lock (_cacheLock)
                    _lastQueuerByPos[__instance.ToWorldPos()] = starterEid;
            }

            var tier = ResolveTierForEntity(starterEid);
            if (tier == null) return;
            float mult = tier.OutputMult;
            if (mult <= 1.001f) return;  // no bonus

            // Figure out how many completions just happened.
            // After-tick state: queue head Multiplier may have decremented.
            // Or it may have been cycled (queue rotated); easier to look at
            // recipe-name-match on the current head.
            int completions = 0;
            var queue = __instance.queue;
            if (queue != null && queue.Length > 0)
            {
                var headNow = queue[queue.Length - 1];
                int nowMult = (headNow?.Recipe == recipe) ? headNow.Multiplier : 0;
                completions = preMult - nowMult;
                if (completions < 0) completions = 0;
            }
            else
            {
                completions = preMult;  // queue gone entirely → all completed
            }
            if (completions <= 0) return;

            int baseCountPerCompletion = Math.Max(1, recipe.count);
            int totalDelivered = baseCountPerCompletion * completions;
            int totalWanted = (int)Math.Floor(totalDelivered * mult);
            int bonus = totalWanted - totalDelivered;
            if (bonus <= 0) return;

            // Resolve the item value and stuff bonus copies into the
            // workstation output array, respecting stack limits.
            try
            {
                var iv = new ItemValue(recipe.itemValueType);
                ItemStack.AddToItemStackArray(__instance.output, new ItemStack(iv, bonus));
                __instance.setModified();
                if (_diagLogs)
                    Log.Out("[StyxCrafting/diag] Output bonus +{0} of {1} (tier {2}, completions {3})",
                        bonus, recipe.GetName(), tier.Perm, completions);
            }
            catch (Exception e)
            {
                Log.Warning("[StyxCrafting] Bonus output add failed: " + e.Message);
            }
        }
    }

    /// <summary>Workstation discovery hook. The actual auto-shutdown evaluation
    /// happens in <see cref="AutoShutdownSweep"/> on a 2s scheduler — see the
    /// rationale on <see cref="_knownWorkstations"/>. The engine appears to
    /// stop ticking workstations once they have no active work (no recipe
    /// queue + no raw material to smelt), even while they're still burning
    /// fuel idly — which is precisely the case auto-shutdown wants to catch.
    /// So this Postfix's only job is to register every workstation we ever
    /// see ticking, so the sweep knows where to look.</summary>
    [HarmonyPatch(typeof(TileEntityWorkstation), nameof(TileEntityWorkstation.UpdateTick))]
    static class TileEntityWorkstation_UpdateTick_Patch
    {
        // Heartbeat throttle: log once per ~10s per workstation when diag is
        // on, so we can still confirm "is this workstation being ticked at
        // all?" — separate from the auto-shutdown work which is now on the
        // scheduler-driven sweep. Cheap to leave at 20Hz: just a HashSet.Add
        // and a counter decrement.
        private static readonly Dictionary<Vector3i, int> _heartbeatCounter = new Dictionary<Vector3i, int>();
        private const int TicksBetweenHeartbeats = 200;  // ~10s at 20Hz

        static void Postfix(TileEntityWorkstation __instance)
        {
            if (_staticCfg == null || !_staticCfg.Enabled) return;
            if (__instance == null) return;

            var pos = __instance.ToWorldPos();
            lock (_cacheLock) _knownWorkstations.Add(pos);

            if (_diagLogs)
            {
                bool logHeartbeat;
                lock (_cacheLock)
                {
                    if (_heartbeatCounter.TryGetValue(pos, out int hb) && hb > 0)
                    {
                        _heartbeatCounter[pos] = hb - 1;
                        logHeartbeat = false;
                    }
                    else
                    {
                        _heartbeatCounter[pos] = TicksBetweenHeartbeats;
                        logHeartbeat = true;
                    }
                }
                if (logHeartbeat)
                {
                    bool hasModArr = __instance.isModuleUsed != null;
                    int modLen = hasModArr ? __instance.isModuleUsed.Length : -1;
                    bool fuelMod = (hasModArr && modLen > 3) ? __instance.isModuleUsed[3] : false;
                    Log.Out("[StyxCrafting/diag] UpdateTick HEARTBEAT pos={0} burning={1} moduleArrLen={2} fuelMod[3]={3} hasQueue={4}",
                        pos, __instance.isBurning, modLen, fuelMod, __instance.hasRecipeInQueue());
                }
            }
        }
    }
}
