# 06 — ZombieLoot

Drops a lootable bag when a zombie dies, with perm-tiered drop chance + per-zombie-class loot tables, night-time tier boost, and blood-moon suppression.

## Commands

| Command | Perm | What |
|---|---|---|
| `/zloot` | (open) | Plugin status + lifetime stats |
| `/zloot stats` | (open) | Lifetime drop counters |

No player-facing commands beyond status — drops happen automatically on zombie kill.

## Permissions

| Perm | Defaults | What |
|---|---|---|
| `styx.zloot.master` | 100% drop, "master" quality | Top tier — admin-grade loot |
| `styx.zloot.vip` | 85% drop, "vip" quality | Mid tier — donor-grade loot |
| `styx.zloot.use` | 60% drop, "basic" quality | Basic — granted to default group |

Players with NO matching tier perm → **zero drops**. Default-group players need at least `styx.zloot.use` to get bags. First-match-wins on tier evaluation.

## Config — `configs/ZombieLoot.json`

The actual schema (from `ZombieLoot.cs` Config class):

```json
{
  "Enabled": true,
  "SuppressOnBloodMoon": true,
  "MaxItemsPerBag": 8,
  "BagLifetimeSeconds": 300,
  "NightBoostEnabled": true,
  "NightStartHour": 22,
  "NightEndHour": 6,
  "DropTiers": [
    { "Perm": "styx.zloot.master", "DropChance": 1.00, "Quality": "master" },
    { "Perm": "styx.zloot.vip",    "DropChance": 0.85, "Quality": "vip"    },
    { "Perm": "styx.zloot.use",    "DropChance": 0.60, "Quality": "basic"  }
  ],
  "TierOrder": ["basic", "vip", "master"],
  "QualityTiers": {
    "basic": {
      "Default": [
        { "Item": "foodCanSoup",       "MinCount": 1, "MaxCount": 1, "Chance": 0.35 },
        { "Item": "drinkJarRiverWater","MinCount": 1, "MaxCount": 2, "Chance": 0.40 }
      ],
      "ByClass": {
        "zombieNurse": [
          { "Item": "medicalBandage", "MinCount": 1, "MaxCount": 2, "Chance": 0.55 }
        ]
      }
    },
    "vip":    { "Default": [...], "ByClass": {...} },
    "master": { "Default": [...], "ByClass": {...} }
  }
}
```

| Field | Meaning |
|---|---|
| `Enabled` | Master kill switch |
| `SuppressOnBloodMoon` | True = no drops during a blood moon (anti-spam) |
| `MaxItemsPerBag` | Hard cap on rolled stacks per bag |
| `BagLifetimeSeconds` | Despawn delay; 0 = no auto-despawn |
| `NightBoostEnabled` | True = night kills bump the player's quality tier up one rung |
| `NightStartHour` / `NightEndHour` | 0–23; window wraps midnight if End ≤ Start |
| `DropTiers[].Perm` | Perm to test on the killer; first match wins |
| `DropTiers[].DropChance` | 0..1 — chance any bag spawns at all |
| `DropTiers[].Quality` | Key into `QualityTiers` |
| `TierOrder` | Ascending tier ladder used by night-boost to find "next tier up" |
| `QualityTiers.<name>.Default` | Catch-all loot table for any zombie not in `ByClass` |
| `QualityTiers.<name>.ByClass.<entityName>` | Per-class loot list — overrides Default for that exact zombie class |
| `QualityTiers.<name>.<entries>[].Item` | Item name (canonical, see `data/game_refs/items.md`) |
| `QualityTiers.<name>.<entries>[].MinCount` / `MaxCount` | Stack-size range |
| `QualityTiers.<name>.<entries>[].Chance` | 0..1 — independent roll per entry |

## Mechanics

- **Killer attribution**: `OnEntityKill(victim, response)` reads the killing player from the response. Drop only fires when a player did the killing — zombie-on-zombie / fire / fall = no drop.
- **Tier resolution**: walk `DropTiers` top-down for the killer. First perm match wins; no match = no drop.
- **Drop roll**: roll the resolved tier's `DropChance` once for the bag itself, then roll each `LootEntry.Chance` independently to fill it. Capped at `MaxItemsPerBag`.
- **ByClass override**: when the dead zombie's class name (e.g. `zombieNurse`, `zombieFatCop`) has an entry in the resolved quality's `ByClass` dict, that list is used INSTEAD of `Default`. Otherwise `Default` is rolled.
- **Bag spawn**: lootable bag at the zombie's death position; despawn scheduled after `BagLifetimeSeconds`.
- **Night boost**: when `NightBoostEnabled` and current hour is in the night window, the resolved tier's quality is shifted up by one rung in `TierOrder`. Already-top-tier players get no further boost.
- **Blood moon**: when `SuppressOnBloodMoon`, all drops are skipped while a blood moon is active.

## Per-zombie loot tweaks

Edit the `QualityTiers.<tier>.ByClass` dict for any specific entity class — the shipped defaults already include themed loot for `zombieNurse` (medical), `zombieFatCop` / `zombieCop` (ammo), `zombieBusinessMan` (cash + paper), `zombieFatHawaiian` (food + booze), and others. Use canonical entity names from `data/game_refs/entities.md` (run `/gamerefs`).

There is **no separate `PerZombieTypeOverride` block** — per-class behaviour comes from the `ByClass` dict inside each quality tier.

## Notes

- **No loot for default-group players by default** — you must grant `styx.zloot.use` for basic players to see drops. Server owners decide whether everyone gets free loot.
- **Drop counters in `/zloot stats`**: per-tier lifetime counts, reset on plugin reload.
- **Hot-reload** on config save.

## Common ops

### Ramp loot quality for a horde event

Temporarily edit `QualityTiers.basic.Default` (or specific `ByClass` entries) to include better items. Hot-reloads. Revert after the event.

### Suppress drops in a specific biome / zone

Not built-in. Workaround: revoke `styx.zloot.use` from `default` and use a region-detection plugin to grant it back in safe zones only.

### Disable for blood moon (already on by default)

`"SuppressOnBloodMoon": true` in config.
