# 05 — StyxCrafting

Perm-tiered crafting buffs for fuel-using workstations (forge, chem station, cement mixer, campfire) and toolbelt crafts.

Three independent modifiers per tier:
1. **Craft speed** — recipes complete N× faster
2. **Output multiplier** — bonus stacks per completion at workstations
3. **Auto-shutdown** — fuel-using stations stop burning when nothing's smelting

## Commands

| Command | Perm | What |
|---|---|---|
| `/crafting` | (open) | Show enabled tiers + diag state |
| `/crafting diag on\|off` | (open) | Toggle verbose patch logging (admin / debugging) |

## Permissions

| Perm | Defaults | What |
|---|---|---|
| `styx.craft.master` | 0.1× time / 3× output / autoshutdown ON | Top tier — admins / max donors |
| `styx.craft.vip` | 0.25× time / 2× output / autoshutdown ON | Mid tier — VIP donors |
| `styx.craft.use` | 0.5× time / 1.25× output / autoshutdown OFF | Basic — granted to default group |

First-match-wins on perm grant — highest tier the player has determines all three modifiers.

## Config — `configs/StyxCrafting.json`

```json
{
  "Enabled": true,
  "Tiers": [
    { "Perm": "styx.craft.master", "CraftTimeMult": 0.1,  "OutputMult": 3.0, "AutoShutdown": true },
    { "Perm": "styx.craft.vip",    "CraftTimeMult": 0.25, "OutputMult": 2.0, "AutoShutdown": true },
    { "Perm": "styx.craft.use",    "CraftTimeMult": 0.5,  "OutputMult": 1.25,"AutoShutdown": false }
  ]
}
```

| Field | Range | What |
|---|---|---|
| `CraftTimeMult` | 0.05 to 10.0 | Multiplier on craft time. <1 = faster. 0.1 = 10× faster. |
| `OutputMult` | ≥1.0 | Multiplier on output count. Bonus = `floor(baseCount × mult) − baseCount`. Workstations only — toolbelt outputs unchanged. |
| `AutoShutdown` | bool | Fuel-using workstations stop burning fuel once raw material is fully smelted. |

## Auto-shutdown behaviour

Triggers within ~2 seconds of:
- The recipe queue going empty AND
- All raw materials in input slots being smelted to `unit_*` reserves AND
- The workstation owner (LCB owner) having a tier with `AutoShutdown: true`

**Ownership lookup:** the plugin walks all Land Claim Blocks and finds whose claim covers the workstation position. Falls back to "last player who queued a recipe here" if the workstation is outside any claim.

## ⚠️ UX quirk — close the UI for buffs to apply

While the workstation UI is **open** on your client, the client holds the queue authoritative — recipes appear to take vanilla speed/output. **Close the UI** and:
- Speed multiplier kicks in
- Output bonus appears in the output slot on each completion
- Autoshutdown fires once smelting finishes

This is a 7DTD V2.6 client-server quirk, not a plugin bug. Tell players: "queue your recipes, close the forge, walk away".

## Diag mode

```
/crafting diag on
```

Logs every patch invocation:
- `EffectManager.GetValue(CraftingTime)` — when toolbelt asks for craft time
- `TE.read at te=…: 1 queue item(s) scanned, 1 rescaled` — when queue gets scaled
- `Output bonus +N of recipeName` — when bonus stacks delivered
- `LCB lookup pos=…: matched=Steam_xxx, tier=…` — ownership resolution
- `AutoShutdown FIRED pos=…` / `SKIP (reason)` — autoshutdown decisions

Hot-reload resets diag to OFF.

## Notes

- **All three modifiers are independent** — a tier can have just speed, just output, just autoshutdown, or any combination.
- **No tier match = vanilla behaviour** — players without any craft perm experience normal crafting.
- **Multipliers apply server-authoritatively** — players can't bypass via client mods.
- **Auto-shutdown saves players' fuel** when they fire-and-forget a smelt and walk away.

## Common ops

### Make autoshutdown universal (every player gets it)

Edit `configs/StyxCrafting.json` so `styx.craft.use` has `AutoShutdown: true`. Save. Hot-reloads.

### Add a "donor" tier between vip and master

```json
{ "Perm": "styx.craft.donor", "CraftTimeMult": 0.15, "OutputMult": 2.5, "AutoShutdown": true },
```

Insert between master and vip. Then `/perm group grant donor styx.craft.donor`.

### Disable the plugin entirely without removing it

Edit config → `"Enabled": false`. All Harmony patches stay attached but no-op.
