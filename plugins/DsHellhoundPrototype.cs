// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Hellhound.
// Second variant in the threat catalogue. Validates the canid-mesh path
// (animalZombieDog inherits from animalWolf, not from a humanoid zombie)
// and the pack-AI pattern that future variants can reuse.
//
// Visual identity:
//   - Custom entity class (dsHellhound) extends animalZombieDog,
//     SizeScale=1.5 (vanilla dog is small; 1.5x is "noticeably bigger
//     but still recognisably a dog"). 200 HP / 400 XP.
//   - Signature buff (buffDsHellhoundFire) wraps the body in p_onFire
//     particles and attaches a miningHelmetLightSource prefab to the
//     Head bone, offset forward, so the glow tracks the head bone
//     during the run animation -- reads as glowing-eyes / forward beam.
//
// Behavioural identity:
//   - Pack-on-spawn: every /dshellhound spawn N drops N _packs_ of 3
//     (not N hounds). Lone hellhounds aren't a thing.
//   - Pack aggro: when any Hellhound takes damage, every live Hellhound
//     within 30m gets SetAttackTarget(attacker) for 60s (1200 ticks).
//     Cooldown 1.5s per Hellhound prevents sustained-DPS spam without
//     dampening the pack response.
//
// REQUIRES: shim active OR the four Config files in place. With Option A
// strip-down (no Config/ files) this prototype is non-functional because
// the entity class and buff don't auto-sync to clients.
//
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml needs to be loaded by the engine's config pass.
//
// Test plan:
//   1. /perm grant user <yourId> dshellhound.admin
//   2. /dshellhound spawn 1   -> a pack of 3 appears in front of you
//   3. Observe: 1.5x-scale zombie dogs with body fire + forward head glow
//   4. Shoot one -- the others should immediately aggro onto you
//   5. /dshellhound despawn (cleanup)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using UnityEngine;

/* @styx-entityclasses
<!--
    dsHellhound — DS:Threats canid variant.
    Inherits animalZombieDog (which itself extends animalWolf — that's the
    canid mesh + AI chain). All Mesh/Prefab/Tags/AI/dismemberment inherit;
    we only override scale, HP, and XP reward. SizeScale 1.5 is the same
    "validated visually correct on a fat zombie" number Behemoth landed
    on; canid bones are smaller so 1.5x is closer to the wolf footprint
    than to dire wolf.
-->
<entity_class name="dsHellhound" extends="animalZombieDog">
    <property name="SizeScale" value="1.5"/>
    <property name="MaxHealth" value="200"/>
    <property name="ExperienceGain" value="400"/>

    <!-- Auto-apply signature buff on first spawn. Without this,
         biome-spawned Hellhounds get the dog mesh + scale but no
         body fire / eye flames / death pyre. Behemoth's
         SpawnHelpers and DsHellhoundPrototype's HandleSpawn both
         AddBuff manually, so they were already working — this
         trigger just covers the natural biome-spawn path. -->
    <effect_group name="DS Hellhound Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsHellhoundFire"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups ZombieDogGroup
<!-- Kept for non-biome spawn paths: Behemoth helper-spawn (which
     uses Helpers.Class config to pick from the dog group), plus
     any quest/twitch/sleeper contexts that reference ZombieDogGroup
     by name. NB: ZombieDogGroup is NOT referenced by any
     biome spawn rule in vanilla spawning.xml (verified via grep) —
     so this entry alone doesn't get Hellhounds into biome wandering
     spawns. The DsCanidsBiome cohort below is the biome integration. -->
dsHellhound, .15
*/

/* @styx-entitygroups DsCanidsBiome new
<!-- Hellhound's contribution to the DS canid cohort. The `new` flag
     creates the entitygroup (DsCanidsBiome is not in vanilla);
     future canid plugins (Gorehound etc) target the same cohort
     without the `new` flag — the synthesiser OR-merges new-status
     per group across all contributors.
     Kept separate from the humanoid DsThreatsBiome cohort so canids
     have their own per-biome spawn budget — a chunk's "DS spawn slot"
     should be either a humanoid or a canid, never decided by the same
     roll where a 0.5 Hellhound would dominate over Behemoth's 0.15.
     Biome→DsCanidsBiome routing declared in DsBehemothPrototype's
     @styx-spawning blocks (alongside the humanoid cohort routing).
     Weight 1.0 means Hellhound is the only canid currently. -->
dsHellhound, 1
*/

