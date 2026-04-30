# 17 — StyxMenu

Interactive server-only action menu. Demonstrates the framework's UI input subsystem.

This is the **action menu** plugin — distinct from the `/m` launcher (which is a framework-level top-level entry that lists every plugin's UI). The action menu is one of the entries inside `/m`.

## Commands

| Command | Perm | What |
|---|---|---|
| `/menu` | (open) | Open the action menu |
| `/menu close` | (open) | Force-close |
| `/m → Action menu (heal / water / tp / info)` | (open) | Same, via launcher |

## Permissions

None — open to all players. Individual actions inside the menu may have their own perms (e.g. heal-full uses a buff that may itself be perm-gated).

## Controls (while menu open)

| Input | Action |
|---|---|
| **JUMP** (space) | Next option |
| **CROUCH** (C) | Previous option |
| **PRIMARY** (LMB) | Confirm — execute action for selected row |
| **SECONDARY** (RMB) | Cancel — close menu |

## Menu actions

| Row | What |
|---|---|
| Heal Full | Apply `buffStyxHealFull` — full restore: HP/Food/Water/Stamina to max + clears injuries (broken legs, sprains, bleeds, abrasions, stuns) + cures diseases (infections all stages, dysentery, food poisoning) + removes lingering treatment buffs (splints, casts) |
| Give Water | Drop a backpack at your feet containing 3× `drinkJarBoiledWater` |
| Teleport 20m east | Short hop teleport (debug / demo). For real teleporting use `StyxTeleport` (`/m → Teleport`). |
| Server Info | Whisper current day, players online, blood-moon flag |
| Close | Just closes the menu (BBCode whisper confirmation) |

## Mechanics

- Uses `Styx.Ui.Input.Acquire(player)` to grab input events.
- `OnPlayerInput` hook receives each input (jump / crouch / primary / secondary).
- Selection state stored in `styx.menu.sel` cvar, bound to UI panel for live highlight.
- `styx.menu.open` cvar toggles panel visibility.
- Actions execute via existing framework APIs (`Player.ApplyBuff`, `Player.GiveBackpack`, `Player.Teleport`, etc.).

## Notes

- **Reference implementation** for the framework `Styx.Ui.Input` + `Styx.Ui.SetVar` + `Styx.Ui.Ephemeral` subsystems. If you're writing a new menu plugin, copy this pattern.
- **Buff-driven heal** — uses `buffStyxHealFull` (defined in `Mods/Styx/Config/buffs.xml`) instead of writing directly to `Stats.Health.Value`. Why: server-side health writes get clobbered by next client PlayerData sync. Buff `triggered_effect AddHealth` works cleanly. See `STYX_CAPABILITIES.md §10d`.
- **Water via GiveBackpack** — same client-authoritative reason. Drops a single backpack at your feet with 5× water.
- **`/m` launcher** is the framework-level top-entry — see `STYX_CAPABILITIES.md §10f`. StyxMenu is just one of the rows in `/m`.

## Common ops

### Add a new action

Edit `Mods/Styx/plugins/StyxMenu.cs`:

```csharp
private const int RowMyAction = 4;

// In the row label registration
Styx.Ui.Labels.Register(this, "styx.menu.row" + RowMyAction, "My New Action");

// In the action dispatch switch
case RowMyAction:
    // do something
    StyxCore.Player.ApplyBuff(p, "buffMegaCrush");
    break;
```

Bump the row count in the XUi panel too (or extend up to the existing row count if there's space). Hot-reloads.

### Customise controls

The controls (jump/crouch/LMB/RMB) are framework-level — every Styx menu uses the same scheme. Not configurable per-plugin without changing `Styx.Ui.Input`.
