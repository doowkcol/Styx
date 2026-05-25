// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Behemoth.
// Internal proof-of-concept validating the full visual-variant stack:
//   - Custom entity class (zombieDsBehemoth) via @styx-entityclasses
//     synthesis (extends vanilla zombieFatHawaiian, SizeScale=2.0, 2000 HP)
//   - Multi-effect signature buff (buffDsBehemothAura) demonstrating:
//       * AttachParticleEffectToEntity  -> radiation aura on body
//       * AttachPrefabToEntity          -> light source on spine (glow)
//       * Explode onSelfDied            -> death-explosion VFX
//     all in one effect_group, all replicating to PC + console clients
//     via the standard buff-sync path.
//   - Admin spawn command for testing.
//
// REQUIRES: shim active OR the four Config files in place. With Option A
// strip-down (no Config/ files) this prototype is non-functional because
// the entity class and buff don't auto-sync to clients.
//
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml needs to be loaded by the engine's config pass; that
// happens at boot only. Subsequent edits to the C# file hot-reload as
// usual but adding/removing the entity_class block won't take effect
// until restart.
//
// Test plan:
//   1. /perm grant user <yourId> dsbehemoth.admin
//   2. /dsbehemoth spawn 1
//   3. Observe: 2x-scale fat zombie with green radiation aura, glowing.
//   4. Kill it: explosion VFX on death + chunky damage to surroundings.
//   5. /dsbehemoth despawn  (cleanup if you spawned a herd).
//
// See docs/console-crossplay/ (internal) for the broader Darkness
// Stumbles concept doc and engine-research findings this prototype
// validates.

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsBehemoth — DS:Threats prototype variant.
    Inherits zombieFatHawaiian (largest base humanoid zombie). 2x scale
    via SizeScale produces a ~4m tall opponent. High HP, high XP reward.
    AI from parent (no custom AI yet). Signature buff applied at spawn
    by the plugin's command handler (or by a future Harmony postfix
    when biome-spawning is added).
    Note vanilla zombies use the `extends=` attribute on entity_class
    (not `<property name="Parent">`) — that's how Mesh, Prefab, Tags,
    AI, dismemberment etc. all inherit from the parent.
-->
<entity_class name="zombieDsBehemoth" extends="zombieFatHawaiian">
    <!-- Scale 1.5 for the prototype iteration: the 2x scale we tested first
         spawned visually correctly but melee strikes passed over the player
         (fat zombie attack-animation strike pivot is at arm height; at 2x
         scale that's well above the player's hitbox). 1.5 is a balance
         test - if damage lands at this scale, the issue is geometry; if
         it still doesn't, we need to dig into AttackList overrides. -->
    <property name="SizeScale" value="1.5"/>
    <property name="MaxHealth" value="2000"/>
    <property name="ExperienceGain" value="2000"/>

    <!-- Auto-apply signature buff on first spawn. Without this, biome-
         spawned Behemoths get the entity_class but no aura/explosion
         (the C# HandleSpawn path that calls AddBuff only runs for
         /dsbehemoth spawn). onSelfFirstSpawn is the vanilla trigger
         used for class-bound visual identity (e.g. radiated zombie
         particle attachments at entityclasses.xml:1629). -->
    <effect_group name="DS Behemoth Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsBehemothAura"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups DsThreatsHigh new
<!-- Behemoth contributes ONLY to the high-tier cohort. DsThreatsHigh
     is the full-pool cohort referenced by burnt_forest / desert / snow /
     wasteland (mid-to-late biomes). Pine forest references DsThreatsLow
     instead (Specter + Wraith only) — that excludes Behemoth from the
     starter biome so a fresh-spawn player doesn't trip over a 2000-HP
     boss in their first chunk. The `new` flag creates the cohort group;
     other contributors (Demon/Specter/Wraith) just append. -->
zombieDsBehemoth, .15
*/

