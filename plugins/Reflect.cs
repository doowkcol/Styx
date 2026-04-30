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

// Reflect plugin — demonstrates cancellable hooks.
//
// Only affects players (never zombies/animals). Four modes via /reflect <mode>:
//   off     — passthrough
//   shield  — cancel ALL damage taken (god mode)
//   back    — take full damage, attacker takes same amount back
//   double  — take half, attacker takes 2x back
//
// Demonstrates three outcomes from a single hook method:
//   return null  → game runs unmodified
//   return false → damage cancelled
//   return int   → damage replaced with that amount

using System;
using Styx;
using Styx.Plugins;

[Info("Reflect", "Doowkcol", "0.1.0")]
public class Reflect : StyxPlugin
{
    public enum Mode { Off, Shield, Back, Double }

    public class Config
    {
        public Mode Mode = Mode.Off;
        public float BackMultiplier = 1.0f;
        public float DoubleReflectFactor = 2.0f;
        public float DoubleTakeFraction = 0.5f;
    }

    private Config _cfg;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        StyxCore.Commands.Register("reflect", "Set damage reflect mode — /reflect <off|shield|back|double>", (ctx, args) =>
        {
            if (args.Length == 0) { ShowStatus(ctx); return; }
            if (Enum.TryParse<Mode>(args[0], true, out var m))
            {
                _cfg.Mode = m;
                StyxCore.Configs.Save(this, _cfg);
                ctx.Reply("[00ff66]Reflect mode now: " + _cfg.Mode + "[-]");
                Log.Out("[Reflect] Mode changed to {0} by {1}", _cfg.Mode, ctx.SenderName);
            }
            else
            {
                ctx.Reply("[ff6666]Unknown mode. Valid: off, shield, back, double[-]");
            }
        });

        Log.Out("[Reflect] Loaded v0.1.0, mode={0}", _cfg.Mode);
    }

    private void ShowStatus(Styx.Commands.CommandContext ctx)
    {
        ctx.Reply("Current mode: [00ff66]" + _cfg.Mode + "[-]");
        ctx.Reply("  off     — no effect");
        ctx.Reply("  shield  — cancel all incoming damage");
        ctx.Reply("  back    — take full damage, reflect " + _cfg.BackMultiplier + "x to attacker");
        ctx.Reply("  double  — take " + (_cfg.DoubleTakeFraction * 100) + "%, reflect " + _cfg.DoubleReflectFactor + "x");
    }

    // The framework fires this hook just before the engine applies damage.
    // Returning non-null alters or cancels the damage:
    //   null  → game unchanged
    //   false → cancel
    //   int   → replace damage value
    object OnEntityDamage(EntityAlive victim, DamageSource source, int strength, bool critical)
    {
        if (_cfg == null || _cfg.Mode == Mode.Off) return null;
        if (!(victim is EntityPlayer player)) return null;  // only affect players
        if (strength <= 0) return null;

        switch (_cfg.Mode)
        {
            case Mode.Shield:
                Log.Out("[Reflect] Shield: blocked {0} damage from {1}", strength, DescribeSource(source));
                return false;   // cancel damage entirely

            case Mode.Back:
            {
                int reflected = (int)(strength * _cfg.BackMultiplier);
                DealBack(source, reflected, player);
                Log.Out("[Reflect] Back: took {0}, reflected {1} to {2}", strength, reflected, DescribeSource(source));
                return null;    // player still takes the full hit
            }

            case Mode.Double:
            {
                int reflected = (int)(strength * _cfg.DoubleReflectFactor);
                int taken = Math.Max(1, (int)(strength * _cfg.DoubleTakeFraction));
                DealBack(source, reflected, player);
                Log.Out("[Reflect] Double: took {0} (of {1}), reflected {2} to {3}",
                    taken, strength, reflected, DescribeSource(source));
                return taken;   // reduce damage taken
            }
        }
        return null;
    }

    private static void DealBack(DamageSource source, int amount, EntityPlayer victim)
    {
        if (amount <= 0) return;
        var attacker = ResolveAttacker(source, victim);
        if (attacker == null || attacker.IsDead()) return;

        // Synthetic damage source — no entity attribution — so recursive hook
        // calls see attackerId == -1 and short-circuit (no infinite loop).
        var reflectSrc = new DamageSource(EnumDamageSource.External, EnumDamageTypes.Bashing);
        attacker.DamageEntity(reflectSrc, amount, false, 1f);
    }

    private static string DescribeSource(DamageSource s)
    {
        // Static sources (DamageSource.eat etc.) carry no attacker — we'll resolve
        // at reflection time via nearby-entity scan.
        int id = s?.getEntityId() ?? -1;
        if (id >= 0)
        {
            var e = GameManager.Instance?.World?.GetEntity(id) as EntityAlive;
            return e?.EntityName ?? e?.EntityClass?.entityClassName ?? ("entity#" + id);
        }
        return s?.damageType.ToString() ?? "unknown";
    }

    /// <summary>
    /// Try source.getEntityId() first; if that's unset (zombie AI uses a shared
    /// static DamageSource), fall back to scanning nearby entities for whoever
    /// is currently targeting the victim. Good enough for melee.
    /// </summary>
    private static EntityAlive ResolveAttacker(DamageSource source, EntityPlayer victim)
    {
        int id = source?.getEntityId() ?? -1;
        if (id >= 0 && id != victim.entityId)
        {
            if (GameManager.Instance?.World?.GetEntity(id) is EntityAlive attacker) return attacker;
        }

        var world = GameManager.Instance?.World;
        if (world == null) return null;

        EntityAlive best = null;
        float bestDist = 4f * 4f; // within 4m considered "in melee reach"
        foreach (var ent in world.Entities.list)
        {
            if (!(ent is EntityAlive candidate)) continue;
            if (candidate == victim || candidate.IsDead()) continue;
            if (candidate.GetAttackTarget() != victim) continue;
            float d2 = (candidate.position - victim.position).sqrMagnitude;
            if (d2 < bestDist) { bestDist = d2; best = candidate; }
        }
        return best;
    }
}
