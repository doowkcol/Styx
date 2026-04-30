# Styx — Operator's Instructions

Per-plugin command reference, perms, and config notes. Pitched at server operators, not framework authors.

For plugin-author / framework docs, see the `STYX_*.md` files in the project root.

## Read first

- **[00 — Bootstrap & first boot](./00-bootstrap.md)** — how to make yourself admin, default groups, where configs live
- **[01 — Permissions system](./01-permissions.md)** — `/perm`, `/perms`, the perm-editor UI

## Player-facing plugins

| # | Plugin | Chat command | What |
|---|---|---|---|
| [02](./02-kit.md) | Kit | `/kit` | Claim starter / VIP / themed item kits |
| [03](./03-teleport.md) | StyxTeleport | `/sethome` `/m → Teleport` | 3 home slots + nearest trader + last death |
| [04](./04-backpack.md) | StyxBackpack | `/b` | Persistent personal storage + perm-tiered death-bag |
| [05](./05-crafting.md) | StyxCrafting | `/crafting` | Perm-tiered forge speed + output bonus + idle-fuel autoshutdown |
| [06](./06-zombie-loot.md) | ZombieLoot | `/zloot` | Perm-tiered loot bag drops on zombie kill |
| [07](./07-donor-perks.md) | DonorPerks | `/donor` `/m → My Buffs` | Group-mapped buffs + per-player toggle UI |
| [08](./08-welcome-message.md) | WelcomeMessage | `/wm` | Permission-gated welcome messages on spawn |
| [10](./10-chat-tags.md) | ChatTags | (none) | Group-priority `[Tag]` prefix on chat |

## Admin tooling

| # | Plugin | Chat command | What |
|---|---|---|---|
| [09](./09-server-restart.md) | ServerRestartManager | `/srm` | Scheduled daily restarts with countdown |
| [11](./11-godmode.md) | Godmode | `/god` | Styx-internal damage immunity flag |
| [12](./12-vanish.md) | Vanish | `/vanish` | Invisible + AI-ignore (admin tool) |
| [13](./13-reflect.md) | Reflect | `/reflect` | Damage-reflect modes (off/shield/back/double) |
| [14](./14-admin-radar.md) | AdminRadar | `/aradar` | Through-walls 5-category entity radar |
| [15](./15-admin-tools.md) | AdminTools | `/m → Admin Tools` | Sub-menu launcher for vanish + radar toggles |

## HUD / menu / interactive UI

| # | Plugin | Chat command | What |
|---|---|---|---|
| [16](./16-hud.md) | StyxHud | (always on) | Top-left HUD: players, rank, wipe + restart countdown |
| [17](./17-menu.md) | StyxMenu | `/menu` `/m` | Interactive action menu — heal-full, water, teleport, server info |
| [18](./18-zombie-radar.md) | StyxZombieRadar | `/radar` | Live per-player zombie-count HUD section, perm-tiered radius |
| [23](./23-zombie-health.md) | StyxZombieHealth | `/zhealth` | Crosshair entity-health HUD — name + HP of what you're aimed at |

## Dev / demo plugins

These are reference implementations — safe to delete from `Mods/Styx/plugins/` if you don't want them.

| # | Plugin | Chat command | What |
|---|---|---|---|
| [19](./19-input-probe.md) | StyxInputProbe | `/input` | Dev tool — whispers each input event for testing |
| [20](./20-game-data-demo.md) | GameDataDemo | `/gd` | Demo of the runtime GameData mutation API |
| [21](./21-game-refs.md) | StyxGameRefs | `/gamerefs` | Dumps item/block/entity/buff registries to MD reference files |
| [22](./22-hello-demos.md) | Hello + HelloSource | `/hello` `/src` | Minimal "hello world" demos of plugin patterns |

---

## Common patterns across all plugins

### Perm naming convention

`styx.<plugin>.<feature>` — e.g.:
- `styx.kit.use`, `styx.kit.basic`, `styx.kit.vip`
- `styx.craft.master`, `styx.craft.vip`, `styx.craft.use`
- `styx.admin.radar`, `styx.admin.tools`, `styx.admin.vanish`
- `styx.perm.admin` (the meta-perm — needed to manage other perms)

### Config files

All plugins read from `Mods/Styx/configs/<PluginName>.json`. Configs **auto-create with sensible defaults** on first boot. Edit them and the framework hot-reloads most plugins (some need a server restart).

### Hot reload

`.cs` plugin files in `Mods/Styx/plugins/` hot-reload on save — no restart needed. JSON configs hot-reload too.

`Styx.Core.dll` requires a server restart (Mono limitation).

### Removing a plugin

Delete its `.cs` file from `Mods/Styx/plugins/`. The framework hot-unloads on file removal. Or rename to `.cs.disabled`.

Or just don't grant the perm — most plugins are perm-gated and silently no-op for users without the right perm.
