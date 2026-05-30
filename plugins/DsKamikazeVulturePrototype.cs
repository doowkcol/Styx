// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Kamikaze Vulture.
// Flying suicide bomber. Vultures already dive at the player on
// aggro; we just add a body-fire telegraph and an Explode action
// triggered when they make contact (peck the player). One-and-done
// threat — the vulture dies in its own AOE.
//
// Visual identity:
//   - Custom entity class (entityDsKamikazeVulture) extends
//     animalZombieVulture (which extends animalTemplateHostile —
//     full vanilla flying-hostile chain). SizeScale=1.0 — vultures
//     are already attention-grabbing, no need to size them up.
//   - Signature buff (buffDsKamikazeBomb) layers:
//       * p_onFire body wrap — the vulture flies wreathed in flame,
//         visible telegraph from a distance ("this one is going to
//         explode"). Same body-fire recipe used by Hellhound.
//       * Explode on attack (onSelfAttackedOther) — fires the
//         moment the vulture's peck hits the player. AOE damage
//         + small crater. Vulture dies in its own explosion.
//       * Explode on death (onSelfDied) — the player shoots it
//         out of the sky? Still gets the explosion. The player
//         needs to either kill it from range AND back away in time,
//         or accept the kamikaze hit.
//
// Behavioural identity:
//   - No custom AI. Vanilla EntityFlying already dives at the
//     player on aggro. Our buff just adds the explosion. The
//     "kamikaze" gameplay loop is:
//       1. Vulture spawns at altitude, sees player, dives toward them.
//       2. Pre-impact: telegraph fire visible from medium range.
//       3. Impact: peck hits player -> Explode (Heat damage /
//          ~80 dmg / 4m radius / small crater).
//       4. Vulture dies in the AOE; player takes the hit.
//   - Glass cannon HP (150 default, config-tunable). They're
//     designed to be expendable — one mid-range shot from a
//     decent weapon should down them before contact.
//
// REQUIRES: shim active OR the four Config files in place.
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml + buffs.xml need to be loaded by the engine's
// config pass.
//
// Test plan:
//   1. /perm grant user <yourId> dskamikaze.admin
//   2. /dskamikaze spawn 1   -> burning vulture appears ~6m above
//                               you and immediately dives at you.
//   3. Stand still, let it hit you: BOOM. Significant damage,
//      small crater under the impact point.
//   4. Spawn another, shoot it before contact: it dies and
//      explodes mid-air. If you're under it, you eat the AOE.
//   5. /dskamikaze despawn (cleanup).

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-entityclasses
<!--
    entityDsKamikazeVulture — DS:Threats kamikaze flying variant.
    Inherits animalZombieVulture (full vanilla flying-hostile chain
    via animalTemplateHostile). SizeScale 1.0 — vultures are already
    attention-grabbing visually; the body-fire wrap is the visual
    distinguisher. HP override applied at spawn time via
    Stats.Health.BaseMax (animalZombieVulture's default is too high
    for a glass-cannon kamikaze; we want them to die before contact
    if the player reacts in time).