/*
    Per-biome <spawn> rules — DsBehemothPrototype is the anchor plugin
    for all DS:Threats biome spawn integration. Two cohorts referenced:

      DsThreatsLow  = Specter + Wraith only (starter-friendly)
      DsThreatsHigh = Specter + Wraith + Demon + Behemoth (full pool)
      DsCanidsBiome = Hellhound (canid pack)

    Difficulty curve mirrors vanilla biome tiering — pine_forest is
    the starter biome (low respawn, low cohort), wasteland is endgame
    (high respawn, full cohort). For reference vanilla animal spawns
    in burnt_forest use respawndelay=2.8, in wasteland use 0.9 — a
    ~3x rate ramp.

    The id "dsThreats" / "dsCanids" must be unique within each biome's
    spawn list (BiomeSpawningFromXml.cs:26-30 throws on collision);
    distinct IDs across biomes is fine.

    Tuning summary (game-days; default day=60min real time):

      Biome          Cohort   Threats respawn   Canids respawn
      ------------- -------- ----------------- ----------------
      pine_forest   Low      6.0 (very rare)   — (hellhounds excluded)
      burnt_forest  High     2.0 (rare)        2.5 (rare)
      desert        High     1.2 (moderate)    1.5 (moderate)
      snow          High     1.0 (moderate)    1.2 (moderate)
      wasteland     High     0.5 (common)      0.6 (common)

    Pine forest tuning: deliberately the gentlest biome. Hellhounds
    are excluded entirely — pack-aggro fire dogs are too punishing
    for the starter zone where players are likely unarmed. The
    Low-cohort Threats slot (Specter + Wraith only) sits at 6.0d
    respawn so new players see at most one or two DS variants over
    a normal-length early game.

    Operators wanting different rates: edit the values below and the
    framework re-synthesises Config/spawning.xml on next boot.
*/

/* @styx-spawning pine_forest
<!-- Starter biome: Low cohort only (no Demon, no Behemoth), very
     slow respawn so a fresh-spawn player sees DS variants as rare
     event encounters rather than constant pressure. Hellhounds
     (DsCanidsBiome) deliberately omitted — pack-aggro fire dogs
     are too punishing for a fresh-spawn zone where players may
     have nothing better than fists. -->
<spawn id="dsThreats" maxcount="1" respawndelay="6.0" time="Any" entitygroup="DsThreatsLow"/>
*/

/* @styx-spawning burnt_forest
<!-- Mid-low: Demon and Behemoth available (rare cohort rolls), but
     the spawn slot itself is slow. -->
<spawn id="dsThreats" maxcount="1" respawndelay="2.0" time="Any" entitygroup="DsThreatsHigh"/>
<spawn id="dsCanids"  maxcount="1" respawndelay="2.5" time="Any" entitygroup="DsCanidsBiome"/>
*/

/* @styx-spawning desert
<!-- Mid: full cohort, moderate respawn. Aligns with vanilla desert
     being mid-tier difficulty. -->
<spawn id="dsThreats" maxcount="1" respawndelay="1.2" time="Any" entitygroup="DsThreatsHigh"/>
<spawn id="dsCanids"  maxcount="1" respawndelay="1.5" time="Any" entitygroup="DsCanidsBiome"/>
*/

/* @styx-spawning snow
<!-- Mid-high: same cohort as desert but slightly faster spawn —
     vanilla snow biome is between desert and wasteland in difficulty. -->
<spawn id="dsThreats" maxcount="1" respawndelay="1.0" time="Any" entitygroup="DsThreatsHigh"/>
<spawn id="dsCanids"  maxcount="1" respawndelay="1.2" time="Any" entitygroup="DsCanidsBiome"/>
*/

/* @styx-spawning wasteland
<!-- Endgame: fastest respawn. Wasteland is meant to feel hostile;
     DS variants are a frequent encounter here. -->
<spawn id="dsThreats" maxcount="1" respawndelay="0.5" time="Any" entitygroup="DsThreatsHigh"/>
<spawn id="dsCanids"  maxcount="1" respawndelay="0.6" time="Any" entitygroup="DsCanidsBiome"/>
*/

