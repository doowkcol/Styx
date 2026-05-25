// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Demon.
// Mid-tier humanoid threat sitting between basic zombies and the
// Behemoth. Validates the LeftHand / RightHand bone-attachment path
// (we'd done body-mesh wraps on Behemoth + Hellhound and Head-bone
// attachments on Hellhound eyes; the hand-fire pattern is new).
//
// Visual identity:
//   - Custom entity class (zombieDsDemon) extends zombieSkateboarderInfernal
//     (the "thug" mesh family — its asset path is literally
//     @:Entities/Zombies/Thug/ZThugRadiated.prefab). Infernal tier brings
//     red/orange MatColor (palette complements fire), 1700 HP, 1800 XP,
//     buffRadiatedRegen on hit (boss-feel hit-back regen), 75% knockdown
//     resist, infernalMoveSpeedPattern (faster), meleeHandZombieInfernal
//     (stronger melee). NB: the Radiated tier in this lineage uses
//     MatColor + alt materials for the green tint, NOT a particle aura
//     (the RadiatedParticlesOnMesh line is commented out in vanilla);
//     Infernal overrides MatColor to "infernal" so there's no green/fire
//     visual conflict. SizeScale=1.2 carves out a mid-boss silhouette
//     between vanilla (1.0) and Behemoth (1.5).
//   - Signature buff (buffDsDemonFire) layers:
//       * p_onFire body wrap (.body + shape_mesh=true on the humanoid
//         SDCS rig — same proven recipe as Behemoth's radiation aura)
//       * p_twitch_zombie_fire_left at the LeftHand bone (vanilla
//         flame-arm particle, used in twitch-action zombies)
//       * p_twitch_zombie_fire_right at the RightHand bone
//     Result: burning humanoid with flaming hands, distinct silhouette
//     even from a distance.
//   - Death effect: Explode action with damage_type=Heat for a fire
//     burst on death — smaller than the Behemoth's crater-grade kaboom
//     because the Demon is mid-tier, not boss-grade.
//   - Spawn puff: dedicated 1-second buffDsDemonSpawnPuff masks the
//     materialisation pop, same pattern proven on Hellhound.
//
// Behavioural identity:
//   - "Wrath call" on damage: when a Demon takes damage, every
//     EntityEnemy (zombies, zombie dogs, vultures, hostile animals)
//     within 50m is sent to attack the source. Hooked on
//     OnPreDamageApplied so client-originated bullet packets
//     (NetPackageDamageEntity -> ProcessDamageResponse, bypasses the
//     DamageEntity prefix) trigger reliably. 2-second per-Demon
//     cooldown prevents broadcast storms under sustained DPS.
//   - Solitary spawn pattern (no pack). The Behemoth's Helpers config
//     can spawn Demons by setting Class=zombieDsDemon, Buff=buffDsDemonFire,
//     SpawnPuffBuff=buffDsDemonSpawnPuff (existing knobs, no code change).
//
// REQUIRES: shim active OR the four Config files in place.
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml needs to be loaded by the engine's config pass.
//
// Test plan:
//   1. /perm grant user <yourId> dsdemon.admin
//   2. /dsdemon spawn 1   -> a single Demon appears with body fire +
//                           flaming hands + spawn puff. Skin tint should
//                           be Infernal red/orange (MatColor=infernal).
//   3. Spawn a few vanilla zombies nearby (within 50m) — for example
//      via standard /spawnentity zombieMoe, several spread around.
//   4. Hit the Demon once: every nearby zombie should converge on you
//      (the wrath call). Server log: "[DsDemon] Wrath call: ... pulled
//      N hostile(s) onto attacker eid=..."
//   5. Engage in melee — Infernal aggression, 1700 HP, regenerates
//      when hit, hard to stagger (75% knockdown resist).
//   6. Kill it: small fire burst at the death position. 1800 XP awarded.
//   7. /dsdemon despawn (cleanup)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsDemon — DS:Threats humanoid mid-boss variant.
    Inherits zombieSkateboarderInfernal (the "thug" mesh family — vanilla
    asset path is @:Entities/Zombies/Thug/ZThugRadiated.prefab). Infernal
    tier brings the right stat profile for free: 1700 HP, 1800 XP,
    buffRadiatedRegen on hit, 75% knockdown resist, faster move pattern,
    stronger melee. NB checked the Radiated tier inheritance — its visual
    is achieved via MatColor + alt materials, NOT a persistent particle
    aura (the RadiatedParticlesOnMesh line is commented out in vanilla
    entityclasses.xml line 3897), and Infernal overrides MatColor to
    "infernal" so there's no green/fire conflict. SizeScale 1.2 sits
    between vanilla 1.0 and Behemoth 1.5 so the silhouette reads as
    mid-boss at a glance. HP/XP overrides intentionally omitted — the
    inherited Infernal tier already lands these in the right tier band.
