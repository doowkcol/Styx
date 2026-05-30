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
// Darkness Stumbles -- DS:Threats prototype: The Cunning (assassin).
//
// A zombie that HUNTS. The flagship feature: it opens doors and hatches
// to reach you -- the only entity in the game that does. No more "barricade
// the door and relax." It is relentless, fast (feral chassis), keen-sighted,
// and can be dispatched to mark and hunt a specific player.
//
// Concept identity (behaviour over appearance -- an assassin blends in):
//   - Looks like a slightly lean, ordinary feral. NO glowing eyes / aura;
//     the menace is that you don't notice it until it's opening your door.
//   - SizeScale 0.95 wiry silhouette; high SightRange (45m) + keen hearing
//     so it locks onto you from distance.
//   - buffDsCunningMark: relentless passive (light regen so it doesn't bleed
//     out of the hunt; no flashy visuals).
//
// THREE capabilities, layered:
//   1. Door/hatch opening (custom C#, the flagship). Every ~0.33s each live
//      Cunning that has a player target probes the blocks ahead of it toward
//      that target; a closed BlockDoor (doors AND hatches are all
//      BlockDoorSecure : BlockDoor) gets its open-bit (meta bit 0) flipped
//      and pushed to clients via SetBlockRPC -- same path the engine uses
//      when a player opens a door, so the swing animation + see-through +
//      pathing all update. The pathfinder treats OPEN doors as passable, so
//      once opened the Cunning simply walks through. LOCKED secure doors
//      (meta bit 2) are respected by default (it smashes them like any
//      zombie) -- set Config.OpenLockedDoors=true for a lock-defeating
//      unstoppable variant.
//   2. Relentless hunt (custom C#). A Cunning can be ASSIGNED a target
//      (via /dscunning hunt or the nightly bounty). While assigned, if it
//      loses line-of-sight it keeps pathing toward the target's last known
//      position (SetInvestigatePosition) instead of wandering off -- so it
//      tracks you across a POI / base.
//   3. Smart senses (XML). SightRange 45 + AINoiseSeekDist 25 (vanilla
//      ~30 / ~8) -- it sees and hears far. Inherits feral run speed + AI.
//
// Spawn paths:
//   - /dscunning spawn [n]      test-spawn in front of you
//   - /dscunning hunt [player]  spawn one assigned to hunt a player (warns them)
//   - Rare slot in DsThreatsHigh (ambient wild appearance, no assignment)
//   - Nightly bounty: at BountyHour, once per in-game day, roll BountyChance
//     to spawn a hunter on a random online player (the "you've been marked"
//     event). Configurable / disableable.
//
// REQUIRES: server restart after first install -- the engine reads
// entityclasses.xml + buffs.xml at boot only (synthesised from the blocks
// below by the framework).

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsCunning — DS:Threats assassin variant. Inherits zombieBoeFeral
    (lean male feral: always-run aggression, MoveSpeedAggro .5/1.35, full
    melee AI). Distinct silhouette from the two slim-female DS variants
    (Specter=Arlene, Wraith=Nurse). SizeScale 0.95 reads as wiry. No
    glowing-eye buff on purpose — the assassin's whole point is that it
    looks ordinary until it's hunting you through your own doors.

    SightRange 45 (vanilla ~30) + AINoiseSeekDist 25 (vanilla ~8) make it
    keen — it acquires you from far and homes on noise. AI tasks inherited
    from the feral chassis (already relentless pursuit); door-opening and
    target-locking are layered in C# (DsCunningPrototype.cs), not AI tasks.

    HP override applied at spawn via Stats.Health.BaseMax (config-tunable);
    zombieBoeFeral's ^healthNormalFeral default is higher than we want for
    a fast skirmisher-assassin.
