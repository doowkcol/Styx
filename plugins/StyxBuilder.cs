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

// StyxBuilder v0.6.0 -- /repair, /upgrade, /downgrade on tracked
// player-placed blocks within the player's CURRENT land claim.
// v0.4   adds /m -> Builder picker UI (3-row stage 0 + confirm stage 1).
// v0.4.1 fixes upgraded/downgraded block falling out of tracker -- the new
//        BlockChangeInfo now carries changedByEntityId so the engine's
//        attribution path resolves addedByPlayer and re-tracks the new block.
// v0.4.2 fixes /downgrade finding zero eligible blocks. Block.DowngradeBlock
//        is the engine's damage-decay chain (rarely set on building tiers),
//        not the inverse of UpgradeBlock. We now build a reverse-upgrade map
//        from Block.list so /downgrade walks the upgrade chain backwards.
// v0.5.0 adds tier-aware UI flow: stage 0 (Types) lets the player pick which
//        block type in their claim to operate on, stage 1 (Actions) picks
//        repair/upgrade/downgrade, stage 2 (Confirm) executes. Solves the
//        mixed-tier base case where /upgrade was demanding concrete for the
//        cobble half of a base while the player only wanted to upgrade the
//        wood-frame half. Chat /repair /upgrade /downgrade unchanged --
//        scan still operates on every type when called without a filter.
// v0.5.1 collapses the type picker from per-block-id rows (24k labels) to
//        per-grade rows (6 tiers: Basic / Wood / Cobblestone / Concrete /
//        Steel / Other). Classification keyed off block.blockMaterial.id.
//        Big bases would have overflowed the picker with shape variants;
//        grade buckets stay 6 rows regardless.
// v0.5.2 excludes TileEntity blocks (storage crates, workstations, doors)
//        from the tier picker entirely. Wood Storage Crate had Mwood
//        material and was inflating the "Wood Blocks" row, then upgrading
//        to a steel crate when the player picked Upgrade. Chat /repair
//        without a filter still hits TE blocks since that path is
//        explicitly opt-in.
// v0.6.0 adds upgrade-cost discount perm tiers (.discount25/50/75) plus the
//        existing .free (100%). Highest tier wins. Adds Config.CostDowngrade
//        (default false) which charges downgrade the same as upgrading
//        INTO the target tier (with discount). styx.builder.upgrade.free
//        always grants free downgrade regardless of CostDowngrade.
//
// Operations:
//   /repair   scan|confirm|cancel|stats|debug -- damage=0 on every damaged
//             tracked block. Cost = sum(RepairItems * damage_ratio).
//   /upgrade  scan|confirm|cancel              -- swap each tracked block
//             for its Block.UpgradeBlock target. Cost = sum(UpgradeBlock.Item
//             * UpgradeBlock.ItemCount). "r" sentinel resolves to the
//             block's RepairItems[0].ItemName.
//   /downgrade scan|confirm|cancel             -- swap each tracked block
//             for its Block.DowngradeBlock target. Free; no refund.
//
// All three: must be standing in your own LCB. Materials draw from your
// owned containers within that claim. Each op has a separate perm:
//   styx.builder.use         -- launcher visibility
//   styx.builder.repair[.free]
//   styx.builder.upgrade[.free]
//   styx.builder.downgrade   -- (no .free; downgrade is already free)
//
// Architecture:
//   1. OnBlockPlaced (Styx.Core hook fired from a Block.OnBlockAdded
//      Postfix, filters to _addedByPlayer != null && !ischild) records
//      every player-placed parent block into a per-player position set.
//   2. OnBlockDestroyed scrubs the corresponding entry from whichever
//      player set owned it, so the tracker doesn't grow unbounded.
//   3. /repair reads the player's CURRENT WORLD POSITION, finds which
//      of THEIR LCBs (if any) they're standing inside, then intersects
//      the tracker set with that LCB's bounding square. Only those
//      blocks are considered. POI noise gone, off-claim blocks gone,
//      remote-trigger griefing impossible.
//   4. Resource debit pulls from the player's owned secured containers
//      INSIDE the same claim. styx.builder.repair.free skips the debit.
//
// Persistence:
//   data/StyxBuilder/<playerId>.json -- one file per player tracker.
//   Position set encoded as packed longs (16 bits per axis, signed).
//   Saves are dirty-batched on a 30s flush tick + on unload to avoid
//   thrashing disk on every block placement.
//
// Caveats (intentional):
//   - Blocks placed BEFORE this plugin loaded are not in the tracker
//     and therefore won't be repaired. Add an opt-in /repair claim-here
//     command later if operators want to bulk-adopt existing builds.
//   - Multiblock children (door top, vehicle parts) are filtered at
//     the Core hook -- the parent block IS tracked, the rest move with it.

using System;
using System.Collections.Generic;
using System.IO;
using Styx;
using Styx.Data;
using Styx.Plugins;
using Styx.Scheduling;

[Info("StyxBuilder", "Doowkcol", "0.6.0")]
public class StyxBuilder : StyxPlugin
{
    public override string Description => "Track + /repair /upgrade /downgrade player-placed blocks within their current land claim";

    private const string PermUse           = "styx.builder.use";
    private const string PermRepair        = "styx.builder.repair";
    private const string PermRepairFree    = "styx.builder.repair.free";
    private const string PermUpgrade       = "styx.builder.upgrade";
    private const string PermUpgradeFree   = "styx.builder.upgrade.free";
    // Upgrade-cost discount tiers. Granted by a single perm each; if a player
    // holds multiple, the highest discount wins. The .free perm above is the
    // 100% sentinel and takes priority over any discount perm. These also
    // apply to /downgrade when Config.CostDowngrade is enabled (downgrade
    // cost matches what it would have cost to upgrade INTO that target tier).
    private const string PermUpgradeDiscount25 = "styx.builder.upgrade.discount25";
    private const string PermUpgradeDiscount50 = "styx.builder.upgrade.discount50";
    private const string PermUpgradeDiscount75 = "styx.builder.upgrade.discount75";
    private const string PermDowngrade     = "styx.builder.downgrade";

    public enum BuilderOp { Repair, Upgrade, Downgrade }

    /// <summary>
    /// Coarse tier buckets for the type picker. Hundreds of shape variants
    /// per material would overflow the picker UI -- bucketing by tier keeps
    /// it to 5+1 rows regardless of how many shapes a base uses.
    /// Classification is keyed off block.blockMaterial.id (locale-stable).
    /// </summary>
    public enum BlockTier
    {
        Basic       = 0,  // woodFrame variants (Mwood_weak*)
        Wood        = 1,  // Mwood + Mwood_regular + MwoodReinforced + variants
        Cobblestone = 2,  // Mcobblestone + variants
        Concrete    = 3,  // Mconcrete / MrConcrete + variants
        Steel       = 4,  // Msteel / Mmetal_hard / Mmetal_medium + variants
        Other       = 5,  // anything else (deployables, crates, doors, ...)
    }
    private const int TierCount = 6;

    public class Config
    {
        // 0 = use vanilla GameStats.LandClaimSize. Override to scope smaller
        // for testing / tighter claims.
        public int ScanRadiusOverride = 0;

        // When true, ONLY pull from lockable containers (TileEntitySecure /
        // TileEntitySecureLootContainer) where the player is owner or on
        // the ACL. Useful on shared bases where you want to exclude your
        // base-mate's locked safes from your repair budget.
        // When false (default), every container in the LCB counts -- the
        // LCB ownership IS the security boundary. Standard player crates
        // (Wood Storage Crate etc.) carry no owner field; the engine
        // shows "(Locked)" on them via LCB protection rules, not via the
        // secure-container API. Filtering on ILockable would exclude all
        // player crates by default, which isn't what most operators want.
        public bool RequireSecureContainerOwnership = false;

        // Scan cache TTL in seconds. After this, /repair confirm fails.
        public int ScanResultTtlSeconds = 300;

        // Flush dirty tracker state to disk every N seconds.
        public int FlushIntervalSeconds = 30;

        // When true (default), /upgrade and /downgrade skip blocks that
        // carry a TileEntity (storage crates, workstations, doors, lights,
        // power source, etc.). Reasons:
        //   1. The new block gets a fresh TE, wiping lock state, owner ACL,
        //      and (depending on the block) the inventory.
        //   2. Operators explicitly chose THAT tier of crate / workstation;
        //      auto-upgrading it to "iron storage crate" is rarely intended.
        // Set false to allow TE-block upgrade if you genuinely want it
        // (e.g. crate-tier rollover at the start of a new wipe).
        public bool SkipTileEntityBlocksOnUpgrade = true;

        // When true, /downgrade costs the same as upgrading INTO the target
        // tier (after applying the player's discount perm, if any). This
        // closes a free-resource grind where players cycle upgrade ->
        // downgrade -> upgrade. Default false = downgrade is free, matching
        // historical behavior. styx.builder.upgrade.free always grants free
        // downgrade regardless of this flag.
        public bool CostDowngrade = false;

        public bool Verbose = true;
    }

    /// <summary>
    /// Per-player tracker state. PlacedPositions stores packed Vector3i
    /// longs (16 bits per axis -- enough for any vanilla world coord, ±32k).
    /// Saved to data/StyxBuilder/&lt;playerId&gt;.json keyed by PlatformId
    /// CombinedString.
    /// </summary>
    public class State
    {
        public Dictionary<string, HashSet<long>> PlacedPositions =
            new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
    }

