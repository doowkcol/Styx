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
// Darkness Stumbles -- DS:Threats prototype: The Grenadier (death grenade).
//
// A fast feral soldier that drops a live grenade when it dies, COD-style.
// Kill it and a grenade detonates at the corpse after a short fuse -- so a
// melee/point-blank kill puts the blast at YOUR feet (back off!), while a
// ranged kill is harmless (the grenade goes off far away). That corpse-
// position detonation makes it self-balancing: the player controls how
// dangerous the kill is by how they take it.
//
// TWO layers:
//   1. Visual identity + behaviour (XML). zombieDsGrenadier extends
//      zombieSoldierFeral -- fast, aggressive, rushes you (inherited feral
//      AI, no override needed). Glowing green eyes (the Wraith recipe,
//      p_twitch_zombie_radiation) are the "this one pops when it dies" tell,
//      so experienced players learn to kill it at range or back off.
//   2. Death grenade (C#). OnEntityKill hook: when a Grenadier dies, play a
//      grenade pin-pull at the corpse and schedule a GameManager.ExplosionServer
//      after a ~2.5s fuse (+ explosion sound). Same proven server-explosion
//      path as elsewhere. Detonates at the death position, attributed to no
//      one (-1) so it still damages whoever is in range, including the player
//      who meleed it -- and other zombies, so it can chain.
//
// REQUIRES: server restart after first install -- the engine reads
// entityclasses.xml / buffs.xml at boot only (synthesised from the blocks
// below by the framework's manifest synthesiser).
//
// Test plan:
//   1. /perm grant user <yourId> dsgrenadier.admin
//   2. /dsgrenadier spawn 1   -> a green-eyed soldier rushes you.
//   3. Melee it down -> hear the pin pull, RUN -> grenade detonates at the
//      corpse ~2.5s later. Stand on it and you eat the blast.
//   4. Shoot one from range -> it dies, grenade pops harmlessly at the corpse.
//   5. /dsgrenadier despawn (cleanup).

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-entityclasses
<!--
    zombieDsGrenadier — DS:Threats death-grenade variant. Extends
    zombieSoldierFeral: fast, aggressive, military mesh (already distinct from
    the other variants), and the feral AI rushes the player — exactly the
    in-your-face behaviour we want, so no AITask override. The only added
    identity is the green eye glow (auto-buffed on first spawn) which tells
    players "this one drops a grenade — kill it at range or back off."
    The death grenade itself is driven by the plugin C# (OnEntityKill).
-->
<entity_class name="zombieDsGrenadier" extends="zombieSoldierFeral">
    <effect_group name="DS Grenadier Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsGrenadierEyes"/>
    </effect_group>
</entity_class>
*/

/* @styx-buffs
<!--
    buffDsGrenadierEyes — the Grenadier's signature: glowing green eyes. Reuses
    the Wraith's eye recipe (p_twitch_zombie_radiation attached to the Head bone
    with eye-position offsets). Green reads as "tagged / dangerous" against the
    soldier's drab fatigues; it's the pre-kill tell for the death grenade.
    (Blue is a one-line swap to p_twitch_zombie_shock if we want to match
    Specter instead.) Hidden icon — it's a zombie identity buff, not a player
    readout. NB: offsets were tuned on the Nurse head (Wraith); the soldier's
    helmeted head may want a small offset tweak if the glow sits off the eyes.
-->
<buff name="buffDsGrenadierEyes"
      icon="ui_game_symbol_zombie"
      hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <effect_group name="DS Grenadier Eyes">
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
    </effect_group>
</buff>
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Grenadier in the high-tier cohort (burnt_forest / desert / snow /
     wasteland). A fast rusher with a death grenade is a real threat, so
     moderate weight. Not in the Low (pine_forest) cohort — starter biome
     stays gentle. -->
zombieDsGrenadier, .20
*/

/* @styx-patch entitygroups
<!-- Grenadier into blood-moon hordes. Soldiers rushing your base that pop
     grenades when you drop them is exactly the horde-night chaos we want, so
     a slightly higher weight than the rarer variants. -->
<append xpath="/entitygroups/entitygroup[starts-with(@name,'feralHordeStageGS')]">
    <entity name="zombieDsGrenadier" prob=".06"/>
</append>
*/

[Info("DsGrenadierPrototype", "Doowkcol", "0.1.0")]
public class DsGrenadierPrototype : StyxPlugin
{
    public override string Description => "DS:Threats Grenadier -- feral soldier that drops a grenade on death (internal).";

    // ============================================================ constants
    private const string ClassName     = "zombieDsGrenadier";
    private const string SignatureBuff = "buffDsGrenadierEyes";
    private const string AdminPerm     = "dsgrenadier.admin";

    private Config _cfg;
    private int _classHash;
    private TimerHandle _dedupeCleaner;

