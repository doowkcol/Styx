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
// Darkness Stumbles -- DS:Threats prototype: The Pyre (ranged fire bombardier).
//
// A zombie that HURLS FIRE. It lobs a big, visible flaming boulder in a slow
// arc; on impact the boulder bursts into fire and sets you ablaze. The first
// DS variant with a true ranged attack, and the first to use the framework's
// custom-item synthesis (@styx-items).
//
// DESIGN NOTE (v0.2): the v0.1 design tried to make it a KITER (retreat to
// hold range) with a separate C# frag mortar. Both were cut after testing:
//   - Kiting via RunawayFromEntity oriented the zombie AWAY from the target so
//     it threw backward, and it orbited as the player advanced/retreated.
//   - The C# frag mortar detonated AT the player's position decoupled from any
//     visible throw -- unreadable, undodgeable, and it insta-killed.
// v0.2 is one coherent, readable attack instead: the visible thrown projectile
// IS the threat. You see the wind-up (facing you), see the boulder arc in, and
// can dodge it; it explodes into fire where it lands.
//
// TWO layers, both effectively XML:
//   1. Visible fire throw. zombieDsPyre extends zombieChuck, inheriting Chuck's
//      proven ranged-throw AI + wind-up animation + Vomit launch (correct
//      target-facing). Its HandItem (meleeHandDsPyre) fires our projectile
//      (ammoDsFirebomb): a slow, big boulder that on impact explodes and
//      applies buffBurningMolotov to blast entities (Explosion.cs applies
//      BuffActions) AND on direct hit via onProjectileImpact. Same fire buff as
//      Hellhound / Kamikaze. Low impact damage -- the FIRE is the threat.
//   2. Identity (buff). Wreathed in flame (p_onFire body wrap) so it reads as a
//      fire-thrower from afar; light regen. Glass cannon: kill it fast.
//
// The C# layer is commands + manual-spawn helper + a 0.5s fire-strip tick that
// keeps the Pyre from dying in its own fire (it can't be baited to death; see
// the entity_class note on why BuffResistance doesn't work for server fire).
//
// REQUIRES: server restart after first install -- the engine reads items.xml /
// entityclasses.xml / buffs.xml at boot only (synthesised from the blocks below
// by the framework's manifest synthesiser, which now owns Config/items.xml).
//
// Test plan:
//   1. /perm grant user <yourId> dspyre.admin
//   2. /dspyre spawn 1   -> a burning zombie ~12m out winds up (facing you) and
//                           lobs a flaming boulder in a slow arc.
//   3. Dodge the arc, or eat it -> impact bursts into fire, you catch alight
//      (buffBurningMolotov). It closes to melee if you get near.
//   4. /dspyre despawn (cleanup).

using System;
using System.Collections.Generic;
using Styx;
using Styx.Commands;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

