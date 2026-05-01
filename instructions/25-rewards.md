# 25 — StyxRewards

The configurable earn engine. Players earn currency and/or XP from kills, loot, harvest, quests, login bonus, and online time. Pays out via `IEconomy` and `ILeveling` — works with whichever you've got installed (pure money server, pure XP server, or both).

## Earn paths

Every path has TWO knobs: **money** (`<X>Bounty/Reward`) and **XP** (`<X>Xp`). Set either to 0 to disable that side. Set both to 0 to disable the path entirely.

| Path | Money key | XP key | Override dict |
|---|---|---|---|
| First-ever spawn | `StartingBalance` | `StartingXp` | — |
| Daily login (first spawn each day) | `DailyLoginBonus` | `DailyLoginXp` | — |
| Online stipend (per N min online) | `OnlineStipend` | `OnlineStipendXp` | — |
| Zombie kill | `ZombieBountyDefault` | `ZombieXpDefault` | `ZombieBountyByClass` / `ZombieXpByClass` (key = entityClassName) |
| Loot container (first open) | `LootBountyDefault` | `LootXpDefault` | `LootBountyByClass` / `LootXpByClass` (key = TileEntity class name) |
| Block harvest (per block, with tool) | `HarvestBountyDefault` | `HarvestXpDefault` | `HarvestBountyByBlock` / `HarvestXpByBlock` (key = block name) |
| Quest complete | `QuestRewardDefault` | `QuestXpDefault` | `QuestRewardByID` / `QuestXpByID` (key = quest ID) |

Override dicts win over the default for matching keys. Example:
```json
"ZombieBountyDefault": 1,
"ZombieXpDefault": 5,
"ZombieBountyByClass": {
    "zombieFatCop":   5,
    "zombieRadiated": 3
},
"ZombieXpByClass": {
    "zombieFatCop":   25,
    "zombieRadiated": 15
}
```

## Multiplier perms (donor / VIP / event)

```json
"Multipliers": [
    { "Perm": "styx.eco.x3", "Multiplier": 3.0 },
    { "Perm": "styx.eco.x2", "Multiplier": 2.0 }
]
```

First-match-wins — order from highest to lowest. Multiplier applies to **both** money and XP for any earn path. Players with no matching perm earn at 1×.

Grant via `/perm group grant donor styx.eco.x2`.

## Whisper toggles

```json
"WhisperOnLogin":   true,    // chatty for "big" events
"WhisperOnQuest":   true,
"WhisperOnStipend": true,
"WhisperOnKill":    false,   // silent for grindy events
"WhisperOnLoot":    false,
"WhisperOnHarvest": false
```

When enabled, the player gets a chat message like:
```
[Reward] +5 Credits, +25 XP (kill zombieFatCop x2)
```

When disabled, the HUD updates live without chat noise.

## Earn perm gate

```json
"EarnPerm": "styx.eco.earn"
```

Players need this perm (default group should have it) to earn from any auto-source. Empty string disables the gate (open to all).

## Block harvest gotchas

- Fires per block destroyed — felling a tree pays per trunk segment (~5-10 payouts). Keep `HarvestBountyDefault` low.
- `HarvestRequireTool: true` (default) only pays for blocks broken with a harvest tool (axe, pickaxe). Hand-punching and demolition don't pay.
- Set to `false` to also pay for hand/wrench breaks — risky on PvP servers (players farm by demolishing buildings).

## Loot container gotchas (fixed v0.2)

The hook only fires for **fresh** loot containers — first open, with a real loot list. Skipped:
- Already-looted containers (re-opens)
- Player-placed storage chests / safes / lockers
- Vehicle storage bags
- Containers the player has put items into

So earnings only pay out for genuine loot discovery, not inventory shuffling.

## Commands

| Command | What | Perm |
|---|---|---|
| `/rewards` | Show your multiplier + online time + next stipend | open |
| `/rewards stipend` | Force a stipend tick now (testing) | `styx.eco.admin` |

## Where state lives

- `data/StyxRewards/state.json` — first-spawn-seeded set + last-login-bonus dates per player
- Online stipend timer: in-memory only (resets on player reconnect — intentional anti-farm)

## Operating it

- **Tune iteratively**: open `configs/StyxRewards.json`, tweak rates, save → framework hot-reloads
- **Test fast**: temporarily set `ZombieXpDefault: 1000` to push yourself past milestones in 25 zombie kills, then revert
- **Spam protection**: leave `LogTransactions: false` on StyxEconomy unless debugging — harvest path is a hot logger

## See also

- [24 — StyxEconomy](./24-economy.md) — the bank this writes to
- [27 — StyxLeveling](./27-leveling.md) — the XP system this writes to
