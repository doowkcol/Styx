# 19 — StyxInputProbe (dev tool)

Demo of the `Styx.Ui.Input` event subsystem. Whispers each input event to the player when active. Use it to verify keybind→server flow when developing UI plugins.

**Operator note:** This is a dev/diagnostic tool. Safe to delete from `Mods/Styx/plugins/` if you don't need it. Doesn't ship to production servers normally.

## Commands

| Command | Perm | What |
|---|---|---|
| `/input on` | (open) | Start receiving input events — every keypress whispers to you |
| `/input off` | (open) | Stop |
| `/input status` | (open) | Report buff state + active consumers |

## Permissions

None — open to all (it's a dev tool).

## What you see when active

Every time you press jump / crouch / LMB / RMB / use / activate, the plugin whispers to you:

```
[Input] jump pressed (entityId=171)
[Input] crouch pressed (entityId=171)
[Input] primary pressed (entityId=171)
[Input] secondary pressed (entityId=171)
```

## Mechanics

- Acquires input via `Styx.Ui.Input.Acquire(player)` — applies the `buffStyxInputProbe` buff that routes keybinds through `NetPackageGameEventRequest` → server.
- Subscribes to `OnPlayerInput` hook (auto-wired by hook bus name convention).
- For each input event, whispers a formatted message.

## Notes

- **Pairs with the `buffStyxInputProbe` buff** defined in `Mods/Styx/Config/buffs.xml`. If you delete that buff, this plugin breaks.
- **Auto-releases on disconnect** — the buff comes off when you log out.
- **Dev/diagnostic only** — useful when you're developing a new menu plugin and want to confirm keybinds reach the server. Production servers don't need this.
- **If you remove this plugin**, the `Styx.Ui.Input` subsystem still works for other plugins (StyxMenu, Kit, etc.) — they each call `Acquire` themselves.

## See also

- `STYX_CAPABILITIES.md §10c` — full design notes on the input subsystem
- `STYX_HOOK_CATALOGUE.md` — `NetPackageGameEventRequest` interception
