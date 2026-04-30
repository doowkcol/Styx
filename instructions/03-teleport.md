# 03 — StyxTeleport

Per-player home teleports + nearest-trader + last-death return.

## Commands

| Command | Perm | What |
|---|---|---|
| `/sethome <1-N>` | `styx.tp.use` | Save current location to home slot N |
| `/delhome <1-N>` | `styx.tp.use` | Clear home slot N |
| `/listhomes` | `styx.tp.use` | Chat dump of your saved homes |
| `/m → Teleport` | `styx.tp.use` | Picker: homes + nearest trader + last death |

`N` is 1 to `MaxHomes` (default 3, configurable up to 6 — XUi has 6 row slots).

## Permissions

| Perm | What |
|---|---|
| `styx.tp.use` | Use teleport at all |
| `styx.tp.basic` / `styx.tp.vip` / `styx.tp.master` | Tier perms — control cooldowns + daily limits |

## Config — `configs/StyxTeleport.json`

```json
{
  "MaxHomes": 3,
  "TraderEnabled": true,
  "DeathEnabled": true,
  "Tiers": [
    {
      "Perm": "styx.tp.master",
      "CooldownSeconds": 60,
      "DailyLimit": 0          // 0 = unlimited
    },
    {
      "Perm": "styx.tp.vip",
      "CooldownSeconds": 300,
      "DailyLimit": 20
    },
    {
      "Perm": "styx.tp.use",
      "CooldownSeconds": 600,
      "DailyLimit": 5
    }
  ]
}
```

**"Most generous wins"** — if a player is in multiple groups, they get the lowest cooldown / highest daily limit across all matching tiers.

## Destinations available in `/m → Teleport`

| Row | Destination | Notes |
|---|---|---|
| 1-N | Home slots | Empty slots are visible but greyed-out |
| Trader | Nearest trader to your current position | Drops you outside the trader area (anti-tamper) |
| Death | Your last death location | Captured automatically `OnEntityDeath` |

## Notes

- **Active quest warning**: teleporting clears the active quest. Two-tap LMB to confirm.
- **Injury preservation**: `EntityPlayer.Respawn(Teleport)` strips broken legs / bleeds / infections — this plugin **snapshots and re-applies** them so you can't exploit teleport to clear injuries.
- **Trader drop**: never spawns inside the trader area (the engine treats that as trespass and may relocate you to the void). Computed drop is on the closest face of the trader rectangle, padded 4 blocks outside.
- **Last death**: persists across logoff. Cleared next time you die (always tracks the most recent).
- **Storage**: `data/StyxTeleport.state.json` per-player.

## Common ops

### Bump max homes from 3 to 6

Edit `configs/StyxTeleport.json` → `"MaxHomes": 6` → save. Hot-reloads. Existing homes preserved.

### Add a new tier for "donor"

```json
{ "Perm": "styx.tp.donor", "CooldownSeconds": 120, "DailyLimit": 10 }
```

Then `/perm group create donor vip` and `/perm group grant donor styx.tp.donor`.