    private class ScanResult
    {
        public BuilderOp Op;
        // Block positions to act on (damaged for Repair, upgradeable for
        // Upgrade, downgradeable for Downgrade).
        public List<Vector3i> Targets;
        // itemName -> count required. Empty for Downgrade (free).
        public Dictionary<string, int> Cost;
        public Vector3i LcbPos;
        public DateTime Timestamp;
    }

    private Config _cfg;
    private DataStore<State> _state;
    private TimerHandle _flushTick;
    private bool _dirty;
    // Per-player most-recent scan -- consumed by /repair confirm.
    private readonly Dictionary<string, ScanResult> _lastScan =
        new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);

    // ---- UI state ----
    // Three-stage flow:
    //   Stage 0 (Types):   pick a block type from the player's tracked-in-claim
    //                      types (e.g. "Wood Frame", "Cobblestone Block").
    //                      Filter is the entry-point so multi-tier bases can
    //                      be promoted in phases without needing concrete
    //                      to upgrade cobble while you're really just trying
    //                      to upgrade frames.
    //   Stage 1 (Actions): Repair / Upgrade / Downgrade applied to the
    //                      selected type.
    //   Stage 2 (Confirm): scan summary + confirm prompt.
    private const int UiStageTypes   = 0;
    private const int UiStageActions = 1;
    private const int UiStageConfirm = 2;
    private const int UiActionCount  = 3;     // Repair / Upgrade / Downgrade
    private const int MaxTypeRows    = 6;     // tier-picker row cap (matches TierCount)

    // No-filter sentinel for Scan(_, tierFilter) -- chat /repair etc.
    // calls scan with -1 to operate on every tracked tier at once.
    private const int NoTypeFilter   = -1;

    private const string CvOpen  = "styx.builder.open";
    private const string CvSel   = "styx.builder.sel";
    private const string CvStage = "styx.builder.stage";
    // CvOp: which op the confirm-stage panel is showing (0=Repair, 1=Upgrade,
    // 2=Downgrade). Drives visibility of the per-op verb header.
    private const string CvOp    = "styx.builder.op";
    // CvCount: eligible block count from the most recent UI-driven scan.
    // Substituted into the stage-2 prompt via {cvar(...)}.
    private const string CvCount = "styx.builder.count";
    // CvFree: 1 if the player's stage-2 confirm will skip resource debit
    // (downgrade always free, or .free perm for repair/upgrade).
    private const string CvFree  = "styx.builder.free";
    // CvTypeId: block type the player picked at stage 0 (-1 = no filter).
    private const string CvTypeId = "styx.builder.type_id";
    // CvTypeCount: number of types in the picker (drives row visibility).
    private const string CvTypeCount = "styx.builder.type_count";

    // Per-row cvars for stage 0 -- block id + tracked count for that row.
    // Block-name labels resolve via builder_block_<id> registered at OnLoad.
    private static string CvRowId(int row)    => "styx.builder.row" + row + "_id";
    private static string CvRowCount(int row) => "styx.builder.row" + row + "_count";

    // entityIds with the Builder UI currently open. Tracked separately from
    // the cvar so we can release input claims cleanly on plugin unload.
    private readonly HashSet<int> _uiOpenFor = new HashSet<int>();

    // Per-player snapshot of the type picker -- index-stable for the duration
    // of one open session so scroll selections map back to the right block id.
    private readonly Dictionary<int, List<TypeRow>> _typeSnapshot =
        new Dictionary<int, List<TypeRow>>();
    // BlockId field actually holds a BlockTier ordinal in v0.5.1+ (kept the
    // field name so the struct shape stayed stable for callers).
    private struct TypeRow { public int BlockId; public int Count; }

    public override void OnLoad()
    {
        _cfg   = StyxCore.Configs.Load<Config>(this);
        _state = this.Data.Store<State>("tracker");

        StyxCore.Perms.RegisterKnown(PermUse,
            "See the /m -> Builder launcher entry", Name);
        StyxCore.Perms.RegisterKnown(PermRepair,
            "Run /repair scan + /repair confirm on your tracked claim blocks", Name);
        StyxCore.Perms.RegisterKnown(PermRepairFree,
            "Skip the resource debit on /repair confirm", Name);
        StyxCore.Perms.RegisterKnown(PermUpgrade,
            "Run /upgrade scan + /upgrade confirm on your tracked claim blocks", Name);
        StyxCore.Perms.RegisterKnown(PermUpgradeFree,
            "Skip the resource debit on /upgrade confirm (and /downgrade when CostDowngrade is enabled)", Name);
        StyxCore.Perms.RegisterKnown(PermUpgradeDiscount25,
            "25% off upgrade cost (and downgrade cost when CostDowngrade is enabled)", Name);
        StyxCore.Perms.RegisterKnown(PermUpgradeDiscount50,
            "50% off upgrade cost (and downgrade cost when CostDowngrade is enabled)", Name);
        StyxCore.Perms.RegisterKnown(PermUpgradeDiscount75,
            "75% off upgrade cost (and downgrade cost when CostDowngrade is enabled)", Name);
        StyxCore.Perms.RegisterKnown(PermDowngrade,
            "Run /downgrade scan + /downgrade confirm on your tracked claim blocks", Name);

        StyxCore.Commands.Register("repair",
            "Repair YOUR damaged blocks in the claim you're standing in -- /repair [scan|confirm|cancel|stats|debug]",
            (ctx, args) => CmdBuilder(ctx, args, BuilderOp.Repair));
        StyxCore.Commands.Register("upgrade",
            "Upgrade YOUR upgradeable blocks in the claim you're standing in -- /upgrade [scan|confirm|cancel]",
            (ctx, args) => CmdBuilder(ctx, args, BuilderOp.Upgrade));
        StyxCore.Commands.Register("downgrade",
            "Downgrade YOUR downgradeable blocks in the claim you're standing in -- /downgrade [scan|confirm|cancel]",
            (ctx, args) => CmdBuilder(ctx, args, BuilderOp.Downgrade));

        // Register UI cvars as ephemeral so the panel doesn't resurrect itself
        // after a server restart (cvars persist in the .ttp save by default).
        Styx.Ui.Ephemeral.Register(CvOpen, CvSel, CvStage, CvOp, CvCount, CvFree,
            CvTypeId, CvTypeCount);
        for (int i = 0; i < MaxTypeRows; i++)
            Styx.Ui.Ephemeral.Register(CvRowId(i), CvRowCount(i));

        // Register tier-name labels (one per BlockTier) so the type-picker rows
        // can render a friendly tier name via
        // {#localization('builder_tier_' + cvar(...))}.
        // Labels staged for next boot via Mods/StyxRuntime/Config/Localization.txt.
        BuildTierLabels();

        // /build: shortcut chat command -> opens the UI directly. The
        // /repair /upgrade /downgrade commands above stay for power users
        // who want chat-driven flows.
        StyxCore.Commands.Register("build",
            "Open the Builder UI -- /build [labels]",
            (ctx, args) =>
            {
                if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
                var pid = ctx.Client.PlatformId?.CombinedString;
                if (string.IsNullOrEmpty(pid)) { ctx.Reply("[ff6666]Could not resolve player id.[-]"); return; }
                if (!StyxCore.Perms.HasPermission(pid, PermUse))
                { ctx.Reply("[ff6666]No permission '" + PermUse + "'.[-]"); return; }

                // Diagnostic for the EngineBridge: resolves each tier label
                // via the engine's Localization.Get (server-side) and echoes
                // it back. If the bridge is working, hot-reloading this
                // plugin with a renamed GetTierName() shows new values here
                // WITHOUT a server restart. /build labels.
                if (args.Length > 0 && args[0].Equals("labels", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Reply("[ccddff][Builder/labels] Server-side Localization.Get for builder_tier_0..5:[-]");
                    for (int t = 0; t < TierCount; t++)
                    {
                        string key = "builder_tier_" + t;
                        string val = Localization.Get(key);
                        ctx.Reply(string.Format("  [-] [ffffff]{0}[-] -> [ffffdd]{1}[-]", key, val));
                    }
                    return;
                }

                var p = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
                if (p == null) { ctx.Reply("Player not found."); return; }
                OpenBuilderUi(p);
                ctx.Reply("[00ff66]Builder open -- SCROLL navigate, LMB select, RMB back[-]");
            });

        // Top-level launcher entry. Gate by PermUse so the launcher hides
        // the row from players without builder access.
        Styx.Ui.Menu.Register(this,
            "Builder  /build",
            OpenBuilderUi,
            permission: PermUse);

        // Periodic flush of dirty tracker state. Block-placement events fire
        // a lot; saving on every one would thrash disk. Dirty-flag + 30s
        // batch is the standard pattern (StyxRewards uses the same shape).
        int flushSecs = Math.Max(5, _cfg.FlushIntervalSeconds);
        _flushTick = Scheduler.Every(flushSecs, FlushIfDirty, name: "StyxBuilder.flush");

        int totalTracked = 0;
        foreach (var s in _state.Value.PlacedPositions.Values) totalTracked += s.Count;
        Log.Out("[StyxBuilder] Loaded v0.6.0 -- {0} player(s) tracked, {1} block(s) total",
            _state.Value.PlacedPositions.Count, totalTracked);
    }

    public override void OnUnload()
    {
        _flushTick?.Destroy();
        _flushTick = null;
        FlushIfDirty();
        StyxCore.Perms.UnregisterKnownByOwner(Name);
        Styx.Ui.Menu.UnregisterAll(this);
        // Release every input claim we still hold so the framework's input
        // dispatcher stops calling us after unload.
        foreach (var eid in _uiOpenFor)
        {
            var p = StyxCore.Player.FindByEntityId(eid);
            if (p == null) continue;
            Styx.Ui.SetVar(p, CvOpen, 0f);
            Styx.Ui.Input.Release(p, Name);
        }
        _uiOpenFor.Clear();
        _lastScan.Clear();
    }

    private void FlushIfDirty()
    {
        if (!_dirty) return;
        _state.Save();
        _dirty = false;
    }

    // ============================================================ Hooks (auto-subscribed)

    /// <summary>
    /// Fires from Styx.Core's Block.OnBlockAdded patch. Already filtered
    /// to player-placed parent blocks at the source.
    /// </summary>
    void OnBlockPlaced(string playerId, Vector3i pos, BlockValue bv)
    {
        if (string.IsNullOrEmpty(playerId)) return;
        bool firstForThisPlayer = !_state.Value.PlacedPositions.ContainsKey(playerId);
        if (!_state.Value.PlacedPositions.TryGetValue(playerId, out var set))
        {
            set = new HashSet<long>();
            _state.Value.PlacedPositions[playerId] = set;
        }
        set.Add(PackPos(pos));
        _dirty = true;
        // Verbose log -- confirms the hook is wired and surfaces the block
        // name + ID form (Steam_ vs EOS_) for diagnosing missing placements.
        if (_cfg.Verbose || firstForThisPlayer)
        {
            string blockName = bv.Block?.GetBlockName() ?? "?";
            Log.Out("[StyxBuilder] Placed by '{0}' at {1} -- block={2}{3}",
                playerId, pos, blockName, firstForThisPlayer ? " (first for player)" : "");
        }
    }

    /// <summary>
    /// Block destroyed (mined to zero HP) -- scrub from whichever player
    /// tracker owned it. A position belongs to at most one player so we
    /// break on first match.
    /// </summary>
    void OnBlockDestroyed(Vector3i pos, BlockValue bv, int entityId, bool useHarvest)
    {
        ScrubFromAllTrackers(pos);
    }

    /// <summary>
    /// Block removed (wrench pickup, SetBlock-to-air, upgrade/downgrade
    /// transition, dynamite, etc.). OnBlockDestroyed only covers
    /// damage-to-zero kills; this catches everything else. For an
    /// upgrade in place, OnBlockRemoved fires for the old block and
    /// OnBlockPlaced re-fires for the new one at the same position --
    /// tracker stays consistent.
    /// </summary>
    void OnBlockRemoved(Vector3i pos, BlockValue bv)
    {
        ScrubFromAllTrackers(pos);
    }

    private void ScrubFromAllTrackers(Vector3i pos)
    {
        long packed = PackPos(pos);
        foreach (var set in _state.Value.PlacedPositions.Values)
        {
            if (set.Remove(packed)) { _dirty = true; break; }
        }
    }

    // ============================================================ Commands

    private void CmdBuilder(Styx.Commands.CommandContext ctx, string[] args, BuilderOp op)
    {
        if (ctx.Client == null) { ctx.Reply("Run from in-game chat."); return; }
        var pid = ctx.Client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("[ff6666]Could not resolve player id.[-]"); return; }
        string actionPerm = ActionPerm(op);
        if (!StyxCore.Perms.HasPermission(pid, actionPerm))
        {
            ctx.Reply("[ff6666]No permission '" + actionPerm + "'.[-]");
            return;
        }

        Action<string> reply = msg => ctx.Reply(msg);

        string cmd = OpCommandName(op);
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";
        switch (sub)
        {
            case "scan":    Scan(ctx.Client, op, NoTypeFilter, reply); return;
            case "confirm": Confirm(ctx.Client, op, reply); return;
            case "cancel":
                _lastScan.Remove(pid);
                ctx.Reply("[ffaa00]Scan cleared.[-]");
                return;
            case "stats":
            {
                int n = 0;
                foreach (var key in CandidateTrackerKeys(ctx.Client))
                    if (_state.Value.PlacedPositions.TryGetValue(key, out var s)) n += s.Count;
                ctx.Reply(string.Format("[ccddff][Builder] You have {0} tracked block(s) across all your placements.[-]", n));
                return;
            }
            case "debug":
                if (op == BuilderOp.Repair) { DebugContainers(ctx, pid); return; }
                ctx.Reply("Usage: /" + cmd + " [scan|confirm|cancel]"); return;
            default:
                if (op == BuilderOp.Repair)
                    ctx.Reply("Usage: /" + cmd + " [scan|confirm|cancel|stats|debug]");
                else
                    ctx.Reply("Usage: /" + cmd + " [scan|confirm|cancel]");
                return;
        }
    }

    private static string OpCommandName(BuilderOp op)
    {
        switch (op)
        {
            case BuilderOp.Repair:    return "repair";
            case BuilderOp.Upgrade:   return "upgrade";
            case BuilderOp.Downgrade: return "downgrade";
            default:                  return "?";
        }
    }

    private static string OpVerb(BuilderOp op)
    {
        switch (op)
        {
            case BuilderOp.Repair:    return "Repair";
            case BuilderOp.Upgrade:   return "Upgrade";
            case BuilderOp.Downgrade: return "Downgrade";
            default:                  return "?";
        }
    }

    private static string ActionPerm(BuilderOp op)
    {
        switch (op)
        {
            case BuilderOp.Repair:    return PermRepair;
            case BuilderOp.Upgrade:   return PermUpgrade;
            case BuilderOp.Downgrade: return PermDowngrade;
            default:                  return PermUse;
        }
    }

    /// <summary>
    /// Resolves whether an op is "free" for this player. Encapsulates:
    ///   - repair  -> styx.builder.repair.free
    ///   - upgrade -> styx.builder.upgrade.free
    ///   - downgrade ->
    ///       Config.CostDowngrade=false: always free (default)
    ///       Config.CostDowngrade=true:  free only if player has upgrade.free
    /// </summary>
    private bool IsFreeForOp(BuilderOp op, string pid)
    {
        switch (op)
        {
            case BuilderOp.Repair:
                return StyxCore.Perms.HasPermission(pid, PermRepairFree);
            case BuilderOp.Upgrade:
                return StyxCore.Perms.HasPermission(pid, PermUpgradeFree);
            case BuilderOp.Downgrade:
                if (!_cfg.CostDowngrade) return true;
                return StyxCore.Perms.HasPermission(pid, PermUpgradeFree);
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns the cost discount % a player gets for an op (0..100).
    /// 100 = entirely free (also covered by IsFreeForOp). Repair has no
    /// discount tiers -- only repair.free for binary 0/100.
    /// Upgrade and Downgrade share the upgrade-discount perm tree.
    /// </summary>
    private static int GetCostDiscountPercent(BuilderOp op, string pid)
    {
        if (op == BuilderOp.Repair)
            return StyxCore.Perms.HasPermission(pid, PermRepairFree) ? 100 : 0;
        // Upgrade & Downgrade
        if (StyxCore.Perms.HasPermission(pid, PermUpgradeFree))       return 100;
        if (StyxCore.Perms.HasPermission(pid, PermUpgradeDiscount75)) return 75;
        if (StyxCore.Perms.HasPermission(pid, PermUpgradeDiscount50)) return 50;
        if (StyxCore.Perms.HasPermission(pid, PermUpgradeDiscount25)) return 25;
        return 0;
    }

    /// <summary>
    /// Apply a percentage discount to a raw cost. Returns 0 only for 100%
    /// off; otherwise floors at 1 so partial discounts on tiny costs don't
    /// silently zero them out.
    /// </summary>
    private static int ApplyDiscount(int rawCost, int discountPct)
    {
        if (discountPct >= 100) return 0;
        if (discountPct <= 0)   return rawCost;
        long discounted = ((long)rawCost * (100 - discountPct)) / 100L;
        return discounted < 1 ? 1 : (int)discounted;
    }

    /// <summary>
    /// "Same cost as upgrading INTO this target tier." Used when
    /// Config.CostDowngrade is true: the downgrade target's predecessor's
    /// upgrade-cost is what's paid. Returns (null, 0) if the target has no
    /// predecessor in the upgrade chain (e.g. tier already at the bottom)
    /// -- in that case the downgrade is implicitly free even with the flag.
    /// </summary>
    private static (string item, int count) GetDowngradeCostMatchingUpgrade(BlockValue targetBv)
    {
        if (targetBv.isair) return (null, 0);
        var predecessor = GetReverseUpgrade(targetBv.type);
        if (predecessor.isair) return (null, 0);
        return GetUpgradeCost(predecessor.Block);
    }

    /// <summary>
    /// Yields every CombinedString form a player might be keyed under in
    /// the tracker dict. The Block.OnBlockAdded patch fires with whatever
    /// PPD.PrimaryId returns (EOS_xxx on crossplay, Steam_xxx on a pure
    /// Steam server), but the chat-command side knows the player as
    /// ClientInfo.PlatformId / CrossplatformId. Try them all.
    /// </summary>
    private static IEnumerable<string> CandidateTrackerKeys(ClientInfo ci)
    {
        if (ci == null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ci.PlatformId != null)
        {
            var k = ci.PlatformId.CombinedString;
            if (!string.IsNullOrEmpty(k) && seen.Add(k)) yield return k;
        }
        if (ci.CrossplatformId != null)
        {
            var k = ci.CrossplatformId.CombinedString;
            if (!string.IsNullOrEmpty(k) && seen.Add(k)) yield return k;
        }
        if (ci.InternalId != null)
        {
            var k = ci.InternalId.CombinedString;
            if (!string.IsNullOrEmpty(k) && seen.Add(k)) yield return k;
        }
    }

    // ============================================================ Scan

    /// <summary>
    /// Returns the resulting ScanResult on success (also stored in _lastScan
    /// keyed by pid). Returns null on any pre-flight failure (no LCB, not
    /// standing inside, etc.). UI callers check the return for null/eligible
    /// to decide whether to advance to the confirm stage.
    /// </summary>
    /// <param name="blockTypeFilter">If &gt;= 0, only blocks whose tier
    /// (GetBlockTier) matches will be considered. -1 = no filter (process
    /// every tracked block). The filter exists so a multi-tier base can be
    /// promoted in phases via the UI without the cost-summing loop demanding
    /// concrete for cobble blocks the player isn't trying to upgrade yet.</param>
    private ScanResult Scan(ClientInfo client, BuilderOp op, int blockTypeFilter, Action<string> reply)
    {
        if (client == null) { reply("[ff6666]Client missing.[-]"); return null; }
        var pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { reply("[ff6666]Could not resolve player id.[-]"); return null; }

        var player = StyxCore.Player.FindByEntityId(client.entityId);
        if (player == null) { reply("[ff6666]Player entity not found.[-]"); return null; }

        var lcbs = FindPlayerLCBs(pid);
        if (lcbs.Count == 0)
        {
            reply("[ff6666][Builder] No land claim found for you. Place an LCB first.[-]");
            return null;
        }

        int half = ResolveHalfSize();
        Vector3i playerPos = new Vector3i(
            (int)Math.Floor(player.position.x),
            (int)Math.Floor(player.position.y),
            (int)Math.Floor(player.position.z));

        Vector3i? containingLcb = FindContainingLCB(playerPos, lcbs, half);
        if (!containingLcb.HasValue)
        {
            reply("[ff6666][Builder] You must be standing inside one of your own land claims to use /" + OpCommandName(op) + ".[-]");
            return null;
        }
        var lcb = containingLcb.Value;

        HashSet<long> trackedAll = null;
        foreach (var key in CandidateTrackerKeys(client))
        {
            if (!_state.Value.PlacedPositions.TryGetValue(key, out var s)) continue;
            if (trackedAll == null) trackedAll = s;
            else foreach (var packed in s) trackedAll.Add(packed);
        }
        if (trackedAll == null || trackedAll.Count == 0)
        {
            reply("[ffaa00][Builder] No tracked placements yet. Place new blocks (after the plugin loaded) and try again.[-]");
            return null;
        }

        var world = GameManager.Instance?.World;
        if (world == null) { reply("[ff6666]World not loaded.[-]"); return null; }

        long startMs = NowMs();
        int inClaim = 0, eligibleCount = 0;
        var targets = new List<Vector3i>();
        var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<long>();

        foreach (var packed in trackedAll)
        {
            var pos = UnpackPos(packed);
            if (Math.Abs(pos.x - lcb.x) > half) continue;
            if (Math.Abs(pos.z - lcb.z) > half) continue;
            inClaim++;

            BlockValue bv;
            try { bv = world.GetBlock(pos); } catch { continue; }
            if (bv.isair) { stale.Add(packed); continue; }
            var b = bv.Block;
            if (b == null) continue;

            // Tier filter (UI-driven phased upgrades). -1 means no filter.
            // When a tier filter is in effect, we ALSO exclude TileEntity
            // blocks so the per-op behavior matches what the picker showed.
            // The picker counts non-TE blocks only -- if Scan included TE
            // blocks here, repair-via-UI would touch crates/doors that the
            // user didn't see in the row count. Chat /repair (no filter)
            // still hits TE blocks because that path is opt-in by command.
            if (blockTypeFilter >= 0)
            {
                if (GetBlockTier(b) != blockTypeFilter) continue;
                if (HasTileEntity(world, pos)) continue;
            }

            // Per-op eligibility + cost
            if (op == BuilderOp.Repair)
            {
                if (bv.damage <= 0) continue;
                if (b.MaxDamage <= 0 || b.RepairItems == null || b.RepairItems.Count == 0) continue;
                targets.Add(pos);
                eligibleCount++;
                float ratio = (float)bv.damage / b.MaxDamage;
                if (ratio > 1f) ratio = 1f;
                foreach (var ri in b.RepairItems)
                {
                    if (string.IsNullOrEmpty(ri.ItemName)) continue;
                    int cost = Math.Max(1, (int)Math.Ceiling(ri.Count * ratio));
                    AddCost(costs, ri.ItemName, cost);
                }
            }
            else if (op == BuilderOp.Upgrade)
            {
                if (b.UpgradeBlock.isair) continue;  // no upgrade target
                if (_cfg.SkipTileEntityBlocksOnUpgrade && HasTileEntity(world, pos)) continue;
                targets.Add(pos);
                eligibleCount++;
                var (item, count) = GetUpgradeCost(b);
                if (!string.IsNullOrEmpty(item) && count > 0)
                    AddCost(costs, item, count);
            }
            else if (op == BuilderOp.Downgrade)
            {
                // Use REVERSE-UPGRADE map (not Block.DowngradeBlock -- that's
                // damage decay, not the upgrade-chain inverse). Empty result
                // means this block tier has no predecessor in the chain.
                var rev = GetReverseUpgrade(b.blockID);
                if (rev.isair) continue;
                if (_cfg.SkipTileEntityBlocksOnUpgrade && HasTileEntity(world, pos)) continue;
                targets.Add(pos);
                eligibleCount++;
                if (_cfg.CostDowngrade)
                {
                    // Cost = upgrading INTO this target tier (per the same
                    // discount perm tree as upgrade). Discount applied later
                    // in aggregate so per-block min-1 floors don't compound.
                    var (item, count) = GetDowngradeCostMatchingUpgrade(rev);
                    if (!string.IsNullOrEmpty(item) && count > 0)
                        AddCost(costs, item, count);
                }
            }
        }

        if (stale.Count > 0)
        {
            foreach (var s in stale) trackedAll.Remove(s);
            _dirty = true;
        }

        long elapsedMs = NowMs() - startMs;

        var result = new ScanResult
        {
            Op = op,
            Targets = targets,
            Cost = costs,
            LcbPos = lcb,
            Timestamp = DateTime.UtcNow,
        };
        _lastScan[pid] = result;

        if (_cfg.Verbose)
            Log.Out("[StyxBuilder] {0} {1}-scan @ LCB ({2}) -- tracked={3}, in-claim={4}, eligible={5}, stale-pruned={6}, {7}ms",
                client.playerName, OpCommandName(op), lcb, trackedAll.Count, inClaim, eligibleCount, stale.Count, elapsedMs);

        reply(string.Format(
            "[ccddff][Builder] In-claim tracked: {0}, {1}able: {2} ({3}ms).[-]",
            inClaim, OpCommandName(op), eligibleCount, elapsedMs));

        if (eligibleCount == 0) return result;

        // Resolve free-state and discount BEFORE displaying costs. Discount
        // applies in aggregate so e.g. 75% off 4 small per-block costs of 1
        // each yields 1 total (min-1 floor) rather than 4 (per-block floor).
        bool free = IsFreeForOp(op, pid);
        int discountPct = GetCostDiscountPercent(op, pid);
        if (!free && discountPct > 0 && discountPct < 100)
        {
            var keys = new List<string>(costs.Keys);
            foreach (var k in keys) costs[k] = ApplyDiscount(costs[k], discountPct);
            // Persist the discounted costs into the saved scan so Confirm
            // debits the same numbers the player saw.
        }

        if (free)
        {
            string reason = (op == BuilderOp.Downgrade && !_cfg.CostDowngrade)
                ? "free downgrade (server default)"
                : "free perm detected";
            reply(string.Format("[00ff66]/{0} confirm to apply ({1}).[-]",
                OpCommandName(op), reason));
            return result;
        }

        if (costs.Count == 0)
        {
            // Not free, but no costs accumulated (e.g. blocks with no
            // upgrade-cost entry, or downgrade target has no predecessor).
            reply("[ffaa00]/" + OpCommandName(op) + " confirm to apply (no materials required).[-]");
            return result;
        }

        reply(string.Format("[ffffdd]Estimated {0} cost{1}:[-]",
            OpCommandName(op),
            discountPct > 0 ? string.Format(" ({0}% off)", discountPct) : ""));
        foreach (var kv in costs)
            reply(string.Format("  [-] [ffffff]{0}[-] x{1}", PrettyItemName(kv.Key), kv.Value));
        reply(string.Format("[ffaa00]/{0} confirm to apply (materials drawn from your secured containers in this claim).[-]",
            OpCommandName(op)));
        return result;
    }

    /// <summary>
    /// True if the block at <paramref name="pos"/> has a TileEntity --
    /// indicates "machinery" (storage, workstation, door, light, power
    /// source, etc.) whose state would be lost in a type-swap upgrade.
    /// </summary>
    private static bool HasTileEntity(WorldBase world, Vector3i pos)
    {
        if (world == null) return false;
        try { return world.GetTileEntity(0, pos) != null; }
        catch { return false; }
    }

    private static void AddCost(Dictionary<string, int> costs, string item, int amount)
    {
        if (string.IsNullOrEmpty(item) || amount <= 0) return;
        if (!costs.ContainsKey(item)) costs[item] = 0;
        costs[item] += amount;
    }

    /// <summary>
    /// Returns (itemName, count) for the upgrade cost, or (null, 0) if the
    /// block has no upgrade. Mirrors ItemActionRepair.GetUpgradeItemName +
    /// CanRemoveRequiredResource: "UpgradeBlock.Item" can be a literal item
    /// name or "r" sentinel meaning "use the block's RepairItems[0].ItemName".
    /// </summary>
    private static (string item, int count) GetUpgradeCost(Block block)
    {
        if (block == null || block.UpgradeBlock.isair) return (null, 0);
        string item = block.Properties?.Values["UpgradeBlock.Item"];
        if (item == "r" && block.RepairItems != null && block.RepairItems.Count > 0)
            item = block.RepairItems[0].ItemName;
        if (string.IsNullOrEmpty(item)) return (null, 0);
        if (!int.TryParse(block.Properties.Values[Block.PropUpgradeBlockClassItemCount], out int count) || count <= 0)
            return (null, 0);
        return (item, count);
    }

    // ============================================================ Reverse-upgrade map
    //
    // Block.DowngradeBlock is NOT the inverse of UpgradeBlock -- it's only
    // populated when the block XML explicitly sets <property name="DowngradeBlock">,
    // and is used by the engine for damage-decay transitions (e.g. concrete ->
    // rebar frame at low HP). Most building blocks have no DowngradeBlock at
    // all, so a literal use of `block.DowngradeBlock` returns air on basically
    // every wood/stone/concrete tier.
    //
    // What players expect "/downgrade" to do is reverse the upgrade chain --
    // i.e. if A.UpgradeBlock == B then B downgrades to A. We build that map
    // ourselves by iterating Block.list once and indexing on UpgradeBlock.type.

    private static Dictionary<int, BlockValue> _reverseUpgradeMap;
    private static readonly object _reverseUpgradeMapLock = new object();

    /// <summary>
    /// Returns the BlockValue that this block's predecessor would upgrade INTO,
    /// or BlockValue.Air if there isn't one. Lazily builds + caches the map on
    /// first call -- the map only depends on Block.list which is immutable
    /// after world load.
    /// </summary>
    private static BlockValue GetReverseUpgrade(int blockType)
    {
        if (_reverseUpgradeMap == null)
        {
            lock (_reverseUpgradeMapLock)
            {
                if (_reverseUpgradeMap == null)
                {
                    var map = new Dictionary<int, BlockValue>();
                    var list = Block.list;
                    if (list != null)
                    {
                        foreach (var b in list)
                        {
                            if (b == null) continue;
                            if (b.UpgradeBlock.isair) continue;
                            if (b.UpgradeBlock.type == b.blockID) continue;  // self-loop
                            // First hit wins. Multiple predecessors are rare; we
                            // could later refine by preferring the predecessor
                            // that shares a class/material if needed.
                            if (map.ContainsKey(b.UpgradeBlock.type)) continue;
                            map[b.UpgradeBlock.type] = new BlockValue((uint)b.blockID);
                        }
                    }
                    _reverseUpgradeMap = map;
                    Log.Out("[StyxBuilder] Reverse-upgrade map built -- {0} entries", map.Count);
                }
            }
        }
        return _reverseUpgradeMap.TryGetValue(blockType, out var rev) ? rev : BlockValue.Air;
    }

    // ============================================================ Confirm

    private void Confirm(ClientInfo client, BuilderOp op, Action<string> reply)
    {
        if (client == null) { reply("[ff6666]Client missing.[-]"); return; }
        var pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { reply("[ff6666]Could not resolve player id.[-]"); return; }

        if (!_lastScan.TryGetValue(pid, out var scan))
        {
            reply("[ffaa00][Builder] No scan to confirm. Run /" + OpCommandName(op) + " scan first.[-]");
            return;
        }
        if (scan.Op != op)
        {
            reply("[ffaa00][Builder] Last scan was a /" + OpCommandName(scan.Op) +
                ". Run /" + OpCommandName(op) + " scan first.[-]");
            return;
        }
        if ((DateTime.UtcNow - scan.Timestamp).TotalSeconds > _cfg.ScanResultTtlSeconds)
        {
            _lastScan.Remove(pid);
            reply("[ffaa00][Builder] Scan expired (>" + _cfg.ScanResultTtlSeconds + "s). Re-run /" + OpCommandName(op) + " scan.[-]");
            return;
        }
        if (scan.Targets.Count == 0)
        {
            reply("[ffaa00][Builder] Last scan found nothing to " + OpCommandName(op) + ".[-]");
            return;
        }

        bool free = IsFreeForOp(op, pid);
        // Downgrade can now have a cost too (when CostDowngrade=true), so the
        // old `op != BuilderOp.Downgrade` filter is gone. scan.Cost values
        // already reflect the discount applied at scan time.
        bool needsResources = !free && scan.Cost.Count > 0;

        List<StorageRef> containers = null;
        if (needsResources)
        {
            var ownerKeys = new HashSet<string>(CandidateTrackerKeys(client),
                StringComparer.OrdinalIgnoreCase);
            containers = FindOwnedContainersInLCB(ownerKeys, scan.LcbPos);
            if (containers.Count == 0)
            {
                reply("[ff6666][Builder] No owned secured containers found in this claim. Place a locked chest with materials inside.[-]");
                return;
            }

            var available = SumResources(containers);
            foreach (var kv in scan.Cost)
            {
                int have = available.TryGetValue(kv.Key, out var a) ? a : 0;
                if (have < kv.Value)
                {
                    reply(string.Format(
                        "[ff6666][Builder] Not enough '{0}' -- need {1}, have {2}. Aborting.[-]",
                        PrettyItemName(kv.Key), kv.Value, have));
                    return;
                }
            }

            foreach (var kv in scan.Cost)
            {
                int remaining = kv.Value;
                foreach (var ct in containers)
                {
                    if (remaining <= 0) break;
                    remaining -= TakeFromContainer(ct, kv.Key, remaining);
                }
                if (remaining > 0)
                    Log.Warning("[StyxBuilder] Resource debit miscount for '{0}' -- {1} short after pre-flight pass.",
                        kv.Key, remaining);
            }
        }

        var world = GameManager.Instance?.World;
        if (world == null) { reply("[ff6666]World not loaded.[-]"); return; }

        int applied = 0;
        long startMs = NowMs();
        var changes = new List<BlockChangeInfo>();
        foreach (var pos in scan.Targets)
        {
            BlockValue oldBv;
            try { oldBv = world.GetBlock(pos); } catch { continue; }
            if (oldBv.isair) continue;
            var oldBlock = oldBv.Block;
            if (oldBlock == null) continue;

            BlockChangeInfo change;
            if (op == BuilderOp.Repair)
            {
                if (oldBv.damage <= 0) continue;
                var bv = oldBv;
                bv.damage = 0;
                change = new BlockChangeInfo(pos, bv, _updateLight: false, _bOnlyDamage: true);
            }
            else if (op == BuilderOp.Upgrade)
            {
                if (oldBlock.UpgradeBlock.isair) continue;
                var newBv = oldBlock.UpgradeBlock;
                newBv.rotation = oldBv.rotation;
                newBv.meta = oldBv.meta;
                newBv.damage = 0;
                // Light updates may matter for upgrade (different block shape /
                // material affects light propagation). Let the engine recompute.
                // Pass changedByEntityId so the engine resolves addedByPlayer
                // via PPD lookup -- our Block.OnBlockAdded patch then re-tracks
                // the upgraded block under the same player. Without this the
                // new block falls out of the tracker (old-block OnBlockRemoved
                // scrubs the position; new-block OnBlockAdded sees null player
                // and is filtered out by the patch).
                change = new BlockChangeInfo(0, pos, newBv,
                    _updateLight: true, _changingEntityId: client.entityId);
            }
            else // Downgrade
            {
                var newBv = GetReverseUpgrade(oldBlock.blockID);
                if (newBv.isair) continue;
                newBv.rotation = oldBv.rotation;
                newBv.meta = oldBv.meta;
                newBv.damage = 0;
                change = new BlockChangeInfo(0, pos, newBv,
                    _updateLight: true, _changingEntityId: client.entityId);
            }
            changes.Add(change);
            applied++;
        }
        if (changes.Count > 0)
        {
            try
            {
                GameManager.Instance.SetBlocksRPC(changes);
            }
            catch (Exception e)
            {
                Log.Warning("[StyxBuilder] SetBlocksRPC failed: {0}", e.Message);
                applied = 0;
            }
        }
        long elapsedMs = NowMs() - startMs;

        _lastScan.Remove(pid);

        string costNote;
        if (free)
        {
            costNote = (op == BuilderOp.Downgrade && !_cfg.CostDowngrade)
                ? " (free downgrade)"
                : " (free)";
        }
        else
        {
            costNote = " (materials deducted)";
        }

        reply(string.Format("[00ff66][Builder] {0}d {1} block(s){2} in {3}ms.[-]",
            OpVerb(op), applied, costNote, elapsedMs));
        if (_cfg.Verbose)
            Log.Out("[StyxBuilder] {0} {1}d {2}/{3} block(s) in {4}ms (free={5}, containers={6})",
                client.playerName, OpVerb(op).ToLowerInvariant(), applied, scan.Targets.Count, elapsedMs, free,
                containers != null ? containers.Count : 0);
    }

    // ============================================================ UI lifecycle

    /// <summary>
    /// Opens the Builder picker at stage 0 (type picker). Builds a per-player
    /// snapshot of which block TYPES the player has tracked inside their
    /// current claim, ordered by descending count. From there:
    ///   stage 0 (Types):   scroll picks a type, LMB advances to stage 1
    ///   stage 1 (Actions): scroll picks Repair/Upgrade/Downgrade, LMB scans
    ///                      and (if eligible) advances to stage 2
    ///   stage 2 (Confirm): LMB confirms, RMB returns to stage 1
    /// At any stage, RMB returns one stage up (stage 0 RMB closes + reopens
    /// the launcher).
    /// </summary>
    private void OpenBuilderUi(EntityPlayer p)
    {
        if (p == null) return;

        // Reset cvars
        Styx.Ui.SetVar(p, CvOpen,  1f);
        Styx.Ui.SetVar(p, CvStage, UiStageTypes);
        Styx.Ui.SetVar(p, CvSel,   0);
        Styx.Ui.SetVar(p, CvOp,    0);
        Styx.Ui.SetVar(p, CvCount, 0);
        Styx.Ui.SetVar(p, CvFree,  0);
        Styx.Ui.SetVar(p, CvTypeId, NoTypeFilter);

        // Build the type-picker snapshot. Stays index-stable until close so
        // OnPlayerInput's selection maps back to the right block id.
        var rows = BuildTypeRows(p);
        _typeSnapshot[p.entityId] = rows;

        // Push row data into cvars; pad unused rows with -1 so visibility
        // bindings can hide them cleanly.
        Styx.Ui.SetVar(p, CvTypeCount, rows.Count);
        for (int i = 0; i < MaxTypeRows; i++)
        {
            if (i < rows.Count)
            {
                Styx.Ui.SetVar(p, CvRowId(i),    rows[i].BlockId);
                Styx.Ui.SetVar(p, CvRowCount(i), rows[i].Count);
            }
            else
            {
                Styx.Ui.SetVar(p, CvRowId(i),    -1);
                Styx.Ui.SetVar(p, CvRowCount(i), 0);
            }
        }

        Styx.Ui.Input.Acquire(p, Name);
        _uiOpenFor.Add(p.entityId);

        if (rows.Count == 0)
        {
            // No tracked types in claim -- whisper a hint and let them RMB out.
            Styx.Server.Whisper(p, "[ffaa00][Builder] No tracked blocks found in your current claim. Place new blocks (after the plugin loaded) or stand inside one of your land claims.[-]");
        }
    }

    private void CloseBuilderUi(EntityPlayer p)
    {
        if (p == null) return;
        Styx.Ui.SetVar(p, CvOpen,  0f);
        Styx.Ui.SetVar(p, CvStage, UiStageTypes);
        Styx.Ui.Input.Release(p, Name);
        _uiOpenFor.Remove(p.entityId);
        _typeSnapshot.Remove(p.entityId);
    }

    /// <summary>
    /// Hook-bus auto-subscription. Fires for every player whose inputs we hold;
    /// we filter on CvOpen so other plugins' menus don't double-handle events.
    /// </summary>
    void OnPlayerInput(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        if (p?.Buffs == null) return;
        if ((int)p.Buffs.GetCustomVar(CvOpen) != 1) return;

        int stage = (int)p.Buffs.GetCustomVar(CvStage);
        int sel   = (int)p.Buffs.GetCustomVar(CvSel);

        switch (stage)
        {
            case UiStageTypes:   HandleStageTypes(p, kind, sel);   break;
            case UiStageActions: HandleStageActions(p, kind, sel); break;
            case UiStageConfirm: HandleStageConfirm(p, kind);      break;
        }
    }

    private void HandleStageTypes(EntityPlayer p, Styx.Ui.StyxInputKind kind, int sel)
    {
        if (!_typeSnapshot.TryGetValue(p.entityId, out var rows) || rows.Count == 0)
        {
            // Empty picker: only RMB does anything.
            if (kind == Styx.Ui.StyxInputKind.SecondaryAction) BackToLauncher(p);
            return;
        }
        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                Styx.Ui.SetVar(p, CvSel, (sel + 1) % rows.Count);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                Styx.Ui.SetVar(p, CvSel, (sel - 1 + rows.Count) % rows.Count);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                if (sel < 0 || sel >= rows.Count) return;
                Styx.Ui.SetVar(p, CvTypeId, rows[sel].BlockId);
                Styx.Ui.SetVar(p, CvSel,    0);
                Styx.Ui.SetVar(p, CvStage,  UiStageActions);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                BackToLauncher(p);
                break;
        }
    }

    private void HandleStageActions(EntityPlayer p, Styx.Ui.StyxInputKind kind, int sel)
    {
        switch (kind)
        {
            case Styx.Ui.StyxInputKind.Jump:
                Styx.Ui.SetVar(p, CvSel, (sel + 1) % UiActionCount);
                break;
            case Styx.Ui.StyxInputKind.Crouch:
                Styx.Ui.SetVar(p, CvSel, (sel - 1 + UiActionCount) % UiActionCount);
                break;
            case Styx.Ui.StyxInputKind.PrimaryAction:
                ScanFromUi(p, (BuilderOp)sel);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to type picker.
                Styx.Ui.SetVar(p, CvSel,   0);
                Styx.Ui.SetVar(p, CvStage, UiStageTypes);
                break;
        }
    }

    private void HandleStageConfirm(EntityPlayer p, Styx.Ui.StyxInputKind kind)
    {
        int opInt = (int)p.Buffs.GetCustomVar(CvOp);
        var op = (BuilderOp)opInt;
        switch (kind)
        {
            case Styx.Ui.StyxInputKind.PrimaryAction:
                ConfirmFromUi(p, op);
                CloseBuilderUi(p);
                break;
            case Styx.Ui.StyxInputKind.SecondaryAction:
                // Back to action picker without executing. CvOp / CvCount
                // are stale at this point but they're stage-2-gated in XML
                // so they don't render.
                Styx.Ui.SetVar(p, CvStage, UiStageActions);
                break;
        }
    }

    private void BackToLauncher(EntityPlayer p)
    {
        CloseBuilderUi(p);
        Styx.Scheduling.Scheduler.Once(0.05,
            () => Styx.Ui.Menu.OpenLauncher?.Invoke(p),
            name: "StyxBuilder.BackToLauncher");
    }

    /// <summary>
    /// UI->Scan bridge. Resolves ClientInfo, runs the perm gate, then calls
    /// the shared Scan with whisper-as-reply, scoping to the type the player
    /// picked at stage 0. On success, hoists eligible count + op into cvars
    /// and switches the UI to stage 2.
    /// </summary>
    private void ScanFromUi(EntityPlayer p, BuilderOp op)
    {
        var client = StyxCore.Player.ClientOf(p);
        if (client == null)
        {
            Styx.Server.Whisper(p, "[ff6666][Builder] Client not found.[-]");
            return;
        }
        var pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid))
        {
            Styx.Server.Whisper(p, "[ff6666][Builder] Could not resolve player id.[-]");
            return;
        }
        string actionPerm = ActionPerm(op);
        if (!StyxCore.Perms.HasPermission(pid, actionPerm))
        {
            Styx.Server.Whisper(p, "[ff6666][Builder] You lack '" + actionPerm + "'.[-]");
            return;
        }

        int typeFilter = (int)p.Buffs.GetCustomVar(CvTypeId);
        var result = Scan(client, op, typeFilter, msg => Styx.Server.Whisper(p, msg));
        if (result == null) return;
        if (result.Targets.Count == 0) return;  // stay on action stage

        bool free = IsFreeForOp(op, pid);

        Styx.Ui.SetVar(p, CvOp,    (int)op);
        Styx.Ui.SetVar(p, CvCount, result.Targets.Count);
        Styx.Ui.SetVar(p, CvFree,  free ? 1 : 0);
        Styx.Ui.SetVar(p, CvStage, UiStageConfirm);
    }

    private void ConfirmFromUi(EntityPlayer p, BuilderOp op)
    {
        var client = StyxCore.Player.ClientOf(p);
        if (client == null)
        {
            Styx.Server.Whisper(p, "[ff6666][Builder] Client not found.[-]");
            return;
        }
        Confirm(client, op, msg => Styx.Server.Whisper(p, msg));
    }

    // ============================================================ Type picker

    /// <summary>
    /// Walk the player's tracker positions, intersect with the LCB they're
    /// currently standing in, and group by TIER (not block id). One row per
    /// BlockTier with at least one tracked block. Ordered by tier ordinal so
    /// the picker reads top-down: Basic, Wood, Cobble, Concrete, Steel, Other.
    /// Empty if no LCB or no tracked blocks in the claim.
    /// </summary>
    private List<TypeRow> BuildTypeRows(EntityPlayer p)
    {
        var rows = new List<TypeRow>();
        var client = StyxCore.Player.ClientOf(p);
        if (client == null) return rows;
        var pid = client.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) return rows;

        var lcbs = FindPlayerLCBs(pid);
        if (lcbs.Count == 0) return rows;

        int half = ResolveHalfSize();
        Vector3i playerPos = new Vector3i(
            (int)Math.Floor(p.position.x),
            (int)Math.Floor(p.position.y),
            (int)Math.Floor(p.position.z));
        var containingLcb = FindContainingLCB(playerPos, lcbs, half);
        if (!containingLcb.HasValue) return rows;
        var lcb = containingLcb.Value;

        var ownerKeys = new HashSet<string>(CandidateTrackerKeys(client),
            StringComparer.OrdinalIgnoreCase);
        var trackedAll = CollectTrackerPositions(ownerKeys);
        if (trackedAll.Count == 0) return rows;

        var world = GameManager.Instance?.World;
        if (world == null) return rows;

        var tierCounts = new int[TierCount];
        foreach (var packed in trackedAll)
        {
            var pos = UnpackPos(packed);
            if (Math.Abs(pos.x - lcb.x) > half) continue;
            if (Math.Abs(pos.z - lcb.z) > half) continue;

            BlockValue bv;
            try { bv = world.GetBlock(pos); } catch { continue; }
            if (bv.isair) continue;
            var b = bv.Block;
            if (b == null) continue;

            // TileEntity blocks (storage crates, workstations, doors,
            // lights, generators, ...) are never building blocks, so we
            // don't want them inflating tier counts. A wood storage crate
            // has Mwood material and would otherwise show up under
            // "Wood Blocks x1" -- which is misleading and dangerous
            // because picking that tier and selecting Upgrade would
            // wipe the crate's lock state + inventory. Use chat /repair
            // (no filter) for TE block repair.
            if (HasTileEntity(world, pos)) continue;

            int tier = GetBlockTier(b);
            if (tier >= 0 && tier < TierCount) tierCounts[tier]++;
        }

        for (int t = 0; t < TierCount; t++)
        {
            if (tierCounts[t] == 0) continue;
            rows.Add(new TypeRow { BlockId = t, Count = tierCounts[t] });
        }
        // Already sorted by tier ordinal -- intentional, not by count, so
        // the player gets a stable top-down reading: Basic -> ... -> Other.
        if (rows.Count > MaxTypeRows) rows = rows.GetRange(0, MaxTypeRows);
        return rows;
    }

    /// <summary>
    /// Bucket a Block into a coarse tier by its material id. The tier picker
    /// shows ONE row per tier instead of one per block-id (which would
    /// overflow the UI on bases with many shape variants). Locale-stable
    /// because material ids are XML keys, not localized strings.
    /// </summary>
    private static int GetBlockTier(Block b)
    {
        if (b?.blockMaterial == null) return (int)BlockTier.Other;
        string m = b.blockMaterial.id;
        if (string.IsNullOrEmpty(m)) return (int)BlockTier.Other;

        // Order matters: Mwood_weak must be matched BEFORE Mwood prefix.
        if (m.StartsWith("Mwood_weak",   StringComparison.Ordinal)) return (int)BlockTier.Basic;
        if (m.StartsWith("Mwood",        StringComparison.Ordinal)) return (int)BlockTier.Wood;
        if (m.StartsWith("Mcobblestone", StringComparison.Ordinal)) return (int)BlockTier.Cobblestone;
        if (m.StartsWith("Mconcrete",    StringComparison.Ordinal)) return (int)BlockTier.Concrete;
        if (m.StartsWith("MrConcrete",   StringComparison.Ordinal)) return (int)BlockTier.Concrete;
        if (m.StartsWith("Msteel",       StringComparison.Ordinal)) return (int)BlockTier.Steel;
        // Hard/medium metal alloys land in Steel; rebar/thin/weak metal stay
        // in Other so deployable furniture (catwalks, ladders) doesn't get
        // wrongly filed as a primary building tier.
        if (m.Equals("Mmetal_hard",   StringComparison.Ordinal)) return (int)BlockTier.Steel;
        if (m.Equals("Mmetal_medium", StringComparison.Ordinal)) return (int)BlockTier.Steel;
        return (int)BlockTier.Other;
    }

    private static string GetTierName(int tier)
    {
        switch (tier)
        {
            case (int)BlockTier.Basic:       return "Basic Blocks";
            case (int)BlockTier.Wood:        return "Wood Blocks";
            case (int)BlockTier.Cobblestone: return "Cobblestone Blocks";
            case (int)BlockTier.Concrete:    return "Concrete Blocks";
            case (int)BlockTier.Steel:       return "Steel Blocks";
            case (int)BlockTier.Other:       return "Other (deployables, doors, ...)";
            default:                         return "?";
        }
    }

    /// <summary>
    /// Register one builder_tier_&lt;id&gt; localization label per BlockTier
    /// value. Replaces the v0.5.0 per-block-id labels that bloated the
    /// runtime localization to 24k entries -- we only need ~6 now. Stale
    /// entries from a prior plugin instance are migrated to this owner and
    /// then unregistered, so the runtime file shrinks back down on next
    /// persist. Labels load into 7DTD's Localization dict on the NEXT
    /// server boot.
    /// </summary>
    private void BuildTierLabels()
    {
        // Hot-reload: the previous plugin instance owned its labels under a
        // different `this` reference, so a plain UnregisterAll(this) won't
        // touch them. Re-register each legacy builder_block_<id> under THIS
        // instance (empty text), then UnregisterAll drops them cleanly.
        // Gated on AllRegistered seeing legacy keys so subsequent reloads
        // skip the iteration.
        var existing = Styx.Ui.Labels.AllRegistered();
        bool hasLegacy = false;
        foreach (var key in existing.Keys)
        {
            if (key.StartsWith("builder_block_", StringComparison.Ordinal))
            { hasLegacy = true; break; }
        }
        if (hasLegacy)
        {
            int migrated = 0;
            foreach (var key in existing.Keys)
            {
                if (!key.StartsWith("builder_block_", StringComparison.Ordinal)) continue;
                Styx.Ui.Labels.Register(this, key, "");
                migrated++;
            }
            Styx.Ui.Labels.UnregisterAll(this);
            Log.Out("[StyxBuilder] Migrated and dropped {0} legacy builder_block_* labels", migrated);
        }

        for (int t = 0; t < TierCount; t++)
            Styx.Ui.Labels.Register(this, "builder_tier_" + t, GetTierName(t));

        Log.Out("[StyxBuilder] Tier labels registered: {0}", TierCount);
    }

    // ============================================================ Debug dump

    /// <summary>
    /// /repair debug -- enumerate every loot-container TE in the player's
    /// current LCB and report its class + lock state + owner ID. Lets you
    /// see exactly what the engine has stored on each crate vs what the
    /// command side expects to find.
    /// </summary>
    private void DebugContainers(Styx.Commands.CommandContext ctx, string pid)
    {
        var player = StyxCore.Player.FindByEntityId(ctx.Client.entityId);
        if (player == null) { ctx.Reply("[ff6666]Player entity not found.[-]"); return; }
        var lcbs = FindPlayerLCBs(pid);
        if (lcbs.Count == 0) { ctx.Reply("[ff6666]No LCB found.[-]"); return; }

        int half = ResolveHalfSize();
        var playerPos = new Vector3i((int)Math.Floor(player.position.x), (int)Math.Floor(player.position.y), (int)Math.Floor(player.position.z));
        var lcb = FindContainingLCB(playerPos, lcbs, half);
        if (!lcb.HasValue) { ctx.Reply("[ff6666]Stand inside your LCB to debug it.[-]"); return; }

        var ownerKeys = new HashSet<string>(CandidateTrackerKeys(ctx.Client), StringComparer.OrdinalIgnoreCase);
        ctx.Reply(string.Format("[ccddff][Builder/debug] LCB ({0}) -- candidate owner IDs: {1}[-]",
            lcb.Value, string.Join(", ", ownerKeys)));

        var world = GameManager.Instance?.World;
        if (world == null) return;

        int minCx = (lcb.Value.x - half) >> 4;
        int maxCx = (lcb.Value.x + half) >> 4;
        int minCz = (lcb.Value.z - half) >> 4;
        int maxCz = (lcb.Value.z + half) >> 4;
        int containerCount = 0, lockableCount = 0;
        for (int cx = minCx; cx <= maxCx; cx++)
        for (int cz = minCz; cz <= maxCz; cz++)
        {
            Chunk chunk;
            try { chunk = world.GetChunkSync(cx, cz) as Chunk; } catch { chunk = null; }
            if (chunk == null) continue;
            var tes = chunk.GetTileEntities();
            if (tes == null) continue;
            foreach (var te in tes.list)
            {
                if (te == null) continue;
                Vector3i wp;
                try { wp = te.ToWorldPos(); } catch { continue; }
                if (Math.Abs(wp.x - lcb.Value.x) > half || Math.Abs(wp.z - lcb.Value.z) > half) continue;

                // Skip TEs that aren't storage of any kind (signs, particle
                // emitters, NPC spawners, etc.).
                var sref = AsStorageRef(te);
                if (!sref.HasValue) continue;
                var s = sref.Value;
                containerCount++;
                if (s.Lockable != null) lockableCount++;

                var blockHere = world.GetBlock(wp);
                string blockName = blockHere.isair ? "(air)" : (blockHere.Block?.GetBlockName() ?? "?");
                string typeName = te.GetType().Name;
                string ownerStr = "(null)";
                if (s.Lockable != null)
                {
                    var owner = s.Lockable.GetOwner();
                    ownerStr = owner != null ? owner.CombinedString : "(null)";
                }

                int slotsUsed = 0;
                var itemSummary = new List<string>();
                if (s.Items != null)
                {
                    foreach (var stk in s.Items)
                    {
                        if (stk == null || stk.IsEmpty() || stk.itemValue == null) continue;
                        slotsUsed++;
                        var ic = ItemClass.GetForId(stk.itemValue.type);
                        string nm = ic != null ? ic.GetItemName() : "(itemId " + stk.itemValue.type + ")";
                        itemSummary.Add(nm + "x" + stk.count);
                    }
                }
                bool acceptedByCurrentRules =
                    !(_cfg.RequireSecureContainerOwnership && (s.Lockable == null || !IsContainerOwnedByPlayer(s.Lockable, ownerKeys)))
                    && s.Items != null && s.Items.Length > 0;
                ctx.Reply(string.Format("  [-] {0} @ ({1},{2},{3}) te={4} block={5} owner={6} accepted={7} slots={8}/{9} items={10}",
                    s.Lockable != null ? "[lockable]" : "[unsecured]",
                    wp.x, wp.y, wp.z, typeName, blockName, ownerStr,
                    acceptedByCurrentRules ? "[00ff66]YES[-]" : "[ff6666]NO[-]",
                    slotsUsed,
                    s.Items != null ? s.Items.Length : 0,
                    itemSummary.Count > 0 ? string.Join(",", itemSummary) : "(empty)"));
            }
        }
        ctx.Reply(string.Format("[ccddff][Builder/debug] Total: {0} container(s), {1} lockable. RequireSecureContainerOwnership={2}.[-]",
            containerCount, lockableCount, _cfg.RequireSecureContainerOwnership));
    }

    // ============================================================ LCB lookup

    private int ResolveHalfSize()
    {
        int radius = _cfg.ScanRadiusOverride;
        if (radius <= 0)
        {
            radius = GameStats.GetInt(EnumGameStats.LandClaimSize);
            if (radius <= 0) radius = 41;
        }
        return (radius - 1) / 2;
    }

    private List<Vector3i> FindPlayerLCBs(string playerId)
    {
        var result = new List<Vector3i>();
        var ppl = GameManager.Instance?.GetPersistentPlayerList();
        if (ppl == null) return result;
        foreach (var kv in ppl.Players)
        {
            var ppd = kv.Value;
            if (ppd == null || ppd.LPBlocks == null || ppd.LPBlocks.Count == 0) continue;
            if (!PpdMatchesPlayer(ppd, playerId)) continue;
            foreach (var lcb in ppd.LPBlocks) result.Add(lcb);
        }
        return result;
    }

    private static bool PpdMatchesPlayer(PersistentPlayerData ppd, string playerId)
    {
        if (string.Equals(ppd.PrimaryId?.CombinedString, playerId, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(ppd.NativeId?.CombinedString,  playerId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Returns the LCB position the player is currently standing inside, or null.</summary>
    private static Vector3i? FindContainingLCB(Vector3i playerPos, List<Vector3i> lcbs, int half)
    {
        foreach (var lcb in lcbs)
        {
            if (Math.Abs(playerPos.x - lcb.x) <= half &&
                Math.Abs(playerPos.z - lcb.z) <= half)
                return lcb;
        }
        return null;
    }

    // ============================================================ Container scan (single LCB)

    /// <summary>
    /// Wraps either a legacy TileEntityLootContainer or a V2.6
    /// TileEntityComposite with a TEFeatureStorage feature. Vanilla 7DTD V2.6
    /// migrated player crates (cntWoodWritableCrate, etc.) to the composite
    /// pattern -- those don't inherit from TileEntityLootContainer at all,
    /// so iterating only the legacy class misses every player-placed crate.
    /// </summary>
    private struct StorageRef
    {
        public TileEntity Te;            // underlying TE for OnModify / dirty
        public ItemStack[] Items;
        public ILockable Lockable;       // null if not lockable
    }

    private static StorageRef? AsStorageRef(TileEntity te)
    {
        if (te == null) return null;
        if (te is TileEntityLootContainer legacy)
        {
            return new StorageRef
            {
                Te = legacy,
                Items = legacy.GetItems(),
                Lockable = legacy as ILockable,  // TileEntitySecure / TileEntitySecureLootContainer
            };
        }
        if (te is TileEntityComposite composite)
        {
            var storage = composite.GetFeature<TEFeatureStorage>();
            if (storage == null) return null;  // composite without storage feature -> not a container
            var lockable = composite.GetFeature<TEFeatureLockable>();
            return new StorageRef
            {
                Te = composite,
                Items = storage.items,
                Lockable = lockable,  // null if no lockable feature
            };
        }
        return null;
    }

    private List<StorageRef> FindOwnedContainersInLCB(HashSet<string> ownerKeys, Vector3i lcb)
    {
        var result = new List<StorageRef>();
        var world = GameManager.Instance?.World;
        if (world == null) return result;

        // Iterate the player's TRACKER positions, not chunk.GetTileEntities().
        //   1. chunk.tileEntities surfaces vanilla POI containers (truck cargo,
        //      hero chests, savage crates) that the player didn't place.
        //   2. POI containers have lazy-init'd empty itemsArr until a player
        //      opens them, which made every container look "empty" in the debug.
        //   3. The tracker already filters to player-placed blocks (via the
        //      Block.OnBlockAdded hook), so anything we find this way is
        //      legitimately the player's.
        int half = ResolveHalfSize();
        var trackerPositions = CollectTrackerPositions(ownerKeys);
        if (trackerPositions == null || trackerPositions.Count == 0) return result;

        foreach (var packed in trackerPositions)
        {
            var pos = UnpackPos(packed);
            if (Math.Abs(pos.x - lcb.x) > half) continue;
            if (Math.Abs(pos.z - lcb.z) > half) continue;

            TileEntity te;
            try { te = world.GetTileEntity(0, pos); } catch { continue; }
            var maybe = AsStorageRef(te);
            if (!maybe.HasValue) continue;
            var sref = maybe.Value;

            if (_cfg.RequireSecureContainerOwnership)
            {
                if (sref.Lockable == null) continue;
                if (!IsContainerOwnedByPlayer(sref.Lockable, ownerKeys)) continue;
            }
            if (sref.Items == null || sref.Items.Length == 0) continue;

            result.Add(sref);
        }
        return result;
    }

    /// <summary>
    /// Merge tracker sets across every candidate ID form for the player.
    /// Crossplay quirk: placement events arrive keyed by PPD.PrimaryId
    /// (often EOS_xxx) while the chat path uses ClientInfo.PlatformId
    /// (often Steam_xxx). At read time we walk both.
    /// </summary>
    private HashSet<long> CollectTrackerPositions(HashSet<string> ownerKeys)
    {
        var merged = new HashSet<long>();
        foreach (var key in ownerKeys)
        {
            if (!_state.Value.PlacedPositions.TryGetValue(key, out var s)) continue;
            foreach (var p in s) merged.Add(p);
        }
        return merged;
    }

    private static bool IsContainerOwnedByPlayer(ILockable lockable, HashSet<string> ownerKeys)
    {
        if (ownerKeys == null || ownerKeys.Count == 0) return false;
        var owner = lockable.GetOwner();
        if (owner != null && ownerKeys.Contains(owner.CombinedString)) return true;
        var users = lockable.GetUsers();
        if (users != null)
        {
            foreach (var u in users)
            {
                if (u != null && ownerKeys.Contains(u.CombinedString)) return true;
            }
        }
        return false;
    }

    // ============================================================ Container resource math

    private static Dictionary<string, int> SumResources(List<StorageRef> containers)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ct in containers)
        {
            var items = ct.Items;
            if (items == null) continue;
            foreach (var stack in items)
            {
                if (stack == null || stack.IsEmpty() || stack.itemValue == null) continue;
                var ic = ItemClass.GetForId(stack.itemValue.type);
                if (ic == null) continue;
                string name = ic.GetItemName();
                if (string.IsNullOrEmpty(name)) continue;
                if (!totals.ContainsKey(name)) totals[name] = 0;
                totals[name] += stack.count;
            }
        }
        return totals;
    }

    private static int TakeFromContainer(StorageRef ct, string itemName, int wanted)
    {
        int taken = 0;
        var items = ct.Items;
        if (items == null) return 0;
        for (int i = 0; i < items.Length && taken < wanted; i++)
        {
            var stack = items[i];
            if (stack == null || stack.IsEmpty() || stack.itemValue == null) continue;
            var ic = ItemClass.GetForId(stack.itemValue.type);
            if (ic == null) continue;
            if (!string.Equals(ic.GetItemName(), itemName, StringComparison.OrdinalIgnoreCase)) continue;

            int take = Math.Min(stack.count, wanted - taken);
            stack.count -= take;
            taken += take;
            if (stack.count == 0) items[i] = ItemStack.Empty.Clone();
        }
        if (taken > 0) ct.Te?.SetModified();
        return taken;
    }

    // ============================================================ Helpers

    private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>Pack Vector3i into a long: 16 bits per axis, signed (range ±32k).</summary>
    private static long PackPos(Vector3i p)
    {
        long x = (ushort)(short)p.x;
        long y = (ushort)(short)p.y;
        long z = (ushort)(short)p.z;
        return x | (y << 16) | (z << 32);
    }

    private static Vector3i UnpackPos(long packed)
    {
        short x = (short)(packed         & 0xFFFF);
        short y = (short)((packed >> 16) & 0xFFFF);
        short z = (short)((packed >> 32) & 0xFFFF);
        return new Vector3i(x, y, z);
    }

    private static string PrettyItemName(string itemName)
    {
        try
        {
            var ic = ItemClass.GetItemClass(itemName, _caseInsensitive: true);
            var pretty = ic?.GetLocalizedItemName();
            return string.IsNullOrEmpty(pretty) ? itemName : pretty;
        }
        catch { return itemName; }
    }
}
