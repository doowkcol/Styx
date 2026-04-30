# 18 — StyxZombieRadar

Live HUD readout of zombies within radius X of each player. Perm-tiered radius — no perm = no panel.

## Commands

| Command | Perm | What |
|---|---|---|
| `/radar` | (open) | Status — show your tier + radius + count |
| `/radar tick` | (open) | Force an immediate tick (debugging) |

## Permissions

| Perm | Default radius | What |
|---|---|---|
| `styx.radar.master` | 60m | Top tier — admins / max donors |
| `styx.radar.vip` | 30m | VIP donors |
| `styx.radar.use` | 10m | Basic — granted to default group |

First-match-wins. Players with NO matching perm → **panel hidden** (vanilla experience).

## Config — `configs/StyxZombieRadar.json`

```json
{
  "TickSeconds": 1.0,
  "RadiusByPerm": [
    { "Perm": "styx.radar.master", "Radius": 60 },
    { "Perm": "styx.radar.vip",    "Radius": 30 },
    { "Perm": "styx.radar.use",    "Radius": 10 }
  ]
}
```

| Field | What |
|---|---|
| `TickSeconds` | How often the count refreshes (default 1s) |
| `RadiusByPerm` | Ordered list — first perm match determines the player's radius. Empty list / no match = hidden. |

## Mechanics

- Server tick: every `TickSeconds`, for each connected player:
  1. Walk `RadiusByPerm` top-down, find first perm the player has → that's their radius
  2. If no match → push hidden state
  3. Else: query `World.AliveInRadius(pos, radius)`, filter to `EntityZombie`, count
  4. Push `styx.radar.count` + `styx.radar.radius` cvars for that player
- StyxHud renders the section if cvars are set.

## Notes

- **Standalone panel + HUD section** — StyxZombieRadar has its own panel (configurable to be visible standalone) AND mirrors into StyxHud's optional radar section.
- **Doesn't affect StyxHud's existence** — if you delete StyxZombieRadar.cs, the HUD just hides the radar section; rest of the HUD keeps working.
- **Sphere radius** — includes Y axis, not just XZ. A zombie 30m below in a basement counts as 30m away.
- **Type-filtered** — counts `EntityZombie` only. Animals, players, screamers (which are zombies — they count) all distinct.
- **Performance**: per-player loaded-entity walk per tick. Default 1s is cheap. Crank tick or radius at your own risk.

## Common ops

### Make everyone get the radar

Grant `styx.radar.use` to the default group:
```
/perm group grant default styx.radar.use
```

### Adjust tier radii

Edit config — hot-reloads.

### Disable for blood moon (e.g. don't want it spoiling the horde)

Not built-in. Workaround: use a hook plugin that revokes `styx.radar.use` while `IsBloodMoon` is active. Or just live with it.

### Add a "donor" tier between vip and master

```json
{ "Perm": "styx.radar.donor", "Radius": 45 }
```
Insert between master and vip in the list. Then `/perm group grant donor styx.radar.donor`.
