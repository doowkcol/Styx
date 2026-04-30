# 02 — Kit

Item kits delivered as a single lootable backpack at the player's feet. Per-kit cooldowns + perm tiers.

## Commands

| Command | Perm | What |
|---|---|---|
| `/kit` | `styx.kit.use` | List available kits |
| `/kit <name>` | `styx.kit.use` + per-kit perm | Claim the kit |
| `/kit info <name>` | `styx.kit.use` | Preview kit contents |
| `/m → Kits` | `styx.kit.use` | Interactive picker (jump/crouch nav, LMB claim, RMB back) |

## Permissions

| Perm | What |
|---|---|
| `styx.kit.use` | Required to use any `/kit` command or open the picker |
| `styx.kit.basic` | Tier perm — kits configured with `Perm: "styx.kit.basic"` |
| `styx.kit.vip` | Tier perm — VIP-tier kits |
| `styx.kit.master` | Tier perm — admin/master-tier kits |

Per-kit perms are **shared** — granting `styx.kit.vip` unlocks every kit configured with `Perm: "styx.kit.vip"`. To unlock all kits, grant the broadest tier you've used.

## Config — `configs/Kit.json`

```json
{
  "Kits": [
    {
      "Name": "starter",
      "DisplayName": "Starter Pack",
      "Description": "Basic survival kit for new players",
      "Perm": "",                        // empty = no perm required
      "CooldownSeconds": 86400,          // 24h
      "Items": [
        { "Item": "drinkJarBoiledWater", "Count": 5, "Quality": 1 },
        { "Item": "foodCanChili",        "Count": 3, "Quality": 1 },
        { "Item": "medicalFirstAidBandage", "Count": 5, "Quality": 1 }
      ]
    },
    {
      "Name": "vip",
      "DisplayName": "VIP Kit",
      "Perm": "styx.kit.vip",
      "CooldownSeconds": 43200,
      "Items": [ ... ]
    }
  ],
  "KitPerms": [
    { "Perm": "styx.kit.basic",  "Description": "Basic kits" },
    { "Perm": "styx.kit.vip",    "Description": "VIP kits" },
    { "Perm": "styx.kit.master", "Description": "Master kits" }
  ]
}
```

**Item names must be exact** — check `server/Data/Config/items.xml` for canonical names. Common gotcha: `drinkJarPureWater` (V1.x) is now `drinkJarBoiledWater`.

## How delivery works

Kit items are dropped as a **single lootable backpack entity** at the player's feet, not added directly to inventory. Why: server-side `inventory.AddItem()` gets clobbered by the client's next PlayerData sync (client-authoritative state). Backpack drops use the entity system which replicates correctly.

Result: player sees a backpack icon at their feet, opens it once, takes everything.

## Cooldowns

Stored in `data/Kit.cooldowns.json`. Survives restart. Per-player + per-kit.

To wipe a player's cooldowns, edit the JSON and remove their entry, then `/kit` again — they'll see the kit ready immediately.

## Notes

- **Hot-reload**: editing `configs/Kit.json` triggers a config reload. New kits appear immediately. Removed kits become unavailable.
- **Cooldown bypass**: there's no admin override built in. Workaround: temporarily grant a kit with `CooldownSeconds: 0`, claim, restore.
- **Empty `Perm` field** = open to anyone with `styx.kit.use`.
