# 01 — Permissions System

Three plugins make up the permission UX:

- **PermManager** (`/perm`) — chat-based admin command surface
- **PermEditor** (UI) — visual group/perm editor (3-stage picker)
- **StyxPerms** (`/perms`) — interactive admin action launcher

Plus the framework's `StyxCore.Perms.*` API that every plugin uses.

## /perm — chat command (PermManager)

**Permission required:** `styx.perm.admin` for any mutation. Read-only commands (`/perm me`, `/perm find`) are open.

| Command | What |
|---|---|
| `/perm` *(no args)* | Same as `/perm me` |
| `/perm me` | Your groups + effective perms |
| `/perm show <player>` | Another player's perms (admin) |
| `/perm grant <player> <perm>` | Grant a perm directly to a player (admin) |
| `/perm revoke <player> <perm>` | Revoke a perm from a player (admin) |
| `/perm addto <player> <group>` | **← Add a player to a group (e.g. `admin`)** |
| `/perm removefrom <player> <group>` | Remove from a group |
| `/perm group list` | All groups |
| `/perm group members <name>` | Who's in a group |
| `/perm group create <name> [parent]` | Create a new group, optionally inheriting from parent |
| `/perm group delete <name>` | Delete a group (not `default`) |
| `/perm group grant <group> <perm>` | Give a group a perm |
| `/perm group revoke <group> <perm>` | Remove a perm from a group |
| `/perm find <pattern>` | Search your effective perms (substring match) |
| `/perm help` | Print all subcommands |

`<player>` accepts: in-game name, bare numeric SteamID (`76561198…`), or full `Steam_76561198…`. Online names are matched case-insensitively, then remembered offline names.

## /perms — interactive UI (StyxPerms)

**Permission required:** `styx.perm.admin`

Open with `/perms` (note the trailing `s`) or via `/m → Perm Manager`.

Action launcher — jump/crouch to navigate, LMB to confirm, RMB to close. Each action whispers formatted results to chat. Useful for quick lookups without typing full commands.

## PermEditor — UI for group × plugin × perm grid

**Permission required:** `styx.perm.admin`

Open via `/m → Perm Editor`. Three-stage picker:

1. **Pick a group** (default / vip / admin / any custom you created)
2. **Pick a plugin** (or `(All)` to see every perm)
3. **Edit perms for that group × plugin slice** — toggle perms ON/OFF, plus edit group's `Priority`, `ChatTag`, and `ChatTagColor` (used by ChatTags plugin)

Group priority controls which group's tag wins in chat (higher = wins). Default priorities: `default=0`, `vip=50`, `admin=100`.

**Limitation:** Labels are baked at server startup. New groups / perms / plugins added at runtime won't appear in the UI until next restart. Use chat `/perm` for those.

## Default groups

Auto-created on first boot:

| Group | Priority | Parent | Default tag |
|---|---|---|---|
| `default` | 0 | (none) | (no tag) |
| `vip` | 50 | `default` | `[VIP]` (gold) |
| `admin` | 100 | `vip` | `[Admin]` (red) |

Plus implicit `[Owner]` (priority 200, magenta) for vanilla level-0 admins — see **[00 — Bootstrap](./00-bootstrap.md)**.

## How perm checks work

When a plugin calls `HasPermission(playerId, "styx.foo.use")`:

1. **Owner shortcut** — if player is at vanilla auth level 0, returns `true` for any `styx.*` perm
2. **Player explicit revoke** — if player has `-styx.foo.use` in their record, returns `false`
3. **Player explicit grant** — if player has `+styx.foo.use`, returns `true`
4. **Walk player's groups** — and each group's parent chain, return `true` if any has it
5. **Default group check** — every player implicitly in `default`
6. Otherwise `false`

## Common perm-related tasks

### Make a new VIP tier

```
/perm group create donor vip          # inherits all vip perms
/perm group grant donor styx.kit.donor
/perm group grant donor styx.craft.master
/perm addto SomePlayer donor
```

### Revoke a single perm from a player without removing from group

```
/perm revoke SomePlayer styx.kit.master
```

(Player explicit revoke beats group grant.)

### See exactly what a player can do

```
/perm show SomePlayer
```

Lists groups + every effective perm.

### Find which plugin a perm belongs to

```
/perm find craft
```

Searches your effective perms — shows `styx.craft.master`, `styx.craft.vip`, etc. with the registering plugin name.

## Direct file editing

`Mods/Styx/data/permissions.json` is the source of truth. Schema:

```json
{
  "Groups": {
    "admin": {
      "Name": "admin",
      "Parent": "vip",
      "Priority": 100,
      "ChatTag": "[Admin]",
      "ChatTagColor": "ff3030",
      "Grants": ["styx.perm.admin", "styx.admin.radar", ...],
      "Revokes": []
    }
  },
  "Players": {
    "Steam_76561198XXXXXXXXX": {
      "Groups": ["admin"],
      "Grants": [],
      "Revokes": []
    }
  }
}
```

Edit while the server is stopped. The framework reloads it at boot.
