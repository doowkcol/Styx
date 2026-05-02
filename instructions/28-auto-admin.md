# 28 — StyxAutoAdmin

Auto-grants vanilla auth level 0 (full owner) to every player who joins. **Test / sandbox / demo server use only** — never enable on a production server.

The whole point is "let visitors experience the full Styx framework without the operator manually granting perms each time." Visitors get full vanilla console + every `styx.*` perm via the auth-0 implicit grant.

## Default state

`Enabled: false`. Plugin loads but does nothing until you flip the switch in `configs/StyxAutoAdmin.json`.

## Config

```json
{
  "Enabled": false,
  "AuthLevel": 0,
  "LogGrants": true
}
```

| Field | What |
|---|---|
| `Enabled` | Master toggle. Default false (safe). Set true to activate auto-grant. |
| `AuthLevel` | 0 = full owner (vanilla console + implicit styx.*). 1 = "near-admin" but vanilla 0-level commands (kick, ban, give) are blocked AND styx.* needs explicit group grants. Use 0 for the easiest "experience everything" demo. |
| `LogGrants` | Log every grant + skip to the server log. Helpful while tuning, set false on a busy server. |

## How it works

On `OnPlayerJoined`:
1. Reads the player's current auth level from `serveradmin.xml` via `StyxCore.Perms.GetAuthLevel(platformId)`
2. If they're already at the target level OR more powerful (lower number), skips — log shows "already at auth N — skipping"
3. Otherwise shells out `admin add <pid> <AuthLevel>` via `Styx.Server.ExecConsole`. Vanilla command persists to `serveradmin.xml` and reloads `AdminTools` so the change is live immediately.

The plugin doesn't track who it's granted to — it just checks-and-grants on every join. After the first grant, future joins for the same player no-op via the "already at this level" skip.

## CRITICAL: gate the wipe commands

Auth-0 visitors get every `styx.*` perm — including `styx.eco.admin` and `styx.xp.admin`, which gate `/eco wipe` and `/xp wipe`. Without an extra lock, any visitor can nuke every player's wallet and XP with a single chat command.

Set both:

**`configs/StyxEconomy.json`:**
```json
"WipeAdditionalPerm": "ops.wipe"
```

**`configs/StyxLeveling.json`:**
```json
"WipeAdditionalPerm": "ops.wipe"
```

Then grant yourself the `ops.wipe` perm (note: `ops.` prefix — NOT `styx.` — so auth-0 implicit grants don't include it):

```
/perm grant <YourSteamId> ops.wipe
```

Now `/eco wipe` and `/xp wipe` require BOTH the styx admin perm AND `ops.wipe`. Visitors fail at the second check; you pass both.

## What about kick / ban?

When everyone is auth-0, in-game `kick`/`ban` console commands work for all visitors — they CAN kick each other. Limiting this is structurally hard (any per-command level gate that blocks visitors also blocks the operator). Three options:

1. **Accept the risk** for a low-traffic test server. If a visitor goes wild, restart the server.
2. **Run visitors at AuthLevel: 1** instead of 0 — vanilla 0-level commands (kick / ban / give / killall / teleportplayer) auto-block. But you'll lose the `styx.*` implicit grant and have to maintain a separate group with the styx perms you want them to have.
3. **Use telnet for moderation** — TelnetPort + TelnetPassword in `serverconfig.xml`. Telnet auth is separate from in-game auth, so only you can kick via telnet.

## Server name + branding

The framework's `Server/serverconfig.xml` and `configs/Branding.json` should reflect that this is a test deployment so visitors know what they've joined. Common pattern:

```xml
<property name="ServerName" value="Styx Framework Test Server"/>
<property name="ServerDescription" value="Public test server for Styx — server-side modding framework. Vanilla balance on Navezgane, no blood moons. discord.gg/yourinvite"/>
```

```json
{
  "ServerName": "Styx",
  "HudHeader": "STYX",
  "HudSubheader": "v0.6.3 — Public Test",
  "LauncherHeader": "Styx Menu"
}
```

## See also

- [24 — StyxEconomy](./24-economy.md#locking-wipe-on-test--sandbox-servers) — `/eco wipe` lock
- [27 — StyxLeveling](./27-leveling.md#locking-wipe-on-test--sandbox-servers) — `/xp wipe` lock