/* @styx-items
<!--
    ammoDsFirebomb — the Pyre's thrown firebomb projectile. Class=Projectile
    (a real flying ballistic round, like Chuck's boulder) but using the molotov
    thrown mesh so it reads as a flying bottle. On impact it explodes and
    applies buffBurningMolotov to every entity in the blast (Explosion.Buff ->
    ExplosionData.BuffActions -> Explosion.cs AddBuff). Modest direct/blast
    damage; the THREAT is the fire, not the impact. Ballistic params mirror the
    boulder so the throw arcs naturally toward the target.
-->
<item name="ammoDsFirebomb">
    <!-- Boulder mesh: a big, clearly-visible projectile you can watch arc in
         and dodge (the molotov bottle was too small to read). It bursts into
         fire on impact rather than carrying in-flight flames. To make it a
         flaming projectile in flight instead, swap to the molotov thrown
         prefab @:Other/Items/Weapons/Ranged/Molotov/molotov_thrownPrefab. -->
    <property name="Meshfile" value="@:Other/Items/Weapons/Ranged/Boulder/BoulderThrownPrefab.prefab"/>
    <property name="Material" value="Morganic"/>
    <property name="CreativeMode" value="None"/>
    <property name="CustomIcon" value="missingIcon"/>
    <property class="Action1">
        <property name="Class" value="Projectile"/>
        <!-- Low direct/blast damage on purpose: the THREAT is the fire DoT,
             not the impact. A direct hit should hurt + ignite, not delete you.
             (The insta-kills were the old decoupled frag mortar, now removed.) -->
        <property name="DamageEntity" value="8"/>
        <property name="DamageBlock" value="10"/>
        <property name="Explosion.ParticleIndex" value="10"/><!-- molotov fire explosion -->
        <property name="Explosion.RadiusBlocks" value="0.5"/>
        <property name="Explosion.RadiusEntities" value="3.5"/>
        <property name="Explosion.EntityDamage" value="10"/>
        <property name="Explosion.BlockDamage" value="15"/>
        <property name="Explosion.Buff" value="buffBurningMolotov"/><!-- set blast entities ablaze -->
        <!-- Slow ballistic arc so it is readable + dodgeable. Negative FlyTime
             solves velocity to the target; lower Velocity = a lazier, more
             telegraphed lob than Chuck's fast boulder (50). -->
        <property name="FlyTime" value="-5"/>
        <property name="Velocity" value="22"/>
        <property name="Gravity" value="-9"/>
        <property name="LifeTime" value="8"/>
        <property name="CollisionRadius" value=".3"/>
    </property>
    <effect_group name="ammoDsFirebomb" tiered="false">
        <passive_effect name="ModSlots" operation="base_set" value="0"/>
        <!-- Reliable direct-hit ignite (same mechanism vanilla flaming arrows
             use). Explosion.Buff above covers near-miss AOE ignition. -->
        <triggered_effect trigger="onProjectileImpact" action="AddBuff" target="other" buff="buffBurningMolotov"/>
    </effect_group>
</item>

<!--
    meleeHandDsPyre — the Pyre's HandItem. Extends Chuck's hand (keeps the
    Action0 melee + the Action1 "Vomit" ranged-launch wiring + the spread /
    magazine passives) and only swaps the launched projectile from the boulder
    to ammoDsFirebomb. The entity's AITask (RangedAttackTarget itemType=1)
    triggers Action1, which launches the firebomb.
-->
<item name="meleeHandDsPyre">
    <property name="Extends" value="meleeHandZombieChuck"/>
    <property name="CreativeMode" value="None"/>
    <property name="Degradation" value="99999" param1="true"/>
    <property class="Action1"> <!-- UseAction -->
        <property name="Magazine_items" value="ammoDsFirebomb"/>
    </property>
    <effect_group tiered="false">
        <passive_effect name="ModSlots" operation="base_set" value="0"/>
        <passive_effect name="DamageFalloffRange" operation="base_set" value="50"/>
        <passive_effect name="MaxRange" operation="base_set" value="100"/>
        <passive_effect name="MagazineSize" operation="base_set" value="1"/>
        <passive_effect name="BurstRoundCount" operation="base_set" value="1"/>
        <passive_effect name="SpreadDegreesVertical" operation="base_set" value="2"/>
        <passive_effect name="SpreadDegreesHorizontal" operation="base_set" value="4"/>
        <passive_effect name="SpreadMultiplierIdle" operation="base_set" value="1"/>
    </effect_group>
</item>
*/