-->
<entity_class name="zombieDsDemon" extends="zombieSkateboarderInfernal">
    <property name="SizeScale" value="1.2"/>

    <!-- Auto-apply signature buff on first spawn (biome-spawned Demons
         bypass the C# HandleSpawn path that AddBuff's the fire wrap).
         See DsBehemothPrototype for the rationale. -->
    <effect_group name="DS Demon Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsDemonFire"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Demon contributes ONLY to the high-tier cohort. Excluded from
     pine_forest (which references the starter-friendly DsThreatsLow
     cohort) — Demon's wrath-call mechanic that pulls every nearby
     hostile onto the player would be brutal for a fresh-spawn
     character. Available in burnt_forest and harder biomes. -->
zombieDsDemon, .25
*/

/* @styx-buffs
<buff name="buffDsDemonFire"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group name="DS Demon Fire">
        <!-- Body fire. Same .body + shape_mesh=true recipe that worked on
             Behemoth (humanoid SDCS rig — the engine's animal branch is
             the one that bombs out without a SkinnedMeshRenderer; the
             SDCS branch resolves to the body/torso mesh node and the
             flag is honoured). p_onFire wraps the corporate suit in
             flames — high contrast against the clean base mesh. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body" shape_mesh="true"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body" shape_mesh="true"/>

        <!-- Flaming hands. p_twitch_zombie_fire_left/right are the vanilla
             twitch-action flame-arm particles (verified in vanilla buffs.xml
             at lines 15205-15221 — used on twitch-spawn zombies for a
             "claws-on-fire" look). The Left/Right bone names are the
             standard humanoid rig hand bones; same convention used by the
             vanilla iron-bucket-on-head buff for hand attachments. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_fire_left" parent_transform="LeftHand"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_fire_left" parent_transform="LeftHand"/>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_fire_right" parent_transform="RightHand"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_fire_right" parent_transform="RightHand"/>

        <!-- Death burst. Fire-damage AOE on death (damage_type=Heat is
             EnumDamageTypes.Heat, the valid fire member — there is no
             "Fire" enum value). Smaller than Behemoth's death (mid-tier
             threat, not boss-grade): 30 damage / 3m radius / no block
             damage. NB Explode's entity_radius is parsed via
             StringParsers.ParseSInt32 (decomp MinEventActionExplode.cs:80)
             — INTEGER ONLY. A decimal like 2.5 throws "Did not parse
             entire string" and cascades the entire buffs.xml parse
             offline, killing every Styx-managed buff. Keep as integer.
             The corpse continues to burn from the body-fire wrap for
             the corpse-decay duration, providing a visual anchor at
             the death spot. -->
        <triggered_effect trigger="onSelfDied" action="Explode"
            blast_power="0"
            block_damage="0" block_radius="0"
            entity_damage="30" entity_radius="3"
            damage_type="Heat"/>
    </effect_group>
</buff>

<!-- One-shot spawn-puff buff. Same pattern as buffDsHellhoundSpawnPuff /
     buffDsBehemothSummon: 1-second duration with explicit
     RemoveParticleEffectFromEntity on buff end so the puff GameObject
     can't linger if the prefab self-destructs after its particle
     finishes. Spine1 is the canonical torso bone on humanoid SDCS rigs
     (verified working at vanilla buffs.xml:6498 for p_electric_shock). -->
<buff name="buffDsDemonSpawnPuff"
      icon="ui_game_symbol_skull"
      hidden="true">
    <stack_type value="replace"/>
    <duration value="1"/>
    <effect_group>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_smokePuff" parent_transform="Spine1"/>
        <triggered_effect trigger="onSelfBuffRemove" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
        <triggered_effect trigger="onSelfDied" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
    </effect_group>
</buff>
*/

[Info("DsDemonPrototype", "Doowkcol", "0.1.0")]
public class DsDemonPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Demon mid-boss humanoid prototype (internal).";

    // ============================================================ constants

    /// <summary>Entity class name as registered in entityclasses.xml.</summary>
    private const string ClassName = "zombieDsDemon";

    /// <summary>Signature buff applied to every spawned Demon.</summary>
    private const string SignatureBuff = "buffDsDemonFire";

    /// <summary>Spawn-puff buff applied alongside the signature buff.</summary>
    private const string SpawnPuffBuff = "buffDsDemonSpawnPuff";

    /// <summary>Permission required to use any /dsdemon subcommand.</summary>
    private const string AdminPerm = "dsdemon.admin";

    // Tunables (HP, wrath radius/cooldown/duration) live in
    // _cfg, loaded from Mods/Styx/Config/DsDemonPrototype.json at OnLoad.
    // Edit the JSON to retune; PluginWatcher reloads the plugin on file
    // change (OnUnload -> OnLoad cycle re-reads cfg). See the Config /
    // WrathConfig POCOs at the bottom of this file.
    private Config _cfg;

    private int _classHash;

    /// <summary>Per-Demon-instance cooldown tracker for the wrath-call
    /// broadcast. Keyed by entityId; value is Time.time of the last
    /// broadcast. Cleaned on entity death via OnEntityDeath.</summary>
    private readonly Dictionary<int, float> _lastAggroTime = new Dictionary<int, float>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        Log.Out("[DsDemon] Prototype loaded. Class={0} hash={1} buff={2} " +
                "hp={3} wrath=(radius={4}m cooldown={5}s duration={6}s)",
            ClassName, _classHash, SignatureBuff,
            _cfg.MaxHealth == 0 ? "inherited" : _cfg.MaxHealth.ToString(),
            _cfg.Wrath.Radius, _cfg.Wrath.CooldownSec, _cfg.Wrath.DurationSec);

        StyxCore.Commands.Register("dsdemon",
            "DS:Threats Demon prototype -- /dsdemon <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        _lastAggroTime.Clear();
        Log.Out("[DsDemon] Prototype unloaded.");
    }

    // ============================================================ damage hook (wrath call)

    /// <summary>
    /// Auto-bound. Hooked on EntityAlive.ProcessDamageResponse via the
    /// framework's OnPreDamageApplied — covers every damage path
    /// including client-originated bullet packets (NetPackageDamageEntity
    /// dispatches directly to ProcessDamageResponse, bypassing the
    /// DamageEntity prefix). Demons hit by anything trigger a 50m
    /// "wrath call" — every nearby EntityEnemy (zombies, hostile animals,
    /// vultures) is sent to attack the source.
    ///
    /// Returns void (observe-only) so the hit lands at full strength.
    /// </summary>
    void OnPreDamageApplied(EntityAlive victim, DamageResponse response)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        if (victim.IsDead()) return;

        // Cooldown gate. Without this, sustained DPS would re-broadcast
        // SetAttackTarget every tick — wasteful and floods the AI.
        int eid = victim.entityId;
        float now = Time.time;
        if (_lastAggroTime.TryGetValue(eid, out var last) && now - last < _cfg.Wrath.CooldownSec)
            return;
        _lastAggroTime[eid] = now;

        // Resolve the attacker. DamageResponse is a struct (CS0023 if you
        // null-check it) but Source inside is a class reference, hence the
        // single-level null-conditional. getEntityId returns < 0 for
        // sourceless damage (DoT, environment, fall) — nothing to aggro on.
        int attackerId = response.Source?.getEntityId() ?? -1;
        if (attackerId < 0) return;

        var world = GameManager.Instance?.World;
        if (world == null) return;
        var attacker = world.GetEntity(attackerId) as EntityAlive;
        if (attacker == null || attacker.IsDead()) return;

        BroadcastWrathCall(victim, attacker);
    }

    /// <summary>
    /// Auto-bound. Cleanup so the cooldown dictionary doesn't grow
    /// unbounded across uptime as Demons live and die.
    /// </summary>
    void OnEntityDeath(EntityAlive victim)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        _lastAggroTime.Remove(victim.entityId);
    }

    /// <summary>
    /// Walk the entity list, find every <see cref="EntityEnemy"/> within
    /// _cfg.Wrath.Radius of the wounded Demon, and call SetAttackTarget on
    /// each. EntityEnemy is the base for zombies (via EntityHuman), zombie
    /// dogs (via EntityEnemyAnimal), vultures (via EntityFlying), and
    /// hostile humans — players and friendly animals (chickens, deer,
    /// rabbits) inherit different bases so they're cleanly excluded.
    /// </summary>
    private void BroadcastWrathCall(EntityAlive demon, EntityAlive attacker)
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return;

        // Snapshot config to locals so a mid-broadcast config reload
        // can't change the radius / duration mid-iteration.
        float radius = _cfg.Wrath.Radius;
        // Vanilla AI uses 20 tick/sec for SetAttackTarget durations; Wrath.DurationSec
        // is the user-friendly seconds value we expose in the JSON.
        int durationTicks = _cfg.Wrath.DurationSec * 20;

        Vector3 center = demon.position;
        float radSq = radius * radius;
        int aggroed = 0;

        foreach (var e in world.Entities.list)
        {
            if (!(e is EntityEnemy enemy)) continue;
            if (enemy.entityId == demon.entityId) continue;   // skip self
            if (enemy.entityId == attacker.entityId) continue; // skip the attacker (don't aggro them onto themselves)
            if (enemy.IsDead()) continue;
            if ((enemy.position - center).sqrMagnitude > radSq) continue;

            try
            {
                enemy.SetAttackTarget(attacker, durationTicks);
                aggroed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[DsDemon] SetAttackTarget on {0} failed: {1}", enemy.entityId, ex.Message);
            }
        }

        if (aggroed > 0)
        {
            Log.Out("[DsDemon] Wrath call: Demon eid={0} pulled {1} hostile(s) onto attacker eid={2} (radius {3}m)",
                demon.entityId, aggroed, attacker.entityId, radius);
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
        ctx.Reply("DS:Demon prototype commands:");
        ctx.Reply("  /dsdemon spawn [count]  -- spawn N Demons in front of you (max 20)");
        ctx.Reply("  /dsdemon find           -- report distance to nearest live Demon");
        ctx.Reply("  /dsdemon despawn        -- remove all live Demons server-wide");
        ctx.Reply("  /dsdemon stats          -- count live Demons + class metadata");
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
            ctx.Reply("[ff6666]Demon entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsDemon] EntityClass.list missing hash {0} for '{1}'. " +
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
            // Spread spacing 2.5m apart (Demons are 1.2x scale so a bit more
            // breathing room than vanilla but tighter than the 8m pack-spacing
            // we used for Hellhound packs).
            float offset = (i - (count - 1) * 0.5f) * 2.5f;
            Vector3 pos = basePos + right * offset;

            // Surface Y resolution. World.GetHeight returns the top of the
            // highest *solid* block (POI floors / placed blocks / terrain);
            // GetTerrainHeight only returns procedural terrain Y, which puts
            // spawns under POI floors. Floor-cap to caller Y so we don't drop
            // them into the void if the spawn XZ samples a low adjacent
            // block while the caller is on raised ground.
            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float surfaceY = world.GetHeight(x, z) + 1.0f;
            pos.y = Math.Max(surfaceY, caller.position.y);

            try
            {
                // zombieBusinessManFeral is in the EntityZombie family
                // (humanoid SDCS rig), so cast to EntityAlive (parent type)
                // is safe and avoids the EntityZombie-vs-EntityEnemyAnimal
                // mismatch we hit on canids.
                var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
                if (entity == null)
                {
                    Log.Warning("[DsDemon] EntityFactory returned null for hash {0}", _classHash);
                    continue;
                }
                entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(entity);

                if (entity.Buffs != null)
                {
                    entity.Buffs.AddBuff(SignatureBuff);
                    entity.Buffs.AddBuff(SpawnPuffBuff);
                }
                else
                {
                    Log.Warning("[DsDemon] Spawned entity {0} but Buffs is null", entity.entityId);
                }

                // HP override. The entity_class inherits ^healthNormalInfernal
                // (1700) by default — _cfg.MaxHealth=0 means "use inherited
                // value, no override". Any positive value writes through
                // Stats.Health.BaseMax (which is the settable max — Max is
                // get-only and reads from BaseMax). Also reset Value so the
                // demon spawns at full health regardless of the previous
                // m_value the engine wrote during construction.
                if (_cfg.MaxHealth > 0 && entity.Stats?.Health != null)
                {
                    entity.Stats.Health.BaseMax = _cfg.MaxHealth;
                    entity.Stats.Health.Value = _cfg.MaxHealth;
                }

                spawned++;
                Log.Out("[DsDemon] Spawned at {0} (entityId={1})", pos, entity.entityId);
            }
            catch (Exception e)
            {
                Log.Error("[DsDemon] Spawn {0} failed: {1}", i, e);
            }
        }

        ctx.Reply("[00ff66]Spawned " + spawned + " Demon(s).[-] " +
            "Signature buff '" + SignatureBuff + "' + spawn puff applied. " +
            "Use /dsdemon find to locate them.");
    }

    // ============================================================ find

    private void HandleFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllDemons(world);
        if (live.Count == 0) { ctx.Reply("No live Demons found in the world."); return; }

        EntityAlive nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var d in live)
        {
            float dist = Vector3.Distance(d.position, caller.position);
            if (dist < nearestDist) { nearestDist = dist; nearest = d; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live Demon(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    // ============================================================ despawn

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllDemons(world);
        int removed = 0;
        foreach (var d in live)
        {
            try
            {
                world.RemoveEntity(d.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsDemon] Despawn of entity {0} failed: {1}", d.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Demon(s).[-] (silent despawn -- no death effect)");
    }

    // ============================================================ stats

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        ctx.Reply("[00ff66]DS:Demon status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff);
        ctx.Reply("  Spawn puff buff:   " + SpawnPuffBuff);

        ctx.Reply("[00ff66]Config[-] (Mods/Styx/Config/" + Name + ".json)");
        ctx.Reply("  Max health:        " + (_cfg.MaxHealth == 0
            ? "inherited (1700 from zombieSkateboarderInfernal)"
            : _cfg.MaxHealth.ToString()));
        ctx.Reply("  Wrath radius:      " + _cfg.Wrath.Radius + "m");
        ctx.Reply("  Wrath cooldown:    " + _cfg.Wrath.CooldownSec + "s");
        ctx.Reply("  Wrath duration:    " + _cfg.Wrath.DurationSec + "s pursuit");

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        var live = FindAllDemons(world);
        ctx.Reply("  Live count:        " + live.Count);
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllDemons(World world)
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
    /// Top-level config persisted to Mods/Styx/Config/DsDemonPrototype.json.
    /// First load writes defaults; later loads merge on-disk values, with
    /// any new POCO fields materialising at default. Editing the file
    /// triggers PluginWatcher.HandleConfigChange which reloads the plugin
    /// (full OnUnload -> OnLoad cycle), so changes take effect within ~1s
    /// of save without a server restart — including Wrath tunables and
    /// MaxHealth (the latter is applied at spawn time via Stats.Health.
    /// BaseMax, NOT via the entity_class XML, so existing live Demons
    /// keep their old HP and only freshly spawned ones get the new value).
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Override for the spawned Demon's max HP. Set to 0 to use the
        /// inherited value from zombieSkateboarderInfernal (^healthNormalInfernal
        /// = 1700). Any positive integer overrides at spawn time.
        /// </summary>
        public int MaxHealth { get; set; } = 0;

        public WrathConfig Wrath { get; set; } = new WrathConfig();
    }

    /// <summary>
    /// "Wrath call" tunables. When a Demon takes damage (any path —
    /// melee, gunshot, fire DoT, fall), it broadcasts SetAttackTarget
    /// to every EntityEnemy within Radius, sending them after the
    /// attacker for DurationSec seconds. CooldownSec gates how often
    /// the broadcast can re-fire per Demon.
    /// </summary>
    public class WrathConfig
    {
        /// <summary>Aggro broadcast radius (metres). Default 50m matches
        /// the original prototype intent: broad enough to drag a horde,
        /// narrow enough to not affect the entire chunk.</summary>
        public float Radius { get; set; } = 50f;

        /// <summary>Per-Demon cooldown between broadcasts (seconds).
        /// Default 2s prevents broadcast storms under sustained DPS.
        /// Bump to 5+ for "rare wrath cry" feel; drop to 0.5 for
        /// frantic re-aggro every hit.</summary>
        public float CooldownSec { get; set; } = 2.0f;

        /// <summary>How long the recruited hostiles pursue the attacker
        /// (seconds). Internally converted to ticks at 20/sec for the
        /// engine's SetAttackTarget API. Default 60s matches the
        /// blood-moon director's pursuit window.</summary>
        public int DurationSec { get; set; } = 60;
    }
}
