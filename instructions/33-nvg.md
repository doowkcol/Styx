# 33 — StyxNvg

**Personal night vision via a buff.** Toggle with `/nvg` and a real Unity light source pins to your camera so you can see in the dark — same mechanism the vanilla Mining Helmet Light mod uses, just driven by buff lifecycle instead of armour-equip events.

Server-side only, EAC on, no client install. Toggle state persists per platform-id, so dying / relogging / respawning preserves your preference.

> **What it actually does (and doesn't):** The Rust `simple night vision` plugin overrides the player's sky to render daylight while it's actually night. 7DTD V2.6 doesn't expose per-player sky / time-of-day overrides to mods, so a literal "force daylight" effect isn't possible without a client install. StyxNvg does the next-best server-side thing: a constant personal flashlight pinned to the camera. The sky still goes dark, but you can navigate, fight, and build by the personal light without burning torches.

## Commands

| Command | Perm | What |
|---|---|---|
| `/nvg` | `styx.nvg.use` | Toggle NVG on/off |
| `/nvg on` / `/nvg off` | `styx.nvg.use` | Explicit form |
| `/nvg show <player>` | `styx.nvg.admin` | Show another player's stored preference + buff status |

## Permissions

| Perm | What |
|---|---|
| `styx.nvg.use` | Run `/nvg` and toggle your own NVG |
| `styx.nvg.admin` | Run `/nvg show <player>` |

To grant NVG to everyone by default, open the perm editor and tick `styx.nvg.use` on the `default` group. To restrict it to VIP / Donor tiers, tick it on those groups instead.

## Config — `configs/StyxNvg.json`

```json
{
  "ReapplyIntervalSeconds": 60,
  "BuffDurationSeconds": 99999
}
```

| Field | Default | Meaning |
|---|---|---|
| `ReapplyIntervalSeconds` | `60` | How often the plugin refreshes the buff on every player who has NVG toggled on. Defensive — death and a few teleport edge cases can drop the buff while the player still wants it. |
| `BuffDurationSeconds` | `99999` | Duration override on each apply. The buff XML defines 999999; this just refreshes the timer. |

State is in `data/StyxNvg.state.json` — a flat list of platform-ids with NVG enabled. Safe to edit by hand if you need to clear someone's preference; the framework hot-reloads JSON state.

## Mechanics

### How the light works

`buffStyxNvg` (defined in `Config/buffs.xml`) fires `AddPartFPV` on `onSelfBuffStart` — that attaches a `miningHelmetLightSource.prefab` Unity light source to the player's `CameraNode` transform. `SetPartActive` switches it on. `RemovePart` on `onSelfBuffRemove` cleans up.

The vanilla Mining Helmet Light uses the same actions with the same prefab, just triggered from `onSelfEquipChanged` (helmet equip) instead of `onSelfBuffStart`. We deliberately skipped the third-person variant (`AddPartTPV`) because it parents to the helmet's `Spotlight` bone, which only exists on the helmet model. Without TPV, other players don't see a beam coming off the wearer — that's fine for nvg's purpose, where the only thing that matters is what the wearer themselves sees.

### Lifecycle

| Event | Plugin behaviour |
|---|---|
| Player runs `/nvg` (turning on) | Add platform-id to `Enabled` set, persist, apply buff |
| Player runs `/nvg` (turning off) | Remove from set, persist, remove buff |
| Player spawns (join, respawn, world change) | Re-apply buff after 1 s if `Enabled.Contains(pid)` |
| Reapply tick (every 60 s by default) | Re-apply buff on every online player whose state is on |
| Plugin unload | Remove buff from every online player. Disk state preserved — next load restores preferences. |
| Perm revoked | Buff is removed at the next reapply tick (the plugin checks `HasPermission(pid, styx.nvg.use)` each pass) |

### Why a buff and not a held-item swap?

Two alternatives we rejected:

1. **Give the player a flashlight on their toolbelt.** Awful UX — uses up a hotbar slot, drops on death, blocks aiming.
2. **Spawn a separate Light entity following the player.** Possible via `AddPartTPV` from the server, but then the light is decoupled from the player's view — you can't aim it.

The buff-driven `AddPartFPV` approach pins the light to the *camera*, so it follows where you look. Same UX as vanilla mining helmet without the helmet.

## Operator notes

- The buff icon (lightbulb) shows in the player's HUD while NVG is on. Players can see at a glance whether their nvg is active.
- `/nvg show <player>` reports two things — the persisted *preference* and whether the buff is currently *active* on the entity. They should match almost always; if "preference: ON, buff active: no" lasts more than 60 s you've found a buff-clearing edge case worth reporting.
- After editing `Config/buffs.xml` (e.g. to swap the icon), **server restart required** — buffs.xml loads once at boot.
- Hot-reloading `StyxNvg.cs` works fine — players' on/off preference is in the data store, untouched by code reload.
- The buff's name + description are runtime-registered by the plugin (`Ui.Labels.Register` from `OnLoad`), so you don't need to edit `Config/Localization.txt`. Already-connected players see the new labels on next reconnect.
