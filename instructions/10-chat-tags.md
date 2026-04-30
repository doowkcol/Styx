# 10 — ChatTags

Group-priority `[Tag]` prefix on chat messages. Highest-priority group wins.

## Commands

None — purely passive.

## Permissions

None — uses your existing group memberships.

## Config

Per-group, in `data/permissions.json`:

```json
"Groups": {
  "admin": {
    "Priority": 100,
    "ChatTag": "[Admin]",
    "ChatTagColor": "ff3030"
  },
  "vip": {
    "Priority": 50,
    "ChatTag": "[VIP]",
    "ChatTagColor": "ffcc00"
  },
  "default": {
    "Priority": 0,
    "ChatTag": "",
    "ChatTagColor": ""
  }
}
```

Edit via `/m → Perm Editor → pick group → edit Priority/Tag/Color`, or directly in JSON.

## Default tags

| Group | Priority | Tag | Colour |
|---|---|---|---|
| `default` | 0 | (none) | (none) |
| `vip` | 50 | `[VIP]` | gold (`ffcc00`) |
| `admin` | 100 | `[Admin]` | red (`ff3030`) |
| Owner *(implicit, vanilla level 0)* | 200 | `[Owner]` | magenta (`ff66ff`) |

Higher priority wins. An admin-and-VIP player shows `[Admin]`.

## Output format

```
[Admin] Doowkcol: hello world
```

The tag is colour-wrapped in BBCode `[ff3030][Admin][-]`. The player name + message use the engine's default chat colour.

## Notes

- **Slash commands pass through unmodified** — `/styx`, `/perm`, etc. don't get tag prefixes (they go through their own dispatch path, not the chat broadcast).
- **Hooks `OnChatMessage`** and returns non-null to cancel the vanilla broadcast, then re-broadcasts with the tag prefix.
- **Owner tag is implicit** — anyone at vanilla `serveradmin.xml` level 0 automatically shows `[Owner]` regardless of Styx group membership. The implicit tag has priority 200 so it beats explicit groups.
- **No tag for default group** by design — keeps regular chat clean.

## Common ops

### Custom tag for a new "donor" group

1. `/perm group create donor vip`
2. `/m → Perm Editor → donor → set Priority=75, Tag="[Donor]", Color="ff8800"`
3. `/perm addto SomePlayer donor`

Now they show `[Donor]` (orange) in chat, ranking between vip (50) and admin (100).

### Disable the plugin

Delete `Mods/Styx/plugins/ChatTags.cs` (or rename `.cs.disabled`). Hot-unloads. Chat reverts to vanilla format.
