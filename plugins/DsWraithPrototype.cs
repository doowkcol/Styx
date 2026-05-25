// SPDX-License-Identifier: LicenseRef-Styx-Plugin-Restricted
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood). All rights reserved.
//
// Darkness Stumbles -- DS:Threats prototype: Wraith.
// Small stealth ambusher — low HP, low aggro range, mid-body mist
// shroud. Where Specter is "fast skirmisher with kill-window
// escalation," Wraith is "doesn't notice you until you're inside
// her conversation distance, then it's already too late."
//
// Visual identity:
//   - Custom entity class (zombieDsWraith) extends zombieNurseFeral
//     (slim female nurse mesh — distinct from Specter's Arlene base,
//     adds the medical-horror association and avoids "Specter but
//     smaller" silhouette confusion). SizeScale=0.85 — smallest
//     variant in the catalogue, sits below Specter's 0.95 so the
//     visual hierarchy reads:
//        Behemoth 1.5 > Demon 1.2 > vanilla 1.0 > Specter 0.95 > Wraith 0.85
//   - Signature buff (buffDsWraithShroud) layers:
//       * Spectral green eyes via p_twitch_zombie_radiation_left/right
//         at the Head bone (eldritch glow, distinct from Specter's
//         electric blue)
//       * Light HP regen (~1.5 HP/sec via HealthChangeOT) — feels
//         "incorporeal," not as aggressive as Specter's 2.5/sec
//   - Body-wrap aura. RadiatedParticlesOnMesh attached at .body with
//     shape_mesh="true" — the proven body-surface emission recipe
//     from Behemoth's signature buff, same green ethereal family as
//     the eye particles. Pale soft glow enveloping the whole body,
//     within which the bright sharp electric-green eye particles read
//     as her "soul lights." Distinct from Behemoth's same-particle aura
//     because the smaller silhouette + Feral aggression + eye glow
//     give Wraith a completely different visual identity.
//
// Behavioural identity:
//   - Low SightRange (default 10m vs vanilla 30m) — she physically
//     cannot see the player past 10m. Until you're inside that
//     bubble she's idle / wandering. Cross the threshold and Feral
//     aggression kicks in instantly.
//   - Low HP (200 default, config-tunable) — once engaged she goes
//     down fast. The threat is the surprise contact, not a sustained
//     fight.
//
// REQUIRES: shim active OR the four Config files in place.
// REQUIRES: server restart after first install -- the synthesised
// entityclasses.xml + buffs.xml need to be loaded by the engine's
// config pass.
//
// Test plan:
//   1. /perm grant user <yourId> dswraith.admin
//   2. /dswraith spawn 1   -> a small slim figure with green eyes
//                              and a wisp of dust mist at her torso.
//   3. Stand 15m away — she should idle / not aggro you (SightRange=10).
//   4. Walk to within 10m — Feral aggression engages, she charges.
//   5. Kill her with one or two shots — 200 HP, dies fast.
//   6. /dswraith despawn (cleanup)

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsWraith — DS:Threats stealth-ambusher variant.
    Inherits zombieNurseFeral (slim female nurse, Feral aggression
    tier — slightly tougher AI than vanilla, no Radiated/Charged chain
    so no aura/electric conflicts with our identity). Distinct from
    Specter's zombieArleneFeral so the visual silhouette doesn't
    blend with the other slim variant.

    SizeScale=0.85 makes her the smallest entry in the catalogue.
    SightRange=10 (vanilla default 30) is the core mechanic — she
    literally cannot see the player past 10m, so her aggro behaviour
    looks like "appears suddenly when you cross the threshold." The
    Feral tier's heightened aggression then triggers the moment
    you're inside her sight bubble.

    HP override applied at spawn time via Stats.Health.BaseMax
    (zombieNurseFeral defaults to ^healthNormalFeral=550 which is
    too high for a glass-cannon ambusher; the threat is the
    surprise, not the HP pool).