/* @styx-buffs
<buff name="buffDsHellhoundFire"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group name="DS Hellhound Fire">
        <!-- Body fire. parent_transform=".body" on an animal entity
             resolves through MinEventActionAttachParticleEffectToEntity's
             animal branch (decomp lines 87-95) to GetPelvisTransform()
             with a 90° rotation tweak — the canonical "body centre" for
             quadrupeds. shape_mesh="true" was REMOVED from this trigger:
             the canid pelvis bone has no SkinnedMeshRenderer (that lives
             on the parent mesh-graph node, not on individual bones), so
             the engine logged "AttachParticleEffectToEntity ... no renderer!"
             every spawn and fell back to a Sphere shape anyway. Dropping
             the flag silences the warning and the visual is identical —
             p_onFire emits as a sphere of fire particles around the dog's
             body centre. The earlier comment claiming "burnt-zombie
             variants exist on quadrupeds" was wrong: vanilla has no
             burnt zombie dogs, only humanoid burnt variants. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>

        <!-- Burning eyes. Earlier prototype attached a miningHelmetLightSource
             prefab here, but that prefab ships with the bulb housing geometry
             visible (it's normally hidden inside a player helmet visor); when
             attached standalone it shows the bulb mesh as a floating disc in
             front of the dog's face and the actual eyes don't glow.
             Replacement: two small flame particles (p_fire_flaming_arrow,
             vanilla flame-arrow head — verified at items.xml:8919) attached
             to the Head bone, offset to the eye positions. The particles
             emit warm light + visible flickering flame and have no fixed
             mesh, so they read cleanly as burning eye sockets and tint the
             dog's muzzle on the body-fire lit pass. Dog rig uses bone-name
             "Head" (vanilla bucket-on-head buff at buffs.xml:15420 confirmed
             this); offsets are eyeballed from typical canid skull geometry:
             ~5cm sideways from skull mid-line, ~6cm above the Head pivot
             (which sits at the base of the skull), ~15cm forward to clear
             the muzzle. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_fire_flaming_arrow" parent_transform="Head"
            local_offset="0.05,0.06,0.15"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_fire_flaming_arrow" parent_transform="Head"
            local_offset="0.05,0.06,0.15"/>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_fire_flaming_arrow" parent_transform="Head"
            local_offset="-0.05,0.06,0.15"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_fire_flaming_arrow" parent_transform="Head"
            local_offset="-0.05,0.06,0.15"/>

        <!-- Spawn puff used to live here as an oneshot trigger sharing
             this persistent buff. Moved to its own 1-second
             buffDsHellhoundSpawnPuff below for cleaner lifecycle: a
             dedicated short-duration buff with explicit Remove on
             expiration mirrors the buffDsBehemothSummon pattern and
             can't accidentally persist if the engine ever re-fires
             onSelfBuffStart on view-distance / sync events. The
             plugin's spawn paths (SpawnPackAt + Behemoth's helper
             summon) AddBuff this alongside the fire wrap. -->

        <!-- Death pyre — molotov-style ignition at the body. Two parts:
             (1) This Explode delivers the immediate fire-damage burst (40
                 damage / 3m radius / damage_type Heat). The valid enum is
                 EnumDamageTypes (decomp/EnumDamageTypes.cs) which has no
                 'Fire' member — Heat IS the fire-damage type and is also
                 MinEventActionExplode.damageType's default. No block
                 damage / no blast power — we don't want canids to crater
                 the floor every time one dies, just torch nearby
                 mobs/players.
             (2) The C# OnEntityDeath hook (DsHellhoundPrototype.cs) finds
                 EntityAlive within the same 3m radius and applies vanilla
                 'buffBurningMolotov' to each — the same 16s fire DoT a
                 real molotov delivers on impact. Pack-mates are excluded
                 from the buff scan so kill-one-pyre-the-others isn't a
                 cascade. The corpse continues to burn from p_onFire on
                 .body for the corpse-decay duration, providing the
                 "fire stays at the spot" visual anchor. -->
        <triggered_effect trigger="onSelfDied" action="Explode"
            blast_power="0"
            block_damage="0" block_radius="0"
            entity_damage="40" entity_radius="3"
            damage_type="Heat"/>
    </effect_group>
</buff>

<!-- One-shot spawn-puff buff. Mirrors buffDsBehemothSummon's pattern:
     1-second duration, replace stack, attach particle on buff start,
     explicit RemoveParticleEffectFromEntity on buff end (so the GameObject
     created at .body doesn't linger past the puff's natural fade) and on
     death (so corpses don't carry a half-faded puff into despawn).
     Applied alongside buffDsHellhoundFire by every spawn path
     (DsHellhoundPrototype.SpawnPackAt and DsBehemothPrototype.SpawnHelpers
     when helpers are dsHellhound). Hides the materialisation pop in a
     small grey cloud — canid pivots are smaller than humanoid pivots so
     a single p_twitch_smokePuff visually contains the dog. -->
<buff name="buffDsHellhoundSpawnPuff"
      icon="ui_game_symbol_skull"
      hidden="true">
    <stack_type value="replace"/>
    <duration value="1"/>
    <effect_group>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_smokePuff" parent_transform=".body"/>
        <triggered_effect trigger="onSelfBuffRemove" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
        <triggered_effect trigger="onSelfDied" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
    </effect_group>
</buff>
*/

