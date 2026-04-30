# 13 — Reflect

Damage reflect modes. Demonstrates cancellable hooks. Only affects players (never zombies/animals).

## Commands

| Command | Perm | What |
|---|---|---|
| `/reflect off` | `styx.reflect.use` | Disable (passthrough) |
| `/reflect shield` | `styx.reflect.use` | Cancel ALL incoming damage (god mode) |
| `/reflect back` | `styx.reflect.use` | Take full damage, attacker takes same back |
| `/reflect double` | `styx.reflect.use` | Take half, attacker takes 2× back |

## Permissions

| Perm | What |
|---|---|
| `styx.reflect.use` | Set reflect mode on yourself |

## Modes

| Mode | You take | Attacker takes |
|---|---|---|
| `off` | 100% (vanilla) | 0% |
| `shield` | 0% | 0% |
| `back` | 100% | 100% mirror |
| `double` | 50% | 200% |

## Notes

- **Per-player mode** — each player sets their own. State is in-memory.
- **Player-vs-player only** — zombie hits, fall damage, drowning, etc. all pass through unaffected. Reflect only fires on player-source damage.
- **Demonstrates three hook outcomes** from a single `OnEntityDamage` method:
  - `return null` → game runs unmodified
  - `return false` → damage cancelled (shield mode)
  - `return int` → damage replaced with that amount (back/double modes)
- **Cleared on disconnect** — reflect state isn't persisted. Reapply on next session.

## Use cases

- **Demo plugin** for showcasing the hook system to plugin authors
- **Anti-grief**: set `back` for staff during PvP investigations — attackers self-damage
- **Donor perk**: gate `shield` mode behind a `styx.reflect.donor` perm tier (currently single perm — add tiers in code if desired)

## Notes

- Reflect mode is **shown to the affected player only** — attackers don't get a notification, just the damage taken back.
- In `back`/`double` modes, the attacker takes damage from a synthetic source — counts as PvP damage in stats.
- *(Historically suggested: pair with `/god on` so Godmode cancels damage before it reaches Reflect. Godmode is currently experimental and the player-visible result is broken — see `instructions/11-godmode.md` — so this combination doesn't keep you alive client-side today.)*