-->
<entity_class name="zombieDsWraith" extends="zombieNurseFeral">
    <property name="SizeScale" value="0.85"/>
    <property name="SightRange" value="10"/>

    <!-- Auto-apply signature buff on first spawn (biome-spawned
         Wraiths bypass the C# HandleSpawn path). Without this,
         biome Wraiths get the small Nurse mesh + low SightRange
         but no eye glow / body aura / regen. -->
    <effect_group name="DS Wraith Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsWraithShroud"/>
    </effect_group>
</entity_class>
*/

/* @styx-entitygroups DsThreatsLow
<!-- Wraith in the starter-friendly Low cohort (pine_forest). Her
     SightRange=10 means she often blends in / doesn't engage unless
     the player walks past her — feels right for a starter biome
     "what was that?" encounter. Glass-cannon HP keeps her killable
     for a fresh-spawn player once spotted. -->
zombieDsWraith, .25
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Wraith also in the High cohort — all biomes get her. -->
zombieDsWraith, .25
*/

/* @styx-buffs
<buff name="buffDsWraithShroud"
      icon="ui_game_symbol_skull">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <update_rate value="2"/>
    <effect_group name="DS Wraith Shroud">
        <!-- Spectral green eyes. Vanilla zombie-radiation particle
             (used at buffs.xml:15171+ on twitch-zombie hands) attached
             to the Head bone with eye-position offsets. Same
             eyeball-the-offset technique used for Specter's blue
             shock eyes; offsets ~4cm sideways from skull mid-line,
             ~6cm above Head pivot, ~12cm forward to clear the brow.
             Reads as eldritch green eye glow from medium range —
             distinct from Specter's electric blue, distinct from
             Feral's default red. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_radiation_left" parent_transform="Head"
            local_offset="0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_radiation_left" parent_transform="Head"
            local_offset="0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_radiation_right" parent_transform="Head"
            local_offset="-0.04,0.06,0.12"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_twitch_zombie_radiation_right" parent_transform="Head"
            local_offset="-0.04,0.06,0.12"/>

        <!-- Body-wrap aura. Same lesson as the Behemoth aura: the
             ".body + shape_mesh=true" recipe was intended to bind the
             particle to the body SkinnedMeshRenderer for surface
             emission, but in practice the SMR lookup falls through
             to a Sphere-shape fallback whose centre sits high above
             the visible torso — a green orb floating above her head.
             Anchored at Spine1 with local_offset Y=-0.8 instead so
             the Sphere centre is at her chest/midsection. At
             SizeScale=0.85 the offset scales to ~0.68m world space,
             putting the cluster across the actual body. Same particle
             (RadiatedParticlesOnMesh) keeps the green family
             continuity with the eye glow above. -->
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="RadiatedParticlesOnMesh" parent_transform="Spine1"
            local_offset="0,-0.8,0"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="RadiatedParticlesOnMesh" parent_transform="Spine1"
            local_offset="0,-0.8,0"/>
        <triggered_effect trigger="onSelfBuffRemove" action="RemoveParticleEffectFromEntity"
            particle="RadiatedParticlesOnMesh"/>
        <triggered_effect trigger="onSelfDied" action="RemoveParticleEffectFromEntity"
            particle="RadiatedParticlesOnMesh"/>

        <!-- Light "incorporeal" regen. 3 HP per 2s = ~1.5 HP/sec — about
             half of Specter's quick regen. Wraith is meant to be glass-
             cannon, not durable; this just adds a faint "she heals
             slowly while you're not watching" feel without making
             her hard to put down once spotted. -->
        <passive_effect name="HealthChangeOT" operation="base_add" value="3"/>
    </effect_group>
</buff>

<!-- (Earlier prototype carried a separate mist-puff buff and a C#
     scheduler that re-applied it for a smoke-trail effect. Removed
     in favour of the persistent body-wrap RadiatedParticlesOnMesh
     above — feels more wraith-y and visually consistent with the
     eye particles. The scheduler infrastructure was also stripped
     from the plugin.) -->
*/

[Info("DsWraithPrototype", "Doowkcol", "0.1.0")]
public class DsWraithPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Wraith stealth-ambusher prototype (internal).";

    // ============================================================ constants

    /// <summary>Entity class name as registered in entityclasses.xml.</summary>
    private const string ClassName = "zombieDsWraith";

    /// <summary>Persistent signature buff (eyes + light regen).</summary>
    private const string SignatureBuff = "buffDsWraithShroud";

    /// <summary>Permission required to use any /dswraith subcommand.</summary>
    private const string AdminPerm = "dswraith.admin";

    private Config _cfg;
    private int _classHash;

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        Log.Out("[DsWraith] Prototype loaded. Class={0} hash={1} buff={2} " +
                "hp={3} sight={4}",
            ClassName, _classHash, SignatureBuff,
            _cfg.MaxHealth == 0 ? "inherited" : _cfg.MaxHealth.ToString(),
            _cfg.SightRange == 0 ? "inherited" : _cfg.SightRange + "m");

        StyxCore.Commands.Register("dswraith",
            "DS:Threats Wraith prototype -- /dswraith <spawn|find|despawn|stats> [count]",
            HandleCommand);
    }

    public override void OnUnload()
    {
        Log.Out("[DsWraith] Prototype unloaded.");
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
        ctx.Reply("DS:Wraith prototype commands:");
        ctx.Reply("  /dswraith spawn [count]  -- spawn N Wraiths in front of you (max 20)");
        ctx.Reply("  /dswraith find           -- report distance to nearest live Wraith");
        ctx.Reply("  /dswraith despawn        -- remove all live Wraiths server-wide");
        ctx.Reply("  /dswraith stats          -- count live Wraiths + class metadata");
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
            ctx.Reply("[ff6666]Wraith entity class not registered.[-] Server restart needed -- " +
                "the engine reads entityclasses.xml at boot only.");
            Log.Warning("[DsWraith] EntityClass.list missing hash {0} for '{1}'. " +
                "Confirm Mods/Styx/Config/entityclasses.xml contains <entity_class name=\"{1}\"/> " +
                "and restart the server.", _classHash, ClassName);
            return;
        }

        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        // Spawn 15m in front (outside her 10m default SightRange) so admins
        // can verify the stealth mechanic — she shouldn't aggro until you
        // close to within 10m.
        Vector3 basePos = caller.position + fwd * 15f;

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
                    Log.Warning("[DsWraith] EntityFactory returned null for hash {0}", _classHash);
                    continue;
                }
                entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
                world.SpawnEntityInWorld(entity);

                if (entity.Buffs != null)
                {
                    // SignatureBuff carries everything visual: eyes,
                    // body-wrap aura, and HP regen. No separate spawn-
                    // puff buff — the body-wrap aura is the persistent
                    // identity, no telegraph needed at materialisation.
                    entity.Buffs.AddBuff(SignatureBuff);
                }

                ApplyHpOverride(entity);

                spawned++;
                Log.Out("[DsWraith] Spawned at {0} (entityId={1})", pos, entity.entityId);
            }
            catch (Exception e)
            {
                Log.Error("[DsWraith] Spawn {0} failed: {1}", i, e);
            }
        }

        ctx.Reply("[00ff66]Spawned " + spawned + " Wraith(s).[-] " +
            "Spawned 15m out — walk forward into her " +
            (_cfg.SightRange == 0 ? "10m" : _cfg.SightRange + "m") +
            " sight range to trigger her aggro.");
    }

    /// <summary>
    /// Apply the configured MaxHealth override at spawn time. Mirrors
    /// the same Stats.Health.BaseMax / Value pattern used by Demon and
    /// Specter — entity_class XML defaults are inherited; this overrides
    /// post-construction so config edits hot-reload cleanly.
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

        var live = FindAllWraiths(world);
        if (live.Count == 0) { ctx.Reply("No live Wraiths found in the world."); return; }

        EntityAlive nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var w in live)
        {
            float d = Vector3.Distance(w.position, caller.position);
            if (d < nearestDist) { nearestDist = d; nearest = w; }
        }

        if (nearest != null)
        {
            ctx.Reply(string.Format(
                "[00ff66]{0} live Wraith(s).[-] Nearest: entityId={1} at {2} (distance {3:0.0}m, HP {4}/{5})",
                live.Count, nearest.entityId, nearest.position.ToString("F0"),
                nearestDist, nearest.Health, nearest.GetMaxHealth()));
        }
    }

    private void HandleDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        var live = FindAllWraiths(world);
        int removed = 0;
        foreach (var w in live)
        {
            try
            {
                world.RemoveEntity(w.entityId, EnumRemoveEntityReason.Despawned);
                removed++;
            }
            catch (Exception e)
            {
                Log.Warning("[DsWraith] Despawn of entity {0} failed: {1}", w.entityId, e.Message);
            }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Wraith(s).[-] (silent despawn)");
    }

    private void HandleStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }

        bool registered = EntityClass.list.ContainsKey(_classHash);
        ctx.Reply("[00ff66]DS:Wraith status[-]");
        ctx.Reply("  Class name:        " + ClassName);
        ctx.Reply("  Class hash:        " + _classHash + (registered ? " (registered)" : " [ff6666](NOT registered -- restart needed)[-]"));
        ctx.Reply("  Signature buff:    " + SignatureBuff + " (eyes + body wrap + regen)");

        ctx.Reply("[00ff66]Config[-] (Mods/Styx/Config/" + Name + ".json)");
        ctx.Reply("  Max health:        " + (_cfg.MaxHealth == 0
            ? "inherited (550 from zombieNurseFeral)"
            : _cfg.MaxHealth.ToString()));
        ctx.Reply("  Sight range:       " + (_cfg.SightRange == 0
            ? "inherited (10m via entity_class XML)"
            : _cfg.SightRange + "m"));

        if (registered)
        {
            var ec = EntityClass.list[_classHash];
            ctx.Reply("  Engine class id:   " + (ec?.entityClassName ?? "(none)"));
        }

        var live = FindAllWraiths(world);
        ctx.Reply("  Live count:        " + live.Count);
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllWraiths(World world)
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
    /// Top-level config persisted to Mods/Styx/Config/DsWraithPrototype.json.
    /// PluginWatcher hot-reloads the plugin on file edit. MaxHealth and
    /// SightRange (when > 0) override the inherited zombieNurseFeral
    /// defaults; the entity_class XML SightRange=10 is the default but
    /// this config field can override per-server tuning. NB: SightRange
    /// in config is applied at spawn time via the entity_class lookup
    /// (cf. how MaxHealth works) — existing live Wraiths keep the
    /// SightRange they spawned with.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Override for spawned Wraith max HP. 0 = use inherited
        /// zombieNurseFeral default (^healthNormalFeral=550). Default
        /// 200 makes her glass-cannon — the threat is the surprise
        /// contact, not surviving the fight.
        /// </summary>
        public int MaxHealth { get; set; } = 200;

        /// <summary>
        /// Override for SightRange (metres). 0 = use the entity_class XML
        /// default of 10 (low — the stealth mechanic). Bump to 20+ for
        /// "less stealthy, more aggressive" tuning; drop to 5 for
        /// "literally invisible until you're on top of her." Vanilla
        /// zombies sit around 30m for reference.
        /// NOTE: SightRange override at runtime requires Harmony patching
        /// the entity's vision, which is more invasive than HP override —
        /// for v0.1 the entity_class XML value is authoritative; this
        /// field is reserved for future use and currently informational
        /// only.
        /// </summary>
        public int SightRange { get; set; } = 0;
    }
}