/* @styx-buffs
<buff name="buffDsBehemothAura"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group name="DS Behemoth Aura">
        <!-- Radiation aura. Earlier prototype used parent_transform=".body"
             + shape_mesh="true" intending to bind the particle to the body
             mesh's SkinnedMeshRenderer for surface emission. In practice
             that resulted in a Sphere-shape fallback (the SMR lookup at
             the resolved "body"/"torso" mesh-graph node didn't succeed
             reliably for our SizeScale-altered variants) with the cluster
             centred high above the visible torso — looked like a green
             orb floating above the boss's head.
             Replacement: anchor at Spine1 (proven canonical torso bone,
             same recipe as Specter's foot-trail) with local_offset Y=-0.8
             to drop the cluster centre into the actual chest/midsection.
             At SizeScale=1.5 the offset scales to ~1.2m world-space —
             puts the visible aura cloud across the body rather than
             halfway up to the sky. shape_mesh dropped: we're committed to
             Sphere shape now, no point pretending we want SMR binding. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="RadiatedParticlesOnMesh" parent_transform="Spine1"
            local_offset="0,-0.8,0"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="RadiatedParticlesOnMesh" parent_transform="Spine1"
            local_offset="0,-0.8,0"/>

        <!-- Death explosion - properly imposing for a Behemoth. Now leaves
             a crater (block_damage 2000 / radius 6 = ~TNT-charge-sized
             hole). blast_power 200 puts serious concussive force on
             nearby entities; entity_damage 300 / radius 10 = deadly to
             anything within point-blank-to-medium range when the boss
             goes down. damage_type Special continues to bypass armour
             resistance per vanilla explosion-on-death precedent. -->
        <triggered_effect trigger="onSelfDied" action="Explode"
            blast_power="200" block_damage="2000" block_radius="6"
            entity_damage="300" entity_radius="10" damage_type="Special"/>
    </effect_group>
</buff>

<!-- One-shot summon-telegraph buff. Fired from the damage hook when
     the Behemoth summons helpers; a brief smoke puff at the boss's
     torso gives players a clear visual that something just happened.
     duration=1 keeps the buff alive for one second; explicit Remove
     triggers below clean the particle when the buff ends. -->
<buff name="buffDsBehemothSummon"
      icon="ui_game_symbol_skull"
      hidden="true">
    <stack_type value="replace"/>
    <duration value="1"/>
    <effect_group>
        <!-- Single-point attachment at the torso bone (Spine1, valid on
             the humanoid rig the Behemoth inherits via zombieFatHawaiian).
             Earlier prototype used parent_transform=".body" + shape_mesh=
             "true" — that pair is the body-wrap recipe for persistent
             auras (correct for RadiatedParticlesOnMesh on this same
             entity), but wrong for a one-shot puff: shape_mesh emits
             from every body vertex AND the engine does not reliably
             auto-remove a shape-mesh attachment when its parent buff
             expires, so the puff would leak out as continuous grey
             smoke breaching the mesh long after the 1s buff was gone,
             also blending into the green radiation aura so the puff
             never read as a discrete event. Single-point Spine1
             anchor + explicit RemoveParticleEffectFromEntity on
             buff-remove gives a clean torso-level burst that disappears
             when it should. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_smokePuff" parent_transform="Spine1"/>
        <triggered_effect trigger="onSelfBuffRemove" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
        <triggered_effect trigger="onSelfDied" action="RemoveParticleEffectFromEntity"
            particle="p_twitch_smokePuff"/>
    </effect_group>
</buff>
*/

