# 12 — Vanish

Admin invisibility + AI ignore via the vanilla spectator flag (`EntityPlayer.IsSpectator`).

## Commands

| Command | Perm | What |
|---|---|---|
| `/vanish` | `styx.admin.vanish` | Toggle vanish on/off for yourself |
| `/vanish on` | `styx.admin.vanish` | Force on |
| `/vanish off` | `styx.admin.vanish` | Force off |

## Permissions

| Perm | What |
|---|---|
| `styx.admin.vanish` | Use `/vanish` |

## What you get

- ✅ AI ignores you (zombies don't aggro)
- ✅ Your character model is hidden from other clients
- ❌ NOT noclip — V2.6 has no per-player noclip flag
- ❌ NOT flight — `GameStats.IsFlyingEnabled` is a global game-mode setting, not per-player

For flight, pair with a separate `/fly` plugin. For wall-clip, use `/m → Teleport`.

## Mechanics

The vanilla `EntityPlayer.IsSpectator` setter does the heavy lifting:
- Triggers `isIgnoredByAI` sync to peers
- Calls `SetVisible(false)` to hide your model from other clients

Styx polls every ~2s and re-applies if the flag drifts (clients can wipe it via `NetPackageEntityAliveFlags` sync — see `STYX_CAPABILITIES.md §20`). Without the polling re-apply, you'd unvanish silently after any client sync.

## Notes

- **Auto-restore on disconnect** — vanish state is in-memory only. Crash / quit → next session you start visible.
- **Drift fix**: log shows `[Vanish] Re-applied IsSpectator for entity N (was wiped by client sync)` whenever the polling kicks in. Normal.
- **Vanilla `sm` console command** is no-arg self-only and runs from the player's own context, so it's not usable from server-side code anyway. Styx writes the flag directly.
- **Other players can't see you in `lp` (list players)** — but admins still see you in chat / commands.
- **Your own HUD compass dots** still show other players normally.

## Common ops

### Status check

`/vanish` (with no arg) toggles, so it's not a status command. Workaround: just type `/vanish on` — if you were already on, it stays on (the setter is idempotent). The chat reply tells you what state you're in.

### Why does it briefly drop?

Client sync packets wipe the spectator flag. The 2s polling re-applies. If you see yourself flicker visible, that's the gap. Reduce poll interval in code if it bothers you.

### Vanish without breaking PvP combat

There is no "selective vanish" — you're vanished to everyone. To monitor a specific area without spectating, use `/m → Admin Tools → Admin Radar` instead.
