# 15 — AdminTools

Sub-menu launcher for admin toggles. Currently exposes Vanish + AdminRadar from a single picker. Future admin toggles plug in here.

## Commands

| Command | Perm | What |
|---|---|---|
| `/m → Admin Tools` | `styx.admin.tools` | Open the picker menu |

No standalone chat command — it's UI-only. Underlying tools each have their own chat command (`/vanish`, `/aradar`).

## Permissions

| Perm | What |
|---|---|
| `styx.admin.tools` | Open the menu (the launcher entry appears for everyone but selecting it whispers a perm error if you lack this) |

## Menu rows

| Row | Toggle | Underlying perm needed |
|---|---|---|
| Vanish | `EntityPlayer.IsSpectator` flag | `styx.admin.vanish` |
| Admin Radar | `styx.aradar.visible` cvar | `styx.admin.radar` |

Each row shows live `[ON]` / `[OFF]` state read from the actual underlying state, not a cached UI variable.

## Mechanics

- Toggle dispatches go through the `CommandManager` — same as if you typed `/vanish` or `/aradar` in chat. All perm checks + whisper feedback fire as normal.
- No logic duplication — the underlying plugins do the actual work.
- **Live status** — the menu reads the real state every render, so `[ON]`/`[OFF]` is always accurate even if state changed via chat command in another window.

## Notes

- **Granting only `styx.admin.tools`** lets you see the menu but selecting either row will whisper a perm error if you don't have the underlying perm. Standard pattern: grant the umbrella perm to your "admin" group, plus the underlying perms.
- **Adding new toggles**: edit `AdminTools.cs`, add a row constant + dispatch case, recompile. Hot-reloads.

## Common ops

### Grant full admin tooling

```
/perm group grant admin styx.admin.tools
/perm group grant admin styx.admin.vanish
/perm group grant admin styx.admin.radar
```

### Use the chat commands instead

Skip the menu entirely — `/vanish` and `/aradar` work identically without `styx.admin.tools`.
