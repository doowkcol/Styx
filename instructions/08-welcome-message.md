# 08 — WelcomeMessage

Permission-gated welcome messages on player spawn. Different ranks get different greetings. Port of Doowkcol's Rust Carbon plugin.

## Commands

| Command | Perm | What |
|---|---|---|
| `/wm list` | `welcomemessage.admin` | List configured messages + their perms / enabled state / rank |
| `/wm reload` | `welcomemessage.admin` | Reload config (also auto-hot-reloads on save) |
| `/wm add <suffix>` | `welcomemessage.admin` | Append a new permission key under the `welcomemessage.` namespace and an associated message stub |
| `/wm test` | `welcomemessage.admin` | Fire your own welcome sequence to preview |

No player-facing command — messages auto-fire on first spawn of the session.

## Permissions

| Perm | What |
|---|---|
| `welcomemessage.admin` | Run `/wm` admin commands |
| `welcomemessage.use` | Optional gate — when `RequireBaseUsePermission=true`, only players with this perm get any welcome at all |
| `welcomemessage.<suffix>` | Per-message gate — every key in `PermissionMessages` is itself a perm name |

The plugin's perms live under the `welcomemessage.*` namespace, **not** `styx.wm.*`.

## Config — `configs/WelcomeMessage.json`

```json
{
  "WelcomeDelaySeconds": 5.0,
  "MessageDelaySeconds": 2.0,
  "RequireBaseUsePermission": false,
  "MessageDepthLimit": 0,
  "Prefix": "[55aaff][Welcome][-] ",
  "PermissionMessages": {
    "welcomemessage.vip": {
      "Message": "Welcome VIP! Use [ffaa00]/kit vip[-] to claim your donor loadout.",
      "Enabled": false,
      "Rank": 20
    },
    "welcomemessage.discord": {
      "Message": "Join our Discord at [55aaff]discord.gg/rekt[-] for updates and community chat.",
      "Enabled": true,
      "Rank": 5
    },
    "welcomemessage.default": {
      "Message": "Welcome to the server! Type [ffaa00]/kit starter[-] to grab a starter kit.",
      "Enabled": true,
      "Rank": 1
    }
  }
}
```

| Field | Meaning |
|---|---|
| `WelcomeDelaySeconds` | Wait this long after spawn before sending the first message |
| `MessageDelaySeconds` | Stagger between consecutive messages |
| `RequireBaseUsePermission` | When true, players without `welcomemessage.use` get nothing |
| `MessageDepthLimit` | `0` = unlimited; otherwise send only the top-N by `Rank` |
| `Prefix` | Prepended to every message (BBCode-capable) |
| `PermissionMessages.<perm>.Message` | Text body |
| `PermissionMessages.<perm>.Enabled` | Master on/off per message |
| `PermissionMessages.<perm>.Rank` | Higher = sent earlier |

## Mechanics

- **First spawn per session** — the in-memory `_welcomed` HashSet skips players already greeted this server lifetime. Server restart re-arms everyone.
- **Rank order** — eligible messages are sorted high→low by `Rank` and delivered with `MessageDelaySeconds` between each.
- **Multiple matches** — admin who inherits vip → default sees every `Enabled` message they have a perm for, in rank order, capped by `MessageDepthLimit`.
- **Whisper-only** — uses `Server.Whisper`, only the spawning player sees their messages.
- **No server-restart persistence of "seen"** — the `_welcomed` set lives in memory only by design; on-disk persistence would mean players never get re-welcomed across restarts, which would feel wrong.

## Notes

- Hot-reloads on config save.
- BBCode colours supported in `Message`. `{playerName}` token is **not** auto-substituted in the current build — write greetings that don't depend on it.
- `/wm test` is the fastest way to verify your perm grants and rank order without waiting for a real spawn.