    /// <summary>Entity ids of Grenadiers that have already dropped their grenade.
    /// The engine re-fires <c>OnEntityKill</c> on every damage hit to an
    /// already-dead corpse (keep shooting the body), which would prime a fresh
    /// grenade — and an audible pin-pull "clink" — on every post-death shot.
    /// First add wins; later hits on the same id short-circuit. Cleared on a
    /// 5-minute cadence (well past corpse despawn) to stay bounded — same
    /// pattern as ZombieLoot's kill-dedupe.</summary>
    private readonly HashSet<int> _popped = new HashSet<int>();

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        StyxCore.Perms.RegisterKnown(AdminPerm, "Spawn DS:Grenadier death-grenade soldiers", Name);
        StyxCore.Commands.Register("dsgrenadier",
            "DS:Threats Grenadier -- /dsgrenadier <spawn|find|despawn|stats> [count]", HandleCommand);

        // The grenade is death-driven (OnEntityKill). The only tick is a cheap
        // housekeeping clear of the dedupe set; corpses despawn long before 5
        // min, so clearing can't cause a double-drop.
        _dedupeCleaner = Scheduler.Every(300.0, () => _popped.Clear(), name: "DsGrenadier.dedupe");

        Log.Out("[DsGrenadier] Loaded v0.1.0 -- class={0} hash={1} hp={2} fuse={3}s dmg={4}/{5}m",
            ClassName, _classHash, _cfg.MaxHealth, _cfg.FuseSeconds, _cfg.EntityDamage, _cfg.EntityRadius);
    }

    public override void OnUnload()
    {
        _dedupeCleaner?.Destroy(); _dedupeCleaner = null;
        _popped.Clear();
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ============================================================ death grenade

    /// <summary>
    /// Hook-bus auto-wired. Fires when any entity is killed; we act only on our
    /// Grenadier class. Drops a fused grenade at the corpse: pin-pull tell now,
    /// blast after the fuse. Killing it in melee leaves the blast at the
    /// player's feet; a ranged kill detonates it harmlessly far away.
    /// </summary>
    void OnEntityKill(EntityAlive victim, DamageResponse response)
    {
        if (victim == null || victim.entityClass != _classHash) return;
        if (!_cfg.Enabled) return;
        // Dedupe: OnEntityKill re-fires on every hit to the already-dead corpse.
        // First add wins; subsequent post-death shots short-circuit so we don't
        // prime a grenade (and play the pin-pull clink) per bullet.
        if (victim.entityId <= 0 || !_popped.Add(victim.entityId)) return;
        DropGrenade(victim.position);
    }

    private void DropGrenade(Vector3 pos)
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        // Pin-pull tell at the corpse the instant it dies -- the cue to run.
        try { gm.PlaySoundAtPositionServer(pos, "grenade_pullpin", AudioRolloffMode.Logarithmic, _cfg.SoundDistance); }
        catch (Exception e) { Log.Warning("[DsGrenadier] pin sound failed: " + e.Message); }

        if (_cfg.Verbose) Log.Out("[DsGrenadier] grenade primed @ {0}, fuse {1}s", pos, _cfg.FuseSeconds);

        double fuse = Math.Max(0.3, _cfg.FuseSeconds);
        Scheduler.Once(fuse, () =>
        {
            try
            {
                var g = GameManager.Instance;
                if (g == null) return;

                try { g.PlaySoundAtPositionServer(pos, "explosion_grenade", AudioRolloffMode.Logarithmic, _cfg.SoundDistance); }
                catch { /* sound is cosmetic */ }

                var ed = default(ExplosionData);
                ed.BlastPower    = _cfg.BlastPower;
                ed.BlockDamage   = _cfg.BlockDamage;
                ed.BlockRadius   = _cfg.BlockRadius;
                ed.BlockTags     = "";
                ed.EntityDamage  = _cfg.EntityDamage;
                ed.EntityRadius  = _cfg.EntityRadius;
                ed.ParticleIndex = _cfg.ParticleIndex;
                ed.IgnoreHeatMap = true;
                // entityId -1: the grenadier is already dead; no instigator. The
                // blast still damages players + other zombies in range (so it
                // can chain into nearby Grenadiers).
                g.ExplosionServer(0, pos, World.worldToBlockPos(pos), Quaternion.identity,
                    ed, -1, 0.1f, _bRemoveBlockAtExplPosition: false);
                if (_cfg.Verbose) Log.Out("[DsGrenadier] grenade detonated @ {0}", pos);
            }
            catch (Exception e) { Log.Warning("[DsGrenadier] detonation failed: " + e.Message); }
        }, name: "DsGrenadier.detonate");
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
            case "find":    CmdFind(ctx);    break;
            case "despawn": CmdDespawn(ctx); break;
            case "stats":   CmdStats(ctx);   break;
            default:
                ctx.Reply("DS:Grenadier commands:");
                ctx.Reply("  /dsgrenadier spawn [n]  -- spawn N soldiers ~10m out (max 10)");
                ctx.Reply("  /dsgrenadier find       -- distance to nearest live Grenadier");
                ctx.Reply("  /dsgrenadier despawn    -- remove all live Grenadiers");
                ctx.Reply("  /dsgrenadier stats      -- live count + class state");
                break;
        }
    }

    private void CmdSpawn(CommandContext ctx, int count)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }
        if (!EntityClass.list.ContainsKey(_classHash))
        {
            ctx.Reply("[ff6666]Grenadier entity class not registered.[-] Restart the server -- " +
                "entityclasses.xml is read at boot only.");
            return;
        }

        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + fwd * 10f;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float off = (i - (count - 1) * 0.5f) * 2.0f;
            Vector3 pos = basePos + right * off;
            int x = Utils.Fastfloor(pos.x), z = Utils.Fastfloor(pos.z);
            pos.y = Math.Max(world.GetHeight(x, z) + 1.0f, caller.position.y);
            if (SpawnAt(world, pos) != null) spawned++;
        }
        ctx.Reply("[00ff66]Spawned " + spawned + " Grenadier(s).[-] Kill one up close and RUN -- it drops a grenade.");
    }

    private EntityAlive SpawnAt(World world, Vector3 pos)
    {
        try
        {
            var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
            if (entity == null) { Log.Warning("[DsGrenadier] EntityFactory returned null for hash {0}", _classHash); return null; }
            entity.SetSpawnerSource(EnumSpawnerSource.StaticSpawner);
            world.SpawnEntityInWorld(entity);
            entity.Buffs?.AddBuff(SignatureBuff);
            if (_cfg.MaxHealth > 0 && entity.Stats?.Health != null)
            {
                entity.Stats.Health.BaseMax = _cfg.MaxHealth;
                entity.Stats.Health.Value   = _cfg.MaxHealth;
            }
            return entity;
        }
        catch (Exception e) { Log.Error("[DsGrenadier] SpawnAt failed: {0}", e); return null; }
    }

    private void CmdFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }
        var live = FindAll(world);
        if (live.Count == 0) { ctx.Reply("No live Grenadiers in the world."); return; }
        EntityAlive nearest = null; float best = float.MaxValue;
        foreach (var z in live)
        {
            float d = Vector3.Distance(z.position, caller.position);
            if (d < best) { best = d; nearest = z; }
        }
        ctx.Reply(string.Format("[00ff66]{0} live Grenadier(s).[-] Nearest: entityId={1} dist {2:0.0}m HP {3}/{4}",
            live.Count, nearest.entityId, best, nearest.Health, nearest.GetMaxHealth()));
    }

    private void CmdDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var live = FindAll(world);
        int removed = 0;
        foreach (var z in live)
        {
            try { world.RemoveEntity(z.entityId, EnumRemoveEntityReason.Despawned); removed++; }
            catch (Exception e) { Log.Warning("[DsGrenadier] despawn {0} failed: {1}", z.entityId, e.Message); }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Grenadier(s).[-]");
    }

    private void CmdStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        bool reg = EntityClass.list.ContainsKey(_classHash);
        var live = FindAll(world);
        ctx.Reply("[00ff66]DS:Grenadier status[-]");
        ctx.Reply("  Class:      " + ClassName + (reg ? " (registered)" : " [ff6666](NOT registered -- restart)[-]"));
        ctx.Reply("  Live count: " + live.Count);
        ctx.Reply("  Grenade:    " + (_cfg.Enabled
            ? string.Format("on -- {0}s fuse, {1} dmg / {2}m radius", _cfg.FuseSeconds, _cfg.EntityDamage, _cfg.EntityRadius)
            : "off"));
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAll(World world)
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
        /// <summary>Master enable for the death grenade. False = a plain green-eyed feral soldier.</summary>
        public bool Enabled = true;

        /// <summary>Spawned Grenadier max HP (manual /dsgrenadier spawn). 0 = inherit
        /// zombieSoldierFeral default. Biome / horde spawns use the class default.</summary>
        public int MaxHealth = 0;

        /// <summary>Seconds between death and detonation — the window to run.</summary>
        public double FuseSeconds = 2.5;

        /// <summary>Max distance (m) the pin-pull / explosion sounds carry.</summary>
        public int SoundDistance = 30;

        // ---- Grenade blast (ExplosionData) ----
        public int   BlastPower    = 18;
        public float BlockDamage   = 30f;
        public float BlockRadius   = 1f;
        public float EntityDamage  = 65f;
        public int   EntityRadius  = 4;
        public int   ParticleIndex = 13;

        /// <summary>Log each grenade prime + detonation.</summary>
        public bool Verbose = true;
    }
}
