# 00 — Bootstrap & First Boot

How to get from a fresh Styx install to a working admin-controlled server.

## Step 0 — Permissions starter file

Styx ships a sanitised permissions template at `data/permissions.example.json`. On first server boot, **rename it to `data/permissions.json`** (or copy it). The framework auto-creates one with framework defaults if neither exists, but the shipped example has the recommended group setup (default / discord / vip / admin) pre-configured — saves you reinventing it.

```
cp Mods/Styx/data/permissions.example.json Mods/Styx/data/permissions.json
```

The `.example` file is the only one tracked in git; your live `permissions.json` is per-server (it accumulates real player Steam IDs) and is gitignored.

## Step 1 — Make yourself owner via vanilla auth

Styx's permission system has an implicit shortcut: anyone at vanilla `serveradmin.xml` permission level **0** (lowest number = highest authority) automatically receives every `styx.*` permission.

In the **server console** (the terminal window the server is running in — no slash):

```
admin add Steam_76561198XXXXXXXXX 0
```

Or via telnet (port 8081, your `TelnetPassword` from `serverconfig.xml`):

```
admin add Steam_76561198XXXXXXXXX 0
```

Or edit `<save>/serveradmin.xml` while the server is stopped:

```xml
<admins>
  <admin steamID="Steam_76561198XXXXXXXXX" permission_level="0" />
</admins>
```

**Find your SteamID** in the server log on connect:
```
[Auth] PlayerName authorization successful: ... PltfmId='Steam_76561198XXXXXXXXX' ...
```

## Step 2 — Verify

Connect with the game client and run in chat:

```
/styx
```

Should reply with version + plugin count. If not, check the server log for `[Styx] InitMod called`.

```
/perm me
```

Should show your effective perms. As a level-0 owner you'll see every `styx.*` perm marked `(implicit)`.

## Step 3 — Add other admins

Once you have `styx.perm.admin` (which you do as owner), add other admins via the admin **group** in chat:

```
/perm addto <player> admin
```

`<player>` accepts in-game name, bare numeric SteamID, or full `Steam_xxx` form.

To revoke later:

```
/perm removefrom <player> admin
```

## Default groups (auto-created on first boot)

| Group | Inherits | Default perms |
|---|---|---|
| `default` | (none) | Open perms (e.g. `styx.kit.use` if you grant it here) |
| `vip` | `default` | Donor-tier perms |
| `admin` | `vip` | Full admin — `styx.perm.admin` etc. |

Group inheritance means `admin` has everything `vip` has + everything `default` has + its own grants.

## Where things live

```
Mods/Styx/
├── configs/             ← per-plugin JSON configs, edit to taste
├── data/                ← per-plugin persistent state (don't edit by hand)
│   └── permissions.json ← all groups + per-player grants
├── Config/              ← XML modlets (XUi panels + buffs)
└── plugins/             ← .cs source files, hot-reload on save
```

The `data/permissions.json` file is the canonical source of truth for groups + grants. You can edit it directly while the server is stopped, but the `/perm` command is safer — it validates everything.

## Common first-boot perms to grant

After bootstrap, common perms to set up:

```
/perm group grant default styx.kit.use         # everyone can claim kits they're permitted to
/perm group grant default styx.zloot.use       # everyone gets basic zombie loot drops
/perm group grant default styx.radar.use       # everyone sees small zombie radar (10m)
/perm group grant default styx.craft.use       # everyone gets minor crafting buff
/perm group grant default styx.backpack.use    # everyone can use /b personal storage

/perm group grant vip styx.kit.vip             # VIPs unlock vip-tier kits
/perm group grant vip styx.zloot.vip           # VIP loot drops
/perm group grant vip styx.radar.vip           # 30m radar
/perm group grant vip styx.craft.vip           # better crafting buff

/perm group grant admin styx.admin.tools       # /m → Admin Tools opens for them
/perm group grant admin styx.admin.radar       # AdminRadar visible
/perm group grant admin styx.admin.vanish      # /vanish allowed
/perm group grant admin styx.craft.master      # max crafting buff
```

You don't need to grant individually — group membership inherits all.

## What to do if you lose access

Vanilla `admin add Steam_xxx 0` always wins — you can't lock yourself out via Styx if you have level 0 in `serveradmin.xml`. That's your safety net.

## Next steps

- See **[01 — Permissions](./01-permissions.md)** for the full `/perm` command surface
- See **[02 — Kit](./02-kit.md)** through **[22 — Hello demos](./22-hello-demos.md)** for individual plugins
