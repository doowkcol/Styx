# 04 — StyxBackpack

Per-player persistent storage container, opened via `/b`. Contents survive logout. Optional perm for "keep your backpack on death".

## Commands

| Command | Perm | What |
|---|---|---|
| `/b` | a tier perm (any of the configured `SizeByPerm` entries) | Open your personal backpack |
| `/m → Backpack` | same | Same, via launcher |

## Permissions

| Perm | What |
|---|---|
| `styx.backpack.master` | Open with the master tier (default 6 rows × 8 cols = 48 slots) |
| `styx.backpack.vip` | VIP tier (default 4 rows × 8 = 32 slots) |
| `styx.backpack.use` | Basic tier (default 3 rows × 8 = 24 slots) |
| `styx.backpack.keep_on_death` | Death-bag immunity — your backpack stays with you across deaths |

First-match-wins on the `SizeByPerm` list: highest tier the player holds wins. No matching tier = no backpack (launcher entry hidden, `/b` refused).

## Config — `configs/StyxBackpack.json`

```json
{
  "Enabled": true,
  "Cols": 8,
  "SizeByPerm": [
    { "Perm": "styx.backpack.master", "Rows": 6 },
    { "Perm": "styx.backpack.vip",    "Rows": 4 },
    { "Perm": "styx.backpack.use",    "Rows": 3 }
  ],
  "KeepOnDeathPerm": "styx.backpack.keep_on_death",
  "DropLifetimeSeconds": 1800,
  "AutosaveSeconds": 2.0,
  "PollSeconds": 0.5,
  "VerboseAutosave": false
}
```

| Field | Meaning |
|---|---|
| `Cols` | Grid width. 8 is the 7DTD standard; other values are untested. |
| `SizeByPerm[].Perm` | Perm that grants this tier |
| `SizeByPerm[].Rows` | Slot rows for that tier (slot count = `Rows × Cols`) |
| `KeepOnDeathPerm` | Holding this perm keeps the backpack across deaths |
| `DropLifetimeSeconds` | Despawn delay for the drop-bag spawned on death without keep-on-death |
| `AutosaveSeconds` | Autosave cadence while open (lower = more crash-safe) |
| `PollSeconds` | Polling cadence for "is the player still in the loot UI" close detection |
| `VerboseAutosave` | Log every autosave (debug only) |

## How it works

The container is **not** a permanent world block. Each `/b` call:

1. Spawns a short-lived `EntityBackpack` at your feet (custom `StyxBackpack` entity class — header reads "Styx Backpack" in the loot window, distinct from vanilla death bags and Styx sell bins)
2. Hydrates it from your saved file (`data/StyxBackpack/<PlatformId>.json`)
3. Triggers the engine's "open loot UI" — the loot window opens, AND the vanilla flow auto-opens your character backpack panel beside it for drag-drop
4. Polls `IsUserAccessing` every `PollSeconds`; on close, serialises everything back to the JSON file, **empties the in-world container, and despawns the entity**

Auto-saves every `AutosaveSeconds` while open — crash-safe within that window.

### Why the container gets emptied on close

The engine treats `EntityBackpack` with `bPlayerBackpack=true` as a persistent player stash and won't auto-Kill it while it has items. If we just call `RemoveEntity`, the chunk save can race the removal and re-persist the bag — leading to orphan bags appearing on the next reconnect. Items are already in the JSON save by the time we clear the in-world container, so this is lossless and lets the engine's normal cleanup path run cleanly.

### Orphan-bag sweep

If you do see a bag appear briefly at login (orphan from before the v0.3.1 fix, or from an unclean shutdown), the plugin runs an orphan sweep at 0.3 / 1 / 2 / 4 seconds after spawn that picks them up. Server log:

```
[StyxBackpack] Post-connect sweep (fast) removed N orphan stash bag(s)
```

After that one cycle the chunk save persists the removal and the orphans are gone permanently. Steady-state should report nothing — if it keeps reporting non-zero counts every login, something's still leaking; capture the log and investigate.

You can also trigger the sweep manually via `/b sweep` (admin).

## Death behaviour

| Player has | What happens on death |
|---|---|
| `styx.backpack.keep_on_death` | Backpack untouched. Player keeps their saved contents. |
| (no death-protection perm) | Backpack contents drop as a **lootable bag** at death position. Saved file is cleared. |

The death-bag despawns after `DropLifetimeSeconds` if nobody picks it up.

## Notes

- **Logoff is not death** — the plugin guards `OnEntityDeath` against the logoff path (`IsDead()/Health<=0` check). Without this, you'd drop a "death bag" every time you disconnect. Fixed in v0.3.0.
- **Item loss on crash**: if the server crashes within the autosave window, the most recent edits may be lost. Acceptable trade-off vs. write-on-every-mutation overhead.
- **Storage**: `data/StyxBackpack/<PlatformId>.json` per-player. Binary `ItemStack.Write/Read` blob, base64-encoded inside JSON (so quality, mods, and durability all round-trip).
- **Stuck-session safety net**: if a session never transitions to "accessing" within 10s (open RPC dropped), the plugin force-cleans so the player isn't blocked from retrying.

## Common ops

### Wipe a player's backpack

Stop the server, delete `data/StyxBackpack/<PlatformId>.json`, restart.

### Find heavy users

Sort `data/StyxBackpack/*.json` by file size — largest = most stored items.
