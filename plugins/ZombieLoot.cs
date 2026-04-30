// SPDX-License-Identifier: MIT
//
// Styx -- server-side modding framework for 7 Days to Die V2.6
// Copyright (c) 2026 Doowkcol (Jack Lockwood)
//
// This file is part of the Styx open-source plugin set.
// Distributed under the MIT License -- see LICENSE at the repo root.
//

// ZombieLoot v0.4 — drops a lootable bag when a zombie dies, with:
//   - perm-tiered drop chance (e.g. vip > basic) — first match wins
//   - perm-tiered loot quality (basic / vip / master tables)
//   - night-time tier boost (zombies are harder at night → better loot)
//   - blood-moon suppression (no spam during hordes)
//   - per-zombie-type loot tables within each quality tier
//   - configurable bag lifetime + scheduled despawn
//
// Bag-only: v0.3 had a block-first / bag-fallback pathway, but custom-block
// placement was unreliable on uneven terrain so we dropped the block path
// (user feedback). EntityBackpack is portable, deterministic and works.
//
// Killer attribution: hooks `OnEntityKill(victim, DamageResponse)` instead of
// `OnEntityDeath` so we can read the attacker via response.Source.getEntityId().
// If the killer isn't a player (zombie-on-zombie, fall, fire), no drop fires.
//
// Config: configs/ZombieLoot.json (auto-created, schema v4 — old v3 configs
// will fail-parse → fresh defaults; back up if you'd customised earlier).
//
// Tier perms (default seed — register them on PermEditor groups as needed):
//   styx.zloot.master  → 100% drop, "master" loot table (admin-grade)
//   styx.zloot.vip     → 85%  drop, "vip"    loot table (donor-grade)
//   styx.zloot.use     → 60%  drop, "basic"  loot table (default group)
//
// Players with no matching perm → zero drops.

using System;
using System.Collections.Generic;
using Styx;
using Styx.Plugins;
using Styx.Scheduling;
using UnityEngine;

[Info("ZombieLoot", "Doowkcol", "0.4.0")]
public class ZombieLoot : StyxPlugin
{
    public override string Description => "Lootable bag on zombie death — perm-tiered drop chance + quality, night boost, blood-moon suppress";

    // ------------------------------------------------------------------ schema

    public class LootEntry
    {
        public string Item;
        public int MinCount = 1;
        public int MaxCount = 1;
        public float Chance = 1.0f;  // 0.0-1.0 — independent roll per entry
    }

    /// <summary>One quality table — has a default fallback list and optional
    /// per-zombie-class overrides. Roll picks ByClass first, then Default.</summary>
    public class QualityTable
    {
        public List<LootEntry> Default = new List<LootEntry>();
        public Dictionary<string, List<LootEntry>> ByClass =
            new Dictionary<string, List<LootEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    public class TierEntry
    {
        public string Perm;             // e.g. "styx.zloot.vip"
        public float DropChance = 1.0f; // 0..1 — chance any bag spawns at all
        public string Quality;          // key into Config.QualityTiers
    }

    public class Config
    {
        public bool Enabled = true;

        /// <summary>True = no loot bags spawn during blood moon (avoids
        /// bag spam from horde kills). False = loot drops as normal.</summary>
        public bool SuppressOnBloodMoon = true;

        /// <summary>Hard cap on stacks per bag (after rolling drop table).
        /// Stops degenerate configs from creating massive bags.</summary>
        public int MaxItemsPerBag = 8;

        /// <summary>Bag lifetime in seconds. Bag is scheduled for removal
        /// after this delay. Set to 0 to disable scheduled despawn.</summary>
        public int BagLifetimeSeconds = 300;  // 5 min

        // ---- Night boost ----

        /// <summary>True = killing zombies during night-time hours bumps
        /// the player's quality tier up by one rung (basic → vip → master).
        /// Drop-chance is taken from the player's own perm tier always —
        /// only the loot table being rolled is upgraded. Players already
        /// at the highest tier get no further boost.</summary>
        public bool NightBoostEnabled = true;

        /// <summary>Night window start hour (0-23). Default 22:00.</summary>
        public int NightStartHour = 22;

        /// <summary>Night window end hour (0-23). Default 06:00.
        /// Window wraps midnight if End ≤ Start.</summary>
        public int NightEndHour = 6;

        // ---- Tier perms ----

        /// <summary>Ordered list of tiers — first perm the killer has wins.
        /// Empty list, or no match for the killer = no drop spawns.</summary>
        public List<TierEntry> DropTiers = new List<TierEntry>
        {
            new TierEntry { Perm = "styx.zloot.master", DropChance = 1.00f, Quality = "master" },
            new TierEntry { Perm = "styx.zloot.vip",    DropChance = 0.85f, Quality = "vip"    },
            new TierEntry { Perm = "styx.zloot.use",    DropChance = 0.60f, Quality = "basic"  },
        };

        /// <summary>Ascending tier order — used by the night-boost mechanic
        /// to find "the next tier up" given a current quality name. Should
        /// list every key present in <see cref="QualityTiers"/>, lowest first.</summary>
        public List<string> TierOrder = new List<string> { "basic", "vip", "master" };

        /// <summary>Quality table by name (referenced by TierEntry.Quality
        /// and TierOrder). Add your own keys here; just keep TierOrder
        /// in sync so night-boost cascades work.</summary>
        public Dictionary<string, QualityTable> QualityTiers =
            new Dictionary<string, QualityTable>(StringComparer.OrdinalIgnoreCase)
            {
                ["basic"]  = MakeBasicTable(),
                ["vip"]    = MakeVipTable(),
                ["master"] = MakeMasterTable(),
            };

        // ------------------------------------------------------------ tables

