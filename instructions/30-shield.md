# 30 — StyxShield

**Sanctuary buff bound to a land claim.** Toggle the shield on the LCB you're standing in (must be your own claim) and zombies stop noticing you while you're inside. It's a stealth zone, not a damage shield — they can't see you, they can't be alerted by your noise, and they won't wander in idly.

Three filters running in-engine via Harmony patches:

- **Aggro** — `EntityAlive.SetAttackTarget` refuses to target a player standing inside their own shielded LCB. Existing aggro is wiped on the next tick.
- **Noise / alerts** — `EntityAlive.SetInvestigatePosition` refuses noise points originating inside any active shield. Stops screamer calls, gunshot alerts, and rock-throw lures from converging.
- **Idle wander** — `EAIWander.CanExecute` flips destinations falling inside an active shield to false, so zombies steer around rather than drift through.

Bloodmoon: protection auto-suspends. The slot stays used; coverage resumes when blood moon ends.

## Commands

| Command | Perm | What |
|---|---|---|
| `/shield` | `styx.shield.use` | Toggle the shield on the LCB you're standing in. Must be your own claim. |
| `/m → Shield` | `styx.shield.use` | Open the info + toggle UI. LMB toggles, RMB closes. |

## Permissions

| Perm | What |
|---|---|
| `styx.shield.use` | Show `/m → Shield` in the launcher and run `/shield` |

Per-player slot count (default 1) is config-controlled, not perm-tiered out of the box. Operators can wire perm tiers to `MaxActivePerPlayer` via a separate plugin if they want VIP "two shields at once" behaviour.

## Config — `configs/StyxShield.json`

| Field | Default | Meaning |
|---|---|---|
| `MaxActivePerPlayer` | `1` | Cap on simultaneous active shields per player. Operators can place multiple LCBs but only sanctify this many at a time. |
| `BlockOnBloodmoon` | `true` | When true, the shield auto-suspends during BloodMoon world events. Set false if you want the shield to override blood moon (rarely sensible — point of bloodmoon is the horde). |
| `BlockRegularAnimals` | `false` | When true, the shield also filters bears, wolves and other vanilla wildlife in addition to undead. Default is undead-only (zombies + zombie animals). |
| `BlockBandits` | `false` | When true, the shield also filters bandits (hostile human NPCs). Bandits are a separate threat class the shield isn't really designed for; default off. |
| `FlushIntervalSeconds` | `30` | How often the zones registry flushes to `data/StyxShield/zones.json`. |
| `Verbose` | `true` | Log activate/deactivate events to the server console. |

## Mechanics

### Threat classification

The shield filters by `EntityFlags`:

| Flag | Filtered? |
|---|---|
| `EntityFlags.Zombie` | Always (covers regular zombies, ferals, radiated, screamers, zombie dogs, zombie vultures, zombie bears) |
| `EntityFlags.Animal` | Only when `BlockRegularAnimals = true` |
| `EntityFlags.Bandit` | Only when `BlockBandits = true` |

Players are never filtered (PvP is intentionally unaffected — the shield doesn't grant safety from other players).

Zombie dogs are tagged `animal,zombie` in `entityclasses.xml`; the zombie flag matches them on the default code path even though their `entityType` reads as Animal at the engine level. Bears do **not** carry the zombie flag and pass through the shield by default.

### Aggro clearing

When the shield activates, any zombie currently chasing you drops aggro on the next AI tick — `attackTarget` and `attackTargetTime` are wiped, so the pursuit task resolves cleanly without a 30 s decay window.

### Cross-shield protection (intentionally not granted)

If player A has a shield up and player B walks into player A's claim, B is **not** protected. The shield belongs to the LCB owner. This is deliberate — shielding party-mates is a perm-tier feature operators may want to add later via a separate plugin.

### Persistence

Active shields survive restarts. State lives at `data/StyxShield/zones.json`:

```json
{
  "Steam_76561198XXXXXXXXX": [
    { "x": -123, "y": 64, "z": 456 }
  ]
}
```

Centers are LCB block positions. Half-size is read from `GameStats.GetInt(EnumGameStats.LandClaimSize)` at activate time. Owners are resolved to entity-id eagerly on join so the hot-path lookup is by entity, not by pid.

### Bloodmoon behaviour

`Shield.IsBloodmoonGate` is overridable — by default it returns `world.IsWorldEvent(World.WorldEvent.BloodMoon)`. Operators can swap this for different semantics (e.g. only suspend during day-of-day-mod-7 night) without touching the plugin code.

## Operator notes

- Tell players: "stand IN your own land claim, then `/shield`". Outside the claim is a no-op.
- A common confusion: the shield doesn't make you invulnerable — zombies can still hit you if they're already next to you when you toggle it on. It just stops new aggro / pathing.
- Performance — three Harmony patches on `EntityAlive.SetAttackTarget`, `EntityAlive.SetInvestigatePosition`, `EAIWander.CanExecute`. Each runs sub-microsecond. Verified at ~0.07 % CPU with 50 active zombies on the test server. `/prof patches` shows live cost.
