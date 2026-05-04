# 32 — StyxPockets

**Perm-tiered extra inventory slots and carry capacity.** Players in the right group automatically get a passive `BagSize` + `CarryCapacity` bump on join — bigger inventory grid, higher encumbrance threshold. Backed by vanilla buffs in `Config/buffs.xml` so it works under EAC with **zero client install** (the engine resizes the bag UI dynamically based on `BagSize`, the same way storage-pocket armour mods do).

Two operator knobs:

- **`BaselineBuff`** — give every player a flat bump regardless of perms (`"+6 for everyone, period"`).
- **`Tiers`** — list of `(perm → buff)` mappings; players with the perm get the buff. Stacks on top of `BaselineBuff`.

Plugin reapplies the right buff(s) on spawn and on a periodic tick, so the bump is sticky for as long as the perm holds.

## Commands

| Command | Perm | What |
|---|---|---|
| `/pockets` | (open) | Show your own active pocket buffs |
| `/pockets show <player>` | (open) | Show another player's active pocket buffs |
| `/pockets list` | (open) | Print the configured tier ladder + mode + baseline |
| `/pockets reapply [<player>\|all]` | `styx.pockets.admin` | Force the apply pass on one or all online players (debug) |

## Permissions

| Perm | What |
|---|---|
| `styx.pockets.t1` | Default tier 1 — `buffStyxPockets6` (+6 slots / +6 carry) |
| `styx.pockets.t2` | Default tier 2 — `buffStyxPockets12` (+12 / +12) |
| `styx.pockets.t3` | Default tier 3 — `buffStyxPockets24` (+24 / +24) |
| `styx.pockets.admin` | Run `/pockets reapply` |

To grant a tier to a group, open the perm editor: `/perm` → pick group → pick `StyxPockets` → toggle the tier perm on. Or chat:

```
/perm group grant vip styx.pockets.t2
/perm group grant donor styx.pockets.t3
```

## Config — `configs/StyxPockets.json`

```json
{
  "BaselineBuff": "",
  "Tiers": [
    { "Perm": "styx.pockets.t1", "Buff": "buffStyxPockets6",  "DisplayName": "+6 pockets"  },
    { "Perm": "styx.pockets.t2", "Buff": "buffStyxPockets12", "DisplayName": "+12 pockets" },
    { "Perm": "styx.pockets.t3", "Buff": "buffStyxPockets24", "DisplayName": "+24 pockets" }
  ],
  "TierMode": "highest",
  "ReapplyIntervalSeconds": 600,
  "ReapplyOnSpawn": true,
  "BuffDurationSeconds": 99999
}
```

| Field | Meaning |
|---|---|
| `BaselineBuff` | Empty = off. Set to a buff name (e.g. `"buffStyxPockets6"`) to apply that buff to **every** player regardless of perm. Stacks with whichever tier the player qualifies for. |
| `Tiers` | Ladder of `(Perm → Buff)`. Order matters in `"highest"` mode. |
| `TierMode` | `"highest"` (default) — apply only the **last** matching tier in config order (a player with t1+t2+t3 perms gets only t3). `"stack"` — apply **every** matching tier; vanilla `passive_effect base_add` stacks them (a player with t1+t2+t3 perms gets +6+12+24 = +42 slots). |
| `ReapplyIntervalSeconds` | Periodic refresh tick. Default 10 min. Defensive — buffs persist 999999s but a periodic refresh covers cases where vanilla cleared a buff (negative-buff wipes, debug actions, mod interactions). |
| `ReapplyOnSpawn` | Apply immediately on player spawn (join, respawn, world change). Default true. |
| `BuffDurationSeconds` | Duration override on each apply. Default 99999. The buff XML defines 999999; this just refreshes the timer. |

## Built-in buff ladder

Defined in `Config/buffs.xml`:

| Buff name | `BagSize` | `CarryCapacity` | Icon |
|---|---|---|---|
| `buffStyxPockets6` | +6 | +6 | backpack |
| `buffStyxPockets12` | +12 | +12 | backpack |
| `buffStyxPockets24` | +24 | +24 | backpack |

Want a different value? Add a buff to `Config/buffs.xml` (copy one of the existing three, change the `value` attribute on the two `passive_effect` lines), then either point a tier at it or set it as `BaselineBuff` in config.

```xml
<buff name="buffStyxPocketsHuge"
      name_key="buffStyxPocketsHugeName"
      icon="ui_game_symbol_backpack">
    <stack_type value="replace"/>
    <duration value="999999"/>
    <effect_group>
        <passive_effect name="BagSize"       operation="base_add" value="48"/>
        <passive_effect name="CarryCapacity" operation="base_add" value="48"/>
    </effect_group>
</buff>
```

Don't forget to add a `Localization.txt` entry for `buffStyxPocketsHugeName` so the buff bar shows a readable label.

## Mechanics

### Why bump both `BagSize` and `CarryCapacity`?

- `BagSize` drives the visible slot grid the client paints. Vanilla resizes the bag UI based on this stat.
- `CarryCapacity` is the encumbrance threshold — the number of slots a player can occupy before vanilla starts piling on the **Encumbered** stamina-cost penalty.

Bumping `BagSize` without bumping `CarryCapacity` gives players extra slots that immediately encumber them when used. Always bump both unless you specifically want that effect.

### Why a separate plugin from StyxBuffs?

Pocket buffs are passive infrastructure — the player doesn't toggle them on/off, they just have them based on their tier. Mixing them into the StyxBuffs picker (`/m → My Buffs`) would clutter the toggle list with non-toggleable rows. StyxPockets stays silent: apply on spawn, refresh on tick, never appears in the picker UI. The buff icon **does** show in the player's HUD buff bar (so they can see they have it), it just doesn't take up a row in the buffs picker.

### Stack vs highest

`"highest"` is the cleaner UX for a tier-promotion model — "Donor gets +24, VIP gets +12, Default gets nothing". Granting a player both `styx.pockets.t1` and `styx.pockets.t2` perms gets them just t2, not t1+t2.

`"stack"` is the right model when tiers represent additive bonuses — e.g. `styx.pockets.event` granting a temporary +6 on top of the player's base tier. Vanilla `passive_effect base_add` stacking just works.

## Operator notes

- The buff icon shows in the player's vanilla buff bar. Players notice the icon and ask in chat what it does — that's a feature, not a bug.
- `/pockets list` is the quick way to dump your current configuration in chat for debugging.
- After editing `configs/StyxPockets.json`, the plugin hot-reloads and the next reapply tick (or `/pockets reapply all`) pushes the new config out.
- After editing buff values in `Config/buffs.xml`, **server restart required** — buffs.xml is loaded once at boot.