/* @styx-entityclasses
<!--
    zombieDsPyre — DS:Threats ranged fire bombardier. Extends zombieChuck to
    inherit the proven ranged-throw AI + wind-up animation + the Vomit launch
    mechanism for free. Overrides:
      - HandItem -> meleeHandDsPyre (launches the firebomb boulder, not Chuck's)
      - SizeScale 1.0 (down from Chuck's 1.2 — a touch leaner; the fire wrap
        is the identity, not the bloat)
      - AITask -> Chuck's own bombard-then-melee list, verbatim except the
        projectile is ours. NO kiting: an earlier RunawayFromEntity attempt
        made it orient AWAY from the target (to flee) and then throw backward,
        plus it orbited as the player advanced/retreated. Chuck's plain AI
        faces the target correctly and throws toward it. cooldown 6 + slow
        projectile = spaced, telegraphed, dodgeable lobs; it closes to melee
        inside minRange. (True kiting needs a custom retreat that also forces
        target-facing during the throw — a later pass if wanted.)
    Auto-buffs buffDsPyreFlame on first spawn (biome / horde spawns bypass the
    C# spawn path).
-->
<entity_class name="zombieDsPyre" extends="zombieChuck">
    <property name="SizeScale" value="1.0"/>
    <property name="HandItem" value="meleeHandDsPyre"/>
    <property name="AITask" value="
    BreakBlock|
    ApproachDistraction|
    RangedAttackTarget startAnimType=2;itemType=1;cooldown=6;duration=5;minRange=6;maxRange=25;releaseDelay=.41;sndStart=chuckwarning;sndRelease=chuckrelease|
    ApproachAndAttackTarget class=EntityPlayer,0,EntityBandit,0,EntityEnemyAnimal,0,EntityAnimal|
    ApproachSpot|
    Look|
    Wander|
    "/>
    <effect_group name="DS Pyre Auto Buff">
        <triggered_effect trigger="onSelfFirstSpawn" action="AddBuff" buff="buffDsPyreFlame"/>
        <!-- NB: fire immunity is NOT done here. BuffResistance only gates buff
             application when _netSync==true (EntityBuffs.cs:190), but molotov
             ground-fire applies buffBurningMolotov via ExplosionDamageArea with
             _netSync = isEntityRemote = FALSE on a server-side zombie, so the
             resistance is bypassed for the area fire (it blocked only the
             netSync=true buffIsOnFire visual -> "no flames but still dying").
             Instead the plugin C# strips the damaging burn buff every 0.5s
             (DsPyrePrototype.StripFire), which works regardless of how the
             buff was applied. The brief catch-fire visual is kept (realistic);
             the DoT just never accumulates. -->
    </effect_group>
</entity_class>
*/

/* @styx-buffs
<!--
    buffDsPyreFlame — the Pyre's signature. Body wrapped in flame (p_onFire on
    the .body transform, the same recipe as Hellhound / Kamikaze) so it reads as
    a burning figure from a distance — the telegraph that this one throws fire.
    Light regen so ranged chip damage does not drop it before it commits.
    Hidden icon (it is a zombie identity buff, not a player readout).
-->
<buff name="buffDsPyreFlame"
      icon="ui_game_symbol_fire"
      hidden="true">
    <stack_type value="ignore"/>
    <duration value="0"/>
    <update_rate value="2"/>
    <effect_group name="DS Pyre Flame">
        <triggered_effect trigger="onSelfBuffStart" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>
        <triggered_effect trigger="onSelfEnteredGame" action="AttachParticleEffectToEntity"
            particle="p_onFire" parent_transform=".body"/>
        <!-- ~1.5 HP/sec relentless regen. Keeps it in the fight through chip
             damage while you chase, without making it tanky (focused fire
             still drops a glass cannon fast). -->
        <passive_effect name="HealthChangeOT" operation="base_add" value="3"/>
    </effect_group>
</buff>
*/

/* @styx-entitygroups DsThreatsHigh
<!-- Pyre in the high-tier cohort (burnt_forest / desert / snow / wasteland).
     A ranged fire-thrower is a meaningful threat, so moderate weight. Not in
     the Low (pine_forest) cohort — the starter biome stays gentle. -->
zombieDsPyre, .20
*/

