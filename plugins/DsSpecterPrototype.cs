// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Specter.
// Fast low-HP skirmisher with a smoke trail and a kill-window mechanic
// that punishes players who don't finish it quickly.
//
// Visual identity:
//   - Custom entity class (zombieDsSpecter) extends zombieArleneFeral
//     (slim humanoid, Feral aggression, no Radiated/Charged chain so
//     no aura/electric conflicts with our identity). SizeScale=0.95
//     for a subtly smaller silhouette — leaves the 0.85 micro-scale
//     reserved for the future Wraith variant.
//   - Signature buff (buffDsSpecterTrail) layers:
//       * p_twitch_smokePuff PERSISTENT on .body shape_mesh=true
//         (NOT oneshot — the emitter ticks continuously, producing
//         a trail of puffs as the Specter moves. Same .body+
//         shape_mesh recipe that worked for Behemoth's radiation
//         aura on the humanoid SDCS rig).
//       * HealthChangeOT regen of 5 HP per 2s update tick (~2.5
//         HP/sec average) — "quick hp regen" per the design spec
//         without being absurd.
//   - Spawn puff: dedicated 1-second buffDsSpecterSpawnPuff masks
//     the materialisation pop, same proven pattern as Hellhound /
//     Demon.
//
// Behavioural identity:
//   - Kill-window mechanic: when a Specter takes its first hit,
//     a per-entity timer starts. If the player kills it within
//     Death.KillWindowSec (default 5s), nothing happens — clean kill.
//     If the player takes too long, on death the Specter spawns
//     Death.SpawnCount revenants (default 2) at the corpse, and each
//     revenant immediately gets SetAttackTarget on the killer for
//     Death.AggroDurationSec.
//   - Revenants are flagged in C# (_revenantIds set) so their own
//     deaths don't trigger another spawn wave. Without this gate the
//     mechanic would cascade exponentially: 1 -> 2 -> 4 -> 8 ... per
//     missed kill window. v0.1 ships with single-generation revenants;
//     cascading can be added as a config flag if the gameplay calls
//     for it.
//   - Low HP (250 default) keeps the kill window achievable for a
//     skilled player but punishing if they're slow.
//
// REQUIRES: shim active OR the four Config files in place.
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml + buffs.xml need to be loaded by the engine's
// config pass.
//
// Test plan:
//   1. /perm grant user <yourId> dsspecter.admin
//   2. /dsspecter spawn 1   -> a single Specter with body smoke
//                              trail + spawn puff. Slim mesh, slightly
//                              smaller than vanilla.
//   3. Hit it once and kill within 5s: clean kill, no revenants.
//   4. Hit it once, wait 6+ seconds, then kill: 2 revenants spawn
//      at the corpse and immediately aggro onto you. Server log:
//      "[DsSpecter] Kill window missed (X.Xs > 5s) -- spawning 2 revenants"
//   5. Don't damage at all and stab from behind: clean kill, no
//      revenants (no first-hit recorded).
//   6. /dsspecter despawn (cleanup)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsSpecter — DS:Threats fast skirmisher variant.
    Inherits zombieArleneFeral (slim female humanoid, Feral aggression
    tier). Feral chosen over higher tiers: we want LOW base HP for the
    "burst-down or face revenants" gameplay loop, and Feral is the
    least-buffed feral-tagged tier. Radiated/Charged/Infernal would
    bring the wrong stats and visual conflicts. SizeScale 0.95 is a
    subtle shrink — clearly not Behemoth/Demon (1.5/1.2) but also
    visibly smaller than vanilla so the Specter reads distinct at a
    glance. HP override applied at spawn time via Stats.Health.BaseMax
    so it's config-tunable (zombieArleneFeral defaults to ^healthNormalFeral
    = 550, which is too high for a low-HP burst target).
-->
<entity_class name="zombieDsSpecter" extends="zombieArleneFeral">
    <property name="SizeScale" value="0.95"/>

    <!-- Auto-apply signature buff on first spawn. Specter's smoke
         trail was already working on biome-spawned variants because
         the C# Scheduler.OnTrailTick iterates ALL live Specters and
         applies the puff buff regardless of spawn path. But the eye
         glow + regen passive in buffDsSpecterTrail was missing on
         biome-spawned ones — this trigger fixes that. -->
    <effect_group name="DS Specter Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsSpecterTrail"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups DsThreatsLow new
