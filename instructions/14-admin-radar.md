# 14 — AdminRadar

Through-walls 5-category entity readout for admins. Updates live in the HUD.

## Commands

| Command | Perm | What |
|---|---|---|
| `/aradar` | `styx.admin.radar` | Toggle the radar HUD on/off for yourself |
| `/aradar on` | `styx.admin.radar` | Force on |
| `/aradar off` | `styx.admin.radar` | Force off |
| `/m → Admin Tools → Admin Radar` | `styx.admin.tools` + `styx.admin.radar` | Same toggle via UI |

## Permissions

| Perm | What |
|---|---|
| `styx.admin.radar` | Allowed to use the radar |

Without this perm the radar is hidden regardless of any toggle attempt.

## What it shows

5 rows in the HUD, each showing **count + nearest distance**:

| Row | Category |
|---|---|
| Players | Other players within radius (excluding self) |
| Zombies | Zombies within radius |
| Animals | Animals (deer, wolves, snakes, etc.) |
| Items | Dropped loot + backpacks |
| Vehicles | Minibikes, motorcycles, 4x4s, gyrocopters |

## Config — `configs/AdminRadar.json`

```json
{
  "RadiusMeters": 80,
  "TickSeconds": 1.0,
  "ShowPlayers":  true,
  "ShowZombies":  true,
  "ShowAnimals":  true,
  "ShowItems":    true,
  "ShowVehicles": true
}
```

| Field | What |
|---|---|
| `RadiusMeters` | Detection radius around you (default 80m) |
| `TickSeconds` | How often the count refreshes (default 1s) |
| `ShowX` | Per-category visibility — disable rows you don't want |

## Mechanics

- Server-tick: every `TickSeconds`, walks the world's loaded entities and counts each category within `RadiusMeters` of each admin who has the radar toggled on.
- Pushes counts via cvars (`styx.aradar.players`, `styx.aradar.zombies`, etc.) bound to the HUD panel.
- **Per-player toggle** — each admin individually controls their radar. State is in-memory (not persisted across server restart).

## Notes

- **Admin tool** — non-admins never see the panel even if their cvars somehow got set.
- **Performance**: 5 categories × N admins × radius walk per second. Default 80m / 1s is cheap. Crank radius or shorten tick at your own risk on big servers.
- **Radius is a sphere** — Y is included, not just XZ.
- **Items category** = dropped world items + transient backpacks. Not chest/storage contents.
- **Vehicles**: only loaded vehicles (in active chunks). Stored vehicles in unloaded chunks aren't counted.
- **Distance**: shown in metres rounded to nearest int.

## Common ops

### Quick on/off via menu

`/m → Admin Tools` shows live ON/OFF status next to "Admin Radar" — one LMB toggles.

### Wider radius for raid investigations

```json
"RadiusMeters": 200
```
Hot-reloads. Reverts on next config save.

### Show only zombies + players

```json
"ShowAnimals":  false,
"ShowItems":    false,
"ShowVehicles": false
```