/* @styx-patch entitygroups
<!-- Pyre into blood-moon hordes (low). A fire-lobber on the edge of a horde
     night zones your defensive position from range. Rare so it is a spike, not
     a constant. POI sleepers omitted — a ranged thrower in a tight sleeper
     room just closes to melee (no room to arc), so it adds little there. -->
<append xpath="/entitygroups/entitygroup[starts-with(@name,'feralHordeStageGS')]">
    <entity name="zombieDsPyre" prob=".04"/>
</append>
*/

[Info("DsPyrePrototype", "Doowkcol", "0.1.0")]
public class DsPyrePrototype : StyxPlugin
{
    public override string Description => "DS:Threats Pyre -- ranged fire bombardier (internal).";

    // ============================================================ constants
    private const string ClassName     = "zombieDsPyre";
    private const string SignatureBuff = "buffDsPyreFlame";
    private const string AdminPerm     = "dspyre.admin";

    private Config _cfg;
    private int _classHash;
    private TimerHandle _fireStripTick;

    // Burn buffs a Pyre can pick up from its own fire. We strip the DAMAGING
    // ones each tick so it can't be baited to death in its own flames.
    // buffIsOnFire (the visual) is deliberately NOT stripped -- it self-removes
    // once the underlying burn buff is gone, leaving a brief, realistic
    // catch-fire flicker without the damage.
    private static readonly string[] BurnBuffs = { "buffBurningMolotov", "buffBurningZombie" };