        // ---- BASIC (default group / lowest tier) ---------------------
        // Realistic "zombie pocket" survival loot — food, drinks, basic
        // medical, small materials. Item names sourced from the
        // LootableZombieCorpse v2.8 reference mod (V2.6-validated). Each
        // ByClass entry is themed: Nurse=medical, Cop=ammo, Boe=materials,
        // etc. Default catch-all hits any zombie type without a ByClass
        // override (e.g. modlet-added zombies fall back to Default).
        private static QualityTable MakeBasicTable() => new QualityTable
        {
            Default = new List<LootEntry>
            {
                new LootEntry { Item = "foodCanSoup",       MinCount = 1, MaxCount = 1, Chance = 0.35f },
                new LootEntry { Item = "foodCanMiso",       MinCount = 1, MaxCount = 1, Chance = 0.20f },
                new LootEntry { Item = "drinkJarRiverWater",MinCount = 1, MaxCount = 2, Chance = 0.40f },
                new LootEntry { Item = "resourceBone",      MinCount = 1, MaxCount = 3, Chance = 0.35f },
                new LootEntry { Item = "resourceCloth",     MinCount = 1, MaxCount = 2, Chance = 0.30f },
                new LootEntry { Item = "medicalBandage",    MinCount = 1, MaxCount = 1, Chance = 0.20f },
                new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 6, Chance = 0.40f },
                new LootEntry { Item = "foodRottingFlesh",  MinCount = 1, MaxCount = 1, Chance = 0.30f },
            },
            ByClass = new Dictionary<string, List<LootEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                // Medical staff — bandages + drugs
                ["zombieNurse"] = new List<LootEntry>
                {
                    new LootEntry { Item = "medicalBandage",         MinCount = 1, MaxCount = 2, Chance = 0.55f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "drugPainkillers",        MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "drugHerbalAntibiotics",  MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "medicalAloeCream",       MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "drinkJarRiverWater",     MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 1, MaxCount = 5, Chance = 0.30f },
                },
                // Cops — ammo + occasional weapon
                ["zombieFatCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",     MinCount = 3, MaxCount = 10, Chance = 0.45f },
                    new LootEntry { Item = "foodCanSoup",            MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",           MinCount = 1, MaxCount = 1,  Chance = 0.25f },
                    new LootEntry { Item = "medicalBandage",         MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 1, MaxCount = 8,  Chance = 0.40f },
                    new LootEntry { Item = "resourceDuctTape",       MinCount = 1, MaxCount = 1,  Chance = 0.10f },
                },
                ["zombieCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",     MinCount = 4, MaxCount = 12, Chance = 0.50f },
                    new LootEntry { Item = "ammoShotgunShell",       MinCount = 1, MaxCount = 4,  Chance = 0.20f },
                    new LootEntry { Item = "drinkJarBeer",           MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "medicalBandage",         MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 1, MaxCount = 8,  Chance = 0.40f },
                },
                // Office workers — wallet + paper + books
                ["zombieBusinessMan"] = new List<LootEntry>
                {
                    new LootEntry { Item = "oldCash",          MinCount = 5, MaxCount = 20, Chance = 0.85f },
                    new LootEntry { Item = "resourcePaper",    MinCount = 2, MaxCount = 5,  Chance = 0.45f },
                    new LootEntry { Item = "drinkJarBoiledWater", MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "foodCanMiso",      MinCount = 1, MaxCount = 1,  Chance = 0.25f },
                    new LootEntry { Item = "medicalBandage",   MinCount = 1, MaxCount = 1,  Chance = 0.15f },
                },
                // Tropical drunks — food + booze
                ["zombieFatHawaiian"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanChili",     MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "foodCanTuna",      MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",     MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "drinkJarYuccaJuice", MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "foodCharredMeat",  MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",          MinCount = 1, MaxCount = 4, Chance = 0.30f },
                },
                ["zombieFemaleFat"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",      MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "foodPumpkinPie",   MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "drinkJarBeer",     MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "resourceCloth",    MinCount = 2, MaxCount = 4, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",          MinCount = 1, MaxCount = 5, Chance = 0.30f },
                },
                // Working class men — materials + tools + booze
                ["zombieBoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron", MinCount = 2, MaxCount = 5, Chance = 0.50f },
                    new LootEntry { Item = "resourceWood",      MinCount = 2, MaxCount = 5, Chance = 0.40f },
                    new LootEntry { Item = "resourceDuctTape",  MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "drinkJarBeer",      MinCount = 1, MaxCount = 1, Chance = 0.35f },
                    new LootEntry { Item = "foodCharredMeat",   MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "resourceSewingKit", MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 4, Chance = 0.30f },
                },
                ["zombieJoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron", MinCount = 2, MaxCount = 4, Chance = 0.45f },
                    new LootEntry { Item = "resourceMetalPipe", MinCount = 1, MaxCount = 2, Chance = 0.20f },
                    new LootEntry { Item = "drinkJarBeer",      MinCount = 1, MaxCount = 1, Chance = 0.35f },
                    new LootEntry { Item = "foodCanLamb",       MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "resourceLockPick",  MinCount = 1, MaxCount = 1, Chance = 0.15f },
                },
                ["zombieMoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",     MinCount = 2, MaxCount = 4, Chance = 0.40f },
                    new LootEntry { Item = "resourceLeather",   MinCount = 1, MaxCount = 2, Chance = 0.25f },
                    new LootEntry { Item = "drinkJarBeer",      MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "foodCanSoup",       MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 4, Chance = 0.30f },
                },
                ["zombieSteve"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron", MinCount = 1, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarRiverWater",MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodCharredMeat",   MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "medicalBandage",    MinCount = 1, MaxCount = 1, Chance = 0.20f },
                },
                // Females — generally housewife / scavenger fare
                ["zombieArlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",       MinCount = 1, MaxCount = 1, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarRiverWater",MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "resourceBone",      MinCount = 2, MaxCount = 4, Chance = 0.40f },
                    new LootEntry { Item = "resourceSewingKit", MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "medicalBandage",    MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 5, Chance = 0.35f },
                },
                ["zombieMarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodBakedPotato",   MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarRiverWater",MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "resourceCloth",     MinCount = 1, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "medicalBandage",    MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 4, Chance = 0.30f },
                },
                ["zombieDarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "drinkJarBeer",      MinCount = 1, MaxCount = 1, Chance = 0.40f },
                    new LootEntry { Item = "foodCanChili",      MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "resourceCloth",     MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",           MinCount = 2, MaxCount = 6, Chance = 0.40f },
                },
                // Spider — agile, scrap-y
                ["zombieSpider"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",     MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "resourceBone",      MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",           MinCount = 1, MaxCount = 4, Chance = 0.25f },
                },
                // Screamer — drug-induced rage; small chance of bites + ammo
                ["zombieScreamer"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall", MinCount = 2, MaxCount = 6, Chance = 0.30f },
                    new LootEntry { Item = "drugFortBites",        MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "drugPainkillers",      MinCount = 1, MaxCount = 1, Chance = 0.15f },
                },
                // Lumberjack — wood + axe-related
                ["zombieLumberjack"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceWood",         MinCount = 4, MaxCount = 10, Chance = 0.60f },
                    new LootEntry { Item = "drinkJarBeer",         MinCount = 1, MaxCount = 2,  Chance = 0.40f },
                    new LootEntry { Item = "foodCharredMeat",      MinCount = 1, MaxCount = 2,  Chance = 0.35f },
                    new LootEntry { Item = "resourceDuctTape",     MinCount = 1, MaxCount = 1,  Chance = 0.10f },
                    new LootEntry { Item = "oldCash",              MinCount = 1, MaxCount = 4,  Chance = 0.30f },
                },
                // Janitor — cleaning chems
                ["zombieJanitor"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceOil",          MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "resourceGlue",         MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "resourceMetalPipe",    MinCount = 1, MaxCount = 2, Chance = 0.25f },
                    new LootEntry { Item = "resourceCloth",        MinCount = 1, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBoiledWater",  MinCount = 1, MaxCount = 1, Chance = 0.30f },
                },
                // Biker — booze + leather
                ["zombieBiker"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceLeather",      MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "drinkJarBeer",         MinCount = 1, MaxCount = 2, Chance = 0.55f },
                    new LootEntry { Item = "ammo9mmBulletBall",   MinCount = 2, MaxCount = 8, Chance = 0.30f },
                    new LootEntry { Item = "foodCharredMeat",      MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",              MinCount = 1, MaxCount = 5, Chance = 0.35f },
                },
                // Soldier — military rations + ammo
                ["zombieSoldier"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall",  MinCount = 4, MaxCount = 12, Chance = 0.50f },
                    new LootEntry { Item = "ammo9mmBulletBall",   MinCount = 4, MaxCount = 10, Chance = 0.40f },
                    new LootEntry { Item = "foodCanLamb",          MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drugPainkillers",      MinCount = 1, MaxCount = 1,  Chance = 0.20f },
                },
                // Mutated — extra everything (slightly tougher = better drops)
                ["zombieMutated"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron",    MinCount = 2, MaxCount = 6, Chance = 0.50f },
                    new LootEntry { Item = "resourceCloth",        MinCount = 2, MaxCount = 4, Chance = 0.45f },
                    new LootEntry { Item = "drugFortBites",        MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "foodCharredMeat",      MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",              MinCount = 1, MaxCount = 5, Chance = 0.30f },
                },
            },
        };

        // ---- VIP (donor tier) ----------------------------------------
        // Basic items at higher rates + better food/drinks/medical, occasional
        // book or schematic. Should feel noticeably better than basic.
        private static QualityTable MakeVipTable() => new QualityTable
        {
            Default = new List<LootEntry>
            {
                new LootEntry { Item = "foodCanSham",        MinCount = 1, MaxCount = 1, Chance = 0.45f },
                new LootEntry { Item = "foodCanLamb",        MinCount = 1, MaxCount = 1, Chance = 0.30f },
                new LootEntry { Item = "drinkJarBoiledWater",MinCount = 1, MaxCount = 2, Chance = 0.50f },
                new LootEntry { Item = "drinkJarBeer",       MinCount = 1, MaxCount = 2, Chance = 0.40f },
                new LootEntry { Item = "resourceBone",       MinCount = 2, MaxCount = 4, Chance = 0.50f },
                new LootEntry { Item = "resourceCloth",      MinCount = 1, MaxCount = 4, Chance = 0.45f },
                new LootEntry { Item = "resourceScrapIron",  MinCount = 1, MaxCount = 5, Chance = 0.35f },
                new LootEntry { Item = "medicalBandage",     MinCount = 1, MaxCount = 2, Chance = 0.40f },
                new LootEntry { Item = "drugPainkillers",    MinCount = 1, MaxCount = 1, Chance = 0.15f },
                new LootEntry { Item = "oldCash",            MinCount = 3, MaxCount = 12, Chance = 0.55f },
                new LootEntry { Item = "foodRottingFlesh",   MinCount = 1, MaxCount = 1, Chance = 0.25f },
            },
            ByClass = new Dictionary<string, List<LootEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["zombieNurse"] = new List<LootEntry>
                {
                    new LootEntry { Item = "medicalBandage",         MinCount = 2, MaxCount = 4, Chance = 0.85f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "drugAntibiotics",        MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "drugHerbalAntibiotics",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugPainkillers",        MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "medicalAloeCream",       MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "medicalSplint",          MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "drinkJarBoiledWater",    MinCount = 1, MaxCount = 1, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",                MinCount = 3, MaxCount = 10,Chance = 0.45f },
                },
                ["zombieFatCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",     MinCount = 10, MaxCount = 25, Chance = 0.75f },
                    new LootEntry { Item = "ammoShotgunShell",       MinCount = 3, MaxCount = 8,   Chance = 0.45f },
                    new LootEntry { Item = "gunHandgunT1Pistol",     MinCount = 1, MaxCount = 1,   Chance = 0.12f },
                    new LootEntry { Item = "drinkJarBeer",           MinCount = 1, MaxCount = 2,   Chance = 0.40f },
                    new LootEntry { Item = "medicalBandage",         MinCount = 1, MaxCount = 3,   Chance = 0.50f },
                    new LootEntry { Item = "oldCash",                MinCount = 3, MaxCount = 12,  Chance = 0.55f },
                    new LootEntry { Item = "resourceDuctTape",       MinCount = 1, MaxCount = 2,   Chance = 0.20f },
                },
                ["zombieCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",     MinCount = 12, MaxCount = 30, Chance = 0.80f },
                    new LootEntry { Item = "ammoShotgunShell",       MinCount = 3, MaxCount = 8,   Chance = 0.50f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 1,   Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 3, MaxCount = 12,  Chance = 0.55f },
                },
                ["zombieBusinessMan"] = new List<LootEntry>
                {
                    new LootEntry { Item = "oldCash",                       MinCount = 10, MaxCount = 35, Chance = 1.00f },
                    new LootEntry { Item = "resourcePaper",                 MinCount = 3,  MaxCount = 8,  Chance = 0.70f },
                    new LootEntry { Item = "bookArtOfMiningCoffee",         MinCount = 1,  MaxCount = 1,  Chance = 0.20f },
                    new LootEntry { Item = "resourceDuctTape",              MinCount = 1,  MaxCount = 2,  Chance = 0.25f },
                    new LootEntry { Item = "drinkJarBoiledWater",           MinCount = 1,  MaxCount = 2,  Chance = 0.40f },
                    new LootEntry { Item = "foodCanMiso",                   MinCount = 1,  MaxCount = 2,  Chance = 0.35f },
                },
                ["zombieFatHawaiian"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanChili",     MinCount = 2, MaxCount = 4, Chance = 0.65f },
                    new LootEntry { Item = "foodCanTuna",      MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "foodShamChowder",  MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",     MinCount = 2, MaxCount = 3, Chance = 0.60f },
                    new LootEntry { Item = "drinkJarYuccaJuice", MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodCharredMeat",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",          MinCount = 2, MaxCount = 8, Chance = 0.40f },
                },
                ["zombieFemaleFat"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",      MinCount = 2, MaxCount = 3, Chance = 0.60f },
                    new LootEntry { Item = "foodPumpkinPie",   MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodCornBread",    MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",     MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "resourceCloth",    MinCount = 3, MaxCount = 5, Chance = 0.50f },
                    new LootEntry { Item = "oldCash",          MinCount = 2, MaxCount = 8, Chance = 0.40f },
                },
                ["zombieBoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron",     MinCount = 4, MaxCount = 10, Chance = 0.75f },
                    new LootEntry { Item = "resourceWood",          MinCount = 3, MaxCount = 8,  Chance = 0.55f },
                    new LootEntry { Item = "resourceDuctTape",      MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "resourceSewingKit",     MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "resourceMetalPipe",     MinCount = 1, MaxCount = 2,  Chance = 0.20f },
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 1, MaxCount = 2,  Chance = 0.45f },
                    new LootEntry { Item = "foodMeatStew",          MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 7,  Chance = 0.40f },
                },
                ["zombieJoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron",     MinCount = 3, MaxCount = 8, Chance = 0.60f },
                    new LootEntry { Item = "resourceMetalPipe",     MinCount = 1, MaxCount = 3, Chance = 0.30f },
                    new LootEntry { Item = "resourceLockPick",      MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "foodCanLamb",           MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 7, Chance = 0.40f },
                },
                ["zombieMoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",         MinCount = 3, MaxCount = 6, Chance = 0.55f },
                    new LootEntry { Item = "resourceLeather",       MinCount = 2, MaxCount = 4, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodCanSoup",           MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "resourceSewingKit",     MinCount = 1, MaxCount = 1, Chance = 0.25f },
                },
                ["zombieSteve"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron",     MinCount = 2, MaxCount = 5, Chance = 0.50f },
                    new LootEntry { Item = "drinkJarRiverWater",    MinCount = 2, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "foodCharredMeat",       MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 1, Chance = 0.25f },
                },
                ["zombieArlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",           MinCount = 2, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "foodPumpkinPie",        MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBoiledWater",   MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "resourceBone",          MinCount = 3, MaxCount = 6, Chance = 0.55f },
                    new LootEntry { Item = "resourceSewingKit",     MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "medicalBandage",        MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",               MinCount = 3, MaxCount = 9, Chance = 0.45f },
                },
                ["zombieMarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodBakedPotato",       MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodCornBread",         MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBoiledWater",   MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "resourceCloth",         MinCount = 2, MaxCount = 4, Chance = 0.50f },
                    new LootEntry { Item = "medicalBandage",        MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 8, Chance = 0.40f },
                },
                ["zombieDarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 1, MaxCount = 2, Chance = 0.55f },
                    new LootEntry { Item = "foodCanChili",          MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugPainkillers",       MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "resourceCloth",         MinCount = 2, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",               MinCount = 3, MaxCount = 10, Chance = 0.50f },
                },
                ["zombieSpider"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",         MinCount = 2, MaxCount = 5, Chance = 0.60f },
                    new LootEntry { Item = "resourceBone",          MinCount = 2, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 6, Chance = 0.35f },
                    new LootEntry { Item = "drugFortBites",         MinCount = 1, MaxCount = 1, Chance = 0.10f },
                },
                ["zombieScreamer"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall",   MinCount = 5, MaxCount = 14, Chance = 0.55f },
                    new LootEntry { Item = "drugFortBites",         MinCount = 1, MaxCount = 2,  Chance = 0.35f },
                    new LootEntry { Item = "drugPainkillers",       MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "drugRecog",             MinCount = 1, MaxCount = 1,  Chance = 0.15f },
                },
                ["zombieLumberjack"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceWood",          MinCount = 8, MaxCount = 18, Chance = 0.75f },
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 2, MaxCount = 3,  Chance = 0.55f },
                    new LootEntry { Item = "foodCharredMeat",       MinCount = 1, MaxCount = 3,  Chance = 0.45f },
                    new LootEntry { Item = "foodHoboStew",          MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "resourceDuctTape",      MinCount = 1, MaxCount = 2,  Chance = 0.20f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 6,  Chance = 0.35f },
                },
                ["zombieJanitor"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceOil",           MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "resourceGlue",          MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "resourceAcid",          MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "resourceMetalPipe",     MinCount = 2, MaxCount = 4, Chance = 0.40f },
                    new LootEntry { Item = "resourceCloth",         MinCount = 2, MaxCount = 4, Chance = 0.50f },
                    new LootEntry { Item = "resourceLeather",       MinCount = 1, MaxCount = 2, Chance = 0.25f },
                    new LootEntry { Item = "oldCash",               MinCount = 2, MaxCount = 6, Chance = 0.40f },
                },
                ["zombieBiker"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceLeather",       MinCount = 2, MaxCount = 5, Chance = 0.60f },
                    new LootEntry { Item = "drinkJarBeer",          MinCount = 2, MaxCount = 3, Chance = 0.65f },
                    new LootEntry { Item = "ammo9mmBulletBall",    MinCount = 4, MaxCount = 12, Chance = 0.45f },
                    new LootEntry { Item = "foodCharredMeat",      MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugSteroids",         MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "oldCash",              MinCount = 2, MaxCount = 9, Chance = 0.45f },
                },
                ["zombieSoldier"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall",  MinCount = 8, MaxCount = 20, Chance = 0.70f },
                    new LootEntry { Item = "ammo9mmBulletBall",    MinCount = 6, MaxCount = 16, Chance = 0.55f },
                    new LootEntry { Item = "ammoShotgunShell",     MinCount = 3, MaxCount = 8,  Chance = 0.30f },
                    new LootEntry { Item = "foodCanLamb",          MinCount = 1, MaxCount = 2,  Chance = 0.40f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "drugPainkillers",      MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "gunHandgunT1Pistol",   MinCount = 1, MaxCount = 1,  Chance = 0.10f },
                },
                ["zombieMutated"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceScrapIron",    MinCount = 4, MaxCount = 10, Chance = 0.65f },
                    new LootEntry { Item = "resourceCloth",        MinCount = 3, MaxCount = 6,  Chance = 0.55f },
                    new LootEntry { Item = "drugFortBites",        MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "foodMeatStew",         MinCount = 1, MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "oldCash",              MinCount = 2, MaxCount = 8,  Chance = 0.40f },
                },
            },
        };

        // ---- MASTER (top tier) ---------------------------------------
        // Substantially better than VIP: occasional weapons, advanced
        // medical, top materials (forged steel, mech/electric parts, coil),
        // schematics + books. Same ~18 zombie classes as Basic/VIP. The
        // "rotting flesh penalty" is dropped to 0.20 / max 1 — at this
        // tier the bag should mostly contain useful gear.
        private static QualityTable MakeMasterTable() => new QualityTable
        {
            Default = new List<LootEntry>
            {
                new LootEntry { Item = "foodCanSham",           MinCount = 1, MaxCount = 2, Chance = 0.55f },
                new LootEntry { Item = "foodCanLamb",           MinCount = 1, MaxCount = 2, Chance = 0.40f },
                new LootEntry { Item = "drinkJarBoiledWater",   MinCount = 1, MaxCount = 3, Chance = 0.55f },
                new LootEntry { Item = "drinkJarBeer",          MinCount = 1, MaxCount = 2, Chance = 0.45f },
                new LootEntry { Item = "resourceForgedIron",    MinCount = 2, MaxCount = 6, Chance = 0.45f },
                new LootEntry { Item = "resourceCloth",         MinCount = 2, MaxCount = 5, Chance = 0.50f },
                new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 2, Chance = 0.30f },
                new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 2, Chance = 0.40f },
                new LootEntry { Item = "drugPainkillers",       MinCount = 1, MaxCount = 2, Chance = 0.30f },
                new LootEntry { Item = "oldCash",               MinCount = 8, MaxCount = 25, Chance = 0.70f },
                new LootEntry { Item = "foodRottingFlesh",      MinCount = 1, MaxCount = 1, Chance = 0.20f },
            },
            ByClass = new Dictionary<string, List<LootEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                // Medical staff — top-tier medical, occasional first-aid kit
                ["zombieNurse"] = new List<LootEntry>
                {
                    new LootEntry { Item = "medicalBandage",         MinCount = 3, MaxCount = 6, Chance = 1.00f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 3, Chance = 0.75f },
                    new LootEntry { Item = "medicalFirstAidKit",     MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "drugAntibiotics",        MinCount = 1, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "drugHerbalAntibiotics",  MinCount = 1, MaxCount = 2, Chance = 0.55f },
                    new LootEntry { Item = "drugPainkillers",        MinCount = 1, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "medicalAloeCream",       MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "medicalSplint",          MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drugRecog",              MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "drinkJarBoiledWater",    MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "oldCash",                MinCount = 5, MaxCount = 18, Chance = 0.55f },
                },
                ["zombieFatCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",      MinCount = 18, MaxCount = 40, Chance = 0.90f },
                    new LootEntry { Item = "ammoShotgunShell",       MinCount = 5, MaxCount = 14, Chance = 0.60f },
                    new LootEntry { Item = "gunHandgunT1Pistol",     MinCount = 1, MaxCount = 1,  Chance = 0.22f },
                    new LootEntry { Item = "gunShotgunT0PipeShotgun", MinCount = 1, MaxCount = 1, Chance = 0.10f },
                    new LootEntry { Item = "drinkJarBeer",           MinCount = 1, MaxCount = 2,  Chance = 0.45f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 2,  Chance = 0.45f },
                    new LootEntry { Item = "drugPainkillers",        MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 5, MaxCount = 20, Chance = 0.65f },
                    new LootEntry { Item = "resourceDuctTape",       MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                },
                ["zombieCop"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo9mmBulletBall",      MinCount = 20, MaxCount = 45, Chance = 0.95f },
                    new LootEntry { Item = "ammoShotgunShell",       MinCount = 5, MaxCount = 14, Chance = 0.65f },
                    new LootEntry { Item = "gunHandgunT1Pistol",     MinCount = 1, MaxCount = 1,  Chance = 0.20f },
                    new LootEntry { Item = "medicalFirstAidBandage", MinCount = 1, MaxCount = 2,  Chance = 0.45f },
                    new LootEntry { Item = "drugPainkillers",        MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "resourceDuctTape",       MinCount = 1, MaxCount = 2,  Chance = 0.30f },
                    new LootEntry { Item = "oldCash",                MinCount = 5, MaxCount = 20, Chance = 0.65f },
                },
                // Office workers — wallet-fat, paper, books, schematics
                ["zombieBusinessMan"] = new List<LootEntry>
                {
                    new LootEntry { Item = "oldCash",                       MinCount = 20, MaxCount = 60, Chance = 1.00f },
                    new LootEntry { Item = "resourcePaper",                 MinCount = 5,  MaxCount = 12, Chance = 0.85f },
                    new LootEntry { Item = "bookArtOfMiningCoffee",         MinCount = 1,  MaxCount = 1,  Chance = 0.30f },
                    new LootEntry { Item = "schematicMaster",               MinCount = 1,  MaxCount = 1,  Chance = 0.18f },
                    new LootEntry { Item = "resourceDuctTape",              MinCount = 1,  MaxCount = 3,  Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBoiledWater",           MinCount = 1,  MaxCount = 2,  Chance = 0.45f },
                    new LootEntry { Item = "foodCanMiso",                   MinCount = 1,  MaxCount = 2,  Chance = 0.40f },
                    new LootEntry { Item = "drugPainkillers",               MinCount = 1,  MaxCount = 1,  Chance = 0.20f },
                },
                // Tropical drunks — feast + booze
                ["zombieFatHawaiian"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanChili",       MinCount = 2, MaxCount = 5, Chance = 0.80f },
                    new LootEntry { Item = "foodCanTuna",        MinCount = 1, MaxCount = 3, Chance = 0.65f },
                    new LootEntry { Item = "foodShamChowder",    MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "foodMeatStew",       MinCount = 1, MaxCount = 1, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBeer",       MinCount = 2, MaxCount = 4, Chance = 0.70f },
                    new LootEntry { Item = "drinkJarYuccaJuice", MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "foodCharredMeat",    MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "oldCash",            MinCount = 4, MaxCount = 12, Chance = 0.50f },
                },
                ["zombieFemaleFat"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",        MinCount = 2, MaxCount = 4, Chance = 0.70f },
                    new LootEntry { Item = "foodPumpkinPie",     MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "foodCornBread",      MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBeer",       MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "resourceCloth",      MinCount = 4, MaxCount = 7, Chance = 0.60f },
                    new LootEntry { Item = "resourceSewingKit",  MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "oldCash",            MinCount = 4, MaxCount = 12, Chance = 0.50f },
                },
                // Working men — top materials, mechanical parts, schematics
                ["zombieBoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceForgedIron",      MinCount = 4, MaxCount = 12, Chance = 0.80f },
                    new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "resourceWood",            MinCount = 5, MaxCount = 12, Chance = 0.65f },
                    new LootEntry { Item = "resourceDuctTape",        MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "resourceSewingKit",       MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "resourceMetalPipe",       MinCount = 1, MaxCount = 3, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 1, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "foodMeatStew",            MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 10, Chance = 0.50f },
                },
                ["zombieJoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceForgedIron",      MinCount = 3, MaxCount = 10, Chance = 0.70f },
                    new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "resourceMetalPipe",       MinCount = 2, MaxCount = 4, Chance = 0.45f },
                    new LootEntry { Item = "resourceLockPick",        MinCount = 1, MaxCount = 3, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 1, MaxCount = 2, Chance = 0.50f },
                    new LootEntry { Item = "foodCanLamb",             MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 10, Chance = 0.50f },
                },
                ["zombieMoe"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",           MinCount = 4, MaxCount = 8, Chance = 0.65f },
                    new LootEntry { Item = "resourceLeather",         MinCount = 2, MaxCount = 5, Chance = 0.55f },
                    new LootEntry { Item = "resourceSewingKit",       MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "foodCanSoup",             MinCount = 1, MaxCount = 2, Chance = 0.45f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 9, Chance = 0.45f },
                },
                ["zombieSteve"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceForgedIron",      MinCount = 2, MaxCount = 6, Chance = 0.55f },
                    new LootEntry { Item = "drinkJarBoiledWater",     MinCount = 2, MaxCount = 4, Chance = 0.55f },
                    new LootEntry { Item = "foodCharredMeat",         MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "medicalFirstAidBandage",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugPainkillers",         MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 9, Chance = 0.45f },
                },
                ["zombieArlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodCanSoup",             MinCount = 2, MaxCount = 4, Chance = 0.65f },
                    new LootEntry { Item = "foodPumpkinPie",          MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drinkJarBoiledWater",     MinCount = 1, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "resourceBone",            MinCount = 4, MaxCount = 8, Chance = 0.60f },
                    new LootEntry { Item = "resourceSewingKit",       MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "medicalFirstAidBandage",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",                 MinCount = 5, MaxCount = 14, Chance = 0.55f },
                },
                ["zombieMarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "foodBakedPotato",         MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "foodCornBread",           MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "foodMeatStew",            MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drinkJarBoiledWater",     MinCount = 1, MaxCount = 3, Chance = 0.55f },
                    new LootEntry { Item = "resourceCloth",           MinCount = 3, MaxCount = 6, Chance = 0.60f },
                    new LootEntry { Item = "medicalFirstAidBandage",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",                 MinCount = 4, MaxCount = 12, Chance = 0.50f },
                },
                ["zombieDarlene"] = new List<LootEntry>
                {
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 2, MaxCount = 3, Chance = 0.65f },
                    new LootEntry { Item = "foodCanChili",            MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "drugPainkillers",         MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "drugRecog",               MinCount = 1, MaxCount = 1, Chance = 0.10f },
                    new LootEntry { Item = "resourceCloth",           MinCount = 2, MaxCount = 4, Chance = 0.45f },
                    new LootEntry { Item = "oldCash",                 MinCount = 5, MaxCount = 16, Chance = 0.60f },
                },
                // Spider — high agility, occasional drug stash
                ["zombieSpider"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceCloth",           MinCount = 3, MaxCount = 7, Chance = 0.70f },
                    new LootEntry { Item = "resourceBone",            MinCount = 2, MaxCount = 4, Chance = 0.50f },
                    new LootEntry { Item = "drugFortBites",           MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "drugSteroids",            MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 10, Chance = 0.45f },
                },
                // Screamer — military-grade ammo + drugs + electrical parts
                ["zombieScreamer"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall",     MinCount = 12, MaxCount = 30, Chance = 0.75f },
                    new LootEntry { Item = "drugFortBites",           MinCount = 1, MaxCount = 2, Chance = 0.55f },
                    new LootEntry { Item = "drugPainkillers",         MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "drugRecog",               MinCount = 1, MaxCount = 1, Chance = 0.30f },
                    new LootEntry { Item = "drugSteroids",            MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "resourceElectricParts",   MinCount = 1, MaxCount = 3, Chance = 0.30f },
                    new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 2, Chance = 0.20f },
                },
                // Lumberjack — heaps of wood, charred meat, occasional pipe MG
                ["zombieLumberjack"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceWood",            MinCount = 12, MaxCount = 25, Chance = 0.85f },
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 2, MaxCount = 4, Chance = 0.65f },
                    new LootEntry { Item = "foodCharredMeat",         MinCount = 2, MaxCount = 4, Chance = 0.55f },
                    new LootEntry { Item = "foodHoboStew",            MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "resourceDuctTape",        MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "gunMGT0PipeMachineGun",   MinCount = 1, MaxCount = 1, Chance = 0.05f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 9, Chance = 0.45f },
                },
                // Janitor — chems + electrical/forge materials
                ["zombieJanitor"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceOil",             MinCount = 2, MaxCount = 4, Chance = 0.60f },
                    new LootEntry { Item = "resourceGlue",            MinCount = 1, MaxCount = 3, Chance = 0.45f },
                    new LootEntry { Item = "resourceAcid",            MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "resourceMetalPipe",       MinCount = 2, MaxCount = 5, Chance = 0.50f },
                    new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 2, Chance = 0.25f },
                    new LootEntry { Item = "resourceCloth",           MinCount = 2, MaxCount = 5, Chance = 0.55f },
                    new LootEntry { Item = "resourceLeather",         MinCount = 1, MaxCount = 3, Chance = 0.35f },
                    new LootEntry { Item = "oldCash",                 MinCount = 3, MaxCount = 9, Chance = 0.50f },
                },
                // Biker — leather, steroids, ammo, occasional pistol
                ["zombieBiker"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceLeather",         MinCount = 3, MaxCount = 7, Chance = 0.75f },
                    new LootEntry { Item = "drinkJarBeer",            MinCount = 2, MaxCount = 4, Chance = 0.75f },
                    new LootEntry { Item = "ammo9mmBulletBall",       MinCount = 8, MaxCount = 18, Chance = 0.55f },
                    new LootEntry { Item = "gunHandgunT1Pistol",      MinCount = 1, MaxCount = 1, Chance = 0.10f },
                    new LootEntry { Item = "foodCharredMeat",         MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "drugSteroids",            MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "drugFortBites",           MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "oldCash",                 MinCount = 4, MaxCount = 14, Chance = 0.55f },
                },
                // Soldier — rifle ammo, advanced medical, possible weapon
                ["zombieSoldier"] = new List<LootEntry>
                {
                    new LootEntry { Item = "ammo762mmBulletBall",     MinCount = 14, MaxCount = 32, Chance = 0.85f },
                    new LootEntry { Item = "ammo9mmBulletBall",       MinCount = 8, MaxCount = 20, Chance = 0.65f },
                    new LootEntry { Item = "ammoShotgunShell",        MinCount = 4, MaxCount = 10, Chance = 0.40f },
                    new LootEntry { Item = "foodCanLamb",             MinCount = 1, MaxCount = 3, Chance = 0.50f },
                    new LootEntry { Item = "medicalFirstAidBandage",  MinCount = 1, MaxCount = 3, Chance = 0.60f },
                    new LootEntry { Item = "medicalFirstAidKit",      MinCount = 1, MaxCount = 1, Chance = 0.15f },
                    new LootEntry { Item = "drugAntibiotics",         MinCount = 1, MaxCount = 2, Chance = 0.35f },
                    new LootEntry { Item = "drugPainkillers",         MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugSteroids",            MinCount = 1, MaxCount = 1, Chance = 0.20f },
                    new LootEntry { Item = "gunHandgunT1Pistol",      MinCount = 1, MaxCount = 1, Chance = 0.18f },
                    new LootEntry { Item = "resourceDuctTape",        MinCount = 1, MaxCount = 2, Chance = 0.30f },
                },
                // Mutated — tanky → top materials + advanced drug + medical
                ["zombieMutated"] = new List<LootEntry>
                {
                    new LootEntry { Item = "resourceForgedIron",      MinCount = 5, MaxCount = 12, Chance = 0.75f },
                    new LootEntry { Item = "resourceForgedSteel",     MinCount = 1, MaxCount = 2, Chance = 0.20f },
                    new LootEntry { Item = "resourceCloth",           MinCount = 4, MaxCount = 7, Chance = 0.65f },
                    new LootEntry { Item = "resourceMechanicalParts", MinCount = 1, MaxCount = 2, Chance = 0.30f },
                    new LootEntry { Item = "drugFortBites",           MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "drugSteroids",            MinCount = 1, MaxCount = 1, Chance = 0.25f },
                    new LootEntry { Item = "foodMeatStew",            MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "medicalFirstAidBandage",  MinCount = 1, MaxCount = 2, Chance = 0.40f },
                    new LootEntry { Item = "oldCash",                 MinCount = 4, MaxCount = 12, Chance = 0.50f },
                },
            },
        };
    }

    // ------------------------------------------------------------ runtime

    private Config _cfg;
    private int _bagsSpawned;
    private int _kills;
    private int _killsAttributed;
    private int _killsNight;
    private int _killsDeduped;

    /// <summary>Entity ids of zombies we've already dropped a bag for in
    /// this session. The engine's <c>Kill(DamageResponse)</c> isn't guarded
    /// against repeat calls — every additional damage hit on a corpse
    /// (continuing to swing at a body) re-fires our <c>OnEntityKill</c>
    /// hook and would spawn another bag without this dedupe.
    ///
    /// Cleared periodically (see <c>_dedupeCleaner</c>) to avoid unbounded
    /// growth — corpses despawn after ~2 minutes anyway, so a 5-minute TTL
    /// is plenty of headroom.</summary>
    private readonly HashSet<int> _alreadyDroppedFor = new HashSet<int>();
    private readonly object _dedupeLock = new object();
    private TimerHandle _dedupeCleaner;

    public override void OnLoad()
    {
        _cfg = StyxCore.Configs.Load<Config>(this);

        // Register every tier perm with PermEditor so admins can toggle them
        // per group from the UI without editing config files.
        foreach (var t in _cfg.DropTiers)
        {
            if (string.IsNullOrEmpty(t.Perm)) continue;
            StyxCore.Perms.RegisterKnown(t.Perm,
                string.Format("Zombie loot drops — {0:P0} chance, '{1}' table",
                    t.DropChance, t.Quality ?? "(none)"),
                Name);
        }

        StyxCore.Commands.Register("zloot", "ZombieLoot status — /zloot stats", (ctx, args) =>
        {
            ctx.Reply(string.Format(
                "[ccddff]ZombieLoot v0.4:[-] enabled={0} kills={1} attributed={2} night={3} bags={4} deduped={5} cap={6} life={7}s bm-suppress={8}",
                _cfg.Enabled, _kills, _killsAttributed, _killsNight, _bagsSpawned, _killsDeduped,
                _cfg.MaxItemsPerBag, _cfg.BagLifetimeSeconds, _cfg.SuppressOnBloodMoon));
            ctx.Reply(string.Format("[ccddff]Night boost:[-] enabled={0} window={1:00}:00–{2:00}:00 currentlyNight={3}",
                _cfg.NightBoostEnabled, _cfg.NightStartHour, _cfg.NightEndHour, IsNightNow()));
            ctx.Reply("[ccddff]Drop tiers (first match wins):[-]");
            foreach (var t in _cfg.DropTiers)
                ctx.Reply(string.Format("  {0} -> {1:P0} drop, table '{2}'",
                    t.Perm, t.DropChance, t.Quality));
            ctx.Reply(string.Format("[ccddff]Quality tables:[-] {0} (order: {1})",
                _cfg.QualityTiers.Count, string.Join(" → ", _cfg.TierOrder)));
        });

        // Periodic flush of the dedupe set — keeps memory bounded on long
        // sessions. 5-minute window is well past the corpse despawn time so
        // it's safe to clear; if a player re-engages a corpse 5+ minutes
        // later we'd just give them a duplicate bag (acceptable edge case).
        _dedupeCleaner = Scheduler.Every(300.0, () =>
        {
            lock (_dedupeLock) _alreadyDroppedFor.Clear();
        }, "ZombieLoot.dedupe-cleaner");

        Log.Out("[ZombieLoot] Loaded v0.4.0 — {0} drop tier(s), {1} quality table(s), night-boost={2}, life={3}s",
            _cfg.DropTiers.Count, _cfg.QualityTiers.Count,
            _cfg.NightBoostEnabled, _cfg.BagLifetimeSeconds);
    }

    public override void OnUnload()
    {
        _dedupeCleaner?.Destroy();
        _dedupeCleaner = null;
        lock (_dedupeLock) _alreadyDroppedFor.Clear();
        StyxCore.Perms.UnregisterKnownByOwner(Name);
    }

    /// <summary>OnEntityKill fires after Kill(DamageResponse) — gives us the
    /// attacker via response.Source.getEntityId(). Filter to zombies killed
    /// by players, look up the killer's tier, roll + spawn.</summary>
    void OnEntityKill(EntityAlive victim, DamageResponse response)
    {
        if (victim == null) return;
        if (!(victim is EntityZombie zombie)) return;
        if (!_cfg.Enabled) return;
        _kills++;

        // Dedupe: the engine's Kill(DamageResponse) re-fires every time damage
        // hits an already-dead corpse (player keeps swinging). Without this
        // gate we'd spawn a new bag on every additional hit. Track victim
        // entityId in a session-scoped HashSet — first add wins, subsequent
        // hits short-circuit out before any tier roll / spawn work.
        int vid = victim.entityId;
        if (vid > 0)
        {
            lock (_dedupeLock)
            {
                if (!_alreadyDroppedFor.Add(vid))
                {
                    _killsDeduped++;
                    return;
                }
            }
        }

        if (_cfg.SuppressOnBloodMoon && (StyxCore.World?.IsBloodMoon ?? false)) return;

        // Find the killer player (if any). Non-player kills (zombie-on-zombie,
        // fall, fire, traps) get no attribution → no drop.
        var killer = ResolveKillerPlayer(response);
        if (killer == null) return;
        _killsAttributed++;

        var pid = StyxCore.Player.PlatformIdOf(killer);
        if (string.IsNullOrEmpty(pid)) return;

        // Pick the killer's tier — first perm in DropTiers they have wins.
        var tier = ResolveTierFor(pid);
        if (tier == null) return;

        // Drop-chance gate (always rolled against the killer's own tier —
        // night boost only affects loot quality, not drop rate).
        if (UnityEngine.Random.value > tier.DropChance) return;

        // Quality selection — start from killer's tier; if night, bump up one.
        string quality = tier.Quality;
        if (_cfg.NightBoostEnabled && IsNightNow())
        {
            _killsNight++;
            quality = NextTierUp(quality) ?? quality;
        }

        if (!_cfg.QualityTiers.TryGetValue(quality ?? "", out var qtable) || qtable == null)
        {
            Log.Warning("[ZombieLoot] Quality table '" + quality + "' missing — no drop");
            return;
        }

        var stacks = RollDrops(zombie, qtable);
        if (stacks.Count == 0) return;

        var pos = zombie.GetPosition() + Vector3.up * 0.5f;
        if (SpawnLootBag(pos, stacks)) _bagsSpawned++;
    }

    // ------------------------------------------------------------ tier resolution

    /// <summary>Walk DropTiers top-down — first perm the player has wins.
    /// Returns null if no tier matches (player gets no drops).</summary>
    private TierEntry ResolveTierFor(string pid)
    {
        foreach (var t in _cfg.DropTiers)
        {
            if (string.IsNullOrEmpty(t?.Perm)) continue;
            if (StyxCore.Perms.HasPermission(pid, t.Perm)) return t;
        }
        return null;
    }

    /// <summary>Resolve the next tier above <paramref name="current"/> using
    /// TierOrder. Returns null if current is already top, or unknown.</summary>
    private string NextTierUp(string current)
    {
        if (string.IsNullOrEmpty(current)) return null;
        var order = _cfg.TierOrder;
        if (order == null) return null;
        int idx = order.FindIndex(s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx >= order.Count - 1) return null;
        return order[idx + 1];
    }

    /// <summary>Pull the killer EntityPlayer out of a DamageResponse. Walks
    /// response.Source.getEntityId(), then maps via the world entity table.
    /// Returns null for non-player kills (zombies, environment, etc).</summary>
    private EntityPlayer ResolveKillerPlayer(DamageResponse response)
    {
        try
        {
            var src = response.Source;
            if (src == null) return null;
            int eid = src.getEntityId();
            if (eid <= 0) return null;
            var world = GameManager.Instance?.World;
            if (world == null) return null;
            var ent = world.GetEntity(eid);
            return ent as EntityPlayer;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ time

    /// <summary>True if the current in-game hour is inside the night window.
    /// Window wraps midnight if End ≤ Start (the typical 22→06 case).</summary>
    private bool IsNightNow()
    {
        try
        {
            ulong t = StyxCore.World?.WorldTimeRaw ?? 0;
            // 1000 ticks per in-game hour, 24h per day.
            int hour = (int)((t / 1000UL) % 24UL);
            int s = _cfg.NightStartHour, e = _cfg.NightEndHour;
            if (s == e) return false;
            return s < e
                ? (hour >= s && hour < e)               // non-wrapping
                : (hour >= s || hour < e);              // wraps midnight
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ rolling

    /// <summary>Pick the right table inside a quality (per-class first, then
    /// default) and roll independent chances for each entry. Caps at
    /// <see cref="Config.MaxItemsPerBag"/> stacks.</summary>
    private List<ItemStack> RollDrops(EntityZombie zombie, QualityTable qtable)
    {
        var stacks = new List<ItemStack>();
        if (qtable == null) return stacks;

        List<LootEntry> table = qtable.Default;
        try
        {
            string className = zombie?.EntityClass?.entityClassName;
            if (!string.IsNullOrEmpty(className) &&
                qtable.ByClass != null &&
                qtable.ByClass.TryGetValue(className, out var classTable) &&
                classTable != null && classTable.Count > 0)
            {
                table = classTable;
            }
        }
        catch { /* fall through to default */ }

        if (table == null) return stacks;

        foreach (var d in table)
        {
            if (string.IsNullOrEmpty(d.Item)) continue;
            if (UnityEngine.Random.value > d.Chance) continue;
            var ic = ItemClass.GetItemClass(d.Item, _caseInsensitive: true);
            if (ic == null)
            {
                Log.Warning("[ZombieLoot] Unknown item '" + d.Item + "' in drop table — skipping");
                continue;
            }
            int count = UnityEngine.Random.Range(d.MinCount, Math.Max(d.MinCount, d.MaxCount) + 1);
            if (count <= 0) continue;
            stacks.Add(new ItemStack(new ItemValue(ic.Id), count));
            if (stacks.Count >= _cfg.MaxItemsPerBag) break;
        }
        return stacks;
    }

    // ------------------------------------------------------------ bag spawn

    /// <summary>
    /// Spawn an EntityBackpack at <paramref name="pos"/> containing
    /// <paramref name="items"/>. Schedules a despawn after BagLifetimeSeconds
    /// (vs the engine default of ~21 days for player death bags).
    /// </summary>
    private bool SpawnLootBag(Vector3 pos, List<ItemStack> items)
    {
        var gm = GameManager.Instance;
        if (gm == null) return false;
        try
        {
            var entity = EntityFactory.CreateEntity("Backpack".GetHashCode(), pos) as EntityBackpack;
            if (entity == null)
            {
                Log.Warning("[ZombieLoot] Couldn't create Backpack entity at " + pos);
                return false;
            }

            var loot = new TileEntityLootContainer((Chunk)null);
            // Keep lootListName pointing at the backpack's default sizing list.
            // Two reasons:
            //   1. Client-side XUiC_LootWindowGroup.OnOpen reads openTime via
            //      LootContainer.GetLootContainer(te.lootListName).openTime with
            //      NO null-guard (Assembly-CSharp:XUiC_LootWindowGroup.cs:157).
            //      Empty/unknown name → returns null → NPE crashes the client UI
            //      and the bag can't be opened.
            //   2. Engine regen is gated by bTouched, not by lootListName —
            //      LootManager.LootContainerOpened early-returns when bTouched is
            //      true. SetEmpty() below sets bTouched = true, so the engine
            //      won't auto-fill on top of our rolled items.
            string sizingList = entity.GetLootList();
            loot.lootListName = sizingList;
            loot.SetUserAccessing(_bUserAccessing: true);
            loot.SetEmpty();  // clears items AND sets bTouched=true → no auto-fill

            var lc = LootContainer.GetLootContainer(sizingList);
            if (lc != null) loot.SetContainerSize(lc.size);

            int added = 0;
            foreach (var s in items)
            {
                if (s == null || s.IsEmpty()) continue;
                loot.AddItem(s.Clone());
                added++;
            }
            if (added == 0) return false;

            // Re-assert bTouched in case any intermediate call (SetContainerSize,
            // AddItem's internal bookkeeping) toggled it. bTouched=true is the
            // engine's "don't regenerate me" flag — essential here so
            // LootManager.LootContainerOpened doesn't stamp vanilla loot on top.
            loot.bTouched = true;

            loot.SetUserAccessing(_bUserAccessing: false);
            loot.SetModified();
            entity.RefPlayerId = -1;  // free-standing — no owning player

            // Best-effort: try to set the engine-side lifetime field too,
            // in case it exists and the engine respects it. If the field
            // name differs across engine versions, this catches silently
            // and we rely on the scheduled despawn below.
            try
            {
                var lifetimeField = typeof(EntityBackpack).GetField("lifetime",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (lifetimeField != null && _cfg.BagLifetimeSeconds > 0)
                    lifetimeField.SetValue(entity, (float)_cfg.BagLifetimeSeconds);
            }
            catch { /* engine-side lifetime field absent or different name — fine */ }

            var ecd = new EntityCreationData(entity)
            {
                id = -1,
                lootContainer = loot,
            };
            entity.OnEntityUnload();
            gm.RequestToSpawnEntityServer(ecd);

            // Belt-and-braces despawn schedule — the entity gets a real id
            // assigned during spawn, so we can't capture it here. Instead
            // schedule a scan that finds + removes our bag after lifetime
            // expires by matching position + entity-class. Cheap.
            if (_cfg.BagLifetimeSeconds > 0)
                ScheduleBagDespawn(pos, _cfg.BagLifetimeSeconds);

            return true;
        }
        catch (Exception e)
        {
            Log.Warning("[ZombieLoot] SpawnLootBag failed: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Schedule a one-shot scan that despawns any EntityBackpack entities
    /// whose position is within 1.5m of the original drop point. Doesn't
    /// despawn player death bags (those have RefPlayerId > 0; ours has -1).
    /// </summary>
    private void ScheduleBagDespawn(Vector3 pos, int seconds)
    {
        Scheduler.Once(seconds, () =>
        {
            try
            {
                var world = GameManager.Instance?.World;
                if (world == null) return;

                var nearby = StyxCore.World.EntitiesInRadius(pos, 1.5f);
                foreach (var e in nearby)
                {
                    if (!(e is EntityBackpack bag)) continue;
                    if (bag.RefPlayerId > 0) continue;  // player death bag — leave alone
                    world.RemoveEntity(bag.entityId, EnumRemoveEntityReason.Despawned);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[ZombieLoot] Scheduled despawn failed: " + e.Message);
            }
        }, name: "ZombieLoot.despawn");
    }
}