[Info("DsHellhoundPrototype", "Doowkcol", "0.1.0")]
public class DsHellhoundPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Hellhound visual+pack-AI prototype (internal).";

    // ============================================================ constants

    /// <summary>Entity class name as registered in entityclasses.xml.</summary>
    private const string ClassName = "dsHellhound";

    /// <summary>Signature buff applied to every spawned Hellhound.</summary>
    private const string SignatureBuff = "buffDsHellhoundFire";

    /// <summary>One-shot spawn-puff buff applied alongside the signature
    /// buff at every spawn. 1-second duration with explicit cleanup, so
    /// the puff appears once at materialisation and disappears cleanly
    /// regardless of any engine sync re-fire of the persistent buff's
    /// onSelfBuffStart trigger. Other plugins reference this buff by its
    /// XML name (string), not via this constant — see DsBehemothPrototype's
    /// HelpersConfig.SpawnPuffBuff which defaults to "buffDsHellhoundSpawnPuff".</summary>
    private const string SpawnPuffBuff = "buffDsHellhoundSpawnPuff";

    /// <summary>Pack size per spawn command. Behemoths spawn alone; Hellhounds
    /// always come as a pack — that's the threat identity. 3 is small enough
    /// not to nuke a player on a single spawn but big enough to feel like a
    /// pack rather than a duo.</summary>
    private const int PackSize = 3;

    /// <summary>Spread radius for the pack on spawn (so they don't overlap).</summary>
    private const float PackSpawnRadius = 2.5f;

    /// <summary>Pack-aggro radius. When any Hellhound is hurt, every live
    /// Hellhound within this many metres turns on the attacker. 30m covers
    /// a typical engagement range without aggroing the entire biome.</summary>
    private const float PackAggroRadius = 30f;

    /// <summary>SetAttackTarget duration in ticks. Vanilla blood-moon
    /// director uses 1200 (60s @ 20 ticks/s). BossGroup uses 60000 for
    /// minions which is overkill — 1200 means the pack drops aggro after
    /// ~60s of not seeing the attacker, which feels right for a hunt.</summary>
    private const int AttackTargetDurationTicks = 1200;

    /// <summary>Per-Hellhound cooldown on triggering pack-aggro to avoid
    /// thousands of SetAttackTarget calls per second under sustained DPS.
    /// 1.5s is short enough that a fresh attacker still gets a pack response
    /// after the first hit.</summary>
    private const float PackAggroCooldownSec = 1.5f;

    /// <summary>Death-pyre AOE radius for the buffBurningMolotov scan in
    /// OnEntityDeath. Matches the entity_radius on the XML Explode trigger
    /// so the visible blast and the buff application cover the same area;
    /// also matches vanilla molotov's positionAOE range (2.7m) loosely.</summary>
    private const float DeathPyreRadius = 3f;

    /// <summary>Vanilla buff name applied by molotov projectile impacts.
    /// 16s fire DoT (configurable via %buffBurningMolotovDuration cvar).
    /// Verified at buffs.xml:6716. Re-using the vanilla buff means the
    /// fire visuals, sound, resistance interactions, and "extinguish in
    /// water" mechanics all just work.</summary>
    private const string MolotovBurnBuff = "buffBurningMolotov";

    /// <summary>Permission required to use any /dshellhound subcommand.</summary>
    private const string AdminPerm = "dshellhound.admin";

    private int _classHash;

    /// <summary>Per-Hellhound cooldown tracker for pack-aggro broadcasts.
    /// Keyed by entityId; value is Time.time of the last aggro broadcast.
    /// Cleaned via OnEntityDeath.</summary>
    private readonly Dictionary<int, float> _lastAggroTime = new Dictionary<int, float>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _classHash = ClassName.GetHashCode();
        Log.Out("[DsHellhound] Prototype loaded. Class={0} hash={1} buff={2} pack={3}",
            ClassName, _classHash, SignatureBuff, PackSize);

        StyxCore.Commands.Register("dshellhound",
            "DS:Threats Hellhound prototype -- /dshellhound <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        _lastAggroTime.Clear();
        Log.Out("[DsHellhound] Prototype unloaded.");
    }

    // ============================================================ damage hook (pack aggro)

    /// <summary>
    /// Auto-bound. Fires on EntityAlive.ProcessDamageResponse — the universal
    /// damage choke point covering melee, gunshots delivered via
    /// NetPackageDamageEntity, buff DoT ticks, fall damage, etc. The
    /// previous OnEntityDamage hook (DamageEntity prefix) was bypassed by
    /// client-originated bullet damage, so pack-aggro fired only for melee
    /// and server-side damage paths. ProcessDamageResponse is the shared
    /// finalisation point, so OnPreDamageApplied fires reliably for every
    /// damage path including bullets.
    ///
    /// Any Hellhound being damaged broadcasts a pack-aggro signal to every
    /// live Hellhound in PackAggroRadius — the pack converges on the
    /// attacker for ~60s before AI naturally drops the target.
    /// </summary>
    void OnPreDamageApplied(EntityAlive victim, DamageResponse response)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        if (victim.IsDead()) return;

        // Cooldown: avoid SetAttackTarget storms under sustained DPS.
        int eid = victim.entityId;
        float now = Time.time;
        if (_lastAggroTime.TryGetValue(eid, out var last) && now - last < PackAggroCooldownSec)
            return;
        _lastAggroTime[eid] = now;

        // Resolve the attacker entity from the damage source. getEntityId
        // returns the canonical "who hit me" id; missing/no-attacker damage
        // (DoT, environment, fall) returns < 0 — nothing to aggro onto.
        // DamageResponse is a struct (value type) so it can't be null —
        // only the inner Source (DamageSource, a class) needs the
        // null-conditional. response?.Source is a CS0023 error.
        int attackerId = response.Source?.getEntityId() ?? -1;
        if (attackerId < 0) return;

        var world = GameManager.Instance?.World;
        if (world == null) return;
        var attacker = world.GetEntity(attackerId) as EntityAlive;
        if (attacker == null || attacker.IsDead()) return;

        BroadcastPackAggro(victim, attacker);
    }

    /// <summary>
    /// Auto-bound. Two responsibilities:
    ///   (1) Cleanup the per-Hellhound cooldown tracker so the dictionary
    ///       doesn't grow unbounded.
    ///   (2) Death pyre — apply vanilla buffBurningMolotov to every
    ///       EntityAlive within DeathPyreRadius of the corpse (excluding
    ///       other Hellhounds, so kill-one-cascades-the-pack doesn't
    ///       happen). Pairs with the XML onSelfDied Explode trigger
    ///       which delivers the immediate fire damage burst; this hook
    ///       adds the lingering 16s burn DoT to anyone caught at death.
    /// </summary>
    void OnEntityDeath(EntityAlive victim)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        _lastAggroTime.Remove(victim.entityId);

        ApplyDeathPyre(victim);
    }

    private void ApplyDeathPyre(EntityAlive victim)
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return;

        Vector3 center = victim.position;
        float radSq = DeathPyreRadius * DeathPyreRadius;
        int torched = 0;

        foreach (var e in world.Entities.list)
        {
            if (!(e is EntityAlive ea)) continue;
            // Don't immolate pack-mates -- avoids cascade kills when a
            // pack dies tightly clustered. Players, vanilla zombies,
            // animals, future DS variants all get caught.
            if (ea.entityClass == _classHash) continue;
            if (ea.IsDead()) continue;
            if ((ea.position - center).sqrMagnitude > radSq) continue;
            if (ea.Buffs == null) continue;

            try
            {
                ea.Buffs.AddBuff(MolotovBurnBuff);
                torched++;
            }
            catch (Exception ex)
            {
                Log.Warning("[DsHellhound] Death-pyre buff apply on entity {0} failed: {1}",
                    ea.entityId, ex.Message);
            }
        }

        if (torched > 0)
        {
            Log.Out("[DsHellhound] Hellhound {0} death pyre torched {1} entit(ies) at {2}",
                victim.entityId, torched, center);
        }
    }

    private void BroadcastPackAggro(EntityAlive triggeringHound, EntityAlive attacker)
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return;

        Vector3 center = triggeringHound.position;
        float radSq = PackAggroRadius * PackAggroRadius;
        int responded = 0;

        foreach (var e in world.Entities.list)
        {
            if (!(e is EntityAlive other)) continue;
            if (other.entityClass != _classHash) continue;
            if (other.IsDead()) continue;
            if ((other.position - center).sqrMagnitude > radSq) continue;

            try
            {
                other.SetAttackTarget(attacker, AttackTargetDurationTicks);
                responded++;
            }
            catch (Exception ex)
            {
                Log.Warning("[DsHellhound] SetAttackTarget on {0} failed: {1}", other.entityId, ex.Message);
            }
        }

        if (responded > 1)
        {
            Log.Out("[DsHellhound] Pack aggro: {0} hounds converging on entity {1} (radius {2}m)",
                responded, attacker.entityId, PackAggroRadius);
        }
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
                int packs = 1;
                if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 && n <= 10)
                    packs = n;
                HandleSpawn(ctx, packs);
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
        ctx.Reply("DS:Hellhound prototype commands:");
        ctx.Reply("  /dshellhound spawn [packs]  -- spawn N packs of " + PackSize + " (max 10 packs)");
        ctx.Reply("  /dshellhound find           -- report distance to nearest live Hellhound");
        ctx.Reply("  /dshellhound despawn        -- remove all live Hellhounds server-wide");
        ctx.Reply("  /dshellhound stats          -- count live Hellhounds + class metadata");
    }

    // ============================================================ spawn

    private void HandleSpawn(CommandContext ctx, int packs)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        if (!EntityClass.list.ContainsKey(_classHash))
        {
            ctx.Reply("[ff6666]Hellhound entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsHellhound] EntityClass.list missing hash {0} for '{1}'. " +
                "Confirm Mods/Styx/Config/entityclasses.xml contains <entity_class name=\"{1}\"/> " +
                "and restart the server.", _classHash, ClassName);
            return;
        }

        // One pack centre 5m in front of caller; subsequent packs offset along
        // the right vector by 8m so they don't overlap with each other.
        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 firstCenter = caller.position + fwd * 5f;

        int totalSpawned = 0;
        for (int p = 0; p < packs; p++)
        {
            float packOffset = (p - (packs - 1) * 0.5f) * 8f;
            Vector3 packCenter = firstCenter + right * packOffset;
            totalSpawned += SpawnPackAt(world, packCenter, caller.position.y);
        }

        ctx.Reply("[00ff66]Spawned " + totalSpawned + " Hellhound(s) in " + packs + " pack(s).[-] " +
            "Aura buff '" + SignatureBuff + "' applied. Damage one to trigger pack aggro.");
    }

    /// <summary>
    /// Spawn a pack of <see cref="PackSize"/> Hellhounds in a small ring
    /// around <paramref name="center"/>. <paramref name="floorY"/> is the
    /// caller's Y (or any known-safe surface Y) used as a minimum so we
    /// don't drop the pack into the void when they spawn under a POI.
    /// Returns the count actually spawned.
    ///
    /// Public-shaped (private but stable signature) so a future DS:Classes
    /// integration can summon a friendly pack via the same code path -- the
    /// "friendly" part will be a Buffs.AddBuff(... ownerEntity ...) tweak,
    /// not a duplicate spawner.
    /// </summary>
    private int SpawnPackAt(World world, Vector3 center, float floorY)
    {
        int spawned = 0;
        for (int i = 0; i < PackSize; i++)
        {
            float angle = (i / (float)PackSize) * (float)(2.0 * Math.PI);
            Vector3 pos = center + new Vector3(
                (float)Math.Cos(angle) * PackSpawnRadius,
                0f,
                (float)Math.Sin(angle) * PackSpawnRadius);

            // Surface Y resolution. World.GetHeight(x,z) returns the top of the
            // highest *solid* block at that XZ -- includes POI floors, placed
            // blocks, terrain. World.GetTerrainHeight returns only procedural
            // terrain, which puts spawns under POI floors / inside roads /
            // below the player when they're on any non-terrain surface.
            // The +1.0f offset gives the canid collision capsule clearance to
            // settle (the dog's pivot sits at body-centre, not feet, so a
            // tighter offset clips half-buried). Floor-cap to caller Y so we
            // don't drop the pack into the void if the spawn XZ samples a low
            // adjacent terrain block while the caller is on raised ground.
            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float surfaceY = world.GetHeight(x, z) + 1.0f;
            pos.y = Math.Max(surfaceY, floorY);

            try
            {
                var hound = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
                if (hound == null)
                {
                    Log.Warning("[DsHellhound] EntityFactory returned null/non-EntityAlive for hash {0}", _classHash);
                    continue;
                }
                hound.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(hound);

                if (hound.Buffs != null)
                {
                    hound.Buffs.AddBuff(SignatureBuff);
                    // Spawn puff lives in its own short-duration buff (see
                    // @styx-buffs block). Applied immediately after the
                    // persistent fire wrap so the visual layering is
                    // puff -> reveal-burning-dog as the puff fades.
                    hound.Buffs.AddBuff(SpawnPuffBuff);
                }
                else Log.Warning("[DsHellhound] Spawned entity {0} but Buffs is null", hound.entityId);

                spawned++;
            }
            catch (Exception e)
            {
                Log.Error("[DsHellhound] Pack spawn {0}/{1} failed: {2}", i + 1, PackSize, e);
            }
        }
        return spawned;
    }

    // ============================================================ find

    private void HandleFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllHellhounds(world);
        if (live.Count == 0) { ctx.Reply("No live Hellhounds found in the world."); return; }

        EntityAlive nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var h in live)
        {
            float d = Vector3.Distance(h.position, caller.position);
            if (d < nearestDist) { nearestDist = d; nearest = h; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live Hellhound(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    // ============================================================ despawn

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllHellhounds(world);
        int removed = 0;
        foreach (var h in live)
        {
            try
            {
                world.RemoveEntity(h.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsHellhound] Despawn of entity {0} failed: {1}", h.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Hellhound(s).[-] (silent despawn)");
    }

    // ============================================================ stats

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        ctx.Reply("[00ff66]DS:Hellhound status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff);
        ctx.Reply("  Pack size:         " + PackSize);
        ctx.Reply("  Pack aggro radius: " + PackAggroRadius + "m");

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        var live = FindAllHellhounds(world);
        ctx.Reply("  Live count:        " + live.Count);
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllHellhounds(World world)
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
}