    // ============================================================ lifecycle

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);
        _classHash = ClassName.GetHashCode();

        StyxCore.Perms.RegisterKnown(AdminPerm, "Spawn DS:Pyre fire-throwers", Name);
        StyxCore.Commands.Register("dspyre",
            "DS:Threats Pyre -- /dspyre <spawn|find|despawn|stats> [count]", HandleCommand);

        // Fire-strip tick: a fire-thrower must not die to its own fire. We
        // can't use BuffResistance (bypassed for server-applied area fire --
        // see the entity_class note), so we strip the damaging burn buff from
        // live Pyres every 0.5s. The DoT curve starts at ~1 HP/s, so at most
        // ~0.5 HP lands per catch before removal -- trivial, and the signature
        // buff's regen covers it. They still flicker on fire (realistic).
        _fireStripTick = Scheduler.Every(0.5, StripFire, name: "DsPyre.fireStrip");

        Log.Out("[DsPyre] Loaded v0.1.0 -- class={0} hash={1} hp={2} (fire-strip active)",
            ClassName, _classHash, _cfg.MaxHealth);
    }

    public override void OnUnload()
    {
        _fireStripTick?.Destroy(); _fireStripTick = null;
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    // ============================================================ fire-strip tick

    /// <summary>
    /// Remove the damaging burn buff(s) from every live Pyre so it can't be
    /// baited to death in its own fire. Runs every 0.5s; cheap (one linear
    /// scan of live Pyres, RemoveBuff only when present). buffIsOnFire is left
    /// alone -- it self-removes when the burn buff is gone, so a brief
    /// catch-fire visual remains without the DoT.
    /// </summary>
    private void StripFire()
    {
        var world = GameManager.Instance?.World;
        if (world == null) return;
        var live = FindAllPyre(world);
        for (int i = 0; i < live.Count; i++)
        {
            var b = live[i].Buffs;
            if (b == null) continue;
            for (int k = 0; k < BurnBuffs.Length; k++)
                if (b.HasBuff(BurnBuffs[k])) b.RemoveBuff(BurnBuffs[k]);
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
            case "find":    CmdFind(ctx);    break;
            case "despawn": CmdDespawn(ctx); break;
            case "stats":   CmdStats(ctx);   break;
            default:
                ctx.Reply("DS:Pyre commands:");
                ctx.Reply("  /dspyre spawn [n]  -- spawn N fire-throwers ~12m out (max 10)");
                ctx.Reply("  /dspyre find       -- distance to nearest live Pyre");
                ctx.Reply("  /dspyre despawn    -- remove all live Pyres");
                ctx.Reply("  /dspyre stats      -- live count + class state");
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
            ctx.Reply("[ff6666]Pyre entity class not registered.[-] Restart the server -- " +
                "items.xml / entityclasses.xml are read at boot only.");
            return;
        }

        Vector3 fwd = caller.GetForwardVector();
        Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;
        Vector3 basePos = caller.position + fwd * 12f;

        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            float off = (i - (count - 1) * 0.5f) * 2.0f;
            Vector3 pos = basePos + right * off;
            int x = Utils.Fastfloor(pos.x), z = Utils.Fastfloor(pos.z);
            pos.y = Math.Max(world.GetHeight(x, z) + 1.0f, caller.position.y);
            if (SpawnAt(world, pos) != null) spawned++;
        }
        ctx.Reply("[00ff66]Spawned " + spawned + " Pyre(s).[-] They lob flaming boulders that burst into fire on impact -- dodge the arc.");
    }

    private EntityAlive SpawnAt(World world, Vector3 pos)
    {
        try
        {
            var entity = EntityFactory.CreateEntity(_classHash, pos) as EntityAlive;
            if (entity == null) { Log.Warning("[DsPyre] EntityFactory returned null for hash {0}", _classHash); return null; }
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
        catch (Exception e) { Log.Error("[DsPyre] SpawnAt failed: {0}", e); return null; }
    }

    private void CmdFind(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var caller = world.GetEntity(ctx.Client.entityId) as EntityPlayer;
        if (caller == null) { ctx.Reply("Could not resolve your player entity."); return; }
        var live = FindAllPyre(world);
        if (live.Count == 0) { ctx.Reply("No live Pyres in the world."); return; }
        EntityAlive nearest = null; float best = float.MaxValue;
        foreach (var z in live)
        {
            float d = Vector3.Distance(z.position, caller.position);
            if (d < best) { best = d; nearest = z; }
        }
        ctx.Reply(string.Format("[00ff66]{0} live Pyre(s).[-] Nearest: entityId={1} dist {2:0.0}m HP {3}/{4}",
            live.Count, nearest.entityId, best, nearest.Health, nearest.GetMaxHealth()));
    }

    private void CmdDespawn(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        var live = FindAllPyre(world);
        int removed = 0;
        foreach (var z in live)
        {
            try { world.RemoveEntity(z.entityId, EnumRemoveEntityReason.Despawned); removed++; }
            catch (Exception e) { Log.Warning("[DsPyre] despawn {0} failed: {1}", z.entityId, e.Message); }
        }
        ctx.Reply("[ff6666]Removed " + removed + " Pyre(s).[-]");
    }

    private void CmdStats(CommandContext ctx)
    {
        var world = GameManager.Instance?.World;
        if (world == null) { ctx.Reply("World not available."); return; }
        bool reg = EntityClass.list.ContainsKey(_classHash);
        var live = FindAllPyre(world);
        ctx.Reply("[00ff66]DS:Pyre status[-]");
        ctx.Reply("  Class:      " + ClassName + (reg ? " (registered)" : " [ff6666](NOT registered -- restart)[-]"));
        ctx.Reply("  Live count: " + live.Count);
        ctx.Reply("  HP:         " + (_cfg.MaxHealth > 0 ? _cfg.MaxHealth.ToString() : "inherited"));
    }

    // ============================================================ helpers

    private List<EntityAlive> FindAllPyre(World world)
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
        /// <summary>Reserved master toggle (currently the Pyre is fully XML-driven;
        /// kept so future C# behaviour can be gated without a schema change).</summary>
        public bool Enabled = true;

        /// <summary>Spawned Pyre max HP (manual /dspyre spawn). 0 = inherit
        /// zombieChuck default. Glass cannon -- kill fast or get zoned by fire.
        /// NB: biome / horde spawns use the entity_class default, not this.</summary>
        public int MaxHealth = 250;
    }
}