<!-- Specter contributes to BOTH cohorts. The Low cohort is the
     starter-friendly pool referenced by pine_forest — only Specter
     and Wraith populate it (no Demon, no Behemoth). Low HP, fast
     kill-window mechanic — manageable for a fresh-spawn character.
     The `new` flag creates the DsThreatsLow group (DsThreatsHigh
     gets `new` from DsBehemothPrototype's contribution). -->
zombieDsSpecter, .35
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Specter also in the high-tier cohort so all biomes can spawn
     it — Specter is the entry-tier ambient threat that scales by
     virtue of pack accumulation through its kill-window mechanic. -->
zombieDsSpecter, .35
*/

/* @styx-buffs
<buff name="buffDsSpecterTrail"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <!-- update_rate drives the regen cadence. The smoke trail is NOT
         driven from this buff (earlier remove+re-attach attempt failed
         because Object.Destroy is queued for end-of-frame — the same-
         tick re-Attach finds the still-existing GameObject and skips
         the Play() branch). Trail is now driven by C# Scheduler.Every
         which re-applies buffDsSpecterSpawnPuff (below) every
         Trail.IntervalSec to each live Specter — same proven 1-second
         attach+remove pattern as buffDsBehemothSummon. -->
    <update_rate value="2"/>
    <effect_group name="DS Specter Trail">
        <!-- Spectral eyes. Vanilla zombie-shock particle (blue electric
             arcs, used at vanilla buffs.xml:15205+ on twitch-zombie hands)
             attached at the Head bone with eye-position offsets. Same
             eyeball-the-offset technique we used for Hellhound eyes:
             ~5cm sideways from skull mid-line, ~6cm above Head pivot,
             ~12cm forward to clear the brow. Reads as ghostly electric
             eye glow from medium range, distinct from Feral's default
             red eyes. To swap to green (eldritch) replace particle name
             with p_twitch_zombie_radiation_left/right. To revert to the
             default Feral eye glow, delete these four triggers. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_shock_left" parent_transform="Head"
            local_offset="0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_shock_left" parent_transform="Head"
            local_offset="0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_shock_right" parent_transform="Head"
            local_offset="-0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_shock_right" parent_transform="Head"
            local_offset="-0.04,0.06,0.12"/>

        <!-- Quick HP regen. At update_rate=2 and value=5, that's
             5 HP per 2s = 2.5 HP/sec average. Fast enough to mean
             "commit to the kill or watch your damage heal back,"
             slow enough that a focused burst still puts the Specter
             down well within the 5s kill window. -->
        <passive_effect name="HealthChangeOT" operation="base_add" value="5"/>
    </effect_group>
</buff>

<!-- (No separate trail-puff buff. The C# OnTrailTick handler re-applies
     buffDsSpecterSpawnPuff every TrailIntervalSec — see plugin OnLoad.
     One buff serves both spawn-marker and ongoing trail purposes;
     keeping a single particle name in the entity's tracker dict
     (EntityAlive.particles is keyed by name, so two simultaneous
     attachments with the same name would corrupt cleanup state). With
     interval > buff duration the previous instance always expires
     and runs its onSelfBuffRemove cleanup before the next AddBuff,
     so no same-frame Object.Destroy race. Each fresh AddBuff = a
     clean 1-second puff cycle at the entity's current torso position.) -->

<!-- One-shot spawn-puff buff. Mirrors buffDsHellhoundSpawnPuff /
     buffDsDemonSpawnPuff: 1-second duration with explicit cleanup.
     Anchored to Spine1 (humanoid SDCS torso bone — canonical and
     proven; vanilla uses it for p_electric_shock at buffs.xml:6498)
     with a large downward local_offset to position the puff at
     ankle/foot level. Spine1's local Y axis points up the spine on
     humanoid Mecanim rigs, so Y=-1.2 = 1.2m below the bone origin
     in its local frame. With Spine1 ~1.3m off the ground on a
     1.0-scale humanoid (and the Specter at 0.95 scale slightly
     less), the offset places the puff at ~0.1m world height = at
     her feet. Bonus: as she runs and her torso leans forward, the
     bone's local axes tilt, so the offset translates partly
     backward — the puff origin trails her body axis naturally. -->
<buff name="buffDsSpecterSpawnPuff"
      icon="ui_game_symbol_skull"
      hidden="true">
    <stack_type value="replace"/>
    <duration value="1"/>
    <effect_group>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_smokePuff" parent_transform="Spine1"
            local_offset="0,-1.2,0"/>
        <triggered_effect trigger="onSelfBuffRemove" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
        <triggered_effect trigger="onSelfDied" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
    </effect_group>
</buff>
*/

/* @styx-patch entitygroups
<!-- Specter into POI sleepers, blood-moon hordes, and wandering hordes.
     starts-with / contains hits every gamestage tier of each pool in one
     patch (the modlet patcher uses full .NET XPath 1.0). Skirmisher numbers
     are fair in a horde, so moderate weight. -->
<append xpath="/entitygroups/entitygroup[starts-with(@name,'sleeperHordeStageGS')]">
    <entity name="zombieDsSpecter" prob=".06"/>
</append>
<append xpath="/entitygroups/entitygroup[starts-with(@name,'feralHordeStageGS')]">
    <entity name="zombieDsSpecter" prob=".06"/>
</append>
<append xpath="/entitygroups/entitygroup[contains(@name,'wanderingHordeStageGS')]">
    <entity name="zombieDsSpecter" prob=".05"/>
</append>
*/

[Info("DsSpecterPrototype", "Doowkcol", "0.1.0")]
public class DsSpecterPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Specter fast skirmisher prototype (internal).";

    // ============================================================ constants

    /// <summary>Entity class name as registered in entityclasses.xml.</summary>
    private const string ClassName = "zombieDsSpecter";

    /// <summary>Signature buff applied to every spawned Specter.</summary>
    private const string SignatureBuff = "buffDsSpecterTrail";

    /// <summary>Spawn-puff buff applied alongside the signature buff.</summary>
    private const string SpawnPuffBuff = "buffDsSpecterSpawnPuff";

    /// <summary>Permission required to use any /dsspecter subcommand.</summary>
    private const string AdminPerm = "dsspecter.admin";

    private Config _cfg;
    private int _classHash;

    /// <summary>Scheduled timer that ticks the trail puff onto every
    /// live Specter. Created in OnLoad, destroyed in OnUnload to avoid
    /// orphaned timers across hot-reloads.</summary>
    private TimerHandle _trailTimer;

    /// <summary>Per-Specter first-damage timestamp. Recorded by
    /// OnPreDamageApplied on the first hit, read by OnEntityDeath to
    /// decide whether the kill window was missed. Cleaned on death.</summary>
    private readonly Dictionary<int, float> _firstDamageTime = new Dictionary<int, float>();

    /// <summary>Per-Specter latest attacker entity id. Updated on every
    /// damage hit, so on death this points at whoever dealt the killing
    /// blow (or close enough — the LAST hitter before death). Used as
    /// the aggro target for spawned revenants.</summary>
    private readonly Dictionary<int, int> _lastAttacker = new Dictionary<int, int>();

    /// <summary>Set of Specter entity ids that were spawned as revenants
    /// (rather than directly via /dsspecter spawn or another spawn path).
    /// Their deaths SKIP the spawn-revenants logic to prevent the
    /// 1 -> 2 -> 4 -> 8 cascade. Cleaned on death.</summary>
    private readonly HashSet<int> _revenantIds = new HashSet<int>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        // Trail-puff scheduler. Re-applies SpawnPuffBuff on each tick to
        // every live Specter, producing visible smoke at her current
        // position. Interval > the buff's 1-second duration so each
        // previous puff expires (and runs its cleanup) before the next
        // AddBuff — avoids the same-frame Object.Destroy race that
        // killed the earlier in-buff remove+re-attach approach.
        _trailTimer = Scheduler.Every(_cfg.Trail.IntervalSec, OnTrailTick, "DsSpecter.trail");

        Log.Out("[DsSpecter] Prototype loaded. Class={0} hash={1} buff={2} " +
                "hp={3} window={4}s spawn={5} aggro={6}s trail={7}s",
            ClassName, _classHash, SignatureBuff,
            _cfg.MaxHealth == 0 ? "inherited" : _cfg.MaxHealth.ToString(),
            _cfg.Death.KillWindowSec, _cfg.Death.SpawnCount, _cfg.Death.AggroDurationSec,
            _cfg.Trail.IntervalSec);

        StyxCore.Commands.Register("dsspecter",
            "DS:Threats Specter prototype -- /dsspecter <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        _trailTimer?.Destroy();
        _trailTimer = null;
        _firstDamageTime.Clear();
        _lastAttacker.Clear();
        _revenantIds.Clear();
        Log.Out("[DsSpecter] Prototype unloaded.");
    }

    // ============================================================ trail tick

    /// <summary>
    /// Scheduler tick. Walks the entity list, finds live Specters, and
    /// re-applies the spawn-puff buff to each. The buff is short-lived
    /// (1s) with stack_type=replace, so each fresh AddBuff fires its
    /// onSelfBuffStart -> AttachParticleEffectToEntity sequence and
    /// produces a visible puff at the Specter's current body position.
    /// As she runs, sequential puffs spawn at sequential positions
    /// along her path — visible trail.
    /// </summary>
    private void OnTrailTick()
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return;

        // Snapshot the live list to avoid concurrent-modification issues
        // if a Specter dies / spawns mid-iteration. Cheap — typically a
        // handful of Specters at most.
        foreach (var e in world.Entities.list)
        {
            if (!(e is EntityAlive ea)) continue;
            if (ea.entityClass != _classHash) continue;
            if (ea.IsDead()) continue;
            if (ea.Buffs == null) continue;

            try { ea.Buffs.AddBuff(SpawnPuffBuff); }
            catch (Exception ex)
            {
                Log.Warning("[DsSpecter] Trail puff AddBuff on entity {0} failed: {1}",
                    ea.entityId, ex.Message);
            }
        }
    }

    // ============================================================ damage hook

    /// <summary>
    /// Auto-bound. Tracks first-damage time + latest attacker for the
    /// kill-window mechanic. Hooked on OnPreDamageApplied (universal
    /// damage choke point — covers melee, gunshots via NetPackageDamage-
    /// Entity, fall, fire DoT, everything) so the timer starts on the
    /// first reliable damage event regardless of source.
    /// </summary>
    void OnPreDamageApplied(EntityAlive victim, DamageResponse response)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        if (victim.IsDead()) return;

        // DamageResponse is a struct -> can't null-conditional it directly
        // (CS0023). Source is the inner class reference; getEntityId returns
        // < 0 for sourceless damage (DoT, environment) — those don't count
        // as "first hit" for the kill-window timer because there's no
        // attacker to revenge against on death.
        int attackerId = response.Source?.getEntityId() ?? -1;
        if (attackerId < 0) return;

        int eid = victim.entityId;

        // Record first-damage time only once. Subsequent hits don't reset
        // the timer — the player only gets ONE window per Specter.
        if (!_firstDamageTime.ContainsKey(eid))
        {
            _firstDamageTime[eid] = Time.time;
        }

        // Always track the latest attacker so on death we know who to
        // aggro the revenants onto. The killing-blow attacker is the
        // most recent value here (assuming no out-of-order damage events).
        _lastAttacker[eid] = attackerId;
    }

    /// <summary>
    /// Auto-bound. Triggers the kill-window check + revenant spawn.
    /// Also cleans up tracker state for the dead entity.
    /// </summary>
    void OnEntityDeath(EntityAlive victim)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;

        int eid = victim.entityId;

        // Revenants don't trigger another spawn wave — caps the cascade
        // at one generation deep.
        bool isRevenant = _revenantIds.Contains(eid);

        // Read the per-entity tracking state.
        bool hadFirstHit = _firstDamageTime.TryGetValue(eid, out var firstTime);
        bool hasAttacker = _lastAttacker.TryGetValue(eid, out var attackerId);

        // Cleanup happens regardless so dictionaries don't accumulate
        // stale entries.
        _firstDamageTime.Remove(eid);
        _lastAttacker.Remove(eid);
        _revenantIds.Remove(eid);

        if (isRevenant) return;
        if (!hadFirstHit || !hasAttacker) return;

        float elapsed = Time.time - firstTime;
        if (elapsed <= _cfg.Death.KillWindowSec) return;  // clean kill

        SpawnRevenants(victim.position, attackerId, elapsed);
    }

    private void SpawnRevenants(Vector3 deathPos, int attackerEntityId, float elapsedSec)
    {
        var world = GameManager.Instance?.World;
        if (world == null) return;

        if (!EntityClass.list.ContainsKey(_classHash))
        {
            Log.Warning("[DsSpecter] Specter class not registered, can't spawn revenants");
            return;
        }

        var attacker = world.GetEntity(attackerEntityId) as EntityAlive;
        // attacker may be null/dead by the time we get here (e.g. player
        // disconnected). We still spawn the revenants — they'll just have
        // no aggro target and behave as wandering Specters.

        int count = _cfg.Death.SpawnCount;
        int aggroTicks = _cfg.Death.AggroDurationSec * 20;
        const float spawnRingRadius = 1.5f;  // tight cluster at the death point
        int spawned = 0;

        for (int i = 0; i < count; i++)
        {
            float angle = (i / (float)count) * (float)(2.0 * Math.PI);
            Vector3 pos = deathPos + new Vector3(
                (float)Math.Cos(angle) * spawnRingRadius,
                0f,
                (float)Math.Sin(angle) * spawnRingRadius);

            // Surface Y safety. Same pattern as the other DS:Threats
            // spawn helpers — GetHeight returns top-of-solid-blocks
            // (POI floors / placed blocks / terrain), floor-cap to the
            // death position's Y so we don't drop revenants into the
            // void if death happened on raised ground.
            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float surfaceY = world.GetHeight(x, z) + 1.0f;
            pos.y = Math.Max(surfaceY, deathPos.y);

            try
            {
                var rev = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
                if (rev == null) continue;
                rev.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(rev);

                if (rev.Buffs != null)
                {
                    rev.Buffs.AddBuff(SignatureBuff);
                    rev.Buffs.AddBuff(SpawnPuffBuff);
                }

                ApplyHpOverride(rev);

                // Mark as a revenant so its own death doesn't cascade
                // another spawn wave.
                _revenantIds.Add(rev.entityId);

                // Aggro on the killer. Skip if no attacker resolvable —
                // e.g. player disconnected between damage and spawn.
                if (attacker != null && !attacker.IsDead())
                {
                    rev.SetAttackTarget(attacker, aggroTicks);
                }

                spawned++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsSpecter] Revenant spawn {0}/{1} failed: {2}", i + 1, count, e.Message);
            }
        }

        Log.Out("[DsSpecter] Kill window missed ({0:0.0}s > {1}s) -- spawned {2} revenant(s) at {3} aggro'd onto eid={4}",
            elapsedSec, _cfg.Death.KillWindowSec, spawned, deathPos, attackerEntityId);
    }

    // ============================================================ command dispatch

    private void HandleCommand(CommandContext ctx, string[] args)
    {
        var pid = ctx.Client?.PlatformId?.CombinedString;
        if (string.IsNullOrEmpty(pid))
        {
            ctx.Reply("This command requires a player context.");
            return;
        }
        if (!StyxCore.Perms.HasPermission(pid, AdminPerm))
        {
            ctx.Reply("[ff6666]Permission denied:[-] requires '" + AdminPerm + "'.");
            return;
        }

        if (args == null || args.Length == 0)
        {
            ShowHelp(ctx);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "spawn":
                int count = 1;
                if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 && n <= 20)
                    count = n;
                HandleSpawn(ctx, count);
                break;
            case "find":
                HandleFind(ctx);
                break;
            case "despawn":
                HandleDespawn(ctx);
                break;
            case "stats":
                HandleStats(ctx);
                break;
            case "help":
            default:
                ShowHelp(ctx);
                break;
        }
    }

    private void ShowHelp(CommandContext ctx)
    {
        ctx.Reply("DS:Specter prototype commands:");
        ctx.Reply("  /dsspecter spawn [count]  -- spawn N Specters in front of you (max 20)");
        ctx.Reply("  /dsspecter find           -- report distance to nearest live Specter");
        ctx.Reply("  /dsspecter despawn        -- remove all live Specters server-wide");
        ctx.Reply("  /dsspecter stats          -- count live Specters + class metadata");
    }

    // ============================================================ spawn

    private void HandleSpawn(CommandContext ctx, int count)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        if (!EntityClass.list.ContainsKey(_classHash))
        {
            ctx.Reply("[ff6666]Specter entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsSpecter] EntityClass.list missing hash {0} for '{1}'. " +
                "Confirm Mods/Styx/Config/entityclasses.xml contains <entity_class name=\"{1}\"/> " +
                "and restart the server.", _classHash, ClassName);
            return;
        }

        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + fwd * 5f;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float offset = (i - (count - 1) * 0.5f) * 2.0f;
            Vector3 pos = basePos + right * offset;

            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float surfaceY = world.GetHeight(x, z) + 1.0f;
            pos.y = Math.Max(surfaceY, caller.position.y);

            try
            {
                var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
                if (entity == null)
                {
                    Log.Warning("[DsSpecter] EntityFactory returned null for hash {0}", _classHash);
                    continue;
                }
                entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(entity);

                if (entity.Buffs != null)
                {
                    entity.Buffs.AddBuff(SignatureBuff);
                    entity.Buffs.AddBuff(SpawnPuffBuff);
                }

                ApplyHpOverride(entity);

                spawned++;
                Log.Out("[DsSpecter] Spawned at {0} (entityId={1})", pos, entity.entityId);
            }
            catch (Exception e)
            {
                Log.Error("[DsSpecter] Spawn {0} failed: {1}", i, e);
            }
        }

        ctx.Reply("[00ff66]Spawned " + spawned + " Specter(s).[-] " +
            "Trail buff '" + SignatureBuff + "' + spawn puff applied. Kill within " +
            _cfg.Death.KillWindowSec + "s of first hit or face revenants.");
    }

    /// <summary>
    /// Apply the configured MaxHealth override at spawn time. Stats.Health
    /// .BaseMax is the settable underlying max — Max is get-only and reads
    /// from BaseMax. Also reset Value so the Specter spawns at full HP.
    /// </summary>
    private void ApplyHpOverride(EntityAlive entity)
    {
        if (_cfg.MaxHealth <= 0) return;
        if (entity?.Stats?.Health == null) return;
        entity.Stats.Health.BaseMax = _cfg.MaxHealth;
        entity.Stats.Health.Value = _cfg.MaxHealth;
    }

    // ============================================================ find / despawn / stats

    private void HandleFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllSpecters(world);
        if (live.Count == 0) { ctx.Reply("No live Specters found in the world."); return; }

        EntityAlive nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var s in live)
        {
            float d = Vector3.Distance(s.position, caller.position);
            if (d < nearestDist) { nearestDist = d; nearest = s; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live Specter(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllSpecters(world);
        int removed = 0;
        foreach (var s in live)
        {
            try
            {
                world.RemoveEntity(s.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsSpecter] Despawn of entity {0} failed: {1}", s.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Specter(s).[-] (silent despawn -- no death revenant trigger)");
    }

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        ctx.Reply("[00ff66]DS:Specter status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff);
        ctx.Reply("  Spawn puff buff:   " + SpawnPuffBuff);

        ctx.Reply("[00ff66]Config[-] (Mods/Styx/Config/" + Name + ".json)");
        ctx.Reply("  Max health:        " + (_cfg.MaxHealth == 0
            ? "inherited (550 from zombieArleneFeral)"
            : _cfg.MaxHealth.ToString()));
        ctx.Reply("  Kill window:       " + _cfg.Death.KillWindowSec + "s");
        ctx.Reply("  Revenant count:    " + _cfg.Death.SpawnCount);
        ctx.Reply("  Revenant aggro:    " + _cfg.Death.AggroDurationSec + "s pursuit");
        ctx.Reply("  Trail interval:    " + _cfg.Trail.IntervalSec + "s");

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        var live = FindAllSpecters(world);
        ctx.Reply("  Live count:        " + live.Count);
        ctx.Reply("  Tracking first-hit: " + _firstDamageTime.Count);
        ctx.Reply("  Active revenants:   " + _revenantIds.Count);
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllSpecters(World world)
    {
        var result = new List<EntityAlive>();
        if (world?.Entities?.list == null) return result;

        foreach (var e in world.Entities.list)
        {
            if (e is EntityAlive ea && ea.entityClass == _classHash && !ea.IsDead())
                result.Add(ea);
        }
        return result;
    }

    // ============================================================ config POCOs

    /// <summary>
    /// Top-level config persisted to Mods/Styx/Config/DsSpecterPrototype.json.
    /// First load writes defaults; PluginWatcher hot-reloads the plugin
    /// when the file is edited (~1s after save). MaxHealth is applied
    /// at spawn time — existing live Specters keep their old HP, only
    /// freshly spawned ones (including spawn-command and revenant-spawn
    /// paths) pick up the new value.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Override for spawned Specter max HP. Set to 0 to use the
        /// inherited zombieArleneFeral default (^healthNormalFeral=550).
        /// Default 250 keeps the kill window achievable for a focused
        /// burst (a tier-1 melee + good shot or two should clear it).
        /// </summary>
        public int MaxHealth { get; set; } = 250;

        public DeathConfig Death { get; set; } = new DeathConfig();
        public TrailConfig Trail { get; set; } = new TrailConfig();
    }

    /// <summary>
    /// Trail-puff scheduler config. Each tick the plugin AddBuff's
    /// buffDsSpecterSpawnPuff to every live Specter, producing a
    /// visible 1-second puff at her current position. Sequential
    /// ticks at sequential positions = trail along her path.
    /// </summary>
    public class TrailConfig
    {
        /// <summary>
        /// Seconds between trail-puff applications. Must be > the
        /// puff buff's duration (1s in current XML) so each previous
        /// puff expires and cleans up before the next AddBuff —
        /// otherwise a same-frame Object.Destroy + re-Attach race
        /// causes the engine's transform.Find to return the still-
        /// existing GameObject and skip the create-and-play branch
        /// (intermittent missed puffs).
        /// Default 1.5s = 1s puff + 0.5s gap. Drop to 1.2 for denser
        /// trail (closer to but still beyond the 1s buff duration);
        /// raise to 2+ for sparse "ghost-footprints" pacing.
        /// </summary>
        public double IntervalSec { get; set; } = 1.5;
    }

    /// <summary>
    /// Kill-window mechanic tunables. The Specter records the time of
    /// its first damage hit; if death happens AFTER KillWindowSec from
    /// that first hit, SpawnCount revenants spawn at the corpse and
    /// immediately aggro the killer for AggroDurationSec.
    /// </summary>
    public class DeathConfig
    {
        /// <summary>
        /// Seconds between first hit and death. Kills under this threshold
        /// are clean (no revenants); kills over trigger the spawn. Default 5s.
        /// </summary>
        public float KillWindowSec { get; set; } = 5.0f;

        /// <summary>
        /// Number of revenants spawned at the corpse on a missed kill window.
        /// Default 2 matches the original design intent. Set to 0 to disable
        /// the mechanic entirely (kill-window timing is still tracked but
        /// nothing spawns).
        /// </summary>
        public int SpawnCount { get; set; } = 2;

        /// <summary>
        /// How long revenants pursue the killer after spawning (seconds).
        /// Internally converted to ticks at 20/sec for SetAttackTarget.
        /// Default 60s matches the blood-moon director's pursuit window.
        /// </summary>
        public int AggroDurationSec { get; set; } = 60;
    }
}