-->
<entity_class name="entityDsKamikazeVulture" extends="animalZombieVulture">
    <property name="SizeScale" value="1.0"/>

    <!-- Auto-apply signature buff on first spawn (biome-spawned
         kamikazes bypass the C# HandleSpawn path). Without this,
         biome kamikazes look like vanilla vultures and don't
         explode on contact. -->
    <effect_group name="DS Kamikaze Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsKamikazeBomb"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Primary spawn route. DsThreatsHigh is the cohort referenced by
     per-biome <spawn> rules in burnt_forest / desert / snow /
     wasteland with respawn delays 2.0 / 1.2 / 1.0 / 0.5 days
     (see DsBehemothPrototype @styx-spawning blocks). Adding the
     kamikaze here gives it reliable biome appearance — relative
     weight in the cohort is .15 / (.15+.25+.35+.25+.15) = .15/1.15
     ≈ 13%, comparable to Behemoth's slot. Kamikazes are flying
     EntityFlying; the engine spawns them at the ground-picked
     position and vulture AI immediately climbs to dive altitude,
     so mixing with ground variants works fine.

     Without this, the kamikaze was only plugged into vanilla
     EnemyAnimalsDesert / EnemyAnimalsWasteland / VultureGroup at
     fractional weights (.03 / .05 / .1) against vanilla weights
     summing to ~100 — that's 0.03–0.10% relative, effectively
     never spawning under vanilla respawn delays of 3.1 / 0.9 days.
     The earlier "3/2003 ≈ 0.15%" comment math was wrong (vanilla
     EnemyAnimalsDesert totals 100, not 2000). -->
entityDsKamikazeVulture, .15
*/

/* @styx-entitygroups EnemyAnimalsDesert
<!-- Supplementary route: ambient rare encounter in vanilla desert
     animal pool. Weight 3 against vanilla totals 100 = ~3% relative;
     rare but visible alongside coyotes / vultures / snakes when the
     biome's normal animal-respawn timer fires. -->
entityDsKamikazeVulture, 3
*/

/* @styx-entitygroups EnemyAnimalsWasteland
<!-- Same supplementary role in the wasteland animal pool — slightly
     higher weight (5) than desert since wasteland is the tougher
     biome where the bomb-vulture fits thematically. ~5% relative. -->
entityDsKamikazeVulture, 5
*/

/* @styx-entitygroups VultureGroup
<!-- VultureGroup is the singleton-vulture entitygroup used by
     spawning.xml for vulture-specific contexts. Vanilla content
     is just animalZombieVulture (weight 1); adding our kamikaze at
     .5 makes vulture-only spawns roughly 1-in-3 kamikazes. -->
entityDsKamikazeVulture, .5
*/

/* @styx-buffs
<buff name="buffDsKamikazeBomb"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group name="DS Kamikaze Bomb">
        <!-- Body fire telegraph. Vultures are EntityFlying (animal-
             family entityFlags), so the .body parent_transform
             resolves via GetPelvisTransform with a 90° rotation tweak
             (decomp MinEventActionAttachParticleEffectToEntity.cs:88-95).
             shape_mesh OMITTED here — same reasoning as Hellhound:
             animal pelvis bones don't expose a SkinnedMeshRenderer so
             shape_mesh=true would log "no renderer!" warnings every
             spawn and fall back to a Sphere shape anyway. Default
             attachment without shape_mesh gives a clean fire-cloud
             at the body centre, which on a vulture in flight reads
             as "the bird is on fire and definitely a problem." -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>

        <!-- Contact explosion. onSelfAttackedOther fires when the
             vulture's peck/bite attack lands on a target (player or
             any other entity). The Explode action delivers AOE
             damage + small block crater + mild concussive blast at
             the vulture's position. Because the vulture is at the
             impact point itself, it dies in its own AOE — the
             "kamikaze suicide" mechanic. damage_type=Heat fits the
             fire telegraph thematically (and matches EnumDamageTypes —
             "Fire" is not a valid enum member, Heat IS the fire
             damage path). entity_radius=4 covers an arm's-length
             AOE around impact; entity_damage=80 is a significant
             chunk against a mid-game player but survivable.
             block_damage=100 / block_radius=2 leaves a small visible
             crater at impact, makes the explosion feel material
             without being base-destroying like the demolisher's
             1000/5 setup. NB: entity_radius / block_radius must be
             integers — ParseSInt32 in MinEventActionExplode (decomp
             line 80). Decimals = parse failure cascading the whole
             buffs.xml load. -->
        <triggered_effect trigger="onSelfAttackedOther" action="Explode"
            blast_power="30"
            block_damage="100" block_radius="2"
            entity_damage="80" entity_radius="4"
            damage_type="Heat"/>

        <!-- Shot-down explosion. If the player kills the vulture
             before contact, it still explodes (mid-air or at impact
             point depending on where it died). Same damage profile
             as the contact case — the player has to either shoot
             AND back away in time, or accept the AOE. Adds tactical
             depth: "is it close enough that I should run instead of
             shoot?" -->
        <triggered_effect trigger="onSelfDied" action="Explode"
            blast_power="30"
            block_damage="100" block_radius="2"
            entity_damage="80" entity_radius="4"
            damage_type="Heat"/>
    </effect_group>
</buff>
*/

/* @styx-patch entitygroups
<!-- Kamikaze bomber into blood-moon hordes and wandering hordes (low). A
     flying suicide bomber adds vertical chaos to a horde night. -->
<append xpath="/entitygroups/entitygroup[starts-with(@name,'feralHordeStageGS')]">
    <entity name="entityDsKamikazeVulture" prob=".04"/>
</append>
<append xpath="/entitygroups/entitygroup[contains(@name,'wanderingHordeStageGS')]">
    <entity name="entityDsKamikazeVulture" prob=".04"/>
</append>
*/

[Info("DsKamikazeVulturePrototype", "Doowkcol", "0.1.0")]
public class DsKamikazeVulturePrototype : StyxPlugin
{
    public override string Description => "DS:Threats Kamikaze Vulture flying-bomber prototype (internal).";

    // ============================================================ constants

    private const string ClassName = "entityDsKamikazeVulture";
    private const string SignatureBuff = "buffDsKamikazeBomb";
    private const string AdminPerm = "dskamikaze.admin";

    /// <summary>How far above the caller's head to spawn the vulture.
    /// Vultures are flying entities — spawning at ground level looks
    /// odd; spawning a few metres up gives the "circling overhead"
    /// presentation and lets the AI's dive-attack behaviour engage
    /// naturally as it descends toward the player.</summary>
    private const float SpawnAltitudeOffset = 6f;

    private Config _cfg;
    private int _classHash;

    /// <summary>Alert-broadcast scheduler handle. Created in OnLoad,
    /// destroyed in OnUnload. Periodically scans live kamikazes for
    /// any with an acquired player target, and propagates that target
    /// to nearby kamikazes (pack-alert behaviour).</summary>
    private TimerHandle _alertTimer;

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        // Pack-alert scheduler. Every TickIntervalSec, walks live
        // kamikazes for any with an acquired player target and
        // forwards that target to other kamikazes within AlertRadius.
        _alertTimer = Scheduler.Every(_cfg.Aggro.TickIntervalSec, OnAlertTick, "DsKamikaze.alert");

        Log.Out("[DsKamikaze] Prototype loaded. Class={0} hash={1} buff={2} " +
                "hp={3} attackThreshold={4} alertRadius={5}m alertTick={6}s",
            ClassName, _classHash, SignatureBuff,
            _cfg.MaxHealth == 0 ? "inherited" : _cfg.MaxHealth.ToString(),
            _cfg.Aggro.AttackHealthThreshold,
            _cfg.Aggro.AlertRadius,
            _cfg.Aggro.TickIntervalSec);

        StyxCore.Commands.Register("dskamikaze",
            "DS:Threats Kamikaze Vulture prototype -- /dskamikaze <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        _alertTimer?.Destroy();
        _alertTimer = null;
        Log.Out("[DsKamikaze] Prototype unloaded.");
    }

    // ============================================================ alert tick

    /// <summary>
    /// Pack-alert scheduler. For each live kamikaze that has an
    /// acquired EntityPlayer target (via the vanilla AI's normal
    /// detection — its targetAttackHealthPercent has been bumped to
    /// _cfg.Aggro.AttackHealthThreshold so it engages on detection
    /// regardless of player HP), find every other live kamikaze
    /// within AlertRadius and SetAttackTarget on them with the same
    /// player. Pack-alert broadcast — vultures are flock animals,
    /// thematically right that one detecting alerts the others.
    ///
    /// We don't gate this with a per-kamikaze cooldown because the
    /// scheduler itself runs on a fixed interval (default 1s) — that's
    /// effectively the cooldown between broadcasts. SetAttackTarget on
    /// a kamikaze that's already pursuing the same target is a cheap
    /// no-op (just refreshes the duration ticks).
    /// </summary>
    private void OnAlertTick()
    {
        var world = GameManager.Instance?.World;
        if (world?.Entities?.list == null) return;

        // Single pass to collect all live kamikazes — avoid walking
        // the entity list twice (outer + inner loop).
        var kamikazes = new List<EntityAlive>();
        foreach (var e in world.Entities.list)
        {
            if (e is EntityAlive ea && ea.entityClass == _classHash && !ea.IsDead())
                kamikazes.Add(ea);
        }

        // Apply targetAttackHealthPercent override to every live kamikaze.
        // HandleSpawn applies this for /dskamikaze spawn but biome-spawned
        // kamikazes bypass that code path, so the field is left at vanilla
        // 0.8 (only attack player at <=80% HP). Setting it on every tick
        // is idempotent and cheap (typically a handful of kamikazes max).
        // Without this, biome-spawned kamikazes would visually identify
        // (fire wrap from the entity_class auto-buff) but behave as
        // vanilla vultures — circling, not engaging.
        float threshold = _cfg.Aggro.AttackHealthThreshold;
        foreach (var ea in kamikazes)
        {
            if (ea is EntityVulture vulture)
                vulture.targetAttackHealthPercent = threshold;
        }

        if (kamikazes.Count < 2) return;  // nothing to alert with < 2 vultures

        int durationTicks = _cfg.Aggro.AggroDurationSec * 20;
        float radSq = _cfg.Aggro.AlertRadius * _cfg.Aggro.AlertRadius;

        foreach (var detector in kamikazes)
        {
            // Only kamikazes that have ALREADY acquired a player target
            // act as detectors. The vanilla EntityVulture AI sets the
            // attack target via its findEnemyTarget() path; with our
            // targetAttackHealthPercent override, that returns players
            // unconditionally (subject to sight/visibility rules).
            var target = detector.GetAttackTarget();
            if (!(target is EntityPlayer player) || player.IsDead()) continue;

            foreach (var peer in kamikazes)
            {
                if (peer.entityId == detector.entityId) continue;  // skip self
                if (peer.GetAttackTarget() == player) continue;    // already on this player
                if ((peer.position - detector.position).sqrMagnitude > radSq) continue;

                try { peer.SetAttackTarget(player, durationTicks); }
                catch (Exception ex)
                {
                    Log.Warning("[DsKamikaze] Pack alert SetAttackTarget on {0} failed: {1}",
                        peer.entityId, ex.Message);
                }
            }
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
        ctx.Reply("DS:Kamikaze Vulture prototype commands:");
        ctx.Reply("  /dskamikaze spawn [count]  -- spawn N kamikaze vultures above you (max 20)");
        ctx.Reply("  /dskamikaze find           -- report distance to nearest live kamikaze");
        ctx.Reply("  /dskamikaze despawn        -- remove all live kamikaze vultures server-wide");
        ctx.Reply("  /dskamikaze stats          -- count live kamikazes + class metadata");
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
            ctx.Reply("[ff6666]Kamikaze vulture entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsKamikaze] EntityClass.list missing hash {0} for '{1}'. " +
                "Confirm Mods/Styx/Config/entityclasses.xml contains <entity_class name=\"{1}\"/> " +
                "and restart the server.", _classHash, ClassName);
            return;
        }

        // Spawn directly above the caller at altitude offset, spread
        // along their right vector so multiple spawns aren't stacked
        // at the same point.
        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + Vector3.up * SpawnAltitudeOffset;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float offset = (i - (count - 1) * 0.5f) * 2.0f;
            Vector3 pos = basePos + right * offset;

            // No surface-Y safety needed for flying entities — they're
            // expected to be in the air. Just use the altitude-offset
            // position directly. The vulture's flying AI handles
            // descent / pursuit toward the target.

            try
            {
                var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
                if (entity == null)
                {
                    Log.Warning("[DsKamikaze] EntityFactory returned null for hash {0}", _classHash);
                    continue;
                }
                entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(entity);

                if (entity.Buffs != null)
                {
                    entity.Buffs.AddBuff(SignatureBuff);
                }
                else
                {
                    Log.Warning("[DsKamikaze] Spawned entity {0} but Buffs is null", entity.entityId);
                }

                ApplyHpOverride(entity);
                ApplyAggroOverride(entity);

                spawned++;
                Log.Out("[DsKamikaze] Spawned at {0} (entityId={1})", pos, entity.entityId);
            }
            catch (Exception e)
            {
                Log.Error("[DsKamikaze] Spawn {0} failed: {1}", i, e);
            }
        }

        ctx.Reply("[00ff66]Spawned " + spawned + " kamikaze vulture(s) overhead.[-] " +
            "Body-fire telegraph applied — they'll dive on aggro and explode on contact.");
    }

    private void ApplyHpOverride(EntityAlive entity)
    {
        if (_cfg.MaxHealth <= 0) return;
        if (entity?.Stats?.Health == null) return;
        entity.Stats.Health.BaseMax = _cfg.MaxHealth;
        entity.Stats.Health.Value = _cfg.MaxHealth;
    }

    /// <summary>
    /// Override the vanilla "circle until prey is wounded" gating.
    /// EntityVulture has a public field `targetAttackHealthPercent`
    /// (default 0.8f) — the AI's findEnemyTarget() method only
    /// returns the player as a valid target if their HP percentage
    /// is at or below this threshold (decomp EntityVulture.cs:792).
    /// Setting it to 1.0f means "any HP%" — vulture engages on
    /// detection regardless of how healthy the player is.
    ///
    /// The cast to EntityVulture is safe: our entity_class
    /// inherits Class="EntityVulture" via the vanilla
    /// animalZombieVulture chain (entityclasses.xml:5461). If a
    /// future engine update changes the vulture C# class name, the
    /// `is` check fails gracefully and the kamikaze just falls back
    /// to vanilla behaviour.
    /// </summary>
    private void ApplyAggroOverride(EntityAlive entity)
    {
        if (entity is EntityVulture vulture)
        {
            vulture.targetAttackHealthPercent = _cfg.Aggro.AttackHealthThreshold;
        }
        else
        {
            Log.Warning("[DsKamikaze] Spawned entity {0} is not EntityVulture (class {1}) — " +
                        "targetAttackHealthPercent override skipped, vulture will use vanilla " +
                        "low-HP gating.", entity?.entityId, entity?.GetType().Name ?? "(null)");
        }
    }

    // ============================================================ find / despawn / stats

    private void HandleFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllVultures(world);
        if (live.Count == 0) { ctx.Reply("No live kamikaze vultures found in the world."); return; }

        EntityAlive nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var v in live)
        {
            float d = Vector3.Distance(v.position, caller.position);
            if (d < nearestDist) { nearestDist = d; nearest = v; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live kamikaze vulture(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllVultures(world);
        int removed = 0;
        foreach (var v in live)
        {
            try
            {
                world.RemoveEntity(v.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsKamikaze] Despawn of entity {0} failed: {1}", v.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " kamikaze vulture(s).[-] (silent despawn -- no death explosion)");
    }

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        ctx.Reply("[00ff66]DS:Kamikaze Vulture status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff + " (fire wrap + contact/death Explode)");

        ctx.Reply("[00ff66]Config[-] (Mods/Styx/Config/" + Name + ".json)");
        ctx.Reply("  Max health:        " + (_cfg.MaxHealth == 0
            ? "inherited (vulture default)"
            : _cfg.MaxHealth.ToString()));
        ctx.Reply("  Spawn altitude:    " + SpawnAltitudeOffset + "m above caller");
        ctx.Reply("[00ff66]Aggro overrides[-]");
        ctx.Reply("  Attack threshold:  " + _cfg.Aggro.AttackHealthThreshold +
                  " (vanilla 0.8 = only attack player at 80%-or-lower HP)");
        ctx.Reply("  Alert radius:      " + _cfg.Aggro.AlertRadius + "m");
        ctx.Reply("  Alert duration:    " + _cfg.Aggro.AggroDurationSec + "s pursuit");
        ctx.Reply("  Alert tick:        " + _cfg.Aggro.TickIntervalSec + "s");

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        var live = FindAllVultures(world);
        ctx.Reply("  Live count:        " + live.Count);
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllVultures(World world)
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
    /// Top-level config persisted to Mods/Styx/Config/DsKamikazeVulturePrototype.json.
    /// PluginWatcher hot-reloads on file edit. MaxHealth applied at
    /// spawn time via Stats.Health.BaseMax (existing live vultures
    /// keep their old HP; only fresh spawns pick up the new value).
    /// Explosion damage / radius / type are baked into the buff XML
    /// (not config-tunable) — edit the @styx-buffs block in this
    /// .cs file to tune them, requires server restart.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Override for spawned kamikaze vulture max HP. 0 = use the
        /// inherited animalZombieVulture default (30 HP — too low to
        /// reach the player). Default 150 keeps them glass-cannon —
        /// designed to die before contact if the player has any
        /// decent ranged weapon, but risk being caught off-guard.
        /// </summary>
        public int MaxHealth { get; set; } = 150;

        public AggroConfig Aggro { get; set; } = new AggroConfig();
    }

    /// <summary>
    /// Vulture aggression overrides. Vanilla EntityVulture has a
    /// "circle until prey is wounded" AI gate (targetAttackHealthPercent
    /// = 0.8 default — only attack at 80%-or-lower player HP). For
    /// kamikaze gameplay we want them to engage on detection regardless
    /// of player HP, plus pack-alert behaviour to nearby kamikazes.
    /// </summary>
    public class AggroConfig
    {
        /// <summary>
        /// Player-HP threshold below which a kamikaze considers the
        /// player a valid target. Written to EntityVulture.targetAttackHealthPercent
        /// at spawn time. Default 1.0 = "any HP, attack on detection."
        /// 0.5 = "only attack at half HP or below" (back to vulture-y
        /// circling), 0.8 = vanilla.
        /// </summary>
        public float AttackHealthThreshold { get; set; } = 1.0f;

        /// <summary>
        /// Distance (metres) between kamikazes for pack-alert. When
        /// any kamikaze acquires a player target, every other live
        /// kamikaze within this radius receives the same target via
        /// SetAttackTarget. Default 30m matches the original design
        /// intent.
        /// </summary>
        public float AlertRadius { get; set; } = 30f;

        /// <summary>
        /// How long alerted kamikazes pursue the broadcast target
        /// (seconds). Internally converted to ticks at 20/sec for the
        /// engine's SetAttackTarget API. Default 60s is plenty for
        /// any kamikaze in a 30m radius to reach the player.
        /// </summary>
        public int AggroDurationSec { get; set; } = 60;

        /// <summary>
        /// Seconds between alert-broadcast ticks. The scheduler walks
        /// live kamikazes, finds those with player targets, and
        /// propagates. 1s default catches new detections quickly
        /// without flooding SetAttackTarget calls.
        /// </summary>
        public double TickIntervalSec { get; set; } = 1.0;
    }
}