-->
<entity_class name="zombieDsCunning" extends="zombieBoeFeral">
    <property name="SizeScale" value="0.95"/>
    <property name="SightRange" value="45"/>
    <property name="AINoiseSeekDist" value="25"/>
    <!-- A bit faster than the feral base (.5,1.35). Format is walk,run
         during aggro; the assassin is always in pursuit so the run value
         is what's felt. Stays calm-fast rather than rage-fast — the plugin
         zeroes rage every tick (see DsCunningPrototype.CunningTick). -->
    <property name="MoveSpeedAggro" value=".5, 1.5"/>

    <!-- Auto-apply signature buff on first spawn so biome-spawned and
         bounty Cunnings alike carry the relentless passive. -->
    <effect_group name="DS Cunning Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsCunningMark"/>
    </effect_group>
</entity_class>
*/

/* @styx-buffs
<!--
    buffDsCunningMark — the assassin's signature. Deliberately understated:
    no eye glow, no body aura (it blends in). A light always-on regen so a
    Cunning that takes chip damage during a long hunt doesn't quietly bleed
    out before it reaches you — reinforces "relentless" without making it
    tanky. The icon is hidden so players don't get a free "that one's
    special" tell from a buff readout.
-->
<buff name="buffDsCunningMark"
      icon="ui_game_symbol_zombie"
      hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <update_rate value="1"/>
    <effect_group name="DS Cunning Mark">
        <!-- 5 HP/sec relentless regen (update_rate=1 ticks every second).
             Makes it shrug off chip damage during a long chase so it keeps
             coming, while still being killable under focused fire (any gun
             out-DPSes 5/sec on its 300 HP). -->
        <passive_effect name="HealthChangeOT" operation="base_add" value="5"/>
    </effect_group>
</buff>

<!--
    buffDsCunningMarked — applied to the HUNTED player when a Cunning is
    dispatched against them (hunt command / nightly bounty). Cosmetic only:
    a visible debuff icon + name so the player has a persistent "you are
    being hunted" reminder. No stat effects. Removed in C# when the
    assigned hunter dies/despawns; long fallback duration in case the
    removal path is missed.
-->
<buff name="buffDsCunningMarked"
      name_key="buffDsCunningMarkedName"
      description_key="buffDsCunningMarkedDesc"
      icon="ui_game_symbol_zombie">
    <stack_type value="replace"/>
    <duration value="1800"/>
</buff>
*/

// NB: the buffDsCunningMarkedName/Desc label keys are registered at runtime
// via Styx.Ui.Labels.Register in OnLoad (the StyxNvg pattern) rather than an
// embedded localization-manifest block -- the label subsystem injects them
// into the live Localization dict immediately and persists them to the
// StyxRuntime mod for the next boot, leaving Config/Localization.txt untouched.
// (Do NOT write the synth marker token in prose here; the block scanner
// would false-match it and clobber the operator-owned Localization.txt.)

/* @styx-entitygroups DsThreatsHigh
<!-- Rare ambient slot in the high-tier cohort (burnt_forest / desert /
     snow / wasteland). Low weight (.10) — a wild Cunning with no hunt
     assignment behaves as a keen-sighted door-opening feral. The headline
     "you've been marked" experience comes from the nightly bounty, not the
     wild spawn. -->
zombieDsCunning, .10
*/

/* @styx-patch entitygroups
<!-- The Cunning into POI sleepers and blood-moon hordes, RARE. In a POI it
     hunts you room to room; in a blood moon it opens your base doors mid
     siege. Kept rare so it lands as a "that one is here" event, not nightly. -->
<append xpath="/entitygroups/entitygroup[starts-with(@name,'sleeperHordeStageGS')]">
    <entity name="zombieDsCunning" prob=".03"/>
</append>
<append xpath="/entitygroups/entitygroup[starts-with(@name,'feralHordeStageGS')]">
    <entity name="zombieDsCunning" prob=".03"/>
</append>
*/