[Info("DsBehemothPrototype", "Doowkcol", "0.1.0")]
public class DsBehemothPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Behemoth visual-variant prototype (internal).";

    // ============================================================ constants

    /// <summary>Entity class name as registered in entityclasses.xml.
    /// Must match the @styx-entityclasses block above. Hashed for engine
    /// lookup (engine keys EntityClass.list by string.GetHashCode()).</summary>
    private const string ClassName = "zombieDsBehemoth";

    /// <summary>Signature buff name. Must match the @styx-buffs block above.</summary>
    private const string SignatureBuff = "buffDsBehemothAura";

    /// <summary>One-shot buff applied when the Behemoth summons helpers.
    /// Fires the smoke-puff VFX as a visual telegraph for the summon.</summary>
    private const string SummonBuff = "buffDsBehemothSummon";

    /// <summary>Permission required to use any /dsbehemoth subcommand.</summary>
    private const string AdminPerm = "dsbehemoth.admin";

    // Helper class/count/radius/cooldown/max-summons live in _cfg.Helpers,
    // loaded from Mods/Styx/Config/DsBehemothPrototype.json at OnLoad. Edit
    // that JSON to retune; the framework's PluginWatcher reloads the plugin
    // automatically when the config file changes (OnUnload -> OnLoad cycle
    // repopulates _cfg and recomputes _helperClassHash). See the Config /
    // HelpersConfig POCOs at the bottom of this file for field descriptions.
    private Config _cfg;

    // Hashed once at OnLoad to avoid recomputing per spawn. _classHash is
    // intrinsic to the plugin (the Behemoth's own class name is fixed);
    // _helperClassHash is recomputed from _cfg.Helpers.Class on every load.
    private int _classHash;
    private int _helperClassHash;

    /// <summary>Per-Behemoth-instance cooldown tracker for summon triggers.
    /// Keyed by entityId, value is the last-summon time in seconds since
    /// game start. Cleaned on entity death via OnEntityDeath.</summary>
    private readonly Dictionary<int, float> _lastSummonTime = new Dictionary<int, float>();

    /// <summary>Per-Behemoth-instance summon counter.
    /// Combined with <c>_cfg.Helpers.MaxSummonsPerBehemoth</c> to gate
    /// "spawn helpers no more than N times across the boss's lifetime".
    /// Keyed by entityId, value is the cumulative summon count for that
    /// Behemoth. Cleaned on entity death.</summary>
    private readonly Dictionary<int, int> _summonCount = new Dictionary<int, int>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();
        _helperClassHash = (_cfg.Helpers.Class ?? "").GetHashCode();

        Log.Out("[DsBehemoth] Prototype loaded. Class={0} hash={1} buff={2} " +
                "helper={3} count={4} cooldown={5}s max={6}",
            ClassName, _classHash, SignatureBuff,
            _cfg.Helpers.Class, _cfg.Helpers.CountPerSummon,
            _cfg.Helpers.CooldownSec,
            _cfg.Helpers.MaxSummonsPerBehemoth == 0
                ? "unlimited"
                : _cfg.Helpers.MaxSummonsPerBehemoth.ToString());

        StyxCore.Commands.Register("dsbehemoth",
            "DS:Threats Behemoth prototype -- /dsbehemoth <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        _lastSummonTime.Clear();
        _summonCount.Clear();
        Log.Out("[DsBehemoth] Prototype unloaded.");
    }

    // ============================================================ damage hook (summon helpers)

    /// <summary>
    /// Auto-bound. Fires on the universal damage choke point
    /// (EntityAlive.ProcessDamageResponse) so every damage path is covered:
    /// melee, gunshots delivered via NetPackageDamageEntity, buff DoT ticks,
    /// fall damage, fire damage, etc. We previously hooked OnEntityDamage
    /// (DamageEntity prefix), but client-originated bullet damage is
    /// dispatched directly via NetPackageDamageEntity.Read -> entity.
    /// ProcessDamageResponse(...) which BYPASSES DamageEntity — making
    /// bullet hits silently fail to fire OnEntityDamage and producing the
    /// "helpers spawn randomly" symptom. ProcessDamageResponse is the
    /// shared finalisation point so OnPreDamageApplied fires reliably.
    ///
    /// Returning void (no value) means we observe damage without modifying
    /// it — the hit lands at full strength. The 8s cooldown still gates
    /// summon spam under sustained DPS.
    /// </summary>
    void OnPreDamageApplied(EntityAlive victim, DamageResponse response)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        if (victim.IsDead()) return;

        int eid = victim.entityId;
        float now = Time.time;

        // Cooldown gate (per-Behemoth). Default 8s — see HelpersConfig.CooldownSec.
        if (_lastSummonTime.TryGetValue(eid, out var last)
            && now - last < _cfg.Helpers.CooldownSec)
        {
            return;
        }

        // Max-summons gate (per-Behemoth). 0 = unlimited; 1 = spawn once on
        // first hit; 2 = twice; etc. The TryGetValue out-default of 0 is the
        // correct "no summons yet" state.
        int max = _cfg.Helpers.MaxSummonsPerBehemoth;
        _summonCount.TryGetValue(eid, out var count);
        if (max > 0 && count >= max) return;

        _lastSummonTime[eid] = now;
        _summonCount[eid] = count + 1;

        SpawnHelpers(victim);
    }

    /// <summary>
    /// Auto-bound hook fired when any entity dies. Used here to clean up
    /// the per-Behemoth cooldown tracker so the dictionary doesn't grow
    /// unbounded over a long server session.
    /// Signature matches the framework's first-party `OnEntityDeath`
    /// hook (FirstPartyPatches.cs Patch_EntityAlive_OnEntityDeath).
    /// </summary>
    void OnEntityDeath(EntityAlive victim)
    {
        if (victim == null) return;
        if (victim.entityClass != _classHash) return;
        _lastSummonTime.Remove(victim.entityId);
        _summonCount.Remove(victim.entityId);
    }

    private void SpawnHelpers(EntityAlive behemoth)
    {
        var world = GameManager.Instance?.World;
        if (world == null) return;

        // Snapshot config values to locals so a mid-spawn config reload
        // can't change the loop count / radius mid-iteration.
        var helperClass  = _cfg.Helpers.Class;
        var helperBuff   = _cfg.Helpers.Buff;
        var helperPuff   = _cfg.Helpers.SpawnPuffBuff;
        int helperCount  = _cfg.Helpers.CountPerSummon;
        float helperRad  = _cfg.Helpers.SpawnRadius;

        // Sanity check the helper class is registered. If config points
        // Helpers.Class at something the engine doesn't know, fail loud
        // once and bail rather than crashing every damage event.
        if (!EntityClass.list.ContainsKey(_helperClassHash))
        {
            Log.Warning("[DsBehemoth] Helper class '{0}' (hash {1}) not registered; summon skipped",
                helperClass, _helperClassHash);
            return;
        }

        // Telegraph the summon with a smoke-puff buff on the Behemoth.
        // One-shot - the buff has duration=1 and removes itself.
        try { behemoth.Buffs?.AddBuff(SummonBuff); }
        catch (Exception e) { Log.Warning("[DsBehemoth] SummonBuff apply failed: " + e.Message); }

        // Spawn helpers in a ring centered on the Behemoth.
        Vector3 center = behemoth.position;
        int spawned = 0;
        for (int i = 0; i < helperCount; i++)
        {
            float angle = (i / (float)helperCount) * (float)(2.0 * Math.PI);
            Vector3 pos = center + new Vector3(
                (float)Math.Cos(angle) * helperRad,
                0f,
                (float)Math.Sin(angle) * helperRad);

            // Surface Y resolution. World.GetHeight returns the top of the
            // highest *solid* block (POI floors / placed blocks / terrain);
            // GetTerrainHeight only returns procedural terrain Y, which puts
            // canid spawns under POI floors -> they fall through the world.
            // The +1.0f offset gives the canid collision capsule clearance
            // (dog pivot sits at body-centre, not feet, so a tighter offset
            // clips half-buried). Floor-cap to the Behemoth's Y so we don't
            // drop helpers into the void if the spawn XZ samples a low
            // adjacent block while the boss is on raised ground.
            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float surfaceY = world.GetHeight(x, z) + 1.0f;
            pos.y = Math.Max(surfaceY, behemoth.position.y);

            try
            {
                // Cast widened to EntityAlive: dsHellhound extends
                // animalZombieDog -> animalWolf which is the canid chain
                // (EntityEnemyAnimal-family), NOT EntityZombie. The original
                // EntityZombie cast worked when helpers were zombieFatHawaiian
                // but returns null for canids and silently drops the spawn.
                var helper = EntityFactory.CreateEntity(_helperClassHash, pos) as EntityAlive;
                if (helper == null) continue;
                helper.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(helper);

                // Apply the helper's signature buff so the helpers spawn
                // visually identical to a direct manual spawn (e.g. for
                // dsHellhound: body fire, eye flames, death pyre). Optional —
                // if Helpers.Buff is empty/null in config (e.g. when the
                // helper class is a vanilla zombie with no custom buff),
                // skip this step.
                if (!string.IsNullOrEmpty(helperBuff) && helper.Buffs != null)
                {
                    helper.Buffs.AddBuff(helperBuff);

                    // Apply the spawn-puff buff (a separate short-duration
                    // buff that masks the materialisation pop in a smoke
                    // cloud). Configurable via Helpers.SpawnPuffBuff so
                    // operators can swap it for a class-appropriate puff
                    // when reconfiguring Helpers.Class — defaults to the
                    // dsHellhound puff. Set empty in config to skip.
                    if (!string.IsNullOrEmpty(helperPuff))
                    {
                        helper.Buffs.AddBuff(helperPuff);
                    }
                }

                spawned++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsBehemoth] Helper spawn {0} failed: {1}", i, e.Message);
            }
        }

        // Surface the cumulative summon count so admins can see the gate working.
        int summonsSoFar = _summonCount.TryGetValue(behemoth.entityId, out var sc) ? sc : 0;
        int max = _cfg.Helpers.MaxSummonsPerBehemoth;
        Log.Out("[DsBehemoth] Behemoth eid={0} HP={1}/{2} summoned {3} helper(s) at {4} -- summon {5}/{6}",
            behemoth.entityId, behemoth.Health, behemoth.GetMaxHealth(),
            spawned, center, summonsSoFar, max == 0 ? "∞" : max.ToString());
    }

    // ============================================================ command dispatch

    private void HandleCommand(CommandContext ctx, string[] args)
    {
        // Perm gate -- everything below is admin-only.
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
        ctx.Reply("DS:Behemoth prototype commands:");
        ctx.Reply("  /dsbehemoth spawn [count]  -- spawn N Behemoths in front of you (max 20)");
        ctx.Reply("  /dsbehemoth find           -- report distance to nearest live Behemoth");
        ctx.Reply("  /dsbehemoth despawn        -- remove all live Behemoths server-wide");
        ctx.Reply("  /dsbehemoth stats          -- count live Behemoths + class metadata");
    }

    // ============================================================ spawn

    private void HandleSpawn(CommandContext ctx, int count)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        // Resolve caller's player entity for spawn-position calculation.
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        // Verify the entity class is registered. If not, the synthesiser
        // hasn't run or the engine hasn't reloaded since the entity_class
        // block was added -- needs a server restart.
        if (!EntityClass.list.ContainsKey(_classHash))
        {
            ctx.Reply("[ff6666]Behemoth entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsBehemoth] EntityClass.list missing hash {0} for '{1}'. " +
                "Confirm Mods/Styx/Config/entityclasses.xml contains <entity_class name=\"{1}\"/> " +
                "and restart the server.", _classHash, ClassName);
            return;
        }

        // Position 5m in front of caller, spread along their right vector
        // so multiple spawns don't overlap.
        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + fwd * 5f;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            // Spread spacing 2m apart, centered on basePos.
            float offset = (i - (count - 1) * 0.5f) * 2f;
            Vector3 pos = basePos + right * offset;

            // Clamp Y to terrain height + small epsilon so the spawn
            // doesn't appear underground or floating.
            int x = Utils.Fastfloor(pos.x);
            int z = Utils.Fastfloor(pos.z);
            float groundY = world.GetTerrainHeight(x, z);
            pos.y = groundY + 0.1f;

            try
            {
                var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityZombie;
                if (entity == null)
                {
                    Log.Warning("[DsBehemoth] EntityFactory returned null or non-zombie for hash {0}", _classHash);
                    continue;
                }
                entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(entity);

                // Apply signature buff. Buff replicates via standard
                // NetPackageEntityStatsBuff -- both PC and console clients
                // see the particle attachments + glow + death explosion.
                if (entity.Buffs != null)
                {
                    entity.Buffs.AddBuff(SignatureBuff);
                }
                else
                {
                    Log.Warning("[DsBehemoth] Spawned entity {0} but Buffs collection is null", entity.entityId);
                }

                spawned++;
                Log.Out("[DsBehemoth] Spawned at {0} (entityId={1})", pos, entity.entityId);
            }
            catch (Exception e)
            {
                Log.Error("[DsBehemoth] Spawn {0} failed: {1}", i, e);
            }
        }

        ctx.Reply("[00ff66]Spawned " + spawned + " Behemoth(s).[-] " +
            "Aura buff '" + SignatureBuff + "' applied. Use /dsbehemoth find to locate them.");
    }

    // ============================================================ find

    private void HandleFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }

        var live = FindAllBehemoths(world);
        if (live.Count == 0)
        {
            ctx.Reply("No live Behemoths found in the world.");
            return;
        }

        EntityZombie nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var z in live)
        {
            float d = Vector3.Distance(z.position, caller.position);
            if (d < nearestDist) { nearestDist = d; nearest = z; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live Behemoth(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    // ============================================================ despawn

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllBehemoths(world);
        int removed = 0;
        foreach (var z in live)
        {
            try
            {
                // SetDead() + Kill() + RemoveEntity ensures clean teardown
                // including the death-explosion buff trigger. If you want
                // silent removal (no explosion), use world.RemoveEntity()
                // directly without Kill().
                world.RemoveEntity(z.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsBehemoth] Despawn of entity {0} failed: {1}", z.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Behemoth(s).[-] (silent despawn -- no death explosion)");
    }

    // ============================================================ stats

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        bool helperRegistered = EntityClass.list.ContainsKey(_helperClassHash);

        ctx.Reply("[00ff66]DS:Behemoth status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff);

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        ctx.Reply("[00ff66]Helper config[-] (Mods/Styx/Config/" + Name + ".json)");
        ctx.Reply("  Helper class:      " + _cfg.Helpers.Class +
                  (helperRegistered ? "" : " [ff6666](NOT registered)[-]"));
        ctx.Reply("  Helper buff:       " + (string.IsNullOrEmpty(_cfg.Helpers.Buff) ? "(none)" : _cfg.Helpers.Buff));
        ctx.Reply("  Spawn puff buff:   " + (string.IsNullOrEmpty(_cfg.Helpers.SpawnPuffBuff) ? "(none)" : _cfg.Helpers.SpawnPuffBuff));
        ctx.Reply("  Count per summon:  " + _cfg.Helpers.CountPerSummon);
        ctx.Reply("  Spawn radius:      " + _cfg.Helpers.SpawnRadius + "m");
        ctx.Reply("  Cooldown:          " + _cfg.Helpers.CooldownSec + "s");
        ctx.Reply("  Max summons/boss:  " +
                  (_cfg.Helpers.MaxSummonsPerBehemoth == 0
                      ? "unlimited"
                      : _cfg.Helpers.MaxSummonsPerBehemoth.ToString()));

        var live = FindAllBehemoths(world);
        ctx.Reply("[00ff66]Live Behemoths:[-] " + live.Count);
        foreach (var b in live)
        {
            int summonsSoFar = _summonCount.TryGetValue(b.entityId, out var sc) ? sc : 0;
            ctx.Reply(string.Format("  eid={0} HP={1}/{2} summoned {3} time(s)",
                b.entityId, b.Health, b.GetMaxHealth(), summonsSoFar));
        }
    }

    // ============================================================ helpers

    /// <summary>
    /// Walk every zombie entity in the world and return only those whose
    /// entity-class hash matches our Behemoth class. World.Entities.list is
    /// the live snapshot; iterating is O(n) where n is total entities --
    /// fine for the prototype's command-frequency use, not for hot paths.
    /// </summary>
    private List<EntityZombie> FindAllBehemoths(World world)
    {
        var result = new List<EntityZombie>();
        if (world?.Entities?.list == null) return result;

        foreach (var e in world.Entities.list)
        {
            if (e is EntityZombie z && z.entityClass == _classHash && !z.IsDead())
            {
                result.Add(z);
            }
        }
        return result;
    }

    // ============================================================ config POCOs

    /// <summary>
    /// Top-level config persisted to Mods/Styx/Config/DsBehemothPrototype.json.
    /// First load writes defaults; later loads merge on-disk values, with
    /// any new POCO fields materialising at default. Editing the file
    /// triggers PluginWatcher.HandleConfigChange which reloads the plugin
    /// (full OnUnload -> OnLoad cycle), so changes take effect within ~1s
    /// of save without a server restart.
    /// </summary>
    public class Config
    {
        public HelpersConfig Helpers { get; set; } = new HelpersConfig();
    }

    /// <summary>
    /// Per-Behemoth helper-summon tuning. Defaults summon a full pack of
    /// 3 Hellhounds every 8 seconds for the Behemoth's lifetime. Tweak
    /// CountPerSummon / CooldownSec to retune frequency, or set
    /// MaxSummonsPerBehemoth &gt; 0 to cap the total summons per boss
    /// (e.g. 1 = "spawn helpers exactly once on first hit").
    /// </summary>
    public class HelpersConfig
    {
        /// <summary>
        /// Entity class name spawned as a helper. Must be registered in
        /// entityclasses.xml -- defaults to "dsHellhound" (the DS:Threats
        /// canid variant). Swap to any vanilla zombie/animal name to
        /// retune the threat: "zombieFatHawaiian" for the original
        /// kin-summon, "animalZombieDog" for plain vanilla dogs, etc.
        /// Changes here force a hash recompute on next plugin load.
        /// </summary>
        public string Class { get; set; } = "dsHellhound";

        /// <summary>
        /// Buff applied to each helper after spawn. Used to give vanilla-
        /// classed helpers a custom visual identity. Set to empty/null
        /// to skip — useful when Class is a vanilla zombie that already
        /// has its full visual identity baked into the entity_class.
        /// </summary>
        public string Buff { get; set; } = "buffDsHellhoundFire";

        /// <summary>
        /// Short-duration spawn-puff buff applied alongside Buff at the
        /// moment of spawn. Default is the dsHellhound puff; swap if
        /// Class is changed to something else (vanilla zombies don't
        /// have a dedicated puff buff so set this empty in that case
        /// to skip the puff entirely).
        /// </summary>
        public string SpawnPuffBuff { get; set; } = "buffDsHellhoundSpawnPuff";

        /// <summary>How many helpers spawn per summon trigger.</summary>
        public int CountPerSummon { get; set; } = 3;

        /// <summary>Ring radius around the Behemoth where helpers appear (metres).</summary>
        public float SpawnRadius { get; set; } = 4.0f;

        /// <summary>
        /// Minimum seconds between summon triggers per Behemoth. Prevents
        /// one-helper-per-bullet spam under sustained DPS.
        /// </summary>
        public float CooldownSec { get; set; } = 8.0f;

        /// <summary>
        /// Cap on total summon triggers per Behemoth lifetime.
        /// 0  = unlimited (default — summon every CooldownSec until the boss dies).
        /// 1  = spawn helpers exactly once on first hit, never again.
        /// 2  = spawn twice across the boss's life.
        /// N  = spawn N times.
        /// Counter resets when the Behemoth dies (next spawn starts fresh).
        /// </summary>
        public int MaxSummonsPerBehemoth { get; set; } = 0;
    }
}
