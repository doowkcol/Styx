# 16 — StyxHud

Always-on player HUD pinned top-left. Shows server-branded header, player count, your rank/tag, server timing (next wipe + restart countdown), and an optional zombie-radar section that mirrors StyxZombieRadar.

## Commands

None — purely passive.

## Permissions

None — visible to every connected player.

## What's shown

| Section | Source | Visibility |
|---|---|---|
| Header (server name) | `configs/Branding.json` → `ServerName` | Always |
| Sub-header (tagline) | `configs/Branding.json` → `Tagline` | Always |
| Online players | `Server.PlayerCount` | Always |
| Your rank/tag | Highest-priority group from PermManager | Always |
| Next restart in | ServerRestartManager next-time → minutes | When SRM has a restart scheduled within configurable window |
| Zombie radar | StyxZombieRadar count cvar | Only if StyxZombieRadar is loaded AND your tier matches |

Day/time deliberately omitted — vanilla compass HUD already shows it.

## Config

Reads server branding from `configs/Branding.json` (auto-created by the framework, not by StyxHud):

```json
{
  "ServerName": "REKT",
  "Tagline":    "Styx Modding Framework"
}
```

Edit once → header + launcher header + any future branded UI all auto-pick it up. No per-plugin config knobs — StyxHud just renders what other plugins push via cvars.

## Mechanics

- **Hybrid panel design**: StyxHud is the always-on core. Other plugins keep their own detail panels but ALSO push their primary cvar so StyxHud can mirror it as an optional section.
- **No tight coupling**: if a plugin (e.g. StyxZombieRadar) is unloaded, its cvar stops updating and the section's visibility binding hides it automatically. No plugin-load-order dependency.
- **Per-player cvars** drive per-player content (count, rank, etc.).

## Notes

- **Pinned top-left** — uses XUi pivot=`TopLeft` anchor. Doesn't move with player input.
- **Toolbelt window-group** — mounted in vanilla `toolbelt` group so it auto-shows on connect, hides during loot/inventory windows (per vanilla XUi behaviour).
- **Restart countdown** appears when ServerRestartManager has a restart scheduled within ~30 minutes. Hidden otherwise.
- **Wipe countdown** — currently a placeholder; not wired to any data source. Future: read from `Branding.json` or a separate WipeManager plugin.

## Common ops

### Change server name / tagline

Edit `configs/Branding.json`. Hot-reloads on save. All HUD elements update next render.

### Hide the HUD entirely

Delete `Mods/Styx/plugins/StyxHud.cs`. Hot-unloads. The XUi panel is gone next connect.

Or just edit the XUi `windows.xml` to set `styxHud` window's `visible="false"`.

### Customise the panel layout

Edit `Mods/Styx/Config/XUi/windows.xml` → find `<window name="styxHud">`. Standard XUi rules apply (pivot/anchor are 9-cell strict enums — see `STYX_NEXT_SESSION_KICKOFF.md`). Server restart needed for XUi changes (no hot-reload).