[Info("DsCunningPrototype", "Doowkcol", "0.1.0")]
public class DsCunningPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Cunning -- door-opening assassin that hunts players (internal).";

    // ============================================================ constants
    private const string ClassName     = "zombieDsCunning";
    private const string SignatureBuff = "buffDsCunningMark";
    private const string MarkedBuff    = "buffDsCunningMarked";
    private const string AdminPerm     = "dscunning.admin";

    private Config _cfg;
    private int _classHash;
    private TimerHandle _tick;       // door-opening + relentless tracking
    private TimerHandle _bountyTick; // once-per-day bounty roll
    private readonly System.Random _rng = new System.Random();

    // hunterEntityId -> targetEntityId. Populated by hunt command + bounty.
    private readonly Dictionary<int, int> _hunts = new Dictionary<int, int>();
    // Last in-game day the bounty fired, so it rolls at most once per night.
    private int _lastBountyDay = -1;

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        StyxCore.Perms.RegisterKnown(AdminPerm,
            "Spawn / dispatch DS:Cunning assassins", Name);

        // Marked-debuff display labels (runtime-registered; see note by the
        // buff block). Injected live + persisted to StyxRuntime for next boot.
        Styx.Ui.Labels.Register(this, "buffDsCunningMarkedName", "Marked");
        Styx.Ui.Labels.Register(this, "buffDsCunningMarkedDesc",
            "Something cunning is hunting you. It will open doors to reach you.");

        StyxCore.Commands.Register("dscunning",
            "DS:Threats Cunning -- /dscunning <spawn|hunt|find|despawn|stats> [arg]",
            HandleCommand);

        if (_cfg.Enabled)
        {
            double dt = Math.Max(0.1, _cfg.DoorTickSeconds);
            _tick = Scheduler.Every(dt, CunningTick, name: "DsCunning.tick");

            if (_cfg.BountyEnabled)
                _bountyTick = Scheduler.Every(60.0, BountyCheck, name: "DsCunning.bounty");
        }

        Log.Out("[DsCunning] Loaded v0.1.0 -- class={0} hash={1} hp={2} sight=45m " +
                "openLocked={3} bounty={4} (hour {5}, {6:P0}/night)",
            ClassName, _classHash, _cfg.MaxHealth, _cfg.OpenLockedDoors,
            _cfg.BountyEnabled, _cfg.BountyHour, _cfg.BountyChance);
    }

    public override void OnUnload()
    {
        _tick?.Destroy();       _tick = null;
        _bountyTick?.Destroy(); _bountyTick = null;
        _hunts.Clear();
        Styx.Ui.Labels.UnregisterAll(this);
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ============================================================ main tick

    /// <summary>
    /// Per-tick brain for every live Cunning: (1) keep assigned hunters
    /// locked onto their target, (2) open any door/hatch the Cunning is
    /// trying to path through toward its current target.
    /// </summary>
    private void CunningTick()
    {
        var world = GameManager.Instance?.World;
        if (world == null) return;

        var live = FindAllCunning(world);

        // Prune dead/gone hunters and unmark their orphaned targets.
        if (_hunts.Count > 0)
        {
            List<int> stale = null;
            foreach (var kv in _hunts)
            {
                var hunter = world.GetEntity(kv.Key) as EntityAlive;
                if (hunter == null || hunter.IsDead())
                    (stale ??= new List<int>()).Add(kv.Key);
            }
            if (stale != null)
                foreach (var eid in stale) ClearHunt(world, eid);
        }

        for (int i = 0; i < live.Count; i++)
        {
            var z = live[i];

            // Never rage. The Cunning is calm and methodical -- it opens
            // doors and tracks rather than frenzy-sprinting. Rage
            // (EntityHuman.moveSpeedRagePer, started by fall/destroy
            // behaviours via StartRage) is zeroed every tick so any
            // momentary rage from dropping through a hatch or beating a
            // wall is suppressed within one tick -- it keeps its steady
            // MoveSpeedAggro pace instead of the erratic rage speed-up.
            if (z is EntityHuman human) human.moveSpeedRagePer = 0f;

            // Resolve this Cunning's effective target: whatever it's actively
            // aggro'd on, else its assigned hunt target (so it opens doors
            // while pathing toward an out-of-sight assigned player too).
            EntityPlayer target = z.attackTarget as EntityPlayer;
            if (target == null && _hunts.TryGetValue(z.entityId, out int tEid))
            {
                target = world.GetEntity(tEid) as EntityPlayer;
                if (target != null && !target.IsDead())
                {
                    // Relentless: nudge it toward the target's position so it
                    // keeps closing even without line-of-sight. When it gets
                    // close enough to see, the feral AI re-acquires and
                    // ApproachAndAttackTarget takes over.
                    z.SetInvestigatePosition(target.position, 600);
                }
            }

            if (target != null && !target.IsDead())
                TryOpenDoorAhead(world, z, target);
        }
    }

    // ============================================================ door opening (flagship)

    /// <summary>
    /// Probe the blocks around the Cunning for a closed door/hatch in its
    /// path to the target and open it. Two passes, one door per tick:
    ///   (a) Vertical column at the Cunning's own X/Z -- catches HATCHES it
    ///       can't reach horizontally: directly overhead while it climbs a
    ///       ladder toward one (the "beats the hatch from below" case), or
    ///       at/just below its feet about to drop through.
    ///   (b) Horizontal toward the target at feet + head height -- catches
    ///       doors / hatches in the doorway it's walking into.
    /// </summary>
    private void TryOpenDoorAhead(World world, EntityAlive z, EntityPlayer target)
    {
        Vector3 from = z.position;
        int bx0 = Mathf.FloorToInt(from.x);
        int bz0 = Mathf.FloorToInt(from.z);
        int feetY = Mathf.FloorToInt(from.y);

        // (a) Vertical column at its own position: hatch above (ladder climb)
        //     or below (drop-through). Scan one below the feet up past head.
        for (int dy = -1; dy <= 3; dy++)
        {
            if (TryOpenDoorAt(world, new Vector3i(bx0, feetY + dy, bz0)))
                return;
        }

        // (b) Horizontal toward the target: the doorway it's walking into.
        Vector3 dir = target.position - from;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0004f) return;
        dir.Normalize();
        float range = Mathf.Max(1.0f, _cfg.DoorOpenRange);
        for (float d = 1.0f; d <= range + 0.001f; d += 0.9f)
        {
            Vector3 p = from + dir * d;
            int bx = Mathf.FloorToInt(p.x);
            int bz = Mathf.FloorToInt(p.z);
            for (int dy = 0; dy <= 1; dy++)
            {
                if (TryOpenDoorAt(world, new Vector3i(bx, feetY + dy, bz)))
                    return; // one open per tick is enough; pathing resumes
            }
        }
    }

    /// <summary>
    /// If the block at <paramref name="pos"/> is a closed (and, by default,
    /// unlocked) door/hatch, open it server-authoritatively. Returns true if
    /// it opened one.
    /// </summary>
    private bool TryOpenDoorAt(World world, Vector3i pos)
    {
        BlockValue bv = world.GetBlock(pos);
        if (!(bv.Block is BlockDoor)) return false;

        // Doors are 1x2x1 multiblocks; resolve a child (top half) to its
        // parent so we read/write the authoritative meta.
        Vector3i parent = pos;
        BlockValue pbv = bv;
        if (bv.ischild)
        {
            parent = bv.Block.multiBlockPos.GetParentPos(pos, bv);
            pbv = world.GetBlock(parent);
            if (!(pbv.Block is BlockDoor)) return false;
        }

        if (BlockDoor.IsDoorOpen(pbv.meta)) return false; // already open

        // Respect locks unless configured otherwise. Hatches + secure doors
        // are BlockDoorSecure (lock = meta bit 2).
        if (!_cfg.OpenLockedDoors
            && pbv.Block is BlockDoorSecure
            && BlockDoorSecure.IsDoorLockedMeta(pbv.meta))
            return false;

        // Flip open-bit (meta bit 0) and push to clients. Mirrors
        // BlockDoor.updateOpenCloseState -> SetBlockRPC, which triggers the
        // swing animation via OnBlockValueChanged on every client.
        BlockValue nv = pbv;
        nv.meta = (byte)(1u | (pbv.meta & 0xFFFFFFFEu));
        try
        {
            world.SetBlockRPC(parent, nv);
            if (_cfg.Verbose)
                Log.Out("[DsCunning] opened door @ {0}", parent);
        }
        catch (Exception e)
        {
            Log.Warning("[DsCunning] SetBlockRPC door open @ {0} failed: {1}", parent, e.Message);
            return false;
        }
        return true;
    }

    // ============================================================ nightly bounty

    /// <summary>
    /// Once per in-game day, at BountyHour, roll BountyChance to dispatch a
    /// hunter against a random online player.
    /// </summary>
    private void BountyCheck()
    {
        if (!_cfg.BountyEnabled) return;
        var world = GameManager.Instance?.World;
        if (world == null) return;

        int day  = StyxCore.World.CurrentDay;
        int hour  = (int)((world.worldTime / 1000UL) % 24UL);
        if (hour != _cfg.BountyHour) return;       // only at the bounty hour
        if (day == _lastBountyDay) return;          // already rolled tonight
        _lastBountyDay = day;

        if (_rng.NextDouble() > _cfg.BountyChance) return; // no bounty tonight

        var players = StyxCore.Player?.All();
        if (players == null || players.Count == 0) return;

        // Pick a random spawned player not already marked.
        var pool = new List<EntityPlayer>();
        foreach (var p in players)
            if (p != null && !p.IsDead() && (p.Buffs == null || !p.Buffs.HasBuff(MarkedBuff)))
                pool.Add(p);
        if (pool.Count == 0) return;

        var victim = pool[_rng.Next(pool.Count)];
        var hunter = SpawnHunter(world, victim);
        if (hunter != null)
            Log.Out("[DsCunning] Bounty: dispatched hunter {0} against {1} (day {2})",
                hunter.entityId, victim.GetDebugName(), day);
    }

    // ============================================================ spawn helpers

    /// <summary>
    /// Spawn a Cunning at distance around <paramref name="victim"/>, assign
    /// it to hunt them, and warn the player. Returns the hunter or null.
    /// </summary>
    private EntityAlive SpawnHunter(World world, EntityPlayer victim)
    {
        if (!EntityClass.list.ContainsKey(_classHash)) return null;

        float dist = Mathf.Max(10f, _cfg.SpawnDistance);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            Vector3 pos = victim.position + new Vector3(
                (float)Math.Cos(ang) * dist, 0f, (float)Math.Sin(ang) * dist);

            if (!StyxCore.World.IsChunkLoadedAt(pos)) continue;
            pos = StyxCore.World.SafeSurface(pos);

            var hunter = SpawnAt(world, pos, EnumSpawnerSource.Dynamic);
            if (hunter == null) continue;

            _hunts[hunter.entityId] = victim.entityId;
            hunter.SetAttackTarget(victim, 99999);
            hunter.SetInvestigatePosition(victim.position, 600);

            MarkPlayer(victim);
            return hunter;
        }
        Log.Warning("[DsCunning] SpawnHunter: no loaded ground near {0} after 6 tries", victim.GetDebugName());
        return null;
    }

    /// <summary>Create + spawn a Cunning at a position, apply buff + HP. No assignment.</summary>
    private EntityAlive SpawnAt(World world, Vector3 pos, EnumSpawnerSource source)
    {
        try
        {
            var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
            if (entity == null)
            {
                Log.Warning("[DsCunning] EntityFactory returned null for hash {0}", _classHash);
                return null;
            }
            entity.SetSpawnerSource(source);
            world.SpawnEntityInWorld(entity);
            entity.Buffs?.AddBuff(SignatureBuff);
            ApplyHpOverride(entity);
            return entity;
        }
        catch (Exception e)
        {
            Log.Error("[DsCunning] SpawnAt failed: {0}", e);
            return null;
        }
    }

    private void ApplyHpOverride(EntityAlive entity)
    {
        if (_cfg.MaxHealth <= 0) return;
        if (entity?.Stats?.Health == null) return;
        entity.Stats.Health.BaseMax = _cfg.MaxHealth;
        entity.Stats.Health.Value   = _cfg.MaxHealth;
    }

    private void MarkPlayer(EntityPlayer victim)
    {
        try { victim.Buffs?.AddBuff(MarkedBuff); } catch { }
        Styx.Server.Whisper(victim,
            "[ff3333]You've been marked.[-] Something cunning is hunting you -- " +
            "it will open doors to reach you.");
        Styx.Ui.Toast(victim, "MARKED -- you are being hunted", Styx.Ui.Sounds.Denied);
    }

    /// <summary>Drop a hunt assignment + clear the target's marked buff if no other hunter holds them.</summary>
    private void ClearHunt(World world, int hunterEid)
    {
        if (!_hunts.TryGetValue(hunterEid, out int targetEid)) { _hunts.Remove(hunterEid); return; }
        _hunts.Remove(hunterEid);

        bool stillHunted = false;
        foreach (var kv in _hunts) if (kv.Value == targetEid) { stillHunted = true; break; }
        if (!stillHunted)
        {
            var target = world.GetEntity(targetEid) as EntityPlayer;
            try { target?.Buffs?.RemoveBuff(MarkedBuff); } catch { }
        }
    }

    // ============================================================ commands

    private void HandleCommand(CommandContext ctx, string[] args)
    {
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid)) { ctx.Reply("This command requires a player context."); return; }
        if (!StyxCore.Perms.HasPermission(pid, AdminPerm))
        { ctx.Reply("[ff6666]Permission denied:[-] requires '" + AdminPerm + "'."); return; }

        string sub = (args != null && args.Length > 0) ? args[0].ToLowerInvariant() : "help";
        switch (sub)
        {
            case "spawn":
                int count = 1;
                if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 && n <= 10) count = n;
                CmdSpawn(ctx, count);
                break;
            case "hunt":    CmdHunt(ctx, args.Length > 1 ? args[1] : null); break;
            case "find":    CmdFind(ctx);    break;
            case "despawn": CmdDespawn(ctx); break;
            case "stats":   CmdStats(ctx);   break;
            default:        ShowHelp(ctx);   break;
        }
    }

    private void ShowHelp(CommandContext ctx)
    {
        ctx.Reply("DS:Cunning (assassin) commands:");
        ctx.Reply("  /dscunning spawn [n]      -- test-spawn N in front of you (max 10)");
        ctx.Reply("  /dscunning hunt [player]  -- dispatch a hunter at a player (you if omitted)");
        ctx.Reply("  /dscunning find           -- distance to nearest live Cunning");
        ctx.Reply("  /dscunning despawn        -- remove all live Cunnings");
        ctx.Reply("  /dscunning stats          -- live count + class/bounty state");
    }

    private void CmdSpawn(CommandContext ctx, int count)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }
        if (!EntityClass.list.ContainsKey(_classHash))
        {
            ctx.Reply("[ff6666]Cunning entity class not registered.[-] Restart the server -- " +
                "entityclasses.xml is read at boot only.");
            return;
        }

        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + fwd * 8f;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float off = (i - (count - 1) * 0.5f) * 2.0f;
            Vector3 pos = basePos + right * off;
            int x = Utils.Fastfloor(pos.x), z = Utils.Fastfloor(pos.z);
            pos.y = Math.Max(world.GetHeight(x, z) + 1.0f, caller.position.y);
            if (SpawnAt(world, pos, EnumSpawnerSource.StaticSpawner) != null) spawned++;
        }
        ctx.Reply("[00ff66]Spawned " + spawned + " Cunning(s).[-] Lean ferals -- they'll open doors to chase you.");
    }

    private void CmdHunt(CommandContext ctx, string targetName)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        if (!EntityClass.list.ContainsKey(_classHash))
        { ctx.Reply("[ff6666]Cunning class not registered -- restart needed.[-]"); return; }

        EntityPlayer victim;
        if (string.IsNullOrEmpty(targetName))
            victim = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        else
            victim = StyxCore.Player.Find(targetName);

        if (victim == null) { ctx.Reply("[ff6666]Target player not found.[-]"); return; }

        var hunter = SpawnHunter(world, victim);
        if (hunter != null)
            ctx.Reply(string.Format("[00ff66]Dispatched a hunter against {0}.[-] Spawned ~{1}m out.",
                victim.GetDebugName(), (int)_cfg.SpawnDistance));
        else
            ctx.Reply("[ff6666]Could not place a hunter near that player (unloaded ground?).[-]");
    }

    private void CmdFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllCunning(world);
        if (live.Count == 0) { ctx.Reply("No live Cunnings in the world."); return; }

        EntityAlive nearest = null; float best = float.MaxValue;
        foreach (var z in live)
        {
            float d = Vector3.Distance(z.position, caller.position);
            if (d < best) { best = d; nearest = z; }
        }
        ctx.Reply(string.Format("[00ff66]{0} live Cunning(s).[-] Nearest: entityId={1} dist {2:0.0}m HP {3}/{4}{5}",
            live.Count, nearest.entityId, best, nearest.Health, nearest.GetMaxHealth(),
            _hunts.ContainsKey(nearest.entityId) ? " (on a hunt)" : ""));
    }

    private void CmdDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var live = FindAllCunning(world);
        int removed = 0;
        foreach (var z in live)
        {
            try
            {
                ClearHunt(world, z.entityId);
                world.RemoveEntity(z.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e) { Log.Warning("[DsCunning] despawn {0} failed: {1}", z.entityId, e.Message); }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Cunning(s).[-]");
    }

    private void CmdStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        bool reg = EntityClass.list.ContainsKey(_classHash);
        var live = FindAllCunning(world);
        ctx.Reply("[00ff66]DS:Cunning status[-]");
        ctx.Reply("  Class:        " + ClassName + (reg ? " (registered)" : " [ff6666](NOT registered -- restart)[-]"));
        ctx.Reply("  Live count:   " + live.Count + " (" + _hunts.Count + " on active hunts)");
        ctx.Reply("  Open locked:  " + _cfg.OpenLockedDoors + "   Door range: " + _cfg.DoorOpenRange + "m");
        ctx.Reply("  Bounty:       " + (_cfg.BountyEnabled
            ? string.Format("on -- hour {0}, {1:P0}/night (last fired day {2})", _cfg.BountyHour, _cfg.BountyChance, _lastBountyDay)
            : "off"));
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllCunning(World world)
    {
        var result = new List<EntityAlive>();
        if (world?.Entities?.list == null) return result;
        var list = world.Entities.list;
        for (int i = 0; i < list.Count; i++)
            if (list[i] is EntityAlive ea && ea.entityClass == _classHash && !ea.IsDead())
                result.Add(ea);
        return result;
    }

    // ============================================================ config

    public class Config
    {
        /// <summary>Master enable. False = no ticks, no bounty (class still spawnable in wild via cohort).</summary>
        public bool Enabled = true;

        /// <summary>Spawned Cunning max HP. 0 = inherit zombieBoeFeral default. 300 = fast skirmisher-assassin.</summary>
        public int MaxHealth = 300;

        /// <summary>When true the assassin opens LOCKED doors/hatches too (bypasses base defence). Default false = respects locks (smashes them like any zombie).</summary>
        public bool OpenLockedDoors = false;

        /// <summary>How far ahead (m) the Cunning probes for a door to open. ~2 = the doorway it's walking into.</summary>
        public float DoorOpenRange = 2.0f;

        /// <summary>Door-opening + hunt-tracking tick interval (s). 0.33 ≈ 3Hz, snappy without being heavy.</summary>
        public double DoorTickSeconds = 0.33;

        /// <summary>Enable the nightly "you've been marked" bounty.</summary>
        public bool BountyEnabled = true;

        /// <summary>Probability (0..1) a bounty fires on a given night.</summary>
        public double BountyChance = 0.5;

        /// <summary>In-game hour (0-23) the bounty rolls. 22 = nightfall.</summary>
        public int BountyHour = 22;

        /// <summary>How far from the target (m) a dispatched hunter spawns.</summary>
        public float SpawnDistance = 35f;

        /// <summary>Log each door opened + bounty dispatch.</summary>
        public bool Verbose = true;
    }
}
