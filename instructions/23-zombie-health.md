# 23 — StyxZombieHealth

Crosshair entity-health HUD readout. Shows the name + HP of any zombie / animal / player you're aimed at, mounted top-center under the compass.

## Commands

| Command | Perm | What |
|---|---|---|
| `/zhealth` *(or `/zhealth status`)* | (open) | Plugin status — config, perms, diag state |
| `/zhealth diag on\|off` | (open) | Toggle target-acquired/lost transition logging to server log |
| `/zhealth probe` | (open) | One-shot scan for what you're aimed at right now (whispered to chat) |

## Permissions

| Perm | Default | What |
|---|---|---|
| `styx.zhealth.use` | (must be granted) | Required to see the readout (gate via `Config.RequirePerm`) |

Grant to default group for everyone-sees-it on a Romero-style server:
```
/perm group grant default styx.zhealth.use
```

## Config — `configs/StyxZombieHealth.json`

```json
{
  "Enabled": true,
  "TickSeconds": 0.2,             // 5Hz refresh
  "MaxRange": 30,                  // metres
  "ConeAngleDegrees": 30,          // pre-filter cone (final test is ray-vs-sphere)
  "EntityHitRadius": 0.7,          // body sphere radius for hit detection
  "RequirePerm": true,
  "Perm": "styx.zhealth.use",
  "ShowZombies": true,
  "ShowAnimals": false,
  "ShowOtherPlayers": false        // PvP servers usually keep this off
}
```

| Knob | Notes |
|---|---|
| `ConeAngleDegrees` | Cheap angular pre-filter. 30° works at all ranges; the ray-vs-sphere test is what actually decides. Skipped at <5m where point-blank angles are misleading. |
| `EntityHitRadius` | Body sphere radius. 0.7m covers torso + shoulders + arms for a humanoid. Bump to 1.0 for tankier zombies if you want more forgiving aim. |
| `ShowAnimals` | Toggle on if you want deer/wolf HP too |
| `ShowOtherPlayers` | PvP info advantage — usually OFF on competitive servers |

## What you see

Aimed at a Boe zombie at 12m:
```
┌───────────────────────────┐
│           Boe             │
│       HP: 224 / 224       │
└───────────────────────────┘
```

Aimed at a Feral Wight as it gets chunked:
```
┌───────────────────────────┐
│       Feral Wight         │
│       HP: 145 / 480       │
└───────────────────────────┘
```

Walk away or look at sky → panel hides within 200ms (next tick).

## Mechanics

- **5Hz tick** per online player. Each tick, raycast multi-sample (head/chest/hips) against every nearby entity that passes type filter.
- **Multi-sample ray-vs-sphere** — checks 3 points along entity's vertical axis (0.4m hips, 1.0m chest, 1.6m head) with 0.7m body radius. Hit if look-ray passes within radius of ANY sample. Approximates a vertical capsule body collider — head shots, body shots, leg shots all register equally.
- **Entity name resolution** uses indexed-localization: at OnLoad, every `EntityClass` gets a small sequential index (1..292), label registered as `styx_zh_e_<idx>` → display name. Cvars push the index (small int — float-precision-safe). XUi binding: `{#localization('styx_zh_e_' + int(cvar('styx.zhealth.classid')))}`.
- **Auto-hide** when not aimed at anything — visibility cvar drops to 0, panel root rect's visible binding evaluates false.

## Notes

- **Dead zombies / corpses are ignored** — `IsDead()` filter skips them. You'll only see HP for live targets.
- **Names need 2 server restarts on first install** — labels stage at boot's startup write, load at next-boot init. After the second restart names resolve to "Boe" / "Feral Wight" etc. Until then they show as raw keys (`styx_zh_e_5`).
- **Modlet zombies are auto-included** — anything in `EntityClass.list` gets a label registered. Add a custom zombie modlet → restart twice → its name appears.
- **Performance**: 5Hz × N players × N entities/tick. Cheap on practical servers. Bump `TickSeconds` higher if you have very dense zombie spawns.
- **Class hashes hit the float-precision wall** — see `STYX_CAPABILITIES.md §24.3` for why we use sequential indices instead of raw class hashes.

## Common ops

### Show animals too (deer, wolves, snakes)

Edit config → `"ShowAnimals": true` → save (hot-reloads).

### Disable for everyone

Two ways:
- Edit config → `"Enabled": false` → hot-reloads
- Revoke the perm: `/perm group revoke default styx.zhealth.use`

### Make targeting more / less generous

| Want | Change |
|---|---|
| Easier aim (catches body shots from further off) | Bump `EntityHitRadius` to 1.0 |
| Tighter aim (only direct crosshair) | Drop `EntityHitRadius` to 0.5 |
| Longer range readout | Bump `MaxRange` to 60 |
| Smoother HP refresh | Drop `TickSeconds` to 0.1 (10Hz, more CPU) |

### Diagnose missing readout

```
/zhealth probe
```

Look at a zombie when you run it. Reports either `Target: …` (working) or `No target. Nearby live zombies=N, dead corpses=M` (with reason hints).

```
/zhealth diag on
```

Server log spams `target ACQUIRED` / `target LOST` per transition. Useful when investigating "panel flickers" or "why doesn't it show on this zombie".

## Future enhancements (v0.3 candidates)

- HP bar segments instead of numeric (10 sprite slices, each visible at >= N×10% pct)
- Color-coded HP (green > 60%, yellow 30-60%, red < 30%)
- Distance readout alongside HP
- Headshot indicator / weakpoint hints
- Configurable position (currently hardcoded top-center)
