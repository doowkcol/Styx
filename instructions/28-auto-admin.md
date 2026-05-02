# 28 — StyxAutoAdmin

Adds every joining player to a configured perm group (defaults to `admin`). **Test / sandbox / demo server use only** — never enable on a production server.

The whole point is "let visitors experience the full Styx framework without the operator manually granting perms each time." The framework's default `admin` group already ships with the right perms (`styx.admin.*`, `styx.perm.admin`, `styx.donor.admin`, etc.) plus inherits from `vip` and `default`, so a one-line group add gets visitors everything they need.

## Default state

`Enabled: false`. Plugin loads but does nothing until you flip the switch in `configs/StyxAutoAdmin.json`.

## Config

```json
{
  "Enabled": false,
  "GroupName": "admin",
  "LogGrants": true
}
```

| Field | What |
|---|---|
| `Enabled` | Master toggle. Default false (safe). Set true to activate auto-add. |
| `GroupName` | Perm group to add joiners to. Must already exist — the plugin won't create groups, only add members. |
| `LogGrants` | Log every add to the server log. Helpful while tuning, set false on a busy server. |

## How it works

On `OnPlayerJoined`:
1. Checks the configured group exists via `StyxCore.Perms.GroupExists`. If not, logs a warning and skips.
2. Calls `StyxCore.Perms.AddPlayerToGroup(pid, GroupName)` — idempotent, so re-joins are no-ops.

That's the whole plugin. ~80 lines including header. The `admin` group's contents are owned by the framework's `EnsureDefaultGroups` bootstrap, not this plugin — see [01 — Permissions](./01-permissions.md) if you want to customise what perms `admin` carries.

## Customising the target group

If you want visitors to land in a custom group with a tailored perm set:

```
/perm group create visitor
/perm group grant  visitor styx.kit.use
/perm group grant  visitor styx.tp.use
/perm group prio   visitor 50
/perm group tag    visitor "[Visitor]" 88ccff
```

Then set `GroupName: "visitor"` in `configs/StyxAutoAdmin.json` and reload the plugin.
